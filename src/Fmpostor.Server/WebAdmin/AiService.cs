using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class AiSettings
{
    /// <summary>
    ///     Allow players to chat with the AI in-game via /aichat.
    /// </summary>
    public bool InGameChatEnabled { get; set; } = false;

    public string Model { get; set; } = "glm-4.7-flash";

    public string SystemPrompt { get; set; } =
        "你是由帆船服务端驱动的 Among Us 服务器的 AI 助手，也是 Among Us 资深玩家和攻略专家。" +
        "可以陪玩家聊天、聊游戏、讲解真实的 Among Us 玩法攻略。\n" +
        "必须遵守：\n" +
        "1. 只能依据真实的 Among Us 游戏知识回答，不得编造角色能力、地图布局、玩法机制、更新内容等；\n" +
        "2. 你只知道系统提供的当前时间、房间号和玩家名单，无法获取该房间任何对局信息（角色、任务、投票、击杀等），不得猜测或编造对局情况；\n" +
        "3. 回复简洁自然（一般 50 字内，攻略类问题可适当详细）；\n" +
        "4. 遇到不确定或需要最新信息的问题，可以使用联网搜索获取真实资料。";

    /// <summary>
    ///     Number of recent messages kept as context (per player / per panel session).
    /// </summary>
    public int MaxContextMessages { get; set; } = 20;

    /// <summary>
    ///     Custom API key for panel AI (any format, not limited to Zhipu).
    ///     Empty = use the built-in key.
    /// </summary>
    public string PanelApiKey { get; set; } = "";

    /// <summary>
    ///     Custom API base URL for panel AI (e.g. https://api.openai.com/v1).
    ///     Empty = built-in Zhipu endpoint.
    /// </summary>
    public string PanelApiBaseUrl { get; set; } = "";

    /// <summary>
    ///     API format for panel AI: "zhipu" (default) or "openai" (OpenAI compatible).
    /// </summary>
    public string PanelApiFormat { get; set; } = "zhipu";

    /// <summary>
    ///     Custom model for panel AI. Empty = use the shared Model.
    /// </summary>
    public string PanelModel { get; set; } = "";

    /// <summary>
    ///     Custom API key for in-game AI (any format, not limited to Zhipu).
    ///     Empty = use the built-in key.
    /// </summary>
    public string InGameApiKey { get; set; } = "";

    /// <summary>
    ///     Custom API base URL for in-game AI. Empty = built-in Zhipu endpoint.
    /// </summary>
    public string InGameApiBaseUrl { get; set; } = "";

    /// <summary>
    ///     API format for in-game AI: "zhipu" (default) or "openai" (OpenAI compatible).
    /// </summary>
    public string InGameApiFormat { get; set; } = "zhipu";

    /// <summary>
    ///     Custom model for in-game AI. Empty = use the shared Model.
    /// </summary>
    public string InGameModel { get; set; } = "";
    /// <summary>游戏内 /aichat 频率限制：滑动窗口秒数与轮数。</summary>
    public int InGameRateSeconds { get; set; } = 60;
    public int InGameRateRounds { get; set; } = 5;
    /// <summary>面板非管理员 AI 频率限制：滑动窗口秒数与轮数。</summary>
    public int PanelRateSeconds { get; set; } = 100;
    public int PanelRateRounds { get; set; } = 2;
    /// <summary>是否允许 AI 联网搜索（内置端点用原生工具，自定义端点用 cn.bing.com 注入）。</summary>
    public bool WebSearchEnabled { get; set; } = true;
}

public class AiChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = ""; // system / user / assistant

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

public class AiChatRecord
{
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string PlayerName { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string Puid { get; set; } = "";
    public string GameCode { get; set; } = "";
    public bool FromInGame { get; set; }
    // Panel records: which panel account sent it ("chat" = normal AI chat,
    // "agent" = Agent auto-operation run). Empty on older records.
    public string PanelUser { get; set; } = "";
    public string Kind { get; set; } = "chat";
    public List<AiChatMessage> Messages { get; set; } = new();
}

internal class AiStoreData
{
    public AiSettings Settings { get; set; } = new();
    public List<AiChatRecord> Chats { get; set; } = new();
}

/// <summary>
///     AI assistant backed by OpenAI-compatible chat-completions APIs
///     (Zhipu GLM / DeepSeek / Moonshot / OpenAI…) or the Anthropic Messages API.
///     - Panel: admins chat with the AI and ask it to analyze server data.
///     - In-game: /aichat lets players talk to the AI privately (toggleable).
///     No API key is compiled in: operators configure their own key in the
///     panel AI settings, config.json (WebAdmin:AiApiKey) or the
///     FMPOSTOR_AI_API_KEY environment variable. Chat history is persisted
///     to webadmin_ai.json.
/// </summary>
public class AiService
{
    // Base URL without the /chat/completions suffix — the suffix is appended
    // by TryOpenAiAsync (and /messages by TryClaudeAsync). Used as the default
    // endpoint when the operator only configures a key without a base URL.
    private const string ApiBaseUrl = "https://open.bigmodel.cn/api/paas/v4";

    // Fallback chain when the configured model is overloaded / unavailable:
    // glm-4.7-flash -> glm-4.5-flash -> glm-4-flash. Only when every model
    // fails do we tell the user that the model is under heavy load.
    private static readonly string[] FallbackModels = { "glm-4.5-flash", "glm-4-flash" };

    // No API key is compiled in — open-source builds ship without credentials.
    // Operators configure their own key via the panel AI settings, the
    // FMPOSTOR_AI_API_KEY environment variable or config.json (WebAdmin:AiApiKey).

    // Cap on per-player in-game conversation contexts (bounded memory).
    private const int MaxGameContexts = 512;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly ILogger<AiService> _logger;
    private readonly PlayerLogService _playerLogs;
    private readonly PlayerStatsService _playerStats;
    private readonly PlayerFootprintService _footprints;
    private readonly GameReplayService _replays;
    private readonly ChatService _chatService;
    private readonly ReportService _reports;
    private readonly WebAdminConfig _config;
    private readonly string _filePath;
    private readonly object _lock = new();
    private AiStoreData _store;

    // Per-player in-game conversation contexts (friend code / puid -> history).
    private readonly ConcurrentDictionary<string, List<AiChatMessage>> _gameContexts = new();

    // Panel conversation context (single admin session).
    private readonly List<AiChatMessage> _panelContext = new();

    private const int MaxChatRecords = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AiService(
        ILogger<AiService> logger,
        PlayerLogService playerLogs,
        PlayerStatsService playerStats,
        PlayerFootprintService footprints,
        GameReplayService replays,
        ChatService chatService,
        ReportService reports,
        Microsoft.Extensions.Options.IOptions<WebAdminConfig> config)
    {
        _logger = logger;
        _playerLogs = playerLogs;
        _playerStats = playerStats;
        _footprints = footprints;
        _replays = replays;
        _chatService = chatService;
        _reports = reports;
        _config = config.Value;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_ai.json");
        _store = new AiStoreData();
        Load();
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
                var data = JsonSerializer.Deserialize<AiStoreData>(json, JsonOptions);
                if (data != null)
                {
                    _store = data;
                    _store.Settings ??= new AiSettings();
                    _store.Chats ??= new List<AiChatRecord>();
                    // 一次性文案迁移：旧设置里的自述改为准确表述
                    // （避免玩家误以为这是"帆船服"官方服务器）
                    _store.Settings.SystemPrompt = _store.Settings.SystemPrompt?
                        .Replace("你是帆船 Among Us 私服服务器的 AI 助手", "你是由帆船服务端驱动的 Among Us 服务器的 AI 助手")
                        ?? _store.Settings.SystemPrompt;
                    _logger.LogInformation("[AI] Loaded settings (inGame={Enabled}, model={Model}) and {Count} chat record(s).",
                        _store.Settings.InGameChatEnabled, _store.Settings.Model, _store.Chats.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AI] Failed to load {File}, using defaults.", _filePath);
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_store, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AI] Failed to save {File}.", _filePath);
            }
        }
    }

    // ========== Settings ==========
    // ========== AI 频率限制（滑动窗口） ==========
    // 每个键（游戏内=玩家 key，面板=用户名）保留窗口内的请求时间戳，
    // 超出轮数即拒绝；提示语固定，不泄露剩余等待时间。
    private readonly object _aiRateLock = new();
    private readonly Dictionary<string, List<DateTime>> _aiRateHits = new();

    /// <summary>
    ///     Returns the rejection message when this key exceeded its AI chat
    ///     allowance ("N rounds per M seconds"), or null when allowed (the
    ///     attempt is recorded). inGame selects the in-game /aichat limits,
    ///     otherwise the panel non-admin limits apply.
    /// </summary>
    public string? CheckAiRateLimit(string key, bool inGame)
    {
        var settings = GetSettings();
        var seconds = inGame ? settings.InGameRateSeconds : settings.PanelRateSeconds;
        var rounds = inGame ? settings.InGameRateRounds : settings.PanelRateRounds;
        if (seconds <= 0 || rounds <= 0)
        {
            return null; // 未启用（管理员可配置）
        }
        var now = DateTime.UtcNow;
        lock (_aiRateLock)
        {
            if (!_aiRateHits.TryGetValue(key, out var hits))
            {
                hits = new List<DateTime>();
                _aiRateHits[key] = hits;
            }
            hits.RemoveAll(t => (now - t).TotalSeconds > seconds);
            if (hits.Count >= rounds)
            {
                return "发送太快了，请稍后再试。";
            }
            hits.Add(now);
            if (_aiRateHits.Count > 4096)
            {
                foreach (var stale in _aiRateHits.Where(kv => kv.Value.Count == 0 || (now - kv.Value[kv.Value.Count - 1]).TotalSeconds > 3600).Select(kv => kv.Key).ToList())
                {
                    _aiRateHits.Remove(stale);
                }
            }
            return null;
        }
    }


    public AiSettings GetSettings()
    {
        lock (_lock)
        {
            return new AiSettings
            {
                InGameChatEnabled = _store.Settings.InGameChatEnabled,
                Model = _store.Settings.Model,
                SystemPrompt = _store.Settings.SystemPrompt,
                MaxContextMessages = _store.Settings.MaxContextMessages,
                PanelApiKey = MaskKey(_store.Settings.PanelApiKey),
                PanelApiBaseUrl = _store.Settings.PanelApiBaseUrl,
                PanelApiFormat = _store.Settings.PanelApiFormat,
                PanelModel = _store.Settings.PanelModel,
                InGameApiKey = MaskKey(_store.Settings.InGameApiKey),
                InGameApiBaseUrl = _store.Settings.InGameApiBaseUrl,
                InGameApiFormat = _store.Settings.InGameApiFormat,
                InGameModel = _store.Settings.InGameModel,
                InGameRateSeconds = _store.Settings.InGameRateSeconds,
                InGameRateRounds = _store.Settings.InGameRateRounds,
                PanelRateSeconds = _store.Settings.PanelRateSeconds,
                PanelRateRounds = _store.Settings.PanelRateRounds,
                WebSearchEnabled = _store.Settings.WebSearchEnabled,
            };
        }
    }

    public void UpdateSettings(
        bool? inGameChatEnabled = null,
        string? systemPrompt = null,
        string? model = null,
        int? maxContextMessages = null,
        string? panelApiKey = null,
        string? inGameApiKey = null,
        string? panelApiBaseUrl = null,
        string? panelApiFormat = null,
        string? panelModel = null,
        string? inGameApiBaseUrl = null,
        string? inGameApiFormat = null,
        string? inGameModel = null,
        int? inGameRateSeconds = null,
        int? inGameRateRounds = null,
        int? panelRateSeconds = null,
        int? panelRateRounds = null,
        bool? webSearchEnabled = null)
    {
        lock (_lock)
        {
            if (inGameChatEnabled.HasValue)
            {
                _store.Settings.InGameChatEnabled = inGameChatEnabled.Value;
            }

            if (systemPrompt != null)
            {
                _store.Settings.SystemPrompt = systemPrompt;
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                _store.Settings.Model = model.Trim();
            }

            if (maxContextMessages.HasValue && maxContextMessages.Value > 0)
            {
                _store.Settings.MaxContextMessages = maxContextMessages.Value;
            }

            // null = not modified; "" = clear custom value (fall back to
            // built-in); otherwise the custom value is stored (server-local).
            if (panelApiKey != null)
            {
                _store.Settings.PanelApiKey = panelApiKey.Trim();
            }

            if (panelApiBaseUrl != null)
            {
                _store.Settings.PanelApiBaseUrl = panelApiBaseUrl.Trim();
            }

            if (panelApiFormat != null)
            {
                var fmt = panelApiFormat.Trim().ToLowerInvariant();
                _store.Settings.PanelApiFormat = fmt == "claude" ? "claude" : "openai";
            }

            if (panelModel != null)
            {
                _store.Settings.PanelModel = panelModel.Trim();
            }

            if (inGameApiKey != null)
            {
                _store.Settings.InGameApiKey = inGameApiKey.Trim();
            }

            if (inGameApiBaseUrl != null)
            {
                _store.Settings.InGameApiBaseUrl = inGameApiBaseUrl.Trim();
            }

            if (inGameApiFormat != null)
            {
                var fmt = inGameApiFormat.Trim().ToLowerInvariant();
                _store.Settings.InGameApiFormat = fmt == "claude" ? "claude" : "openai";
            }

            if (inGameModel != null)
            {
                _store.Settings.InGameModel = inGameModel.Trim();
            }
            if (inGameRateSeconds.HasValue && inGameRateSeconds.Value > 0)
            {
                _store.Settings.InGameRateSeconds = Math.Clamp(inGameRateSeconds.Value, 10, 86400);
            }
            if (inGameRateRounds.HasValue && inGameRateRounds.Value > 0)
            {
                _store.Settings.InGameRateRounds = Math.Clamp(inGameRateRounds.Value, 1, 100);
            }
            if (panelRateSeconds.HasValue && panelRateSeconds.Value > 0)
            {
                _store.Settings.PanelRateSeconds = Math.Clamp(panelRateSeconds.Value, 10, 86400);
            }
            if (panelRateRounds.HasValue && panelRateRounds.Value > 0)
            {
                _store.Settings.PanelRateRounds = Math.Clamp(panelRateRounds.Value, 1, 100);
            }
            if (webSearchEnabled.HasValue)
            {
                _store.Settings.WebSearchEnabled = webSearchEnabled.Value;
            }
            Save();
            _logger.LogInformation("[AI] Settings updated: inGame={Enabled}, model={Model}, panelCustom={PanelCustom}, inGameCustom={InGameCustom}",
                _store.Settings.InGameChatEnabled, _store.Settings.Model,
                !string.IsNullOrEmpty(_store.Settings.PanelApiKey) || !string.IsNullOrEmpty(_store.Settings.PanelApiBaseUrl),
                !string.IsNullOrEmpty(_store.Settings.InGameApiKey) || !string.IsNullOrEmpty(_store.Settings.InGameApiBaseUrl));
        }
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return key.Length <= 10 ? "****" : key[..4] + "****" + key[^4..];
    }

    private sealed record AiEndpoint(string Key, string BaseUrl, string Format, string Model, bool Custom);

    private const string ClaudeApiBaseUrl = "https://api.anthropic.com/v1";

    /// <summary>
    ///     Resolves the effective endpoint (key/baseUrl/format/model) for the
    ///     given scope: the admin's custom values when configured, otherwise
    ///     the default Zhipu-compatible endpoint with the operator's key from
    ///     config.json / FMPOSTOR_AI_API_KEY. No key is compiled in — when no
    ///     key is configured the endpoint has an empty key and chat calls are
    ///     rejected with a setup hint.
    ///     Format: "openai" (OpenAI-compatible: Zhipu/DeepSeek/Moonshot/OpenAI…)
    ///     or "claude" (Anthropic Messages API). Legacy "zhipu" maps to openai.
    /// </summary>
    private AiEndpoint GetEndpoint(bool inGame)
    {
        lock (_lock)
        {
            var s = _store.Settings;
            var key = inGame ? s.InGameApiKey : s.PanelApiKey;
            var baseUrl = inGame ? s.InGameApiBaseUrl : s.PanelApiBaseUrl;
            var format = inGame ? s.InGameApiFormat : s.PanelApiFormat;
            var model = inGame ? s.InGameModel : s.PanelModel;

            var custom = !string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(baseUrl);

            // Default endpoint: shared base URL + operator-provided key from
            // config.json / environment. Empty key means "AI not configured".
            if (!custom)
            {
                var overrideKey = Environment.GetEnvironmentVariable("FMPOSTOR_AI_API_KEY");
                if (string.IsNullOrWhiteSpace(overrideKey))
                {
                    overrideKey = _config.AiApiKey;
                }

                var effectiveKey = overrideKey?.Trim() ?? string.Empty;
                return new AiEndpoint(effectiveKey, ApiBaseUrl, "openai", s.Model?.Trim() ?? "glm-4.7-flash", false);
            }

            var fmt = string.IsNullOrWhiteSpace(format) ? "openai" : format.Trim().ToLowerInvariant();
            if (fmt != "claude")
            {
                fmt = "openai"; // zhipu 等旧值归入 OpenAI 兼容
            }

            return new AiEndpoint(
                key.Trim(),
                string.IsNullOrWhiteSpace(baseUrl) ? ApiBaseUrl : baseUrl.Trim().TrimEnd('/'),
                fmt,
                !string.IsNullOrWhiteSpace(model) ? model.Trim() : (s.Model?.Trim() ?? "glm-4.7-flash"),
                true);
        }
    }

    // ========== Chat records ==========

    public Task<List<AiChatRecord>> GetChatRecordsAsync(
        string? search = null,
        int limit = 100,
        string? source = null,
        string? kind = null,
        string? user = null,
        string? date = null)
    {
        lock (_lock)
        {
            IEnumerable<AiChatRecord> query = _store.Chats.AsEnumerable().Reverse();
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(r =>
                    r.PlayerName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.FriendCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.Puid.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.GameCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.PanelUser.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    r.Messages.Any(m => m.Content.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }

            if (source == "panel") query = query.Where(r => !r.FromInGame);
            else if (source == "ingame") query = query.Where(r => r.FromInGame);

            if (kind == "chat") query = query.Where(r => r.Kind != "agent" && r.Kind != "schedule");
            else if (kind == "agent") query = query.Where(r => r.Kind == "agent");
            else if (kind == "schedule") query = query.Where(r => r.Kind == "schedule");

            if (!string.IsNullOrWhiteSpace(user))
            {
                query = query.Where(r => r.PanelUser.Contains(user, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(date) && date.Length == 10)
            {
                query = query.Where(r => r.Time.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) == date);
            }

            return Task.FromResult(query.Take(limit).Select(r => new AiChatRecord
            {
                Time = r.Time,
                PlayerName = r.PlayerName,
                FriendCode = r.FriendCode,
                Puid = r.Puid,
                GameCode = r.GameCode,
                FromInGame = r.FromInGame,
                PanelUser = r.PanelUser,
                Kind = r.Kind,
                Messages = r.Messages.ToList(),
            }).ToList());
        }
    }

    private void AddRecord(AiChatRecord record)
    {
        lock (_lock)
        {
            _store.Chats.Add(record);
            if (_store.Chats.Count > MaxChatRecords)
            {
                _store.Chats.RemoveRange(0, _store.Chats.Count - MaxChatRecords);
            }

            Save();
        }
    }

    // ========== Context helpers ==========

    private static void AppendContext(List<AiChatMessage> history, string role, string content, int max)
    {
        history.Add(new AiChatMessage { Role = role, Content = content });
        if (history.Count > max)
        {
            history.RemoveRange(0, history.Count - max);
        }
    }

    private List<AiChatMessage> GetGameContext(string playerKey)
    {
        // Bound the context table: player keys are attacker-influenceable, so drop
        // everything when the cap is reached (conversation memory loss is harmless).
        if (_gameContexts.Count >= MaxGameContexts)
        {
            _gameContexts.Clear();
        }

        return _gameContexts.GetOrAdd(playerKey, _ => new List<AiChatMessage>());
    }

    // ========== 联网（按需：规划器生成搜索词 / 直访用户给的网址，Turbo-640） ==========

    // 纯问候/礼貌/应答（整条消息只有这些内容）——永远不联网。
    private static readonly System.Text.RegularExpressions.Regex GreetingOnlyPattern = new(
        "^(你好|您好|哈喽|嗨|halo|hello|hi|hey|yo|在吗|在么|早上好|中午好|下午好|晚上好|晚安|谢谢|多谢|感谢|拜拜|再见|辛苦了|ok|okay|好的|嗯|哦|哈)[!！?？.。,，~～\\s]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // 短消息里的身份/能力问题（我是谁/你是谁/什么模型…）——模型自身即可回答，无需联网。
    private static readonly System.Text.RegularExpressions.Regex SelfIdentityPattern = new(
        "我是谁|你是谁|你是哪个模型|什么模型|哪家公司|谁开发|谁训练|谁制造|谁做的|你是机器人|你是人吗|你叫什么|你的名字|你是谁啊|who am i|who are you|what model|your name",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // 网址识别：http(s) 显式链接，或常见顶级域的裸域名（如 au.fanchuanovo.cn）。
    // 顶级域白名单避免把 config.json / 1.2.3 之类误判为网址。
    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
        @"https?://[^\s<>""'（()）【】\[\]{}]+"
        + @"|(?<![a-zA-Z0-9_@.\-/])[a-zA-Z0-9][a-zA-Z0-9-]*(?:\.[a-zA-Z0-9-]+)+\.(?:com|cn|net|org|io|dev|cc|me|xyz|top|vip|site|online|tech|store|app|ai|gov|edu|info|club|fun|wiki|tv|co|asia|cloud|pro|biz|ltd|shop|icu)(?::\d+)?(?:/[^\s<>""'（）()【】\[\]{}]*)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private const string WebSearchPlannerSystemPrompt =
        "你是联网搜索规划器。判断为了高质量回答【用户最新消息】，是否需要联网获取真实网页资料。\n" +
        "需要时输出一行：YES|搜索词 —— 搜索词从消息里提取关键实体（游戏名/事物名/问题核心词，可含\"最新\"\"版本\"\"年份\"等时效词），去掉\"请问/帮我/告诉我/是什么\"等虚词和问号，10~20 个字。\n" +
        "不需要时只输出 NO。不需要的情况：问候与闲聊；询问 AI 自身身份/能力；对话上文已能回答的追问；翻译、写作、数学、代码；关于本服务器/面板自身的操作问题；模糊到无法形成有效搜索词。\n" +
        "只输出 YES|搜索词 或 NO，不要输出任何其他内容。";

    /// <summary>
    ///     Turbo-640 联网改造：让 AI"真会看网页，自主获取/理解信息"。
    ///     1) 消息里带网址 → 直接抓取网页正文（最多 2 个）；
    ///     2) 其余交给同端点规划器小调用：是否需要搜索 + 生成高质量搜索词，
    ///        再抓取 cn.bing.com 结果（修复原"原话当搜索词"导致的词典/外语垃圾结果）；
    ///     3) 纯问候/短消息身份问题不联网；任何失败一律按"不联网"降级。
    ///     返回注入系统提示的资料文本；无联网内容时返回 null。
    /// </summary>
    private async Task<string?> BuildWebKnowledgeAsync(AiEndpoint endpoint, List<AiChatMessage> messages, List<object>? sourcesOut)
    {
        var lastUser = (messages.LastOrDefault(m => m.Role == "user")?.Content ?? "").Trim();
        if (lastUser.Length == 0)
        {
            return null;
        }

        // 纯问候/身份类：连规划调用都省了
        if (GreetingOnlyPattern.IsMatch(lastUser))
        {
            return null;
        }

        if (lastUser.Length <= 20 && SelfIdentityPattern.IsMatch(lastUser))
        {
            return null;
        }

        // 1) 消息里带网址：直接访问网页
        var pages = await TryFetchUrlPagesAsync(lastUser, sourcesOut);
        if (!string.IsNullOrEmpty(pages))
        {
            return pages;
        }

        // 2) 规划器：要不要搜 + 搜什么
        var (need, query) = await TryPlanWebSearchAsync(endpoint, messages);
        if (!need)
        {
            _logger.LogDebug("[AI] Web search skipped by planner.");
            return null;
        }

        return await BingSearchAsync(string.IsNullOrWhiteSpace(query) ? lastUser : query, sourcesOut);
    }

    /// <summary>
    ///     同端点极小调用（max_tokens=32、temperature=0）：判断是否需要联网
    ///     搜索，需要时同时产出搜索词。失败/无法解析一律按"不搜索"降级。
    /// </summary>
    private async Task<(bool Need, string? Query)> TryPlanWebSearchAsync(AiEndpoint endpoint, List<AiChatMessage> messages)
    {
        try
        {
            var transcript = string.Join("\n", messages.TakeLast(6).Select(m =>
                (m.Role == "user" ? "用户" : "AI") + ": " + Truncate(m.Content ?? "", 200)));
            var ask = new List<AiChatMessage>
            {
                new() { Role = "user", Content = "对话记录：\n" + transcript + "\n\n判断【用户最新消息】是否需要联网搜索；需要则按格式给出搜索词。" },
            };
            var answer = endpoint.Format == "claude"
                ? await TryClaudeAsync(endpoint, endpoint.Model, ask, WebSearchPlannerSystemPrompt)
                : await TryOpenAiAsync(endpoint, endpoint.Model, ask, WebSearchPlannerSystemPrompt, webSearch: false, useTools: false, maxTokens: 32, temperature: 0);
            if (string.IsNullOrWhiteSpace(answer))
            {
                _logger.LogDebug("[AI] Search planner returned empty; skipping web search.");
                return (false, null);
            }

            var line = answer.Trim().Split('\n')[0].Trim();
            if (line.StartsWith("YES", StringComparison.OrdinalIgnoreCase))
            {
                var query = line[3..].TrimStart('|', '｜', '：', ':', '-', '—', ' ', '\t').Trim('「', '」', '"', '“', '”', ' ', '\t');
                if (query.Length > 40)
                {
                    query = query[..40].Trim();
                }

                return (true, string.IsNullOrWhiteSpace(query) ? null : query);
            }

            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI] Search planner failed; skipping web search.");
            return (false, null);
        }
    }

    /// <summary>
    ///     从用户消息里提取网址（显式 http(s) 链接或常见顶级域裸域名），
    ///     真实抓取网页正文注入提示——这是"访问 au.fanchuanovo.cn"类问题的
    ///     正解：AI 不是"知道"网页，而是刚刚真的去读了。
    /// </summary>
    private async Task<string?> TryFetchUrlPagesAsync(string message, List<object>? sourcesOut)
    {
        var matches = UrlPattern.Matches(message);
        if (matches.Count == 0)
        {
            return null;
        }

        var urls = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var url = m.Value.Trim().TrimEnd('.', ',', '，', '。', '；', ';', '！', '?', '？', ')', '）', ']', '】', '"', '\'', '>', '：', ':');
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            if (urls.Count >= 2)
            {
                break;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && uri.Host.Contains('.')
                && !urls.Any(u => string.Equals(u, uri.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                urls.Add(uri.ToString());
            }
        }

        if (urls.Count == 0)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        var n = 0;
        foreach (var url in urls)
        {
            var (title, content) = await FetchPageTextAsync(url);
            if (content.Length == 0)
            {
                continue;
            }

            n++;
            sb.Append("\n【网页").Append(n).Append("】").Append(url);
            if (title.Length > 0)
            {
                sb.Append("（").Append(Truncate(title, 60)).Append('）');
            }

            sb.Append('\n').Append(content);
            sourcesOut?.Add(new { title = Truncate(title.Length > 0 ? title : url, 60), url });
        }

        if (n == 0)
        {
            _logger.LogWarning("[AI] URL fetch produced no readable content for {Count} url(s).", urls.Count);
            return null;
        }

        return "【已访问网页正文】（以下为刚刚真实抓取的网页内容，供回答参考）" + sb.ToString();
    }

    /// <summary>
    ///     抓取单个网页并抽取可读正文。10 秒超时、512KB 上限、只处理
    ///     文本类内容（text/*、json、xml）；失败返回空串（调用方降级）。
    /// </summary>
    private async Task<(string Title, string Content)> FetchPageTextAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.5");
            request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AI] Page fetch HTTP {Status} for {Url}", (int)response.StatusCode, Truncate(url, 80));
                return ("", "");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Length > 0
                && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("[AI] Page fetch skipped non-text content ({Type}) for {Url}", contentType, Truncate(url, 80));
                return ("", "");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length > 512 * 1024)
            {
                Array.Resize(ref bytes, 512 * 1024);
            }

            var html = DecodeHtmlBytes(bytes, response.Content.Headers.ContentType?.CharSet);
            var titleMatch = System.Text.RegularExpressions.Regex.Match(
                html, "<title[^>]*>\\s*(.*?)\\s*</title>",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var title = StripHtml(titleMatch.Success ? titleMatch.Groups[1].Value : "");
            var text = HtmlToText(html);
            if (text.Length > 2000)
            {
                text = text[..2000] + "…";
            }

            return (title, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI] Page fetch failed for {Url}", Truncate(url, 80));
            return ("", "");
        }
    }

    /// <summary>按响应字符集解码，UTF-8 严格解码失败时按 GB18030 兜底（中文站常见）。</summary>
    private static string DecodeHtmlBytes(byte[] bytes, string? charset)
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 注册失败则只用 UTF-8 / 内置编码
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
            {
                var cs = charset.Trim('"').ToLowerInvariant();
                if (cs.Contains("gb") || cs.Contains("936"))
                {
                    return System.Text.Encoding.GetEncoding("GB18030").GetString(bytes);
                }

                if (cs.Contains("big5"))
                {
                    return System.Text.Encoding.GetEncoding("big5").GetString(bytes);
                }
            }
        }
        catch
        {
            // 编码不可用时落到 UTF-8 探测
        }

        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            try
            {
                return System.Text.Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }
    }

    /// <summary>HTML → 可读正文：去 script/style/注释，块级标签转换行，解码实体，压缩空白。</summary>
    private static string HtmlToText(string html)
    {
        html = System.Text.RegularExpressions.Regex.Replace(html, "<(script|style)\\b[^>]*>.*?</\\1>", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, "<!--.*?-->", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        html = System.Text.RegularExpressions.Regex.Replace(html, "<br\\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, "</(p|div|li|tr|h[1-6]|section|article|table|ul|ol)>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        var lines = html.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, "\\s+", " ").Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>
    ///     Scrapes cn.bing.com web results (top 5) for the given query.
    ///     Returns null on any failure — callers must treat search as best-effort.
    /// </summary>
    private async Task<string?> BingSearchAsync(string? query, List<object>? sourcesOut = null)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0)
        {
            return null;
        }
        if (query.Length > 80)
        {
            query = query[..80];
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query) + "&count=5&setlang=zh-CN&mkt=zh-CN");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            // 不带语言头时 bing 可能返回错误语言的页面（实测出过德语结果）
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.5");
            request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var response = await Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AI] Bing search HTTP {Status} for: {Query}", (int)response.StatusCode, Truncate(query, 60));
                return null;
            }
            var html = await response.Content.ReadAsStringAsync(cts.Token);
            var rx = new System.Text.RegularExpressions.Regex(
                "<li class=\"b_algo\".*?<h2[^>]*>\\s*<a[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>.*?<p[^>]*>(.*?)</p>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            var sb = new System.Text.StringBuilder();
            var n = 0;
            foreach (var m in rx.Matches(html).Cast<System.Text.RegularExpressions.Match>())
            {
                var title = StripHtml(m.Groups[2].Value);
                var snip = StripHtml(m.Groups[3].Value);
                if (title.Length == 0)
                {
                    continue;
                }
                n++;
                sb.Append('\n').Append(n).Append(". ").Append(title);
                if (snip.Length > 0)
                {
                    sb.Append("：").Append(Truncate(snip, 120));
                }
                if (n >= 5)
                {
                    break;
                }
            }
            if (n == 0)
            {
                _logger.LogWarning("[AI] Bing search returned no results for: {Query}", Truncate(query, 60));
                return null;
            }

            if (sourcesOut != null)
            {
                foreach (var m2 in rx.Matches(html).Cast<System.Text.RegularExpressions.Match>().Take(5))
                {
                    var t2 = StripHtml(m2.Groups[2].Value);
                    if (t2.Length == 0) continue;
                    sourcesOut.Add(new { title = Truncate(t2, 60), url = m2.Groups[1].Value });
                }
            }

            return "【联网搜索结果】（来自 cn.bing.com，仅供参考；如据此回答请注明信息来自网络搜索）" + sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI] Bing search failed for: {Query}", Truncate(query, 60));
            return null;
        }
    }

    private static string StripHtml(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        // WebUtility.HtmlDecode 覆盖全部实体（含 &#228; 数字实体）
        return System.Net.WebUtility.HtmlDecode(s).Trim();
    }

    /// <summary>
    ///     游戏内聊天框是纯文本：把 AI 回复里的 Markdown 痕迹
    ///     （# 标题 / ** 加粗 / 反引号 / - 列表）转成简单文本标签。
    /// </summary>
    private static string SanitizeGameReply(string reply)
    {
        if (string.IsNullOrEmpty(reply))
        {
            return reply;
        }
        var lines = reply.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            l = System.Text.RegularExpressions.Regex.Replace(l, "^#{1,6}\\s*", "");
            l = System.Text.RegularExpressions.Regex.Replace(l, "^\\s*[-*•]\\s+", "· ");
            l = l.Replace("**", "").Replace("__", "").Replace("`", "");
            lines[i] = l;
        }
        return string.Join("\n", lines);
    }
    // ========== Core chat ==========

    private async Task<string?> ChatAsync(List<AiChatMessage> messages, string systemPrompt, bool inGame, bool webSearch, List<object>? sourcesOut = null)
    {
        var endpoint = GetEndpoint(inGame);
        // 开源版不内置任何 API Key：未配置时直接给出设置指引，不再请求。
        if (string.IsNullOrWhiteSpace(endpoint.Key))
        {
            _logger.LogWarning("[AI] No API key configured; AI chat disabled. Set a key in panel AI settings, config.json (WebAdmin:AiApiKey) or FMPOSTOR_AI_API_KEY.");
            return "⚠️ AI 尚未配置：请服主在面板「AI 设置」填写 API Key（或设置服务器环境变量 FMPOSTOR_AI_API_KEY），配置后即可使用。";
        }
        // 联网（Turbo-640 重做）：开关打开时按需联网——
        // 1) 消息里带网址 → 直接抓取该网页正文；2) 其余由同端点规划器小调用
        // 判断是否需要搜索并生成高质量搜索词，再抓 cn.bing.com。
        // 资料注入系统提示并附使用规范，杜绝"我无法联网"式回答。
        // 开关关闭时完全不联网。来源收集进 sourcesOut 供面板展示。
        if (webSearch && endpoint.Format != "claude")
        {
            var knowledge = await BuildWebKnowledgeAsync(endpoint, messages, sourcesOut);
            if (!string.IsNullOrEmpty(knowledge))
            {
                systemPrompt += "\n\n" + knowledge
                    + "\n\n【联网使用规范】上面是你刚刚通过内置联网工具真实抓取到的网页/搜索资料。"
                    + "回答时优先依据这些资料，涉及网络信息时注明来自网络；"
                    + "绝不要声称\"无法联网\"或\"没有最新信息\"——你刚刚已经联网了。"
                    + "若资料仍不足以回答，请如实说明缺少哪方面信息。";
            }
        }
        var configuredModel = endpoint.Model;

        // Try the configured model first; the fallback chain (glm-4.5-flash,
        // glm-4-flash) only applies to the built-in endpoint — custom endpoints
        // are used as configured.
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            candidates.Add(configuredModel);
        }

        if (!endpoint.Custom)
        {
            foreach (var fallback in FallbackModels)
            {
                if (!candidates.Contains(fallback))
                {
                    candidates.Add(fallback);
                }
            }
        }

        foreach (var model in candidates)
        {
            var reply = await TryChatOnceAsync(model, messages, systemPrompt, inGame, false);
            if (reply != null)
            {
                if (!model.Equals(configuredModel, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[AI] Model {Model} unavailable, fell back to {Fallback}.", configuredModel, model);
                }

                return reply;
            }

            _logger.LogWarning("[AI] Model {Model} failed, trying next model...", model);
        }

        return null;
    }

    private async Task<string?> TryChatOnceAsync(string model, List<AiChatMessage> messages, string systemPrompt, bool inGame, bool webSearch, bool useTools = true)
    {
        var endpoint = GetEndpoint(inGame);
        // 运行时检测当前生效的模型/服务商，把真实身份写进系统提示：
        // 玩家和管理员问"你是谁"时，AI 能如实报出厂商与模型名。
        systemPrompt = BuildIdentityLine(endpoint, model, inGame) + "\n" + systemPrompt;
        return endpoint.Format == "claude"
            ? await TryClaudeAsync(endpoint, model, messages, systemPrompt)
            : await TryOpenAiAsync(endpoint, model, messages, systemPrompt, webSearch, useTools);
    }

    /// <summary>
    ///     Detects the AI vendor from the effective base URL / model name and
    ///     returns a system-prompt line declaring the model's real identity.
    ///     Works for custom endpoints too (detection is per-call, not static).
    /// </summary>
    private static string BuildIdentityLine(AiEndpoint endpoint, string model, bool inGame)
    {
        var url = (endpoint.BaseUrl ?? "").ToLowerInvariant();
        var m = (model ?? "").ToLowerInvariant();

        string vendor;
        if (url.Contains("bigmodel.cn") || m.StartsWith("glm") || (!endpoint.Custom && url.Contains("open.bigmodel.cn")))
            vendor = "智谱AI（Z.ai）";
        else if (url.Contains("api.deepseek.com") || m.StartsWith("deepseek"))
            vendor = "深度求索（DeepSeek）";
        else if (url.Contains("dashscope.aliyuncs") || m.StartsWith("qwen"))
            vendor = "阿里巴巴（通义千问）";
        else if (url.Contains("moonshot") || m.StartsWith("kimi"))
            vendor = "月之暗面（Kimi）";
        else if (url.Contains("anthropic") || endpoint.Format == "claude" || m.StartsWith("claude"))
            vendor = "Anthropic";
        else if (url.Contains("api.openai.com") || m.StartsWith("gpt") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4"))
            vendor = "OpenAI";
        else if (url.Contains("generativelanguage.googleapis.com") || m.StartsWith("gemini"))
            vendor = "Google";
        else if (url.Contains("x.ai") || m.StartsWith("grok"))
            vendor = "xAI";
        else if (url.Contains("openrouter.ai"))
            vendor = "OpenRouter 聚合平台（模型：" + model + "）";
        else if (url.Contains("siliconflow"))
            vendor = "硅基流动";
        else if (url.Contains("minimax") || m.StartsWith("minimax") || m.StartsWith("abab"))
            vendor = "MiniMax";
        else
            vendor = "未知服务商（自定义接入点）";

        var role = inGame
            ? "Among Us 游戏内 AI 助手（玩家通过 /aichat 与你对话）"
            : "Among Us 服务器管理面板 AI 助手";
        return "【模型身份】你是由 " + vendor + " 训练的 " + model + " 模型，当前以" + role +
               "的身份运行。当用户询问你是谁、哪家公司、什么模型时，如实回答以上身份，不要虚构或冒充其他模型。";
    }

    /// <summary>
    ///     OpenAI-compatible chat completions (Zhipu / DeepSeek / Moonshot /
    ///     OpenAI ...): POST {baseUrl}/chat/completions with Bearer auth,
    ///     response choices[0].message.content.
    /// </summary>
    private async Task<string?> TryOpenAiAsync(AiEndpoint endpoint, string model, List<AiChatMessage> messages, string systemPrompt, bool webSearch, bool useTools, int maxTokens = 512, double temperature = 0.6)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new List<AiChatMessage>
            {
                new() { Role = "system", Content = systemPrompt },
            },
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
        };

        ((List<AiChatMessage>)payload["messages"]!).AddRange(messages);

        if (webSearch && useTools)
        {
            payload["tools"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "web_search",
                    ["web_search"] = new Dictionary<string, object> { ["enable"] = true },
                },
            };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.BaseUrl + "/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Key);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AI] Model {Model} API error {Status}: {Body}",
                    model, (int)response.StatusCode, Truncate(body, 300));

                // Some models / accounts do not support the web_search tool:
                // retry the same model once without tools before giving up.
                if (webSearch && useTools && (int)response.StatusCode is 400 or 422)
                {
                    return await TryOpenAiAsync(endpoint, model, messages, systemPrompt, webSearch, useTools: false);
                }

                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    return content.GetString();
                }
            }

            _logger.LogWarning("[AI] Model {Model} unexpected API response: {Body}", model, Truncate(body, 300));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI] Model {Model} chat request failed.", model);
            return null;
        }
    }

    /// <summary>
    ///     Anthropic Claude Messages API: POST {baseUrl}/messages with
    ///     x-api-key + anthropic-version headers, system prompt as a top-level
    ///     field, response content[0].text.
    /// </summary>
    private async Task<string?> TryClaudeAsync(AiEndpoint endpoint, string model, List<AiChatMessage> messages, string systemPrompt)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 512,
            ["temperature"] = 0.6,
            ["system"] = systemPrompt,
            ["messages"] = messages,
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.BaseUrl + "/messages");
            request.Headers.TryAddWithoutValidation("x-api-key", endpoint.Key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AI] Claude model {Model} API error {Status}: {Body}",
                    model, (int)response.StatusCode, Truncate(body, 300));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("content", out var contentArr) && contentArr.GetArrayLength() > 0)
            {
                var first = contentArr[0];
                if (first.TryGetProperty("text", out var text))
                {
                    return text.GetString();
                }
            }

            _logger.LogWarning("[AI] Claude model {Model} unexpected API response: {Body}", model, Truncate(body, 300));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI] Claude model {Model} chat request failed.", model);
            return null;
        }
    }

    /// <summary>
    ///     In-game /aichat: private conversation between a player and the AI.
    ///     The AI is an Among Us guide/expert: it may chat and give real game
    ///     tips, uses web search when needed, and must NEVER fabricate in-game
    ///     state. It only knows the current time, room code and player names.
    /// </summary>
    public async Task<(bool Ok, string? Reply, string? Error)> GameChatAsync(string playerKey, string playerName, string friendCode, string puid, string gameCode, IReadOnlyList<string> playerNames, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return (false, null, "消息不能为空。用法: /aichat 内容");
        }

        if (!GetSettings().InGameChatEnabled)
        {
            return (false, null, "AI 聊天功能未开启，请联系管理员。");
        }

        var now = DateTime.Now;
        var systemPrompt = GetSettings().SystemPrompt
            + $"\n当前时间：{now:yyyy-MM-dd HH:mm:ss}（服务器本地时间）\n"
            + $"当前房间：{gameCode}\n"
            + $"本房间玩家：{(playerNames.Count > 0 ? string.Join("、", playerNames) : "无")}\n"
            + "即使对局正在进行，你也无法知道任何对局信息（谁是什么角色、任务进度、投票、击杀等），"
            + "玩家问到时如实说明，并给出 Among Us 通用攻略建议。"
            + "\n输出格式要求：游戏内聊天框是纯文本，不支持 Markdown。禁止使用 **加粗**、# 标题、表格、反引号等符号；"
            + "需要分段时使用【小标题】、换行和 1. 2. 3. 序号；回复总长控制在 400 字以内。";

        var context = GetGameContext(playerKey);
        List<AiChatMessage> snapshot;
        lock (context)
        {
            AppendContext(context, "user", message, GetSettings().MaxContextMessages);
            snapshot = context.ToList();
        }

        // In-game chat uses the in-game endpoint (custom key/url/model if
        // configured) and enables web search on the built-in endpoint.
        var reply = await ChatAsync(snapshot, systemPrompt, inGame: true, webSearch: GetSettings().WebSearchEnabled);

        if (string.IsNullOrEmpty(reply))
        {
            return (false, null, "AI 模型访问量大，请稍后再试。");
        }

        reply = SanitizeGameReply(reply);

        lock (context)
        {
            AppendContext(context, "assistant", reply, GetSettings().MaxContextMessages);
        }

        AddRecord(new AiChatRecord
        {
            PlayerName = playerName,
            FriendCode = friendCode,
            Puid = puid,
            GameCode = gameCode,
            FromInGame = true,
            Messages = new List<AiChatMessage>
            {
                new() { Role = "user", Content = message },
                new() { Role = "assistant", Content = reply },
            },
        });

        _logger.LogDebug("[AI] In-game chat {Name} in {Game}: \"{Msg}\" -> \"{Reply}\"",
            playerName, gameCode, Truncate(message, 60), Truncate(reply, 60));
        return (true, reply, null);
    }

    /// <summary>
    ///     Panel chat: admin talks to the AI, optionally asking it to analyze
    ///     server data (behavior logs, stats, footprints, replays, chat logs, reports).
    /// </summary>
    public async Task<(bool Ok, string? Reply, string? Error)> PanelChatAsync(
        string message,
        string? context = null,
        string? panelUser = null,
        string kind = "chat",
        bool useContext = true,
        List<object>? sourcesOut = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return (false, null, "消息不能为空。");
        }

        var now = DateTime.Now;
        var systemPrompt = GetSettings().SystemPrompt
            + $"\n当前时间：{now:yyyy-MM-dd HH:mm:ss}（服务器本地时间）\n"
            + "你是管理员的管理助手。如果管理员要求分析服务器数据，请基于提供的数据客观回答；数据不足时明确说明。"
            + "如果管理员问及其他话题（包括 Among Us 玩法攻略），以 AU 攻略专家身份正常回答。"
            + "\n注意：当前是管理面板对话，不涉及具体房间，没有房间号和玩家名单上下文，不要提及或猜测它们。";

        var extraData = context != null ? BuildAnalysisData(context) : null;
        if (!string.IsNullOrEmpty(extraData))
        {
            systemPrompt += "\n\n【管理员请求分析的数据】\n" + extraData;
        }

        List<AiChatMessage> snapshot;
        if (useContext)
        {
            AppendContext(_panelContext, "user", message, GetSettings().MaxContextMessages);
            snapshot = _panelContext.ToList();
        }
        else
        {
            // 一次性对话（定时任务等后台调用）：不读写面板共享上下文，
            // 避免计划的自动提示词污染管理员正在进行的对话记忆。
            snapshot = new List<AiChatMessage> { new() { Role = "user", Content = message } };
        }
        var reply = await ChatAsync(snapshot, systemPrompt, inGame: false, webSearch: GetSettings().WebSearchEnabled, sourcesOut);

        if (string.IsNullOrEmpty(reply))
        {
            return (false, null, "AI 模型访问量大，请稍后再试。");
        }

        if (useContext)
        {
            AppendContext(_panelContext, "assistant", reply, GetSettings().MaxContextMessages);
        }

        var recordKind = kind == "agent" ? "agent" : kind == "schedule" ? "schedule" : "chat";
        AddRecord(new AiChatRecord
        {
            PlayerName = "WebAdmin",
            FromInGame = false,
            PanelUser = (panelUser ?? "").Trim(),
            Kind = recordKind,
            Messages = new List<AiChatMessage>
            {
                new() { Role = "user", Content = message },
                new() { Role = "assistant", Content = reply },
            },
        });

        _logger.LogDebug("[AI] Panel chat: \"{Msg}\" -> \"{Reply}\"", Truncate(message, 60), Truncate(reply, 60));
        return (true, reply, null);
    }

    public Task<bool> ClearPanelContextAsync()
    {
        lock (_lock)
        {
            _panelContext.Clear();
            return Task.FromResult(true);
        }
    }

    // ========== Analysis data builder ==========

    private string BuildAnalysisData(string context)
    {
        try
        {
            switch (context)
            {
                case "full":
                {
                    // 定时 AI 任务用：一次性带上主要运营数据快照，
                    // 否则"总结今日数据"类的提示词拿不到任何数据。
                    var sb = new System.Text.StringBuilder();
                    var logs = BuildAnalysisData("logs");
                    if (logs.Length > 0) sb.Append(logs).Append("\n\n");
                    var stats = BuildAnalysisData("stats");
                    if (stats.Length > 0) sb.Append(stats).Append("\n\n");
                    var foot = BuildAnalysisData("footprints");
                    if (foot.Length > 0) sb.Append(foot).Append("\n\n");
                    var rep = BuildAnalysisData("reports");
                    if (rep.Length > 0) sb.Append(rep).Append("\n\n");
                    var chat = BuildAnalysisData("chats");
                    if (chat.Length > 0) sb.Append(chat);
                    return sb.ToString();
                }

                case "logs":
                {
                    var logs = _playerLogs.GetLogsAsync(limit: 80).GetAwaiter().GetResult();
                    return "最近行为日志（最多80条）：\n" + string.Join("\n", logs.Select(l =>
                        $"{l.Time:HH:mm:ss} [{l.Type}] {l.PlayerName}({l.FriendCode}) 房间{l.GameCode} {l.Detail}"));
                }

                case "stats":
                {
                    var stats = _playerStats.GetAllAsync().GetAwaiter().GetResult().Take(30).ToList();
                    return "玩家战绩 Top30（按场次）：\n" + string.Join("\n", stats.Select(s =>
                        $"{s.FriendCode} {s.LastKnownName} 场次{s.GamesPlayed} 胜{s.Wins} 负{s.Losses} 内鬼胜{s.ImpostorWins} 击杀{s.Kills} 死亡{s.Deaths} 任务{s.TasksCompleted} 被投出{s.TimesExiled}"));
                }

                case "footprints":
                {
                    var fps = _footprints.GetListAsync(limit: 30).GetAwaiter().GetResult();
                    return "最近活跃玩家足迹（最多30人）：\n" + string.Join("\n", fps.Select(f =>
                        $"{f.Name}({f.FriendCode}) 累计在线{Math.Round(f.TotalOnlineSeconds / 3600.0, 1)}小时 会话{f.Sessions.Count}次 IP:{string.Join(",", f.Ips.Take(3))} 最近上线{f.LastSeen:MM-dd HH:mm}"));
                }

                case "replays":
                {
                    var replays = _replays.GetListAsync().GetAwaiter().GetResult().Take(20).ToList();
                    return "最近对局复盘（最多20局）：\n" + string.Join("\n", replays.Select(r =>
                        $"房间{r.GameCode} {r.StartedAt:MM-dd HH:mm} 地图{r.Map} 时长{r.DurationSeconds}秒 结果{r.Result} 玩家:{string.Join(",", r.Players.Select(p => p.Name + (p.IsImpostor ? "(内鬼)" : "")))}"));
                }

                case "chats":
                {
                    var chats = _chatService.GetRecentForAi();
                    return "最近聊天记录（最多60条）：\n" + string.Join("\n", chats);
                }

                case "reports":
                {
                    var reports = _reports.GetAllAsync().GetAwaiter().GetResult().Take(30).ToList();
                    return "最近举报（最多30条）：\n" + string.Join("\n", reports.Select(r =>
                        $"#{r.Id} {r.Time:MM-dd HH:mm} 举报者{r.ReporterName}({r.ReporterFriendCode}) 房间{r.GameCode} 状态{r.Status} 描述:{r.Description}"));
                }

                default:
                    return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AI] Failed to build analysis data for {Context}", context);
            return string.Empty;
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        return s.Length <= max ? s : s[..max] + "...";
    }
}