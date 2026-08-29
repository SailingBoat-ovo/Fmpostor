using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Games.Managers;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Automatically destroys lobby rooms that have been empty for longer than the
///     configured TTL, preventing zombie rooms from squatting on game codes.
///     Toggle + TTL configurable from the panel (defaults on, 10 minutes).
/// </summary>
public class RoomCleanupService : IDisposable
{
    private readonly ILogger<RoomCleanupService> _logger;
    private readonly IGameManager _gameManager;
    private readonly WebAdminSettingsService _settings;
    private readonly ConcurrentDictionary<GameCode, DateTime> _createdAt = new();
    private readonly Timer _timer;

    public RoomCleanupService(ILogger<RoomCleanupService> logger, IGameManager gameManager, WebAdminSettingsService settings)
    {
        _logger = logger;
        _gameManager = gameManager;
        _settings = settings;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void TrackCreated(IGame game)
    {
        _createdAt[game.Code] = DateTime.UtcNow;
    }

    public void Untrack(GameCode code)
    {
        _createdAt.TryRemove(code, out _);
    }

    private void Tick()
    {
        var cfg = _settings.GetRoomCleanup();
        if (!cfg.Enabled)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(cfg.TtlMinutes);

        foreach (var game in _gameManager.Games.ToList())
        {
            if (game.PlayerCount > 0 || game.GameState != GameStates.NotStarted)
            {
                continue;
            }

            var created = _createdAt.TryGetValue(game.Code, out var t) ? t : DateTime.UtcNow;
            if (created <= cutoff)
            {
                _logger.LogInformation("[Cleanup] Destroying empty lobby room {Code} (empty since {Since}).",
                    game.Code.Code, created.ToString("HH:mm:ss"));
                _ = _gameManager.RemoveAsync(game.Code);
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}