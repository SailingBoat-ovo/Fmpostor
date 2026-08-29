using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Maps a client connection (by IP) to the identity registered during the
///     HTTP /api/user token exchange. Also handles IPv4-mapped-IPv6 addresses
///     so CDN/proxy-normalized IPs and direct UDP source IPs match reliably.
/// </summary>
public class PlayerIdentityService
{
    private readonly ConcurrentDictionary<string, IdentityEntry> _byIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IdentityEntry> _byPuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, IdentityEntry> _byPort = new();
    private readonly ILogger<PlayerIdentityService> _logger;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    // Hard caps: with spoofable proxy headers an attacker could otherwise register
    // unlimited unique keys. Beyond the cap, expired entries are purged and the
    // tables are trimmed to the most recent data.
    private const int MaxIpEntries = 50_000;
    private const int MaxPuidEntries = 50_000;

    public PlayerIdentityService(ILogger<PlayerIdentityService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Removes characters outside the safe identity charset so a hostile
    ///     friend code / PUID can never poison log lines, cache files or keys.
    /// </summary>
    public static string SanitizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().Take(64).Where(c =>
            char.IsLetterOrDigit(c) || c is '#' or '_' or '-' or '.' or '/' or '@').ToArray();
        return new string(chars);
    }

    public static string GenerateFriendCode(string puid)
    {
        if (string.IsNullOrEmpty(puid))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(puid));
        var disc = BitConverter.ToUInt16(hash, 0) % 10000;
        return $"failauth#{disc:D4}";
    }

    public static bool IsPlaceholderFriendCode(string friendCode)
    {
        return string.Equals(friendCode, "kidcode#8888", StringComparison.OrdinalIgnoreCase)
            || string.Equals(friendCode, "nocode#9999", StringComparison.OrdinalIgnoreCase)
            || string.Equals(friendCode, "unchecked#0001", StringComparison.OrdinalIgnoreCase)
            || friendCode.StartsWith("failauth#", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     帆船FID: 形如 1234/abcde，由 PUID 通过 SHA256 确定性生成。
    ///     Same PUID = same FID on any 帆船 server.
    /// </summary>
    public static string GenerateFid(string puid)
    {
        if (string.IsNullOrEmpty(puid))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(puid));
        uint d = (uint)(hash[0] * 7919 + hash[1] * 6271 + hash[2] * 5113 + hash[3] * 4093);
        d = (d ^ (d >> 13)) * 0x5bd1e995;
        d = (d ^ (d >> 15)) % 10000;
        var c = new char[5];
        for (int i = 0; i < 5; i++)
        {
            var v = (hash[4 + i] * 31 + hash[9 + i] * 17 + hash[14 + i] * 7) % 26;
            c[i] = (char)('a' + v);
        }

        return $"{d:D4}/{new string(c)}";
    }

    public void Register(string ipAddress, string puid, string friendCode, int port = 0)
    {
        puid = SanitizeId(puid);
        if (string.IsNullOrEmpty(puid))
        {
            return;
        }

        var ip = NormalizeIpString(ipAddress);
        if (string.IsNullOrEmpty(ip))
        {
            ip = "0.0.0.0";
        }

        var fid = GenerateFid(puid);
        var entry = new IdentityEntry(ip, puid, SanitizeId(friendCode), fid, DateTime.UtcNow, port);

        _byIp[ip] = entry;
        _byPuid[puid] = entry;
        EnforceCaps();
    }

    /// <summary>
    ///     Registers an identity under a delta UDP port (allocated during the HTTP
    ///     token exchange). Used to match the TCP auth session to the UDP connection
    ///     when multiple players share the same NAT/CDN IP.
    /// </summary>
    public void RegisterByPort(int port, string puid, string friendCode)
    {
        if (port <= 0 || string.IsNullOrEmpty(puid))
        {
            return;
        }

        puid = SanitizeId(puid);
        if (string.IsNullOrEmpty(puid))
        {
            return;
        }

        var fid = GenerateFid(puid);
        var entry = new IdentityEntry($"port:{port}", puid, SanitizeId(friendCode), fid, DateTime.UtcNow, port);

        _byPort[port] = entry;
        _byPuid[puid] = entry;
        EnforceCaps();
    }

    private void EnforceCaps()
    {
        if (_byIp.Count <= MaxIpEntries && _byPuid.Count <= MaxPuidEntries)
        {
            return;
        }

        foreach (var kv in _byIp)
        {
            if (IsExpired(kv.Value))
            {
                _byIp.TryRemove(kv.Key, out _);
            }
        }

        foreach (var kv in _byPuid)
        {
            if (IsExpired(kv.Value))
            {
                _byPuid.TryRemove(kv.Key, out _);
            }
        }

        // Still over the cap (flood faster than TTL): drop the oldest quarter.
        if (_byIp.Count > MaxIpEntries)
        {
            foreach (var key in _byIp.OrderBy(kv => kv.Value.Time).Take(_byIp.Count / 4).Select(kv => kv.Key).ToList())
            {
                _byIp.TryRemove(key, out _);
            }

            _logger.LogWarning("[PlayerIdentity] IP table exceeded {Cap} entries; trimmed.", MaxIpEntries);
        }

        if (_byPuid.Count > MaxPuidEntries)
        {
            foreach (var key in _byPuid.OrderBy(kv => kv.Value.Time).Take(_byPuid.Count / 4).Select(kv => kv.Key).ToList())
            {
                _byPuid.TryRemove(key, out _);
            }

            _logger.LogWarning("[PlayerIdentity] PUID table exceeded {Cap} entries; trimmed.", MaxPuidEntries);
        }
    }

    public IdentityEntry? LookupByPort(int port)
    {
        if (port <= 0)
        {
            return null;
        }

        if (_byPort.TryGetValue(port, out var entry))
        {
            if (!IsExpired(entry))
            {
                return entry;
            }

            _byPort.TryRemove(port, out _);
        }

        return null;
    }

    public void RemoveByPort(int port)
    {
        if (port > 0)
        {
            _byPort.TryRemove(port, out _);
        }
    }

    public IdentityEntry? Lookup(IPAddress? clientAddr)
    {
        if (clientAddr == null)
        {
            return null;
        }

        var ip = NormalizeIpString(clientAddr.ToString());
        if (string.IsNullOrEmpty(ip))
        {
            return null;
        }

        if (_byIp.TryGetValue(ip, out var entry))
        {
            if (!IsExpired(entry))
            {
                return entry;
            }

            _byIp.TryRemove(ip, out _);
        }

        _logger.LogDebug("[PlayerIdentity] No match for {Ip}", ip);
        return null;
    }

    public IdentityEntry? LookupByPuid(string puid)
    {
        if (string.IsNullOrEmpty(puid))
        {
            return null;
        }

        if (_byPuid.TryGetValue(puid, out var entry) && !IsExpired(entry))
        {
            return entry;
        }

        _byPuid.TryRemove(puid, out _);
        return null;
    }

    private static bool IsExpired(IdentityEntry entry)
    {
        return DateTime.UtcNow - entry.Time > Ttl;
    }

    private static string NormalizeIpString(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return string.Empty;
        }

        if (IPAddress.TryParse(ip.Trim(), out var parsed))
        {
            return parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4().ToString() : parsed.ToString();
        }

        return ip.Trim();
    }

    public record IdentityEntry(string IpAddress, string Puid, string FriendCode, string Fid, DateTime Time, int Port);
}
