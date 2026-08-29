using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class PlayerLogEntry
{
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "";   // chat, murder, exile, vote, task, vent, meeting, join, leave, report, game, kick, ban
    public string PlayerName { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string Puid { get; set; } = "";
    public string GameCode { get; set; } = "";
    public string Detail { get; set; } = "";
}

internal class PlayerLogStoreData
{
    public List<PlayerLogEntry> Logs { get; set; } = new();
}

/// <summary>
///     Structured player behavior log: every meaningful in-game action is recorded
///     with time/type/player/room/detail and viewable from the web panel.
/// </summary>
public class PlayerLogService
{
    private readonly string _filePath;
    private readonly ILogger<PlayerLogService> _logger;
    private readonly object _lock = new();
    private readonly List<PlayerLogEntry> _entries = new();
    private readonly List<PlayerLogEntry> _pending = new();
    private readonly System.Threading.Timer _flushTimer;

    private const int MaxEntries = 20000;
    private const int BatchThreshold = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public PlayerLogService(IOptions<WebAdminConfig> config, ILogger<PlayerLogService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.PlayerLogFile);
        Load();
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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
                var data = JsonSerializer.Deserialize<PlayerLogStoreData>(json, JsonOptions);
                if (data?.Logs != null)
                {
                    _entries.AddRange(data.Logs.TakeLast(MaxEntries));
                    _logger.LogInformation("[PlayerLog] Loaded {Count} entries from {File}.", _entries.Count, _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PlayerLog] Failed to load {File}.", _filePath);
            }
        }
    }

    public void Add(string type, string playerName, string friendCode, string puid, string gameCode, string detail = "")
    {
        var entry = new PlayerLogEntry
        {
            Time = DateTime.UtcNow,
            Type = type,
            PlayerName = playerName ?? "",
            FriendCode = friendCode ?? "",
            Puid = puid ?? "",
            GameCode = gameCode ?? "",
            Detail = detail ?? "",
        };

        lock (_lock)
        {
            _entries.Add(entry);
            _pending.Add(entry);

            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }

            // Flush eagerly at batch threshold so the panel never lags far behind.
            if (_pending.Count >= BatchThreshold)
            {
                Flush();
            }
        }
    }

    public void Flush()
    {
        List<PlayerLogEntry> batch;
        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = _pending.ToList();
            _pending.Clear();
        }

        try
        {
            List<PlayerLogEntry> all;
            lock (_lock)
            {
                all = _entries.TakeLast(MaxEntries).ToList();
            }

            var data = new PlayerLogStoreData { Logs = all };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PlayerLog] Failed to flush to {File}.", _filePath);
        }
    }

    public Task<List<PlayerLogEntry>> GetLogsAsync(string? type = null, string? search = null, int limit = 500)
    {
        lock (_lock)
        {
            IEnumerable<PlayerLogEntry> query = _entries.AsEnumerable().Reverse();

            if (!string.IsNullOrWhiteSpace(type) && type != "all")
            {
                query = query.Where(e => e.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(e =>
                    e.PlayerName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    e.FriendCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    e.Puid.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    e.GameCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    e.Detail.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(query.Take(limit).ToList());
        }
    }

    public Task<List<string>> GetTypesAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_entries.Select(e => e.Type).Distinct().OrderBy(t => t).ToList());
        }
    }
}