using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class WelcomeStore
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Single template list used for BOTH new and returning players
    ///     (new/returning distinction was removed). Supports {PLAYER_NAME}
    ///     (alias {Name}) / {Room} / {LAST_LOGIN_TIME} / {TIME_SINCE_LAST_LOGIN}
    ///     / {PLAY_TIME}.
    /// </summary>
    /// <remarks>
    ///     文案支持 Unity 富文本(&lt;color=#RRGGBB&gt;、&lt;size&gt;), 所以这里带颜色。
    ///     只做欢迎, 不夹带功能说明 —— /m off 之类的开关提示统一放在游戏内
    ///     /help 菜单里, 需要时玩家自己能查, 不必在每次进房时塞一条长文本。
    /// </remarks>
    public List<string> Messages { get; set; } = new()
    {
        WelcomeService.DefaultMessage,
    };

    /// <summary>
    ///     进房后等多少秒再发欢迎语。刚生成身体时聊天框还在初始化, 立刻发容易被刷掉或看不到。
    /// </summary>
    public int DelaySeconds { get; set; } = 3;

    /// <summary>
    ///     第一次来本服的玩家用的欢迎语(他们没有"上次登录"可显示)。留空 = 与老玩家共用上面的列表。
    /// </summary>
    public string NewPlayerMessage { get; set; } = WelcomeService.DefaultNewPlayerMessage;
}

/// <summary>
///     Welcome messages sent privately to players when they join a room.
///     Editable from the web panel; supports {Name} and {Room} placeholders.
/// </summary>
public class WelcomeService
{
    private readonly string _filePath;
    private readonly ILogger<WelcomeService> _logger;
    private readonly object _lock = new();
    private WelcomeStore _store;
    private long _fileVersion = -1;
    private int _lastIndex = -1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public WelcomeService(IOptions<WebAdminConfig> config, ILogger<WelcomeService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.WelcomeFile);
        _store = new WelcomeStore();
        Load();
    }

    /// <summary>
    ///     The single template this service shipped with before the list gained the
    ///     coloured variant. Kept only so an untouched install can be upgraded once.
    /// </summary>
    private const string LegacyDefaultMessage = "欢迎 {PLAYER_NAME} 加入 {Room} 房间，祝游戏愉快！";

    /// <summary>上一版的带颜色默认欢迎语。同样只用于识别"服主从没改过"的列表并升级。</summary>
    private const string PreviousColouredDefaultMessage =
        "<color=#FFD700>欢迎</color> <color=#4ADE80>{PLAYER_NAME}</color> 加入 <color=#60A5FA>{Room}</color> 房间，祝游戏愉快！";

    /// <summary>当前默认欢迎语(多行, Unity 富文本)。</summary>
    internal const string DefaultMessage =
        "<color=#FF00FF>★ </color><color=#FF0000>帆船</color><color=#FF8800>AmongUs</color><color=#660077>服务器</color><color=#FF00FF> ★\n"
        + "欢迎归来，</color><color=#FFFF00>{PLAYER_NAME}</color><color=#FF00FF>！</color>\n"
        + "\n"
        + "<color=#00FF00>▸ 上次登录：{LAST_LOGIN_TIME}</color>\n"
        + "<color=#FFAA00>▸ 距上次登录：{TIME_SINCE_LAST_LOGIN}</color>\n"
        + "<color=#FF44FF>▸ 累计游玩：{PLAY_TIME}</color>";

    /// <summary>默认的新玩家欢迎语。</summary>
    internal const string DefaultNewPlayerMessage =
        "<color=#FF0000>欢迎</color><color=#00FF00>{PLAYER_NAME}</color><color=#0000FF>加入帆船AmongUs服务器！</color>";

    /// <summary>欢迎语延迟的上限(秒)。</summary>
    private const int MaxDelaySeconds = 30;

    /// <summary>
    ///     曾经被并入欢迎语列表的那条 /m 提示。它现在归 /help 菜单, 所以检测到就退回。
    /// </summary>
    private const string RetiredBroadcastHint = "/m off";

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
                var data = JsonSerializer.Deserialize<WelcomeStore>(json, JsonOptions);
                if (data != null)
                {
                    _store = data;
                    _store.Messages = (_store.Messages ?? new List<string>())
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .ToList();

                    // 一次性升级/回退, 只针对"从没被服主改过"的列表:
                    //   · 旧的无颜色默认 → 换成带颜色的新默认
                    //   · 曾经多出来的 /m 说明 → 移除(那条提示归 /help 菜单)
                    // 服主一旦自己增删过, 列表就与这两种形态都不符, 不会被覆盖。
                    // {Name} 是 {PLAYER_NAME} 的旧写法, 比较前统一成新写法 —— 旧安装里存的默认值
                    // 常常是 {Name} 版本, 不统一的话认不出来, 就永远停在旧默认上。
                    var only = _store.Messages.Count == 1
                        ? _store.Messages[0].Trim().Replace("{Name}", "{PLAYER_NAME}", StringComparison.Ordinal)
                        : null;
                    var isLegacyDefault = only != null
                                          && (only == LegacyDefaultMessage || only == PreviousColouredDefaultMessage);

                    var carriesRetiredHint = _store.Messages.Any(m => m.Contains(RetiredBroadcastHint, StringComparison.Ordinal));

                    if (isLegacyDefault || carriesRetiredHint)
                    {
                        _store.Messages = _store.Messages
                            .Where(m => !m.Contains(RetiredBroadcastHint, StringComparison.Ordinal))
                            .ToList();

                        if (_store.Messages.Count == 0 || isLegacyDefault)
                        {
                            _store.Messages = new WelcomeStore().Messages;
                        }

                        Save();
                        _logger.LogInformation(
                            "[Welcome] Normalised default messages to the coloured set ({Count} message(s)); the /m hint lives in /help now.",
                            _store.Messages.Count);
                    }

                    _fileVersion = GetFileVersion();
                    _logger.LogInformation("[Welcome] Loaded {Count} welcome message(s) from {File} (enabled={Enabled}).",
                        _store.Messages.Count, _filePath, _store.Enabled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Welcome] Failed to load {File}, using defaults.", _filePath);
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

    public void Save()
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
                _logger.LogWarning(ex, "[Welcome] Failed to save {File}.", _filePath);
            }
        }
    }

    /// <summary>
    ///     Picks a welcome message (round-robin over the single template list)
    ///     with placeholders replaced, or null when disabled / no messages.
    ///     Placeholders: {PLAYER_NAME} (alias {Name}) {Room} {LAST_LOGIN_TIME}
    ///     {TIME_SINCE_LAST_LOGIN} {PLAY_TIME}.
    ///     第一次来的玩家用 NewPlayerMessage(非空时); 其余情况用模板列表轮流发送。
    /// </summary>
    public string? Pick(
        string playerName,
        string roomCode,
        PlayerTimeEntry? playerData = null,
        bool firstVisit = false,
        HitokotoService.Quote? hitokoto = null,
        bool appendWhenMissing = false)
    {
        lock (_lock)
        {
            TryReload();
            if (!_store.Enabled)
            {
                return null;
            }

            string template;

            if (firstVisit && !string.IsNullOrWhiteSpace(_store.NewPlayerMessage))
            {
                template = _store.NewPlayerMessage;
            }
            else
            {
                var templates = _store.Messages
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();
                if (templates.Count == 0)
                {
                    return null;
                }

                // Round-robin over the list so every message gets used.
                _lastIndex = (_lastIndex + 1) % templates.Count;
                template = templates[_lastIndex];
            }

            var lastLogin = playerData?.LastLoginTime;
            var playTimeMinutes = playerData?.TotalPlayTimeMinutes ?? 0;
            var timeSince = lastLogin.HasValue ? DateTime.UtcNow - lastLogin.Value : TimeSpan.Zero;

            // Strip rich-text brackets from substituted player names so a crafted
            // name cannot inject Unity rich-text formatting into announcements.
            var safeName = (playerName ?? string.Empty).Replace('<', '‹').Replace('>', '›');

            template = ApplyHitokoto(template, hitokoto, appendWhenMissing);

            return template
                .Replace("{Name}", safeName, StringComparison.Ordinal)
                .Replace("{Room}", roomCode, StringComparison.Ordinal)
                .Replace("{PLAYER_NAME}", safeName, StringComparison.Ordinal)
                .Replace("{LAST_LOGIN_TIME}", lastLogin?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知", StringComparison.Ordinal)
                .Replace("{TIME_SINCE_LAST_LOGIN}", FormatDuration(timeSince), StringComparison.Ordinal)
                .Replace("{PLAY_TIME}", FormatMinutes(playTimeMinutes), StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     自动追加时使用的一言两行(句子 + 出处), 风格与原来的"每小时漂流瓶"一致。
    /// </summary>
    internal const string HitokotoLines = "<color=#FF0000>{HITOKOTO}</color>\n<color=#0000CC>--------{HITOKOTO_FROM}</color>";

    /// <summary>句子占位符: 新写法 {HITOKOTO}, 旧写法 &lt;YY&gt;。</summary>
    private static readonly string[] TextTokens = { "{HITOKOTO}", "<YY>" };

    /// <summary>出处占位符: 新写法 {HITOKOTO_FROM}, 旧写法 &lt;ZZ&gt;。</summary>
    private static readonly string[] SourceTokens = { "{HITOKOTO_FROM}", "<ZZ>" };

    /// <summary>
    ///     Fills the quote placeholders, appending the quote lines when asked to.
    /// </summary>
    /// <remarks>
    ///     · 没有句子(功能关闭)时, 含占位符的行整行去掉, 不会留下空壳;
    ///     · 句子有但出处为空时, 只含出处占位符的行去掉, 避免一行孤零零的"--------"。
    /// </remarks>
    internal static string ApplyHitokoto(string template, HitokotoService.Quote? quote, bool append)
    {
        static bool HasAny(string s, string[] tokens) => tokens.Any(t => s.Contains(t, StringComparison.Ordinal));

        if (quote != null && append && !HasAny(template, TextTokens) && !HasAny(template, SourceTokens))
        {
            template = template.TrimEnd() + "\n" + HitokotoLines;
        }

        if (!HasAny(template, TextTokens) && !HasAny(template, SourceTokens))
        {
            return template;
        }

        var lines = template.Split('\n').Where(line =>
        {
            var hasText = HasAny(line, TextTokens);
            var hasSource = HasAny(line, SourceTokens);
            if (quote == null)
            {
                return !hasText && !hasSource;
            }

            return !(hasSource && !hasText && string.IsNullOrEmpty(quote.Source));
        });

        var result = string.Join('\n', lines).TrimEnd();
        if (quote == null)
        {
            return result;
        }

        foreach (var token in TextTokens)
        {
            result = result.Replace(token, quote.Text, StringComparison.Ordinal);
        }

        foreach (var token in SourceTokens)
        {
            result = result.Replace(token, quote.Source, StringComparison.Ordinal);
        }

        return result;
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes <= 0)
        {
            return "0分钟";
        }

        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0 ? $"{h}小时{m}分钟" : $"{m}分钟";
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero)
        {
            return "刚刚";
        }

        if (ts.TotalDays >= 1)
        {
            return $"{(int)ts.TotalDays}天前";
        }

        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}小时前";
        }

        if (ts.TotalMinutes >= 1)
        {
            return $"{(int)ts.TotalMinutes}分钟前";
        }

        return "刚刚";
    }

    public WelcomeStore GetSnapshot()
    {
        lock (_lock)
        {
            TryReload();
            return new WelcomeStore
            {
                Enabled = _store.Enabled,
                Messages = _store.Messages.ToList(),
                DelaySeconds = Math.Clamp(_store.DelaySeconds, 0, MaxDelaySeconds),
                NewPlayerMessage = _store.NewPlayerMessage ?? string.Empty,
            };
        }
    }

    public void AddMessage(string message)
    {
        lock (_lock)
        {
            TryReload();
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _store.Messages.Add(message.Trim());
            Save();
        }
    }

    public void RemoveMessageAt(int index)
    {
        lock (_lock)
        {
            TryReload();
            if (index >= 0 && index < _store.Messages.Count)
            {
                _store.Messages.RemoveAt(index);
                Save();
            }
        }
    }

    /// <summary>当前的欢迎语延迟(秒), 已限制在 0~30。</summary>
    public int DelaySeconds
    {
        get
        {
            lock (_lock)
            {
                TryReload();
                return Math.Clamp(_store.DelaySeconds, 0, MaxDelaySeconds);
            }
        }
    }

    public void UpdateSettings(bool? enabled, int? delaySeconds = null, string? newPlayerMessage = null)
    {
        lock (_lock)
        {
            TryReload();
            if (enabled.HasValue)
            {
                _store.Enabled = enabled.Value;
            }

            if (newPlayerMessage != null)
            {
                _store.NewPlayerMessage = newPlayerMessage.Trim();
            }

            if (delaySeconds.HasValue)
            {
                _store.DelaySeconds = Math.Clamp(delaySeconds.Value, 0, MaxDelaySeconds);
            }

            Save();
        }
    }
}