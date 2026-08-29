using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Events.Meeting;
using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class ReplayEventEntry
{
    public string Time { get; set; } = "";       // elapsed "mm:ss"
    public string Type { get; set; } = "";       // murder / vote / exile / meeting / system
    public string Detail { get; set; } = "";
}

public class ReplayPlayerEntry
{
    public string Name { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string Puid { get; set; } = "";
    public bool IsHost { get; set; }
    public bool IsImpostor { get; set; }
    public bool IsDead { get; set; }
}

public class GameReplay
{
    public string GameCode { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string Map { get; set; } = "";
    public string Result { get; set; } = "";
    public string CrewmateWin { get; set; } = "";
    public List<ReplayPlayerEntry> Players { get; set; } = new();
    public List<ReplayEventEntry> Events { get; set; } = new();
}

/// <summary>
///     Per-game replay recorder: captures player roles, kill chains, votes, exiles
///     and meetings for every finished game, stored as JSON under webadmin_replays/.
///     Toggle + retention configurable from the panel (defaults on).
/// </summary>
public class GameReplayService : IEventListener
{
    private readonly ILogger<GameReplayService> _logger;
    private readonly WebAdminSettingsService _settings;
    private readonly string _replayDir;
    private readonly ConcurrentDictionary<GameCode, GameReplay> _active = new();
    private readonly object _indexLock = new();
    private List<GameReplay> _index = new(); // recent replays for the panel (in memory)

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public GameReplayService(ILogger<GameReplayService> logger, WebAdminSettingsService settings)
    {
        _logger = logger;
        _settings = settings;
        _replayDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_replays");
    }

    private bool Enabled => _settings.GetReplays().Enabled;

    // ========== Event hooks ==========

    [EventListener]
    public void OnGameStarted(IGameStartedEvent e)
    {
        if (!Enabled)
        {
            return;
        }

        var replay = new GameReplay
        {
            GameCode = e.Game.Code.Code,
            StartedAt = DateTime.UtcNow,
            Map = e.Game.Options.Map.ToString(),
        };

        foreach (var p in e.Game.Players)
        {
            replay.Players.Add(new ReplayPlayerEntry
            {
                Name = p.Character?.PlayerInfo?.PlayerName ?? p.Client.Name,
                FriendCode = p.Client.FriendCode ?? "",
                Puid = p.Client.Puid ?? "",
                IsHost = p.IsHost,
            });
        }

        _active[e.Game.Code] = replay;
        _logger.LogInformation("[Replay] Recording game {Code} ({Map}) with {Count} player(s).",
            replay.GameCode, replay.Map, replay.Players.Count);
    }

    [EventListener]
    public void OnPlayerMurder(IPlayerMurderEvent e)
    {
        if (!Enabled)
        {
            return;
        }

        if (!_active.TryGetValue(e.Game.Code, out var replay))
        {
            return;
        }

        var killer = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        var victim = e.Victim?.PlayerInfo?.PlayerName ?? "unknown";
        replay.Events.Add(new ReplayEventEntry
        {
            Time = Elapsed(replay),
            Type = "murder",
            Detail = $"{killer} 击杀了 {victim}",
        });
    }

    [EventListener]
    public void OnPlayerVoted(IPlayerVotedEvent e)
    {
        if (!Enabled)
        {
            return;
        }

        if (!_active.TryGetValue(e.Game.Code, out var replay))
        {
            return;
        }

        var voter = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        var target = e.VotedFor?.PlayerInfo?.PlayerName ?? (e.VoteType == VoteType.Skipped ? "跳过" : "弃票");
        replay.Events.Add(new ReplayEventEntry
        {
            Time = Elapsed(replay),
            Type = "vote",
            Detail = $"{voter} 投了 {target}",
        });
    }

    [EventListener]
    public void OnPlayerExile(IPlayerExileEvent e)
    {
        if (!Enabled)
        {
            return;
        }

        if (!_active.TryGetValue(e.Game.Code, out var replay))
        {
            return;
        }

        var exiled = e.ClientPlayer.Character?.PlayerInfo?.PlayerName ?? e.ClientPlayer.Client.Name;
        replay.Events.Add(new ReplayEventEntry
        {
            Time = Elapsed(replay),
            Type = "exile",
            Detail = $"{exiled} 被投出",
        });
    }

    [EventListener]
    public void OnMeetingStarted(IMeetingStartedEvent e)
    {
        if (!Enabled)
        {
            return;
        }

        if (_active.TryGetValue(e.Game.Code, out var replay))
        {
            replay.Events.Add(new ReplayEventEntry { Time = Elapsed(replay), Type = "meeting", Detail = "会议开始" });
        }
    }

    [EventListener]
    public void OnMeetingEnded(IMeetingEndedEvent e)
    {
        if (!Enabled)
        {
            return;
        }

        if (_active.TryGetValue(e.Game.Code, out var replay))
        {
            var detail = e.IsTie ? "会议结束（平票）" : (e.Exiled?.PlayerInfo?.PlayerName != null ? $"会议结束，投出 {e.Exiled.PlayerInfo.PlayerName}" : "会议结束");
            replay.Events.Add(new ReplayEventEntry { Time = Elapsed(replay), Type = "meeting", Detail = detail });
        }
    }

    [EventListener]
    public void OnGameEnded(IGameEndedEvent e)
    {
        if (!Enabled)
        {
            return;
        }

        if (!_active.TryRemove(e.Game.Code, out var replay))
        {
            return;
        }

        var reason = e.GameOverReason.ToString();
        replay.EndedAt = DateTime.UtcNow;
        replay.DurationSeconds = (int)(replay.EndedAt - replay.StartedAt).TotalSeconds;
        replay.Result = reason;
        replay.CrewmateWin = reason.StartsWith("Crewmates", StringComparison.Ordinal) ? "crewmate" : "impostor";

        foreach (var p in e.Game.Players)
        {
            var entry = replay.Players.FirstOrDefault(x => x.FriendCode == (p.Client.FriendCode ?? "") && !string.IsNullOrEmpty(x.FriendCode));
            if (entry == null)
            {
                entry = replay.Players.FirstOrDefault(x => x.Name == (p.Character?.PlayerInfo?.PlayerName ?? p.Client.Name));
            }

            if (entry != null)
            {
                entry.IsImpostor = p.Character?.PlayerInfo?.IsImpostor ?? false;
                entry.IsDead = p.Character?.PlayerInfo?.IsDead ?? false;
            }
        }

        SaveReplay(replay);
    }

    [EventListener]
    public void OnGameDestroyed(IGameDestroyedEvent e)
    {
        // A game that ended normally was already removed from _active by
        // OnGameEnded. If it is still here, the game was interrupted (host left,
        // lobby dissolved, etc.) — save it as an interrupted replay so the data
        // is not lost and single-player tests can verify the feature.
        if (_active.TryRemove(e.Game.Code, out var replay))
        {
            replay.EndedAt = DateTime.UtcNow;
            replay.DurationSeconds = (int)(replay.EndedAt - replay.StartedAt).TotalSeconds;
            replay.Result = "Interrupted";
            replay.CrewmateWin = "interrupted";
            SaveReplay(replay);
        }
    }

    // ========== Storage ==========

    private string? Elapsed(GameReplay replay)
    {
        var seconds = (int)(DateTime.UtcNow - replay.StartedAt).TotalSeconds;
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }

    private void SaveReplay(GameReplay replay)
    {
        try
        {
            Directory.CreateDirectory(_replayDir);

            var fileName = $"{Sanitize(replay.GameCode)}_{replay.StartedAt:yyyyMMdd_HHmmss}.json";
            var path = Path.Combine(_replayDir, fileName);
            var json = JsonSerializer.Serialize(replay, JsonOptions);
            File.WriteAllText(path, json);

            lock (_indexLock)
            {
                _index.Insert(0, replay);
                if (_index.Count > 200)
                {
                    _index.RemoveRange(200, _index.Count - 200);
                }
            }

            // Retention: keep only the newest MaxReplays files.
            var max = _settings.GetReplays().MaxReplays;
            var files = Directory.GetFiles(_replayDir, "*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .ToList();
            foreach (var old in files.Skip(max))
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                    // Best effort.
                }
            }

            _logger.LogInformation("[Replay] Saved replay {File} ({Dur}s, {Result}).",
                fileName, replay.DurationSeconds, replay.Result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Replay] Failed to save replay for game {Code}.", replay.GameCode);
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    // ========== Panel API ==========

    public Task<List<GameReplay>> GetListAsync()
    {
        lock (_indexLock)
        {
            return Task.FromResult(_index.Select(r => new GameReplay
            {
                GameCode = r.GameCode,
                StartedAt = r.StartedAt,
                EndedAt = r.EndedAt,
                DurationSeconds = r.DurationSeconds,
                Map = r.Map,
                Result = r.Result,
                CrewmateWin = r.CrewmateWin,
                Players = r.Players,
                Events = new List<ReplayEventEntry>(),
            }).ToList());
        }
    }

    public Task<GameReplay?> GetByFileNameAsync(string fileName)
    {
        try
        {
            // Strict containment: sanitize + full-path check inside the replay dir.
            var path = SafeFiles.ResolveInside(_replayDir, fileName);
            if (path == null || !File.Exists(path))
            {
                return Task.FromResult<GameReplay?>(null);
            }

            var json = File.ReadAllText(path);
            return Task.FromResult(JsonSerializer.Deserialize<GameReplay>(json, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Replay] Failed to read {File}.", fileName);
            return Task.FromResult<GameReplay?>(null);
        }
    }

    public Task<List<string>> GetFilesAsync()
    {
        if (!Directory.Exists(_replayDir))
        {
            return Task.FromResult(new List<string>());
        }

        return Task.FromResult(Directory.GetFiles(_replayDir, "*.json")
            .Select(Path.GetFileName)
            .OrderByDescending(f => f)
            .ToList());
    }

    public Task<int> DeleteAllAsync()
    {
        if (!Directory.Exists(_replayDir))
        {
            return Task.FromResult(0);
        }

        var count = 0;
        foreach (var f in Directory.GetFiles(_replayDir, "*.json"))
        {
            try
            {
                File.Delete(f);
                count++;
            }
            catch
            {
                // Best effort.
            }
        }

        lock (_indexLock)
        {
            _index.Clear();
        }

        return Task.FromResult(count);
    }
}