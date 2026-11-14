using System.Linq;
using System.Threading.Tasks;
using Fmpostor.Api;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;
using Impostor.Hazel;
using Fmpostor.Server.Events;
using Fmpostor.Server.Net.Hazel;
using Fmpostor.Server.Net.Reconnect;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.State
{
    internal partial class Game
    {
        private async ValueTask PlayerAdd(ClientPlayer player)
        {
            // Store player.
            if (!_players.TryAdd(player.Client.Id, player))
            {
                throw new FmpostorException("Failed to add player to game.");
            }

            // Assign hostId if none is set.
            if (HostId == -1)
            {
                HostId = player.Client.Id;
            }

            await _eventManager.CallAsync(new GamePlayerJoinedEvent(this, player));
        }

        private async ValueTask<bool> PlayerRemove(int playerId, bool isBan = false, bool fromKick = false, bool skipReserve = false)
        {
            if (!_players.TryGetValue(playerId, out var pendingPlayer))
            {
                return false;
            }

            // Grace period: keep the slot (and therefore the character and the
            // PlayerInfo) alive so the player can come back into their own body.
            // The slot is released either by a successful resume or by
            // ReleaseReconnectSlot when the grace period expires.
            //
            // skipReserve is used by the grace-expiry path itself: it must tear
            // the slot down for real instead of reserving it a second time.
            //
            // ★ fromKick 的排除可以被开发开关放开: 用踢出制造掉线来测试重连时,
            //   需要被踢的人也能拿到保槽(否则 TryDeferReconnectSlot 根本不会被调用,
            //   重连必然失败 —— 表现为"游戏已经开始")。
            //   ⚠️ 默认 false: 生产环境里被踢的人不该保留位置。
            //   封禁(isBan)永远不放开 —— 那是"你不许回来"。
            var kickBlocksReserve = fromKick && !_reconnectConfig.DevAllowKickReserve;

            // 一次尚未确认的重连在这里断开: 把它用掉的原保槽还回去(含模组状态),
            // 而不是再按这具新身体另建一个保槽 —— 新身体上还没有被恢复的状态。
            if (skipReserve)
            {
                _unconfirmedResumes.TryRemove(playerId, out _);
                _resumeConfirmScheduled.TryRemove(playerId, out _);
            }
            else if (TryHandBackUnconfirmedResume(playerId, isBan, kickBlocksReserve))
            {
                skipReserve = true;
            }

            if (!skipReserve && !isBan && !kickBlocksReserve && TryDeferReconnectSlot(pendingPlayer, playerId))
            {
                return true;
            }

            if (!_players.TryRemove(playerId, out var player))
            {
                return false;
            }

            _logger.LogInformation("{0} - Player {1} ({2}) has left.", Code, player.Client.Name, playerId);

            if (GameState == GameStates.Starting || GameState == GameStates.Started || GameState == GameStates.NotStarted)
            {
                if (player.Character?.PlayerInfo != null)
                {
                    player.Character.PlayerInfo.Disconnected = true;

                    if (GameState == GameStates.NotStarted)
                    {
                        player.Character.PlayerInfo.LastDeathReason = DeathReason.Disconnect;
                    }
                }
            }

            player.Client.Player = null;

            // Host migration.
            if (HostId == playerId)
            {
                // 房主走了, 他的能力声明随之失效: 新房主必须重新声明, 否则换人之后
                // 仍沿用旧房主的承诺, 可能把新玩家放进一个其实不支持观战的房间。
                ForgetRoomCapability("host left");

                await MigrateHost();
                await _eventManager.CallAsync(new GameHostChangedEvent(this, player, Host));
            }

            // Game is empty, remove it.
            if (_players.IsEmpty || Host == null)
            {
                GameState = GameStates.Destroyed;

                // Remove instance reference.
                await _gameManager.RemoveAsync(Code);
                return true;
            }

            if (isBan)
            {
                BanIp(player.Client.Connection.EndPoint.Address);
            }

            await _eventManager.CallAsync(new GamePlayerLeftEvent(this, player, isBan));

            // Player can refuse to be kicked and keep the connection open, check for this.
            _ = Task.Run(async () =>
            {
                await Task.Delay(_timeoutConfig.ConnectionTimeout);

                if (player.Client.Connection.IsConnected && player.Client.Connection is HazelConnection hazel)
                {
                    _logger.LogInformation("{0} - Player {1} ({2}) kept connection open after leaving, disposing.", Code, player.Client.Name, playerId);
                    await player.Client.DisconnectAsync(isBan ? DisconnectReason.Banned : DisconnectReason.Kicked);
                }
            });

            // Clean up the PlayerInfo if we own it and we're still in the lobby
            if (GameState == GameStates.NotStarted)
            {
                if (GameNet.GameData.PlayersByClientId.TryGetValue(playerId, out var playerInfo))
                {
                    await DespawnPlayerInfoAsync(playerInfo);
                }
            }

            return true;
        }

        private async ValueTask MigrateHost()
        {
            // Only a player that is actually here may host. With the reconnect grace
            // period a disconnected player deliberately stays in _players until the
            // slot expires, and handing HostId to that entry would point the room at a
            // dead connection: starting rounds, kicks and every host-only action would
            // then fail for everyone.
            static bool CanHost(ClientPlayer p)
            {
                return !p.IsAwaitingReconnect &&
                       p.Client.Connection != null &&
                       p.Client.Connection.IsConnected;
            }

            // Prefer an explicitly requested target (set by the /nexthost
            // host-transfer feature), otherwise pick the first present player.
            ClientPlayer? host = null;
            if (PreferredHostId.HasValue)
            {
                if (_players.TryGetValue(PreferredHostId.Value, out var preferred) && CanHost(preferred))
                {
                    host = preferred;
                }
                else
                {
                    _logger.LogWarning(
                        "{0} - Requested next host {1} is not available (disconnected or reserved); picking another player.",
                        Code,
                        PreferredHostId.Value);
                }

                PreferredHostId = null;
            }

            host ??= _players.Select(p => p.Value).FirstOrDefault(CanHost);

            if (host == null)
            {
                return;
            }

            foreach (var player in _players.Values)
            {
                player.Character?.RequestedPlayerName.Clear();
                player.Character?.RequestedColorId.Clear();
            }

            HostId = host.Client.Id;
            _logger.LogInformation("{0} - Assigned {1} ({2}) as new host.", Code, host.Client.Name, host.Client.Id);

            // Check our current game state.
            if (GameState == GameStates.Ended && host.Limbo == LimboStates.WaitingForHost)
            {
                GameState = GameStates.NotStarted;

                // Spawn the host.
                await HandleJoinGameNew(host, false);

                // Pull players out of limbo.
                await CheckLimboPlayers();
            }
        }

        private async ValueTask CheckLimboPlayers()
        {
            using var message = MessageWriter.Get(MessageType.Reliable);

            foreach (var (_, player) in _players.Where(x => x.Value.Limbo == LimboStates.WaitingForHost))
            {
                WriteJoinedGameMessage(message, true, player);
                WriteAlterGameMessage(message, false, IsPublic);

                player.Limbo = LimboStates.NotLimbo;

                await SendToAsync(message, player.Client.Id);
            }
        }
    }
}
