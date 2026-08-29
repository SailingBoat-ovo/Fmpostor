using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class FilterStore
{
    public bool Enabled { get; set; } = true;
    public List<string> BlockedWords { get; set; } = new();
    public string TipMessage { get; set; } = "你的消息包含违禁词，已被系统屏蔽。";

    /// <summary>
    ///     Auto-mute: after ViolationLimit blocked messages the player is muted for
    ///     MuteDurationMinutes. Off by default; enabled from the panel.
    /// </summary>
    public bool AutoMuteEnabled { get; set; } = false;
    public int ViolationLimit { get; set; } = 3;
    public int MuteDurationMinutes { get; set; } = 10;
    public string MuteMessage { get; set; } = "你因多次发送违禁词被禁言 {Minutes} 分钟。";
}

/// <summary>
///     Chat filter: banned words list with hot reload from webadmin_filter.json.
///     Messages containing a banned word are cancelled and the sender receives a
///     private in-game tip. Optional auto-mute after repeated violations.
/// </summary>
public class BadWordFilterService
{
    private readonly string _filePath;
    private readonly ILogger<BadWordFilterService> _logger;
    private readonly object _lock = new();
    private FilterStore _store;
    private long _fileVersion = -1;

    // playerKey (friend code / puid / ip) -> (violation count, mute until UTC)
    private readonly ConcurrentDictionary<string, (int Count, DateTime MuteUntil)> _violations = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public BadWordFilterService(IOptions<WebAdminConfig> config, ILogger<BadWordFilterService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.FilterFile);
        _store = new FilterStore();
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
                var data = JsonSerializer.Deserialize<FilterStore>(json, JsonOptions);
                if (data != null)
                {
                    _store = data;
                    _store.BlockedWords = (_store.BlockedWords ?? new List<string>())
                        .Where(w => !string.IsNullOrWhiteSpace(w))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (_store.ViolationLimit <= 0) _store.ViolationLimit = 3;
                    if (_store.MuteDurationMinutes <= 0) _store.MuteDurationMinutes = 10;
                    _fileVersion = GetFileVersion();
                    _logger.LogInformation("[Filter] Loaded {Count} blocked word(s) from {File} (enabled={Enabled}, autoMute={AutoMute}).",
                        _store.BlockedWords.Count, _filePath, _store.Enabled, _store.AutoMuteEnabled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Filter] Failed to load {File}, using defaults.", _filePath);
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
                _logger.LogWarning(ex, "[Filter] Failed to save {File}.", _filePath);
            }
        }
    }

    /// <summary>
    ///     Returns true when the message must be blocked. When true, <paramref name="tip"/>
    ///     contains the private message to send to the player (may be null).
    ///     <paramref name="playerKey"/> identifies the player (friend code / PUID / IP)
    ///     for auto-mute bookkeeping.
    /// </summary>
    public bool Check(string message, string playerKey, out string? tip)
    {
        return Check(message, playerKey, out _, out tip);
    }

    /// <summary>Same as <see cref="Check(string,string,out string?)"/> but also outputs the matched word (for statistics).</summary>
    public bool Check(string message, string playerKey, out string? word, out string? tip)
    {
        word = null;
        tip = null;
        lock (_lock)
        {
            TryReload();

            if (!_store.Enabled || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            // Active mute check.
            if (!string.IsNullOrEmpty(playerKey) && _violations.TryGetValue(playerKey, out var state))
            {
                if (state.MuteUntil > DateTime.UtcNow)
                {
                    var remaining = (int)Math.Ceiling((state.MuteUntil - DateTime.UtcNow).TotalMinutes);
                    tip = remaining > 0 ? $"你因多次发送违禁词被禁言，剩余 {Math.Max(1, remaining)} 分钟。" : null;
                    return true;
                }

                _violations.TryRemove(playerKey, out _);
            }

            if (_store.BlockedWords.Count == 0)
            {
                return false;
            }

            var lower = message.ToLowerInvariant();
            foreach (var w in _store.BlockedWords)
            {
                if (string.IsNullOrWhiteSpace(w))
                {
                    continue;
                }

                if (lower.Contains(w.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    word = w;
                    tip = _store.TipMessage;
                    RecordViolation(playerKey);
                    return true;
                }
            }

            // Clean message resets the violation counter (forgiving).
            if (!string.IsNullOrEmpty(playerKey))
            {
                _violations.TryRemove(playerKey, out _);
            }
        }

        return false;
    }

    private void RecordViolation(string playerKey)
    {
        if (string.IsNullOrEmpty(playerKey) || !_store.AutoMuteEnabled)
        {
            return;
        }

        // Bound the violation table: keys are attacker-influenceable.
        if (_violations.Count > 10_000)
        {
            foreach (var kv in _violations.Where(kv => kv.Value.MuteUntil <= DateTime.UtcNow).Take(5_000).ToList())
            {
                _violations.TryRemove(kv.Key, out _);
            }
        }

        var now = DateTime.UtcNow;
        var state = _violations.AddOrUpdate(playerKey,
            _ => (1, now.AddMinutes(_store.MuteDurationMinutes)),
            (_, old) =>
            {
                // If the mute already expired, restart the counter.
                var count = old.MuteUntil < now ? 1 : old.Count + 1;
                return (count, count >= _store.ViolationLimit ? now.AddMinutes(_store.MuteDurationMinutes) : old.MuteUntil);
            });

        if (state.Count >= _store.ViolationLimit)
        {
            _logger.LogWarning("[Filter] Player {Key} muted for {Minutes} min after {Count} violations.",
                playerKey, _store.MuteDurationMinutes, state.Count);
        }
    }

    public string TipMessage
    {
        get
        {
            lock (_lock)
            {
                TryReload();
                return _store.TipMessage;
            }
        }
    }

    public bool Enabled
    {
        get
        {
            lock (_lock)
            {
                TryReload();
                return _store.Enabled;
            }
        }
    }

    public FilterStore GetSnapshot()
    {
        lock (_lock)
        {
            TryReload();
            return new FilterStore
            {
                Enabled = _store.Enabled,
                BlockedWords = _store.BlockedWords.ToList(),
                TipMessage = _store.TipMessage,
                AutoMuteEnabled = _store.AutoMuteEnabled,
                ViolationLimit = _store.ViolationLimit,
                MuteDurationMinutes = _store.MuteDurationMinutes,
                MuteMessage = _store.MuteMessage,
            };
        }
    }

    public void AddWord(string word)
    {
        lock (_lock)
        {
            TryReload();
            if (string.IsNullOrWhiteSpace(word))
            {
                return;
            }

            word = word.Trim();
            if (!_store.BlockedWords.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                _store.BlockedWords.Add(word);
                Save();
                _logger.LogInformation("[Filter] Added blocked word: {Word}", word);
            }
        }
    }

    public void RemoveWord(string word)
    {
        lock (_lock)
        {
            TryReload();
            if (_store.BlockedWords.RemoveAll(w => w.Equals(word, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Save();
                _logger.LogInformation("[Filter] Removed blocked word: {Word}", word);
            }
        }
    }

    public void UpdateSettings(bool? enabled, string? tipMessage, bool? autoMuteEnabled = null, int? violationLimit = null, int? muteDurationMinutes = null, string? muteMessage = null)
    {
        lock (_lock)
        {
            TryReload();
            if (enabled.HasValue)
            {
                _store.Enabled = enabled.Value;
            }

            if (tipMessage != null)
            {
                _store.TipMessage = tipMessage;
            }

            if (autoMuteEnabled.HasValue)
            {
                _store.AutoMuteEnabled = autoMuteEnabled.Value;
            }

            if (violationLimit.HasValue && violationLimit.Value > 0)
            {
                _store.ViolationLimit = violationLimit.Value;
            }

            if (muteDurationMinutes.HasValue && muteDurationMinutes.Value > 0)
            {
                _store.MuteDurationMinutes = muteDurationMinutes.Value;
            }

            if (muteMessage != null)
            {
                _store.MuteMessage = muteMessage;
            }

            Save();
        }
    }
}
