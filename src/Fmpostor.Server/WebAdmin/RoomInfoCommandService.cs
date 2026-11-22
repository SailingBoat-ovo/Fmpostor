using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     In-game room information commands: /players (player list) and /maxplayers (room size).
/// </summary>
/// <remarks>
///     · /players(别名 /list、#players、#list): 任何人可用, 私聊返回本房间玩家列表 ——
///       房间码、房主、人数/上限, 以及每人的 [连接Id][玩家Id] 名字 : 好友代码 | 延迟。
///       列表较长时按约 500 字分段发送, 避免单条聊天过长被截断。
///     · /maxplayers N(别名 /maxplayer、/max、#maxplayers、#maxplayer):
///       只有房主、只在大厅(未开局)可用; N 为 5~127, 且不能少于当前人数。
///       改完后把房间设置同步给全房间(与房主在游戏里改设置效果一致)。
/// </remarks>
public sealed class RoomInfoCommandService
{
    private const int MinMaxPlayers = 5;
    private const int MaxMaxPlayers = 127;
    private const int ChunkLength = 500;
    private const string Divider = "■■■■■■■■";

    private readonly GameTrackerService _tracker;
    private readonly ILogger<RoomInfoCommandService> _logger;

    public RoomInfoCommandService(GameTrackerService tracker, ILogger<RoomInfoCommandService> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    /// <summary>
    ///     Handles the message if it is one of these commands. Returns true when it was handled.
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(IPlayerChatEvent e, string trimmed)
    {
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length < 2 || (parts[0][0] != '/' && parts[0][0] != '#'))
        {
            return false;
        }

        var command = parts[0][1..].ToLowerInvariant();
        switch (command)
        {
            case "players":
            case "list":
                e.IsCancelled = true;
                await SendPlayerListAsync(e);
                return true;

            case "maxplayers":
            case "maxplayer":
            case "max":
                e.IsCancelled = true;
                await SetMaxPlayersAsync(e, parts.Length > 1 ? parts[1] : null);
                return true;

            default:
                return false;
        }
    }

    private async ValueTask SendPlayerListAsync(IPlayerChatEvent e)
    {
        var me = e.ClientPlayer.Character;
        if (me == null)
        {
            return;
        }

        var game = e.Game;
        var host = game.Host;
        var sb = new StringBuilder();
        sb.Append(Divider).Append('\n');
        sb.Append($"房间: {game.Code.Code} | 房主: {DisplayName(host)} | 玩家: {game.PlayerCount}/{game.Options.MaxPlayers}\n");

        // 房主排第一, 其余按连接顺序。
        var players = game.Players
            .OrderByDescending(p => p == host)
            .ThenBy(p => p.Client.Id)
            .ToList();

        foreach (var p in players)
        {
            var line = FormatLine(p, p == host);
            if (sb.Length + line.Length > ChunkLength)
            {
                await me.SendChatToPlayerAsync(sb.ToString().TrimEnd('\n'), me);
                sb.Clear();
            }

            sb.Append(line);
        }

        sb.Append(Divider);
        await me.SendChatToPlayerAsync(sb.ToString(), me);
    }

    private string FormatLine(IClientPlayer p, bool isHost)
    {
        var playerId = p.Character?.PlayerId is { } id ? id.ToString() : "-";
        var friendCode = FriendCodeFilter.ForClients(p.Client.FriendCode);
        if (string.IsNullOrEmpty(friendCode))
        {
            friendCode = "无";
        }

        var ping = _tracker.ConnectionPing(p);
        var pingText = ping > 0 ? $"{ping}ms" : "-";
        var crown = isHost ? "👑" : string.Empty;
        return $"[{p.Client.Id}][{playerId}] {crown}{DisplayName(p)} : {friendCode} | {pingText}\n";
    }

    private static string DisplayName(IClientPlayer? p)
    {
        if (p == null)
        {
            return "-";
        }

        var name = p.Character?.PlayerInfo?.PlayerName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = p.Client.Name;
        }

        // 名字原样进聊天框, 去掉尖括号防止夹带富文本。
        return (name ?? "-").Replace('<', '‹').Replace('>', '›');
    }

    private async ValueTask SetMaxPlayersAsync(IPlayerChatEvent e, string? argument)
    {
        var me = e.ClientPlayer.Character;
        if (me == null)
        {
            return;
        }

        var game = e.Game;

        if (!int.TryParse(argument, out var value) || value < MinMaxPlayers || value > MaxMaxPlayers)
        {
            await me.SendChatToPlayerAsync($"用法：/maxplayers <人数>（{MinMaxPlayers}~{MaxMaxPlayers}）", me);
            return;
        }

        if (!e.ClientPlayer.IsHost)
        {
            await me.SendChatToPlayerAsync("只有房主才能修改最大人数。", me);
            return;
        }

        if (game.GameState != GameStates.NotStarted)
        {
            await me.SendChatToPlayerAsync("只能在大厅（未开局）时修改最大人数。", me);
            return;
        }

        if (game.PlayerCount > value)
        {
            await me.SendChatToPlayerAsync($"当前已有 {game.PlayerCount} 人，最大人数不能小于当前人数。", me);
            return;
        }

        var previous = game.Options.MaxPlayers;
        try
        {
            game.Options.MaxPlayers = (byte)value;
            await game.SyncSettingsAsync();
            await me.SendChatToPlayerAsync($"最大人数已设置为 {value}。", me);
            _logger.LogInformation("[RoomInfo] Host {Name} set max players of {Code} to {Max}.", e.ClientPlayer.Client.Name, game.Code.Code, value);
        }
        catch (Exception ex)
        {
            game.Options.MaxPlayers = previous;
            _logger.LogWarning(ex, "[RoomInfo] Failed to set max players of {Code} to {Max}.", game.Code.Code, value);
            await me.SendChatToPlayerAsync("修改失败：房间设置还没准备好，请稍后再试。", me);
        }
    }
}
