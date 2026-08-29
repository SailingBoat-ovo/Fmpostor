using System.Collections.Generic;

namespace Fmpostor.Server.WebAdmin;

public class WebAdminConfig
{
    public const string Section = "WebAdmin";

    /// <summary>
    ///     Default admin username and password, hardcoded and used only when
    ///     webadmin_auth.json does not exist yet or is in the legacy format.
    ///     Passwords are stored in plaintext in webadmin_auth.json and must never
    ///     appear in config.json.
    /// </summary>
    public const string DefaultAdminUsername = "fanadmin";

    public const string DefaultAdminPassword = "fanchuanimpostorwebuinb";

    /// <summary>
    ///     Verification server is hardcoded and cannot be changed through config.
    /// </summary>


    public string AuthFile { get; set; } = "webadmin_auth.json";
    public string BanFile { get; set; } = "webadmin_bans.json";
    public string LogFile { get; set; } = "webadmin_logs.json";
    public string ChatLogDir { get; set; } = "chatlog";
    public string PathPrefix { get; set; } = "/webadmin";

    /// <summary>
    ///     Filter file storing banned words (chat filter) — hot reloadable.
    /// </summary>
    public string FilterFile { get; set; } = "webadmin_filter.json";

    /// <summary>
    ///     Welcome message file — hot reloadable.
    /// </summary>
    public string WelcomeFile { get; set; } = "webadmin_welcome.json";

    /// <summary>
    ///     In-game report file.
    /// </summary>
    public string ReportFile { get; set; } = "webadmin_reports.json";

    /// <summary>
    ///     Player title system file (称号) — hot reloadable.
    /// </summary>
    public string TitlesFile { get; set; } = "webadmin_titles.json";

    /// <summary>
    ///     Player play-time tracking file (welcome plugin data) — hot reloadable.
    /// </summary>
    public string PlayerTimesFile { get; set; } = "webadmin_player_times.json";

    /// <summary>
    ///     QQ room monitor (OneBot) settings file — hot reloadable.
    /// </summary>
    public string RoomMonitorFile { get; set; } = "webadmin_monitor.json";

    /// <summary>
    ///     Banned-word filter trigger statistics (friend code + word + count).
    /// </summary>
    public string FilterStatsFile { get; set; } = "webadmin_filterstats.json";

    /// <summary>
    ///     QQ-group broadcast delivery statistics (sender QQ/friend code + group).
    /// </summary>
    public string BroadcastStatsFile { get; set; } = "webadmin_broadcaststats.json";

    /// <summary>
    ///     Structured player behavior log file.
    /// </summary>
    public string PlayerLogFile { get; set; } = "webadmin_player_logs.json";

    /// <summary>
    ///     Per-friend-code player stats file.
    /// </summary>
    public string PlayerStatsFile { get; set; } = "webadmin_player_stats.json";

    /// <summary>
    ///     Custom game code list file (4 or 6 letter A-Z codes).
    /// </summary>
    public string GameCodesFile { get; set; } = "webadmin_codes.json";

    /// <summary>
    ///     Delta UDP port pool: when enabled, every authenticated player gets a
    ///     dedicated UDP port that ties the HTTP auth session to the UDP connection
    ///     (solves NAT/CDN multi-player same-IP misidentification). Defaults to on.
    /// </summary>
    public bool DeltaPortsEnabled { get; set; } = true;

    /// <summary>
    ///     First port of the delta range (inclusive).
    /// </summary>
    public int DeltaPortStart { get; set; } = 22024;

    /// <summary>
    ///     Last port of the delta range (inclusive).
    /// </summary>
    public int DeltaPortEnd { get; set; } = 22223;

    /// <summary>
    ///     Optional shared secret sent as X-Proxy-Key to a self-hosted
    ///     Innersloth gateway.
    /// </summary>
    public string InnerslothProxyKey { get; set; } = string.Empty;

    /// <summary>
    ///     Base URL of the self-hosted Innersloth (friend-code) gateway. When
    ///     empty the built-in default
    ///     (http://backend.playerinfo.impwm.fcaugame.cn:58080) is used. Set this
    ///     to e.g. https://backend.playerinfo.impwm.fcaugame.cn when the gateway
    ///     is fronted by a reverse proxy / CDN such as Tencent EdgeOne.
    /// </summary>
    public string InnerslothApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    ///     Trust X-Forwarded-For / X-Real-IP / CF-Connecting-IP from any source.
    ///     Enable ONLY when the server is behind a CDN or reverse proxy; when it is
    ///     exposed directly these headers are client-controlled and enable IP-spoofing
    ///     (ban evasion, rate-limit bypass). Defaults to false since Turbo-620.
    /// </summary>
    public bool TrustAllProxies { get; set; } = false;

    /// <summary>
    ///     When TrustAllProxies is false, only these proxy IPs/CIDRs are trusted.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = new();

    /// <summary>
    ///     Optional CORS origin allow-list for the HTTP API (e.g. "https://panel.example.com").
    ///     Empty = any origin is allowed without credentials (legacy behaviour).
    ///     Cross-origin panel deployments should set this to the panel origin.
    /// </summary>
    public List<string> CorsAllowedOrigins { get; set; } = new();

    /// <summary>
    ///     Optional shared secret for verifying /check responses from the auth server.
    ///     When set (and the auth server runs with AUTH_SERVER_HMAC_KEY), the "success"
    ///     result is only accepted with a valid HMAC signature, blocking MITM/DNS spoofing
    ///     of the plain-HTTP verification channel.
    /// </summary>
    public string AuthServerHmacKey { get; set; } = string.Empty;

    /// <summary>
    ///     AI provider API key for the default endpoint. Nothing is compiled in —
    ///     set this (or the FMPOSTOR_AI_API_KEY environment variable) to your own
    ///     key to enable AI features. Leave empty to disable AI chat.
    /// </summary>
    public string AiApiKey { get; set; } = string.Empty;
}
