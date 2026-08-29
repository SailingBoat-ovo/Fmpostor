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
    public List<string> Messages { get; set; } = new()
    {
        "欢迎 {PLAYER_NAME} 加入 {Room} 房间，祝游戏愉快！",
    };
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
    ///     {TIME_SINCE_LAST_LOGIN} {PLAY_TIME}. New and returning players use
    ///     the same template list.
    /// </summary>
    public string? Pick(string playerName, string roomCode, PlayerTimeEntry? playerData = null)
    {
        lock (_lock)
        {
            TryReload();
            if (!_store.Enabled)
            {
                return null;
            }

            var templates = _store.Messages
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
            if (templates.Count == 0)
            {
                return null;
            }

            // Round-robin over the list so every message gets used.
            _lastIndex = (_lastIndex + 1) % templates.Count;
            var template = templates[_lastIndex];

            var lastLogin = playerData?.LastLoginTime;
            var playTimeMinutes = playerData?.TotalPlayTimeMinutes ?? 0;
            var timeSince = lastLogin.HasValue ? DateTime.UtcNow - lastLogin.Value : TimeSpan.Zero;

            // Strip rich-text brackets from substituted player names so a crafted
            // name cannot inject Unity rich-text formatting into announcements.
            var safeName = (playerName ?? string.Empty).Replace('<', '‹').Replace('>', '›');

            return template
                .Replace("{Name}", safeName, StringComparison.Ordinal)
                .Replace("{Room}", roomCode, StringComparison.Ordinal)
                .Replace("{PLAYER_NAME}", safeName, StringComparison.Ordinal)
                .Replace("{LAST_LOGIN_TIME}", lastLogin?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知", StringComparison.Ordinal)
                .Replace("{TIME_SINCE_LAST_LOGIN}", FormatDuration(timeSince), StringComparison.Ordinal)
                .Replace("{PLAY_TIME}", FormatMinutes(playTimeMinutes), StringComparison.Ordinal);
        }
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

    public void UpdateSettings(bool? enabled)
    {
        lock (_lock)
        {
            TryReload();
            if (enabled.HasValue)
            {
                _store.Enabled = enabled.Value;
            }

            Save();
        }
    }
}