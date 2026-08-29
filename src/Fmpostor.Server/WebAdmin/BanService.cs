using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class BanCheckResult
{
    public bool IsBanned { get; set; }
    public BanEntry? BanEntry { get; set; }
}

public class BanService
{
    private readonly BanDatabase _database;
    private readonly IGameManager _gameManager;
    private readonly ILogger<BanService> _logger;

    public BanService(BanDatabase database, IGameManager gameManager, ILogger<BanService> logger)
    {
        _database = database;
        _gameManager = gameManager;
        _logger = logger;
    }

    public Task AddBanAsync(BanEntry entry)
    {
        return _database.AddBanAsync(entry);
    }

    public Task RemoveBanAsync(int id)
    {
        return _database.RemoveBanAsync(id);
    }

    public Task<List<BanEntry>> GetAllBansAsync()
    {
        return _database.GetAllBansAsync();
    }

    public Task<BanEntry?> FindBanAsync(string? ipAddress, string? playerName, string? puid = null, string? fid = null, string? friendCode = null)
    {
        return _database.FindBanAsync(ipAddress, playerName, puid, fid, friendCode);
    }

    public async Task<BanCheckResult> CheckAndBanPlayerAsync(int clientId)
    {
        foreach (var game in _gameManager.Games)
        {
            var player = game.Players.FirstOrDefault(p => p.Client.Id == clientId);
            if (player != null)
            {
                var ip = player.Client.Connection?.EndPoint?.Address?.ToString();
                var puid = player.Client.Puid;
                var fid = player.Client.Items.TryGetValue("Fid", out var f) ? f?.ToString() : null;
                var friendCode = player.Client.FriendCode;

                var ban = await _database.FindBanAsync(
                    ip, player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name,
                    puid, fid, friendCode);
                if (ban != null)
                {
                    await player.Client.DisconnectAsync(DisconnectReason.Custom,
                        $"You are banned. Reason: {ban.Reason ?? "No reason provided"}");
                    _logger.LogInformation("Auto-banned player {PlayerName} (IP: {Ip}). Reason: {Reason}",
                        player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name, ip, ban.Reason);
                    return new BanCheckResult { IsBanned = true, BanEntry = ban };
                }
            }
        }

        return new BanCheckResult { IsBanned = false };
    }

    public async Task<int> KickByBanAsync(string? ipAddress, string? playerName)
    {
        var count = 0;
        foreach (var game in _gameManager.Games)
        {
            foreach (var player in game.Players)
            {
                var playerIp = player.Client.Connection?.EndPoint?.Address?.ToString();
                var playerNameMatch = !string.IsNullOrEmpty(playerName) &&
                    (player.Character?.PlayerInfo?.PlayerName?.Equals(playerName, StringComparison.OrdinalIgnoreCase) == true ||
                     player.Client.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                var ipMatch = !string.IsNullOrEmpty(ipAddress) &&
                    !string.IsNullOrEmpty(playerIp) &&
                    playerIp == ipAddress;

                if (ipMatch || playerNameMatch)
                {
                    await player.Client.DisconnectAsync(DisconnectReason.Custom,
                        "You have been banned from this server.");
                    _logger.LogInformation("Disconnected banned player {PlayerName} (IP: {Ip}).",
                        player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name, playerIp);
                    count++;
                }
            }
        }
        return count;
    }
}
