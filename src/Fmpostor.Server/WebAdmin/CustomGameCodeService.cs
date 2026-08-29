using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class GameCodesStore
{
    public bool Enabled { get; set; } = true;
    public List<string> Codes { get; set; } = new();
}

/// <summary>
///     Custom room codes: a pool of administrator-defined 4/6-letter A-Z codes.
///     Implements IGameCodeFactory so the GameManager draws from this pool, and
///     releases codes back when their game is destroyed. Editable from the panel.
/// </summary>
public class CustomGameCodeService : IGameCodeFactory, IEventListener
{
    private static readonly HashSet<char> ValidChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToHashSet();

    private readonly string _filePath;
    private readonly ILogger<CustomGameCodeService> _logger;
    private readonly object _lock = new();
    private GameCodesStore _store;
    private readonly List<string> _available = new();
    private readonly HashSet<string> _inUse = new();
    private long _fileVersion = -1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public CustomGameCodeService(IOptions<WebAdminConfig> config, ILogger<CustomGameCodeService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.GameCodesFile);
        _store = new GameCodesStore();
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                Save();
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    var data = JsonSerializer.Deserialize<GameCodesStore>(json, JsonOptions);
                    if (data != null)
                    {
                        _store = data;
                        _store.Codes = (_store.Codes ?? new List<string>())
                            .Select(NormalizeCode)
                            .Where(c => c != null)
                            .Cast<string>()
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        _fileVersion = GetFileVersion();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Codes] Failed to load {File}, using defaults.", _filePath);
                }
            }

            RebuildPool();
            _logger.LogInformation("[Codes] Loaded {Count} custom code(s) from {File} (enabled={Enabled}).",
                _store.Codes.Count, _filePath, _store.Enabled);
        }
    }

    private void RebuildPool()
    {
        _available.Clear();
        _inUse.Clear();
        foreach (var code in _store.Codes)
        {
            if (!string.IsNullOrEmpty(code))
            {
                _available.Add(code);
            }
        }
    }

    private void TryReload()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        var version = GetFileVersion();
        if (version == _fileVersion)
        {
            return;
        }

        _fileVersion = version;
        Load();
    }

    private long GetFileVersion()
    {
        var fi = new FileInfo(_filePath);
        return (fi.LastWriteTimeUtc.Ticks << 8) ^ fi.Length;
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_store, JsonOptions);
                File.WriteAllText(_filePath, json);
                _fileVersion = GetFileVersion();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Codes] Failed to save {File}.", _filePath);
            }
        }
    }

    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var upper = code.Trim().ToUpperInvariant();
        if (upper.Length != 4 && upper.Length != 6)
        {
            return null;
        }

        return upper.All(c => ValidChars.Contains(c)) ? upper : null;
    }

    // ========== IGameCodeFactory ==========

    public GameCode Create()
    {
        lock (_lock)
        {
            TryReload();
            if (_store.Enabled && _available.Count > 0)
            {
                var index = Next(0, _available.Count);
                var code = _available[index];
                _available.RemoveAt(index);
                _inUse.Add(code);
                _logger.LogDebug("[Codes] Allocated custom code {Code} ({Count} left)", code, _available.Count);
                return new GameCode(code);
            }

            if (_store.Enabled && _store.Codes.Count > 0)
            {
                _logger.LogWarning("[Codes] Custom code pool exhausted - falling back to random code.");
            }

            return GameCode.Create();
        }
    }

    /// <summary>
    ///     Cryptographic random index without modulo bias.
    /// </summary>
    private static int Next(int minValue, int maxExclusiveValue)
    {
        if (minValue >= maxExclusiveValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minValue));
        }

        var range = (long)maxExclusiveValue - minValue;
        var limit = (long)((ulong.MaxValue / (ulong)range) * (ulong)range);

        uint value;
        do
        {
            var bytes = new byte[4];
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt32(bytes);
        }
        while ((ulong)value >= (ulong)limit);

        return (int)(minValue + (long)((ulong)value % (ulong)range));
    }

    // ========== Event hooks (release codes when a game is destroyed) ==========

    [EventListener]
    public void OnGameDestroyed(IGameDestroyedEvent e)
    {
        Release(e.Game.Code.Code);
    }

    public void Release(string code)
    {
        lock (_lock)
        {
            if (_inUse.Remove(code) && !_available.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                _available.Add(code);
                _logger.LogDebug("[Codes] Released custom code {Code} back to pool", code);
            }
        }
    }

    // ========== Panel API backing ==========

    public GameCodesStore GetSnapshot()
    {
        lock (_lock)
        {
            TryReload();
            return new GameCodesStore
            {
                Enabled = _store.Enabled,
                Codes = _store.Codes.ToList(),
            };
        }
    }

    public (List<string> Available, List<string> InUse) GetPoolState()
    {
        lock (_lock)
        {
            TryReload();
            return (_available.ToList(), _inUse.ToList());
        }
    }

    public bool AddCode(string code)
    {
        var normalized = NormalizeCode(code);
        if (normalized == null)
        {
            return false;
        }

        lock (_lock)
        {
            TryReload();
            if (_store.Codes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            _store.Codes.Add(normalized);
            _available.Add(normalized);
            Save();
            _logger.LogInformation("[Codes] Added custom code {Code}", normalized);
            return true;
        }
    }

    public bool RemoveCode(string code)
    {
        var normalized = NormalizeCode(code);
        if (normalized == null)
        {
            return false;
        }

        lock (_lock)
        {
            TryReload();
            var removed = _store.Codes.RemoveAll(c => c.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            _available.RemoveAll(c => c.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            _inUse.Remove(normalized);
            if (removed > 0)
            {
                Save();
                _logger.LogInformation("[Codes] Removed custom code {Code}", normalized);
            }

            return removed > 0;
        }
    }

    public void UpdateSettings(bool? enabled)
    {
        lock (_lock)
        {
            TryReload();
            if (enabled.HasValue)
            {
                _store.Enabled = enabled.Value;
            }

            Save();
        }
    }
}