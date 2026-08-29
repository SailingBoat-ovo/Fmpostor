using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace Fmpostor.Server.Http;

/// <summary>
///     Resolves the real client IP when the HTTP traffic is proxied by a CDN
///     (Cloudflare, Tencent CDN, Aliyun CDN, etc.) or a reverse proxy.
/// </summary>
public static class ClientIpHelper
{
    /// <summary>
    ///     Returns the real client IP string for an HTTP request.
    ///     Priority: CF-Connecting-IP → X-Real-IP → X-Forwarded-For (first entry) → RemoteIpAddress.
    /// </summary>
    public static string GetClientIp(HttpRequest request, bool trustAllProxies = true, IReadOnlyCollection<string>? trustedProxies = null)
    {
        var remoteIp = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var remoteTrusted = trustAllProxies || IsTrustedProxy(remoteIp, trustedProxies);
        if (!remoteTrusted)
        {
            return Normalize(remoteIp);
        }

        // Cloudflare is the most explicit source.
        var cf = request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cf) && IPAddress.TryParse(cf.Trim().Split(',')[0].Trim(), out _))
        {
            return Normalize(cf.Trim().Split(',')[0].Trim());
        }

        var realIp = request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realIp) && IPAddress.TryParse(realIp.Trim().Split(',')[0].Trim(), out _))
        {
            return Normalize(realIp.Trim().Split(',')[0].Trim());
        }

        var forwardedFor = request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out _))
            {
                return Normalize(first);
            }
        }

        return Normalize(remoteIp);
    }

    private static bool IsTrustedProxy(string remoteIp, IReadOnlyCollection<string>? trustedProxies)
    {
        if (trustedProxies == null || trustedProxies.Count == 0)
        {
            return false;
        }

        if (!IPAddress.TryParse(remoteIp, out var address))
        {
            return false;
        }

        foreach (var entry in trustedProxies)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var candidate = entry.Trim();
            if (candidate.Contains('/'))
            {
                if (IPNetwork.TryParse(candidate, out var network) && network.Contains(address))
                {
                    return true;
                }
            }
            else if (IPAddress.TryParse(candidate, out var single) && single.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string ip)
    {
        if (IPAddress.TryParse(ip, out var parsed) && parsed.IsIPv4MappedToIPv6)
        {
            return parsed.MapToIPv4().ToString();
        }

        return ip;
    }
}
