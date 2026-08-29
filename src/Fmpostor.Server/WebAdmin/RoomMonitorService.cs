using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Innersloth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class RoomMonitorSettings
{
    public bool Enabled { get; set; }
    public string OneBotUrl { get; set; } = "";   // e.g. http://127.0.0.1:3001 (HTTP API) or ws://... (WebSocket)
    public string OneBotToken { get; set; } = "";
    public List<long> AllowedGroups { get; set; } = new();
    public string ServerName { get; set; } = "帆船 AmongUs 服务器";
}

public class RoomMonitorStore
{
    public RoomMonitorSettings Settings { get; set; } = new();
}

/// <summary>
///     Room monitor / QQ broadcast (ported from Fanchuan.RoomMonitor.Plugin):
///     connects to a OneBot v11 (NapCat) endpoint, answers "#在线状态" in
///     allowed QQ groups with the room list, and lets hosts toggle per-room
///     broadcasting with /m on|off. Config persisted to webadmin_monitor.json.
/// </summary>
public class RoomMonitorService : IEventListener, IDisposable
{
    public static readonly object BroadcastEnabledKey = new();

    /// <summary>Per-room one-time remark (set by /m &lt;内容&gt;, rendered once in the next room report, then removed).</summary>
    public static readonly object RoomRemarkKey = new();

    private readonly ILogger<RoomMonitorService> _logger;
    private readonly IEventManager _eventManager;
    private readonly IGameManager _gameManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AdminStatsService _adminStats;
    private readonly string _filePath;
    private readonly string _subsFile;
    private readonly object _subsLock = new();
    private Dictionary<string, List<long>> _subs = new();
    private readonly object _lock = new();
    private RoomMonitorStore _store;
    private long _fileVersion = -1;
    private CancellationTokenSource? _cts;
    private IDisposable? _eventRegistration;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public RoomMonitorService(
        IOptions<WebAdminConfig> config,
        ILogger<RoomMonitorService> logger,
        IEventManager eventManager,
        IGameManager gameManager,
        IHttpClientFactory httpClientFactory,
        AdminStatsService adminStats)
    {
        _logger = logger;
        _eventManager = eventManager;
        _gameManager = gameManager;
        _httpClientFactory = httpClientFactory;
        _adminStats = adminStats;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.RoomMonitorFile);
        _subsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_subs.json");
        _store = new RoomMonitorStore();
        Load();
        LoadSubs();
    }

    public void Start()
    {
        if (_eventRegistration == null)
        {
            _eventRegistration = _eventManager.RegisterListener(this);
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
        _logger.LogInformation("[RoomMonitor] Started (enabled={Enabled}).", _store.Settings.Enabled);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _eventRegistration?.Dispose();
        _eventRegistration = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<RoomMonitorStore>(json, JsonOptions);
                if (data != null)
                {
                    _store = data;
                    _store.Settings ??= new RoomMonitorSettings();
                    _store.Settings.AllowedGroups ??= new List<long>();
                    _fileVersion = GetFileVersion();
                    _logger.LogInformation("[RoomMonitor] Loaded config from {File} (enabled={Enabled}).",
                        _filePath, _store.Settings.Enabled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RoomMonitor] Failed to load {File}.", _filePath);
            }
        }
    }

    private void TryReload()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        var version = GetFileVersion();
        if (version == _fileVersion)
        {
            return;
        }

        _fileVersion = version;
        Load();
    }

    private long GetFileVersion()
    {
        var fi = new FileInfo(_filePath);
        return (fi.LastWriteTimeUtc.Ticks << 8) ^ fi.Length;
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_store, JsonOptions);
                File.WriteAllText(_filePath, json);
                _fileVersion = GetFileVersion();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RoomMonitor] Failed to save {File}.", _filePath);
            }
        }
    }

    public RoomMonitorSettings GetSettings()
    {
        lock (_lock)
        {
            TryReload();
            return new RoomMonitorSettings
            {
                Enabled = _store.Settings.Enabled,
                OneBotUrl = _store.Settings.OneBotUrl,
                OneBotToken = _store.Settings.OneBotToken,
                AllowedGroups = _store.Settings.AllowedGroups.ToList(),
                ServerName = _store.Settings.ServerName,
            };
        }
    }

    public void UpdateSettings(bool? enabled, string? oneBotUrl, string? oneBotToken, List<long>? allowedGroups, string? serverName)
    {
        lock (_lock)
        {
            TryReload();
            if (enabled.HasValue)
            {
                _store.Settings.Enabled = enabled.Value;
            }

            if (oneBotUrl != null)
            {
                _store.Settings.OneBotUrl = oneBotUrl.Trim();
            }

            if (oneBotToken != null)
            {
                _store.Settings.OneBotToken = oneBotToken.Trim();
            }

            if (allowedGroups != null)
            {
                _store.Settings.AllowedGroups = allowedGroups.Distinct().ToList();
            }

            if (serverName != null)
            {
                _store.Settings.ServerName = serverName.Trim();
            }

            Save();
            _logger.LogInformation("[RoomMonitor] Settings updated (enabled={Enabled}).", _store.Settings.Enabled);

            // Restart the connection loop with the new config.
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = new CancellationTokenSource();
                _ = RunLoopAsync(_cts.Token);
            }
        }
    }

    // ========== OneBot loop ==========

    private async Task RunLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RoomMonitor] WebSocket connection lost, retrying in 10s...");
                try
                {
                    await Task.Delay(10000, cancellation);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken cancellation)
    {
        var settings = GetSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.OneBotUrl))
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
            return;
        }

        // Only WebSocket endpoints are supported for receiving messages.
        var wsUrl = settings.OneBotUrl
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        _logger.LogInformation("[RoomMonitor] Connecting to OneBot WebSocket: {Url}", wsUrl);

        using var ws = new ClientWebSocket();
        // Send the token as an Authorization header instead of an access_token query
        // parameter so it never lands in NapCat/proxy/server access logs.
        if (!string.IsNullOrWhiteSpace(settings.OneBotToken))
        {
            ws.Options.SetRequestHeader("Authorization", "Bearer " + settings.OneBotToken);
        }

        await ws.ConnectAsync(new Uri(wsUrl), cancellation);
        _logger.LogInformation("[RoomMonitor] OneBot WebSocket connected.");

        var buffer = new byte[1024 * 64];
        var messageBuffer = new StringBuilder();

        while (ws.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            }
            catch (WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                var json = messageBuffer.ToString();
                messageBuffer.Clear();
                await ProcessMessageAsync(json);
            }
        }
    }

    private async Task ProcessMessageAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var postType = root.TryGetProperty("post_type", out var pt) ? pt.GetString() : null;
            var msgType = root.TryGetProperty("message_type", out var mt) ? mt.GetString() : null;
            var groupId = root.TryGetProperty("group_id", out var gi) ? gi.GetInt64() : 0L;
            var rawMsg = root.TryGetProperty("raw_message", out var rm) ? rm.GetString() : null;

            if (postType == "meta_event" || postType == "notice")
            {
                return;
            }

            if (msgType != "group")
            {
                return;
            }

            var settings = GetSettings();
            if (!settings.AllowedGroups.Contains(groupId))
            {
                return;
            }

            string messageText = rawMsg ?? "";
            if (root.TryGetProperty("message", out var msg))
            {
                if (msg.ValueKind == JsonValueKind.String)
                {
                    messageText = msg.GetString() ?? "";
                }
                else if (msg.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var segment in msg.EnumerateArray())
                    {
                        if (segment.TryGetProperty("type", out var type) && type.GetString() == "text"
                            && segment.TryGetProperty("data", out var data) && data.TryGetProperty("text", out var text))
                        {
                            sb.Append(text.GetString());
                        }
                    }

                    messageText = sb.ToString();
                }
            }

            messageText = messageText.Trim();
            var userId = root.TryGetProperty("user_id", out var ui) ? ui.GetInt64() : 0L;

            if (messageText is "#帮助" or "#help")
            {
                await SendGroupMessageAsync(settings, groupId, QqHelpText);
                return;
            }

            if (messageText == "#在线状态")
            {
                _logger.LogInformation("[RoomMonitor] Received #在线状态 from group {GroupId} user {Qq}.", groupId, userId);
                var reply = BuildRoomStatusMessage(settings.ServerName, _gameManager.Games);
                await SendGroupMessageAsync(settings, groupId, reply);
                _adminStats.RecordBroadcast("qq", userId, groupId, "", "", "#在线状态");
                return;
            }

            if (messageText.StartsWith("#订阅", StringComparison.Ordinal))
            {
                await HandleSubscribeAsync(settings, groupId, userId, messageText["#订阅".Length..].Trim());
                return;
            }

            if (messageText.StartsWith("#退订", StringComparison.Ordinal))
            {
                await HandleUnsubscribeAsync(settings, groupId, userId, messageText["#退订".Length..].Trim());
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RoomMonitor] Failed to process WebSocket message.");
        }
    }

    private string BuildRoomStatusMessage(string serverName, IEnumerable<IGame> games)
    {
        var roomList = games.Where(IsBroadcastEnabled).ToList();
        var sb = new StringBuilder();
        sb.Append("📡 ").Append(serverName).AppendLine(" · 房间报告");

        if (roomList.Count == 0)
        {
            sb.AppendLine("▶ 当前没有活跃房间");
            return sb.ToString();
        }

        sb.Append("▶ 在线房间数: ").Append(roomList.Count).AppendLine();
        sb.AppendLine("───");

        var index = 1;
        foreach (var game in roomList)
        {
            sb.Append(" ◆ ").Append(game.Code.ToString()).AppendLine();
            sb.Append("   ").Append(game.PlayerCount).Append('/').Append(game.Options.MaxPlayers)
                .Append(" 人 · 房主: ").Append(game.Host?.Client?.Name ?? "未知").AppendLine();
            sb.Append("   ").Append(game.IsPublic ? "🔓 公开" : "🔒 私密")
                .Append(" · ").Append(FormatGameState(game.GameState)).AppendLine();
            // 一次性备注：/m <内容> 时由房主房间挂上，本次报告渲染后清除
            if (game.Items.TryGetValue(RoomRemarkKey, out var remarkObj) && remarkObj is string remark && remark.Length > 0)
            {
                sb.Append("   📝 ").AppendLine(remark);
            }

            game.Items.Remove(RoomRemarkKey);
            if (index < roomList.Count)
            {
                sb.AppendLine("───");
            }

            index++;
        }

        return sb.ToString();
    }

    private static string FormatGameState(GameStates state)
    {
        return state switch
        {
            GameStates.NotStarted => "等待中",
            GameStates.Starting or GameStates.Started => "游戏中",
            GameStates.Ended => "已结束",
            GameStates.Destroyed => "已关闭",
            _ => "未知",
        };
    }

    /// <summary>
    ///     Sends a message to every allowed QQ group (used by the panel's
    ///     scheduled-task engine). Returns the number of groups actually
    ///     delivered (HTTP success); 0 means the monitor is disabled, has no
    ///     allowed groups, or every send failed.
    /// </summary>
    public async Task<int> SendAnnouncementToGroupsAsync(string message)
    {
        RoomMonitorSettings settings;
        lock (_lock)
        {
            settings = _store.Settings;
        }
        if (settings == null || !settings.Enabled)
        {
            return 0;
        }
        var groups = settings.AllowedGroups ?? new List<long>();
        var delivered = 0;
        foreach (var groupId in groups)
        {
            if (await SendGroupMessageAsync(settings, groupId, message))
            {
                delivered++;
                _adminStats.RecordBroadcast("schedule", 0, groupId, "", "", message);
            }
        }
        return delivered;
    }

    /// <summary>Returns true when the OneBot HTTP send returned success.</summary>
    private async Task<bool> SendGroupMessageAsync(RoomMonitorSettings settings, long groupId, string message)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);

            var url = settings.OneBotUrl.TrimEnd('/') + "/send_group_msg";
            var payload = new { group_id = groupId, message };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            if (!string.IsNullOrWhiteSpace(settings.OneBotToken))
            {
                // Token via header, not URL query (keeps it out of access logs).
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.OneBotToken);
                response = await client.SendAsync(req);
            }
            else
            {
                response = await client.PostAsync(url, content);
            }

            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[RoomMonitor] send_group_msg failed [{Status}]: {Body}", response.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RoomMonitor] Failed to send group message.");
            return false;
        }
    }

    // ========== In-game ==========

    [EventListener]
    public ValueTask OnGameCreated(IGameCreatedEvent e)
    {
        var game = e.Game;
        game.Items[BroadcastEnabledKey] = true;

        _ = SendHostWelcomeAsync(game);
        return default;
    }

    [EventListener]
    public async ValueTask OnPlayerChat(IPlayerChatEvent e)
    {
        var message = e.Message.Trim();

        // /m：推送房间报告（代替群内 #在线状态）；
        // /m <内容>：推送房间报告，并在本房间条目下附带一次性备注（发送后清除）。
        if (message == "/m" || (message.StartsWith("/m ", StringComparison.Ordinal) && message != "/m on" && message != "/m off"))
        {
            if (!e.ClientPlayer.IsHost)
            {
                return;
            }

            e.IsCancelled = true;
            var senderName = e.ClientPlayer.Client.Name ?? "未知";
            var remark = message == "/m" ? "" : message[3..].Trim();
            if (remark.Length > 0)
            {
                // 一次性备注：挂在本房间上，本次报告渲染后即清除
                e.Game.Items[RoomRemarkKey] = remark;
            }

            var push = BuildRoomStatusMessage(GetSettings().ServerName, _gameManager.Games);
            var groups = GetSettings().AllowedGroups ?? new List<long>();
            var delivered = 0;
            var statContent = remark.Length > 0 ? "备注：" + remark : "房间报告";
            foreach (var gid in groups)
            {
                if (await SendGroupMessageAsync(GetSettings(), gid, push))
                {
                    delivered++;
                    _adminStats.RecordBroadcast("game", 0, gid,
                        e.ClientPlayer.Client.FriendCode ?? "", senderName, statContent);
                }
            }

            if (e.PlayerControl != null)
            {
                await e.PlayerControl.SendChatToPlayerAsync(
                    delivered > 0 ? $"已推送到 {delivered} 个QQ群。" : "推送失败：QQ 广播未启用或没有可用群。",
                    e.PlayerControl);
            }

            _logger.LogInformation("[RoomMonitor] Host {Host} pushed /m to {Count} groups in {Code}.",
                senderName, delivered, e.Game.Code);
            return;
        }

        if (message != "/m off" && message != "/m on")
        {
            return;
        }

        if (!e.ClientPlayer.IsHost)
        {
            return;
        }

        var game = e.Game;
        var isOn = message == "/m on";
        game.Items[BroadcastEnabledKey] = isOn;
        e.IsCancelled = true;

        if (e.PlayerControl != null)
        {
            await e.PlayerControl.SendChatToPlayerAsync(
                isOn
                    ? "您已开启广播，您的房间将会被BOT公开到QQ群。如需关闭请输入/m off"
                    : "您已关闭广播，您的房间不会被BOT公开到QQ群。如需重新开启请输入/m on",
                e.PlayerControl);
        }

        _logger.LogInformation("[RoomMonitor] Host {Host} {Action} broadcast in {Code}.",
            e.ClientPlayer.Client.Name, isOn ? "enabled" : "disabled", game.Code);
    }

    public static bool IsBroadcastEnabled(IGame game)
    {
        return game.Items.TryGetValue(BroadcastEnabledKey, out var val) ? val is true : true;
    }

    private async Task SendHostWelcomeAsync(IGame game)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));

            if (game.GameState == GameStates.Destroyed)
            {
                return;
            }

            var host = game.Host;
            if (host?.Character == null)
            {
                return;
            }

            const string message = "欢迎加入由帆船服务端驱动的服务器，如果您想使您的房间不在QQ群内被BOT广播，您可以输入：/m off 以关闭此功能，不需要关闭可以忽略此消息！";
            await host.Character.SendChatToPlayerAsync(message, host.Character);
            _logger.LogInformation("[RoomMonitor] Sent host welcome to {Code}.", game.Code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoomMonitor] Host welcome failed for {Code}.", game.Code);
        }
    }

    // ========== 订阅功能 ==========

    public const string QqHelpText =
        "📖 QQ群命令帮助\n" +
        "#在线状态 - 查看服务器房间列表\n" +
        "#订阅 <房间号> - 订阅房间，游戏结束后私信通知您\n" +
        "#退订 <房间号> - 取消订阅\n" +
        "#帮助 - 显示本帮助\n" +
        "（房主可在游戏内 /m 推送房间报告，/m <内容> 附一次性备注）";

    private void LoadSubs()
    {
        try
        {
            if (File.Exists(_subsFile))
            {
                var json = File.ReadAllText(_subsFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, List<long>>>(json, JsonOptions);
                if (data != null)
                {
                    _subs = data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoomMonitor] Failed to load subscriptions.");
        }
    }

    private void SaveSubs()
    {
        try
        {
            File.WriteAllText(_subsFile, JsonSerializer.Serialize(_subs, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoomMonitor] Failed to save subscriptions.");
        }
    }

    private async Task HandleSubscribeAsync(RoomMonitorSettings settings, long groupId, long qq, string roomCode)
    {
        roomCode = roomCode.ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(roomCode, "^[A-Z]{4,10}$"))
        {
            await SendGroupMessageAsync(settings, groupId, "❌ 房间号格式不正确，应为 4-10 位大写字母（如 ABCDEF）。用法：#订阅 ABCDEF");
            return;
        }

        lock (_subsLock)
        {
            if (!_subs.TryGetValue(roomCode, out var list))
            {
                list = new List<long>();
                _subs[roomCode] = list;
            }

            if (!list.Contains(qq))
            {
                list.Add(qq);
                SaveSubs();
            }
        }

        _adminStats.RecordBroadcast("qq", qq, groupId, "", "", "#订阅 " + roomCode);
        _logger.LogInformation("[RoomMonitor] QQ {Qq} subscribed to {Code} in group {GroupId}.", qq, roomCode, groupId);
        await SendGroupMessageAsync(settings, groupId,
            "✅ 订阅成功！房间 " + roomCode + " 的游戏结束后，BOT 将私信通知您。\n" +
            "📌 订阅功能：在群里输入 #订阅 房间号，该房间结束游戏时会私信提醒您。\n" +
            "⚠️ 注意：如果本群设置了“禁止群成员发起临时会话”，您将收不到私信通知；" +
            "可先主动私聊 BOT 一次建立会话，或联系群主允许临时会话。取消订阅请输入 #退订 " + roomCode);
    }

    private async Task HandleUnsubscribeAsync(RoomMonitorSettings settings, long groupId, long qq, string roomCode)
    {
        roomCode = roomCode.ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(roomCode, "^[A-Z]{4,10}$"))
        {
            await SendGroupMessageAsync(settings, groupId, "❌ 房间号格式不正确。用法：#退订 ABCDEF");
            return;
        }

        bool removed;
        lock (_subsLock)
        {
            removed = _subs.TryGetValue(roomCode, out var list) && list.Remove(qq);
            if (removed)
            {
                if (list.Count == 0)
                {
                    _subs.Remove(roomCode);
                }

                SaveSubs();
            }
        }

        _adminStats.RecordBroadcast("qq", qq, groupId, "", "", "#退订 " + roomCode);
        await SendGroupMessageAsync(settings, groupId, removed ? "✅ 已退订房间 " + roomCode + "。" : "您没有订阅房间 " + roomCode + "。");
    }

    [EventListener]
    public async ValueTask OnGameEnded(IGameEndedEvent e)
    {
        var code = e.Game.Code.ToString();
        List<long> targets;
        lock (_subsLock)
        {
            if (!_subs.TryGetValue(code, out var list) || list.Count == 0)
            {
                return;
            }

            targets = list.ToList();
        }

        var settings = GetSettings();
        if (settings == null || !settings.Enabled)
        {
            return;
        }

        var message = "您订阅的 " + code + " 房间游戏结束！";
        var ok = 0;
        foreach (var qq in targets)
        {
            if (await SendPrivateMessageAsync(settings, qq, message))
            {
                ok++;
            }
        }

        _logger.LogInformation("[RoomMonitor] Game {Code} ended; notified {Ok}/{Count} subscribers.", code, ok, targets.Count);
    }

    /// <summary>OneBot private (DM) message; false when the endpoint rejects it (e.g. group disallows temp sessions).</summary>
    private async Task<bool> SendPrivateMessageAsync(RoomMonitorSettings settings, long qq, string message)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);

            var url = settings.OneBotUrl.TrimEnd('/') + "/send_private_msg";
            var payload = new { user_id = qq, message };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            if (!string.IsNullOrWhiteSpace(settings.OneBotToken))
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.OneBotToken);
                response = await client.SendAsync(req);
            }
            else
            {
                response = await client.PostAsync(url, content);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[RoomMonitor] send_private_msg to {Qq} failed.", qq);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoomMonitor] Failed to send private message to {Qq}.", qq);
            return false;
        }
    }
}