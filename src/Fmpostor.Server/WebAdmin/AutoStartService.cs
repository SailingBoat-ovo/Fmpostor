using System;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Auto-start when the room is full (ported from fanchuan.autostart.plugin):
///     /auto on|off|starttime|coldtime|help commands, full-room countdown and
///     post-game cooldown. State is stored per game in IGame.Items.
/// </summary>
public class AutoStartService : IEventListener
{
    private readonly ILogger<AutoStartService> _logger;
    private readonly IEventManager _eventManager;

    private const string KeyEnabled = "autostart_enabled";
    private const string KeyStartTime = "autostart_starttime";
    private const string KeyColdTime = "autostart_coldtime";
    private const string KeyCooldownUntil = "autostart_cooldown_until";
    private const string KeyLocked = "autostart_locked";

    private const int DefaultStartTime = 5;
    private const int DefaultColdTime = 30;

    public AutoStartService(ILogger<AutoStartService> logger, IEventManager eventManager)
    {
        _logger = logger;
        _eventManager = eventManager;
    }

    // ========== Commands ==========

    public async ValueTask HandleChat(IPlayerChatEvent e, string message)
    {
        if (!e.ClientPlayer.IsHost)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("只有房主才能使用此命令。", e.ClientPlayer.Character);
            }

            e.IsCancelled = true;
            return;
        }

        e.IsCancelled = true;
        var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var game = e.ClientPlayer.Game;

        if (parts.Length < 2)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("用法：/auto on 或 /auto off\n使用 /auto help 查看帮助", e.ClientPlayer.Character);
            }

            return;
        }

        switch (parts[1].ToLowerInvariant())
        {
            case "on":
                game.Items[KeyEnabled] = true;
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync("自动开局已开启，满人后自动开始游戏。", e.ClientPlayer.Character);
                }

                _logger.LogInformation("[AutoStart] {Name} enabled auto-start in {Code}.", e.ClientPlayer.Client.Name, game.Code);
                CheckAndAutoStart(game);
                break;

            case "off":
                game.Items[KeyEnabled] = false;
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync("自动开局已关闭。", e.ClientPlayer.Character);
                }

                break;

            case "starttime":
                await HandleStartTimeCommand(e, parts, game);
                break;

            case "coldtime":
                await HandleColdTimeCommand(e, parts, game);
                break;

            case "help":
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync(
                        "=== 自动开局帮助 ===\n/auto on - 开启自动开局\n/auto off - 关闭自动开局\n/auto starttime <秒数> - 人满后倒计时（默认5秒，1-60）\n/auto coldtime <秒数> - 结束后冷却（默认30秒，0-300，0=关闭）\n/auto help - 帮助",
                        e.ClientPlayer.Character);
                }

                break;

            default:
                if (e.ClientPlayer.Character != null)
                {
                    await e.ClientPlayer.Character.SendChatToPlayerAsync("未知命令。使用 /auto help 查看帮助", e.ClientPlayer.Character);
                }

                break;
        }
    }

    private async ValueTask HandleStartTimeCommand(IPlayerChatEvent e, string[] parts, IGame game)
    {
        if (parts.Length < 3 || !int.TryParse(parts[2], out var seconds) || seconds < 1 || seconds > 60)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("用法：/auto starttime <1~60秒>\n例如：/auto starttime 10", e.ClientPlayer.Character);
            }

            return;
        }

        game.Items[KeyStartTime] = seconds;
        if (e.ClientPlayer.Character != null)
        {
            await e.ClientPlayer.Character.SendChatToPlayerAsync($"人满倒计时已设置为 {seconds} 秒。", e.ClientPlayer.Character);
        }
    }

    private async ValueTask HandleColdTimeCommand(IPlayerChatEvent e, string[] parts, IGame game)
    {
        if (parts.Length < 3 || !int.TryParse(parts[2], out var seconds) || seconds < 0 || seconds > 300)
        {
            if (e.ClientPlayer.Character != null)
            {
                await e.ClientPlayer.Character.SendChatToPlayerAsync("用法：/auto coldtime <0~300秒>\n例如：/auto coldtime 15\n设为0则关闭冷却功能。", e.ClientPlayer.Character);
            }

            return;
        }

        game.Items[KeyColdTime] = seconds;
        if (e.ClientPlayer.Character != null)
        {
            await e.ClientPlayer.Character.SendChatToPlayerAsync(seconds == 0
                ? "冷却时间已关闭。"
                : $"冷却时间已设置为 {seconds} 秒。", e.ClientPlayer.Character);
        }
    }

    // ========== Events ==========

    [EventListener]
    public ValueTask OnPlayerJoined(IGamePlayerJoinedEvent e)
    {
        CheckAndAutoStart(e.Game);
        return default;
    }

    [EventListener]
    public async ValueTask OnGameEnded(IGameEndedEvent e)
    {
        var game = e.Game;
        game.Items[KeyLocked] = true;
        _logger.LogInformation("[AutoStart] Game ended in {Code}, cooldown locked.", game.Code);

        await Task.Delay(2000);
        var host = game.Host;
        if (host != null && host.Character != null)
        {
            StartCooldown(game);
        }
    }

    [EventListener]
    public ValueTask OnPlayerSpawned(IPlayerSpawnedEvent e)
    {
        if (e.ClientPlayer?.Character == null || e.ClientPlayer.Client == null)
        {
            return default;
        }

        var game = e.Game;
        if (game.Items.TryGetValue(KeyLocked, out var locked) && locked is true)
        {
            if (e.ClientPlayer.IsHost)
            {
                StartCooldown(game);
            }
        }

        return default;
    }

    private void StartCooldown(IGame game)
    {
        game.Items.Remove(KeyLocked);

        var coldTime = DefaultColdTime;
        if (game.Items.TryGetValue(KeyColdTime, out var ctObj) && ctObj is int ct)
        {
            coldTime = ct;
        }

        if (coldTime <= 0)
        {
            return;
        }

        game.Items[KeyCooldownUntil] = DateTime.UtcNow.AddSeconds(coldTime);
        _logger.LogInformation("[AutoStart] Cooldown started ({Seconds}s) in {Code}.", coldTime, game.Code);
    }

    private void CheckAndAutoStart(IGame game)
    {
        if (!game.Items.TryGetValue(KeyEnabled, out var toggle) || toggle is not bool enabled || !enabled)
        {
            return;
        }

        if (game.GameState != GameStates.NotStarted)
        {
            return;
        }

        if (game.PlayerCount < game.Options.MaxPlayers)
        {
            return;
        }

        if (IsLocked(game))
        {
            _logger.LogDebug("[AutoStart] Cooldown active, auto-start blocked in {Code}.", game.Code);
            return;
        }

        var startTime = DefaultStartTime;
        if (game.Items.TryGetValue(KeyStartTime, out var stObj) && stObj is int st)
        {
            startTime = st;
        }

        if (startTime <= 0)
        {
            startTime = 1;
        }

        _logger.LogInformation("[AutoStart] Room {Code} is full ({Count}/{Max}), starting in {Seconds}s.",
            game.Code, game.PlayerCount, game.Options.MaxPlayers, startTime);

        game.Items[KeyEnabled] = false;

        var seconds = startTime;
        _ = Task.Run(async () =>
        {
            var host = game.Host;
            for (var i = seconds; i >= 1; i--)
            {
                if (host?.Character != null)
                {
                    await host.Character.SendChatAsync($"此房间启动了人满自动开始游戏\n将在{i}秒后开始游戏");
                }

                if (i > 1)
                {
                    await Task.Delay(1000);
                }
            }

            try
            {
                if (game is Fmpostor.Server.Net.State.Game g)
                {
                    await g.StartGameByServerAsync();
                }
                else
                {
                    _logger.LogWarning("[AutoStart] Game is not the server Game type, cannot auto-start in {Code}.", game.Code);
                }

                _logger.LogInformation("[AutoStart] Auto-start executed in {Code}.", game.Code);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AutoStart] Auto-start failed in {Code}.", game.Code);
            }
        });
    }

    private bool IsLocked(IGame game)
    {
        if (game.Items.TryGetValue(KeyLocked, out var locked) && locked is true)
        {
            return true;
        }

        if (game.Items.TryGetValue(KeyCooldownUntil, out var untilObj) && untilObj is DateTime until)
        {
            if (DateTime.UtcNow < until)
            {
                return true;
            }

            game.Items.Remove(KeyCooldownUntil);
        }

        return false;
    }
}