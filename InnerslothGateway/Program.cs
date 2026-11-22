// ============================================================
// InnerslothGateway — Fmpostor 好友代码网关
// 自建 Innersloth API 网关（反向代理）
//
// 作用：
//   让主服务端通过本网关访问官方 /api/user/username 获取好友码，
//   服务端所在机器连不上官方接口时，把网关放到能连上的机器上即可。
//
// 用法：
//   InnerslothGateway [端口] [X-Proxy-Key] [目标BaseUrl]
//     端口       默认 58080
//     X-Proxy-Key 可选；设置后客户端请求必须带相同值的 X-Proxy-Key 请求头
//     目标BaseUrl 可选；默认 https://backend.innersloth.com
//
// 环境变量：
//   INNERSLOTH_TARGET  目标地址（优先级低于第三个命令行参数）
//   GATEWAY_RATE_LIMIT 每 IP 每分钟最大请求数，0 表示不限（默认 60）
//   GATEWAY_ALLOWED_PREFIX 只允许转发的路径前缀（默认 api），防止被当成通用代理
// ============================================================
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

var port = args.Length > 0 && int.TryParse(args[0], out var parsedPort) ? parsedPort : 58080;
var proxyKey = args.Length > 1 && !string.IsNullOrEmpty(args[1]) ? args[1] : null;
var targetBase = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2])
    ? args[2].TrimEnd('/')
    : Environment.GetEnvironmentVariable("INNERSLOTH_TARGET") ?? "https://backend.innersloth.com";

var rateLimit = int.TryParse(Environment.GetEnvironmentVariable("GATEWAY_RATE_LIMIT"), out var rl) ? rl : 60;
var limiter = rateLimit > 0 ? new SimpleRateLimiter(rateLimit, TimeSpan.FromMinutes(1)) : null;

// Path allow-list: the gateway only exists to reach the official Innersloth API
// (paths like api/user/username). Anything else is refused so the gateway cannot
// be used as a generic relay even when the proxy key leaks.
var allowedPrefix = Environment.GetEnvironmentVariable("GATEWAY_ALLOWED_PREFIX") ?? "api";
allowedPrefix = allowedPrefix.Trim().TrimEnd('/');

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Periodic cleanup of the rate limiter table.
var cleanupTimer = new System.Threading.Timer(_ => limiter?.Cleanup(), null,
    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

var handler = new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = false,
    MaxConnectionsPerServer = 100,
};

var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
// 带上与官方客户端一致的请求特征。
client.DefaultRequestHeaders.TryAddWithoutValidation(
    "User-Agent", "UnityPlayer/2022.3.44f1 (UnityWebRequest/1.0, libcurl/7.84.0-DEV)");
client.DefaultRequestHeaders.TryAddWithoutValidation("X-Unity-Version", "2022.3.44f1");

app.Map("/{**path}", async (HttpContext ctx, string? path) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // 路径白名单: 只转发 api/ 开头的请求。
    if (!string.IsNullOrEmpty(allowedPrefix) &&
        !(path ?? string.Empty).StartsWith(allowedPrefix + "/", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(path, allowedPrefix, StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("Forbidden: path is not allowed");
        return;
    }

    // 可选鉴权：只有持有相同 Key 的主服务端才能调用本网关。
    if (!string.IsNullOrEmpty(proxyKey) &&
        !string.Equals(ctx.Request.Headers["X-Proxy-Key"].FirstOrDefault(), proxyKey, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Unauthorized: missing or invalid X-Proxy-Key");
        return;
    }

    if (limiter != null && !limiter.Allow(ip))
    {
        ctx.Response.StatusCode = 429;
        await ctx.Response.WriteAsync("Too Many Requests");
        return;
    }

    var target = $"{targetBase}/{path}{ctx.Request.QueryString}";
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {ip} {ctx.Request.Method} /{path} -> {target}");

    try
    {
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

        string? contentType = null;
        foreach (var h in ctx.Request.Headers)
        {
            if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("X-Proxy-Key", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = h.Value.ToString();
                continue;
            }

            try
            {
                req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            }
            catch
            {
                // 某些头不允许设置在 HttpRequestMessage 上，忽略即可。
            }
        }

        if (ctx.Request.ContentLength > 0
            || ctx.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            || ctx.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            || ctx.Request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
        {
            var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            ms.Position = 0;
            req.Content = new StreamContent(ms);
            if (!string.IsNullOrEmpty(contentType))
            {
                req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        ctx.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
        {
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }

        foreach (var h in resp.Content.Headers)
        {
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }

        ctx.Response.Headers.Remove("transfer-encoding");

        await using var stream = await resp.Content.ReadAsStreamAsync();
        await stream.CopyToAsync(ctx.Response.Body);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {ip} {ctx.Request.Method} /{path} <- {resp.StatusCode}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {ip} {ctx.Request.Method} /{path} ERROR: {ex.Message}");
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync($"Gateway error: {ex.Message}");
    }
});

Console.WriteLine(
    $"InnerslothGateway listening on 0.0.0.0:{port} -> {targetBase} " +
    $"(key {(string.IsNullOrEmpty(proxyKey) ? "disabled" : "enabled")}, rate limit {rateLimit}/min/IP)");
app.Run($"http://0.0.0.0:{port}");

/// <summary>
///     极简内存限流：每 IP 每窗口最多 N 次请求，防止网关被滥用。
/// </summary>
sealed class SimpleRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new(StringComparer.Ordinal);
    private readonly int _max;
    private readonly TimeSpan _window;

    public SimpleRateLimiter(int max, TimeSpan window)
    {
        _max = max;
        _window = window;
    }

    public bool Allow(string key)
    {
        var now = DateTime.UtcNow;
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > _window)
            {
                q.Dequeue();
            }

            if (q.Count >= _max)
            {
                return false;
            }

            q.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    ///     Removes empty per-IP queues so the table cannot grow without bound.
    /// </summary>
    public void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _hits)
        {
            lock (kv.Value)
            {
                while (kv.Value.Count > 0 && now - kv.Value.Peek() > _window)
                {
                    kv.Value.Dequeue();
                }
            }

            if (kv.Value.Count == 0)
            {
                _hits.TryRemove(kv.Key, out _);
            }
        }
    }
}
