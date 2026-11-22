using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>AstrBot 地址, 例如 http://127.0.0.1:6185。</summary>
    public string AstrBotUrl { get; set; } = "";

    /// <summary>AstrBot 的 OpenAPI 密钥(abk_ 开头, 需要 im 权限)。</summary>
    public string AstrBotApiKey { get; set; } = "";

    /// <summary>
    ///     房间报告、/m、定时公告要推送到的会话(AstrBot 的 UMO,
    ///     例如 "qq_official:GroupMessage:xxxx")。
    /// </summary>
    public List<string> TargetSessions { get; set; } = new();

    /// <summary>
    ///     桥接口令: AstrBot 插件转发群命令到本服务端时要带的口令。
    ///     第一次用到时自动生成, 面板里可以查看和重新生成。
    /// </summary>
    public string BridgeToken { get; set; } = "";

    public string ServerName { get; set; } = "帆船 AmongUs 服务器";

    // QQ 消息模板（空 = 使用内置默认；面板编辑框未修改时即显示内置默认）
    public string RoomReportTemplate { get; set; } = "";

    public string StatusReplyTemplate { get; set; } = "";

    // 群内激活房间汇报的状态命令（默认 #在线状态，可配置多个，空 = 默认）
    public List<string> StatusTriggers { get; set; } = new();
}

public class RoomMonitorStore
{
    public RoomMonitorSettings Settings { get; set; } = new();
}

/// <summary>One "#订阅" entry: where the user subscribed and who they are.</summary>
public class RoomSubscription
{
    /// <summary>订阅时所在的会话(UMO), 游戏结束的提醒发回这里。</summary>
    public string Session { get; set; } = "";

    public string SenderId { get; set; } = "";

    public string SenderName { get; set; } = "";
}

/// <summary>
///     Room monitor / QQ broadcast through AstrBot.
/// </summary>
/// <remarks>
///     发送: 房间报告(/m)、定时公告、订阅提醒都通过 AstrBot 的 OpenAPI
///     (POST /api/v1/im/messages, X-API-Key) 发到指定会话。AstrBot 可以接 QQ 官方机器人,
///     也可以接其它平台, 服务端不关心是哪个平台。
///
///     接收: AstrBot 的 OpenAPI 不会把群消息转给外部程序, 所以群命令
///     (#在线状态 / #订阅 / #退订 / #帮助) 由配套的 AstrBot 插件转发到本服务端的桥接接口,
///     本服务只负责生成回复文本, 由插件在原会话里回复(被动回复, 不受主动消息额度限制)。
///
///     游戏内: 房主 /m 推送房间报告、/m &lt;内容&gt; 附一次性备注、/m on|off 开关本房间广播。
///     设置保存在 webadmin_monitor.json。
/// </remarks>
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
    private Dictionary<string, List<RoomSubscription>> _subs = new();
    private readonly object _lock = new();
    private RoomMonitorStore _store;
    private long _fileVersion = -1;
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

        // 订阅改为按会话(UMO)记录, 与以前按 QQ 号记录的文件格式不同, 所以用新文件名。
        _subsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_subscriptions.json");
        _store = new RoomMonitorStore();
        Load();
        LoadSubs();
    }

    public void Start()
    {
        _eventRegistration ??= _eventManager.RegisterListener(this);
        _logger.LogInformation("[RoomMonitor] Started (enabled={Enabled}, sessions={Count}).",
            _store.Settings.Enabled, _store.Settings.TargetSessions.Count);
    }

    public void Stop()
    {
        _eventRegistration?.Dispose();
        _eventRegistration = null;
    }

    public void Dispose()
    {
        Stop();
    }

    // ========== settings ==========

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
                    _store.Settings.TargetSessions ??= new List<string>();
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
            EnsureBridgeToken();
            return new RoomMonitorSettings
            {
                Enabled = _store.Settings.Enabled,
                AstrBotUrl = _store.Settings.AstrBotUrl ?? "",
                AstrBotApiKey = _store.Settings.AstrBotApiKey ?? "",
                TargetSessions = _store.Settings.TargetSessions.ToList(),
                BridgeToken = _store.Settings.BridgeToken,
                ServerName = _store.Settings.ServerName,
                RoomReportTemplate = _store.Settings.RoomReportTemplate ?? "",
                StatusReplyTemplate = _store.Settings.StatusReplyTemplate ?? "",
                StatusTriggers = _store.Settings.StatusTriggers?.ToList() ?? new List<string>(),
            };
        }
    }

    /// <summary>桥接口令为空时生成一个(32 位十六进制)。调用方需持有 _lock。</summary>
    private void EnsureBridgeToken()
    {
        if (!string.IsNullOrWhiteSpace(_store.Settings.BridgeToken))
        {
            return;
        }

        _store.Settings.BridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Save();
    }

    /// <summary>Generates a new bridge token (the AstrBot plugin must be updated with it).</summary>
    public string RegenerateBridgeToken()
    {
        lock (_lock)
        {
            TryReload();
            _store.Settings.BridgeToken = string.Empty;
            EnsureBridgeToken();
            return _store.Settings.BridgeToken;
        }
    }

    /// <summary>Constant-time check of the token sent by the AstrBot plugin.</summary>
    public bool CheckBridgeToken(string? token)
    {
        string expected;
        lock (_lock)
        {
            TryReload();
            EnsureBridgeToken();
            expected = _store.Settings.BridgeToken;
        }

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(token);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>状态命令的内置默认（面板"恢复默认"与未配置时的回退值）。</summary>
    public static readonly List<string> DefaultStatusTriggers = new() { "#在线状态" };

    /// <summary>归一化状态命令：去空白、去重、去掉空项，最多 10 条。</summary>
    internal static List<string> NormalizeTriggers(IEnumerable<string>? triggers)
    {
        return (triggers ?? Enumerable.Empty<string>())
            .Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
    }

    /// <summary>当前生效的状态命令列表（未配置 = 内置默认）。</summary>
    internal static List<string> StatusTriggerList(RoomMonitorSettings settings)
    {
        var list = NormalizeTriggers(settings.StatusTriggers);
        return list.Count > 0 ? list : DefaultStatusTriggers.ToList();
    }

    /// <summary>会话(UMO)的归一化: 去空白、去重、去空, 最多 50 个。</summary>
    internal static List<string> NormalizeSessions(IEnumerable<string>? sessions)
    {
        return (sessions ?? Enumerable.Empty<string>())
            .Select(s => (s ?? "").Trim())
            .Where(s => s.Length > 0 && s.Length <= 300)
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToList();
    }

    public void UpdateSettings(bool? enabled, string? astrBotUrl, string? astrBotApiKey, List<string>? targetSessions, string? serverName,
        string? roomReportTemplate = null, string? statusReplyTemplate = null, List<string>? statusTriggers = null)
    {
        lock (_lock)
        {
            TryReload();
            if (enabled.HasValue)
            {
                _store.Settings.Enabled = enabled.Value;
            }

            if (astrBotUrl != null)
            {
                _store.Settings.AstrBotUrl = astrBotUrl.Trim().TrimEnd('/');
            }

            if (astrBotApiKey != null)
            {
                _store.Settings.AstrBotApiKey = astrBotApiKey.Trim();
            }

            if (targetSessions != null)
            {
                _store.Settings.TargetSessions = NormalizeSessions(targetSessions);
            }

            if (serverName != null)
            {
                _store.Settings.ServerName = serverName.Trim();
            }

            // 模板与内置默认一致时存空串：默认格式以后演进时仍自动生效。
            if (roomReportTemplate != null)
            {
                _store.Settings.RoomReportTemplate = roomReportTemplate.Trim() == DefaultRoomReportTemplate ? "" : roomReportTemplate;
            }

            if (statusReplyTemplate != null)
            {
                _store.Settings.StatusReplyTemplate = statusReplyTemplate.Trim() == DefaultStatusReplyTemplate ? "" : statusReplyTemplate;
            }

            // 状态命令：与内置默认完全一致时存空列表（= 未自定义），空提交同样回到默认。
            if (statusTriggers != null)
            {
                var normalized = NormalizeTriggers(statusTriggers);
                var isDefault = normalized.Count == DefaultStatusTriggers.Count &&
                                !normalized.Except(DefaultStatusTriggers, StringComparer.Ordinal).Any();
                _store.Settings.StatusTriggers = isDefault ? new List<string>() : normalized;
            }

            EnsureBridgeToken();
            Save();
            _logger.LogInformation("[RoomMonitor] Settings updated (enabled={Enabled}, sessions={Count}).",
                _store.Settings.Enabled, _store.Settings.TargetSessions.Count);
        }
    }

    // ========== AstrBot bridge (群命令) ==========

    /// <summary>Commands the AstrBot plugin should forward (it only forwards these).</summary>
    public List<string> BridgeCommands()
    {
        var settings = GetSettings();
        var list = StatusTriggerList(settings);
        list.AddRange(new[] { "#帮助", "#help", "#订阅", "#退订" });
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Handles a chat command forwarded by the AstrBot plugin and returns the reply text,
    ///     or null when the message is not one of our commands.
    /// </summary>
    public string? HandleBridgeCommand(string session, string senderId, string senderName, string messageText)
    {
        var settings = GetSettings();
        if (!settings.Enabled)
        {
            return null;
        }

        messageText = (messageText ?? "").Trim();
        if (messageText.Length == 0)
        {
            return null;
        }

        if (messageText is "#帮助" or "#help")
        {
            return QqHelpText;
        }

        // 状态命令可配置多个（默认 #在线状态），精确匹配整条消息。
        var triggers = StatusTriggerList(settings);
        if (triggers.Contains(messageText, StringComparer.Ordinal))
        {
            _logger.LogInformation("[RoomMonitor] Status command {Command} from {Session} ({Sender}).", messageText, session, senderName);
            _adminStats.RecordBroadcast("qq", 0, 0, "", senderName, messageText + " @ " + session);
            return BuildStatusReplyMessage(settings, _gameManager.Games);
        }

        if (messageText.StartsWith("#订阅", StringComparison.Ordinal))
        {
            return Subscribe(session, senderId, senderName, messageText["#订阅".Length..].Trim());
        }

        if (messageText.StartsWith("#退订", StringComparison.Ordinal))
        {
            return Unsubscribe(session, senderId, senderName, messageText["#退订".Length..].Trim());
        }

        return null;
    }

    // ========== QQ 消息模板 ==========

    /// <summary>/m 房间报告的内置默认格式（面板编辑框未修改时显示并使用这一份）。</summary>
    public const string DefaultRoomReportTemplate = "📡 {server} · 房间报告\n{rooms}";

    /// <summary>#在线状态 回复的内置默认格式。</summary>
    public const string DefaultStatusReplyTemplate = "📡 {server} · 房间报告\n{rooms}";

    /// <summary>
    ///     渲染 QQ 消息模板。占位符：{server} 服务器名、{count} 房间数、
    ///     {rooms} 房间列表（自动渲染，无房间时为"当前没有活跃房间"）、
    ///     {time} 服务器本地时间。模板为空时使用内置默认。
    /// </summary>
    private static string RenderQqTemplate(string template, string defaultTemplate, RoomMonitorSettings settings, IEnumerable<IGame> games)
    {
        var roomList = games.Where(IsBroadcastEnabled).ToList();
        var body = string.IsNullOrWhiteSpace(template) ? defaultTemplate : template.Replace("\\n", "\n");
        return body
            .Replace("{server}", settings.ServerName ?? "")
            .Replace("{count}", roomList.Count.ToString())
            .Replace("{rooms}", RenderRoomListBody(roomList))
            .Replace("{time}", DateTime.Now.ToString("MM-dd HH:mm"));
    }

    private static string RenderRoomListBody(List<IGame> roomList)
    {
        if (roomList.Count == 0)
        {
            return "▶ 当前没有活跃房间";
        }

        var sb = new StringBuilder();
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

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private string BuildRoomStatusMessage(RoomMonitorSettings settings, IEnumerable<IGame> games)
    {
        return RenderQqTemplate(settings.RoomReportTemplate, DefaultRoomReportTemplate, settings, games);
    }

    private string BuildStatusReplyMessage(RoomMonitorSettings settings, IEnumerable<IGame> games)
    {
        return RenderQqTemplate(settings.StatusReplyTemplate, DefaultStatusReplyTemplate, settings, games);
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

    // ========== 发送 ==========

    /// <summary>
    ///     Sends a message to every target session (used by the panel's scheduled-task engine).
    ///     Returns how many sessions accepted it; 0 when disabled, no sessions, or all failed.
    /// </summary>
    public async Task<int> SendAnnouncementToGroupsAsync(string message)
    {
        var settings = GetSettings();
        if (!settings.Enabled)
        {
            return 0;
        }

        var delivered = 0;
        foreach (var session in settings.TargetSessions)
        {
            var (ok, _) = await SendToSessionAsync(settings, session, message);
            if (ok)
            {
                delivered++;
                _adminStats.RecordBroadcast("schedule", 0, 0, "", "", message);
            }
        }

        return delivered;
    }

    /// <summary>Panel "test" button: sends one message to one session and reports AstrBot's answer.</summary>
    public async Task<(bool Ok, string Detail)> SendTestAsync(string session, string message)
    {
        var settings = GetSettings();
        return await SendToSessionAsync(settings, session, message);
    }

    /// <summary>
    ///     Sends plain text to one AstrBot session (UMO).
    /// </summary>
    /// <remarks>
    ///     AstrBot 当前版本的路径是 /api/v1/im/messages; 早期(v4.18 前后)文档写的是
    ///     /api/v1/im/message。先用新的, 遇到 404/405 再试旧的。
    ///     成功的判断: HTTP 2xx 且返回的 status 不是 "error"。
    /// </remarks>
    private async Task<(bool Ok, string Detail)> SendToSessionAsync(RoomMonitorSettings settings, string session, string message)
    {
        if (string.IsNullOrWhiteSpace(settings.AstrBotUrl) || string.IsNullOrWhiteSpace(settings.AstrBotApiKey))
        {
            return (false, "AstrBot 地址或 API Key 未设置");
        }

        if (string.IsNullOrWhiteSpace(session))
        {
            return (false, "会话为空");
        }

        var baseUrl = settings.AstrBotUrl.TrimEnd('/');
        var payload = JsonSerializer.Serialize(new { umo = session.Trim(), message });

        foreach (var path in new[] { "/api/v1/im/messages", "/api/v1/im/message" })
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("X-API-Key", settings.AstrBotApiKey);

                using var response = await client.SendAsync(req);
                var body = await response.Content.ReadAsStringAsync();

                if ((int)response.StatusCode is 404 or 405)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[RoomMonitor] AstrBot send to {Session} failed [{Status}]: {Body}", session, (int)response.StatusCode, Truncate(body));
                    return (false, $"HTTP {(int)response.StatusCode}: {Truncate(body)}");
                }

                var (status, msg) = ReadEnvelope(body);
                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[RoomMonitor] AstrBot rejected message to {Session}: {Message}", session, msg);
                    return (false, "AstrBot: " + msg);
                }

                return (true, "已发送");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[RoomMonitor] AstrBot send to {Session} failed: {Error}", session, ex.Message);
                return (false, ex.Message);
            }
        }

        return (false, "AstrBot 没有消息发送接口（需要 v4.18 或更新版本）");
    }

    private static (string? Status, string Message) ReadEnvelope(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            return (status, msg);
        }
        catch (JsonException)
        {
            return (null, "");
        }
    }

    private static string Truncate(string s, int max = 200) => s.Length <= max ? s : s[..max] + "...";

    // ========== In-game ==========

    [EventListener]
    public ValueTask OnGameCreated(IGameCreatedEvent e)
    {
        e.Game.Items[BroadcastEnabledKey] = true;
        return default;
    }

    [EventListener]
    public async ValueTask OnPlayerChat(IPlayerChatEvent e)
    {
        var message = e.Message.Trim();

        // /m：推送房间报告；/m <内容>：推送房间报告，并在本房间条目下附带一次性备注（发送后清除）。
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
                e.Game.Items[RoomRemarkKey] = remark;
            }

            var monitorSettings = GetSettings();
            var delivered = 0;
            if (monitorSettings.Enabled)
            {
                var push = BuildRoomStatusMessage(monitorSettings, _gameManager.Games);
                var statContent = remark.Length > 0 ? "备注：" + remark : "房间报告";
                foreach (var session in monitorSettings.TargetSessions)
                {
                    var (ok, _) = await SendToSessionAsync(monitorSettings, session, push);
                    if (ok)
                    {
                        delivered++;
                        _adminStats.RecordBroadcast("game", 0, 0, e.ClientPlayer.Client.FriendCode ?? "", senderName, statContent);
                    }
                }
            }

            if (e.PlayerControl != null)
            {
                await e.PlayerControl.SendChatToPlayerAsync(
                    delivered > 0 ? $"已推送到 {delivered} 个QQ群。" : "推送失败：QQ 广播未启用或没有可用的群。",
                    e.PlayerControl);
            }

            _logger.LogInformation("[RoomMonitor] Host {Host} pushed /m to {Count} sessions in {Code}.",
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

    // ========== 订阅功能 ==========

    public const string QqHelpText =
        "📖 QQ群命令帮助\n" +
        "#在线状态 - 查看服务器房间列表\n" +
        "#订阅 <房间号> - 订阅房间，游戏结束后在本群提醒您\n" +
        "#退订 <房间号> - 取消订阅\n" +
        "#帮助 - 显示本帮助\n" +
        "（房主可在游戏内 /m 推送房间报告，/m <内容> 附一次性备注）";

    private void LoadSubs()
    {
        try
        {
            if (File.Exists(_subsFile))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, List<RoomSubscription>>>(File.ReadAllText(_subsFile), JsonOptions);
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

    private static bool IsRoomCode(string roomCode) => System.Text.RegularExpressions.Regex.IsMatch(roomCode, "^[A-Z]{4,10}$");

    private string Subscribe(string session, string senderId, string senderName, string roomCode)
    {
        roomCode = roomCode.ToUpperInvariant();
        if (!IsRoomCode(roomCode))
        {
            return "❌ 房间号格式不正确，应为 4-10 位字母（如 ABCDEF）。用法：#订阅 ABCDEF";
        }

        lock (_subsLock)
        {
            if (!_subs.TryGetValue(roomCode, out var list))
            {
                list = new List<RoomSubscription>();
                _subs[roomCode] = list;
            }

            if (!list.Any(s => s.Session == session && s.SenderId == senderId))
            {
                list.Add(new RoomSubscription { Session = session, SenderId = senderId, SenderName = senderName });
                SaveSubs();
            }
        }

        _adminStats.RecordBroadcast("qq", 0, 0, "", senderName, "#订阅 " + roomCode);
        _logger.LogInformation("[RoomMonitor] {Sender} subscribed to {Code} from {Session}.", senderName, roomCode, session);
        return "✅ 订阅成功！房间 " + roomCode + " 的游戏结束后，BOT 会在这里提醒您。取消订阅请输入 #退订 " + roomCode;
    }

    private string Unsubscribe(string session, string senderId, string senderName, string roomCode)
    {
        roomCode = roomCode.ToUpperInvariant();
        if (!IsRoomCode(roomCode))
        {
            return "❌ 房间号格式不正确。用法：#退订 ABCDEF";
        }

        bool removed;
        lock (_subsLock)
        {
            removed = _subs.TryGetValue(roomCode, out var list)
                      && list.RemoveAll(s => s.Session == session && s.SenderId == senderId) > 0;
            if (removed)
            {
                if (list!.Count == 0)
                {
                    _subs.Remove(roomCode);
                }

                SaveSubs();
            }
        }

        _adminStats.RecordBroadcast("qq", 0, 0, "", senderName, "#退订 " + roomCode);
        return removed ? "✅ 已退订房间 " + roomCode + "。" : "您没有订阅房间 " + roomCode + "。";
    }

    /// <remarks>
    ///     游戏结束时, 把提醒发回每个订阅者订阅时所在的会话(同一会话的多人合并成一条)。
    ///     这是主动消息: QQ 官方机器人需要群主在群设置里打开"机器人主动在群聊内发言"才能收到。
    /// </remarks>
    [EventListener]
    public async ValueTask OnGameEnded(IGameEndedEvent e)
    {
        var code = e.Game.Code.ToString();
        List<RoomSubscription> targets;
        lock (_subsLock)
        {
            if (!_subs.TryGetValue(code, out var list) || list.Count == 0)
            {
                return;
            }

            targets = list.ToList();
        }

        var settings = GetSettings();
        if (!settings.Enabled)
        {
            return;
        }

        var ok = 0;
        foreach (var group in targets.GroupBy(t => t.Session))
        {
            var names = string.Join("、", group.Select(t => string.IsNullOrWhiteSpace(t.SenderName) ? t.SenderId : t.SenderName).Distinct());
            var message = "🔔 " + names + "：您订阅的房间 " + code + " 游戏结束！";
            var (sent, _) = await SendToSessionAsync(settings, group.Key, message);
            if (sent)
            {
                ok += group.Count();
            }
        }

        _logger.LogInformation("[RoomMonitor] Game {Code} ended; notified {Ok}/{Count} subscribers.", code, ok, targets.Count);
    }
}
