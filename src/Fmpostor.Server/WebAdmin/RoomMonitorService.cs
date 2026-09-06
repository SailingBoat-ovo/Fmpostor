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

    // Turbo-650：QQ 消息模板（空 = 使用内置默认；面板编辑框未修改时即显示内置默认）
    public string RoomReportTemplate { get; set; } = "";
    public string StatusReplyTemplate { get; set; } = "";

    // Turbo-650：群内激活房间汇报的状态命令（默认 #在线状态，可配置多个，空 = 默认）
    public List<string> StatusTriggers { get; set; } = new();
}

public class RoomMonitorStore
{
    public RoomMonitorSettings Settings { get; set; } = new();
}

/// <summary>
///     Room monitor / QQ broadcast (ported from Fanchuan.RoomMonitor.Plugin):
///     connects to a OneBot v11 (NapCat) endpoint, answers the status command
///     (default "#在线状态"; multiple configurable from the panel) in
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

    // HTTP 轮询水位（实例级：WS/轮询模式切换时保留，不重放、不丢窗口）
    private readonly Dictionary<long, long> _pollLastIds = new();
    private readonly HashSet<long> _pollInitialized = new();

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
                RoomReportTemplate = _store.Settings.RoomReportTemplate ?? "",
                StatusReplyTemplate = _store.Settings.StatusReplyTemplate ?? "",
                StatusTriggers = _store.Settings.StatusTriggers?.ToList() ?? new List<string>(),
            };
        }
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

    public void UpdateSettings(bool? enabled, string? oneBotUrl, string? oneBotToken, List<long>? allowedGroups, string? serverName,
        string? roomReportTemplate = null, string? statusReplyTemplate = null, List<string>? statusTriggers = null)
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
                // Turbo-620 修订：ws:// / wss:// 走正向 WebSocket 接收；
                // http:// / https:// 走混合接收（Turbo-650）：优先尝试与 HTTP API
                // 同端口的 WebSocket（NapCat HTTP 服务器开启"启用Ws"时可用，
                // 与原 Fanchuan.RoomMonitor.Plugin 相同的接收方式，实时推送），
                // WS 不可用时退回 HTTP 轮询 get_group_msg_history，并定期重试 WS。
                // 注意：接收循环必须由 Program.cs 调用 Start() 才会运行。
                var url = GetSettings().OneBotUrl ?? "";
                if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                {
                    await ConnectAndListenAsync(cancellation);
                }
                else
                {
                    await ReceiveHttpEndpointAsync(cancellation);
                }
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

    /// <summary>
    ///     Turbo-650 混合接收入口：http(s):// 地址先尝试与 HTTP API 同端口的
    ///     正向 WebSocket（NapCat "启用Ws"，原插件接收方式），连上即长驻监听；
    ///     连不上立即退回 HTTP 轮询。WS 会话结束后清空轮询水位，防止恢复
    ///     轮询时重放 WS 期间已处理过的消息。
    /// </summary>
    private async Task ReceiveHttpEndpointAsync(CancellationToken cancellation)
    {
        if (await ConnectAndListenAsync(cancellation))
        {
            _pollInitialized.Clear();
            _pollLastIds.Clear();
            return;
        }

        await HttpPollLoopAsync(cancellation);
    }

    /// <summary>
    ///     正向 WebSocket 接收。returns=true 表示连接曾建立并监听至断开；
    ///     false 表示连接尝试失败（可退回 HTTP 轮询）。
    /// </summary>
    private async Task<bool> ConnectAndListenAsync(CancellationToken cancellation)
    {
        var settings = GetSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.OneBotUrl))
        {
            await SleepAsync(cancellation, 10000);
            return false;
        }

        // Only WebSocket endpoints are supported for receiving messages.
        var wsUrl = settings.OneBotUrl
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        using var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(settings.OneBotToken))
        {
            // 与原 Fanchuan.RoomMonitor.Plugin 一致：token 走 access_token 查询参数
            // （NapCat WS 鉴权最兼容的方式）；同时保留 Authorization 头兼容其他实现，
            // 两者都不会把 token 明文写进服务器访问日志以外的文件。
            ws.Options.SetRequestHeader("Authorization", "Bearer " + settings.OneBotToken);
            wsUrl += (wsUrl.Contains('?') ? "&" : "?") + "access_token=" + Uri.EscapeDataString(settings.OneBotToken);
        }

        _logger.LogInformation("[RoomMonitor] Trying OneBot WebSocket on same host/port as the HTTP API (push mode, like the original plugin)...");

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(new Uri(wsUrl), connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            _logger.LogInformation("[RoomMonitor] WebSocket connect timed out; falling back to HTTP polling.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[RoomMonitor] WebSocket unavailable on this endpoint ({Reason}); falling back to HTTP polling.",
                ex.Message.Length > 120 ? ex.Message[..120] : ex.Message);
            return false;
        }

        _logger.LogInformation("[RoomMonitor] OneBot WebSocket connected — receiving group events (push mode).");

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

        return true;
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

            var messageText = ExtractMessageText(root, rawMsg);
            var userId = root.TryGetProperty("user_id", out var ui) ? ui.GetInt64() : 0L;
            await HandleGroupCommandAsync(settings, groupId, userId, messageText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RoomMonitor] Failed to process WebSocket message.");
        }
    }

    /// <summary>
    ///     Extracts plain text from a OneBot message object: raw_message,
    ///     or the message field as string / segment array (text segments only).
    ///     Works for both WebSocket event objects and polled history entries.
    /// </summary>
    private static string ExtractMessageText(JsonElement root, string? rawMsg)
    {
        var messageText = rawMsg ?? "";
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

        return messageText.Trim();
    }

    /// <summary>
    ///     Shared command dispatch for group messages, used by both the
    ///     WebSocket receive path and the HTTP polling receive path.
    /// </summary>
    private async Task HandleGroupCommandAsync(RoomMonitorSettings settings, long groupId, long userId, string messageText)
    {
        messageText = (messageText ?? "").Trim();
        if (messageText.Length == 0)
        {
            return;
        }

        if (messageText is "#帮助" or "#help")
        {
            await SendGroupMessageAsync(settings, groupId, QqHelpText);
            return;
        }

        // Turbo-650：状态命令可配置多个（默认 #在线状态），精确匹配整条消息。
        var triggers = StatusTriggerList(settings);
        if (triggers.Contains(messageText, StringComparer.Ordinal))
        {
            _logger.LogInformation("[RoomMonitor] Received status command {Command} from group {GroupId} user {Qq}.", messageText, groupId, userId);
            var reply = BuildStatusReplyMessage(settings, _gameManager.Games);
            await SendGroupMessageAsync(settings, groupId, reply);
            _adminStats.RecordBroadcast("qq", userId, groupId, "", "", messageText);
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

    // ========== HTTP 轮询接收（Turbo-620 修订） ==========

    private sealed record PolledGroupMessage(long MessageId, long UserId, string Text);

    /// <summary>
    ///     HTTP 接收模式（Turbo-650 退回路径）：直接轮询 OneBot HTTP API
    ///     get_group_msg_history（NapCat / go-cqhttp / Lagrange 等均实现）。
    ///     每 3 秒一轮，按 message_id 去重；每组首轮只记录水位不执行命令
    ///     （避免重放历史）。每 20 轮（约 1 分钟）返回一次，让 RunLoop 重新
    ///     尝试同端口 WebSocket（水位保存在实例字段，切换不丢、不重放）。
    /// </summary>
    private async Task HttpPollLoopAsync(CancellationToken cancellation)
    {
        var settings = GetSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.OneBotUrl))
        {
            // 与 WS 分支一致：未启用时空转后回到 RunLoop 重新判断。
            if (!await SleepAsync(cancellation))
            {
                return;
            }

            return;
        }

        _logger.LogInformation("[RoomMonitor] HTTP polling mode: receiving group messages via get_group_msg_history on {Url} (3s interval).", settings.OneBotUrl);

        var consecutiveFailures = 0;
        var rounds = 0;

        while (!cancellation.IsCancellationRequested)
        {
            settings = GetSettings();
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.OneBotUrl))
            {
                return; // 回到 RunLoop 重新选择接收模式
            }

            var groups = settings.AllowedGroups ?? new List<long>();
            if (groups.Count == 0)
            {
                if (!await SleepAsync(cancellation, 5000))
                {
                    return;
                }

                continue;
            }

            var failed = false;
            foreach (var groupId in groups)
            {
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var msgs = await FetchGroupHistoryAsync(settings, groupId, cancellation);
                    if (msgs == null)
                    {
                        failed = true;
                        continue;
                    }

                    if (!_pollInitialized.Contains(groupId))
                    {
                        // 首轮：只推进水位，不执行历史消息里的命令
                        _pollInitialized.Add(groupId);
                        _pollLastIds[groupId] = msgs.Count > 0 ? msgs[^1].MessageId : 0;
                        continue;
                    }

                    var last = _pollLastIds.TryGetValue(groupId, out var l) ? l : 0;
                    foreach (var m in msgs)
                    {
                        if (m.MessageId <= last)
                        {
                            continue;
                        }

                        _pollLastIds[groupId] = m.MessageId;
                        await HandleGroupCommandAsync(settings, groupId, m.UserId, m.Text);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failed = true;
                    _logger.LogWarning(ex, "[RoomMonitor] Poll failed for group {GroupId}.", groupId);
                }
            }

            consecutiveFailures = failed ? consecutiveFailures + 1 : 0;
            if (consecutiveFailures == 5)
            {
                _logger.LogWarning(
                    "[RoomMonitor] HTTP polling keeps failing on {Url}. 请确认该地址是 NapCat 的 HTTP 服务器地址（/m 能发送即 HTTP 可用）；若想用正向 WebSocket 接收，请把地址改成 ws:// 开头。",
                    settings.OneBotUrl);
            }

            // 每 20 轮让出一次，给同端口 WebSocket 一次重试机会（RunLoop 驱动）。
            if (++rounds >= 20)
            {
                _logger.LogInformation("[RoomMonitor] Polling handover: retrying same-port WebSocket before the next polling stretch.");
                return;
            }

            if (!await SleepAsync(cancellation, 3000))
            {
                return;
            }
        }
    }

    /// <summary>
    ///     Calls OneBot get_group_msg_history for one group. Returns the
    ///     messages sorted by message_id ascending, or null when the call
    ///     failed (caller counts it as a failed round).
    /// </summary>
    private async Task<List<PolledGroupMessage>?> FetchGroupHistoryAsync(RoomMonitorSettings settings, long groupId, CancellationToken cancellation)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(6);

            var url = settings.OneBotUrl.TrimEnd('/') + "/get_group_msg_history";
            var payload = new { group_id = groupId, count = 20 };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            if (!string.IsNullOrWhiteSpace(settings.OneBotToken))
            {
                // Token via header, same as send_group_msg (keeps it out of access logs).
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.OneBotToken);
                response = await client.SendAsync(req, cancellation);
            }
            else
            {
                response = await client.PostAsync(url, content, cancellation);
            }

            var body = await response.Content.ReadAsStringAsync(cancellation);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[RoomMonitor] get_group_msg_history failed [{Status}] for group {GroupId}: {Body}",
                    response.StatusCode, groupId, body.Length > 200 ? body[..200] : body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data))
            {
                _logger.LogWarning("[RoomMonitor] get_group_msg_history unexpected response for group {GroupId}.", groupId);
                return null;
            }

            JsonElement arr;
            if (data.ValueKind == JsonValueKind.Array)
            {
                arr = data;
            }
            else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                arr = ms;
            }
            else
            {
                return new List<PolledGroupMessage>();
            }

            var list = new List<PolledGroupMessage>();
            foreach (var item in arr.EnumerateArray())
            {
                var mid = GetLongLenient(item, "message_id");
                if (mid <= 0)
                {
                    continue;
                }

                var userId = GetLongLenient(item, "user_id");
                var text = ExtractMessageText(item, item.TryGetProperty("raw_message", out var rm) ? rm.GetString() : null);
                list.Add(new PolledGroupMessage(mid, userId, text));
            }

            list.Sort((a, b) => a.MessageId.CompareTo(b.MessageId));
            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoomMonitor] get_group_msg_history request failed for group {GroupId}.", groupId);
            return null;
        }
    }

    private static long GetLongLenient(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v))
        {
            return 0;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.TryGetInt64(out var n) ? n : 0;
        }

        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s))
        {
            return s;
        }

        return 0;
    }

    /// <summary>Task.Delay that swallows cancellation; false when cancelled.</summary>
    private static async Task<bool> SleepAsync(CancellationToken cancellation, int milliseconds = 10000)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellation);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ========== QQ 消息模板（Turbo-650） ==========

    /// <summary>/m 房间报告的内置默认格式（面板编辑框未修改时显示并使用这一份）。</summary>
    public const string DefaultRoomReportTemplate = "📡 {server} · 房间报告\n{rooms}";

    /// <summary>#在线状态 回复的内置默认格式（与 640 及更早版本的输出完全一致）。</summary>
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

            var monitorSettings = GetSettings();
            var push = BuildRoomStatusMessage(monitorSettings, _gameManager.Games);
            var groups = monitorSettings.AllowedGroups ?? new List<long>();
            var delivered = 0;
            var statContent = remark.Length > 0 ? "备注：" + remark : "房间报告";
            foreach (var gid in groups)
            {
                if (await SendGroupMessageAsync(monitorSettings, gid, push))
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