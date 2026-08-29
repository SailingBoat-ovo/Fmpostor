using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

internal class UserConfig
{
    /// <summary>
    ///     Legacy plaintext password (kept only for backward compatibility;
    ///     automatically migrated to <see cref="PasswordHash" /> on load).
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    ///     PBKDF2 password hash: "pbkdf2$iterations$salt_b64$hash_b64".
    /// </summary>
    public string PasswordHash { get; set; } = "";

    public string Role { get; set; } = "user";
}

internal class SessionInfo
{
    public string Username { get; set; } = "";
    public DateTime LastActivity { get; set; }
}

public class WebAdminAuthService
{
    private readonly string _authFilePath;
    private readonly ILogger<WebAdminAuthService> _logger;
    private readonly WebAdminConfig _config;
    private readonly object _lock = new();
    private Dictionary<string, UserConfig> _users;
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private long _authFileVersion = -1;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(18);

    // Login brute-force protection: key = "ip|username".
    private readonly Dictionary<string, (int FailCount, DateTime LockUntil)> _loginFails = new();
    // Username-only lockout: counts failures across ALL source IPs so rotating the
    // (possibly spoofable) client IP cannot bypass the per-key lock.
    private readonly Dictionary<string, (int FailCount, DateTime LockUntil)> _userFails = new();
    private const int MaxLoginFails = 5;
    private const int MaxUserFails = 20;
    private static readonly TimeSpan LoginLockDuration = TimeSpan.FromMinutes(5);
    private const int MaxLockEntries = 10_000;

    private const int Pbkdf2Iterations = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public WebAdminAuthService(IOptions<WebAdminConfig> config, ILogger<WebAdminAuthService> logger)
    {
        _config = config.Value;
        _authFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.AuthFile);
        _logger = logger;

        // Default admin is only a fallback for the first start; once webadmin_auth.json
        // exists, users (with plaintext passwords) are loaded from that file on every start.
        _users = new Dictionary<string, UserConfig>
        {
            { WebAdminConfig.DefaultAdminUsername, new UserConfig { Password = WebAdminConfig.DefaultAdminPassword, Role = "admin" } }
        };

        LoadOrCreateAuthConfig();
    }

    private void LoadOrCreateAuthConfig()
    {
        lock (_lock)
        {
            if (File.Exists(_authFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_authFilePath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var parsed = ParseUsers(root, out var hasPlaintext);
                    if (parsed != null && hasPlaintext && parsed.Count > 0)
                    {
                        // Valid plaintext format: load exactly what the file contains and
                        // never rewrite it, so admin-configured passwords are preserved.
                        _users = parsed;
                        _authFileVersion = GetAuthFileVersion();
                        _logger.LogInformation("[WebAdmin] Auth file {File} loaded with {Count} user(s).", _authFilePath, _users.Count);
                        return;
                    }

                    _logger.LogWarning("[WebAdmin] {File} is missing or in an unsupported format (no plaintext 'password' users); resetting to the default admin.", _authFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[WebAdmin] Failed to read {File}; resetting to the default admin.", _authFilePath);
                }
            }

            _users = new Dictionary<string, UserConfig>
            {
                { WebAdminConfig.DefaultAdminUsername, new UserConfig { Password = WebAdminConfig.DefaultAdminPassword, Role = "admin" } }
            };
            SaveAuthConfig();
            _logger.LogWarning(
                "[WebAdmin] Default admin credentials created: username '{User}'. The initial password is the well-known compiled-in default and MUST be changed via the web panel before the panel becomes usable (API access is blocked until then).",
                WebAdminConfig.DefaultAdminUsername);
        }
    }

    private Dictionary<string, UserConfig>? ParseUsers(JsonElement root, out bool hasPlaintext)
    {
        hasPlaintext = false;
        if (!root.TryGetProperty("users", out var usersEl))
        {
            return null;
        }

        var result = new Dictionary<string, UserConfig>();
        foreach (var user in usersEl.EnumerateObject())
        {
            var hasPassword = user.Value.TryGetProperty("password", out var pwd);
            var hasHash = user.Value.TryGetProperty("passwordHash", out var hash);
            if (!hasPassword && !hasHash)
            {
                _logger.LogWarning(
                    "[WebAdmin] User '{User}' in {File} has neither 'password' nor 'passwordHash'; skipped.",
                    user.Name,
                    _authFilePath);
                continue;
            }

            var role = user.Value.TryGetProperty("role", out var r) ? r.GetString() : "user";
            var config = new UserConfig { Role = role ?? "user" };
            if (hasPassword)
            {
                config.Password = pwd.GetString() ?? "";
                hasPlaintext = true;
            }

            if (hasHash)
            {
                config.PasswordHash = hash.GetString() ?? "";
            }

            result[user.Name] = config;
        }

        return result;
    }

    private long GetAuthFileVersion()
    {
        var fi = new FileInfo(_authFilePath);
        return (fi.LastWriteTimeUtc.Ticks << 8) ^ fi.Length;
    }

    private void TryReloadAuthFile()
    {
        if (!File.Exists(_authFilePath))
        {
            return;
        }

        var version = GetAuthFileVersion();
        if (version == _authFileVersion)
        {
            return;
        }

        _authFileVersion = version;
        try
        {
            var json = File.ReadAllText(_authFilePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var parsed = ParseUsers(root, out var hasPlaintext);
            if (parsed == null || !hasPlaintext || parsed.Count == 0)
            {
                _logger.LogWarning("[WebAdmin] Reload of {File} ignored: no plaintext users found.", _authFilePath);
                return;
            }

            _users = parsed;
            _logger.LogInformation("[WebAdmin] Reloaded {Count} user(s) from {File}.", _users.Count, _authFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WebAdmin] Failed to reload {File}; keeping current users.", _authFilePath);
        }
    }

    public void SaveAuthConfig()
    {
        lock (_lock)
        {
            var usersDict = new Dictionary<string, object>();
            foreach (var kv in _users)
            {
                usersDict[kv.Key] = new
                {
                    password = kv.Value.Password,
                    role = kv.Value.Role,
                };
            }

            var data = new
            {
                users = usersDict,
            };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(_authFilePath, json);
            _authFileVersion = GetAuthFileVersion();
        }
    }

    public string? ValidateSession(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        lock (_sessions)
        {
            if (_sessions.TryGetValue(token, out var si) && DateTime.UtcNow - si.LastActivity < SessionTimeout)
            {
                si.LastActivity = DateTime.UtcNow;
                return si.Username;
            }
            if (token != null && _sessions.ContainsKey(token))
                _sessions.Remove(token);
        }

        return null;
    }

    public string CreateSession(string username)
    {
        var token = Guid.NewGuid().ToString("N");
        lock (_sessions)
        {
            _sessions[token] = new SessionInfo { Username = username, LastActivity = DateTime.UtcNow };
        }

        CleanupSessions();
        return token;
    }

    public void DestroySession(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return;
        lock (_sessions)
        {
            _sessions.Remove(token);
        }
    }

    private void CleanupSessions()
    {
        var cutoff = DateTime.UtcNow - SessionTimeout;
        lock (_sessions)
        {
            var expired = _sessions.Where(kv => kv.Value.LastActivity < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
                _sessions.Remove(key);
        }
    }

    /// <summary>
    ///     Authenticates with brute-force protection. Passwords are verified
    ///     against the PBKDF2 hash when available (legacy plaintext is accepted
    ///     once and migrated). After MaxLoginFails failures the key
    ///     (ip|username) is locked for LoginLockDuration.
    /// </summary>
    public bool TryAuthenticate(string username, string password, string clientKey, out string? role, out bool locked)
    {
        role = null;
        locked = false;

        lock (_lock)
        {
            TryReloadAuthFile();

            // Clean expired locks lazily.
            var now = DateTime.UtcNow;
            foreach (var kv in _loginFails.Where(kv => kv.Value.LockUntil != default && kv.Value.LockUntil <= now).ToList())
            {
                _loginFails.Remove(kv.Key);
            }

            foreach (var kv in _userFails.Where(kv => kv.Value.LockUntil != default && kv.Value.LockUntil <= now).ToList())
            {
                _userFails.Remove(kv.Key);
            }

            if (_loginFails.TryGetValue(clientKey, out var fail) && fail.LockUntil > now)
            {
                locked = true;
                return false;
            }

            if (_userFails.TryGetValue(username, out var userFail) && userFail.LockUntil > now)
            {
                locked = true;
                return false;
            }

            if (!_users.TryGetValue(username, out var userConfig) || !VerifyPassword(password, userConfig))
            {
                RecordFailure(clientKey, _loginFails, MaxLoginFails, username, now);
                RecordFailure(username, _userFails, MaxUserFails, username, now);
                locked = _loginFails.TryGetValue(clientKey, out var f2) && f2.LockUntil > now
                    || _userFails.TryGetValue(username, out var f3) && f3.LockUntil > now;
                return false;
            }

            _loginFails.Remove(clientKey);
            _userFails.Remove(username);

            // Passwords are stored as plaintext in the server-local auth file
            // (per operator decision: anyone who can read this file already has
            // server access). For smooth upgrades, a legacy PBKDF2 hash left in
            // the file by a previous build is still accepted once and converted
            // back to plaintext.
            if (!string.IsNullOrEmpty(userConfig.PasswordHash) && string.IsNullOrEmpty(userConfig.Password))
            {
                userConfig.Password = password;
                userConfig.PasswordHash = "";
                SaveAuthConfig();
                _logger.LogInformation("[Auth] Converted legacy password hash back to plaintext for user {User}.", username);
            }

            role = userConfig.Role;
            return true;
        }
    }

    private void RecordFailure(string key, Dictionary<string, (int FailCount, DateTime LockUntil)> table, int max, string logKey, DateTime now)
    {
        var (count, _) = table.TryGetValue(key, out var f) ? f : (0, default);
        count++;
        if (count >= max)
        {
            table[key] = (0, DateTime.UtcNow.Add(LoginLockDuration));
            _logger.LogWarning("[Auth] Too many failed logins for {Key}, locked {Minutes} minutes.", logKey, LoginLockDuration.TotalMinutes);
        }
        else
        {
            table[key] = (count, default);
        }

        // Bound the tables so spoofed keys / username spray cannot grow memory.
        if (table.Count > MaxLockEntries)
        {
            foreach (var kv in table.Where(kv => kv.Value.LockUntil == default || kv.Value.LockUntil <= now).Take(MaxLockEntries / 2).ToList())
            {
                table.Remove(kv.Key);
            }
        }
    }

    /// <summary>
    ///     Returns the remaining lock seconds for the given login key (also honouring
    ///     the username-only lock), 0 when not locked.
    /// </summary>
    public int GetLoginLockRemaining(string clientKey, string? username = null)
    {
        lock (_lock)
        {
            var remaining = 0;
            if (_loginFails.TryGetValue(clientKey, out var fail) && fail.LockUntil > DateTime.UtcNow)
            {
                remaining = (int)Math.Ceiling((fail.LockUntil - DateTime.UtcNow).TotalSeconds);
            }

            if (!string.IsNullOrEmpty(username)
                && _userFails.TryGetValue(username, out var uf)
                && uf.LockUntil > DateTime.UtcNow)
            {
                remaining = Math.Max(remaining, (int)Math.Ceiling((uf.LockUntil - DateTime.UtcNow).TotalSeconds));
            }

            return remaining;
        }
    }

    /// <summary>
    ///     True while the given user still authenticates with the well-known default
    ///     admin password (the panel is locked down until it is changed).
    /// </summary>
    public bool NeedsPasswordChange(string username)
    {
        if (!string.Equals(username, WebAdminConfig.DefaultAdminUsername, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_lock)
        {
            return _users.TryGetValue(username, out var u)
                && !string.IsNullOrEmpty(u.Password)
                && u.Password == WebAdminConfig.DefaultAdminPassword;
        }
    }

    /// <summary>
    ///     Invalidates every active session of the given user (used after password
    ///     change and account deletion so old tokens cannot outlive the credential).
    /// </summary>
    public void InvalidateUserSessions(string username, string? exceptToken = null)
    {
        lock (_sessions)
        {
            var doomed = _sessions
                .Where(kv => string.Equals(kv.Value.Username, username, StringComparison.OrdinalIgnoreCase) && kv.Key != exceptToken)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in doomed)
            {
                _sessions.Remove(key);
            }
        }
    }

    private static bool VerifyPassword(string password, UserConfig user)
    {
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            var parts = user.PasswordHash.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
            {
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }

        // Legacy plaintext comparison (constant-time to avoid a length/char oracle).
        var expectedBytes = Encoding.UTF8.GetBytes(user.Password ?? string.Empty);
        var actualBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    public string? GetUserRole(string username)
    {
        lock (_lock)
        {
            TryReloadAuthFile();
            if (_users.TryGetValue(username, out var uc))
                return uc.Role;
        }
        return null;
    }

    public bool IsAdmin(string username)
    {
        return GetUserRole(username) == "admin";
    }

    public bool ChangePassword(string username, string oldPassword, string newPassword)
    {
        // Minimum length for NEW passwords (existing short passwords keep working
        // until changed). Prevents "1234"-style panel accounts.
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8)
        {
            return false;
        }

        lock (_lock)
        {
            TryReloadAuthFile();
            if (!_users.TryGetValue(username, out var uConfig) || !VerifyPassword(oldPassword, uConfig))
                return false;

            uConfig.Password = newPassword;
            uConfig.PasswordHash = "";
            SaveAuthConfig();
            // Old sessions must not survive a credential change (keep the caller's
            // own session alive — the controller passes its current token).
            return true;
        }
    }

    public bool AddUser(string username, string password, string role)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || password.Length < 8)
        {
            return false;
        }

        lock (_lock)
        {
            TryReloadAuthFile();
            if (_users.ContainsKey(username))
                return false;

            _users[username] = new UserConfig
            {
                Password = password,
                Role = role == "admin" ? "admin" : "user",
            };
            SaveAuthConfig();
            return true;
        }
    }

    public bool DeleteUser(string username)
    {
        lock (_lock)
        {
            TryReloadAuthFile();
            if (!_users.ContainsKey(username))
                return false;

            _users.Remove(username);
            SaveAuthConfig();
            return true;
        }
    }

    public List<(string Username, string Role)> GetUsers()
    {
        lock (_lock)
        {
            TryReloadAuthFile();
            return _users.Select(u => (u.Key, u.Value.Role)).ToList();
        }
    }

    public bool UserExists(string username)
    {
        lock (_lock)
        {
            TryReloadAuthFile();
            return _users.ContainsKey(username);
        }
    }
}
