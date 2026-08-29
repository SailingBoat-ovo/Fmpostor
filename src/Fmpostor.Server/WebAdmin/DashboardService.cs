using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Fmpostor.Api.Games.Managers;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Real-time dashboard data: online player history (sampled every 60s) and
///     today's activity counters derived from the player behavior log.
/// </summary>
public class DashboardService : IDisposable
{
    private readonly ILogger<DashboardService> _logger;
    private readonly IGameManager _gameManager;
    private readonly PlayerLogService _playerLogs;
    private readonly List<(DateTime Time, int Players)> _history = new();
    private readonly object _lock = new();
    private readonly Timer _timer;
    private DateTime _day = DateTime.UtcNow.Date;

    private const int MaxHistoryPoints = 30;

    public DashboardService(ILogger<DashboardService> logger, IGameManager gameManager, PlayerLogService playerLogs)
    {
        _logger = logger;
        _gameManager = gameManager;
        _playerLogs = playerLogs;
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
    }

    private void Sample()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow.Date != _day)
            {
                _day = DateTime.UtcNow.Date;
                _history.Clear();
            }

            _history.Add((DateTime.UtcNow, _gameManager.Games.Sum(g => g.PlayerCount)));
            if (_history.Count > MaxHistoryPoints)
            {
                _history.RemoveRange(0, _history.Count - MaxHistoryPoints);
            }
        }
    }

    public object GetSnapshot()
    {
        lock (_lock)
        {
            var today = DateTime.UtcNow.Date;
            var games = _gameManager.Games.ToList();

            // Today's activity from the behavior log.
            var joins = 0;
            var chats = 0;
            var reports = 0;
            var murders = 0;
            try
            {
                var logs = _playerLogs.GetLogsAsync(limit: 2000).GetAwaiter().GetResult();
                foreach (var log in logs)
                {
                    if (log.Time < today)
                    {
                        continue;
                    }

                    switch (log.Type)
                    {
                        case "join": joins++; break;
                        case "chat": chats++; break;
                        case "report": reports++; break;
                        case "murder": murders++; break;
                    }
                }
            }
            catch
            {
                // Dashboard counters are best-effort.
            }

            return new
            {
                totalGames = games.Count,
                totalPlayers = games.Sum(g => g.PlayerCount),
                todayJoins = joins,
                todayChats = chats,
                todayReports = reports,
                todayMurders = murders,
                onlineHistory = _history.Select(h => new { time = h.Time.ToString("HH:mm"), players = h.Players }).ToList(),
                hotGames = games
                    .OrderByDescending(g => g.PlayerCount)
                    .Take(5)
                    .Select(g => new
                    {
                        code = g.Code.Code,
                        players = g.PlayerCount,
                        maxPlayers = g.Options.MaxPlayers,
                        map = g.Options.Map.ToString(),
                        host = g.Host?.Client.Name ?? "?",
                    })
                    .ToList(),
            };
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}