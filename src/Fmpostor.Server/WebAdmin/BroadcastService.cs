using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fmpostor.Api.Games.Managers;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class BroadcastMessageItem
{
    public string Text { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class BroadcastStore
{
    public bool Enabled { get; set; } = false;
    public int IntervalMinutes { get; set; } = 30;
    public List<BroadcastMessageItem> Messages { get; set; } = new()
    {
        new BroadcastMessageItem { Text = "欢迎来到由帆船服务端驱动的服务器！请文明游戏，举报作弊请输入 /report 按提示选择玩家。", Enabled = true },
    };
}

/// <summary>
///     Periodic server-wide announcement: broadcasts queued messages to all rooms
///     at a configurable interval. Config persisted to webadmin_broadcast.json.
/// </summary>
public class BroadcastService : IDisposable
{
    private readonly string _filePath;
    private readonly ILogger<BroadcastService> _logger;
    private readonly IGameManager _gameManager;
    private readonly object _lock = new();
    private BroadcastStore _store;
    private long _fileVersion = -1;
    private DateTime _nextBroadcastAt;
    private int _lastIndex = -1;
    private readonly System.Threading.Timer _timer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public BroadcastService(IGameManager gameManager, ILogger<BroadcastService> logger)
    {
        _logger = logger;
        _gameManager = gameManager;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_broadcast.json");
        _store = new BroadcastStore();
        Load();
        // First broadcast shortly after startup when enabled, so the feature is
        // immediately verifiable; afterwards the configured interval applies.
        _nextBroadcastAt = _store.Enabled
            ? DateTime.UtcNow.AddSeconds(10)
            : DateTime.MaxValue;
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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
                var data = JsonSerializer.Deserialize<BroadcastStore>(json, JsonOptions);
                if (data != null)
                {
                    _store = data;
                    if (_store.IntervalMinutes <= 0) _store.IntervalMinutes = 30;
                    // 一次性文案迁移：旧数据里的"帆船服务器"改为准确表述
                    // （避免玩家误以为这是"帆船服"官方服务器）
                    foreach (var item in _store.Messages)
                    {
                        item.Text = item.Text?.Replace("欢迎来到帆船服务器！", "欢迎来到由帆船服务端驱动的服务器！") ?? item.Text;
                    }
                    _fileVersion = GetFileVersion();
                    _logger.LogInformation("[Broadcast] Loaded {Count} message(s), interval={Min}min, enabled={Enabled}.",
                        _store.Messages.Count, _store.IntervalMinutes, _store.Enabled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Broadcast] Failed to load {File}, using defaults.", _filePath);
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
                _logger.LogWarning(ex, "[Broadcast] Failed to save {File}.", _filePath);
            }
        }
    }

    private void Tick()
    {
        lock (_lock)
        {
            TryReload();
            if (!_store.Enabled || _store.Messages.Count == 0)
            {
                return;
            }

            if (DateTime.UtcNow < _nextBroadcastAt)
            {
                return;
            }

            _nextBroadcastAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _store.IntervalMinutes));

            var message = NextMessageLocked();
            if (message != null)
            {
                _ = BroadcastAsync(message);
            }
        }
    }

    private string? NextMessageLocked()
    {
        var enabled = _store.Messages.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Text)).ToList();
        if (enabled.Count == 0)
        {
            return null;
        }

        _lastIndex = (_lastIndex + 1) % enabled.Count;
        return enabled[_lastIndex].Text;
    }

    public async Task<int> BroadcastAsync(string message)
    {
        var sent = 0;
        foreach (var game in _gameManager.Games)
        {
            var host = game.Host?.Character;
            if (host == null)
            {
                continue;
            }

            try
            {
                var announcement = $"[公告] {message}";
                // Register the exact announcement so the chat event handler can tell
                // real server messages apart from players spoofing the "[公告]" prefix.
                RecordServerBroadcast(game.Code.Code, announcement);
                await host.SendChatAsync(announcement);
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Broadcast] Failed to send announcement to game {Code}", game.Code.Code);
            }
        }

        _logger.LogInformation("[Broadcast] Announcement sent to {Count} room(s): {Msg}", sent, message);
        return sent;
    }

    /// <summary>
    ///     Panel action: broadcast a specific message right now.
    /// </summary>
    public Task<int> BroadcastNowAsync(string message)
    {
        return BroadcastAsync(message);
    }

    public BroadcastStore GetSnapshot()
    {
        lock (_lock)
        {
            TryReload();
            return new BroadcastStore
            {
                Enabled = _store.Enabled,
                IntervalMinutes = _store.IntervalMinutes,
                Messages = _store.Messages.Select(m => new BroadcastMessageItem { Text = m.Text, Enabled = m.Enabled }).ToList(),
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

            _store.Messages.Add(new BroadcastMessageItem { Text = message.Trim(), Enabled = true });
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

    public void UpdateSettings(bool? enabled, int? intervalMinutes)
    {
        lock (_lock)
        {
            TryReload();
            if (enabled.HasValue)
            {
                _store.Enabled = enabled.Value;
            }

            if (intervalMinutes.HasValue && intervalMinutes.Value > 0)
            {
                _store.IntervalMinutes = intervalMinutes.Value;
            }

            Save();

            // When (re-)enabled, broadcast the first message shortly after saving so
            // the admin can immediately confirm the feature works; afterwards the
            // regular interval applies.
            if (_store.Enabled)
            {
                _nextBroadcastAt = DateTime.UtcNow.AddSeconds(10);
            }
            else
            {
                _nextBroadcastAt = DateTime.MaxValue;
            }

            _logger.LogInformation("[Broadcast] Settings updated: enabled={Enabled}, interval={Min}min, next broadcast at {At}",
                _store.Enabled, _store.IntervalMinutes, _nextBroadcastAt);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }

    // ===== Server-announcement registry =====

    private static readonly TimeSpan AnnouncementTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Remembers the last announcement text per game so OnGamePlayerChat can
    ///     distinguish genuine server announcements from a player typing "[公告]…".
    /// </summary>
    public void RecordServerBroadcast(string gameCode, string announcement)
    {
        lock (_recentLock)
        {
            _recent[gameCode] = (announcement, DateTime.UtcNow);
        }
    }

    public bool MatchesRecentServerBroadcast(string gameCode, string message)
    {
        lock (_recentLock)
        {
            if (!_recent.TryGetValue(gameCode, out var entry))
            {
                return false;
            }

            var ok = DateTime.UtcNow - entry.Time <= AnnouncementTtl
                     && string.Equals(entry.Text, message, StringComparison.Ordinal);
            if (!ok && DateTime.UtcNow - entry.Time > AnnouncementTtl)
            {
                _recent.Remove(gameCode);
            }

            return ok;
        }
    }

    private readonly object _recentLock = new();
    private readonly Dictionary<string, (string Text, DateTime Time)> _recent = new();
}