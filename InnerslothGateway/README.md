# InnerslothGateway

[中文](#中文) · [English](#english)

Fmpostor 的**好友代码网关**：一个很小的反向代理，只把 `api/` 开头的请求转发到 Innersloth 官方接口 `https://backend.innersloth.com`。

---

## 中文

### 为什么需要它

```
游戏客户端 ──► Fmpostor 服务端 ──► 网关（本程序） ──► backend.innersloth.com
               GET /api/user/username                 （官方接口）
```

玩家连私服时，服务端要拿玩家的 EOS 令牌去官方接口换出好友代码。很多服务器（尤其国内）直接连不上官方接口，所以需要一台**能连上官方接口**的机器做中转，这就是网关。

Fmpostor 默认使用公共网关 `http://backend.playerinfo.impwm.fcaugame.cn`。公共网关可能限流、要求密钥或暂时不可用；如果你的服务器上好友代码获取不到（显示占位符），就按下面的步骤自己搭一个。

### 1. 准备一台机器

- **必须能直连 `backend.innersloth.com`**（香港 / 海外 VPS，或能访问官方的国内机器）。在这台机器上执行 `curl -I https://backend.innersloth.com` 能返回 HTTP 响应即可。
- 安装 **ASP.NET Core Runtime 8.0**（Ubuntu/Debian：`sudo apt-get install -y aspnetcore-runtime-8.0`）。不想装运行时可以用下面的“自包含”方式编译。

### 2. 获取程序

- **直接下载**：在 [Releases](https://github.com/SailingBoat-ovo/Fmpostor/releases/latest) 下载 `InnerslothGateway-win-x64.zip` 或 `InnerslothGateway-linux-x64.tar.gz`。
- **自己编译**（需要 .NET 8 SDK）：

```bash
dotnet publish InnerslothGateway.csproj -c Release -r linux-x64 -p:PublishSingleFile=true -o out/linux
# Windows: -r win-x64
# 不想装运行时（体积约 70MB）: 加 --self-contained true
```

### 3. 启动

```bash
# 生成一个随机密钥
openssl rand -hex 32

# Linux 前台测试
chmod +x InnerslothGateway start.sh
./start.sh 58080 你的密钥

# Windows
InnerslothGateway.exe 58080 你的密钥
```

参数：`InnerslothGateway [端口] [密钥] [目标地址]`

| 参数 / 环境变量 | 默认 | 说明 |
|---|---|---|
| 端口 | `58080` | 监听端口，可随意改 |
| 密钥 | 无 | 设置后，请求必须带相同的 `X-Proxy-Key` 请求头，否则返回 401。**暴露到公网时务必设置** |
| 目标地址 / `INNERSLOTH_TARGET` | `https://backend.innersloth.com` | 一般不用改 |
| `GATEWAY_RATE_LIMIT` | `60` | 每个 IP 每分钟最多请求数，`0` = 不限 |
| `GATEWAY_ALLOWED_PREFIX` | `api` | 只转发这个前缀下的路径，其它返回 403，防止被当成通用代理 |

Linux 常驻（systemd，`/etc/systemd/system/innersloth-gateway.service`）：

```ini
[Unit]
Description=Innersloth Gateway
After=network-online.target

[Service]
WorkingDirectory=/opt/innersloth-gateway
ExecStart=/opt/innersloth-gateway/InnerslothGateway 58080 你的密钥
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
```

```bash
systemctl daemon-reload && systemctl enable --now innersloth-gateway
```

想用 80 / 443 端口或域名访问，可以用 Nginx / Caddy / CDN 反代到网关端口。

### 4. 让 Fmpostor 使用你的网关

在 Fmpostor 的 `config.json` 里的 `WebAdmin` 段加入（改完重启服务端）：

```json
"WebAdmin": {
  "InnerslothApiBaseUrl": "http://你的网关地址:58080",
  "InnerslothProxyKey": "你的密钥"
}
```

- `InnerslothApiBaseUrl` 留空 = 使用默认公共网关。
- `InnerslothProxyKey` 必须和网关启动时的密钥一致；网关没设密钥就两边都留空。

### 5. 验证

```bash
# 不带密钥应返回 401，路径不对应返回 403 —— 说明网关在运行
curl -i http://你的网关地址:58080/api/user/username
```

然后用游戏进服，服务端日志出现 `FriendCode fetched from Innersloth username API` 即成功。服务端会把结果缓存在 `Data/PuidToFriendCode.txt`，同一个玩家只在第一次时请求网关。

### 安全建议

1. 能限制来源就用防火墙只放行你的服务端 IP。
2. 暴露到公网时一定要设置密钥，并保持默认的路径白名单和限流。
3. 网关控制台会打印每个请求的来源 IP 和状态码，方便排查。

---

## English

### Why it exists

```
Game client ──► Fmpostor server ──► gateway (this program) ──► backend.innersloth.com
                GET /api/user/username                         (official API)
```

When a player joins, the server exchanges the player's EOS token for their friend code at the official Innersloth API. Many servers (especially in mainland China) cannot reach that API directly, so a machine that **can** reach it relays the request. That relay is this gateway.

Fmpostor uses the public gateway `http://backend.playerinfo.impwm.fcaugame.cn` by default. The public gateway may be rate-limited, require a key or be temporarily down; if friend codes don't come through on your server (placeholders are shown), host your own with the steps below.

### 1. Pick a machine

- It **must reach `backend.innersloth.com` directly** (an overseas VPS, for example). `curl -I https://backend.innersloth.com` should return an HTTP response.
- Install the **ASP.NET Core Runtime 8.0** (Ubuntu/Debian: `sudo apt-get install -y aspnetcore-runtime-8.0`), or build self-contained as shown below.

### 2. Get the program

- **Download**: `InnerslothGateway-win-x64.zip` or `InnerslothGateway-linux-x64.tar.gz` from [Releases](https://github.com/SailingBoat-ovo/Fmpostor/releases/latest).
- **Build it yourself** (.NET 8 SDK):

```bash
dotnet publish InnerslothGateway.csproj -c Release -r linux-x64 -p:PublishSingleFile=true -o out/linux
# Windows: -r win-x64
# No runtime on the host (about 70 MB): add --self-contained true
```

### 3. Run it

```bash
# make a random key
openssl rand -hex 32

# Linux, foreground test
chmod +x InnerslothGateway start.sh
./start.sh 58080 your-key

# Windows
InnerslothGateway.exe 58080 your-key
```

Arguments: `InnerslothGateway [port] [key] [target]`

| Argument / env var | Default | Meaning |
|---|---|---|
| port | `58080` | Listen port; change freely |
| key | none | When set, requests must carry the same `X-Proxy-Key` header or get 401. **Always set it on a public host** |
| target / `INNERSLOTH_TARGET` | `https://backend.innersloth.com` | Rarely needs changing |
| `GATEWAY_RATE_LIMIT` | `60` | Max requests per IP per minute, `0` = unlimited |
| `GATEWAY_ALLOWED_PREFIX` | `api` | Only paths under this prefix are forwarded; others get 403, so the gateway can't be used as a general proxy |

For a systemd service, see the unit file in the Chinese section above (`ExecStart=/opt/innersloth-gateway/InnerslothGateway 58080 your-key`). To serve it on port 80/443 or a domain, put Nginx / Caddy / a CDN in front of the gateway port.

### 4. Point Fmpostor at your gateway

Add this to the `WebAdmin` section of Fmpostor's `config.json`, then restart the server:

```json
"WebAdmin": {
  "InnerslothApiBaseUrl": "http://your-gateway:58080",
  "InnerslothProxyKey": "your-key"
}
```

- Leave `InnerslothApiBaseUrl` empty to use the default public gateway.
- `InnerslothProxyKey` must match the gateway's key; if the gateway has no key, leave both empty.

### 5. Check it

```bash
# 401 without the key, 403 for a wrong path: the gateway is up
curl -i http://your-gateway:58080/api/user/username
```

Then join with the game; the server log line `FriendCode fetched from Innersloth username API` means it works. Results are cached in `Data/PuidToFriendCode.txt`, so each player hits the gateway only once.

### Security

1. If you can, firewall the gateway port to your server's IP only.
2. On a public host, always set a key and keep the default path allow-list and rate limit.
3. The gateway console prints each request's source IP and status code for troubleshooting.
