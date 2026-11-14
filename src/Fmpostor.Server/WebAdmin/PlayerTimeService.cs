using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class PlayerTimeEntry
{
    public string FriendCode { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public DateTime FirstJoinTime { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginTime { get; set; } = DateTime.UtcNow;
    public int TotalPlayTimeMinutes { get; set; }
}

public class PlayerTimeStore
{
    public Dictionary<string, PlayerTimeEntry> Players { get; set; } = new();
}

/// <summary>
///     Player play-time tracking (ported from Fanchuan.WelcomePlayer.Plugin):
///     tracks online minutes per friend code, persisted to
///     webadmin_player_times.json (hot-reloaded). Used by the welcome service
///     for {PLAY_TIME}/{LAST_LOGIN_TIME}/{TIME_SINCE_LAST_LOGIN} placeholders.
/// </summary>
public class PlayerTimeService : IEventListener, IHostedService
{
    private readonly ILogger<PlayerTimeService> _logger;
    private readonly IEventManager _eventManager;
    private IDisposable? _eventRegistration;
    private readonly string _filePath;
    private readonly object _lock = new();
    private PlayerTimeStore _store;
    private long _fileVersion = -1;

    // friend code -> session start
    private readonly Dictionary<string, DateTime> _sessions = new();

    // friend code -> 本次进房之前的状态(上次登录时间 / 是否第一次来)。
    //   进房时 AddPlayer 会立刻把"上次登录"改成现在, 而欢迎语是在身体生成之后才发的 ——
    //   不先记下旧值的话, 欢迎语里的"上次登录"永远是此刻、"距上次登录"永远是"刚刚"。
    private readonly Dictionary<string, (DateTime? PreviousLogin, bool FirstVisit)> _joinInfo = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public PlayerTimeService(IOptions<WebAdminConfig> config, ILogger<PlayerTimeService> logger, IEventManager eventManager)
    {
        _logger = logger;
        _eventManager = eventManager;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.PlayerTimesFile);
        _store = new PlayerTimeStore();
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
                var data = JsonSerializer.Deserialize<PlayerTimeStore>(json, JsonOptions);
                if (data != null)
                {
                    _store = data;
                    _store.Players ??= new Dictionary<string, PlayerTimeEntry>();
                    _fileVersion = GetFileVersion();
                    _logger.LogInformation("[PlayerTime] Loaded {Count} player record(s) from {File}.",
                        _store.Players.Count, _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PlayerTime] Failed to load {File}.", _filePath);
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

    private void Save()
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
                _logger.LogWarning(ex, "[PlayerTime] Failed to save {File}.", _filePath);
            }
        }
    }

    private static string GetKey(IClientPlayer player)
    {
        return player.Client.FriendCode ?? player.Client.Puid ?? player.Client.Name ?? player.Client.Id.ToString();
    }

    public void AddPlayer(IClientPlayer player)
    {
        var key = GetKey(player);
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            TryReload();
            var existed = _store.Players.TryGetValue(key, out var entry);
            if (!existed || entry == null)
            {
                entry = new PlayerTimeEntry { FriendCode = key };
                _store.Players[key] = entry;
            }

            _joinInfo[key] = (existed ? entry.LastLoginTime : null, !existed);

            entry.PlayerName = player.Client.Name ?? entry.PlayerName;
            entry.LastLoginTime = now;
            _sessions[key] = now;
            Save();
        }
    }

    public void RemovePlayer(IClientPlayer player)
    {
        var key = GetKey(player);

        lock (_lock)
        {
            TryReload();
            _joinInfo.Remove(key);

            if (_sessions.Remove(key, out var start))
            {
                var minutes = (int)Math.Round((DateTime.UtcNow - start).TotalMinutes);
                if (minutes > 0 && _store.Players.TryGetValue(key, out var entry))
                {
                    entry.TotalPlayTimeMinutes += minutes;
                    Save();
                }
            }
        }
    }

    public void ClearAllSessions()
    {
        lock (_lock)
        {
            foreach (var (key, start) in _sessions)
            {
                var minutes = (int)Math.Round((DateTime.UtcNow - start).TotalMinutes);
                if (minutes > 0 && _store.Players.TryGetValue(key, out var entry))
                {
                    entry.TotalPlayTimeMinutes += minutes;
                }
            }

            _sessions.Clear();
            Save();
        }
    }

    /// <summary>
    ///     Returns the player record (a copy) or null when unknown.
    /// </summary>
    public PlayerTimeEntry? GetPlayerData(IClientPlayer player)
    {
        var key = GetKey(player);
        lock (_lock)
        {
            TryReload();
            return _store.Players.TryGetValue(key, out var entry)
                ? new PlayerTimeEntry
                {
                    FriendCode = entry.FriendCode,
                    PlayerName = entry.PlayerName,
                    FirstJoinTime = entry.FirstJoinTime,
                    LastLoginTime = entry.LastLoginTime,
                    TotalPlayTimeMinutes = entry.TotalPlayTimeMinutes,
                }
                : null;
        }
    }

    /// <summary>
    ///     欢迎语用的玩家资料: "上次登录"是【本次进房之前】的那一次; 另外告诉调用方这是不是第一次来。
    ///     没有进房记录时(例如服务端刚重启)退回当前资料。
    /// </summary>
    public (PlayerTimeEntry? Data, bool FirstVisit) GetWelcomeData(IClientPlayer player)
    {
        var data = GetPlayerData(player);
        var key = GetKey(player);

        lock (_lock)
        {
            if (!_joinInfo.TryGetValue(key, out var info))
            {
                return (data, data == null);
            }

            if (data != null && info.PreviousLogin.HasValue)
            {
                data.LastLoginTime = info.PreviousLogin.Value;
            }

            return (data, info.FirstVisit);
        }
    }

    public Dictionary<string, PlayerTimeEntry> GetSnapshot()
    {
        lock (_lock)
        {
            TryReload();
            return _store.Players.ToDictionary(p => p.Key, p => new PlayerTimeEntry
            {
                FriendCode = p.Value.FriendCode,
                PlayerName = p.Value.PlayerName,
                FirstJoinTime = p.Value.FirstJoinTime,
                LastLoginTime = p.Value.LastLoginTime,
                TotalPlayTimeMinutes = p.Value.TotalPlayTimeMinutes,
            });
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _store.Players.Clear();
            _sessions.Clear();
            Save();
        }
    }

    // ========== Events ==========

    [EventListener]
    public ValueTask OnPlayerJoined(IGamePlayerJoinedEvent e)
    {
        AddPlayer(e.Player);
        return default;
    }

    [EventListener]
    public ValueTask OnPlayerLeft(IGamePlayerLeftEvent e)
    {
        RemovePlayer(e.Player);
        return default;
    }

    [EventListener]
    public ValueTask OnPlayerDestroyed(IPlayerDestroyedEvent e)
    {
        RemovePlayer(e.ClientPlayer);
        return default;
    }

    [EventListener]
    public ValueTask OnGameDestroyed(IGameDestroyedEvent e)
    {
        ClearAllSessions();
        return default;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 必须显式注册监听器，否则 [EventListener] 方法永远不会被调用，
        // 在线时长记录永远是空表（面板"在线时长"页因此无数据）。
        _eventRegistration = _eventManager.RegisterListener(this);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _eventRegistration?.Dispose();
        ClearAllSessions();
        return Task.CompletedTask;
    }
}