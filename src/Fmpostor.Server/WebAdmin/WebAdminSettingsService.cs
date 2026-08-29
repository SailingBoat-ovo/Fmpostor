using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class DeltaPortSettings
{
    public bool Enabled { get; set; } = true;
    public int Start { get; set; } = 22024;
    public int End { get; set; } = 22223;
}

public class ReplaySettings
{
    /// <summary>
    ///     Per-game replay recording (JSON under webadmin_replays/). Defaults to on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Maximum number of replay files kept on disk (oldest removed first).
    /// </summary>
    public int MaxReplays { get; set; } = 100;
}

public class FootprintSettings
{
    /// <summary>
    ///     Per-player footprint tracking (session history, total online time, IPs).
    ///     Defaults to on. Storage is capped.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public class RoomCleanupSettings
{
    /// <summary>
    ///     Auto-destroy empty lobby rooms that have been empty for TtlMinutes.
    ///     Defaults to on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Minutes an empty room may stay alive before cleanup.
    /// </summary>
    public int TtlMinutes { get; set; } = 10;
}

public class WebAdminSettings
{
    public DeltaPortSettings DeltaPorts { get; set; } = new();
    public ReplaySettings Replays { get; set; } = new();
    public FootprintSettings Footprints { get; set; } = new();
    public RoomCleanupSettings RoomCleanup { get; set; } = new();
}

/// <summary>
///     Panel-editable server settings, persisted to webadmin_settings.json.
///     Values configured in config.json are used as initial defaults; once the
///     settings file exists it takes precedence (hot reloadable).
/// </summary>
public class WebAdminSettingsService
{
    private readonly string _filePath;
    private readonly ILogger<WebAdminSettingsService> _logger;
    private readonly object _lock = new();
    private WebAdminSettings _settings;
    private long _fileVersion = -1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public WebAdminSettingsService(IOptions<WebAdminConfig> config, ILogger<WebAdminSettingsService> logger)
    {
        _logger = logger;

        // Initial defaults come from config.json's WebAdmin section.
        _settings = new WebAdminSettings
        {
            DeltaPorts = new DeltaPortSettings
            {
                Enabled = config.Value.DeltaPortsEnabled,
                Start = config.Value.DeltaPortStart,
                End = config.Value.DeltaPortEnd,
            },
        };

        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_settings.json");
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<WebAdminSettings>(json, JsonOptions);
                if (data != null)
                {
                    // Merge: fields missing from an older settings file keep defaults,
                    // then the file is rewritten so new fields are persisted (auto-migration).
                    _settings.DeltaPorts = Normalize(data.DeltaPorts ?? new DeltaPortSettings
                    {
                        Enabled = _settings.DeltaPorts.Enabled,
                        Start = _settings.DeltaPorts.Start,
                        End = _settings.DeltaPorts.End,
                    });
                    _settings.Replays = data.Replays ?? new ReplaySettings();
                    _settings.Footprints = data.Footprints ?? new FootprintSettings();
                    _settings.RoomCleanup = data.RoomCleanup ?? new RoomCleanupSettings();

                    _fileVersion = GetFileVersion();
                    Save();
                    _logger.LogInformation("[Settings] Loaded webadmin settings (DeltaPorts={DPe} {DS}-{DE}, Replays={Re}, Footprints={Fe}, RoomCleanup={RCe}).",
                        _settings.DeltaPorts.Enabled, _settings.DeltaPorts.Start, _settings.DeltaPorts.End,
                        _settings.Replays.Enabled, _settings.Footprints.Enabled, _settings.RoomCleanup.Enabled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Settings] Failed to load {File}, using config defaults.", _filePath);
            }
        }
    }

    private static DeltaPortSettings Normalize(DeltaPortSettings s)
    {
        if (s.Start <= 0 || s.End < s.Start || s.End > 65535)
        {
            s.Start = 22024;
            s.End = 22223;
        }

        return s;
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
                var json = JsonSerializer.Serialize(_settings, JsonOptions);
                File.WriteAllText(_filePath, json);
                _fileVersion = GetFileVersion();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Settings] Failed to save {File}.", _filePath);
            }
        }
    }

    public DeltaPortSettings GetDeltaPorts()
    {
        lock (_lock)
        {
            TryReload();
            return new DeltaPortSettings
            {
                Enabled = _settings.DeltaPorts.Enabled,
                Start = _settings.DeltaPorts.Start,
                End = _settings.DeltaPorts.End,
            };
        }
    }

    public ReplaySettings GetReplays()
    {
        lock (_lock)
        {
            TryReload();
            return new ReplaySettings
            {
                Enabled = _settings.Replays.Enabled,
                MaxReplays = _settings.Replays.MaxReplays > 0 ? _settings.Replays.MaxReplays : 100,
            };
        }
    }

    public FootprintSettings GetFootprints()
    {
        lock (_lock)
        {
            TryReload();
            return new FootprintSettings { Enabled = _settings.Footprints.Enabled };
        }
    }

    public RoomCleanupSettings GetRoomCleanup()
    {
        lock (_lock)
        {
            TryReload();
            return new RoomCleanupSettings
            {
                Enabled = _settings.RoomCleanup.Enabled,
                TtlMinutes = _settings.RoomCleanup.TtlMinutes > 0 ? _settings.RoomCleanup.TtlMinutes : 10,
            };
        }
    }

    /// <summary>
    ///     Updates storage/cleanup feature toggles (replays, footprints, room cleanup).
    /// </summary>
    public void UpdateFeatureSettings(bool? replaysEnabled = null, int? maxReplays = null, bool? footprintsEnabled = null, bool? roomCleanupEnabled = null, int? roomCleanupTtlMinutes = null)
    {
        lock (_lock)
        {
            TryReload();
            if (replaysEnabled.HasValue)
            {
                _settings.Replays.Enabled = replaysEnabled.Value;
            }

            if (maxReplays.HasValue && maxReplays.Value > 0)
            {
                _settings.Replays.MaxReplays = maxReplays.Value;
            }

            if (footprintsEnabled.HasValue)
            {
                _settings.Footprints.Enabled = footprintsEnabled.Value;
            }

            if (roomCleanupEnabled.HasValue)
            {
                _settings.RoomCleanup.Enabled = roomCleanupEnabled.Value;
            }

            if (roomCleanupTtlMinutes.HasValue && roomCleanupTtlMinutes.Value > 0)
            {
                _settings.RoomCleanup.TtlMinutes = roomCleanupTtlMinutes.Value;
            }

            Save();
        }
    }

    /// <summary>
    ///     Applies new delta port settings from the panel. Returns the normalized
    ///     values actually stored.
    /// </summary>
    public DeltaPortSettings UpdateDeltaPorts(bool? enabled, int? start, int? end)
    {
        lock (_lock)
        {
            TryReload();
            var s = _settings.DeltaPorts;

            if (enabled.HasValue)
            {
                s.Enabled = enabled.Value;
            }

            if (start.HasValue && start.Value > 0 && start.Value <= 65535)
            {
                s.Start = start.Value;
            }

            if (end.HasValue && end.Value > 0 && end.Value <= 65535)
            {
                s.End = end.Value;
            }

            s = Normalize(s);
            _settings.DeltaPorts = s;
            Save();
            _logger.LogInformation("[Settings] Delta ports updated: enabled={Enabled}, range={Start}-{End}",
                s.Enabled, s.Start, s.End);
            return new DeltaPortSettings { Enabled = s.Enabled, Start = s.Start, End = s.End };
        }
    }
}