<?php
/**
 * 帆船 Impostor Server - API 代理
 * PHP 8.0+（使用 str_starts_with；如需 PHP 7 请自行加 polyfill）
 *
 * 安全说明（Turbo-620 加固）：
 * - 可代理到任意服务器地址（面板支持连接任何 Fmpostor 服务器），目标路径强制规整化后
 *   必须落在 /webadmin 下；
 *   URL 由 parse_url 拆解重建，配合 CURLOPT_PATH_AS_IS 禁止 libcurl 静默规整化
 *   /../ 跳出路径约束（旧版可被 dot-segment 绕过）。
 * - TLS 证书校验默认开启（CURLOPT_SSL_VERIFYPEER）；自签证书请改用 $ALLOW_INSECURE_TLS
 *   或安装受信证书，不要全局关闭校验。
 * - 移除 HTTPS→HTTP 自动降级：不再把管理员 Cookie 重发到明文链路。
 * - 仅转发 webadmin_token Cookie，避免同域其他应用会话被转发到后端。
 * - 移除 Access-Control-Allow-Origin:*：本代理只服务同源面板（浏览器与 proxy.php 同源），
 *   第三方站点无法再把它当作公开跳板；并内置每 IP 限速，抑制密码爆破。
 * - 不返回服务器 IP / PHP 版本等调试信息。
 */

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

// 自签证书环境置 true 才会跳过 TLS 校验（默认必须校验）。
$ALLOW_INSECURE_TLS = false;

if ($_SERVER['REQUEST_METHOD'] === 'OPTIONS') {
    http_response_code(204);
    exit;
}
if (!in_array($_SERVER['REQUEST_METHOD'], ['GET', 'POST', 'DELETE'], true)) {
    http_response_code(405);
    die(json_encode(['success' => false, 'message' => 'Method not allowed']));
}

// ===== 每 IP 简易固定窗口限速（默认 120 次/分钟）=====
$RATE_LIMIT = 120;
if (!proxy_rate_limit_ok($RATE_LIMIT)) {
    http_response_code(429);
    die(json_encode(['success' => false, 'message' => 'Too many requests']));
}

$api = $_GET['_api'] ?? '';
if ($api === '') {
    http_response_code(400);
    die(json_encode(['success' => false, 'message' => 'Missing _api parameter']));
}

// ===== 解析并严格校验目标 URL =====
$parts = parse_url($api);
$scheme = strtolower($parts['scheme'] ?? '');
$hostPort = strtolower($parts['host'] ?? '') . (isset($parts['port']) ? ':' . $parts['port'] : '');
$path = $parts['path'] ?? '';
if (!in_array($scheme, ['http', 'https'], true) || $hostPort === '' || $path === '') {
    http_response_code(403);
    die(json_encode(['success' => false, 'message' => 'Forbidden: invalid target URL.']));
}

// 规整化路径（去除 /./ 与 /../ 段），并强制要求落在 /webadmin 下。
$path = normalize_dot_segments($path);
if (!preg_match('#^/webadmin(/|$)#i', $path)) {
    http_response_code(403);
    die(json_encode(['success' => false, 'message' => 'Forbidden: target path must be under /webadmin/.']));
}

// 由已校验的部件重建 URL（查询串原样保留），不再使用原始输入。
$target = $scheme . '://' . $parts['host']
    . (isset($parts['port']) ? ':' . $parts['port'] : '')
    . $path
    . (isset($parts['query']) ? '?' . $parts['query'] : '');

$result = doCurl($target);

if ($result['errno'] !== 0) {
    http_response_code(502);
    die(json_encode([
        'success' => false,
        'message' => 'Cannot connect to the target server.',
    ]));
}

// 仅转发面板会话 Cookie。
foreach (explode("\r\n", $result['headers']) as $hdr) {
    if (str_starts_with(strtolower($hdr), 'set-cookie:') && stripos($hdr, 'webadmin_token=') !== false) {
        header($hdr, false);
    }
}

http_response_code($result['code']);
echo $result['body'];

// ===== helpers =====

function normalize_dot_segments(string $path): string
{
    $out = [];
    foreach (explode('/', $path) as $seg) {
        if ($seg === '' || $seg === '.') {
            continue;
        }
        if ($seg === '..') {
            array_pop($out);
            continue;
        }
        $out[] = $seg;
    }
    return '/' . implode('/', $out);
}

/** 每 IP 固定窗口限速；基于系统临时目录的小文件，失败时放行（不阻塞面板）。 */
function proxy_rate_limit_ok(int $limit): bool
{
    $ip = $_SERVER['REMOTE_ADDR'] ?? 'unknown';
    $key = preg_replace('/[^0-9a-fA-F.:]/', '_', $ip);
    $window = (int) floor(time() / 60);
    $file = sys_get_temp_dir() . '/fslimit_' . $key . '_' . $window . '.cnt';

    // 顺手清理上一分钟的旧计数文件，避免临时目录累积。
    foreach (glob(sys_get_temp_dir() . '/fslimit_' . $key . '_*.cnt') ?: [] as $old) {
        if ($old !== $file && (int) preg_replace('/.*_(\d+)\.cnt$/', '$1', $old) < $window - 1) {
            @unlink($old);
        }
    }

    $count = 0;
    $fp = @fopen($file, 'c+');
    if ($fp === false) {
        return true;
    }
    if (flock($fp, LOCK_EX)) {
        $count = (int) stream_get_contents($fp);
        $count++;
        if ($count > $limit) {
            flock($fp, LOCK_UN);
            fclose($fp);
            return false;
        }
        ftruncate($fp, 0);
        rewind($fp);
        fwrite($fp, (string) $count);
        flock($fp, LOCK_UN);
    }
    fclose($fp);
    return true;
}

function doCurl(string $url): array
{
    global $ALLOW_INSECURE_TLS;
    $body = file_get_contents('php://input');
    $method = $_SERVER['REQUEST_METHOD'];

    $ch = curl_init($url);
    $opts = [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_TIMEOUT => 8,
        CURLOPT_CONNECTTIMEOUT => 5,
        CURLOPT_HEADER => true,
        CURLOPT_FOLLOWLOCATION => false,  // 不跟随重定向（防 302 跳转绕过白名单）
        CURLOPT_PATH_AS_IS => true,       // 不让 libcurl 静默规整化 /../（防止旧版绕过）
    ];
    if ($ALLOW_INSECURE_TLS) {
        // 仅自签证书环境显式开启；默认开启严格校验。
        $opts[CURLOPT_SSL_VERIFYPEER] = false;
        $opts[CURLOPT_SSL_VERIFYHOST] = 0;
    } else {
        $opts[CURLOPT_SSL_VERIFYPEER] = true;
        $opts[CURLOPT_SSL_VERIFYHOST] = 2;
    }
    curl_setopt_array($ch, $opts);

    if ($body !== '' && $body !== false) curl_setopt($ch, CURLOPT_POSTFIELDS, $body);

    // 仅转发面板会话 Cookie；不转发其他同域应用的 Cookie。
    $cookiePairs = [];
    foreach (explode(';', $_SERVER['HTTP_COOKIE'] ?? '') as $pair) {
        $pair = trim($pair);
        if ($pair === '') continue;
        if (stripos($pair, 'webadmin_token=') === 0) $cookiePairs[] = $pair;
    }
    $req = ['Content-Type: application/json'];
    if ($cookiePairs) $req[] = 'Cookie: ' . implode('; ', $cookiePairs);
    curl_setopt($ch, CURLOPT_HTTPHEADER, $req);

    if ($method === 'POST' && $body === false) curl_setopt($ch, CURLOPT_POST, true);
    elseif ($method === 'DELETE') curl_setopt($ch, CURLOPT_CUSTOMREQUEST, 'DELETE');

    $raw = curl_exec($ch);
    $code = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    $hdrSize = curl_getinfo($ch, CURLINFO_HEADER_SIZE);
    $errNo = curl_errno($ch);
    curl_close($ch);

    if ($raw === false) {
        return ['body' => '', 'headers' => '', 'code' => 0, 'error' => '', 'errno' => $errNo];
    }
    return [
        'body' => substr($raw, $hdrSize),
        'headers' => substr($raw, 0, $hdrSize),
        'code' => $code,
        'error' => '',
        'errno' => 0,
    ];
}
