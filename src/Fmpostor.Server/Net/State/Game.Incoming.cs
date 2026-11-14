using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Messages;
using Impostor.Hazel;
using Fmpostor.Server.Events;
using Fmpostor.Server.Net.Reconnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.State
{
    internal partial class Game
    {
        private readonly SemaphoreSlim _clientAddLock = new SemaphoreSlim(1, 1);

        public async ValueTask HandleStartGame(IMessageReader message)
        {
            GameState = GameStates.Starting;
            ResetMeetingWindow();

            using var packet = MessageWriter.Get(MessageType.Reliable);
            message.CopyTo(packet);
            await SendToAllAsync(packet);

            await _eventManager.CallAsync(new GameStartingEvent(this));
        }

        /// <summary>
        ///     Starts the game from the server side (used by the /auto
        ///     full-room auto-start feature): broadcasts a StartGame packet
        ///     just like the host client would.
        /// </summary>
        public async ValueTask StartGameByServerAsync()
        {
            if (GameState != GameStates.NotStarted)
            {
                return;
            }

            GameState = GameStates.Starting;
            ResetMeetingWindow();

            using var packet = MessageWriter.Get(MessageType.Reliable);
            packet.StartMessage(MessageFlags.StartGame);
            packet.EndMessage();
            await SendToAllAsync(packet);

            await _eventManager.CallAsync(new GameStartingEvent(this));
        }

        public async ValueTask HandleEndGame(IMessageReader message, GameOverReason gameOverReason)
        {
            // A reserved slot is meaningless once the round ends (and the PlayerInfo
            // objects are despawned below), so release them before anything else.
            await ReleaseAllReservedSlotsAsync("round ended");

            // 本局结束: 死亡名册随之失效(下一局是全新生死态)。
            ResetDeadRoster();

            // 观战名单同样随本局结束清空。
            ResetSpectators();

            // 会议窗口与未确认的重连都只属于本局。
            ResetMeetingWindow();
            ResetUnconfirmedResumes();

            GameState = GameStates.Ended;

            // Broadcast end of the game.
            using (var packet = MessageWriter.Get(MessageType.Reliable))
            {
                message.CopyTo(packet);
                await SendToAllAsync(packet);
            }

            // Put all players in the correct limbo state.
            foreach (var player in _players)
            {
                player.Value.Limbo = LimboStates.PreSpawn;
            }

            // Delete all PlayerInfo objects
            foreach (var playerInfo in GameNet.GameData.Players.Values.ToArray())
            {
                await DespawnPlayerInfoAsync(playerInfo);
            }

            await _eventManager.CallAsync(new GameEndedEvent(this, gameOverReason));
        }

        public async ValueTask HandleAlterGame(IMessageReader message, IClientPlayer sender, bool isPublic)
        {
            IsPublic = isPublic;

            using var packet = MessageWriter.Get(MessageType.Reliable);
            message.CopyTo(packet);
            await SendToAllExceptAsync(packet, sender.Client.Id);

            await _eventManager.CallAsync(new GameAlterEvent(this, isPublic));
        }

        public async ValueTask HandleRemovePlayer(int playerId, DisconnectReason reason, bool skipReserve = false)
        {
            var deferred = await PlayerRemove(playerId, skipReserve: skipReserve);

            // When the removal was deferred for a possible reconnect, the slot is
            // still alive: do not announce the leave to the room, otherwise the
            // clients would tear down a character we are trying to preserve.
            if (deferred && _players.ContainsKey(playerId))
            {
                return;
            }

            // It's possible that the last player was removed, so check if the game is still around.
            if (GameState == GameStates.Destroyed)
            {
                return;
            }

            using var packet = MessageWriter.Get(MessageType.Reliable);
            WriteRemovePlayerMessage(packet, false, playerId, reason);
            await SendToAllExceptAsync(packet, playerId);
        }

        public async ValueTask HandleKickPlayer(int playerId, bool isBan)
        {
            _logger.LogInformation("{0} - Player {1} has left.", Code, playerId);

            using var message = MessageWriter.Get(MessageType.Reliable);

            // Send message to everyone that this player was kicked.
            WriteKickPlayerMessage(message, false, playerId, isBan);

            await SendToAllAsync(message);
            await PlayerRemove(playerId, isBan, fromKick: true);

            // Remove the player from everyone's game.
            WriteRemovePlayerMessage(
                message,
                true,
                playerId,
                isBan ? DisconnectReason.Banned : DisconnectReason.Kicked);

            await SendToAllExceptAsync(message, playerId);
        }

        public async ValueTask<GameJoinResult> AddClientAsync(ClientBase client)
        {
            var hasLock = false;

            try
            {
                hasLock = await _clientAddLock.WaitAsync(TimeSpan.FromMinutes(1));

                if (hasLock)
                {
                    return await AddClientSafeAsync(client);
                }
            }
            finally
            {
                if (hasLock)
                {
                    _clientAddLock.Release();
                }
            }

            return GameJoinResult.FromError(GameJoinError.InvalidClient);
        }

        private async ValueTask HandleJoinGameNew(ClientPlayer sender, bool isNew)
        {
            _logger.LogInformation("{0} - Player {1} ({2}) is joining.", Code, sender.Client.Name, sender.Client.Id);

            // Add player to the game.
            if (isNew)
            {
                await PlayerAdd(sender);
            }

            sender.InitializeSpawnTimeout();

            using (var message = MessageWriter.Get(MessageType.Reliable))
            {
                WriteJoinedGameMessage(message, false, sender);
                WriteAlterGameMessage(message, false, IsPublic);

                sender.Limbo = LimboStates.NotLimbo;

                await SendToAsync(message, sender.Client.Id);
                await BroadcastJoinMessage(message, true, sender);
            }
        }

        private async ValueTask<GameJoinResult> AddClientSafeAsync(ClientBase client)
        {
            // Check if the IP of the player is banned.
            if (_bannedIps.Contains(client.Connection.EndPoint.Address))
            {
                return GameJoinResult.FromError(GameJoinError.Banned);
            }

            var player = client.Player;

            // Check if the player is running the same version as the host
            if (_compatibilityConfig.AllowVersionMixing == false &&
                this.Host != null && client.GameVersion != this.Host.Client.GameVersion)
            {
                var versionCheckResult = _compatibilityManager.CanJoinGame(Host.Client.GameVersion, client.GameVersion);
                if (versionCheckResult != GameJoinError.None)
                {
                    return GameJoinResult.FromError(versionCheckResult);
                }
            }

            // 会议闸门: 必须在匹配保槽【之前】。匹配会认领保槽, 会议中被放进来的人
            // 随后会被移出房间, 保槽就白白用掉了(散会再进时职业无法恢复)。
            if (GameState == GameStates.Starting || GameState == GameStates.Started)
            {
                var meetingRefusal = CheckMeetingGate(client);
                if (meetingRefusal != null)
                {
                    return meetingRefusal.Value;
                }
            }

            // Reconnect gate. A player holding a live reserved slot is allowed
            // back into a round that already started; everybody else keeps the
            // original behaviour of being turned away.
            PendingSlot? resumeSlot = null;
            var isResume = (GameState == GameStates.Starting || GameState == GameStates.Started)
                           && TryMatchReconnectSlot(client, out resumeSlot);

            // 判定顺序(重要): 先看保槽(命中 = 重连, 有原职业要恢复); 未命中且对局
            // 已开始, 才考虑观战。顺序反了会把重连者误判成观战者。
            var joinAsSpectator = !isResume
                                  && (GameState == GameStates.Starting || GameState == GameStates.Started)
                                  && ShouldJoinAsSpectator();

            if ((GameState == GameStates.Starting || GameState == GameStates.Started) && !isResume && !joinAsSpectator)
            {
                return GameJoinResult.FromError(GameJoinError.GameStarted);
            }

            // From here on the slot (if any) is claimed. Every abort must hand it back,
            // otherwise the slot and the preserved body stay reserved forever.
            GameJoinResult AbortResume(GameJoinError error)
            {
                resumeSlot?.ReleaseClaim();
                return GameJoinResult.FromError(error);
            }

            if (GameState == GameStates.Destroyed)
            {
                return AbortResume(GameJoinError.GameDestroyed);
            }

            // Check if;
            // - The player is already in this game.
            // - The game is full.
            // 观战者不占玩家名额, 所以满员检查对它们不适用(观战上限另行判定)。
            if (!joinAsSpectator && player?.Game != this && _players.Count >= Options.MaxPlayers)
            {
                return AbortResume(GameJoinError.GameFull);
            }

            var isNew = false;

            if (player == null || player.Game != this)
            {
                var clientPlayer = new ClientPlayer(_serviceProvider.GetRequiredService<ILogger<ClientPlayer>>(), client, this, _timeoutConfig.SpawnTimeout);

                if (!_clientManager.Validate(client))
                {
                    return AbortResume(GameJoinError.InvalidClient);
                }

                isNew = true;
                player = clientPlayer;
                client.Player = clientPlayer;
            }

            // Check current player state.
            if (player.Limbo == LimboStates.NotLimbo)
            {
                return AbortResume(GameJoinError.InvalidLimbo);
            }

            if (GameState == GameStates.Ended)
            {
                // The round is over: the reserved slot cannot be used any more, so a
                // claim taken above must be released instead of leaking.
                AbortResume(GameJoinError.None);
                await HandleJoinGameNext(player, isNew);
                return GameJoinResult.CreateSuccess(player);
            }

            // A resumed player goes straight back into the running round with
            // their original slot, instead of being treated as a late joiner.
            if (isResume && resumeSlot != null)
            {
                await HandleJoinGameResume(player, resumeSlot);
                return GameJoinResult.CreateSuccess(player);
            }

            var @event = new GamePlayerJoiningEvent(this, player);
            await _eventManager.CallAsync(@event);

            if (@event.JoinResult != null && !@event.JoinResult.Value.IsSuccess)
            {
                return @event.JoinResult.Value;
            }

            await HandleJoinGameNew(player, isNew);

            // 全新玩家加入已开始的对局: 走观战路径(中立观战职业 + 固定为灵魂)。
            if (joinAsSpectator)
            {
                await AdmitAsSpectatorAsync(player);
            }

            return GameJoinResult.CreateSuccess(player);
        }

        private async ValueTask HandleJoinGameNext(ClientPlayer sender, bool isNew)
        {
            _logger.LogInformation("{0} - Player {1} ({2}) is rejoining.", Code, sender.Client.Name, sender.Client.Id);

            // Add player to the game.
            if (isNew)
            {
                await PlayerAdd(sender);
            }

            // Check if the host joined and let everyone join.
            if (sender.Client.Id == HostId)
            {
                GameState = GameStates.NotStarted;

                // Spawn the host.
                await HandleJoinGameNew(sender, false);

                // Pull players out of limbo.
                await CheckLimboPlayers();
                return;
            }

            sender.Limbo = LimboStates.WaitingForHost;

            using (var packet = MessageWriter.Get(MessageType.Reliable))
            {
                WriteWaitForHostMessage(packet, false, sender);

                await SendToAsync(packet, sender.Client.Id);
                await BroadcastJoinMessage(packet, true, sender);
            }
        }
    }
}
