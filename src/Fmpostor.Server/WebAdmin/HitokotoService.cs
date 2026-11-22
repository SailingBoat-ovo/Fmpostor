using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

/// <summary>One custom quote with its source.</summary>
public sealed class HitokotoEntry
{
    public string Text { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;
}

/// <summary>Persisted settings of the quote (一言) feature.</summary>
public sealed class HitokotoStore
{
    public bool Enabled { get; set; } = true;

    /// <summary>全服共用的那句一言多久换一次(秒)。默认一小时 —— "每小时的漂流瓶"。</summary>
    public int UpdateIntervalSeconds { get; set; } = 3600;

    /// <summary>一言接口。留空 = 默认接口。</summary>
    public string ApiUrl { get; set; } = HitokotoService.DefaultUrl;

    /// <summary>每次发欢迎语时, 改用自定义一言的概率(百分比, 0~100)。</summary>
    public double CustomChance { get; set; } = 5;

    /// <summary>自定义一言(句子 + 出处)。</summary>
    public List<HitokotoEntry> CustomList { get; set; } = new()
    {
        new() { Text = "这是一条自定义一言", From = "自定义" },
        new() { Text = HitokotoService.BuiltinText, From = HitokotoService.BuiltinFrom },
        new() { Text = "Hello World", From = "开发者" },
    };

    /// <summary>欢迎语里没有写一言占位符时, 是否自动在末尾加上一言。</summary>
    public bool AppendToWelcome { get; set; } = true;
}

/// <summary>
///     The quote (一言) feature used by welcome messages.
/// </summary>
/// <remarks>
///     行为:
///       · 全服共用一句"当前一言", 按 UpdateIntervalSeconds 定时从接口刷新(默认每小时一次);
///         刷新失败就继续用上一句;
///       · 每次发欢迎语时, 按 CustomChance 的概率改用一条自定义一言(随机挑一条);
///         内置的那条"帆船"一言始终在自定义池里;
///       · 欢迎语里用 {HITOKOTO} / {HITOKOTO_FROM}, 旧写法 &lt;YY&gt; / &lt;ZZ&gt; 同样有效;
///       · 还没取到过接口一言时(刚开服、接口不通), 用自定义池兜底, 不会出现空行;
///       · 句子会进游戏聊天框, 所以去掉尖括号、合并换行、限制长度;
///       · 首次启动时若旁边有旧的 Fanchuan.Hitokoto.Config.json, 自动把其中的设置导入。
/// </remarks>
public sealed class HitokotoService : IHostedService, IDisposable
{
    public const string DefaultUrl = "https://v1.hitokoto.cn/";

    internal const string BuiltinText = "欢迎使用帆船修改的自定义一言功能插件";
    internal const string BuiltinFrom = "帆船";

    private const string LegacyConfigFile = "Fanchuan.Hitokoto.Config.json";
    private const int MinIntervalSeconds = 60;
    private const int MaxTextLength = 80;
    private const int MaxSourceLength = 40;
    private const int MaxCustomEntries = 500;

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger<HitokotoService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private HitokotoStore _store = new();
    private Quote? _current;
    private DateTime _currentAt;
    private Timer? _timer;
    private int _fetching;

    public HitokotoService(IOptions<WebAdminConfig> config, ILogger<HitokotoService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.HitokotoFile);
        Load();
    }

    /// <summary>一句话和它的出处(出处可为空)。</summary>
    public sealed record Quote(string Text, string Source);

    // ================= lifecycle =================

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RestartTimer();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void RestartTimer()
    {
        int interval;
        bool enabled;
        lock (_lock)
        {
            enabled = _store.Enabled;
            interval = Math.Max(MinIntervalSeconds, _store.UpdateIntervalSeconds);
        }

        _timer?.Dispose();
        _timer = null;

        if (!enabled)
        {
            return;
        }

        // 立即取一次, 之后按间隔刷新。
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(interval));
    }

    // ================= quotes =================

    /// <summary>
    ///     The quote to use for one welcome message, or null when the feature is off.
    /// </summary>
    public Quote? Pick()
    {
        lock (_lock)
        {
            if (!_store.Enabled)
            {
                return null;
            }

            var pool = CustomPool();
            var useCustom = _store.CustomChance > 0 && Random.Shared.NextDouble() * 100.0 < _store.CustomChance;

            if (!useCustom && _current != null)
            {
                return _current;
            }

            // 抽中自定义, 或者接口一言还没取到: 从自定义池里挑一条。
            var entry = pool[Random.Shared.Next(pool.Count)];
            return new Quote(entry.Text, entry.From);
        }
    }

    /// <summary>Whether welcome messages without a placeholder should get the quote appended.</summary>
    public bool AppendToWelcome
    {
        get
        {
            lock (_lock)
            {
                return _store.Enabled && _store.AppendToWelcome;
            }
        }
    }

    /// <summary>当前的接口一言与取到的时间(面板展示用)。</summary>
    public (Quote? Quote, DateTime FetchedAtUtc) Current
    {
        get
        {
            lock (_lock)
            {
                return (_current, _currentAt);
            }
        }
    }

    /// <summary>
    ///     Fetches a new shared quote now. Returns the new quote, or null when the fetch failed.
    /// </summary>
    public async Task<Quote?> RefreshAsync()
    {
        if (Interlocked.Exchange(ref _fetching, 1) == 1)
        {
            return null;
        }

        string url;
        lock (_lock)
        {
            url = string.IsNullOrWhiteSpace(_store.ApiUrl) ? DefaultUrl : _store.ApiUrl;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain;q=0.9, */*;q=0.5");

            using var response = await Client.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Hitokoto] {Url} returned HTTP {Status}; keeping the previous quote.", url, (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var quote = Parse(body);
            if (quote == null)
            {
                _logger.LogWarning("[Hitokoto] {Url} returned no usable quote; keeping the previous quote.", url);
                return null;
            }

            lock (_lock)
            {
                _current = quote;
                _currentAt = DateTime.UtcNow;
            }

            _logger.LogInformation("[Hitokoto] Quote updated: {Text} —— {From}", quote.Text, quote.Source);
            return quote;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Hitokoto] Fetch from {Url} failed: {Error}; keeping the previous quote.", url, ex.Message);
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    /// <summary>自定义池: 设置里的条目, 外加始终存在的内置那条。</summary>
    private List<HitokotoEntry> CustomPool()
    {
        var pool = _store.CustomList
            .Where(e => !string.IsNullOrWhiteSpace(e.Text))
            .Select(e => new HitokotoEntry { Text = Clean(e.Text, MaxTextLength), From = Clean(e.From, MaxSourceLength) })
            .ToList();

        if (!pool.Any(e => e.Text == BuiltinText))
        {
            pool.Add(new HitokotoEntry { Text = BuiltinText, From = BuiltinFrom });
        }

        return pool;
    }

    // ================= settings =================

    public HitokotoStore GetSnapshot()
    {
        lock (_lock)
        {
            return new HitokotoStore
            {
                Enabled = _store.Enabled,
                UpdateIntervalSeconds = _store.UpdateIntervalSeconds,
                ApiUrl = string.IsNullOrWhiteSpace(_store.ApiUrl) ? DefaultUrl : _store.ApiUrl,
                CustomChance = _store.CustomChance,
                CustomList = _store.CustomList.Select(e => new HitokotoEntry { Text = e.Text, From = e.From }).ToList(),
                AppendToWelcome = _store.AppendToWelcome,
            };
        }
    }

    /// <summary>Updates the given fields; null leaves a field unchanged.</summary>
    public void Update(
        bool? enabled = null,
        int? updateIntervalSeconds = null,
        string? apiUrl = null,
        double? customChance = null,
        List<HitokotoEntry>? customList = null,
        bool? appendToWelcome = null)
    {
        var restart = false;
        lock (_lock)
        {
            if (enabled.HasValue && enabled.Value != _store.Enabled)
            {
                _store.Enabled = enabled.Value;
                restart = true;
            }

            if (updateIntervalSeconds.HasValue)
            {
                var v = Math.Clamp(updateIntervalSeconds.Value, MinIntervalSeconds, 7 * 24 * 3600);
                restart |= v != _store.UpdateIntervalSeconds;
                _store.UpdateIntervalSeconds = v;
            }

            if (apiUrl != null)
            {
                // 只接受 http/https 的绝对地址; 留空或不合法 = 默认接口。
                var trimmed = apiUrl.Trim();
                var url = Uri.TryCreate(trimmed, UriKind.Absolute, out var u)
                          && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
                    ? trimmed
                    : DefaultUrl;
                restart |= url != _store.ApiUrl;
                _store.ApiUrl = url;
            }

            if (customChance.HasValue && !double.IsNaN(customChance.Value))
            {
                _store.CustomChance = Math.Clamp(customChance.Value, 0, 100);
            }

            if (customList != null)
            {
                _store.CustomList = customList
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Text))
                    .Take(MaxCustomEntries)
                    .Select(e => new HitokotoEntry { Text = e.Text.Trim(), From = (e.From ?? string.Empty).Trim() })
                    .ToList();
            }

            if (appendToWelcome.HasValue)
            {
                _store.AppendToWelcome = appendToWelcome.Value;
            }

            Save();
        }

        if (restart)
        {
            RestartTimer();
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    _store = JsonSerializer.Deserialize<HitokotoStore>(File.ReadAllText(_filePath), JsonOptions) ?? new HitokotoStore();
                    _store.CustomList ??= new List<HitokotoEntry>();
                    return;
                }

                if (TryImportLegacy(out var imported))
                {
                    _store = imported;
                    _logger.LogInformation("[Hitokoto] Imported settings from {File}.", LegacyConfigFile);
                }

                Save();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Hitokoto] Failed to load {File}, using defaults.", _filePath);
                _store = new HitokotoStore();
            }
        }
    }

    /// <summary>旧版一言配置的格式: 句子与出处是两个平行列表, 概率是百分比。</summary>
    private sealed class LegacyConfig
    {
        public bool Enabled { get; set; } = true;

        public int UpdateIntervalSeconds { get; set; } = 3600;

        public double CustomHitokotoChance { get; set; } = 5;

        public List<string>? CustomHitokotoList { get; set; }

        public List<string>? CustomHitokotoFromList { get; set; }
    }

    private bool TryImportLegacy(out HitokotoStore store)
    {
        store = new HitokotoStore();
        foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var path = Path.Combine(dir, LegacyConfigFile);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var legacy = JsonSerializer.Deserialize<LegacyConfig>(File.ReadAllText(path), JsonOptions);
                if (legacy == null)
                {
                    continue;
                }

                var texts = legacy.CustomHitokotoList ?? new List<string>();
                var froms = legacy.CustomHitokotoFromList ?? new List<string>();
                store = new HitokotoStore
                {
                    Enabled = legacy.Enabled,
                    UpdateIntervalSeconds = Math.Max(MinIntervalSeconds, legacy.UpdateIntervalSeconds),
                    CustomChance = Math.Clamp(legacy.CustomHitokotoChance, 0, 100),
                    CustomList = texts
                        .Select((t, i) => new HitokotoEntry { Text = t ?? string.Empty, From = i < froms.Count ? froms[i] ?? string.Empty : "未知出处" })
                        .Where(e => !string.IsNullOrWhiteSpace(e.Text))
                        .ToList(),
                };
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Hitokoto] Could not import {File}.", path);
            }
        }

        return false;
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_store, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hitokoto] Failed to save {File}.", _filePath);
        }
    }

    // ================= parsing =================

    private static Quote? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                var text = ReadString(root, "hitokoto") ?? ReadString(root, "content") ?? ReadString(root, "text");
                var cleaned = Clean(text, MaxTextLength);
                if (cleaned.Length == 0)
                {
                    return null;
                }

                // 与原来一致: 出处只用 from(作品/来源), 不拼作者。
                return new Quote(cleaned, Clean(ReadString(root, "from"), MaxSourceLength));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // 纯文本接口: 整段就是句子。
        var plain = Clean(trimmed, MaxTextLength);
        return plain.Length == 0 ? null : new Quote(plain, string.Empty);
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>去掉尖括号与换行、合并空白、截断。</summary>
    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value.Trim())
        {
            var ch = c switch
            {
                '<' => '‹',
                '>' => '›',
                '\r' or '\n' or '\t' => ' ',
                _ => c,
            };

            if (char.IsControl(ch))
            {
                continue;
            }

            if (ch == ' ')
            {
                if (lastWasSpace)
                {
                    continue;
                }

                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            sb.Append(ch);
        }

        var result = sb.ToString().Trim();
        return result.Length <= maxLength ? result : result[..maxLength] + "…";
    }
}
