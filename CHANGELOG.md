# 更新日志 / Changelog

[中文](#中文) · [English](#english)

---

## 中文

### Max-800.1-20261122

上一版本：Max-800.0-20261122。可在面板「自动更新」一键升级。

- 🪪 修复好友代码获取失败：默认网关地址由 `http://backend.playerinfo.impwm.fcaugame.cn:58080` 改为 `http://backend.playerinfo.impwm.fcaugame.cn`（80 端口）。在 `config.json` 里自己设置过 `WebAdmin:InnerslothApiBaseUrl` 的不受影响。
- 🔓 好友代码网关程序开源：源码和自建教程在仓库的 `InnerslothGateway/` 目录，Release 附 Windows / Linux 编译版。公共网关不可用时可以自己搭建。

### Max-800.0-20261122

上一版本：Turbo-700.0-20261024。700 服务器可在面板「自动更新」一键升级。版本号从 Turbo 系列进入 **Max** 系列。

#### 🔌 断线重连完整版（配合 NebulaResume v2.0）

- **会议中重连**：会议进行中也能进房。重连者以原身份继续投票，会议（含放逐动画）结束后自动续局；原身份若在这场会议里被投出，回来就是灵魂。需要房主装有 NebulaResume v2.0，旧版插件维持“会议中不放行、会后开放”。
- **观战者中途加入**：允许观战时，新玩家可在对局中途以中立观战者身份进入，不参与胜负。
- **重连后正常结算**：满足胜利条件时，没有回来的旧身份和已被新身份接替的旧身份都会被清理，不再出现“满足条件却不结算”。
- **结算总结**：同一个人重连多次只显示一行，不再出现占位用的船员职业；重连过的玩家标注「重连玩家」。
- 修复重连后附加的「紧急会议」技能重复出现。
- 修复中途生成的身体在每帧更新里反复触发空引用（造成卡顿）。

#### 🖥️ 启动界面

- 控制台启动界面改为与「帆船AmongUs服务器工具箱」同一风格：彩色 FANCHUAN 大字、标题、版本号、监听端口 / 公网地址 / HTTP 服务 / 配置文件状态；窗口标题显示版本。

#### 🧾 面板对话框

- 面板里所有确认（删除、封禁、清空等）和踢人原因输入都改为面板自绘的对话框，不再使用浏览器自带弹窗。
- 修复：踢人时点「取消」仍然会踢出。现在取消、按 Esc、点空白处都不会执行。
- 危险操作的确认按钮显示为红色；支持回车确认。

#### ⚡ 性能与稳定性（不影响功能）

- 改用工作站 GC（后台回收），常驻内存与线程数明显降低；启用 TieredPGO。
- 空引用防护：连接在注册前断开、AI 返回空回复、手改 JSON 把列表写成 null、增量端口在主端口就绪前启动等情况不再抛异常。

#### 🍾 一言（每小时的漂流瓶）回归

- 全服共用一句「当前一言」，按间隔自动刷新（默认每小时，可改）；每次发欢迎语时按概率（默认 5%）改用一条自定义一言，内置的「帆船」那条始终在自定义池里。
- 欢迎语里写 `{HITOKOTO}` / `{HITOKOTO_FROM}`，旧写法 `<YY>` / `<ZZ>` 同样有效；没写的话可以自动加在欢迎语末尾。
- 接口不通时用自定义一言兜底，不会出现空行。
- 面板「欢迎语」页新增一言卡片：开关、刷新间隔、自定义概率、接口地址、自定义一言（`句子|出处`，一行一条）、「立即换一句」。
- 首次启动自动导入旧的 `Fanchuan.Hitokoto.Config.json`。

#### 👥 新命令：`/players`、`/maxplayers`

- `/players`（也可用 `/list`、`#players`）：任何人可用，私聊返回本房间玩家列表：房间码、房主、人数/上限，每人的编号、名字、好友代码、延迟。人多时自动分段发送。
- `/maxplayers <人数>`（也可用 `/maxplayer`、`/max`、`#maxplayers`）：房主在大厅修改最大人数（5~127，不能少于当前人数），改完同步给全房间。
- 两条命令已加入 `/help`。

#### 📢 接入游戏自带的举报按钮

- 原版和模组玩家点聊天框旁边的举报按钮，举报都会进入面板「举报处理」，与 `/report` 在同一个列表（内容标注「游戏内举报按钮」和原因）。
- 游戏里会正常提示「已举报」；同一人 60 秒内重复举报同一个人、或 1 分钟内超过 5 次，提示「举报太频繁」。
- `/report` 交互式举报保留不变。

#### 🤖 QQ 机器人改用 AstrBot（替换 OneBot）

- 删除 OneBot/NapCat 连接，改为通过 **AstrBot** 收发 QQ 消息。AstrBot 可以直接接入 **QQ 官方机器人**。
- 发送：`/m` 房间报告、定时公告、订阅提醒通过 AstrBot 的 OpenAPI（`POST /api/v1/im/messages`，兼容旧版 `/im/message`）发到面板里填的会话（UMO）。
- 接收群命令：新增配套 AstrBot 插件 [`astrbot_plugin_fmpostor`](https://github.com/SailingBoat-ovo/astrbot_plugin_fmpostor)（发布包里也附带），把 `#在线状态` / `#订阅` / `#退订` / `#帮助` 转发给服务端，用桥接口令校验。
- `#订阅` 的提醒改为发回订阅时所在的群（QQ 官方机器人不能私聊只在群里出现过的成员）。
- QQ 官方机器人在群里主动发言需要群主在群设置里打开「机器人主动在群聊内发言」；群命令的回复不受影响。
- ⚠️ 升级后需要在面板重新填写 QQ 设置（AstrBot 地址、API Key、推送会话），旧的 OneBot 地址与群号不再使用；旧的订阅记录不会迁移。

#### 📱 面板手机竖屏界面

- 宽度 ≤ 760px 时自动切换：☰ 抽屉式菜单、底部导航（房间 / 玩家 / 聊天 / 举报 / 更多）、精简顶栏。
- 表格自动变成卡片（每行一张卡，带列名），表单单列排布，按钮和输入框加大，输入框不再触发 iOS 自动放大，弹窗全屏。
- 桌面端不受影响。

### Turbo-700.0-20261024

上一版本：Turbo-650.0-20260905。650 服务器可在面板「自动更新」一键升级。

本版三大重点：**支持 Among Us 19.0**、**NOS 断线重连**、**常用插件功能全部内置**。

---

#### 🎮 一、支持 Among Us 19.0（2026.9.29）

- 新增 19.0 版本兼容组（PC build 7489 / 移动端 build 7492），19.0 客户端可以正常进服、建房、加入。
- 新职业 **Spirit Guide**（RoleTypes = 21）及其职业选项。
- 新 RPC `SpiritGuideMessage`（67）带反作弊校验：只有该职业能发送，消息图片数量必须是 1~3 张。
- 游戏选项版本升至 12；StringNames / RpcCalls / 外观数据同步更新。
- 反作弊新增类别 `ItemLimits`（数量越界，开关 `EnableItemLimitChecks`，默认开启）；另预留 `Identifiers` 类别与 `EnableIdentifierChecks` 开关，暂无检查使用。
- 关门 RPC 的发送者没有角色时不再空引用。

---

#### 🔌 二、断线重连 + 观战席（NOS 模组，默认关闭）

掉线的玩家用同一个房间号重新加入，就能回到原来的对局，**职业 / 幽灵职业 / 附加职业 / 位置 / 任务 / 生死** 全部恢复。

**怎么用**

1. 服务端 `config.json` 加上下面这段（整段不写 = 功能完全关闭，行为与旧版一致）：

   ```json
   "Reconnect": {
     "Enabled": true,
     "GracePeriodSeconds": 45,
     "EnableSpectator": false,
     "MaxSpectators": 3,
     "MeetingJoinGraceSeconds": 17,
     "ResumeConfirmSeconds": 10
   }
   ```

2. 玩家安装配套的 NOS 客户端插件 **NebulaResume**，源码与安装说明见独立仓库 👉 [SailingBoat-ovo/NebulaResume](https://github.com/SailingBoat-ovo/NebulaResume)。
3. 掉线后回主菜单，用「输入房间号」重新进入同一个房间即可。

**服务端做了什么**

- **保槽**：玩家意外掉线后保留位置 `GracePeriodSeconds` 秒（默认 45）。同一身份（好友码 / PUID）在窗口内回来即可续局；被踢出、被封禁不保槽。
- **新身份续局**：回来的人以新的 PlayerId 进场，旧身份由服务端干净地移除并销毁，不会留下"残影"。服务端**不创建、不序列化角色**——身体由房主客户端生成，服务端只负责登记和下发指令。
- **两阶段确认**：身体就位并稳定连接 `ResumeConfirmSeconds` 秒（默认 10）后才真正消费保槽；在此之前再次掉线，保槽和模组状态原样还回去，下一次仍能恢复。
- **会议闸门**：会议进行中（会议开始 → 投票结束 → 放逐动画 → 重生选点）拒绝重连和观战加入，**不消耗保槽**。会议结束优先以房主模组上报的状态为准，会后 2 秒重新开放；兜底为投票结束 + `MeetingJoinGraceSeconds`（默认 17 秒）。
- **模组状态容器**：客户端上报的模组状态当作不透明字节存储和转发，服务端不解析——模组升级后服务端不用改。
- **观战席**（`EnableSpectator`）：全新玩家可以在对局中途以中立观战者身份加入，不计入任何阵营、不影响胜负，上限 `MaxSpectators`。
- **面板「断线重连」页**：重连统计、当前等待重连的玩家、重连事件记录（谁掉线、谁回来了、在哪个房间、隔了多久），支持一键清空。新接口：`api/reconnect/logs` · `stats` · `events` · `current` · `clear`。
- 原版客户端也能重连（恢复原版状态）；**模组状态的恢复需要 NebulaResume 插件**。
- `DevAllowKickReserve` / `DevAllowHostReserve` 仅供测试（让"踢出"或房主掉线也保槽），生产环境请保持 `false`。

**客户端插件这一版修好的问题**

- 重连后，场上其他玩家在重连者眼里全变成船员 → 重连者进场后向房主同步全场职业。
- 别人眼里重连者是没有职业的白板船员 → 重连者恢复的职业连同参数广播给全场。
- 会议期间重连后回到会议桌而不是原位置 → 位置取自本人身体，会议中冻结为会前位置。
- 会议后重连提示"服务器没有保留位置"、任务列表消失 → 服务端身体生成后再下发续局信号。
- 会议投票格只显示名字、没有外观；紧急会议按钮冷却被重置。
- 旧身份身体销毁后，日志每帧刷 `SEHException`（几 MB）→ 自动把失效玩家标记为掉线。

---

#### 🧩 三、常用插件功能全部内置（不用再单独装插件）

以前需要另外安装的几个插件，功能已全部并入服务端，开箱即用，并可在面板管理。其中部分功能在之前的版本里已经内置，但从未写进更新日志，这里一并补记。

| 原插件 | 内置后的功能 |
|---|---|
| 欢迎语插件 | 玩家进房后私聊欢迎语；区分**首次来本服的新玩家**与**老玩家**（老玩家显示上次登录时间、累计游玩时长）；可设置**进房后延迟**（默认 3 秒，0~30）；文案在面板编辑，占位符 `{PLAYER_NAME}` `{Room}` `{LAST_LOGIN_TIME}` `{TIME_SINCE_LAST_LOGIN}` `{PLAY_TIME}`。 |
| 房间解散插件 | `/d [原因]`：房主解散房间。 |
| 房主转让插件 | `/nexthost`：房主选择玩家编号，把房主转让给他。 |
| 称号插件 | 昵称旁显示自定义称号，面板管理称号与玩家；`/title help`、`/title set 1 true/false`（启用/禁用）、`/title set 2 true/false`（是否带 `[]`）、`/title set 3 left/right`（显示在名字前/后）。 |
| 自动开局插件 | `/auto on/off`：人满自动开局；`/auto starttime <1~60>` 人满后倒计时；`/auto coldtime <0~300>` 一局结束后的冷却；`/auto help`。 |
| 房间监控插件（QQ 群） | 开房 / 满员推送到 QQ 群；`/m on/off` 房主开关本房间广播；`/m [备注]` 立即推送房间报告；群内 `#在线状态` 查询（命令可自定义）、`#订阅` / `#退订` / `#帮助`。 |

游戏内输入 `/help` 可查看全部命令。

本版对这些功能的改进：

- **欢迎语**：默认文案更新；新增「进房后延迟」和「新玩家欢迎语」（留空则与普通列表共用）。
- 修复：上次登录时间在欢迎语生成之前就被覆盖，老玩家看到的"上次登录"总是本次。
- 修复：欢迎语里的名字为空（身体刚生成时游戏内名字还没同步；现在延迟结束后再读，读不到就用账号名）。
- 旧版 `{Name}` 写法的默认文案自动升级为新默认。
- 原来写死在代码里的房主提示（`/m off` 说明）并入欢迎语默认文案，不再重复发送两条。

---

#### 🪪 大厅社交面板显示好友代码

- 等待大厅右上角社交面板（手机按钮）里，现在能像官方服务器一样看到所有玩家的好友代码，原版和模组客户端都适用。
- 原因：服务端生成的玩家资料对象里，好友代码和 PUID 一直写的是空字符串。现在写入服务端登录时换取到的真实代码。
- 占位代码（含取码失败时生成的 `failauth#xxxx`）不再下发给客户端，避免玩家拿假代码去加好友；面板里照常显示。
- 房主声明的代码只在服务端自己不知道时才采用，不能覆盖服务端的权威值。

#### 📶 面板延迟显示真实值（低于 50ms 也能显示）

- 旧版延迟来自 Hazel 的平滑值，内部被 `Math.Max(50, …)` 钳住，永远显示 ≥ 50ms。
- 现在每个连接每 2 秒发一个 Hazel Ping 包并精确计时它的确认（ACK），不受下限影响；探测不可用时依次回退到 ICMP、Hazel。

#### 🖥 其他

- 启动面板重做：一次画完（不再逐句等待约 12 秒），版本、端口、公网 IP、配置状态从配置读取，中文对齐正确。
- 修复玩家行为日志 JSONL 格式误判（650 性能优化引入的回归），混合格式文件也能正确读取。
- 面板样式重做（同一套 class，不影响功能）。
- 全局版本号更新至 Turbo-700.0-20261024。

---

## English

### Max-800.1-20261122

Previous version: Max-800.0-20261122. Upgrade in one click from the panel's **Auto Update** page.

- 🪪 Fixed friend-code lookups failing: the default gateway address changed from `http://backend.playerinfo.impwm.fcaugame.cn:58080` to `http://backend.playerinfo.impwm.fcaugame.cn` (port 80). Servers that set `WebAdmin:InnerslothApiBaseUrl` in `config.json` themselves are not affected.
- 🔓 The friend-code gateway is now open source: source and a self-hosting guide live in `InnerslothGateway/`, and the release includes Windows / Linux builds. Host your own if the public gateway is unavailable.

### Max-800.0-20261122

Previous version: Turbo-700.0-20261024. A 700 server can upgrade in one click from the panel's **Auto Update** page. Version numbers move from the Turbo series to the **Max** series.

#### 🔌 Reconnect, complete (with NebulaResume v2.0)

- **Rejoin during meetings**: players can now enter while a meeting is running. They vote under their original identity, and the round resumes once the meeting (including the ejection) ends; if that identity was voted out, they come back as a ghost. Needs the host to run NebulaResume v2.0; with older add-ons, joins stay closed during meetings and reopen afterwards.
- **Spectators mid-round**: when spectating is allowed, new players can join a running round as neutral spectators who cannot win or lose.
- **Games end normally after reconnects**: once a win condition is met, old identities that never came back, and old identities already replaced by a new one, are cleaned up, so the game no longer hangs without ending.
- **End-of-game summary**: a player who reconnected several times is listed once, without the placeholder crewmate role, and tagged "reconnected".
- Fixed the extra "Emergency meeting" modifier after reconnecting.
- Fixed bodies spawned mid-round throwing a null reference on every physics frame (stutter).

#### 🖥️ Startup screen

- The console startup screen now matches the "帆船AmongUs服务器" toolbox style: coloured FANCHUAN banner, title, version, listen port / public address / HTTP / config status, and the version in the window title.

#### 🧾 Panel dialogs

- Every panel confirmation (delete, ban, clear…) and the kick-reason input now use an in-page dialog instead of the browser's own popups.
- Fixed: pressing Cancel on a kick still kicked the player. Cancel, Esc or clicking outside the dialog now does nothing.
- Dangerous confirmations use a red button; Enter confirms.

#### ⚡ Performance and stability (no functional changes)

- Switched to workstation (background) GC: noticeably lower resident memory and fewer threads. TieredPGO enabled.
- Null-reference guards for a connection that drops before it is registered, an empty AI reply, JSON files edited to contain null lists, and a delta port starting before the main port is ready.

#### 🍾 Hitokoto ("the hourly drift bottle") is back

- One shared quote for the whole server, refreshed on an interval (hourly by default, configurable). Each welcome message has a chance (5% by default) to use a custom quote instead; the built-in "帆船" line is always in the custom pool.
- Use `{HITOKOTO}` / `{HITOKOTO_FROM}` in welcome texts; the old `<YY>` / `<ZZ>` placeholders still work. Without a placeholder, the quote can be appended automatically.
- When the quote API is unreachable a custom quote is used, so the line is never empty.
- New Hitokoto card on the panel's Welcome page: on/off, refresh interval, custom chance, API URL, custom quotes (`text|source`, one per line), "new quote now".
- The old `Fanchuan.Hitokoto.Config.json` is imported automatically on first start.

#### 👥 New commands: `/players`, `/maxplayers`

- `/players` (also `/list`, `#players`): anyone can use it. It privately lists the room: code, host, players/limit, and each player's number, name, friend code and latency. Long lists are split into several messages.
- `/maxplayers <n>` (also `/maxplayer`, `/max`, `#maxplayers`): the host changes the room size in the lobby (5–127, not below the current player count), and the change is synced to the whole room.
- Both are listed in `/help`.

#### 📢 The game's own report button now reaches the server

- When vanilla or modded players use the report button next to the chat box, the report lands in the panel's "Reports" page, in the same list as `/report` (marked "in-game report button" with the reason).
- The game shows "reported" as usual. Reporting the same player again within 60 s, or more than 5 times a minute, answers "rate limited".
- The interactive `/report` command is unchanged.

#### 🤖 QQ bot now runs through AstrBot (OneBot removed)

- The OneBot/NapCat connection is gone; QQ messages now go through **AstrBot**, which can connect to the **official QQ bot** platform directly.
- Sending: `/m` room reports, scheduled announcements and subscription reminders go out through AstrBot's OpenAPI (`POST /api/v1/im/messages`, with the older `/im/message` as a fallback) to the sessions (UMOs) entered in the panel.
- Group commands: a companion AstrBot plugin, [`astrbot_plugin_fmpostor`](https://github.com/SailingBoat-ovo/astrbot_plugin_fmpostor) (also included in the release), forwards `#在线状态` / `#订阅` / `#退订` / `#帮助` to the server, authenticated with a bridge token.
- `#订阅` reminders are now sent back to the group where the user subscribed (the official QQ bot cannot DM members it only knows from a group).
- For the official QQ bot to post on its own in a group, the group owner must enable "allow the bot to speak proactively in group chat". Replies to group commands are not affected.
- ⚠️ After upgrading, re-enter the QQ settings in the panel (AstrBot address, API key, target sessions). The old OneBot address and group numbers are no longer used, and old subscriptions are not migrated.

#### 📱 Phone (portrait) layout for the panel

- At widths of 760 px or less the panel switches to a phone layout: ☰ slide-out menu, bottom navigation (Rooms / Players / Chat / Reports / More) and a compact top bar.
- Tables become cards (one card per row, with column names), forms use one column, buttons and inputs are bigger, inputs no longer trigger iOS auto-zoom, and dialogs are full-screen.
- The desktop layout is unchanged.

### Turbo-700.0-20261024

Previous version: Turbo-650.0-20260905. A 650 server can upgrade in one click from the panel's **Auto Update** page.

The three headline changes: **Among Us 19.0 support**, **disconnect & reconnect for the NOS mod**, and **all the common plugins built in**.

---

#### 🎮 1. Among Us 19.0 (2026.9.29) support

- New 19.0 compatibility group (PC build 7489 / mobile build 7492): 19.0 clients can connect, host and join normally.
- New role **Spirit Guide** (RoleTypes = 21) and its role options.
- New RPC `SpiritGuideMessage` (67) with anticheat validation: only that role may send it, and the message must carry 1–3 images.
- Game options version bumped to 12; StringNames / RpcCalls / cosmetics data updated.
- New anticheat category `ItemLimits` (counts out of range; switch `EnableItemLimitChecks`, on by default). An `Identifiers` category and its `EnableIdentifierChecks` switch are reserved, but no check uses them yet.
- The close-doors RPC no longer throws a null reference when the sender has no character.

---

#### 🔌 2. Disconnect & reconnect + spectator seats (NOS mod, off by default)

A player who drops can rejoin the same room code and return to the round they were in, with **role / ghost role / modifiers / position / tasks / alive-or-dead state** all restored.

**How to use it**

1. Add this section to the server's `config.json`. Leaving the whole section out turns the feature off completely and keeps the old behaviour:

   ```json
   "Reconnect": {
     "Enabled": true,
     "GracePeriodSeconds": 45,
     "EnableSpectator": false,
     "MaxSpectators": 3,
     "MeetingJoinGraceSeconds": 17,
     "ResumeConfirmSeconds": 10
   }
   ```

2. Players install the companion NOS client add-on **NebulaResume**. Source and install guide: 👉 [SailingBoat-ovo/NebulaResume](https://github.com/SailingBoat-ovo/NebulaResume).
3. After a drop, go back to the main menu and rejoin the same room with "Enter code".

**What the server does**

- **Slot reservation**: when a player drops unexpectedly, the server keeps their slot for `GracePeriodSeconds` (default 45). The same identity (friend code / PUID) can resume within that window. Kicks and bans never reserve a slot.
- **Resume as a new identity**: the returning player enters with a new PlayerId, and the old identity is removed and despawned cleanly, leaving no ghost body. The server **never creates or serialises a character**: the host client spawns the body, and the server only registers it and sends the instructions.
- **Two-phase confirmation**: the slot is only spent once the body is in place and the connection has stayed up for `ResumeConfirmSeconds` (default 10). If the player drops again before that, the slot and its mod state are handed back, so the next attempt can still restore them.
- **Meeting gate**: during a meeting (meeting start → end of voting → exile animation → respawn pick), reconnects and spectator joins are refused **without consuming the slot**. The end of the meeting is taken from the state reported by the host's mod, and joins reopen 2 s later. The fallback is end of voting + `MeetingJoinGraceSeconds` (default 17 s).
- **Mod-state container**: client-reported mod state is stored and forwarded as opaque bytes and never interpreted, so mod updates need no server change.
- **Spectator seats** (`EnableSpectator`): a brand-new player may join a running round as a neutral spectator. Spectators belong to no team and never affect the result. The limit is set by `MaxSpectators`.
- **"Reconnect" page in the panel**: reconnect statistics, players waiting to reconnect, and the reconnect event log (who dropped, who came back, which room, after how long). The log can be cleared in one click. New endpoints: `api/reconnect/logs` · `stats` · `events` · `current` · `clear`.
- Vanilla clients can reconnect too, with vanilla state restored. **Restoring mod state needs the NebulaResume add-on.**
- `DevAllowKickReserve` / `DevAllowHostReserve` are for testing only: they make kicks or host drops reserve a slot. Keep them `false` in production.

**Fixed in the client add-on this release**

- After a reconnect, every other player looked like a crewmate to the reconnecter. The reconnecter now syncs everyone's roles from the host.
- Others saw the reconnecter as a blank crewmate. The restored role is now broadcast to everyone, together with its parameters.
- A reconnect during a meeting put the player back at the meeting table. Position now comes from the player's own body and is frozen to the pre-meeting spot during a meeting.
- A reconnect after a meeting said "no reserved slot" and lost the task list. The resume signal is now sent only after the body has spawned.
- Meeting vote areas showed only names without cosmetics, and the emergency button cooldown was reset.
- After the old body was despawned, the log filled with `SEHException` every frame (several MB). Stale players are now marked as disconnected automatically.

---

#### 🧩 3. Common plugins built in (no separate install)

The plugins that used to be installed separately are now part of the server, work out of the box, and can be managed from the panel. Some of them were already built into earlier versions but never made it into a changelog, so they are recorded here as well.

| Former plugin | Built-in feature |
|---|---|
| Welcome | Private welcome message after joining, with separate texts for **first-time visitors** and **returning players**. Returning players see their last login and total play time. Configurable **delay after joining** (default 3 s, 0–30). Texts are edited in the panel. Placeholders: `{PLAYER_NAME}` `{Room}` `{LAST_LOGIN_TIME}` `{TIME_SINCE_LAST_LOGIN}` `{PLAY_TIME}`. |
| Room adjourn | `/d [reason]`: the host closes the room. |
| Host transfer | `/nexthost`: the host picks a player number and hands the host role over. |
| Titles | A custom title next to the player name; titles and players are managed in the panel. Commands: `/title help`; `/title set 1 true/false` turns titles on or off; `/title set 2 true/false` wraps the title in `[]`; `/title set 3 left/right` puts it before or after the name. |
| Auto start | `/auto on/off`: start automatically when the room is full. `/auto starttime <1–60>` sets the countdown once full; `/auto coldtime <0–300>` sets the cool-down after a round; `/auto help`. |
| Room monitor (QQ groups) | New and full rooms are pushed to QQ groups. `/m on/off` lets the host switch this room's broadcast; `/m [note]` pushes a room report now. In the group: `#在线状态` (customisable), `#订阅` / `#退订` / `#帮助`. |

Type `/help` in game to list every command.

Improvements to these features in this release:

- **Welcome**: new default text, plus a new **delay after joining** and **first-time player message** (leave it empty to share the normal list).
- Fixed the last-login time being overwritten before the welcome was built, so returning players always saw "this session" as their last login.
- Fixed an empty player name in the welcome. The in-game name is not synced yet when the body first spawns, so it is now read after the delay, falling back to the account name.
- Default texts using the old `{Name}` placeholder are migrated to the new default automatically.
- The host hint (the `/m off` explanation), which used to be hard-coded, is now part of the default welcome text and is no longer sent twice.

---

#### 🪪 Friend codes in the lobby social panel

- The social panel in the lobby (the phone button at the top right) now shows every player's friend code, as on the official servers, for vanilla and modded clients alike.
- Cause: the player info objects the server creates always carried an empty friend code and PUID. They now carry the real code obtained at login.
- Placeholder codes, including the `failauth#xxxx` generated when the lookup fails, are no longer sent to clients, so players never try to add a fake code. The panel still shows them.
- A code the host claims is only used when the server has none of its own; it never overrides the server's value.

#### 📶 Real latency in the panel (values below 50 ms now show)

- The old value came from Hazel's smoothed ping, which is clamped internally by `Math.Max(50, …)`, so it never showed less than 50 ms.
- Every connection now gets a Hazel Ping packet every 2 s, and the server times its acknowledgement exactly, with no floor. If probing is unavailable the server falls back to ICMP, then to Hazel.

#### 🖥 Other

- Startup panel redone: drawn in one go (no more ~12 s of line-by-line waiting), with version, port, public IP and config status read from the configuration, and correct alignment for Chinese text.
- Fixed JSONL format misdetection in the player activity log, a regression from the 650 performance work. Mixed-format files are now read correctly.
- Panel restyled (same class names, no functional change).
- Global version string updated to Turbo-700.0-20261024.

---

Older versions: see `Turbo650更新日志.txt` and `Turbo640更新日志.txt`. 更早版本见对应的更新日志文件。
