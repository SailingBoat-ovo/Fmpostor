using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Events.Client;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Events.Meeting;
using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class GameEventHandler : IEventListener
{
    private readonly ILogger<GameEventHandler> _logger;
    private readonly BanService _banService;
    private readonly ChatService _chatService;
    private readonly ConnectionLogger _connLogger;
    private readonly PlayerIdentityService _identityService;
    private readonly BadWordFilterService _filter;
    private readonly WelcomeService _welcome;
    private readonly ReportService _reportService;
    private readonly PlayerLogService _playerLogs;
    private readonly PlayerStatsService _playerStats;
    private readonly ReactorModService _reactorMods;
    private readonly RoomCleanupService _roomCleanup;
    private readonly AiService _aiService;
    private readonly PlayerTimeService _playerTimes;
    private readonly TitleService _titleService;
    private readonly AutoStartService _autoStartService;
    private readonly RoomMonitorService _roomMonitorService;
    private readonly RoomTransferService _roomTransferService;
    private readonly BroadcastService _broadcast;
    private readonly AdminStatsService _adminStats;

    // Per-player limiter for /aichat (5 requests / minute).
    private static readonly FixedWindowRateLimiter _aiChatLimiter = new(5, TimeSpan.FromMinutes(1));

    private const string HelpText =
        "帆船服务端可用命令：\n" +
        "/report - 举报违规玩家（交互式选择被举报人）\n" +
        "/kick <玩家名> - 房主踢出玩家\n" +
        "/ban <玩家名> - 房主封禁玩家\n" +
        "/aichat <内容> - 与 AI 助手聊天（需管理员开启）\n" +
        "/d [原因] - 房主解散房间\n" +
        "/nexthost - 房主转让房主给指定玩家\n" +
        "/title help - 称号系统（显示/设置称号）\n" +
        "/auto help - 满人自动开局（房主）\n" +
        "/m on|off - 房主开关QQ群房间广播\n" +
        "/m - 房主推送房间报告到全部启用的QQ群\n" +
        "/m <内容> - 推送房间报告并在本房间下附一次性备注（下次发送不保留）\n" +
        "/help - 显示本帮助";

    // ========== 交互式举报会话（Turbo-640 重做，替代旧 /report <描述> 一段式） ==========
    //
    // 流程：/report → 私聊收到本房间玩家列表（每页 5 人，全局编号）→
    // 输入编号选择被举报人 → 显示其 ID/名字/好友代码 → 输入 0 确认（1 退出）→
    // 直接输入举报内容（无需前缀）→ 提交成功。翻页：上一页 / 下一页；退出：退出举报。
    private const int ReportPageSize = 5;
    private static readonly TimeSpan ReportSessionTimeout = TimeSpan.FromMinutes(10);

    private sealed class ReportSession
    {
        public IGame Game { get; set; } = null!;
        public int Page { get; set; }
        public IClientPlayer? Selected { get; set; }
        public bool Confirmed { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReportSession> _reportSessions = new();

    public GameEventHandler(
        ILogger<GameEventHandler> logger,
        BanService banService,
        ChatService chatService,
        ConnectionLogger connLogger,
        PlayerIdentityService identityService,
        BadWordFilterService filter,
        WelcomeService welcome,
        ReportService reportService,
        PlayerLogService playerLogs,
        PlayerStatsService playerStats,
        ReactorModService reactorMods,
        RoomCleanupService roomCleanup,
        AiService aiService,
        PlayerTimeService playerTimes,
        TitleService titleService,
        AutoStartService autoStartService,
        RoomMonitorService roomMonitorService,
        RoomTransferService roomTransferService,
        BroadcastService broadcastService,
        AdminStatsService adminStats)
    {
        _logger = logger;
        _banService = banService;
        _chatService = chatService;
        _connLogger = connLogger;
        _identityService = identityService;
        _filter = filter;
        _welcome = welcome;
        _reportService = reportService;
        _playerLogs = playerLogs;
        _playerStats = playerStats;
        _reactorMods = reactorMods;
        _roomCleanup = roomCleanup;
        _aiService = aiService;
        _playerTimes = playerTimes;
        _titleService = titleService;
        _autoStartService = autoStartService;
        _roomMonitorService = roomMonitorService;
        _roomTransferService = roomTransferService;
        _broadcast = broadcastService;
        _adminStats = adminStats;
    }

    // ========== Reactor mod handshake ==========

    [EventListener]
    public void OnClientConnection(IClientConnectionEvent e)
    {
        _reactorMods.ParseAndStore(e);
    }

    [EventListener]
    public void OnClientConnected(IClientConnectedEvent e)
    {
        _reactorMods.AttachToClient(e.Client);
    }

    // ========== Game lifecycle ==========

    [EventListener]
    public void OnGameCreated(IGameCreatedEvent e)
    {
        _logger.LogInformation("[WebAdmin] Game {Code} created.", e.Game.Code);
        _playerLogs.Add("game", "", "", "", e.Game.Code.Code, "Game created");
        _roomCleanup.TrackCreated(e.Game);
    }

    [EventListener]
    public void OnGameDestroyed(IGameDestroyedEvent e)
    {
        _logger.LogInformation("[WebAdmin] Game {Code} destroyed.", e.Game.Code);
        _playerLogs.Add("game", "", "", "", e.Game.Code.Code, "Game destroyed");
        _roomCleanup.Untrack(e.Game.Code);
        CleanupReportSessions(e.Game.Code.Code);
    }

    [EventListener]
    public void OnGameStarted(IGameStartedEvent e)
    {
        _playerLogs.Add("game", "", "", "", e.Game.Code.Code, "Game started");
    }

    [EventListener]
    public void OnGameEnded(IGameEndedEvent e)
    {
        var reason = e.GameOverReason.ToString();
        var crewmateWin = reason.StartsWith("Crewmates", StringComparison.Ordinal);
        _playerLogs.Add("game", "", "", "", e.Game.Code.Code, $"Game ended: {reason}");

        // Player stats: only count real wins/losses, not disconnects.
        if (reason == "ImpostorDisconnect" || reason == "CrewmateDisconnect")
        {
            return;
        }

        foreach (var player in e.Game.Players)
        {
            var fc = player.Client.FriendCode;
            if (string.IsNullOrEmpty(fc))
            {
                continue;
            }

            var wasImpostor = player.Character?.PlayerInfo?.IsImpostor ?? false;
            var name = player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name;
            _playerStats.RecordGameEnd(fc, name, crewmateWin, wasImpostor);
        }
    }

    // ========== Player join/leave ==========

    [EventListener]
    public async ValueTask OnGamePlayerJoining(IGamePlayerJoiningEvent e)
    {
        var player = e.Player;
        var client = player.Client;
        var ip = client.Connection?.EndPoint?.Address?.ToString();
        var playerName = player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name;

        // Backfill identity from the HTTP /api/user exchange (by PUID or IP), or generate
        // a deterministic fallback FriendCode so the panel always has something to show.
        var puid = client.Puid;
        if (string.IsNullOrEmpty(puid))
        {
            var byIp = _identityService.Lookup(client.Connection?.EndPoint?.Address);
            if (byIp != null)
            {
                puid = byIp.Puid;
                client.Puid = puid;
                client.ProductUserId = puid;
                client.Items["Puid"] = puid;
                client.Items["ProductUserId"] = puid;
            }
        }

        if (string.IsNullOrEmpty(client.FriendCode) || PlayerIdentityService.IsPlaceholderFriendCode(client.FriendCode))
        {
            var identity = _identityService.LookupByPuid(puid);
            if (identity == null)
            {
                identity = _identityService.Lookup(client.Connection?.EndPoint?.Address);
            }

            string? friendCode = null;
            if (identity != null
                && !string.IsNullOrEmpty(identity.FriendCode)
                && !PlayerIdentityService.IsPlaceholderFriendCode(identity.FriendCode))
            {
                friendCode = identity.FriendCode;
                client.Puid = identity.Puid;
                client.ProductUserId = identity.Puid;
                client.Items["Puid"] = identity.Puid;
                client.Items["ProductUserId"] = identity.Puid;
                client.Items["Fid"] = identity.Fid;
            }
            else if (!string.IsNullOrEmpty(puid))
            {
                friendCode = PlayerIdentityService.GenerateFriendCode(puid);
                client.Items["Fid"] = PlayerIdentityService.GenerateFid(puid);
            }

            if (!string.IsNullOrEmpty(friendCode))
            {
                client.FriendCode = friendCode;
                client.Items["FriendCode"] = friendCode;
                _logger.LogInformation("[WebAdmin] FriendCode for {Name}: {FC}", playerName, friendCode);
            }
        }

        var fid = client.Items.TryGetValue("Fid", out var fidObj) ? fidObj?.ToString() : null;

        var ban = await _banService.FindBanAsync(ip, playerName, puid, fid, client.FriendCode);
        if (ban != null)
        {
            var reason = ban.Reason ?? "No reason provided";
            _logger.LogInformation("[WebAdmin] Blocked banned player {PlayerName} (IP: {Ip}) from joining game. Reason: {Reason}",
                playerName, ip ?? "unknown", reason);
            _connLogger.Log("error", playerName, ip ?? "?", e.Game.Code.ToString(), "Blocked banned: " + reason);
            _playerLogs.Add("ban", playerName, client.FriendCode ?? "", puid ?? "", e.Game.Code.Code, "Blocked banned: " + reason);
            e.JoinResult = GameJoinResult.CreateCustomError($"You are banned from this server. Reason: {reason}");
        }
    }

    [EventListener]
    public async ValueTask OnGamePlayerJoined(IGamePlayerJoinedEvent e)
    {
        var playerName = e.Player.Character?.PlayerInfo?.PlayerName ?? e.Player.Client.Name;
        var ip = e.Player.Client.Connection?.EndPoint?.Address?.ToString();
        _logger.LogInformation("[WebAdmin] Player {Name} joined game {Code}.", playerName, e.Game.Code);
        _connLogger.Log("connect", playerName, ip ?? "?", e.Game.Code.ToString());
        _playerLogs.Add("join", playerName, e.Player.Client.FriendCode ?? "", e.Player.Client.Puid ?? "", e.Game.Code.Code, "");
    }

    /// <summary>
    ///     Welcome message is sent when the player's character has actually spawned
    ///     (the Character/PlayerControl is guaranteed to exist at this point).
    ///     Only in the lobby (NotStarted), mirroring the reference implementation.
    /// </summary>
    [EventListener]
    public async ValueTask OnPlayerSpawned(IPlayerSpawnedEvent e)
    {
        if (e.Game.GameState != GameStates.NotStarted)
        {
            return;
        }

        var playerName = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        var playerData = _playerTimes.GetPlayerData(e.ClientPlayer);
        var welcome = _welcome.Pick(playerName, e.Game.Code.Code, playerData);
        if (welcome != null)
        {
            await e.PlayerControl.SendChatToPlayerAsync(welcome, e.PlayerControl);
            _logger.LogInformation("[Welcome] Sent welcome to {Name} in {Code}: {Msg}", playerName, e.Game.Code.Code, welcome);
        }
    }

    [EventListener]
    public void OnGamePlayerLeft(IGamePlayerLeftEvent e)
    {
        var playerName = e.Player.Character?.PlayerInfo?.PlayerName ?? e.Player.Client.Name;
        var ip = e.Player.Client.Connection?.EndPoint?.Address?.ToString();
        _logger.LogInformation("[WebAdmin] Player {Name} left game {Code}.", playerName, e.Game.Code);
        _connLogger.Log("disconnect", playerName, ip ?? "?", e.Game.Code.ToString(), e.IsBan ? "Banned" : "");
        _playerLogs.Add("leave", playerName, e.Player.Client.FriendCode ?? "", e.Player.Client.Puid ?? "", e.Game.Code.Code, e.IsBan ? "Banned" : "Left");

        // The leaving player's interactive report session dies with them.
        var leftKey = e.Player.Client.FriendCode ?? e.Player.Client.Puid ?? ip ?? "";
        if (leftKey.Length > 0)
        {
            _reportSessions.TryRemove(leftKey, out _);
        }
    }

    // ========== Chat: filter + commands + log ==========

    [EventListener]
    public async ValueTask OnGamePlayerChat(IPlayerChatEvent e)
    {
        var playerName = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        var message = e.Message ?? string.Empty;
        var trimmed = message.Trim();
        var playerKey = e.ClientPlayer.Client.FriendCode ?? e.ClientPlayer.Client.Puid ?? e.ClientPlayer.Client.Connection?.EndPoint?.Address?.ToString() ?? "";

        // Server-originated announcements (sent via the host character by the
        // BroadcastService) must bypass command/filter processing — they are only
        // recorded, otherwise a banned word inside an announcement would cancel it.
        // A player typing "[公告]" is NOT a server announcement: verify against the
        // broadcast registry so the prefix cannot be used to dodge the word filter
        // or spoof server messages in the logs.
        if (message.StartsWith("[公告]", StringComparison.Ordinal)
            && _broadcast.MatchesRecentServerBroadcast(e.Game.Code.Code, message))
        {
            await _chatService.SavePlayerChatAsync(e.Game.Code.Code, "[服务器]", message);
            _playerLogs.Add("chat", "[服务器]", "", "", e.Game.Code.Code, message);
            return;
        }

        // Interactive report flow (Turbo-640): an active session intercepts all
        // subsequent chat input from this player until it ends.
        if (_reportSessions.TryGetValue(playerKey, out var reportSession))
        {
            if (reportSession.Game.Code.Code == e.Game.Code.Code)
            {
                e.IsCancelled = true;
                await HandleReportSessionInputAsync(e, reportSession, trimmed, playerName, playerKey);
                return;
            }

            // Player switched rooms mid-flow: drop the stale session and let the
            // message be processed as a normal chat.
            _reportSessions.TryRemove(playerKey, out _);
        }

        // /report starts the interactive report flow.
        if (trimmed.StartsWith("/report", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == "/report".Length || trimmed["/report".Length] == ' '))
        {
            e.IsCancelled = true;
            if (trimmed.Equals("/report help", StringComparison.OrdinalIgnoreCase))
            {
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync(
                        "交互式举报：输入 /report 后按提示选择被举报玩家（编号），确认后直接输入举报内容。",
                        e.ClientPlayer.Character);
                }

                return;
            }

            await StartReportSessionAsync(e, playerName, playerKey);
            return;
        }

        // Host management commands.
        if (trimmed.StartsWith("/kick", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("/ban", StringComparison.OrdinalIgnoreCase))
        {
            e.IsCancelled = true;

            if (!e.ClientPlayer.IsHost)
            {
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync("仅房主可以使用 /kick 和 /ban 命令。", e.ClientPlayer.Character);
                }

                return;
            }

            var isBan = trimmed.StartsWith("/ban", StringComparison.OrdinalIgnoreCase);
            var targetName = trimmed[isBan ? "/ban".Length.. : "/kick".Length..].Trim();

            if (string.IsNullOrEmpty(targetName) || targetName.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync(
                        isBan ? "用法: /ban 玩家名 （将该玩家移出本房间并禁止其再次加入本房间，其他房间不受影响）" : "用法: /kick 玩家名 （将该玩家移出房间）",
                        e.ClientPlayer.Character);
                }

                return;
            }

            var target = e.Game.Players.FirstOrDefault(p =>
                (p.Character?.PlayerInfo?.PlayerName?.Equals(targetName, StringComparison.OrdinalIgnoreCase) == true) ||
                p.Client.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync($"未找到玩家 \"{targetName}\"。", e.ClientPlayer.Character);
                }

                return;
            }

            var targetNameFull = target.Character?.PlayerInfo?.PlayerName ?? target.Client.Name;
            if (isBan)
            {
                // 仅原版房间级封禁（BanAsync）：只对当前房间生效，玩家仍可进入
                // 其他房间；房间关闭/同房主重开后封禁自动失效。绝不写服务器级
                // 封禁库 —— 房主的 /ban 不应该影响玩家在全服的进入资格。
                await target.BanAsync();
                _playerLogs.Add("ban", targetNameFull, target.Client.FriendCode ?? "", target.Client.Puid ?? "", e.Game.Code.Code, $"Room-banned by host {playerName}");
                _logger.LogInformation("[WebAdmin] Host {Host} room-banned {Target} in game {Code}.", playerName, targetNameFull, e.Game.Code.Code);
            }
            else
            {
                await target.KickAsync();
                _playerLogs.Add("kick", targetNameFull, target.Client.FriendCode ?? "", target.Client.Puid ?? "", e.Game.Code.Code, $"Kicked by host {playerName}");
                _logger.LogInformation("[WebAdmin] Host {Host} kicked {Target} in game {Code}.", playerName, targetNameFull, e.Game.Code.Code);
            }

            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync(
                    $"已{(isBan ? "封禁" : "踢出")}玩家 {targetNameFull}。", e.ClientPlayer.Character);
            }

            return;
        }

        // /d [原因]: host disbands the room (Fanchuan.RoomAdjourned.Plugin).
        if (trimmed.Equals("/d", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("/d ", StringComparison.OrdinalIgnoreCase))
        {
            if (!e.ClientPlayer.IsHost)
            {
                return;
            }

            e.IsCancelled = true;

            var reason = trimmed.Length > 2 ? trimmed[2..].Trim() : "房主妈妈叫房主吃饭啦（默认）";
            if (string.IsNullOrEmpty(reason))
            {
                reason = "房主妈妈叫房主吃饭啦（默认）";
            }

            var disconnectMessage = $"该房间被房主解散 原因：\n{reason}";
            _playerLogs.Add("game", playerName, e.ClientPlayer.Client.FriendCode ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, $"Host disbanded room: {reason}");

            foreach (var player in e.Game.Players)
            {
                try
                {
                    await player.Client.DisconnectAsync(DisconnectReason.Custom, disconnectMessage);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[WebAdmin] Failed to disconnect {Name} during room disband.", player.Client.Name);
                }
            }

            return;
        }

        // /nexthost: host transfer (Fanchuan.RoomOwnerConversion.Plugin).
        if (trimmed.Equals("/nexthost", StringComparison.OrdinalIgnoreCase))
        {
            await _roomTransferService.HandleChat(e, trimmed);
            return;
        }

        // Step 2 of /nexthost: the host enters the target player ID as a bare number.
        if (int.TryParse(trimmed, out _))
        {
            await _roomTransferService.HandleChat(e, trimmed);
            return;
        }

        // /title: player title system (PlayerNamePlugin).
        if (trimmed.StartsWith("/title", StringComparison.OrdinalIgnoreCase))
        {
            e.IsCancelled = true;
            await _titleService.HandleChat(e, trimmed);
            return;
        }

        // /auto: full-room auto-start (fanchuan.autostart.plugin).
        if (trimmed.StartsWith("/auto", StringComparison.OrdinalIgnoreCase))
        {
            await _autoStartService.HandleChat(e, trimmed);
            return;
        }

        // /m on|off: handled by RoomMonitorService itself (QQ broadcast toggle).

        // /help: list all available commands.
        if (trimmed.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            e.IsCancelled = true;
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync(HelpText, e.ClientPlayer.Character);
            }

            return;
        }

        // /aichat: private conversation with the AI (admin-controlled toggle).
        // Runs asynchronously so a slow AI response never blocks the player's
        // connection (which used to cause timeouts / disconnects).
        if (trimmed.StartsWith("/aichat", StringComparison.OrdinalIgnoreCase))
        {
            e.IsCancelled = true;

            var content = trimmed["/aichat".Length..].Trim();
            // Cap the prompt length: game clients can send long chat payloads and
            // each /aichat hits the external AI provider.
            if (content.Length > 500)
            {
                content = content[..500];
            }

            // Per-player rate limit (admin-configurable: N rounds per M seconds,
            // sliding window in AiService) so one client cannot spam the AI endpoint.
            var rateMsg = _aiService.CheckAiRateLimit("aichat:" + playerKey, inGame: true);
            if (rateMsg != null)
            {
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync("🤖 " + rateMsg, e.ClientPlayer.Character);
                }

                return;
            }

            var friendCode = e.ClientPlayer.Client.FriendCode ?? "";
            var puid = e.ClientPlayer.Client.Puid ?? "";
            var gameCode = e.Game.Code.Code;
            var playerNames = e.Game.Players
                .Select(p => p.Character?.PlayerInfo?.PlayerName ?? p.Client.Name)
                .ToList();
            var character = e.ClientPlayer.Character;

            // Instant confirmation so the player knows the request was received.
            if (character != null)
            {
                await character.SendChatToPlayerAsync("🤖 AI 思考中，请稍候...", character);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var (ok, reply, error) = await _aiService.GameChatAsync(
                        playerKey, playerName, friendCode, puid, gameCode, playerNames, content);

                    _playerLogs.Add("ai_chat", playerName, friendCode, puid, gameCode, content);

                    if (character != null)
                    {
                        var text = ok ? reply : (error ?? "AI 暂时无法回复，请稍后再试。");
                        await character.SendChatToPlayerAsync(text, character);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AI] Async in-game chat failed for {Name}.", playerName);
                }
            });

            return;
        }

        // Banned word filter: cancel the message and send a private tip.
        if (_filter.Check(message, playerKey, out var hitWord, out var tip))
        {
            e.IsCancelled = true;
            _adminStats.RecordFilterHit(
                e.ClientPlayer.Client.FriendCode ?? playerKey,
                playerName,
                hitWord ?? "");
            _logger.LogInformation("[Filter] Blocked message from {Name} ({Fc}) in {Code}: {Msg}",
                playerName, e.ClientPlayer.Client.FriendCode, e.Game.Code.Code, message);

            if (e.ClientPlayer.Character != null && !string.IsNullOrEmpty(tip))
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync(tip, e.ClientPlayer.Character);
            }

            return;
        }

        await _chatService.SavePlayerChatAsync(e.Game.Code.Code, playerName, message);
        _playerLogs.Add("chat", playerName, e.ClientPlayer.Client.FriendCode ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, message);
        _logger.LogDebug("[WebAdmin] Chat from {Name} in {Code}: {Msg}",
            playerName, e.Game.Code, e.Message);
    }

    // ========== Interactive report session (Turbo-640) ==========

    private static string ReportPlayerName(IClientPlayer p)
    {
        return p.Character?.PlayerInfo?.PlayerName ?? p.Client?.Name ?? "?";
    }

    private static List<IClientPlayer> GetReportablePlayers(IGame game)
    {
        return game.Players
            .Where(p => p.Client != null && p.Client.Connection?.IsConnected != false)
            .ToList();
    }

    private async Task StartReportSessionAsync(IPlayerChatEvent e, string playerName, string playerKey)
    {
        var players = GetReportablePlayers(e.Game);
        if (players.Count == 0)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("当前房间没有可举报的玩家。", e.ClientPlayer.Character);
            }

            return;
        }

        var session = new ReportSession { Game = e.Game, Page = 0 };
        _reportSessions[playerKey] = session;
        _logger.LogInformation("[Report] Session started for {Name} ({Key}) in {Code}.", playerName, playerKey, e.Game.Code);
        await SendReportListPageAsync(e, session, "请选择你要举报的玩家：");
    }

    private async Task SendReportListPageAsync(IPlayerChatEvent e, ReportSession session, string header)
    {
        var players = GetReportablePlayers(session.Game);
        var pageCount = Math.Max(1, (int)Math.Ceiling(players.Count / (double)ReportPageSize));
        if (session.Page >= pageCount)
        {
            session.Page = pageCount - 1;
        }

        if (session.Page < 0)
        {
            session.Page = 0;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine("── 玩家列表 第 " + (session.Page + 1) + "/" + pageCount + " 页 ──");
        foreach (var p in players.Skip(session.Page * ReportPageSize).Take(ReportPageSize))
        {
            var idx = players.IndexOf(p) + 1;
            sb.AppendLine(idx + ". " + ReportPlayerName(p));
        }

        sb.AppendLine("输入编号选择要举报的玩家；上一页 / 下一页 翻页；退出举报 结束。");
        if (e.ClientPlayer.Character != null)
        {
            await e.ClientPlayer.Character.SendChatToPlayerAsync(sb.ToString(), e.ClientPlayer.Character);
        }
    }

    private async Task HandleReportSessionInputAsync(IPlayerChatEvent e, ReportSession session, string trimmed, string playerName, string playerKey)
    {
        var idle = DateTime.UtcNow - session.LastActivity;
        session.LastActivity = DateTime.UtcNow;
        var character = e.ClientPlayer.Character;

        async Task SendAsync(string msg)
        {
            if (character != null)
            {
                await character.SendChatToPlayerAsync(msg, character);
            }
        }

        // 过期清理：长时间无操作自动结束会话
        if (idle >= ReportSessionTimeout)
        {
            _reportSessions.TryRemove(playerKey, out _);
            await SendAsync("举报会话已超时结束，如需举报请重新输入 /report。");
            return;
        }

        // 房间已结束/销毁：结束会话
        if (session.Game.GameState is GameStates.Ended or GameStates.Destroyed)
        {
            _reportSessions.TryRemove(playerKey, out _);
            await SendAsync("房间已结束，举报会话已退出。");
            return;
        }

        var selected = session.Selected;

        // 阶段一：选择被举报人（列表）
        if (selected == null)
        {
            if (trimmed == "上一页")
            {
                session.Page--;
                await SendReportListPageAsync(e, session, "请选择你要举报的玩家：");
                return;
            }

            if (trimmed == "下一页")
            {
                session.Page++;
                await SendReportListPageAsync(e, session, "请选择你要举报的玩家：");
                return;
            }

            if (trimmed == "退出举报")
            {
                _reportSessions.TryRemove(playerKey, out _);
                await SendAsync("已退出举报。");
                return;
            }

            if (int.TryParse(trimmed, out var num))
            {
                var players = GetReportablePlayers(session.Game);
                var target = num >= 1 && num <= players.Count ? players[num - 1] : null;
                if (target == null)
                {
                    await SendAsync("没有编号 " + num + "，请输入当前列表中的编号。");
                    return;
                }

                session.Selected = target;
                var targetName = ReportPlayerName(target);
                var targetFc = target.Client?.FriendCode ?? "";
                await SendAsync(
                    "您将举报：" + targetName + "（编号 " + num +
                    (string.IsNullOrEmpty(targetFc) ? "）" : "，好友代码 " + targetFc + "）") +
                    "\n输入 0 确认举报，输入 1 退出。");
                return;
            }

            await SendAsync("无法识别的输入。输入列表中的编号选择玩家；上一页 / 下一页 翻页；退出举报 结束。");
            return;
        }

        var selName = ReportPlayerName(selected);
        var selFc = selected.Client?.FriendCode ?? "";

        // 阶段二：确认（0=确认举报，1=退出）
        if (!session.Confirmed)
        {
            if (trimmed == "0")
            {
                session.Confirmed = true;
                await SendAsync("已选择举报 " + selName + "。请直接输入举报内容（无需任何指令前缀），发送后即提交。");
                return;
            }

            if (trimmed == "1" || trimmed == "退出举报")
            {
                _reportSessions.TryRemove(playerKey, out _);
                await SendAsync("已退出举报。");
                return;
            }

            await SendAsync("请输入 0 确认举报 " + selName + "，或输入 1 退出。");
            return;
        }

        // 阶段三：输入举报内容（无需前缀，发送即提交）
        var description = trimmed.Length > 200 ? trimmed[..200] : trimmed;
        await _reportService.AddAsync(new ReportEntry
        {
            ReporterName = playerName,
            ReporterFriendCode = e.ClientPlayer.Client.FriendCode ?? "",
            ReporterPuid = e.ClientPlayer.Client.Puid ?? "",
            ReporterIp = e.ClientPlayer.Client.Connection?.EndPoint?.Address?.ToString() ?? "",
            GameCode = e.Game.Code.Code,
            Description = description,
            ReportedPlayerName = selName,
            ReportedPlayerFriendCode = selFc,
            ReportedPlayerPuid = selected.Client?.Puid ?? "",
        });

        _playerLogs.Add("report", playerName, e.ClientPlayer.Client.FriendCode ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, "举报 " + selName + "(" + selFc + "): " + description);
        _reportSessions.TryRemove(playerKey, out _);
        _logger.LogInformation("[Report] Submitted by {Name} against {Target}({Fc}) in {Code}.",
            playerName, selName, selFc, e.Game.Code);
        await SendAsync("✅ 举报成功！管理员会在管理面板看到你的举报，感谢反馈。");
    }

    private void CleanupReportSessions(string gameCode)
    {
        foreach (var kv in _reportSessions)
        {
            if (kv.Value.Game.Code.Code == gameCode)
            {
                _reportSessions.TryRemove(kv.Key, out _);
            }
        }
    }

    // ========== Behavior log + stats: in-game actions ==========

    [EventListener]
    public void OnPlayerMurder(IPlayerMurderEvent e)
    {
        var killerName = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        var killerFc = e.ClientPlayer.Client.FriendCode;
        var victimName = e.Victim?.PlayerInfo?.PlayerName ?? "unknown";
        var victimFc = GetFriendCode(e.Game, e.Victim);

        _playerLogs.Add("murder", killerName, killerFc ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, $"Victim: {victimName} ({victimFc ?? "unknown"})");

        if (killerFc != null)
        {
            _playerStats.RecordKill(killerFc, killerName);
        }

        if (victimFc != null)
        {
            _playerStats.RecordDeath(victimFc, victimName);
        }
    }

    [EventListener]
    public void OnPlayerExile(IPlayerExileEvent e)
    {
        var name = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        var fc = e.ClientPlayer.Client.FriendCode;
        _playerLogs.Add("exile", name, fc ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, "Ejected");

        if (fc != null)
        {
            _playerStats.RecordExile(fc, name);
        }
    }

    [EventListener]
    public void OnPlayerCompletedTask(IPlayerCompletedTaskEvent e)
    {
        var name = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        var fc = e.ClientPlayer.Client.FriendCode;
        _playerLogs.Add("task", name, fc ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, null);

        if (fc != null)
        {
            _playerStats.RecordTaskCompleted(fc, name);
        }
    }

    [EventListener]
    public void OnPlayerVoted(IPlayerVotedEvent e)
    {
        var name = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        var target = e.VotedFor?.PlayerInfo?.PlayerName ?? (e.VoteType == VoteType.Skipped ? "skipped" : e.VoteType.ToString());
        _playerLogs.Add("vote", name, e.ClientPlayer.Client.FriendCode ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, $"Voted: {target}");
    }

    [EventListener]
    public void OnPlayerEnterVent(IPlayerEnterVentEvent e)
    {
        var name = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        _playerLogs.Add("vent", name, e.ClientPlayer.Client.FriendCode ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, "Enter vent");
    }

    [EventListener]
    public void OnPlayerExitVent(IPlayerExitVentEvent e)
    {
        var name = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        _playerLogs.Add("vent", name, e.ClientPlayer.Client.FriendCode ?? "", e.ClientPlayer.Client.Puid ?? "", e.Game.Code.Code, "Exit vent");
    }

    [EventListener]
    public void OnMeetingStarted(IMeetingStartedEvent e)
    {
        _playerLogs.Add("meeting", "", "", "", e.Game.Code.Code, "Meeting started");
    }

    [EventListener]
    public void OnMeetingEnded(IMeetingEndedEvent e)
    {
        var detail = e.IsTie ? "Meeting ended (tie)" : (e.Exiled?.PlayerInfo?.PlayerName != null ? $"Meeting ended, exiled: {e.Exiled.PlayerInfo.PlayerName}" : "Meeting ended");
        _playerLogs.Add("meeting", "", "", "", e.Game.Code.Code, detail);
    }

    private static string? GetFriendCode(IGame game, IInnerPlayerControl? target)
    {
        if (target == null)
        {
            return null;
        }

        var match = game.Players.FirstOrDefault(p => p.Character?.PlayerId == target.PlayerId);
        return match?.Client.FriendCode;
    }
}
