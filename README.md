# Fmpostor

[中文](#中文) · [English](#english)

---

## 中文

**帆船的 Impostor 服务端** —— 面向私服运营者的 All-in-one Among Us 服务端。基于 [Impostor](https://github.com/Impostor/Impostor)（GPL-3.0）二次开发，内置 Web 管理面板与全套自动化运营能力。

> [!IMPORTANT]
> **好友代码依靠网关获取。** 玩家进服时，服务端要用玩家的令牌向 Innersloth 官方接口查询好友代码。Fmpostor 默认通过公共网关 `http://backend.playerinfo.impwm.fcaugame.cn` 查询。
> 公共网关可能限流、需要密钥或暂时不可用 —— **如果你的服务器取不到好友代码（显示占位符），请自己搭建网关**：网关程序已开源，源码和搭建教程见 👉 **[InnerslothGateway/](InnerslothGateway/)**，搭好后在 `config.json` 的 `WebAdmin` 里填 `InnerslothApiBaseUrl`（网关地址）和 `InnerslothProxyKey`（网关密钥）即可。

> 当前版本：**Max-800.1** · 支持 Among Us **19.0** · 完整更新日志见 [CHANGELOG.md](CHANGELOG.md)

### ✨ 核心优势

- 🎮 **支持最新 Among Us 19.0**（含新职业 Spirit Guide）
- 🔌 **断线重连**（NOS 模组）— 掉线后重进同一房间，职业 / 位置 / 任务 / 生死全部恢复；会议中也能重连回来投票；新玩家可中途观战
- 🪪 **真实好友码** — 通过网关直连官方（[网关开源，可自建](InnerslothGateway/)），好友码 / PUID 非占位符；大厅社交面板（手机按钮）像官方服一样显示所有人的好友代码
- 🤖 **AI 管家** — 一句话查数据；Agent 自动执行面板操作；游戏内 `/aichat`
- 📼 **对局复盘** — 身份分配 / 击杀链 / 投票时间线全存档
- ⏰ **定时任务 + AI** — 定时发消息、清数据，还能定时让 AI 干活
- 🛡️ **全链路审计** — 举报 / 进出服 / 聊天 / IP 足迹留痕，一键封禁
- 📣 **QQ 群联动**（AstrBot，可直接接入 QQ 官方机器人）— `#在线状态` 查房间、`/m` 推送房间报告、`#订阅` 游戏结束提醒；配套插件 👉 [astrbot_plugin_fmpostor](https://github.com/SailingBoat-ovo/astrbot_plugin_fmpostor)
- 🧩 **常用插件全部内置** — 欢迎语（含一言）、解散房间、房主转让、称号、自动开局、房间监控，无需另装
- 📱 **手机竖屏面板** — 抽屉菜单 + 底部导航，表格自动变卡片
- 📶 **真实延迟** — 面板延迟精确到个位毫秒（不再被钳在 50ms 以上）

### 🔌 断线重连（NOS）

掉线的玩家用同一个房间号重新加入，就能回到原对局：**职业 / 幽灵职业 / 附加职业 / 位置 / 任务 / 生死** 全部恢复。会议进行中也能进房：重连者以原身份继续投票，散会后自动续局（房主装有 NebulaResume v2.0 时启用；旧版插件维持“会议中不放行、会后自动开放”，保槽不会被浪费）。

- 服务端部分就在本仓库（`src/Fmpostor.Server/Net/State/Game.Reconnect.cs`、`Game.MeetingGate.cs`、`Game.MeetingJoin.cs`、`Net/Reconnect/`、`Net/Custom/NebulaResumeRpc.cs`）。
- **NOS 客户端插件的源码、安装方法和实现说明**在独立仓库 👉 **[SailingBoat-ovo/NebulaResume](https://github.com/SailingBoat-ovo/NebulaResume)**。

开启方法：在 `config.json` 加入（整段不写 = 功能关闭，行为与旧版一致）：

```json
"Reconnect": {
  "Enabled": true,
  "GracePeriodSeconds": 45,
  "EnableSpectator": false,
  "MaxSpectators": 3
}
```

| 配置项 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `false` | 总开关 |
| `GracePeriodSeconds` | `45` | 掉线后保留位置的秒数 |
| `EnableSpectator` | `false` | 允许新玩家中途以观战者加入 |
| `MaxSpectators` | `3` | 每房观战人数上限 |
| `MeetingJoinGraceSeconds` | `17` | 会议结束后的兜底等待秒数 |
| `ResumeConfirmSeconds` | `10` | 回来后稳定连接多久才算真正用掉保槽 |
| `RequireSameIp` | `false` | 要求从同一 IP 回来 |

### 🧩 内置命令

| 命令 | 说明 |
|---|---|
| `/help` | 显示全部命令 |
| `/d [原因]` | 房主解散房间 |
| `/nexthost` | 房主转让 |
| `/auto on/off` · `/auto starttime <秒>` · `/auto coldtime <秒>` | 人满自动开局 |
| `/title help` · `/title set 1/2/3 …` | 称号系统 |
| `/m on/off` · `/m [备注]` | 房间 QQ 群广播开关 / 立即推送 |
| `/players` | 查看本房间玩家列表（好友代码、延迟） |
| `/maxplayers <人数>` | 房主在大厅修改最大人数（5~127） |
| `/report` | 交互式举报（也可直接用游戏聊天框旁的举报按钮） |
| `/aichat <内容>` | 与 AI 聊天（需管理员开启） |

### 🧠 AI 管家

- 面板对话带上下文记忆，可对服务器数据做自然语言分析
- **Agent 模式**：AI 自动调用面板接口执行操作（仅管理员可用）
- 游戏内 `/aichat` 私聊 AI（可开关）
- **按需联网**：AI 自行判断何时联网，消息里带网址时直接访问该网页
- 支持任意 OpenAI 兼容端点（智谱 / DeepSeek / Moonshot / OpenAI…）与 Claude API
- **开源版不内置任何 API Key**，服主在面板「AI 设置」自行配置即可启用

### 🛠 更多能力

房间码池 · 多端口池 · 违禁词过滤（含触发统计）· 欢迎语（新老玩家分开、延迟发送）· 称号 · 断线重连记录页 · 数据大屏 · 玩家足迹与时长统计 · 操作日志审计 · 面板三语（简体中文 / 繁體中文 / English）

### 🚀 部署

**Windows**

1. 从 [Releases](https://github.com/SailingBoat-ovo/Fmpostor/releases) 下载 `Fmpostor.Server-win-x64.zip` 并解压
2. 按需修改 `config.json`（公网 IP、端口等，默认即可直接运行）
3. 双击 `Fmpostor.Server.exe`，在防火墙放行 UDP 22023
4. 游戏内添加服务器：`你的公网IP:22023`

**Linux**

```bash
tar -xzf Fmpostor.Server-linux-x64.tar.gz
chmod +x Fmpostor.Server
./Fmpostor.Server
```

开机自启（systemd）——新建 `/etc/systemd/system/fmpostor.service`：

```ini
[Unit]
Description=Fmpostor Among Us Server
After=network.target

[Service]
WorkingDirectory=/opt/fmpostor
ExecStart=/opt/fmpostor/Fmpostor.Server
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

然后 `systemctl daemon-reload && systemctl enable --now fmpostor`。

**从源码编译**

```bash
git clone https://github.com/SailingBoat-ovo/Fmpostor.git
cd Fmpostor
dotnet publish src/Fmpostor.Server/Fmpostor.Server.csproj -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true -o out
# Windows 用 -r win-x64；另支持 linux-arm / linux-arm64 / osx-x64
```

标准 `dotnet publish`，无任何混淆。

**Web 管理面板**

- **官方面板（推荐）**：直接打开 [https://impwm.fcaugame.cn/](https://impwm.fcaugame.cn/)，填入你的服务器地址即可管理 —— 可连接任何 Fmpostor 服务器，无需任何配置
- **自托管**：把仓库 `panel/` 目录上传到任意 PHP 8.0+ 主机即可（同样可连接任何服务器，无白名单限制）

**HTTPS（游戏 16.0.5+ 要求）**

用 Nginx / Caddy 将 443 反代到服务端 22023 端口即可，详见上游文档 [docs/Http-server.md](https://github.com/Impostor/Impostor/blob/master/docs/Http-server.md)。

### 📝 更新日志

**Max-800.1-20261122**（最新）

- 🪪 修复好友代码获取：网关地址改用 80 端口（原 58080 端口已不可用）
- 🔓 好友代码网关程序开源（[InnerslothGateway/](InnerslothGateway/)），附自建教程

**Max-800.0-20261122**

- 🔌 断线重连完整版：会议中重连、观战者中途加入、重连后正常结算、结算总结里同一人只显示一次并标注“重连玩家”（需 NebulaResume v2.0）
- 🍾 一言回归；👥 新命令 `/players`、`/maxplayers`；📢 接入游戏自带举报按钮
- 🤖 QQ 机器人改用 AstrBot（可接 QQ 官方机器人），配套插件 [astrbot_plugin_fmpostor](https://github.com/SailingBoat-ovo/astrbot_plugin_fmpostor)
- 📱 面板手机竖屏界面；面板所有确认 / 踢人改用自绘对话框（点取消就真的不执行）
- 🖥️ 启动界面改为“帆船AmongUs服务器”彩色字样；内存占用更低；多处空引用防护

**Turbo-700.0-20261024**

- 🎮 支持 Among Us 19.0（2026.9.29）：新职业 Spirit Guide 与新 RPC 的反作弊校验、选项版本 12
- 🔌 NOS 断线重连 + 观战席：保槽、新身份续局、两阶段确认、会议闸门、面板重连记录页；客户端插件见 [NebulaResume](https://github.com/SailingBoat-ovo/NebulaResume)
- 🧩 常用插件全部内置：欢迎语（新老玩家 / 延迟）、`/d`、`/nexthost`、`/title`、`/auto`、QQ 房间监控（补记以往未写进日志的功能）
- 🪪 大厅社交面板显示所有玩家的好友代码
- 📶 面板延迟显示真实值（低于 50ms）
- 启动面板重做、JSONL 日志格式误判修复、面板样式重做

完整内容见 [CHANGELOG.md](CHANGELOG.md)；更早版本见 `Turbo650更新日志.txt`、`Turbo640更新日志.txt`。

### ⚠️ 声明

> 本服务端大部分源码由 AI 产出。使用中遇到任何 bug 或漏洞，欢迎反馈至 [admin@fanchuanovo.cn](mailto:admin@fanchuanovo.cn)。

### 📄 协议

本项目基于 [Impostor](https://github.com/Impostor/Impostor)（GPL-3.0）二次开发，依 [GPL-3.0](LICENSE) 开源。

---

## English

**Fanchuan's Impostor server**: an all-in-one Among Us server for private-server operators. A fork of [Impostor](https://github.com/Impostor/Impostor) (GPL-3.0) with a built-in web admin panel and a full set of automation for running a community server.

> [!IMPORTANT]
> **Friend codes are fetched through a gateway.** When a player joins, the server uses their token to look up the friend code at the official Innersloth API. By default Fmpostor goes through the public gateway `http://backend.playerinfo.impwm.fcaugame.cn`.
> The public gateway may be rate-limited, require a key or be temporarily down. **If your server can't get friend codes (placeholders are shown), host your own gateway**: it is open source, with source and a setup guide in 👉 **[InnerslothGateway/](InnerslothGateway/)**. Then set `InnerslothApiBaseUrl` (gateway address) and `InnerslothProxyKey` (gateway key) under `WebAdmin` in `config.json`.

> Current version: **Max-800.1** · supports Among Us **19.0** · full changelog in [CHANGELOG.md](CHANGELOG.md)

### ✨ Highlights

- 🎮 **Among Us 19.0 support**, including the new Spirit Guide role
- 🔌 **Disconnect & reconnect** (NOS mod): rejoin the same room after a drop and get role / position / tasks / alive-or-dead state back, even in the middle of a meeting; new players can join a running round as spectators
- 🪪 **Real friend codes**: a gateway talks to the official API ([open source, self-hostable](InnerslothGateway/)), so friend codes and PUIDs are real, not placeholders. The lobby social panel (phone button) shows everyone's friend code, just like the official servers
- 🤖 **AI assistant**: ask about your data in plain language; Agent mode runs panel actions; in-game `/aichat`
- 📼 **Match replays**: role assignment, kill chains and vote timelines are all archived
- ⏰ **Scheduled tasks + AI**: send messages, clean data, or have the AI run jobs on a schedule
- 🛡️ **Full audit trail**: reports, joins/leaves, chat and IP footprints are logged; ban in one click
- 📣 **QQ group integration** (through AstrBot, which connects to the official QQ bot directly): `#在线状态` room list, `/m` room reports, `#订阅` game-end reminders; companion plugin 👉 [astrbot_plugin_fmpostor](https://github.com/SailingBoat-ovo/astrbot_plugin_fmpostor)
- 🧩 **Common plugins built in**: welcome messages (with Hitokoto quotes), close room, host transfer, titles, auto start and room monitor, with no extra installs
- 📱 **Phone-friendly panel**: slide-out menu and bottom navigation; tables turn into cards
- 📶 **Real latency**: the panel shows latency down to single-digit milliseconds (no longer clamped at 50 ms)

### 🔌 Disconnect & reconnect (NOS)

A player who drops can rejoin the same room code and return to the round, with **role / ghost role / modifiers / position / tasks / alive-or-dead state** all restored. Players can also rejoin during a meeting: they vote under their original identity and the round resumes once the meeting ends (enabled when the host runs NebulaResume v2.0; with older add-ons, joins are refused during meetings and reopen automatically afterwards, so no reserved slot is wasted).

- The server side lives in this repository: `src/Fmpostor.Server/Net/State/Game.Reconnect.cs`, `Game.MeetingGate.cs`, `Game.MeetingJoin.cs`, `Net/Reconnect/` and `Net/Custom/NebulaResumeRpc.cs`.
- **The NOS client add-on (source, installation and how it works)** lives in its own repository: 👉 **[SailingBoat-ovo/NebulaResume](https://github.com/SailingBoat-ovo/NebulaResume)**.

To turn it on, add this to `config.json`. Leaving the section out turns the feature off and keeps the old behaviour:

```json
"Reconnect": {
  "Enabled": true,
  "GracePeriodSeconds": 45,
  "EnableSpectator": false,
  "MaxSpectators": 3
}
```

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch |
| `GracePeriodSeconds` | `45` | How long a dropped player's slot is kept, in seconds |
| `EnableSpectator` | `false` | Let new players join a running round as spectators |
| `MaxSpectators` | `3` | Spectator limit per room |
| `MeetingJoinGraceSeconds` | `17` | Fallback wait after a meeting ends, in seconds |
| `ResumeConfirmSeconds` | `10` | How long a returning player must stay connected before the slot is spent |
| `RequireSameIp` | `false` | Require the player to come back from the same IP |

### 🧩 Built-in commands

| Command | What it does |
|---|---|
| `/help` | List every command |
| `/d [reason]` | Host closes the room |
| `/nexthost` | Host transfer |
| `/auto on/off` · `/auto starttime <s>` · `/auto coldtime <s>` | Auto start when the room is full |
| `/title help` · `/title set 1/2/3 …` | Title system |
| `/m on/off` · `/m [note]` | Toggle this room's QQ broadcast / push a report now |
| `/players` | List the players in this room (friend codes, latency) |
| `/maxplayers <n>` | Host changes the room size in the lobby (5–127) |
| `/report` | Interactive player report (the game's own report button works too) |
| `/aichat <text>` | Chat with the AI (admin must enable it) |

### 🧠 AI assistant

- Panel chat with conversation memory; natural-language analysis of server data
- **Agent mode**: the AI calls panel endpoints to carry out actions (admins only)
- In-game `/aichat` private chat with the AI (can be turned off)
- **Web access on demand**: the AI decides when to go online, and opens links included in a message directly
- Works with any OpenAI-compatible endpoint (Zhipu / DeepSeek / Moonshot / OpenAI…) and the Claude API
- **The open-source build ships no API key**; operators add their own under "AI Settings" in the panel

### 🛠 More

Room code pool · multi-port pool · banned-word filter with statistics · welcome messages (separate first-time / returning, delayed) · titles · reconnect log page · data dashboards · player footprints and play time · operation audit log · panel in Simplified Chinese / Traditional Chinese / English

### 🚀 Deployment

**Windows**

1. Download `Fmpostor.Server-win-x64.zip` from [Releases](https://github.com/SailingBoat-ovo/Fmpostor/releases) and unzip it
2. Edit `config.json` if needed (public IP, ports…; the defaults run as is)
3. Run `Fmpostor.Server.exe` and allow UDP 22023 through the firewall
4. Add the server in game: `your-public-ip:22023`

**Linux**

```bash
tar -xzf Fmpostor.Server-linux-x64.tar.gz
chmod +x Fmpostor.Server
./Fmpostor.Server
```

To start on boot, create `/etc/systemd/system/fmpostor.service` with the unit shown in the Chinese section above, then run `systemctl daemon-reload && systemctl enable --now fmpostor`.

**Build from source**

```bash
git clone https://github.com/SailingBoat-ovo/Fmpostor.git
cd Fmpostor
dotnet publish src/Fmpostor.Server/Fmpostor.Server.csproj -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true -o out
# use -r win-x64 on Windows; linux-arm / linux-arm64 / osx-x64 also work
```

A standard `dotnet publish`, with no obfuscation.

**Web admin panel**

- **Hosted panel (recommended)**: open [https://impwm.fcaugame.cn/](https://impwm.fcaugame.cn/) and enter your server address. It can manage any Fmpostor server with no setup.
- **Self-hosted**: upload the `panel/` folder to any PHP 8.0+ host. It can also connect to any server, with no whitelist.

**HTTPS (required by game 16.0.5+)**

Reverse-proxy port 443 to the server's port 22023 with Nginx or Caddy; see the upstream guide [docs/Http-server.md](https://github.com/Impostor/Impostor/blob/master/docs/Http-server.md).

### 📝 Changelog

**Max-800.1-20261122** (latest)

- 🪪 Fixed friend-code lookup: the gateway is now reached on port 80 (port 58080 no longer works)
- 🔓 The friend-code gateway is now open source ([InnerslothGateway/](InnerslothGateway/)), with a self-hosting guide

**Max-800.0-20261122**

- 🔌 Reconnect, complete: rejoin during meetings, spectators joining mid-round, normal game end after reconnects, and an end-of-game summary that lists each player once with a "reconnected" tag (needs NebulaResume v2.0)
- 🍾 Hitokoto is back; 👥 new `/players` and `/maxplayers` commands; 📢 the game's own report button now reaches the panel
- 🤖 QQ bot now runs through AstrBot (works with the official QQ bot); companion plugin [astrbot_plugin_fmpostor](https://github.com/SailingBoat-ovo/astrbot_plugin_fmpostor)
- 📱 Phone layout for the panel; every panel confirmation and kick now uses an in-page dialog (Cancel really cancels)
- 🖥️ New startup screen; lower memory use; extra null-reference guards

**Turbo-700.0-20261024**

- 🎮 Among Us 19.0 (2026.9.29) support: the new Spirit Guide role, anticheat validation for its new RPC, options version 12
- 🔌 NOS disconnect & reconnect + spectator seats: slot reservation, resume as a new identity, two-phase confirmation, meeting gate and a reconnect log page in the panel. The client add-on is [NebulaResume](https://github.com/SailingBoat-ovo/NebulaResume)
- 🧩 Common plugins built in: welcome messages (first-time / returning, delay), `/d`, `/nexthost`, `/title`, `/auto` and the QQ room monitor (including features that were built in earlier but never logged)
- 🪪 The lobby social panel shows every player's friend code
- 📶 Real latency in the panel (below 50 ms)
- Startup panel redone, JSONL log format fix, panel restyle

Full details in [CHANGELOG.md](CHANGELOG.md); older versions in `Turbo650更新日志.txt` and `Turbo640更新日志.txt`.

### ⚠️ Disclaimer

> Most of this server's source code was generated by AI. If you find a bug or vulnerability, please report it to [admin@fanchuanovo.cn](mailto:admin@fanchuanovo.cn).

### 📄 License

Based on [Impostor](https://github.com/Impostor/Impostor) (GPL-3.0) and released under [GPL-3.0](LICENSE).
