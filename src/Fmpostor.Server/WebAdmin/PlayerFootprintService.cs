using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Manager;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class FootprintSession
{
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public int DurationSeconds { get; set; }
    public string Ip { get; set; } = "";
}

public class PlayerFootprint
{
    public string Key { get; set; } = "";            // friend code (preferred) / puid / ip
    public string FriendCode { get; set; } = "";
    public string Puid { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public int TotalOnlineSeconds { get; set; }
    public List<string> Ips { get; set; } = new();
    public List<FootprintSession> Sessions { get; set; } = new();
}

internal class FootprintStoreData
{
    public List<PlayerFootprint> Players { get; set; } = new();
}

/// <summary>
///     Player footprint tracking: session history, cumulative online time and known
///     IP addresses per player. Persisted to webadmin_footprints.json (capped).
///     Toggleable from the panel (defaults on).
/// </summary>
public class PlayerFootprintService : IDisposable
{
    private readonly ILogger<PlayerFootprintService> _logger;
    private readonly WebAdminSettingsService _settings;
    private readonly IClientManager _clientManager;
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, PlayerFootprint> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, (string Key, DateTime Since)> _online = new();
    private readonly System.Threading.Timer _timer;
    private long _dirty;

    private const int MaxPlayers = 500;
    private const int MaxSessionsPerPlayer = 200;
    private const int MaxIpsPerPlayer = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public PlayerFootprintService(ILogger<PlayerFootprintService> logger, WebAdminSettingsService settings, IClientManager clientManager)
    {
        _logger = logger;
        _settings = settings;
        _clientManager = clientManager;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_footprints.json");
        Load();
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private bool Enabled => _settings.GetFootprints().Enabled;

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
                var data = JsonSerializer.Deserialize<FootprintStoreData>(json, JsonOptions);
                if (data?.Players != null)
                {
                    foreach (var p in data.Players)
                    {
                        if (!string.IsNullOrEmpty(p.Key))
                        {
                            _players[p.Key] = p;
                        }
                    }

                    _logger.LogInformation("[Footprint] Loaded {Count} footprint(s) from {File}.", _players.Count, _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Footprint] Failed to load {File}.", _filePath);
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var data = new FootprintStoreData { Players = _players.Values.ToList() };
                var json = JsonSerializer.Serialize(data, JsonOptions);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Footprint] Failed to save {File}.", _filePath);
            }
        }
    }

    private void MaybeSave()
    {
        // Turbo-650: mark dirty only; Tick persists when something changed.
        System.Threading.Interlocked.Increment(ref _dirty);
    }

    private void Tick()
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var current = _clientManager.Clients.ToList();
            var currentIds = new HashSet<int>(current.Select(c => c.Id));

            // Settle sessions that ended since the last tick.
            foreach (var kv in _online)
            {
                if (!currentIds.Contains(kv.Key))
                {
                    SettleSession(kv.Key, kv.Value.Key, kv.Value.Since, now);
                    _online.TryRemove(kv.Key, out _);
                }
            }

            // Accumulate online time for current clients.
            foreach (var client in current)
            {
                var key = GetKey(client);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var fp = GetOrCreate(key, client);
                fp.LastSeen = now;
                fp.TotalOnlineSeconds += 30;

                if (_online.TryGetValue(client.Id, out var entry))
                {
                    entry.Since = now.AddSeconds(-30); // keep rolling; settled on disconnect
                }
                else
                {
                    _online[client.Id] = (key, now);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Footprint] Tick failed.");
        }

        // Turbo-650: persist only when sessions settled / data changed since the
        // last tick, instead of rewriting the whole store on every 30s tick.
        if (System.Threading.Interlocked.Exchange(ref _dirty, 0) > 0)
        {
            Save();
        }
    }

    private void SettleSession(int clientId, string key, DateTime since, DateTime now)
    {
        var fp = _players.TryGetValue(key, out var existing) ? existing : null;
        if (fp == null)
        {
            return;
        }

        var duration = (int)(now - since).TotalSeconds;
        if (duration <= 0)
        {
            return;
        }

        var session = new FootprintSession
        {
            Start = since,
            End = now,
            DurationSeconds = duration,
            Ip = fp.Ips.LastOrDefault() ?? "",
        };
        fp.Sessions.Add(session);

        if (fp.Sessions.Count > MaxSessionsPerPlayer)
        {
            fp.Sessions.RemoveRange(0, fp.Sessions.Count - MaxSessionsPerPlayer);
        }

        _logger.LogDebug("[Footprint] Session settled for {Key}: {Dur}s", key, duration);
        MaybeSave();
    }

    private PlayerFootprint GetOrCreate(string key, IClient client)
    {
        // Enforce the table cap: keys are derived from attacker-influenceable
        // friend codes / PUIDs, so trim oldest entries before adding new ones.
        if (_players.Count >= MaxPlayers && !_players.ContainsKey(key))
        {
            foreach (var victim in _players.Values.OrderBy(p => p.LastSeen).Take(MaxPlayers / 10).ToList())
            {
                _players.TryRemove(victim.Key, out _);
            }
        }

        var fp = _players.GetOrAdd(key, _ =>
        {
            var created = new PlayerFootprint
            {
                Key = key,
                FriendCode = client.FriendCode ?? "",
                Puid = client.Puid ?? "",
                Name = client.Name ?? "",
                FirstSeen = DateTime.UtcNow,
            };
            return created;
        });

        // Keep the best identity info we have.
        if (!string.IsNullOrEmpty(client.FriendCode))
        {
            fp.FriendCode = client.FriendCode;
        }

        if (!string.IsNullOrEmpty(client.Puid))
        {
            fp.Puid = client.Puid;
        }

        if (!string.IsNullOrEmpty(client.Name))
        {
            fp.Name = client.Name;
        }

        var ip = client.Connection?.EndPoint?.Address?.ToString();
        if (!string.IsNullOrEmpty(ip) && !fp.Ips.Contains(ip, StringComparer.OrdinalIgnoreCase))
        {
            fp.Ips.Add(ip);
            if (fp.Ips.Count > MaxIpsPerPlayer)
            {
                fp.Ips.RemoveRange(0, fp.Ips.Count - MaxIpsPerPlayer);
            }
        }

        return fp;
    }

    private static string GetKey(IClient client)
    {
        if (!string.IsNullOrEmpty(client.FriendCode) && !PlayerIdentityService.IsPlaceholderFriendCode(client.FriendCode))
        {
            return "fc:" + PlayerIdentityService.SanitizeId(client.FriendCode);
        }

        if (!string.IsNullOrEmpty(client.Puid))
        {
            return "puid:" + PlayerIdentityService.SanitizeId(client.Puid);
        }

        var ip = client.Connection?.EndPoint?.Address?.ToString();
        return string.IsNullOrEmpty(ip) ? string.Empty : "ip:" + ip;
    }

    // ========== Panel API ==========

    public Task<List<PlayerFootprint>> GetListAsync(string? search = null, int limit = 100)
    {
        lock (_lock)
        {
            IEnumerable<PlayerFootprint> query = _players.Values;

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(p =>
                    p.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.FriendCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.Puid.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.Ips.Any(ip => ip.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }

            return Task.FromResult(query
                .OrderByDescending(p => p.LastSeen)
                .Take(limit)
                .Select(p => new PlayerFootprint
                {
                    Key = p.Key,
                    FriendCode = p.FriendCode,
                    Puid = p.Puid,
                    Name = p.Name,
                    FirstSeen = p.FirstSeen,
                    LastSeen = p.LastSeen,
                    TotalOnlineSeconds = p.TotalOnlineSeconds,
                    Ips = p.Ips.ToList(),
                    Sessions = p.Sessions.OrderByDescending(s => s.Start).ToList(),
                })
                .ToList());
        }
    }

    public Task<bool> ClearAllAsync()
    {
        lock (_lock)
        {
            _players.Clear();
            Save();
            return Task.FromResult(true);
        }
    }

    public void Flush()
    {
        Save();
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }
}