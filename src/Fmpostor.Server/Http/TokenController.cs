using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Innersloth;
using Fmpostor.Server.Net;
using Fmpostor.Server.WebAdmin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.Http;

[Route("/api/user")]
[ApiController]
public sealed class TokenController : ControllerBase
{
    // Self-hosted Innersloth gateway. Overridable via config.json
    // (WebAdmin:InnerslothApiBaseUrl) — e.g. switching to an EdgeOne-proxied
    // https:// URL — without recompiling. Legacy hardcoded default below.
    private const string InnerslothApiBaseUrlDefault = "http://backend.playerinfo.impwm.fcaugame.cn:58080";

    private string InnerslothApiBaseUrl =>
        string.IsNullOrWhiteSpace(_webAdminConfig.InnerslothApiBaseUrl)
            ? InnerslothApiBaseUrlDefault
            : _webAdminConfig.InnerslothApiBaseUrl.Trim().TrimEnd('/');

    private static readonly TimeSpan InnerslothRequestTimeout = TimeSpan.FromSeconds(10);

    // Per-connection rate limit for the unauthenticated token endpoint. Keyed on the
    // DIRECT remote IP (not proxy headers, which are client-controlled): stops port
    // pool / identity-table exhaustion floods while allowing whole-CDN bursts.
    private static readonly FixedWindowRateLimiter TokenRateLimiter = new(120, TimeSpan.FromMinutes(1));

    // Friend codes must look like "name#1234" with a bounded, URL/log-safe charset;
    // anything else (e.g. HTML payloads) is treated as a placeholder.
    private static readonly System.Text.RegularExpressions.Regex FriendCodeRegex =
        new(@"^[A-Za-z0-9_.\-]{1,48}#[A-Za-z0-9_\-]{1,16}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // PUIDs are EOS identifiers: bounded alphanumeric/dash strings only.
    private static readonly System.Text.RegularExpressions.Regex PuidRegex =
        new(@"^[A-Za-z0-9_\-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string CacheFileDir =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

    private static readonly string CacheFilePath =
        Path.Combine(CacheFileDir, "PuidToFriendCode.txt");

    private static readonly object FileLock = new();

    private static readonly HttpClient ForwardClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly PlayerIdentityService _identityService;
    private readonly DeltaPortPoolService _portPool;
    private readonly IDeltaListenerManager _deltaListenerManager;
    private readonly ILogger<TokenController> _logger;
    private readonly WebAdminConfig _webAdminConfig;

    public TokenController(
        PlayerIdentityService identityService,
        DeltaPortPoolService portPool,
        IDeltaListenerManager deltaListenerManager,
        ILogger<TokenController> logger,
        IOptions<WebAdminConfig> webAdminConfig)
    {
        _identityService = identityService;
        _portPool = portPool;
        _deltaListenerManager = deltaListenerManager;
        _logger = logger;
        _webAdminConfig = webAdminConfig.Value;
    }

    [HttpPost]
    public async Task<IActionResult> GetTokenAsync([FromBody] TokenRequest request)
    {
        var directIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!TokenRateLimiter.Allow(directIp))
        {
            return StatusCode(429, new { success = false, message = "Too many requests." });
        }

        var authHeader = HttpContext.Request.Headers["Authorization"].FirstOrDefault();
        var eosToken = authHeader != null && authHeader.StartsWith("Bearer ")
            ? authHeader["Bearer ".Length..]
            : string.Empty;

        // The reference server treats the EOS token as authoritative: the PUID is
        // extracted from the JWT, not blindly trusted from the client request.
        var puid = SanitizePuid(request.ProductUserId);
        if (!string.IsNullOrEmpty(eosToken))
        {
            var tokenPuid = SanitizePuid(TryExtractPuidFromEosToken(eosToken));
            if (!string.IsNullOrEmpty(tokenPuid))
            {
                if (!string.Equals(tokenPuid, puid, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "[Token] Client PUID {ClientPuid} differs from EOS token PUID {TokenPuid}; using token PUID",
                        puid, tokenPuid);
                }

                puid = tokenPuid;
            }
        }

        var actualIp = ClientIpHelper.GetClientIp(
            HttpContext.Request,
            _webAdminConfig.TrustAllProxies,
            _webAdminConfig.TrustedProxies);

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

        var friendCode = await ResolveFriendCodeAsync(puid, request.FriendCode, eosToken);

        // Register under both the real client IP (from proxy headers) and the direct
        // connection IP, so UDP matching works whether or not HTTP went through a CDN.
        _identityService.Register(actualIp, puid, friendCode);
        if (!string.IsNullOrEmpty(remoteIp) &&
            !string.Equals(remoteIp, actualIp, StringComparison.OrdinalIgnoreCase))
        {
            _identityService.Register(remoteIp, puid, friendCode);
        }

        // Delta port pool: allocate a dedicated UDP port for this PUID so the TCP
        // auth session can be matched to the UDP connection exactly, even when many
        // players share one NAT/CDN IP. The IP entries are re-registered with the
        // port so /api/games can hand it out to the client.
        var deltaPort = _portPool.AllocatePort(puid);
        if (deltaPort > 0)
        {
            _identityService.RegisterByPort(deltaPort, puid, friendCode);
            _identityService.Register(actualIp, puid, friendCode, deltaPort);
            if (!string.IsNullOrEmpty(remoteIp) &&
                !string.Equals(remoteIp, actualIp, StringComparison.OrdinalIgnoreCase))
            {
                _identityService.Register(remoteIp, puid, friendCode, deltaPort);
            }

            _ = _deltaListenerManager.StartDeltaListenerAsync(deltaPort);
            _logger.LogInformation("[Token] Delta port {Port} allocated for PUID={Puid}", deltaPort, puid);
        }

        var token = new Token
        {
            Content = new TokenPayload
            {
                ProductUserId = puid,
                ClientVersion = request.ClientVersion,
            },
            Hash = "fmpostor_was_here",
            Port = deltaPort,
        };

        var serialized = JsonSerializer.SerializeToUtf8Bytes(token);
        return Ok(Convert.ToBase64String(serialized));
    }

    // ========== FriendCode resolution ==========

    private static string? SanitizePuid(string? puid)
    {
        if (string.IsNullOrWhiteSpace(puid))
        {
            return null;
        }

        puid = puid.Trim();
        return PuidRegex.IsMatch(puid) ? puid : null;
    }

    /// <summary>
    ///     A client-supplied friend code is only accepted when it matches the real
    ///     EOS format ("name#1234"). This prevents attacker-chosen payloads (HTML/JS)
    ///     and impersonation through obviously invalid values from ever entering the
    ///     identity system, the panel, or persistent files.
    /// </summary>
    private static bool IsPlausibleFriendCode(string? friendCode)
    {
        return !string.IsNullOrWhiteSpace(friendCode) && FriendCodeRegex.IsMatch(friendCode.Trim());
    }

    private async Task<string> ResolveFriendCodeAsync(string puid, string? clientFriendCode, string eosToken)
    {
        // 1. Client-provided code (fast path) — only when it looks like a real code.
        if (IsPlausibleFriendCode(clientFriendCode) && !PlayerIdentityService.IsPlaceholderFriendCode(clientFriendCode))
        {
            _logger.LogInformation("[Token] Client provided FriendCode PUID={Puid} FC={Fc}", puid, clientFriendCode.Trim());
            return clientFriendCode.Trim();
        }

        // 2. Persistent PUID -> FriendCode cache.
        var cached = TryGetFriendCodeFromCache(puid);
        if (!string.IsNullOrEmpty(cached))
        {
            _logger.LogInformation("[Token] FriendCode cache hit PUID={Puid} FC={Fc}", puid, cached);
            return cached;
        }

        // 3. Query the self-hosted Innersloth gateway with the EOS token.
        var fetched = await FetchFromInnerslothAsync(eosToken, puid);
        if (!string.IsNullOrEmpty(fetched))
        {
            SaveFriendCodeToCache(puid, fetched);
            return fetched;
        }

        // 4. Deterministic fallback so the panel always has something to show.
        var fallback = PlayerIdentityService.GenerateFriendCode(puid);
        _logger.LogWarning("[Token] FriendCode fetch failed for PUID={Puid}, using fallback: {Fc}", puid, fallback);
        return fallback;
    }

    private static string TruncateForLog(string s, int max = 200)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        return s.Length <= max ? s : s[..max] + "...";
    }

    private async Task<string?> FetchFromInnerslothAsync(string eosToken, string productUserId)
    {
        if (string.IsNullOrEmpty(eosToken))
        {
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(InnerslothRequestTimeout);

        try
        {
            // Preferred: GET /api/user/username (used by the reference server).
            using var getReq = new HttpRequestMessage(HttpMethod.Get, $"{InnerslothApiBaseUrl}/api/user/username");
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", eosToken);
            getReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
            if (!string.IsNullOrEmpty(_webAdminConfig.InnerslothProxyKey))
            {
                getReq.Headers.TryAddWithoutValidation("X-Proxy-Key", _webAdminConfig.InnerslothProxyKey);
            }

            using var getResp = await ForwardClient.SendAsync(getReq, timeoutCts.Token);
            if (getResp.IsSuccessStatusCode)
            {
                var body = await getResp.Content.ReadAsStringAsync(timeoutCts.Token);
                // Raw Innersloth responses contain player PII — Debug level only.
                _logger.LogDebug("[Token] Innersloth raw response for PUID={Puid}: {Json}", productUserId, TruncateForLog(body));
                var friendCode = TryExtractFriendCodeFromInnersloth(body);
                if (IsPlausibleFriendCode(friendCode) && !PlayerIdentityService.IsPlaceholderFriendCode(friendCode))
                {
                    _logger.LogInformation("[Token] FriendCode fetched from Innersloth username API PUID={Puid} FC={Fc}", productUserId, friendCode);
                    return friendCode.Trim();
                }
            }
            else
            {
                _logger.LogDebug("[Token] Innersloth username API returned {Status} for PUID={Puid}", getResp.StatusCode, productUserId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Token] Innersloth username API failed for PUID={Puid}: {Error}", productUserId, ex.Message);
        }

        return null;
    }

    private static string? TryExtractPuidFromEosToken(string eosToken)
    {
        try
        {
            var parts = eosToken.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2:
                    payload += "==";
                    break;
                case 3:
                    payload += "=";
                    break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String)
            {
                return sub.GetString();
            }

            if (root.TryGetProperty("puid", out var puid) && puid.ValueKind == JsonValueKind.String)
            {
                return puid.GetString();
            }
        }
        catch
        {
            // Not a JWT or unreadable payload.
        }

        return null;
    }

    // ========== Cache helpers ==========

    private static string? TryGetFriendCodeFromCache(string productUserId)
    {
        lock (FileLock)
        {
            if (!System.IO.File.Exists(CacheFilePath))
            {
                return null;
            }

            foreach (var line in System.IO.File.ReadLines(CacheFilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0] == productUserId
                    && IsPlausibleFriendCode(parts[1])
                    && !PlayerIdentityService.IsPlaceholderFriendCode(parts[1]))
                {
                    return parts[1];
                }
            }
        }

        return null;
    }

    private static void SaveFriendCodeToCache(string productUserId, string friendCode)
    {
        // Never persist values that are not plausible friend codes (the gateway
        // response is third-party data) and keep the cache line format strict.
        if (string.IsNullOrEmpty(productUserId) || !PuidRegex.IsMatch(productUserId)
            || !IsPlausibleFriendCode(friendCode))
        {
            return;
        }

        lock (FileLock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(CacheFileDir);

                if (System.IO.File.Exists(CacheFilePath))
                {
                    foreach (var line in System.IO.File.ReadLines(CacheFilePath))
                    {
                        if (line.StartsWith(productUserId + "=", StringComparison.Ordinal))
                        {
                            return;
                        }
                    }
                }

                System.IO.File.AppendAllText(CacheFilePath, $"{productUserId}={friendCode.Trim()}{Environment.NewLine}");
            }
            catch
            {
                // Best-effort cache.
            }
        }
    }

    private static string? TryExtractFriendCodeFromInnersloth(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var attrs = root.TryGetProperty("data", out var data)
                        && data.TryGetProperty("attributes", out var a) ? a : root;

            var username = attrs.TryGetProperty("username", out var u) ? u.GetString() : null;
            var discriminator = attrs.TryGetProperty("discriminator", out var d) ? d.GetString() : null;

            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(discriminator))
            {
                return $"{username}#{discriminator}";
            }
        }
        catch
        {
            // Fall through.
        }

        return null;
    }

    // ========== Request/response models ==========

    public class TokenRequest
    {
        [JsonPropertyName("Puid")]
        public required string ProductUserId { get; init; }

        [JsonPropertyName("Username")]
        public required string Username { get; init; }

        [JsonPropertyName("ClientVersion")]
        public required int ClientVersion { get; init; }

        [JsonPropertyName("Language")]
        public required Language Language { get; init; }

        [JsonPropertyName("FriendCode")]
        public string? FriendCode { get; init; }
    }

    public sealed class Token
    {
        [JsonPropertyName("Content")]
        public required TokenPayload Content { get; init; }

        [JsonPropertyName("Hash")]
        public required string Hash { get; init; }

        [JsonPropertyName("Port")]
        public int Port { get; init; }
    }

    public sealed class TokenPayload
    {
        private static readonly DateTime DefaultExpiryDate = new(2012, 12, 21);

        [JsonPropertyName("Puid")]
        public required string ProductUserId { get; init; }

        [JsonPropertyName("ClientVersion")]
        public required int ClientVersion { get; init; }

        [JsonPropertyName("ExpiresAt")]
        public DateTime ExpiresAt { get; init; } = DefaultExpiryDate;
    }
}
