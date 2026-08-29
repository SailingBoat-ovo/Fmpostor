using System;
using System.Linq;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Host transfer (ported from Fanchuan.RoomOwnerConversion.Plugin):
///     /nexthost lists players, host enters the target ID and is kicked after
///     3 seconds; the server migrates the host to the chosen target via
///     Game.PreferredHostId (the game itself picks that player in MigrateHost).
/// </summary>
public class RoomTransferService
{
    private readonly ILogger<RoomTransferService> _logger;

    private const string PendingKey = "nexthost_pending_host_id";
    private const string TargetKey = "nexthost_target_id";

    public RoomTransferService(ILogger<RoomTransferService> logger)
    {
        _logger = logger;
    }

    public async ValueTask HandleChat(IPlayerChatEvent e, string message)
    {
        var game = e.ClientPlayer.Game;
        var hostId = game.HostId;

        if (message.Equals("/nexthost", StringComparison.OrdinalIgnoreCase))
        {
            await HandleNextHostCommand(e, game, hostId);
            return;
        }

        await HandleIdInput(e, game, hostId);
    }

    private async ValueTask HandleNextHostCommand(IPlayerChatEvent e, IGame game, int hostId)
    {
        var clientId = e.ClientPlayer.Client.Id;

        if (clientId != hostId)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("只有房主才能使用此命令。", e.ClientPlayer.Character);
            }

            e.IsCancelled = true;
            return;
        }

        e.IsCancelled = true;

        var otherPlayers = game.Players.Where(p => p.Client.Id != hostId).ToList();
        if (otherPlayers.Count == 0)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("房间内没有其他玩家可以转让房主。", e.ClientPlayer.Character);
            }

            return;
        }

        var playerList = "当前房间玩家列表：\n" + string.Join("\n", otherPlayers.Select(p => $"{p.Client.Id} - {p.Client.Name}"))
            + "\n\n请输入要转让的玩家ID：";

        if (e.ClientPlayer.Character != null)
        {
            await e.ClientPlayer.Character.SendChatToPlayerAsync(playerList, e.ClientPlayer.Character);
        }

        game.Items[PendingKey] = clientId;
        _logger.LogInformation("[Transfer] Host {Name}({Id}) requested transfer in {Code}.",
            e.ClientPlayer.Client.Name, clientId, game.Code);
    }

    private async ValueTask HandleIdInput(IPlayerChatEvent e, IGame game, int hostId)
    {
        var clientId = e.ClientPlayer.Client.Id;

        if (!game.Items.TryGetValue(PendingKey, out var pendingObj) ||
            pendingObj is not int pendingHostId ||
            pendingHostId != clientId)
        {
            return;
        }

        if (!int.TryParse(e.Message.Trim(), out var targetId))
        {
            return;
        }

        e.IsCancelled = true;

        var targetPlayer = game.Players.FirstOrDefault(p => p.Client.Id == targetId);
        if (targetPlayer == null)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("未找到该ID的玩家，请重新输入/nexthost查看列表。", e.ClientPlayer.Character);
            }

            game.Items.Remove(PendingKey);
            return;
        }

        if (targetId == hostId)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("不能转让给自己。", e.ClientPlayer.Character);
            }

            game.Items.Remove(PendingKey);
            return;
        }

        game.Items[TargetKey] = targetId;
        game.Items.Remove(PendingKey);

        if (e.ClientPlayer.Character != null)
        {
            await e.ClientPlayer.Character.SendChatToPlayerAsync("你将在3秒后被踢出转让房主给 " + targetPlayer.Client.Name, e.ClientPlayer.Character);
        }

        _logger.LogInformation("[Transfer] Host {HostName}({HostId}) transferring to {TargetName}({TargetId}) in {Code}.",
            e.ClientPlayer.Client.Name, clientId, targetPlayer.Client.Name, targetId, game.Code);

        var character = e.ClientPlayer.Character;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            try
            {
                // Set the preferred host so MigrateHost picks the target.
                if (game is Fmpostor.Server.Net.State.Game g && targetPlayer.Client.Player != null)
                {
                    g.PreferredHostId = targetId;
                }

                if (character != null)
                {
                    await e.ClientPlayer.KickAsync();
                    _logger.LogInformation("[Transfer] Host kicked, host will migrate to {TargetId}.", targetId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Transfer] Failed to kick host.");
            }
        });
    }
}