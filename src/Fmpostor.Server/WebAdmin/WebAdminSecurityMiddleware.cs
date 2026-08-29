using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fmpostor.Server.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Security hardening for Turbo-620:
///     - SecurityHeadersMiddleware: adds X-Content-Type-Options / X-Frame-Options / Referrer-Policy.
///     - WebAdminCsrfMiddleware: rejects cookie-authenticated cross-site state-changing requests.
///       Requests carrying a valid Bearer session token are always allowed (a custom header
///       cannot be attached cross-site without CORS approval, so those are not CSRF-able).
///     - WebAdminPendingPasswordMiddleware: when the default admin still uses the hardcoded
///       initial password, only settings / change-password / logout are served.
///     - FixedWindowRateLimiter: tiny per-key fixed-window limiter used for /api/user,
///       /webadmin/api/login and panel AI chat.
///     - SafeFiles: shared file-name sanitizing + full-path containment helpers.
/// </summary>
public static class SafeFiles
{
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        name = name.Trim();
        // Strip any drive prefix / root and all path separators before char replacement.
        name = name.Replace("\\", "_").Replace("/", "_").Replace(":", "_");
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        // Never allow a bare dot sequence (".", "..", "...") as the final name.
        name = name.TrimStart('.');
        return name.Trim();
    }

    public static string? ResolveInside(string baseDir, string? fileName)
    {
        var safe = SanitizeFileName(fileName);
        if (string.IsNullOrEmpty(safe))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(baseDir, safe));
            var baseFull = Path.GetFullPath(baseDir);
            if (!full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(baseFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(full, baseFull, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        return full;
    }
}

/// <summary>
///     Adds baseline security headers to every HTTP response.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-XSS-Protection"] = "1; mode=block";
        await _next(context);
    }
}

/// <summary>
///     CSRF protection for /webadmin/api/*: state-changing requests (POST/PUT/DELETE/PATCH)
///     are accepted when any of the following holds:
///       1. A valid Bearer session token is presented (custom headers cannot be attached
///          by a cross-site page without a CORS preflight approval);
///       2. No webadmin_token cookie is presented either (the request is not
///          cookie-authenticated, so there is nothing to CSRF — it will simply
///          fail authentication downstream if the caller is unauthenticated);
///       3. The Origin/Referer authority matches the request Host authority
///          (same-host deployments: any port/scheme combination counts, e.g.
///          panel on :80 or https while the API answers on :22023);
///       4. The Origin/Referer authority is listed in WebAdmin:CorsAllowedOrigins
///          (operator-declared panel origins for exotic proxy topologies).
///     Everything else (cookie-authenticated cross-site write) is rejected with 403.
/// </summary>
public sealed class WebAdminCsrfMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WebAdminAuthService _auth;
    private readonly WebAdminConfig _config;

    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
    { "POST", "PUT", "DELETE", "PATCH" };

    public WebAdminCsrfMiddleware(RequestDelegate next, WebAdminAuthService auth, IOptions<WebAdminConfig> config)
    {
        _next = next;
        _auth = auth;
        _config = config.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (MutatingMethods.Contains(context.Request.Method)
            && path.StartsWithSegments("/webadmin/api")
            && !IsExempt(path))
        {
            var bearer = context.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(bearer) && bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = bearer["Bearer ".Length..].Trim();
                if (_auth.ValidateSession(token) != null)
                {
                    await _next(context);
                    return;
                }
            }

            // Not cookie-authenticated → nothing to forge; let the API's own
            // auth layer answer (401/403) instead of a misleading CSRF error.
            var hasSessionCookie = context.Request.Cookies.TryGetValue("webadmin_token", out var cookieToken)
                                   && !string.IsNullOrEmpty(cookieToken);
            if (!hasSessionCookie)
            {
                await _next(context);
                return;
            }

            // Cookie-authenticated: accept only same-host or operator-allowed origins.
            var origin = context.Request.Headers["Origin"].FirstOrDefault();
            if (string.IsNullOrEmpty(origin))
            {
                // Some proxies strip Origin but keep Referer — try it as a fallback.
                origin = GetAuthority(context.Request.Headers["Referer"].FirstOrDefault());
            }

            if (!string.IsNullOrEmpty(origin)
                && !origin.Equals("null", StringComparison.OrdinalIgnoreCase)
                && IsAllowedAuthority(origin, context.Request.Host))
            {
                await _next(context);
                return;
            }

            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"success\":false,\"message\":\"Cross-site request blocked.\"}");
            return;
        }

        await _next(context);
    }

    private static bool IsExempt(PathString path)
    {
        // Login and logout are exempt: login establishes a session (no cross-site
        // privilege to abuse), logout is low-impact and the panel calls it with the
        // Bearer token attached.
        return path.StartsWithSegments("/webadmin/api/login")
            || path.StartsWithSegments("/webadmin/api/logout");
    }

    /// <summary>Extracts the scheme://authority part of a URL string, or empty.</summary>
    private static string GetAuthority(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private bool IsAllowedAuthority(string origin, HostString host)
    {
        if (AuthorityMatches(origin, host))
        {
            return true;
        }

        // Operator-declared panel origins (covers reverse proxies that rewrite Host).
        var allowed = _config.CorsAllowedOrigins;
        if (allowed != null && allowed.Count > 0)
        {
            var originAuthority = GetAuthority(origin).TrimEnd('/');
            foreach (var entry in allowed)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }
                var candidate = entry.Trim().TrimEnd('/');
                if (!candidate.Contains("://"))
                {
                    candidate = "https://" + candidate;
                }
                if (string.Equals(originAuthority, GetAuthority(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AuthorityMatches(string origin, HostString host)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Same host (IP or DNS name) on any port / scheme counts as the operator's
        // own console: panels are commonly served on :80/https while the API answers
        // on :22023, and reverse proxies may rewrite the port either way. A hostile
        // page would live on a different host entirely.
        if (string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow scheme-mismatched same authority (https panel -> http backend on the
        // same host:port), including default-port equivalence.
        var originAuthority = uri.Authority; // host[:port]
        var requestAuthority = host.Value ?? string.Empty;
        return string.Equals(originAuthority, requestAuthority, StringComparison.OrdinalIgnoreCase)
            && (uri.Port == host.Port
                || (uri.Port == -1 && host.Port == (uri.Scheme == "https" ? 443 : 80))
                || (host.Port == -1 && uri.Port == 80 && uri.Scheme == "http"));
    }
}

/// <summary>
///     When a session user still authenticates with the hardcoded default admin
///     password, restrict the API surface to password change / logout / settings
///     until the password has been changed.
/// </summary>
public sealed class WebAdminPendingPasswordMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WebAdminAuthService _auth;

    private static readonly string[] AllowedSuffixes =
    {
        "/api/settings",
        "/api/change-password",
        "/api/logout",
        "/api/ping",
        "/api/config",
        "/api/version",
    };

    public WebAdminPendingPasswordMiddleware(RequestDelegate next, WebAdminAuthService auth)
    {
        _next = next;
        _auth = auth;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/webadmin/api") && !IsAllowed(path))
        {
            string? token = null;
            if (context.Request.Cookies.TryGetValue("webadmin_token", out var cookieToken)
                && !string.IsNullOrEmpty(cookieToken))
            {
                token = cookieToken;
            }
            else
            {
                var header = context.Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = header["Bearer ".Length..].Trim();
                }
            }

            var user = _auth.ValidateSession(token);
            if (user != null && _auth.NeedsPasswordChange(user))
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(
                    "{\"success\":false,\"message\":\"The default admin password must be changed before using the panel.\",\"mustChangePassword\":true}");
                return;
            }
        }

        await _next(context);
    }

    private static bool IsAllowed(PathString path)
    {
        foreach (var suffix in AllowedSuffixes)
        {
            if (path.Value!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
///     Minimal thread-safe fixed-window rate limiter (per string key).
/// </summary>
public sealed class FixedWindowRateLimiter
{
    private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _hits = new();
    private readonly int _limit;
    private readonly TimeSpan _window;
    private DateTime _lastSweep = DateTime.UtcNow;

    public FixedWindowRateLimiter(int limit, TimeSpan window)
    {
        _limit = Math.Max(1, limit);
        _window = window;
    }

    public bool Allow(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return true;
        }

        var now = DateTime.UtcNow;
        var allowed = true;
        _hits.AddOrUpdate(key,
            _ => (1, now),
            (_, entry) =>
            {
                if (now - entry.WindowStart >= _window)
                {
                    return (1, now);
                }

                if (entry.Count >= _limit)
                {
                    allowed = false;
                    return entry;
                }

                return (entry.Count + 1, entry.WindowStart);
            });

        // Occasional sweep so long-tail keys do not accumulate forever.
        if (_hits.Count > 4096 || now - _lastSweep > TimeSpan.FromMinutes(10))
        {
            foreach (var kv in _hits)
            {
                if (now - kv.Value.WindowStart >= _window)
                {
                    _hits.TryRemove(kv.Key, out _);
                }
            }

            _lastSweep = now;
        }

        return allowed;
    }
}

/// <summary>
///     Blocks oversized request bodies for the API surface (the default Kestrel limit
///     is far too generous for these JSON endpoints).
/// </summary>
public sealed class RequestBodyLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly long _maxBytes;

    public RequestBodyLimitMiddleware(RequestDelegate next, long maxBytes = 128 * 1024)
    {
        _next = next;
        _maxBytes = maxBytes;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isApi = context.Request.Path.StartsWithSegments("/webadmin/api")
                    || context.Request.Path.StartsWithSegments("/api/user")
                    || context.Request.Path.StartsWithSegments("/api/games");
        if (isApi && context.Request.ContentLength > _maxBytes)
        {
            context.Response.StatusCode = 413;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"success\":false,\"message\":\"Request body too large.\"}");
            return;
        }

        await _next(context);
    }
}