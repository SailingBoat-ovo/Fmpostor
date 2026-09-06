using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class FilterStatEntry
{
    public int Id { get; set; }
    public string FriendCode { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Word { get; set; } = "";
    public int Count { get; set; }
    public DateTime LastTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     One QQ-group broadcast delivery row. Source:
///     "qq" = triggered by a #command inside a QQ group (sender QQ + group),
///     "game" = triggered by in-game /m (friend code + player name per delivered group),
///     "schedule" = scheduled task / panel broadcast (no personal sender).
/// </summary>
public class BroadcastStatEntry
{
    public int Id { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "";
    public long Qq { get; set; }
    public long GroupId { get; set; }
    public string FriendCode { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Content { get; set; } = "";
}

public class FilterStatsStore
{
    public List<FilterStatEntry> Stats { get; set; } = new();
    public int NextId { get; set; } = 1;
}

public class BroadcastStatsStore
{
    public List<BroadcastStatEntry> Stats { get; set; } = new();
    public int NextId { get; set; } = 1;
}

/// <summary>
///     Aggregated statistics for the banned-word filter and QQ-group
///     broadcasts. Both stores are capped and panel-deletable so admins can
///     reclaim disk space on small servers.
/// </summary>
public class AdminStatsService
{
    private const int MaxFilterEntries = 5000;
    private const int MaxBroadcastRows = 2000;

    private readonly ILogger<AdminStatsService> _logger;
    private readonly string _filterFile;
    private readonly string _broadcastFile;
    private readonly object _fLock = new();
    private readonly object _bLock = new();
    private FilterStatsStore _filter = new();
    private BroadcastStatsStore _broadcast = new();
    private bool _filterDirty;
    private bool _broadcastDirty;
    private readonly System.Threading.Timer _saveTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AdminStatsService(IOptions<WebAdminConfig> config, ILogger<AdminStatsService> logger)
    {
        _logger = logger;
        _filterFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.FilterStatsFile);
        _broadcastFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.BroadcastStatsFile);
        LoadAll();
        // Turbo-650: records no longer rewrite the store file on every event —
        // dirty stores are persisted by this timer (and immediately on clears).
        _saveTimer = new System.Threading.Timer(_ => SaveDirty(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void SaveDirty()
    {
        try
        {
            lock (_fLock)
            {
                if (_filterDirty)
                {
                    _filterDirty = false;
                    SaveFilterLocked();
                }
            }

            lock (_bLock)
            {
                if (_broadcastDirty)
                {
                    _broadcastDirty = false;
                    SaveBroadcastLocked();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stats] SaveDirty failed.");
        }
    }

    private void LoadAll()
    {
        try
        {
            if (File.Exists(_filterFile))
            {
                var data = JsonSerializer.Deserialize<FilterStatsStore>(File.ReadAllText(_filterFile), JsonOptions);
                if (data != null)
                {
                    _filter = data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stats] Failed to load filter stats.");
        }

        try
        {
            if (File.Exists(_broadcastFile))
            {
                var data = JsonSerializer.Deserialize<BroadcastStatsStore>(File.ReadAllText(_broadcastFile), JsonOptions);
                if (data != null)
                {
                    _broadcast = data;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stats] Failed to load broadcast stats.");
        }
    }

    private void SaveFilterLocked()
    {
        try
        {
            File.WriteAllText(_filterFile, JsonSerializer.Serialize(_filter, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stats] Failed to save filter stats.");
        }
    }

    private void SaveBroadcastLocked()
    {
        try
        {
            File.WriteAllText(_broadcastFile, JsonSerializer.Serialize(_broadcast, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stats] Failed to save broadcast stats.");
        }
    }

    /// <summary>Records one blocked message, aggregated per (friend code, word).</summary>
    public void RecordFilterHit(string friendCode, string playerName, string word)
    {
        friendCode = (friendCode ?? "").Trim();
        word = (word ?? "").Trim();
        if (word.Length == 0)
        {
            return;
        }

        try
        {
            lock (_fLock)
            {
                var now = DateTime.UtcNow;
                var entry = _filter.Stats.FirstOrDefault(x => x.Word == word && x.FriendCode == friendCode);
                if (entry == null)
                {
                    _filter.Stats.Add(new FilterStatEntry
                    {
                        Id = _filter.NextId++,
                        FriendCode = friendCode,
                        PlayerName = (playerName ?? "").Trim(),
                        Word = word,
                        Count = 1,
                        LastTime = now,
                    });
                }
                else
                {
                    entry.Count++;
                    entry.LastTime = now;
                    if (!string.IsNullOrWhiteSpace(playerName))
                    {
                        entry.PlayerName = playerName.Trim();
                    }
                }

                if (_filter.Stats.Count > MaxFilterEntries)
                {
                    _filter.Stats = _filter.Stats.OrderByDescending(x => x.LastTime).Take(MaxFilterEntries).ToList();
                }

                _filterDirty = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stats] RecordFilterHit failed.");
        }
    }

    public List<FilterStatEntry> GetFilterStats()
    {
        lock (_fLock)
        {
            return _filter.Stats.OrderByDescending(x => x.LastTime).Take(1000).ToList();
        }
    }

    /// <summary>id=null clears everything; otherwise removes that entry. Returns removed rows.</summary>
    public int ClearFilterStats(int? id)
    {
        lock (_fLock)
        {
            var removed = id == null
                ? _filter.Stats.Count
                : _filter.Stats.RemoveAll(x => x.Id == id.Value);
            if (id == null)
            {
                _filter.Stats.Clear();
            }

            SaveFilterLocked();
            return removed;
        }
    }

    public void RecordBroadcast(string source, long qq, long groupId, string friendCode, string playerName, string content)
    {
        try
        {
            lock (_bLock)
            {
                _broadcast.Stats.Add(new BroadcastStatEntry
                {
                    Id = _broadcast.NextId++,
                    Time = DateTime.UtcNow,
                    Source = source ?? "",
                    Qq = qq,
                    GroupId = groupId,
                    FriendCode = friendCode ?? "",
                    PlayerName = playerName ?? "",
                    Content = content == null ? "" : (content.Length > 120 ? content[..120] : content),
                });

                if (_broadcast.Stats.Count > MaxBroadcastRows)
                {
                    _broadcast.Stats = _broadcast.Stats.OrderByDescending(x => x.Time).Take(MaxBroadcastRows).ToList();
                }

                _broadcastDirty = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stats] RecordBroadcast failed.");
        }
    }

    public List<BroadcastStatEntry> GetBroadcastStats()
    {
        lock (_bLock)
        {
            return _broadcast.Stats.OrderByDescending(x => x.Time).Take(1000).ToList();
        }
    }

    public int ClearBroadcastStats(int? id)
    {
        lock (_bLock)
        {
            var removed = id == null
                ? _broadcast.Stats.Count
                : _broadcast.Stats.RemoveAll(x => x.Id == id.Value);
            if (id == null)
            {
                _broadcast.Stats.Clear();
            }

            SaveBroadcastLocked();
            return removed;
        }
    }
}