using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class PlayerStatsEntry
{
    public string FriendCode { get; set; } = "";
    public string? LastKnownName { get; set; }
    public int GamesPlayed { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int ImpostorWins { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int TasksCompleted { get; set; }
    public int TimesExiled { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

internal class PlayerStatsStoreData
{
    public List<PlayerStatsEntry> Players { get; set; } = new();
}

/// <summary>
///     Per-friend-code player statistics (games, wins/losses, kills, deaths, tasks,
///     exiles), persisted to webadmin_player_stats.json and shown in the panel.
/// </summary>
public class PlayerStatsService
{
    private readonly string _filePath;
    private readonly ILogger<PlayerStatsService> _logger;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, PlayerStatsEntry> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _saveTimer;
    private long _dirty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public PlayerStatsService(IOptions<WebAdminConfig> config, ILogger<PlayerStatsService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.PlayerStatsFile);
        Load();
        _saveTimer = new System.Threading.Timer(_ => Save(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<PlayerStatsStoreData>(json, JsonOptions);
                if (data?.Players != null)
                {
                    foreach (var p in data.Players)
                    {
                        if (!string.IsNullOrEmpty(p.FriendCode))
                        {
                            _stats[p.FriendCode] = p;
                        }
                    }

                    _logger.LogInformation("[Stats] Loaded {Count} player stat(s) from {File}.", _stats.Count, _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Stats] Failed to load {File}.", _filePath);
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var data = new PlayerStatsStoreData { Players = _stats.Values.ToList() };
                var json = JsonSerializer.Serialize(data, JsonOptions);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Stats] Failed to save {File}.", _filePath);
            }
        }
    }

    private void MaybeSave()
    {
        if (System.Threading.Interlocked.Increment(ref _dirty) % 20 == 0)
        {
            Save();
        }
    }

    private const int MaxEntries = 5000;

    private PlayerStatsEntry GetOrCreate(string friendCode, string? name)
    {
        var fc = (friendCode ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(fc))
        {
            return new PlayerStatsEntry();
        }

        // Friend codes are attacker-influenceable; bound the table so cycling
        // codes cannot grow memory/disk without limit.
        if (_stats.Count >= MaxEntries && !_stats.ContainsKey(fc))
        {
            foreach (var victim in _stats.Values.OrderBy(s => s.LastSeen).Take(MaxEntries / 10))
            {
                _stats.TryRemove(victim.FriendCode, out _);
            }
        }

        var entry = _stats.GetOrAdd(fc, _ => new PlayerStatsEntry { FriendCode = fc, FirstSeen = DateTime.UtcNow });
        if (!string.IsNullOrEmpty(name))
        {
            entry.LastKnownName = name;
        }

        entry.LastSeen = DateTime.UtcNow;
        return entry;
    }

    public void RecordKill(string friendCode, string? name)
    {
        var e = GetOrCreate(friendCode, name);
        e.Kills++;
        MaybeSave();
    }

    public void RecordDeath(string friendCode, string? name)
    {
        var e = GetOrCreate(friendCode, name);
        e.Deaths++;
        MaybeSave();
    }

    public void RecordTaskCompleted(string friendCode, string? name)
    {
        var e = GetOrCreate(friendCode, name);
        e.TasksCompleted++;
        MaybeSave();
    }

    public void RecordExile(string friendCode, string? name)
    {
        var e = GetOrCreate(friendCode, name);
        e.TimesExiled++;
        MaybeSave();
    }

    /// <summary>
    ///     Records a finished game for one player.
    /// </summary>
    /// <param name="friendCode">Player's friend code.</param>
    /// <param name="name">Last known player name.</param>
    /// <param name="crewmateWin">True when the crewmates won.</param>
    /// <param name="wasImpostor">True when the player was an impostor this game.</param>
    public void RecordGameEnd(string friendCode, string? name, bool crewmateWin, bool wasImpostor)
    {
        var e = GetOrCreate(friendCode, name);
        e.GamesPlayed++;
        if (wasImpostor)
        {
            if (!crewmateWin)
            {
                e.ImpostorWins++;
            }
        }
        else
        {
            if (crewmateWin)
            {
                e.Wins++;
            }
            else
            {
                e.Losses++;
            }
        }

        MaybeSave();
    }

    public Task<List<PlayerStatsEntry>> GetAllAsync()
    {
        return Task.FromResult(_stats.Values.OrderByDescending(s => s.GamesPlayed).ToList());
    }

    public Task<PlayerStatsEntry?> GetByFriendCodeAsync(string friendCode)
    {
        _stats.TryGetValue((friendCode ?? string.Empty).Trim(), out var entry);
        return Task.FromResult(entry);
    }

    public Task ClearAllAsync()
    {
        _stats.Clear();
        Save();
        return Task.CompletedTask;
    }

    public void Flush()
    {
        Save();
    }
}