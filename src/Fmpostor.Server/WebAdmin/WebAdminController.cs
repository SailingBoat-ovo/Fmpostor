using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fmpostor.Server.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

[Route("/webadmin")]
[ApiController]
public class WebAdminController : ControllerBase
{
    private readonly GameTrackerService _tracker;
    private readonly BanService _banService;
    private readonly ChatService _chatService;
    private readonly ConnectionLogger _connLogger;
    private readonly WebAdminAuthService _auth;
    private readonly LogService _logService;
    private readonly WebAdminConfig _config;
    private readonly BadWordFilterService _filter;
    private readonly WelcomeService _welcome;
    private readonly ReportService _reportService;
    private readonly PlayerLogService _playerLogs;
    private readonly PlayerStatsService _playerStats;
    private readonly CustomGameCodeService _gameCodes;
    private readonly WebAdminSettingsService _settingsService;
    private readonly DeltaPortPoolService _portPool;
    private readonly BroadcastService _broadcast;
    private readonly GameReplayService _replays;
    private readonly PlayerFootprintService _footprints;
    private readonly DashboardService _dashboard;
    private readonly AiService _ai;
    private readonly TitleService _titles;
    private readonly RoomMonitorService _roomMonitor;
    private readonly AdminStatsService _adminStats;
    private readonly PlayerTimeService _playerTimes;
    private readonly ScheduleService _schedule;
    private readonly UpdateService _update;
    private readonly ILogger<WebAdminController> _logger;

    private static readonly string PluginVersion = "Turbo-650.0-20260905";

    public WebAdminController(
        GameTrackerService tracker,
        BanService banService,
        ChatService chatService,
        ConnectionLogger connLogger,
        WebAdminAuthService auth,
        LogService logService,
        IOptions<WebAdminConfig> config,
        BadWordFilterService filter,
        WelcomeService welcome,
        ReportService reportService,
        PlayerLogService playerLogs,
        PlayerStatsService playerStats,
        CustomGameCodeService gameCodes,
        WebAdminSettingsService settingsService,
        DeltaPortPoolService portPool,
        BroadcastService broadcast,
        GameReplayService replays,
        PlayerFootprintService footprints,
        DashboardService dashboard,
        AiService ai,
        TitleService titles,
        RoomMonitorService roomMonitor,
        AdminStatsService adminStats,
        PlayerTimeService playerTimes,
        ScheduleService schedule,
        UpdateService update,
        ILogger<WebAdminController> logger)
    {
        _tracker = tracker;
        _banService = banService;
        _chatService = chatService;
        _connLogger = connLogger;
        _auth = auth;
        _logService = logService;
        _config = config.Value;
        _filter = filter;
        _welcome = welcome;
        _reportService = reportService;
        _playerLogs = playerLogs;
        _playerStats = playerStats;
        _gameCodes = gameCodes;
        _settingsService = settingsService;
        _portPool = portPool;
        _broadcast = broadcast;
        _replays = replays;
        _footprints = footprints;
        _dashboard = dashboard;
        _ai = ai;
        _titles = titles;
        _roomMonitor = roomMonitor;
        _adminStats = adminStats;
        _playerTimes = playerTimes;
        _schedule = schedule;
        _update = update;
        _logger = logger;
    }

    private string? GetSessionToken()
    {
        if (Request.Cookies.TryGetValue("webadmin_token", out var token) && !string.IsNullOrEmpty(token))
            return token;
        var authHeader = Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
            return authHeader[7..];
        return null;
    }

    private string? GetSessionUser()
    {
        return _auth.ValidateSession(GetSessionToken());
    }

    private string GetClientIp()
    {
        var ip = ClientIpHelper.GetClientIp(Request, _config.TrustAllProxies, _config.TrustedProxies);
        return string.IsNullOrEmpty(ip) ? "unknown" : ip;
    }

    private static string MaskSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        return secret.Length <= 8 ? "****" : secret[..4] + "****" + secret[^4..];
    }

    // ========== Auth API ==========

    /// <summary>
    /// GET /webadmin/api/ping — no auth required, tests basic connectivity
    /// </summary>
    [HttpGet("api/ping")]
    public IActionResult Ping()
    {
        return Ok(new { success = true, message = "pong", time = DateTime.UtcNow });
    }

    /// <summary>
    /// POST /webadmin/api/login
    /// </summary>
    [HttpPost("api/login")]
    public async Task<IActionResult> Login([FromBody] JsonElement body)
    {
        try
        {
            var username = body.GetProperty("username").GetString() ?? "";
            var password = body.GetProperty("password").GetString() ?? "";
            var clientKey = GetClientIp() + "|" + username;

            if (_auth.TryAuthenticate(username, password, clientKey, out var role, out var locked))
            {
                var token = _auth.CreateSession(username);
                Response.Cookies.Append("webadmin_token", token, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = Request.IsHttps,
                    MaxAge = TimeSpan.FromHours(18),
                    Path = "/",
                });

                var mustChange = _auth.NeedsPasswordChange(username);
                _logService.AddLog("login", $"User '{username}' logged in", GetClientIp());
                return Ok(new { success = true, message = "Login successful.", role, token, mustChangePassword = mustChange });
            }

            if (locked)
            {
                var remaining = _auth.GetLoginLockRemaining(clientKey, username);
                _logService.AddLog("login_locked", $"Login locked for '{username}' ({remaining}s)", GetClientIp());
                return StatusCode(429, new { success = false, message = $"登录失败次数过多，已锁定 {remaining} 秒，请稍后再试。" });
            }

            _logService.AddLog("login_failed", $"Failed login attempt for '{username}'", GetClientIp());
            return Unauthorized(new { success = false, message = "Invalid username or password." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/logout
    /// </summary>
    [HttpPost("api/logout")]
    public async Task<IActionResult> Logout()
    {
        var user = GetSessionUser() ?? "";
        _logService.AddLog("logout", $"User '{user}' logged out", GetClientIp());
        _auth.DestroySession(GetSessionToken());
        Response.Cookies.Delete("webadmin_token");
        return Ok(new { success = true, message = "Logged out." });
    }

    // ========== Stats API ==========

    /// <summary>
    /// GET /webadmin/api/stats
    /// </summary>
    [HttpGet("api/stats")]
    public async Task<IActionResult> GetStats()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        return Ok(new
        {
            totalGames = _tracker.TotalGames,
            totalPlayers = _tracker.TotalPlayers,
        });
    }

    // ========== Players API ==========

    /// <summary>
    /// GET /webadmin/api/players
    /// </summary>
    [HttpGet("api/players")]
    public async Task<IActionResult> GetPlayers()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var players = _tracker.GetGames()
            .SelectMany(g => g.Players.Select(p => new
            {
                p.ClientId,
                p.PlayerName,
                p.IpAddress,
                p.FriendCode,
                p.Fid,
                p.ProductUserId,
                p.IsHost,
                p.IsConnected,
                p.Limbo,
                PingMs = p.PingMs,
                DeltaPort = p.DeltaPort,
                p.GameVersion,
                p.Platform,
                p.Mods,
                gameCode = g.Code,
            }));
        return Ok(players);
    }

    // ========== Games API ==========

    /// <summary>
    /// GET /webadmin/api/games
    /// </summary>
    [HttpGet("api/games")]
    public async Task<IActionResult> GetGames()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var games = _tracker.GetGames().Select(g => new
        {
            g.Code,
            g.DisplayName,
            g.PlayerCount,
            g.GameState,
            g.IsPublic,
            g.HostName,
        });
        return Ok(games);
    }

    // ========== Bans API ==========

    /// <summary>
    /// GET /webadmin/api/bans
    /// </summary>
    [HttpGet("api/bans")]
    public async Task<IActionResult> GetBans()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var bans = await _banService.GetAllBansAsync();
        return Ok(bans);
    }

    /// <summary>
    /// POST /webadmin/api/ban/add
    /// </summary>
    [HttpPost("api/ban/add")]
    public async Task<IActionResult> AddBan([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var entry = new BanEntry
            {
                PlayerName = body.TryGetProperty("playerName", out var n) ? n.GetString() : null,
                IpAddress = body.TryGetProperty("ipAddress", out var ip) ? ip.GetString() : null,
                Puid = body.TryGetProperty("puid", out var pu) ? pu.GetString() : null,
                FriendCode = body.TryGetProperty("friendCode", out var fc) ? fc.GetString() : null,
                Fid = body.TryGetProperty("fid", out var fi) ? fi.GetString() : null,
                Reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null,
                BannedAt = DateTime.UtcNow,
                BannedBy = body.TryGetProperty("bannedBy", out var b) ? b.GetString() : "WebAdmin",
            };

            await _banService.AddBanAsync(entry);
            var kicked = await _tracker.KickPlayersByBanEntryAsync(entry.IpAddress, entry.PlayerName, entry.FriendCode);
            var msg = "Ban entry added." + (kicked > 0 ? $" {kicked} player(s) disconnected." : "");
            return Ok(new { success = true, message = msg });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/ban/remove
    /// </summary>
    [HttpPost("api/ban/remove")]
    public async Task<IActionResult> RemoveBan([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var id = body.GetProperty("id").GetInt32();
            await _banService.RemoveBanAsync(id);
            return Ok(new { success = true, message = "Ban entry removed." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Kick/Ban API ==========

    /// <summary>
    /// POST /webadmin/api/kick
    /// </summary>
    [HttpPost("api/kick")]
    public async Task<IActionResult> KickPlayer([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var clientId = body.GetProperty("clientId").GetInt32();
            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;

            await _tracker.KickPlayerAsync(clientId, reason);
            return Ok(new { success = true, message = "Player kicked." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/kick/batch - kick multiple players and/or all players of rooms
    /// </summary>
    [HttpPost("api/kick/batch")]
    public async Task<IActionResult> KickPlayersBatch([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var clientIds = body.TryGetProperty("clientIds", out var c) ? c.Deserialize<List<int>>() : null;
            var gameCodes = body.TryGetProperty("gameCodes", out var g) ? g.Deserialize<List<string>>() : null;
            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;

            if ((clientIds == null || clientIds.Count == 0) && (gameCodes == null || gameCodes.Count == 0))
                return BadRequest(new { success = false, message = "clientIds or gameCodes required." });

            var ids = new HashSet<int>();
            if (clientIds != null)
            {
                foreach (var id in clientIds)
                    ids.Add(id);
            }

            if (gameCodes != null && gameCodes.Count > 0)
            {
                var codes = gameCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var game in _tracker.GetGames())
                {
                    if (codes.Contains(game.Code))
                    {
                        foreach (var player in game.Players)
                            ids.Add(player.ClientId);
                    }
                }
            }

            var count = await _tracker.KickPlayersAsync(ids, reason);
            _logService.AddLog("kick_batch", $"Kicked {count} player(s)", GetClientIp());
            return Ok(new { success = true, message = $"{count} player(s) kicked.", kicked = count });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/ban (in-game ban)
    /// </summary>
    [HttpPost("api/ban")]
    public async Task<IActionResult> BanPlayer([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var clientId = body.GetProperty("clientId").GetInt32();
            var player = _tracker.FindPlayer(clientId);

            if (player == null)
                return NotFound(new { success = false, message = "Player not found." });

            await _tracker.BanPlayerAsync(clientId);

            var banEntry = new BanEntry
            {
                PlayerName = player.PlayerName,
                IpAddress = player.IpAddress,
                FriendCode = player.FriendCode,
                Reason = "Banned via web admin (in-game ban)",
                BannedAt = DateTime.UtcNow,
                BannedBy = "WebAdmin",
            };
            await _banService.AddBanAsync(banEntry);

            return Ok(new { success = true, message = "Player banned." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Logs API ==========

    /// <summary>
    /// GET /webadmin/api/logs
    /// </summary>
    [HttpGet("api/logs")]
    public async Task<IActionResult> GetLogs()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var logs = await _logService.GetLogsAsync();
        return Ok(logs);
    }

    // ========== Settings API ==========

    /// <summary>
    /// GET /webadmin/api/settings
    /// </summary>
    [HttpGet("api/settings")]
    public async Task<IActionResult> GetSettings()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var isAdmin = _auth.IsAdmin(sessionUser);
        var role = _auth.GetUserRole(sessionUser);

        // User enumeration is admin-only: regular accounts see only themselves.
        var users = isAdmin
            ? _auth.GetUsers().Select(u => new { username = u.Username, role = u.Role })
            : null;

        return Ok(new
        {
            username = sessionUser,
            role,
            isAdmin,
            users,
            version = PluginVersion,
        });
    }

    // ========== Config API ==========

    /// <summary>
    /// GET /webadmin/api/config
    /// </summary>
    [HttpGet("api/config")]
    public async Task<IActionResult> GetConfig()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        return Ok(new
        {
            success = true,
            config = new
            {
                pathPrefix = _config.PathPrefix,
                language = "zh",
                theme = "dark",
            }
        });
    }

    // ========== Change Password API ==========

    /// <summary>
    /// POST /webadmin/api/change-password
    /// </summary>
    [HttpPost("api/change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        try
        {
            var oldPassword = body.GetProperty("oldPassword").GetString() ?? "";
            var newPassword = body.GetProperty("newPassword").GetString() ?? "";

            if (string.IsNullOrEmpty(newPassword))
                return BadRequest(new { success = false, message = "New password cannot be empty." });
            if (newPassword.Length < 8)
                return BadRequest(new { success = false, message = "新密码至少需要 8 位。" });

            if (_auth.ChangePassword(sessionUser, oldPassword, newPassword))
            {
                // Other sessions of this user must not survive the credential change.
                _auth.InvalidateUserSessions(sessionUser, GetSessionToken());
                _logService.AddLog("change_password", $"User '{sessionUser}' changed password", GetClientIp());
                _logger.LogInformation("[WebAdmin] Password changed by user {User}.", sessionUser);
                return Ok(new { success = true, message = "Password changed successfully." });
            }
            else
            {
                return Unauthorized(new { success = false, message = "Current password is incorrect." });
            }
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== User Management API ==========

    /// <summary>
    /// GET /webadmin/api/users
    /// </summary>
    [HttpGet("api/users")]
    public async Task<IActionResult> GetUsers()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        if (!_auth.IsAdmin(sessionUser))
            return StatusCode(403, new { success = false, message = "Admin only." });

        var users = _auth.GetUsers().Select(u => new { username = u.Username, role = u.Role });
        return Ok(users);
    }

    /// <summary>
    /// POST /webadmin/api/users
    /// </summary>
    [HttpPost("api/users")]
    public async Task<IActionResult> AddUser([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        if (!_auth.IsAdmin(sessionUser))
            return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var newUser = body.GetProperty("username").GetString() ?? "";
            var newPass = body.GetProperty("password").GetString() ?? "";

            if (string.IsNullOrEmpty(newUser) || string.IsNullOrEmpty(newPass))
                return BadRequest(new { success = false, message = "Username and password required." });

            if (_auth.UserExists(newUser))
                return BadRequest(new { success = false, message = "User already exists." });

            var role = body.TryGetProperty("role", out var r) ? r.GetString() : "user";
            _auth.AddUser(newUser, newPass, role ?? "user");
            _logService.AddLog("add_user", $"Admin '{sessionUser}' added user '{newUser}'", GetClientIp());
            return Ok(new { success = true, message = "User added." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// DELETE /webadmin/api/users
    /// </summary>
    [HttpDelete("api/users")]
    public async Task<IActionResult> DeleteUser([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        if (!_auth.IsAdmin(sessionUser))
            return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var delUser = body.GetProperty("username").GetString() ?? "";

            if (string.IsNullOrEmpty(delUser) || delUser == sessionUser)
                return BadRequest(new { success = false, message = "Cannot delete yourself." });

            if (!_auth.UserExists(delUser))
                return NotFound(new { success = false, message = "User not found." });

            _auth.DeleteUser(delUser);
            _auth.InvalidateUserSessions(delUser); // deleted accounts lose all live sessions
            _logService.AddLog("delete_user", $"Admin '{sessionUser}' deleted user '{delUser}'", GetClientIp());
            return Ok(new { success = true, message = "User deleted." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Chat API ==========

    /// <summary>
    /// POST /webadmin/api/chat/send
    /// </summary>
    [HttpPost("api/chat/send")]
    public async Task<IActionResult> SendChat([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var gameCode = body.TryGetProperty("gameCode", out var gc) ? gc.GetString() : null;
            var gameCodes = body.TryGetProperty("gameCodes", out var gcs) && gcs.ValueKind == JsonValueKind.Array
                ? gcs.Deserialize<List<string>>() : null;
            var message = body.GetProperty("message").GetString() ?? "";
            // targetClientIds 宽松解析：数字数组 / 字符串数组（数字、好友码、玩家名
            // 均可）/ 单个值——Agent 与旧版面板调用都不会再触发 JsonException。
            List<int>? targets = null;
            if (body.TryGetProperty("targetClientIds", out var t))
            {
                targets = new List<int>();
                if (t.ValueKind == JsonValueKind.Array)
                {
                    var keys = new List<string>();
                    foreach (var el in t.EnumerateArray())
                    {
                        keys.Add(el.ValueKind == JsonValueKind.Number ? el.GetRawText() : (el.GetString() ?? ""));
                    }
                    targets = _chatService.ResolveClientIds(keys);
                }
                else if (t.ValueKind == JsonValueKind.Number)
                {
                    targets = _chatService.ResolveClientIds(new[] { t.GetRawText() });
                }
                else if (t.ValueKind == JsonValueKind.String)
                {
                    targets = _chatService.ResolveClientIds(new[] { t.GetString() ?? "" });
                }
            }
            var senderName = sessionUser;

            if (string.IsNullOrEmpty(message))
                return BadRequest(new { success = false, message = "message required." });

            if (targets != null && targets.Count > 0)
            {
                await _chatService.SendPrivateMessageToPlayersAsync(targets, message, senderName);
            }
            else if (gameCodes != null && gameCodes.Count > 0)
            {
                await _chatService.SendPublicMessageToGamesAsync(gameCodes, message, senderName);
            }
            else if (!string.IsNullOrEmpty(gameCode))
            {
                await _chatService.SendPublicMessageAsync(gameCode, message, senderName);
            }
            else
            {
                return BadRequest(new { success = false, message = "gameCode, gameCodes or targetClientIds required." });
            }

            return Ok(new { success = true, message = "Message sent." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// GET /webadmin/api/chat/logs - List chat log files
    /// </summary>
    [HttpGet("api/chat/logs")]
    public async Task<IActionResult> GetChatLogFiles()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var fileList = _chatService.GetChatLogFiles();
        return Ok(fileList);
    }

    /// <summary>
    /// POST /webadmin/api/chat/logs - Load specific chat log
    /// </summary>
    [HttpPost("api/chat/logs")]
    public async Task<IActionResult> GetChatLogs([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        try
        {
            var gameDir = body.GetProperty("gameDir").GetString() ?? "";
            var fileName = body.GetProperty("fileName").GetString() ?? "";
            var entries = await _chatService.GetChatLogAsync(gameDir, fileName);
            return Ok(entries ?? new List<ChatLogEntry>());
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Connection Logs API ==========

    /// <summary>
    /// GET /webadmin/api/connect/logs - List connection log files
    /// </summary>
    [HttpGet("api/connect/logs")]
    public async Task<IActionResult> GetConnectLogFiles()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        return Ok(_connLogger.GetLogFiles());
    }

    /// <summary>
    /// POST /webadmin/api/connect/logs - Load specific connection log
    /// </summary>
    [HttpPost("api/connect/logs")]
    public async Task<IActionResult> GetConnectLogs([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        try
        {
            var fileName = body.GetProperty("fileName").GetString() ?? "";
            var filter = body.TryGetProperty("filter", out var f) ? f.GetString() : null;
            return Ok(_connLogger.GetLogContent(fileName, filter));
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Version API ==========

    /// <summary>
    /// GET /webadmin/api/version
    /// </summary>
    [HttpGet("api/version")]
    public async Task<IActionResult> GetVersion()
    {
        return Ok(new { version = PluginVersion });
    }

    // ========== Banned words (chat filter) API ==========

    /// <summary>
    /// GET /webadmin/api/filter
    /// </summary>
    [HttpGet("api/filter")]
    public async Task<IActionResult> GetFilter()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var snapshot = _filter.GetSnapshot();
        return Ok(new
        {
            success = true,
            enabled = snapshot.Enabled,
            tipMessage = snapshot.TipMessage,
            blockedWords = snapshot.BlockedWords,
            autoMuteEnabled = snapshot.AutoMuteEnabled,
            violationLimit = snapshot.ViolationLimit,
            muteDurationMinutes = snapshot.MuteDurationMinutes,
            muteMessage = snapshot.MuteMessage,
        });
    }

    /// <summary>
    /// POST /webadmin/api/filter/words/add
    /// </summary>
    [HttpPost("api/filter/words/add")]
    public async Task<IActionResult> AddFilterWord([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var word = body.GetProperty("word").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(word))
                return BadRequest(new { success = false, message = "word required." });

            _filter.AddWord(word);
            _logService.AddLog("filter_add", $"Added banned word '{word}'", GetClientIp());
            return Ok(new { success = true, message = "Banned word added." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/filter/words/remove
    /// </summary>
    [HttpPost("api/filter/words/remove")]
    public async Task<IActionResult> RemoveFilterWord([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var word = body.GetProperty("word").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(word))
                return BadRequest(new { success = false, message = "word required." });

            _filter.RemoveWord(word);
            _logService.AddLog("filter_remove", $"Removed banned word '{word}'", GetClientIp());
            return Ok(new { success = true, message = "Banned word removed." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/filter/settings
    /// </summary>
    [HttpPost("api/filter/settings")]
    public async Task<IActionResult> UpdateFilterSettings([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            bool? enabled = body.TryGetProperty("enabled", out var e) ? e.GetBoolean() : null;
            string? tipMessage = body.TryGetProperty("tipMessage", out var t) ? t.GetString() : null;
            bool? autoMuteEnabled = body.TryGetProperty("autoMuteEnabled", out var am) ? am.GetBoolean() : null;
            int? violationLimit = body.TryGetProperty("violationLimit", out var vl) ? vl.GetInt32() : null;
            int? muteDurationMinutes = body.TryGetProperty("muteDurationMinutes", out var md) ? md.GetInt32() : null;
            string? muteMessage = body.TryGetProperty("muteMessage", out var mm) ? mm.GetString() : null;
            _filter.UpdateSettings(enabled, tipMessage, autoMuteEnabled, violationLimit, muteDurationMinutes, muteMessage);
            _logService.AddLog("filter_settings", "Updated filter settings", GetClientIp());
            return Ok(new { success = true, message = "Filter settings updated." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Welcome messages API ==========

    /// <summary>
    /// GET /webadmin/api/welcome
    /// </summary>
    [HttpGet("api/welcome")]
    public async Task<IActionResult> GetWelcome()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var snapshot = _welcome.GetSnapshot();
        return Ok(new
        {
            success = true,
            enabled = snapshot.Enabled,
            messages = snapshot.Messages,
        });
    }

    /// <summary>
    /// POST /webadmin/api/welcome/messages/add
    /// </summary>
    [HttpPost("api/welcome/messages/add")]
    public async Task<IActionResult> AddWelcomeMessage([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var message = body.GetProperty("message").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(message))
                return BadRequest(new { success = false, message = "message required." });

            _welcome.AddMessage(message);
            _logService.AddLog("welcome_add", "Added welcome message", GetClientIp());
            return Ok(new { success = true, message = "Welcome message added." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/welcome/messages/remove
    /// </summary>
    [HttpPost("api/welcome/messages/remove")]
    public async Task<IActionResult> RemoveWelcomeMessage([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var index = body.GetProperty("index").GetInt32();
            _welcome.RemoveMessageAt(index);
            _logService.AddLog("welcome_remove", "Removed welcome message", GetClientIp());
            return Ok(new { success = true, message = "Welcome message removed." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/welcome/settings
    /// </summary>
    [HttpPost("api/welcome/settings")]
    public async Task<IActionResult> UpdateWelcomeSettings([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            bool? enabled = body.TryGetProperty("enabled", out var e) ? e.GetBoolean() : null;
            _welcome.UpdateSettings(enabled);
            return Ok(new { success = true, message = "Welcome settings updated." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Reports API ==========

    /// <summary>
    /// GET /webadmin/api/reports
    /// </summary>
    [HttpGet("api/reports")]
    public async Task<IActionResult> GetReports()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var reports = await _reportService.GetAllAsync();
        return Ok(new { success = true, reports });
    }

    /// <summary>
    /// POST /webadmin/api/reports/remove
    /// </summary>
    [HttpPost("api/reports/remove")]
    public async Task<IActionResult> RemoveReport([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var id = body.GetProperty("id").GetInt32();
            var removed = await _reportService.RemoveAsync(id);
            if (!removed)
                return NotFound(new { success = false, message = "Report not found." });

            _logService.AddLog("report_remove", $"Removed report #{id}", GetClientIp());
            return Ok(new { success = true, message = "Report removed." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/reports/status
    /// </summary>
    [HttpPost("api/reports/status")]
    public async Task<IActionResult> SetReportStatus([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var id = body.GetProperty("id").GetInt32();
            var status = body.GetProperty("status").GetString() ?? "pending";
            var ok = await _reportService.SetStatusAsync(id, status);
            if (!ok)
                return NotFound(new { success = false, message = "Report not found." });

            return Ok(new { success = true, message = "Report status updated." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Player behavior logs API ==========

    /// <summary>
    /// GET /webadmin/api/player-logs?type=&amp;search=
    /// </summary>
    [HttpGet("api/player-logs")]
    public async Task<IActionResult> GetPlayerLogs([FromQuery] string? type = null, [FromQuery] string? search = null, [FromQuery] int limit = 500)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var logs = await _playerLogs.GetLogsAsync(type, search, Math.Clamp(limit, 1, 2000));
        return Ok(new { success = true, logs });
    }

    /// <summary>
    /// GET /webadmin/api/player-logs/types
    /// </summary>
    [HttpGet("api/player-logs/types")]
    public async Task<IActionResult> GetPlayerLogTypes()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var types = await _playerLogs.GetTypesAsync();
        return Ok(new { success = true, types });
    }

    // ========== Player stats API ==========

    /// <summary>
    /// GET /webadmin/api/player-stats
    /// </summary>
    [HttpGet("api/player-stats")]
    public async Task<IActionResult> GetPlayerStats()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var stats = await _playerStats.GetAllAsync();
        return Ok(new { success = true, players = stats });
    }

    /// <summary>
    /// POST /webadmin/api/player-stats/reset
    /// </summary>
    [HttpPost("api/player-stats/reset")]
    public async Task<IActionResult> ResetPlayerStats()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        await _playerStats.ClearAllAsync();
        _logService.AddLog("stats_reset", "Reset all player stats", GetClientIp());
        return Ok(new { success = true, message = "Player stats reset." });
    }

    // ========== Custom game codes API ==========

    /// <summary>
    /// GET /webadmin/api/codes
    /// </summary>
    [HttpGet("api/codes")]
    public async Task<IActionResult> GetGameCodes()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var snapshot = _gameCodes.GetSnapshot();
        var (available, inUse) = _gameCodes.GetPoolState();
        return Ok(new
        {
            success = true,
            enabled = snapshot.Enabled,
            codes = snapshot.Codes.Select(c => new { code = c, inUse = inUse.Contains(c, StringComparer.OrdinalIgnoreCase) }),
        });
    }

    /// <summary>
    /// POST /webadmin/api/codes/add
    /// </summary>
    [HttpPost("api/codes/add")]
    public async Task<IActionResult> AddGameCode([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var code = body.GetProperty("code").GetString() ?? "";
            if (!_gameCodes.AddCode(code))
                return BadRequest(new { success = false, message = "Invalid or duplicate code. Use 4 or 6 letters A-Z." });

            _logService.AddLog("code_add", $"Added custom room code '{code}'", GetClientIp());
            return Ok(new { success = true, message = "Room code added." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/codes/remove
    /// </summary>
    [HttpPost("api/codes/remove")]
    public async Task<IActionResult> RemoveGameCode([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var code = body.GetProperty("code").GetString() ?? "";
            if (!_gameCodes.RemoveCode(code))
                return NotFound(new { success = false, message = "Code not found." });

            _logService.AddLog("code_remove", $"Removed custom room code '{code}'", GetClientIp());
            return Ok(new { success = true, message = "Room code removed." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/codes/settings
    /// </summary>
    [HttpPost("api/codes/settings")]
    public async Task<IActionResult> UpdateGameCodesSettings([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            bool? enabled = body.TryGetProperty("enabled", out var e) ? e.GetBoolean() : null;
            _gameCodes.UpdateSettings(enabled);
            return Ok(new { success = true, message = "Room code settings updated." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Delta ports settings API ==========

    /// <summary>
    /// GET /webadmin/api/settings/delta-ports
    /// </summary>
    [HttpGet("api/settings/delta-ports")]
    public async Task<IActionResult> GetDeltaPortSettings()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var s = _settingsService.GetDeltaPorts();
        return Ok(new { success = true, enabled = s.Enabled, start = s.Start, end = s.End, poolActive = _portPool.IsEnabled });
    }

    /// <summary>
    /// POST /webadmin/api/settings/delta-ports
    /// </summary>
    [HttpPost("api/settings/delta-ports")]
    public async Task<IActionResult> UpdateDeltaPortSettings([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            bool? enabled = body.TryGetProperty("enabled", out var e) ? e.GetBoolean() : null;
            int? start = body.TryGetProperty("start", out var st) ? st.GetInt32() : null;
            int? end = body.TryGetProperty("end", out var en) ? en.GetInt32() : null;

            if (start.HasValue && start.Value <= 0)
                return BadRequest(new { success = false, message = "start must be a valid port (1-65535)." });
            if (end.HasValue && end.Value <= 0)
                return BadRequest(new { success = false, message = "end must be a valid port (1-65535)." });

            var updated = _settingsService.UpdateDeltaPorts(enabled, start, end);
            _portPool.ApplySettings(updated);
            _logService.AddLog("delta_ports", $"Delta ports updated: enabled={updated.Enabled}, {updated.Start}-{updated.End}", GetClientIp());
            return Ok(new { success = true, message = "Delta port settings saved.", enabled = updated.Enabled, start = updated.Start, end = updated.End });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Broadcast API ==========

    /// <summary>
    /// GET /webadmin/api/broadcast
    /// </summary>
    [HttpGet("api/broadcast")]
    public async Task<IActionResult> GetBroadcast()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var snapshot = _broadcast.GetSnapshot();
        return Ok(new { success = true, enabled = snapshot.Enabled, intervalMinutes = snapshot.IntervalMinutes, messages = snapshot.Messages });
    }

    /// <summary>
    /// POST /webadmin/api/broadcast/settings
    /// </summary>
    [HttpPost("api/broadcast/settings")]
    public async Task<IActionResult> UpdateBroadcastSettings([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            bool? enabled = body.TryGetProperty("enabled", out var e) ? e.GetBoolean() : null;
            int? intervalMinutes = body.TryGetProperty("intervalMinutes", out var im) ? im.GetInt32() : null;
            _broadcast.UpdateSettings(enabled, intervalMinutes);
            _logService.AddLog("broadcast_settings", "Updated broadcast settings", GetClientIp());
            return Ok(new { success = true, message = "Broadcast settings saved." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/broadcast/messages/add
    /// </summary>
    [HttpPost("api/broadcast/messages/add")]
    public async Task<IActionResult> AddBroadcastMessage([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var message = body.GetProperty("message").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(message))
                return BadRequest(new { success = false, message = "message required." });

            _broadcast.AddMessage(message);
            return Ok(new { success = true, message = "Broadcast message added." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/broadcast/messages/remove
    /// </summary>
    [HttpPost("api/broadcast/messages/remove")]
    public async Task<IActionResult> RemoveBroadcastMessage([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var index = body.GetProperty("index").GetInt32();
            _broadcast.RemoveMessageAt(index);
            return Ok(new { success = true, message = "Broadcast message removed." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    /// POST /webadmin/api/broadcast/send-now
    /// </summary>
    [HttpPost("api/broadcast/send-now")]
    public async Task<IActionResult> BroadcastNow([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var message = body.GetProperty("message").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(message))
                return BadRequest(new { success = false, message = "message required." });

            var sent = await _broadcast.BroadcastNowAsync(message);
            _logService.AddLog("broadcast", $"Manual broadcast to {sent} room(s)", GetClientIp());
            return Ok(new { success = true, message = $"Announcement sent to {sent} room(s).", sent });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Game replays API ==========

    /// <summary>
    /// GET /webadmin/api/replays
    /// </summary>
    [HttpGet("api/replays")]
    public async Task<IActionResult> GetReplays()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var replays = await _replays.GetListAsync();
        return Ok(new { success = true, replays });
    }

    /// <summary>
    /// GET /webadmin/api/replays/files
    /// </summary>
    [HttpGet("api/replays/files")]
    public async Task<IActionResult> GetReplayFiles()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var files = await _replays.GetFilesAsync();
        return Ok(new { success = true, files });
    }

    /// <summary>
    /// GET /webadmin/api/replays/detail?file=xxx.json
    /// </summary>
    [HttpGet("api/replays/detail")]
    public async Task<IActionResult> GetReplayDetail([FromQuery] string file)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        if (string.IsNullOrWhiteSpace(file))
            return BadRequest(new { success = false, message = "file required." });

        var replay = await _replays.GetByFileNameAsync(file);
        if (replay == null)
            return NotFound(new { success = false, message = "Replay not found." });

        return Ok(new { success = true, replay });
    }

    /// <summary>
    /// POST /webadmin/api/replays/delete-all
    /// </summary>
    [HttpPost("api/replays/delete-all")]
    public async Task<IActionResult> DeleteAllReplays()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        var count = await _replays.DeleteAllAsync();
        _logService.AddLog("replays_delete", $"Deleted {count} replay file(s)", GetClientIp());
        return Ok(new { success = true, message = $"Deleted {count} replay file(s).", count });
    }

    // ========== Player footprints API ==========

    /// <summary>
    /// GET /webadmin/api/footprints?search=
    /// </summary>
    [HttpGet("api/footprints")]
    public async Task<IActionResult> GetFootprints([FromQuery] string? search = null)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var footprints = await _footprints.GetListAsync(search);
        return Ok(new { success = true, footprints });
    }

    /// <summary>
    /// POST /webadmin/api/footprints/clear
    /// </summary>
    [HttpPost("api/footprints/clear")]
    public async Task<IActionResult> ClearFootprints()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        await _footprints.ClearAllAsync();
        _logService.AddLog("footprints_clear", "Cleared all player footprints", GetClientIp());
        return Ok(new { success = true, message = "Footprints cleared." });
    }

    // ========== Dashboard API ==========

    /// <summary>
    /// GET /webadmin/api/dashboard
    /// </summary>
    [HttpGet("api/dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        return Ok(new { success = true, data = _dashboard.GetSnapshot() });
    }

    // ========== Feature toggles API (replays / footprints / room cleanup) ==========

    /// <summary>
    /// GET /webadmin/api/settings/features
    /// </summary>
    [HttpGet("api/settings/features")]
    public async Task<IActionResult> GetFeatureSettings()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var replays = _settingsService.GetReplays();
        var footprints = _settingsService.GetFootprints();
        var cleanup = _settingsService.GetRoomCleanup();
        return Ok(new
        {
            success = true,
            replaysEnabled = replays.Enabled,
            maxReplays = replays.MaxReplays,
            footprintsEnabled = footprints.Enabled,
            roomCleanupEnabled = cleanup.Enabled,
            roomCleanupTtlMinutes = cleanup.TtlMinutes,
        });
    }

    /// <summary>
    /// POST /webadmin/api/settings/features
    /// </summary>
    [HttpPost("api/settings/features")]
    public async Task<IActionResult> UpdateFeatureSettings([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            bool? replaysEnabled = body.TryGetProperty("replaysEnabled", out var re) ? re.GetBoolean() : null;
            int? maxReplays = body.TryGetProperty("maxReplays", out var mr) ? mr.GetInt32() : null;
            bool? footprintsEnabled = body.TryGetProperty("footprintsEnabled", out var fe) ? fe.GetBoolean() : null;
            bool? roomCleanupEnabled = body.TryGetProperty("roomCleanupEnabled", out var rc) ? rc.GetBoolean() : null;
            int? roomCleanupTtlMinutes = body.TryGetProperty("roomCleanupTtlMinutes", out var rt) ? rt.GetInt32() : null;

            _settingsService.UpdateFeatureSettings(replaysEnabled, maxReplays, footprintsEnabled, roomCleanupEnabled, roomCleanupTtlMinutes);
            _logService.AddLog("feature_settings", "Updated feature toggles", GetClientIp());
            return Ok(new { success = true, message = "Feature settings saved." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== AI assistant API ==========

    /// <summary>
    ///     GET /webadmin/api/ai/settings
    /// </summary>
    [HttpGet("api/ai/settings")]
    public async Task<IActionResult> GetAiSettings()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
        if (!_auth.IsAdmin(sessionUser))
            return StatusCode(403, new { success = false, message = "Admin only." });

        var s = _ai.GetSettings();
        return Ok(new
        {
            success = true,
            inGameChatEnabled = s.InGameChatEnabled,
            model = s.Model,
            systemPrompt = s.SystemPrompt,
            maxContextMessages = s.MaxContextMessages,
            panelApiKey = s.PanelApiKey,
            panelApiBaseUrl = s.PanelApiBaseUrl,
            panelApiFormat = s.PanelApiFormat,
            panelModel = s.PanelModel,
            inGameApiKey = s.InGameApiKey,
            inGameApiBaseUrl = s.InGameApiBaseUrl,
            inGameApiFormat = s.InGameApiFormat,
            inGameModel = s.InGameModel,
            inGameRateSeconds = s.InGameRateSeconds,
            inGameRateRounds = s.InGameRateRounds,
            panelRateSeconds = s.PanelRateSeconds,
            panelRateRounds = s.PanelRateRounds,
            webSearchEnabled = s.WebSearchEnabled,
        });
    }

    /// <summary>
    ///     POST /webadmin/api/ai/settings
    ///     Custom endpoint fields (panelApiKey/inGameApiKey/...): omit = keep,
    ///     "" = clear custom value (fall back to built-in), otherwise set.
    ///     Keys may be in ANY format (not limited to Zhipu).
    /// </summary>
    [HttpPost("api/ai/settings")]
    public async Task<IActionResult> UpdateAiSettings([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            bool? inGameChatEnabled = body.TryGetProperty("inGameChatEnabled", out var ig) ? ig.GetBoolean() : null;
            string? systemPrompt = body.TryGetProperty("systemPrompt", out var sp) ? sp.GetString() : null;
            string? model = body.TryGetProperty("model", out var mo) ? mo.GetString() : null;
            int? maxContextMessages = body.TryGetProperty("maxContextMessages", out var mc) ? mc.GetInt32() : null;
            string? panelApiKey = body.TryGetProperty("panelApiKey", out var pk) ? pk.GetString() : null;
            string? panelApiBaseUrl = body.TryGetProperty("panelApiBaseUrl", out var pb) ? pb.GetString() : null;
            string? panelApiFormat = body.TryGetProperty("panelApiFormat", out var pf) ? pf.GetString() : null;
            string? panelModel = body.TryGetProperty("panelModel", out var pm) ? pm.GetString() : null;
            string? inGameApiKey = body.TryGetProperty("inGameApiKey", out var ik) ? ik.GetString() : null;
            string? inGameApiBaseUrl = body.TryGetProperty("inGameApiBaseUrl", out var ib) ? ib.GetString() : null;
            string? inGameApiFormat = body.TryGetProperty("inGameApiFormat", out var inf) ? inf.GetString() : null;
            string? inGameModel = body.TryGetProperty("inGameModel", out var im) ? im.GetString() : null;
            int? inGameRateSeconds = body.TryGetProperty("inGameRateSeconds", out var igs) && igs.ValueKind == JsonValueKind.Number ? igs.GetInt32() : (int?)null;
            int? inGameRateRounds = body.TryGetProperty("inGameRateRounds", out var igr) && igr.ValueKind == JsonValueKind.Number ? igr.GetInt32() : (int?)null;
            int? panelRateSeconds = body.TryGetProperty("panelRateSeconds", out var prs) && prs.ValueKind == JsonValueKind.Number ? prs.GetInt32() : (int?)null;
            int? panelRateRounds = body.TryGetProperty("panelRateRounds", out var prr) && prr.ValueKind == JsonValueKind.Number ? prr.GetInt32() : (int?)null;
            bool? webSearchEnabled = body.TryGetProperty("webSearchEnabled", out var wse) && wse.ValueKind == JsonValueKind.True || wse.ValueKind == JsonValueKind.False ? wse.GetBoolean() : (bool?)null;

            // The panel echoes the masked key back when unchanged — a masked value
            // must never overwrite the real secret; treat it as "not modified".
            if (panelApiKey != null && panelApiKey.Contains("****", StringComparison.Ordinal))
            {
                panelApiKey = null;
            }
            if (inGameApiKey != null && inGameApiKey.Contains("****", StringComparison.Ordinal))
            {
                inGameApiKey = null;
            }

            _ai.UpdateSettings(inGameChatEnabled, systemPrompt, model, maxContextMessages,
                panelApiKey, inGameApiKey, panelApiBaseUrl, panelApiFormat, panelModel,
                inGameApiBaseUrl, inGameApiFormat, inGameModel,
                inGameRateSeconds, inGameRateRounds, panelRateSeconds, panelRateRounds, webSearchEnabled);
            _logService.AddLog("ai_settings", "Updated AI settings", GetClientIp());
            return Ok(new { success = true, message = "AI settings saved." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    ///     POST /webadmin/api/ai/chat — admin chats with the AI, optionally
    ///     asking it to analyze server data (context: logs/stats/footprints/
    ///     replays/chats/reports).
    /// </summary>
    // Per-session rate limit for panel AI chat: prevents a logged-in account from
    // burning the (possibly custom) AI provider quota with unlimited requests.
    // 30/min: one Agent run needs up to ~7 rounds, so 10 was too tight.
    private static readonly FixedWindowRateLimiter AiChatLimiter = new(30, TimeSpan.FromMinutes(1));

    [HttpPost("api/ai/chat")]
    public async Task<IActionResult> AiChat([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        if (!AiChatLimiter.Allow("ai:" + sessionUser))
            return StatusCode(429, new { success = false, message = "AI 请求过于频繁，请稍后再试。" });

        // 非管理员额外受可配置的滑动窗口限制（管理员在 AI 设置里配置 N 秒 M 轮）。
        if (!_auth.IsAdmin(sessionUser))
        {
            var rateMsg = _ai.CheckAiRateLimit("panel:" + sessionUser, inGame: false);
            if (rateMsg != null)
            {
                return StatusCode(429, new { success = false, message = rateMsg });
            }
        }

        try
        {
            var message = body.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
            string? context = body.TryGetProperty("context", out var ctx) ? ctx.GetString() : null;
            // "chat" = normal panel AI chat; "agent" = Agent auto-operation round.
            var kind = body.TryGetProperty("kind", out var kd) ? kd.GetString() : null;
            // 16KB: the Agent's round-1 prompt (tool list + task) must fit; the
            // 128KB request-body cap remains the outer DoS boundary.
            if (message.Length > 16000)
            {
                message = message[..16000];
            }

            var sources = new List<object>();
            var (ok, reply, error) = await _ai.PanelChatAsync(message, context, sessionUser, kind ?? "chat", useContext: true, sourcesOut: sources);
            if (!ok)
            {
                return BadRequest(new { success = false, message = error ?? "AI 无法回复，请稍后再试。" });
            }

            return Ok(new { success = true, reply, sources });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>
    ///     GET /webadmin/api/ai/chats — AI chat history with category filters.
    ///     Query params: search, source ("panel"|"ingame"), kind ("chat"|"agent"),
    ///     user (panel username, contains), date ("yyyy-MM-dd", local time).
    /// </summary>
    [HttpGet("api/ai/chats")]
    public async Task<IActionResult> GetAiChats(
        [FromQuery] string? search = null,
        [FromQuery] string? source = null,
        [FromQuery] string? kind = null,
        [FromQuery] string? user = null,
        [FromQuery] string? date = null)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
        // 聊天记录含玩家私聊内容/好友码/PUID 与其他管理员的提示词，
        // 与面板其余敏感数据一致，仅管理员可读。
        if (!_auth.IsAdmin(sessionUser))
            return StatusCode(403, new { success = false, message = "Admin only." });

        var chats = await _ai.GetChatRecordsAsync(search, 100, source, kind, user, date);
        return Ok(new { success = true, chats });
    }

    /// <summary>
    ///     POST /webadmin/api/ai/context-clear — clear the panel AI context.
    /// </summary>
    [HttpPost("api/ai/context-clear")]
    public async Task<IActionResult> ClearAiContext()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        await _ai.ClearPanelContextAsync();
        _logService.AddLog("ai_context_clear", "Cleared AI panel context", GetClientIp());
        return Ok(new { success = true, message = "AI 对话上下文已清空。" });
    }

    // ========== Player titles API（称号） ==========

    /// <summary>
    ///     GET /webadmin/api/titles
    /// </summary>
    [HttpGet("api/titles")]
    public async Task<IActionResult> GetTitles()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var s = _titles.GetSnapshot();
        return Ok(new
        {
            success = true,
            enableTitle = s.EnableTitle,
            titleWithBrackets = s.TitleWithBrackets,
            titlePosition = s.TitlePosition,
            titles = s.Titles,
            players = s.Players,
        });
    }

    /// <summary>
    ///     POST /webadmin/api/titles — action: addTitle / removeTitle /
    ///     setPlayer / removePlayer / updateSettings.
    /// </summary>
    [HttpPost("api/titles")]
    public async Task<IActionResult> UpdateTitles([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            var action = body.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            switch (action)
            {
                case "addTitle":
                {
                    var name = body.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var html = body.TryGetProperty("htmlFormat", out var h) ? h.GetString() ?? "" : "";
                    _titles.AddTitle(name, html);
                    _logService.AddLog("title_add", $"Added title '{name}'", GetClientIp());
                    break;
                }

                case "removeTitle":
                {
                    var name = body.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    _titles.RemoveTitle(name);
                    _logService.AddLog("title_remove", $"Removed title '{name}'", GetClientIp());
                    break;
                }

                case "setPlayer":
                {
                    var friendCode = body.TryGetProperty("friendCode", out var fc) ? fc.GetString() ?? "" : "";
                    var title = body.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    _titles.SetPlayerTitle(friendCode, title);
                    _logService.AddLog("title_player_set", $"Set title '{title}' for {friendCode}", GetClientIp());
                    break;
                }

                case "removePlayer":
                {
                    var friendCode = body.TryGetProperty("friendCode", out var fc) ? fc.GetString() ?? "" : "";
                    _titles.RemovePlayerTitle(friendCode);
                    _logService.AddLog("title_player_remove", $"Removed title for {friendCode}", GetClientIp());
                    break;
                }

                case "updateSettings":
                {
                    bool? enableTitle = body.TryGetProperty("enableTitle", out var et) ? et.GetBoolean() : null;
                    bool? withBrackets = body.TryGetProperty("titleWithBrackets", out var wb) ? wb.GetBoolean() : null;
                    string? position = body.TryGetProperty("titlePosition", out var tp) ? tp.GetString() : null;
                    _titles.UpdateGlobalSettings(enableTitle, withBrackets, position);
                    _logService.AddLog("title_settings", "Updated title settings", GetClientIp());
                    break;
                }

                default:
                    return BadRequest(new { success = false, message = "Unknown action." });
            }

            return Ok(new { success = true, message = "OK" });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Room monitor API（QQ 广播） ==========

    /// <summary>
    ///     GET /webadmin/api/monitor
    /// </summary>
    [HttpGet("api/monitor")]
    public async Task<IActionResult> GetMonitorSettings()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
        if (!_auth.IsAdmin(sessionUser))
            return StatusCode(403, new { success = false, message = "Admin only." });

        var s = _roomMonitor.GetSettings();
        return Ok(new
        {
            success = true,
            enabled = s.Enabled,
            oneBotUrl = s.OneBotUrl,
            // The QQ bot token is a credential: only ever leave as a masked value.
            oneBotToken = MaskSecret(s.OneBotToken),
            allowedGroups = s.AllowedGroups,
            serverName = s.ServerName,
            // Turbo-650: QQ 消息模板（空 = 使用内置默认；面板未修改时在编辑框里显示默认值）
            roomReportTemplate = s.RoomReportTemplate,
            statusReplyTemplate = s.StatusReplyTemplate,
            defaultRoomReportTemplate = RoomMonitorService.DefaultRoomReportTemplate,
            defaultStatusReplyTemplate = RoomMonitorService.DefaultStatusReplyTemplate,
            // Turbo-650: 状态命令（默认 #在线状态，可配置多个；未配置返回生效默认）
            statusTriggers = RoomMonitorService.StatusTriggerList(s),
            defaultStatusTriggers = RoomMonitorService.DefaultStatusTriggers,
        });
    }

    /// <summary>
    ///     POST /webadmin/api/monitor
    /// </summary>
    [HttpPost("api/monitor")]
    public async Task<IActionResult> UpdateMonitorSettings([FromBody] JsonElement body)
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        try
        {
            bool? enabled = body.TryGetProperty("enabled", out var e) ? e.GetBoolean() : null;
            string? url = body.TryGetProperty("oneBotUrl", out var u) ? u.GetString() : null;
            string? token = body.TryGetProperty("oneBotToken", out var t) ? t.GetString() : null;
            List<long>? groups = body.TryGetProperty("allowedGroups", out var g) ? g.Deserialize<List<long>>() : null;
            string? name = body.TryGetProperty("serverName", out var sn) ? sn.GetString() : null;
            string? reportTpl = body.TryGetProperty("roomReportTemplate", out var rt) ? rt.GetString() : null;
            string? statusTpl = body.TryGetProperty("statusReplyTemplate", out var st) ? st.GetString() : null;
            List<string>? statusTriggers = body.TryGetProperty("statusTriggers", out var tg) ? tg.Deserialize<List<string>>() : null;

            // The panel echoes the masked token back when unchanged — a masked value
            // must never overwrite the real secret; treat it as "not modified".
            if (token != null && token.Contains("****", StringComparison.Ordinal))
            {
                token = null;
            }

            _roomMonitor.UpdateSettings(enabled, url, token, groups, name, reportTpl, statusTpl, statusTriggers);
            _logService.AddLog("monitor_settings", "Updated room monitor settings", GetClientIp());
            return Ok(new { success = true, message = "Monitor settings saved." });
        }
        catch (Exception ex)
        {
            // Generic client response: exception details are logged, never echoed.
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    // ========== Player play-time API ==========

    /// <summary>
    ///     GET /webadmin/api/player-times — welcome/play-time tracking data.
    /// </summary>
    [HttpGet("api/player-times")]
    public async Task<IActionResult> GetPlayerTimes()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });

        var players = _playerTimes.GetSnapshot();
        return Ok(new { success = true, players });
    }

    /// <summary>
    ///     POST /webadmin/api/player-times/clear — reset all play-time records.
    /// </summary>
    [HttpPost("api/player-times/clear")]
    public async Task<IActionResult> ClearPlayerTimes()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
            if (!_auth.IsAdmin(sessionUser))
                return StatusCode(403, new { success = false, message = "Admin only." });

        _playerTimes.ClearAll();
        _logService.AddLog("player_times_clear", "Cleared player play-time records", GetClientIp());
        return Ok(new { success = true, message = "Player times cleared." });
    }

    // ========== Scheduled tasks API (Turbo-620) ==========

    private IActionResult? AdminGuard()
    {
        var sessionUser = GetSessionUser();
        if (sessionUser == null)
            return Unauthorized(new { success = false, message = "Not authenticated." });
        if (!_auth.IsAdmin(sessionUser))
            return StatusCode(403, new { success = false, message = "Admin only." });
        return null;
    }

    /// <summary>GET /webadmin/api/filter/stats — banned-word trigger stats (admin).</summary>
    [HttpGet("api/filter/stats")]
    public async Task<IActionResult> GetFilterStats()
    {
        var denied = AdminGuard();
        if (denied != null) return denied;
        return Ok(new { success = true, stats = _adminStats.GetFilterStats() });
    }

    /// <summary>POST /webadmin/api/filter/stats/clear — delete stats; body {"id":N} or {"all":true} (admin).</summary>
    [HttpPost("api/filter/stats/clear")]
    public async Task<IActionResult> ClearFilterStats([FromBody] JsonElement body)
    {
        var denied = AdminGuard();
        if (denied != null) return denied;
        int? id = body.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : null;
        var removed = _adminStats.ClearFilterStats(id);
        _logService.AddLog("filter_stats_clear", "Cleared filter stats (" + removed + ")", GetClientIp());
        return Ok(new { success = true, removed });
    }

    /// <summary>GET /webadmin/api/monitor/bstats — QQ broadcast delivery stats (admin).</summary>
    [HttpGet("api/monitor/bstats")]
    public async Task<IActionResult> GetBroadcastStats()
    {
        var denied = AdminGuard();
        if (denied != null) return denied;
        return Ok(new { success = true, stats = _adminStats.GetBroadcastStats() });
    }

    /// <summary>POST /webadmin/api/monitor/bstats/clear — delete stats; body {"id":N} or {"all":true} (admin).</summary>
    [HttpPost("api/monitor/bstats/clear")]
    public async Task<IActionResult> ClearBroadcastStats([FromBody] JsonElement body)
    {
        var denied = AdminGuard();
        if (denied != null) return denied;
        int? id = body.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : null;
        var removed = _adminStats.ClearBroadcastStats(id);
        _logService.AddLog("broadcast_stats_clear", "Cleared broadcast stats (" + removed + ")", GetClientIp());
        return Ok(new { success = true, removed });
    }
    /// <summary>GET /webadmin/api/schedule — list all scheduled tasks.</summary>
    [HttpGet("api/schedule")]
    public async Task<IActionResult> GetSchedule()
    {
        var denied = AdminGuard();
        if (denied != null) return denied;
        return Ok(new { success = true, tasks = _schedule.GetAll() });
    }

    /// <summary>POST /webadmin/api/schedule — create a scheduled task.</summary>
    [HttpPost("api/schedule")]
    public async Task<IActionResult> CreateSchedule([FromBody] JsonElement body)
    {
        var denied = AdminGuard();
        if (denied != null) return denied;

        try
        {
            var task = new ScheduledTask
            {
                Name = body.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "",
                Type = body.TryGetProperty("type", out var ty) ? (ty.GetString() ?? "").Trim() : "",
                Message = body.TryGetProperty("message", out var m) ? (m.GetString() ?? "").Trim() : "",
                IntervalMinutes = body.TryGetProperty("intervalMinutes", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : 0,
                DailyTime = body.TryGetProperty("dailyTime", out var dt) ? (dt.GetString() ?? "").Trim() : "",
                GameCodes = body.TryGetProperty("gameCodes", out var gc) && gc.ValueKind == JsonValueKind.Array
                    ? gc.Deserialize<List<string>>() ?? new List<string>()
                    : new List<string>(),
            };
            // 单次任务：前端发 ISO 8601（含 Z）；容忍本地时间字符串
            if (body.TryGetProperty("runOnceAt", out var ro) && ro.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(ro.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var once))
                {
                    task.RunOnceAt = once.Kind == DateTimeKind.Utc ? once : once.ToUniversalTime();
                }
                else
                {
                    return BadRequest(new { success = false, message = "Invalid runOnceAt datetime." });
                }
            }
            var (ok, error, created) = _schedule.Create(task, GetSessionUser() ?? "");
            if (!ok)
            {
                return BadRequest(new { success = false, message = error });
            }
            _logService.AddLog("schedule_create", "Created scheduled task: " + created!.Name, GetClientIp());
            return Ok(new { success = true, message = "Task created.", task = created });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WebAdmin] Request to {Path} failed.", Request.Path);
            return BadRequest(new { success = false, message = "Invalid request: " + ex.GetType().Name + "." });
        }
    }

    /// <summary>POST /webadmin/api/schedule/toggle — enable/disable a task.</summary>
    [HttpPost("api/schedule/toggle")]
    public async Task<IActionResult> ToggleSchedule([FromBody] JsonElement body)
    {
        var denied = AdminGuard();
        if (denied != null) return denied;

        var id = body.TryGetProperty("id", out var i) ? i.GetString() : null;
        var enabled = body.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        if (string.IsNullOrEmpty(id))
            return BadRequest(new { success = false, message = "Missing id." });
        var updated = _schedule.SetEnabled(id, enabled);
        if (updated == null)
            return NotFound(new { success = false, message = "Task not found." });
        _logService.AddLog("schedule_toggle", (enabled ? "Enabled" : "Disabled") + " scheduled task: " + updated.Name, GetClientIp());
        return Ok(new { success = true, message = enabled ? "Task enabled." : "Task disabled.", task = updated });
    }

    /// <summary>POST /webadmin/api/schedule/run — execute a task immediately.</summary>
    [HttpPost("api/schedule/run")]
    public async Task<IActionResult> RunSchedule([FromBody] JsonElement body)
    {
        var denied = AdminGuard();
        if (denied != null) return denied;

        var id = body.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id))
            return BadRequest(new { success = false, message = "Missing id." });
        var task = _schedule.GetById(id);
        if (task == null)
            return NotFound(new { success = false, message = "Task not found." });
        var (ok, message) = _schedule.RunNow(id, manual: true);
        _logService.AddLog("schedule_run", (ok ? "Ran" : "Failed to run") + " scheduled task: " + task.Name, GetClientIp());
        return Ok(new { success = ok, message });
    }

    /// <summary>POST /webadmin/api/schedule/delete — remove a task.</summary>
    [HttpPost("api/schedule/delete")]
    public async Task<IActionResult> DeleteSchedule([FromBody] JsonElement body)
    {
        var denied = AdminGuard();
        if (denied != null) return denied;

        var id = body.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id))
            return BadRequest(new { success = false, message = "Missing id." });
        var task = _schedule.GetById(id);
        if (!_schedule.Delete(id))
            return NotFound(new { success = false, message = "Task not found." });
        _logService.AddLog("schedule_delete", "Deleted scheduled task: " + (task?.Name ?? id), GetClientIp());
        return Ok(new { success = true, message = "Task deleted." });
    }

    /// <summary>GET /webadmin/api/update/check — compare the running version with the latest GitHub release (admin).</summary>
    [HttpGet("api/update/check")]
    public async Task<IActionResult> CheckUpdate()
    {
        var denied = AdminGuard();
        if (denied != null) return denied;
        try
        {
            var result = await _update.CheckAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Update] check failed");
            return StatusCode(502, new { success = false, message = "Update check failed: " + ex.Message });
        }
    }

    /// <summary>POST /webadmin/api/update/apply — download the matching release asset, stage it and restart (admin).</summary>
    [HttpPost("api/update/apply")]
    public async Task<IActionResult> ApplyUpdate()
    {
        var denied = AdminGuard();
        if (denied != null) return denied;
        var (ok, message) = await _update.ApplyAsync();
        _logService.AddLog("update_apply", message, GetClientIp());
        return Ok(new { success = ok, message });
    }

}
