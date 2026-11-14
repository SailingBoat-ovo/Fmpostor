using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     One reconnect-related event, as surfaced to the web panel.
/// </summary>
public class ReconnectLogEntry
{
    /// <summary>UTC time of the event.</summary>
    public DateTime Time { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     reserved | resumed | expired | failed
    ///     - reserved : a dropped player's slot was kept for a possible return
    ///     - resumed  : the player came back and got their original body
    ///     - expired  : the grace period elapsed without a return
    ///     - failed   : a resume was attempted and could not be completed
    /// </summary>
    public string Event { get; set; } = "";

    public string GameCode { get; set; } = "";

    public string PlayerName { get; set; } = "";

    public string FriendCode { get; set; } = "";

    public string Puid { get; set; } = "";

    public string IpAddress { get; set; } = "";

    /// <summary>The preserved in-game slot id.</summary>
    public int PlayerId { get; set; }

    /// <summary>ClientId before the drop, and after a resume.</summary>
    public int OldClientId { get; set; }

    public int NewClientId { get; set; }

    /// <summary>Configured grace period, in seconds.</summary>
    public int GracePeriodSeconds { get; set; }

    /// <summary>Seconds between the drop and the successful return.</summary>
    public double GapSeconds { get; set; }

    /// <summary>Free-form extra information (reason, diagnostics).</summary>
    public string Detail { get; set; } = "";
}

/// <summary>
///     Append-only structured log of every disconnect/reconnect event, so an
///     operator can see who dropped, who came back, from which room, and when.
///     Persistence uses the project's shared JSONL helper: appends are O(new
///     entries) and the file is compacted only when it outgrows its budget.
/// </summary>
public class ReconnectLogService
{
    private const int MaxEntries = 20000;
    private const long MaxBytes = 8L * 1024 * 1024;
    private const int BatchThreshold = 50;

    private readonly string _filePath;
    private readonly ILogger<ReconnectLogService> _logger;
    private readonly ReconnectConfig _config;
    private readonly object _lock = new();
    private readonly List<ReconnectLogEntry> _entries = new();
    private readonly List<ReconnectLogEntry> _pending = new();
    private readonly Timer _flushTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ReconnectLogService(
        IOptions<ReconnectConfig> config,
        ILogger<ReconnectLogService> logger)
    {
        _config = config.Value;
        _logger = logger;

        var dir = Path.Combine(AppContext.BaseDirectory, "webadmin-data");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "reconnect-logs.jsonl");

        try
        {
            _entries.AddRange(JsonLines.Read<ReconnectLogEntry>(_filePath, JsonOptions));
            if (_entries.Count > MaxEntries)
            {
                _entries = _entries.Skip(_entries.Count - MaxEntries).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read reconnect log store.");
        }

        _flushTimer = new Timer(_ => Flush(), null, 5000, 5000);
    }

    /// <summary>Records one event. Never throws.</summary>
    public void Record(ReconnectLogEntry entry)
    {
        try
        {
            if (entry.Time.Kind != DateTimeKind.Utc)
            {
                entry.Time = entry.Time.ToUniversalTime();
            }

            lock (_lock)
            {
                _pending.Add(entry);

                if (_pending.Count >= BatchThreshold)
                {
                    FlushLocked();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record reconnect event.");
        }
    }

    /// <summary>Convenience overload for the common connection of fields.</summary>
    public void Record(
        string @event,
        string gameCode,
        string playerName,
        string friendCode,
        string puid,
        string ipAddress,
        int playerId,
        int oldClientId,
        int newClientId = 0,
        double gapSeconds = 0,
        string detail = "")
    {
        Record(new ReconnectLogEntry
        {
            Event = @event,
            GameCode = gameCode,
            PlayerName = playerName,
            FriendCode = friendCode,
            Puid = puid,
            IpAddress = ipAddress,
            PlayerId = playerId,
            OldClientId = oldClientId,
            NewClientId = newClientId,
            GracePeriodSeconds = _config.GracePeriodSeconds,
            GapSeconds = Math.Round(gapSeconds, 2),
            Detail = detail,
        });
    }

    public Task<List<ReconnectLogEntry>> GetLogsAsync(
        string? @event = null,
        string? gameCode = null,
        string? search = null,
        int limit = 500)
    {
        lock (_lock)
        {
            FlushLocked();

            IEnumerable<ReconnectLogEntry> query = _entries;

            if (!string.IsNullOrWhiteSpace(@event) &&
                !string.Equals(@event, "all", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(e => string.Equals(e.Event, @event, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(gameCode))
            {
                query = query.Where(e => string.Equals(e.GameCode, gameCode, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim();
                query = query.Where(e =>
                    Contains(e.PlayerName, needle) ||
                    Contains(e.FriendCode, needle) ||
                    Contains(e.Puid, needle) ||
                    Contains(e.IpAddress, needle) ||
                    Contains(e.Detail, needle));
            }

            var result = query
                .OrderByDescending(e => e.Time)
                .Take(Math.Clamp(limit, 1, 5000))
                .ToList();

            return Task.FromResult(result);
        }
    }

    /// <summary>Distinct event kinds, for the panel's filter dropdown.</summary>
    public Task<List<string>> GetEventTypesAsync()
    {
        lock (_lock)
        {
            var types = _entries
                .Select(e => e.Event)
                .Where(e => !string.IsNullOrEmpty(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(types);
        }
    }

    /// <summary>Aggregate counters shown on the dashboard.</summary>
    public Task<ReconnectStats> GetStatsAsync()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var day = now.AddDays(-1);

            var stats = new ReconnectStats
            {
                Total = _entries.Count,
                Reserved = _entries.Count(e => e.Event == "reserved"),
                Resumed = _entries.Count(e => e.Event == "resumed"),
                Expired = _entries.Count(e => e.Event == "expired"),
                Failed = _entries.Count(e => e.Event == "failed"),
                ResumedLast24h = _entries.Count(e => e.Event == "resumed" && e.Time >= day),
                ReservedLast24h = _entries.Count(e => e.Event == "reserved" && e.Time >= day),
            };

            var gaps = _entries
                .Where(e => e.Event == "resumed" && e.GapSeconds > 0)
                .Select(e => e.GapSeconds)
                .ToList();

            if (gaps.Count > 0)
            {
                stats.AverageGapSeconds = Math.Round(gaps.Average(), 2);
            }

            return Task.FromResult(stats);
        }
    }

    public Task ClearAsync()
    {
        lock (_lock)
        {
            _entries.Clear();
            _pending.Clear();

            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clear reconnect log file.");
            }

            return Task.CompletedTask;
        }
    }

    private static bool Contains(string? value, string needle)
    {
        return !string.IsNullOrEmpty(value) &&
               value.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void Flush()
    {
        lock (_lock)
        {
            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        try
        {
            JsonLines.Append(_filePath, _pending, JsonOptions, MaxBytes, MaxEntries);
            _entries.AddRange(_pending);

            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not flush reconnect log store.");
        }
        finally
        {
            _pending.Clear();
        }
    }
}

public class ReconnectStats
{
    public int Total { get; set; }

    public int Reserved { get; set; }

    public int Resumed { get; set; }

    public int Expired { get; set; }

    public int Failed { get; set; }

    public int ReservedLast24h { get; set; }

    public int ResumedLast24h { get; set; }

    public double AverageGapSeconds { get; set; }
}
