using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

internal class BanStore
{
    public List<BanEntry> Bans { get; set; } = new();
    public int NextId { get; set; } = 1;
}

public class BanDatabase : IDisposable
{
    private readonly string _filePath;
    private readonly ILogger<BanDatabase> _logger;
    private readonly object _lock = new();
    private List<BanEntry> _bans = new();
    private int _nextId = 1;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public BanDatabase(ILogger<BanDatabase> logger)
    {
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_bans.json");
        _logger = logger;
        InitializeAsync().GetAwaiter().GetResult();
    }

    public Task InitializeAsync()
    {
        lock (_lock)
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    var data = JsonSerializer.Deserialize<BanStore>(json, JsonOptions);
                    if (data != null)
                    {
                        _bans = data.Bans ?? new List<BanEntry>();
                        _nextId = data.NextId > 0 ? data.NextId : (_bans.Count > 0 ? _bans.Max(b => b.Id) + 1 : 1);
                    }
                    _logger.LogInformation("Ban database loaded from {FilePath} ({Count} entries).", _filePath, _bans.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load ban database from {FilePath}, starting fresh.", _filePath);
                    _bans = new List<BanEntry>();
                    _nextId = 1;
                }
            }
            else
            {
                _logger.LogInformation("Ban database file not found at {FilePath}, will create on first save.", _filePath);
            }
        }

        return Task.CompletedTask;
    }

    private void Save()
    {
        var data = new BanStore
        {
            Bans = _bans,
            NextId = _nextId,
        };
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    public Task AddBanAsync(BanEntry entry)
    {
        lock (_lock)
        {
            entry.Id = _nextId++;
            entry.BannedAt = entry.BannedAt == default ? DateTime.UtcNow : entry.BannedAt;
            _bans.Add(entry);
            Save();
            _logger.LogInformation("Ban entry added for {PlayerName} (ID: {Id}).", entry.PlayerName ?? "Unknown", entry.Id);
        }

        return Task.CompletedTask;
    }

    public Task RemoveBanAsync(int id)
    {
        lock (_lock)
        {
            var removed = _bans.RemoveAll(b => b.Id == id);
            if (removed > 0)
            {
                Save();
                _logger.LogInformation("Ban entry {Id} removed.", id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<List<BanEntry>> GetAllBansAsync()
    {
        lock (_lock)
        {
            var result = _bans.OrderByDescending(b => b.BannedAt).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<bool> IsBannedAsync(string? ipAddress, string? puid = null, string? fid = null, string? friendCode = null)
    {
        lock (_lock)
        {
            var banned = _bans.Any(b =>
                IpMatches(b.IpAddress, ipAddress) ||
                (!string.IsNullOrEmpty(puid) && b.Puid == puid) ||
                (!string.IsNullOrEmpty(fid) && b.Fid == fid) ||
                (!string.IsNullOrEmpty(friendCode) && !string.IsNullOrEmpty(b.FriendCode) &&
                 b.FriendCode.Equals(friendCode, StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult(banned);
        }
    }

    public Task<BanEntry?> FindBanAsync(string? ipAddress, string? playerName = null, string? puid = null, string? fid = null, string? friendCode = null)
    {
        lock (_lock)
        {
            var ban = _bans.FirstOrDefault(b =>
                IpMatches(b.IpAddress, ipAddress) ||
                (!string.IsNullOrEmpty(puid) && b.Puid == puid) ||
                (!string.IsNullOrEmpty(fid) && b.Fid == fid) ||
                (!string.IsNullOrEmpty(friendCode) && !string.IsNullOrEmpty(b.FriendCode) &&
                 b.FriendCode.Equals(friendCode, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(playerName) && !string.IsNullOrEmpty(b.PlayerName) &&
                 b.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult(ban);
        }
    }

    /// <summary>
    ///     IP match that also supports CIDR ranges (e.g. "1.2.3.0/24").
    ///     IPv4-mapped-IPv6 addresses are normalized to IPv4 before comparison.
    /// </summary>
    private static bool IpMatches(string? entryIp, string? checkIp)
    {
        if (string.IsNullOrEmpty(entryIp) || string.IsNullOrEmpty(checkIp))
        {
            return false;
        }

        var normalized = NormalizeIp(checkIp);
        if (string.Equals(entryIp, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (entryIp.Contains('/')
            && IPNetwork.TryParse(entryIp, out var network)
            && IPAddress.TryParse(normalized, out var address))
        {
            return network.Contains(address);
        }

        return false;
    }

    private static string NormalizeIp(string ip)
    {
        if (IPAddress.TryParse(ip.Trim(), out var parsed))
        {
            return parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4().ToString() : parsed.ToString();
        }

        return ip.Trim();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
