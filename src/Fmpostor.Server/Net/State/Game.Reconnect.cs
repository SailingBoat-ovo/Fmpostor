using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net.Messages;
using Microsoft.Extensions.DependencyInjection;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner;
using Fmpostor.Server.Net.Custom;
using Fmpostor.Server.Net.Inner;
using Fmpostor.Server.Net.Inner.Objects;
using Impostor.Hazel;
using Fmpostor.Server.Net.Reconnect;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.State
{
    /// <summary>
    ///     Disconnect-reconnect support.
    ///
    ///     Design notes:
    ///     * The server is the only decision maker. Clients never decide whether
    ///       a reconnect is allowed.
    ///     * Vanilla state (role slot, tasks, dead/alive, position) is already
    ///       owned by the server, so it can always be restored.
    ///     * Modded state (NoS roles) is treated as an opaque blob: it is
    ///       captured from a connected client and handed back verbatim, so the
    ///       server never has to understand it and never version-couples to the
    ///       mod.
    ///
    ///     The mechanism used here is a *deferred removal*: instead of tearing
    ///     the player down on disconnect, the slot is kept alive for a grace
    ///     period. That keeps three things intact for free, all of which would
    ///     otherwise have to be reconstructed:
    ///       - the <c>ClientPlayer</c> entry in <c>_players</c>,
    ///       - the server side <c>InnerPlayerControl</c> (and its components),
    ///       - the <c>InnerPlayerInfo</c> holding role, tasks and dead/alive.
    /// </summary>
    internal partial class Game
    {
        /// <summary>
        ///     True when this game currently honours the reconnect grace period:
        ///     the feature is on and a round is in progress.
        /// </summary>
        public bool IsReconnectArmed =>
            Reconnect.Enabled &&
            (GameState == GameStates.Starting || GameState == GameStates.Started);

        /// <summary>
        ///     Number of dropped players whose slot is still reserved.
        /// </summary>
        public int ReconnectReservedSlots => Reconnect.SlotCount;

        /// <summary>
        ///     Decides whether the leaving player may keep their slot, and starts
        ///     the grace timer. Returns true when the removal has been deferred.
        /// </summary>
        private bool TryDeferReconnectSlot(ClientPlayer player, int playerId)
        {
            try
            {
                if (!Reconnect.Enabled)
                {
                    return false;
                }

                // 只服务装了断线重连插件的房间: 房主从没声明过(原版 / 其他模组)时不保槽,
                //   掉线就是普通离开, 身体不会被留在房间里。这类房间里续局也无法完成
                //   (需要房主的插件补生成身体), 保槽只会让其他人看到一个"卡住的玩家"。
                if (!RoomUsesResume)
                {
                    return false;
                }

                // 房主在设置里关掉了断线重连: 不保槽, 掉线即离开。
                if (HostDisabledReconnect)
                {
                    _logger.LogInformation(
                        "Reconnect: host disabled reconnect for this room; {Name} (clientId {ClientId}) leaves normally.",
                        player.Client.Name,
                        playerId);
                    return false;
                }

                // Only meaningful while an actual round is in progress.
                if (GameState != GameStates.Starting && GameState != GameStates.Started)
                {
                    return false;
                }

                // The host cannot normally be resumed: a resume always arrives on
                // a new ClientId while HostId is keyed by ClientId, so host
                // migration has already happened and the returning host could only
                // come back as a normal player.
                //
                // DevAllowHostReserve exists for two-client testing on one machine,
                // where the tester is usually the host - without it no slot is ever
                // created and the server answers "the game has already started".
                if (HostId == playerId && !_reconnectConfig.DevAllowHostReserve)
                {
                    return false;
                }

                var control = player.Character;
                if (control == null)
                {
                    return false;
                }

                var playerInfo = GameNet.GameData.GetPlayerById(control.PlayerId);
                if (playerInfo == null)
                {
                    return false;
                }

                // A player that was already dead stays dead; nothing to protect.
                if (playerInfo.IsDead)
                {
                    return false;
                }

                var identityKey = ReconnectRegistry.BuildIdentityKey(
                    player.Client.Puid,
                    player.Client.FriendCode,
                    player.Client.Name);

                if (!Reconnect.TryReserve(
                        identityKey,
                        playerInfo.PlayerId,
                        playerId,
                        player.Client.Puid ?? string.Empty,
                        player.Client.FriendCode ?? string.Empty,
                        player.Client.Connection.EndPoint.Address.ToString(),
                        player.Client.Name,
                        isHost: false,
                        disconnectReason: "NetworkError",
                        DateTimeOffset.UtcNow,
                        out var slot) ||
                    slot == null)
                {
                    return false;
                }

                // Announce the reservation: this line is the quickest way to confirm
                // from the console that a slot really was created (and, with the dev
                // flag on, that it was the host's).
                _logger.LogInformation(
                    "Reconnect: reserved slot for {Name} (playerId {PlayerId}, clientId {ClientId}, host={IsHost}).",
                    player.Client.Name,
                    playerInfo.PlayerId,
                    playerId,
                    HostId == playerId);

                // Remember which body to hand back on resume, and publish the
                // countdown so the web panel can show it.
                slot.Snapshot.CharacterNetId = control.NetId;
                player.ReconnectExpiresAt = slot.Snapshot.ExpiresAt;

                _reconnectLogs.Record(
                    "reserved",
                    Code.Code,
                    player.Client.Name,
                    player.Client.FriendCode ?? string.Empty,
                    player.Client.Puid ?? string.Empty,
                    player.Client.Connection.EndPoint.Address.ToString(),
                    playerInfo.PlayerId,
                    playerId,
                    detail: $"grace={_reconnectConfig.GracePeriodSeconds}s");

                // Note: Client.Player is deliberately left attached. The whole
                // slot is kept alive for the grace period, and the deferred
                // teardown path reuses the existing connection-invalidation
                // logic. If the player never returns, the timer below releases
                // everything.
                player.Disconnected = true;

                ScheduleGraceExpiry(identityKey, playerId, player.Client.Name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect: failed to defer removal of client {ClientId}.", playerId);
                return false;
            }
        }

        private void ScheduleGraceExpiry(string identityKey, int clientId, string playerName)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(1, _reconnectConfig.GracePeriodSeconds));

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay);

                    // A successful resume consumes the slot; if it is still
                    // pending when the timer fires, the player never came back.
                    if (!Reconnect.TryGetPendingByIdentity(identityKey, out var slot))
                    {
                        return;
                    }

                    _logger.LogInformation(
                        "Reconnect: grace period over for {Name} (playerId {PlayerId}); releasing the slot.",
                        playerName,
                        slot.Snapshot.PlayerId);

                    Reconnect.ForgetLiveModState(slot.Snapshot.PlayerId);
                    await ReleaseReconnectSlotAsync(clientId, slot.Snapshot.PlayerId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reconnect: grace expiry failed for clientId {ClientId}.", clientId);
                }
            });
        }

        /// <summary>
        ///     Tears down a slot whose grace period expired, running the normal
        ///     leave path (host migration, events, PlayerInfo cleanup).
        /// </summary>
        private async ValueTask ReleaseReconnectSlotAsync(int playerId, byte gamePlayerId)
        {
            Reconnect.DropSlot(gamePlayerId);

            if (!_players.TryGetValue(playerId, out var player))
            {
                return;
            }

            // Re-run the normal removal, this time without a grace period.
            player.Disconnected = false;

            if (player.Client.Player == null)
            {
                // Detached during the grace period; re-attach so the normal path
                // can run and then clear it again.
                player.Client.Player = player;
            }

            // 移除路径会把 player.Character 置空, 先抓住引用。
            var staleCharacter = player.Character;

            _reconnectLogs.Record(
                "expired",
                Code.Code,
                player.Client.Name,
                player.Client.FriendCode ?? string.Empty,
                player.Client.Puid ?? string.Empty,
                player.Client.Connection.EndPoint.Address.ToString(),
                gamePlayerId,
                playerId,
                detail: "grace period elapsed");

            await HandleRemovePlayer(playerId, DisconnectReason.ExitGame, skipReserve: true);

            // 保槽过期也是"旧身份彻底结束": 期间从未广播过 Remove, 所以这具身体还留在
            // 所有客户端的世界里。必须由服务端主动销毁, 否则会一直挂着当残影。
            await DespawnCharacterAsync(staleCharacter);
        }

        /// <summary>
        ///     Stores a mod-state blob reported by a connected client.
        /// </summary>
        /// <remarks>
        ///     Called from the mod RPC receiver. Two things are deliberately NOT
        ///     done here:
        ///       · the blob is never interpreted - the server is only a container,
        ///         which is what keeps it independent of the mod version;
        ///       · the reported identity is never trusted - the caller id comes
        ///         from the connection, and the in-game slot is checked against
        ///         that connection, so a client cannot report state on behalf of
        ///         somebody else.
        /// </remarks>
        internal void StoreReportedModState(int reporterClientId, byte gamePlayerId, byte[] blob)
        {
            try
            {
                if (!Reconnect.Enabled)
                {
                    return;
                }

                // Ownership check. The mod broadcasts its state to every client, so
                // every client holds a full view and could technically report for
                // anybody; two cases are legitimate:
                //   1. a client reporting its OWN slot (the normal, self-report path), and
                //   2. another client reporting the slot of a player who is currently
                //      disconnected / awaiting reconnect - that is the "report on behalf
                //      of the absent player" path the design relies on.
                // Anything else (a connected player's slot reported by somebody else) is
                // dropped. This stays cheap because the client reports every player on a
                // heartbeat: a rejection here is normal traffic, never a warning.
                var ownerClientId = GameNet.GameData.GetPlayerById(gamePlayerId)?.ClientId;
                if (ownerClientId != reporterClientId)
                {
                    if (ownerClientId.HasValue && IsClientAwaitingReconnect(ownerClientId.Value))
                    {
                        _logger.LogDebug(
                            "Reconnect: client {Reporter} reports mod state on behalf of the disconnected playerId {PlayerId} (client {Owner}).",
                            reporterClientId,
                            gamePlayerId,
                            ownerClientId);
                    }
                    else
                    {
                        _logger.LogTrace(
                            "Reconnect: mod state for playerId {PlayerId} from client {Reporter} ignored (owned by connected client {Owner}).",
                            gamePlayerId,
                            reporterClientId,
                            ownerClientId);
                        return;
                    }
                }

                if (Reconnect.TryStoreLiveModState(
                        gamePlayerId,
                        blob,
                        seq: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        DateTimeOffset.UtcNow,
                        out var reason))
                {
                    _logger.LogDebug(
                        "Reconnect: stored mod state for playerId {PlayerId} ({Bytes} bytes).",
                        gamePlayerId,
                        blob.Length);
                }
                else if (reason != "rate")
                {
                    _logger.LogDebug(
                        "Reconnect: mod state for playerId {PlayerId} not stored ({Reason}).",
                        gamePlayerId,
                        reason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect: failed to store reported mod state.");
            }
        }

        /// <summary>
        ///     True when the given client is a slot that is being held open for a
        ///     reconnect (i.e. its player is currently absent).
        /// </summary>
        private bool IsClientAwaitingReconnect(int clientId)
        {
            return _players.TryGetValue(clientId, out var player) && player.IsAwaitingReconnect;
        }

        /// <summary>
        ///     Remembers the mod's process hash and target netId as observed in a real
        ///     report, so a restore frame can be addressed back to the clients.
        /// </summary>
        internal void RememberModWireInfo(int processHash, uint targetNetId, int reporterClientId)
        {
            Reconnect.RememberWireInfo(processHash, targetNetId, reporterClientId, DateTimeOffset.UtcNow);
        }

        /// <summary>
        ///     Decides whether an incoming join is a reconnect, returning the
        ///     reserved slot when it is.
        /// </summary>
        private bool TryMatchReconnectSlot(ClientBase client, [MaybeNullWhen(false)] out PendingSlot slot)
        {
            slot = null;

            if (!Reconnect.Enabled)
            {
                return false;
            }

            // ⚠️ 必须确认这【真的是一条重连连接】, 而不是一个恰好同名的正常加入。
            //
            // 实测踩到的现场(服务端 connectlog):
            //   03:14:39  Drayliable disconnect SQILRR     ← 保槽建立
            //   03:15:09  帆船       connect    SQILRR     ← 这条 join 抢走了上面那个槽
            //   03:15:11  帆船       disconnect SQILRR     ← 2 秒后被踢
            // 结果: 一个【本来就在房间里】的玩家被塞进别人的槽位然后被踢,
            //       而真正掉线的那个人反而进不来 —— 表现为"要等 5 分钟才能再进游戏"。
            //
            // 仅靠身份键不足以区分"重连"与"正常加入": 身份键里的名字在同一 IP 下
            // 也可能重复(服务端身份是按 IP 建立的), 而且保槽在宽限期内一直存在,
            // 期间任何同身份的正常 join 都会命中它。
            //
            // 判据: 正常加入的客户端处于 PreSpawn(尚未进过这一局);
            //       重连的客户端此前已经进过对局, 因此 Limbo 不是 PreSpawn。
            if (client.Player != null && client.Player.Limbo == LimboStates.PreSpawn)
            {
                return false;
            }

            var identityKey = ReconnectRegistry.BuildIdentityKey(
                client.Puid,
                client.FriendCode,
                client.Name);

            return Reconnect.TryTake(
                identityKey,
                client.Connection.EndPoint.Address.ToString(),
                DateTimeOffset.UtcNow,
                out slot);
        }
        /// <summary>
        ///     Puts a reconnecting player back into the running round using
        ///     their original slot.
        /// </summary>
        private async ValueTask HandleJoinGameResume(ClientPlayer player, PendingSlot slot)
        {
            _logger.LogInformation(
                "{0} - Player {1} ({2}) is resuming an existing slot (playerId {3}).",
                Code,
                player.Client.Name,
                player.Client.Id,
                slot.Snapshot.PlayerId);

            player.Disconnected = false;

            // Drop the stale entry left behind by the grace period. The deferred
            // removal deliberately kept _players[oldClientId] alive; once the
            // player is back under a NEW ClientId that old entry is a ghost:
            //   - every broadcast would still be sent to a dead connection,
            //   - the panel would count the player twice,
            //   - and worst, MigrateHost picks the next host with
            //     _players.Values.FirstOrDefault(), so it could hand HostId to a
            //     disconnected client and break start-game / kick for everyone.
            if (slot.Snapshot.OldClientId != player.Client.Id &&
                _players.TryRemove(slot.Snapshot.OldClientId, out var stale))
            {
                stale.Client.Player = null;

                _logger.LogInformation(
                    "Reconnect: dropped the stale slot entry for the old clientId {OldClientId} of {Name}.",
                    slot.Snapshot.OldClientId,
                    slot.Snapshot.PlayerName);
            }

            // Join with the NEW ClientId. HostId is untouched, so the room owner
            // does not change because of a reconnect.
            await PlayerAdd(player);
            player.InitializeSpawnTimeout();

            // Re-key the old PlayerInfo onto the new ClientId and clear the
            // disconnect marks, preserving PlayerId / role / tasks / dead flag.
            await AttachReconnectSlotAsync(slot, player);

            // Tell the client which game it is in, then hand it the roster.
            using (var message = MessageWriter.Get(MessageType.Reliable))
            {
                WriteJoinedGameMessage(message, false, player);
                WriteAlterGameMessage(message, false, IsPublic);

                player.Limbo = LimboStates.NotLimbo;

                await SendToAsync(message, player.Client.Id);
                await BroadcastJoinMessage(message, true, player);
            }

            // Hand the original character back to the client and to the room.
            await ReattachResumedCharacterAsync(player, slot);

            // Hand the mod state back as well (NOS roles etc.). It is queued rather
            // than sent here: the frame has to be addressed to an object the client
            // has already spawned, and the client only finishes syncing the world
            // when it sends SceneChangeFlag - see FlushPendingModStateRestoreAsync.
            QueueModStateRestore(player, slot);

            // And tell the client that it is resuming rather than joining fresh: the
            // client cannot tell those apart on its own, and without the signal its UI
            // stays in the lobby state (no HUD, cannot move). Sent before the mod state.
            QueueResumeSignal(player, slot);

            _reconnectLogs.Record(
                "resumed",
                Code.Code,
                player.Client.Name,
                player.Client.FriendCode ?? string.Empty,
                player.Client.Puid ?? string.Empty,
                player.Client.Connection.EndPoint.Address.ToString(),
                slot.Snapshot.PlayerId,
                slot.Snapshot.OldClientId,
                newClientId: player.Client.Id,
                gapSeconds: (DateTimeOffset.UtcNow - slot.Snapshot.DisconnectedAt).TotalSeconds);
        }

        /// <summary>
        ///     Removes a stale character object from the world and tells every client to
        ///     destroy it.
        /// </summary>
        /// <remarks>
        ///     为什么需要它: 掉线保槽是延迟移除, 期间不广播 Remove, 于是那具身体会一直
        ///     留在所有客户端的世界里(心跳人数虚高、多一个残影)。旧身份彻底结束后必须
        ///     由服务端主动 despawn —— 客户端自己发 DespawnFlag 会被归属校验拒绝, 因为
        ///     对象的 OwnerId 还是旧 ClientId。
        ///
        ///     顺序: 先从 _allObjects 摘掉并解绑引用(OnDestroyAsync 会清 player.Character
        ///     并抛 PlayerDestroyedEvent), 再广播, 避免广播后本地还留着一份。
        /// </remarks>
        private async ValueTask DespawnCharacterAsync(InnerPlayerControl? character)
        {
            if (character == null)
            {
                return;
            }

            // 已经不在了就不用做事(可能被别的路径销毁过)。
            if (!_allObjects.ContainsKey(character.NetId))
            {
                return;
            }

            var netId = character.NetId;

            RemoveNetObject(character);
            await OnDestroyAsync(character);
            await SendObjectDespawnAsync(character);

            _logger.LogInformation(
                "Reconnect: 已销毁旧角色对象 netId {NetId}，并向全场广播。",
                netId);
        }

        /// <summary>
        ///     Re-attaches the returned player to its character. The server never
        ///     creates a body - see the remarks.
        /// </summary>
        /// <remarks>
        ///     服务端【不生成身体】, 只做登记。
        ///
        ///     InnerPlayerControl.SerializeAsync 是故意未实现的(见该文件头说明), 所以服务端
        ///     任何 SendObjectSpawnAsync(character) 都会抛 NotImplementedException —— 实测
        ///     19:50:45 就是这样崩的(栈: InnerPlayerControl.SerializeAsync ->
        ///     Game.SendObjectSpawnAsync -> Game.CreateResumedCharacterAsync)。
        ///     角色的生成消息只由【房主客户端】发出: HandleJoinGameResume 里的
        ///     BroadcastJoinMessage 已经通知了房主, 它会自己造一具。
        /// </remarks>
        private async ValueTask ReattachResumedCharacterAsync(ClientPlayer player, PendingSlot slot)
        {
            var control = FindObjectByNetId<InnerPlayerControl>(slot.Snapshot.CharacterNetId);

            if (control != null)
            {
                // 对象还在服务端世界里 -> 把归属与引用接回去。
                // 这里【不调 SendObjectSpawnAsync】—— 那会去序列化 InnerPlayerControl 而炸。
                control.OwnerId = player.Client.Id;
                player.Character = control;
                player.DisableSpawnTimeout();

                var info = GameNet.GameData.GetPlayerById(slot.Snapshot.PlayerId);
                if (info != null)
                {
                    info.Controller = control;
                }

                _logger.LogInformation(
                    "Reconnect: kept the existing character (netId {NetId}) for {Name} on clientId {ClientId}.",
                    control.NetId,
                    slot.Snapshot.PlayerName,
                    player.Client.Id);

                // 角色已就位: 也走一次 flush(此刻队列通常还是空的, 幂等且无副作用)。
                await OnCharacterReadyAsync(player.Client.Id, "character");

                return;
            }

            // 服务端世界里没有那具身体了 —— 这本来就是常态: 掉线时角色对象被销毁,
            // 而房主客户端会按 BroadcastJoinMessage 重新造一具。等它来即可。
            _logger.LogInformation(
                "Reconnect: no server-side character object for {Name} (netId {NetId} was released); the host is expected to spawn one after the join broadcast.",
                slot.Snapshot.PlayerName,
                slot.Snapshot.CharacterNetId);

            return;
        }

        /// <summary>
        ///     Re-attaches a reserved slot to the reconnecting client, so the
        ///     client is given the original PlayerId, role, tasks and dead/alive
        ///     flag.
        /// </summary>
        /// <remarks>
        ///     Ordering is critical. Once the client enters the OnlineGame scene
        ///     it sends SceneChangeFlag, and that handler calls
        ///     <c>SpawnPlayerInfoAsync</c>. That method only creates a *new*
        ///     PlayerInfo when <c>PlayersByClientId</c> has no entry for the
        ///     client, so the old PlayerInfo has to be re-keyed onto the new
        ///     ClientId before that sync can happen. Otherwise the restored state
        ///     would be replaced by a blank one.
        /// </remarks>
        private async Task AttachReconnectSlotAsync(PendingSlot slot, ClientPlayer player)
        {
            var snapshot = slot.Snapshot;
            var newClientId = player.Client.Id;

            // 重连按【新身份加入】处理: 不把旧 PlayerInfo 重挂到新 clientId。
            //
            // 复用同一个 PlayerId 造新身体时, 各客户端模组层仍持有指向【已销毁旧对象】的
            // 引用, 这些引用不会被刷新 → 每帧解引用已销毁对象(实测堆栈落在
            // PlayerModInfo.CalcSize 与 MeetingModRpc.SortVotingArea), 随后客户端陆续掉线、
            // 会议界面打不开。所以旧身份必须按普通离开清理掉, 新连接以全新玩家加入;
            // 职业/生死等状态由随后的状态包恢复到【新身体】上。
            //
            // 保槽只保留: 身份键、模组状态快照、原 PlayerId 的数值(仅日志/状态包标识用)。
            Reconnect.DropSlot(snapshot.PlayerId);

            // 从登记表摘下 ≠ 用掉: 先记为"未确认", 身体就位并稳定连接后才算消费;
            // 在那之前断开, 移除路径会把这个保槽原样还回去(见 Game.MeetingGate.cs)。
            TrackUnconfirmedResume(newClientId, slot);

            if (_players.TryGetValue(snapshot.OldClientId, out var oldPlayer))
            {
                // 走正常移除(含 PlayerInfo/身体清理与相应广播), 但不要再保槽。
                oldPlayer.Disconnected = false;

                // 先抓住旧角色对象的引用: 移除路径会把 player.Character 置空, 之后就找不到了。
                var staleCharacter = oldPlayer.Character;

                if (oldPlayer.Client.Player == null)
                {
                    // 宽限期内已摘除; 重新挂上, 让正常移除路径能跑完。
                    oldPlayer.Client.Player = oldPlayer;
                }

                await HandleRemovePlayer(snapshot.OldClientId, DisconnectReason.ExitGame, skipReserve: true);

                // 广播销毁旧角色对象(延迟移除期间从未广播过 Remove, 残影就是它)。
                await DespawnCharacterAsync(staleCharacter);
            }

            _logger.LogInformation(
                "Reconnect: {Name} resumed into clientId {ClientId} (was {OldClientId}); the slot's mod state follows on the new identity.",
                snapshot.PlayerName,
                newClientId,
                snapshot.OldClientId);
        }

        /// <summary>
        ///     Releases every reserved slot. Used when the round is over: the reserved
        ///     players cannot come back into a finished round (their PlayerInfo objects
        ///     are despawned right after), and keeping the ghost entries would also
        ///     block host migration and leave the room in a half-occupied state.
        /// </summary>
        internal async ValueTask ReleaseAllReservedSlotsAsync(string reason)
        {
            var reserved = _players
                .Where(kv => kv.Value.IsAwaitingReconnect)
                .Select(kv => (ClientId: kv.Key, Player: kv.Value))
                .ToArray();

            if (reserved.Length == 0)
            {
                return;
            }

            _logger.LogInformation(
                "Reconnect: releasing {Count} reserved slot(s) of {Code} ({Reason}).",
                reserved.Length,
                Code.Code,
                reason);

            foreach (var (clientId, player) in reserved)
            {
                try
                {
                    var gamePlayerId = player.Character?.PlayerInfo?.PlayerId;
                    if (gamePlayerId.HasValue)
                    {
                        Reconnect.ForgetLiveModState(gamePlayerId.Value);
                        Reconnect.DropSlot(gamePlayerId.Value);
                    }

                    player.Disconnected = false;

                    if (player.Client.Player == null)
                    {
                        player.Client.Player = player;
                    }

                    // 移除路径会把 player.Character 置空, 先抓住引用。
                    var staleCharacter = player.Character;

                    _reconnectLogs.Record(
                        "expired",
                        Code.Code,
                        player.Client.Name,
                        player.Client.FriendCode ?? string.Empty,
                        player.Client.Puid ?? string.Empty,
                        player.Client.Connection?.EndPoint?.Address?.ToString() ?? string.Empty,
                        gamePlayerId ?? 0,
                        clientId,
                        detail: reason);

                    await HandleRemovePlayer(clientId, DisconnectReason.ExitGame, skipReserve: true);

                    // 同 ReleaseReconnectSlotAsync: 保槽期间没有广播过 Remove,
                    // 这具身体仍在所有客户端的世界里, 必须服务端主动销毁。
                    await DespawnCharacterAsync(staleCharacter);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reconnect: failed to release a reserved slot of {Code}.", Code.Code);
                }
            }
        }

        // ========== 模组状态回发（缺口①）：把快照里的 blob 还给重连者 ==========

        /// <summary>
        ///     A mod-state restore that still has to be delivered to a resumed client.
        /// </summary>
        private sealed class PendingModStateRestore
        {
            public byte PlayerId { get; init; }

            public byte[] Blob { get; init; } = Array.Empty<byte>();

            public string PlayerName { get; init; } = string.Empty;

            /// <summary>
            ///     The player's own body. Preferred RPC target: the server has just
            ///     re-spawned exactly this object to the reconnecting client, so it is
            ///     guaranteed to exist there, and it mirrors the object the mod itself
            ///     addresses its reports to.
            /// </summary>
            public uint CharacterNetId { get; init; }

            /// <summary>
            ///     ClientId this player used before dropping: its own reports carry the
            ///     process hash of that player's mod build.
            /// </summary>
            public int OldClientId { get; init; }

            /// <summary>Set once the frame has been written to the client.</summary>
            public int Sent;
        }

        private readonly ConcurrentDictionary<int, PendingModStateRestore> _pendingModStateRestores = new();

        private const int ModStateRestoreFallbackMs = 2500;

        /// <summary>
        ///     Queues the captured mod state of a resumed player, and schedules a
        ///     fallback attempt in case the client never sends SceneChangeFlag.
        /// </summary>
        private void QueueModStateRestore(ClientPlayer player, PendingSlot slot)
        {
            var blob = slot.Snapshot.ModState;

            if (blob == null || blob.Length == 0)
            {
                // Either the mod never reported (vanilla client) or CaptureModState
                // is off. Vanilla restore already happened above; nothing to send.
                return;
            }

            var pending = new PendingModStateRestore
            {
                PlayerId = slot.Snapshot.PlayerId,
                Blob = blob,
                PlayerName = slot.Snapshot.PlayerName,
                CharacterNetId = slot.Snapshot.CharacterNetId,
                OldClientId = slot.Snapshot.OldClientId,
            };

            _pendingModStateRestores[player.Client.Id] = pending;

            _logger.LogInformation(
                "Reconnect: mod state queued for {Name} (playerId {PlayerId}, {Bytes} bytes); it is sent once the client reports its scene.",
                slot.Snapshot.PlayerName,
                slot.Snapshot.PlayerId,
                blob.Length);

            var clientId = player.Client.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ModStateRestoreFallbackMs);
                    await FlushPendingModStateRestoreAsync(clientId, "fallback");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reconnect: delayed mod-state restore failed for client {ClientId}.", clientId);
                }
            });
        }

        /// <summary>
        ///     Records that a resume signal has to be pushed to this client once it has
        ///     finished entering the scene. Mirrors <see cref="QueueModStateRestore" />:
        ///     the signal must NOT be sent while the client is still loading, because the
        ///     client's resume transition needs the ship to be present.
        /// </summary>
        /// <remarks>
        ///     与状态包同样挂一个兜底定时器: 没有模组状态(香草客户端/未采集)时
        ///     队列里没有状态包, 那条兜底路径就不会触发, 续局信号会一直等下去。
        ///     Sent 标记保证重复触发只会发一次。
        /// </remarks>
        private void QueueResumeSignal(ClientPlayer player, PendingSlot slot)
        {
            _pendingResumeSignals[player.Client.Id] = new PendingResumeSignal
            {
                PlayerId = slot.Snapshot.PlayerId,
                OldClientId = slot.Snapshot.OldClientId,
                PlayerName = slot.Snapshot.PlayerName,
                FriendCode = slot.Snapshot.FriendCode,
                DisconnectedAt = slot.Snapshot.DisconnectedAt,
                Sent = 0,
            };

            var clientId = player.Client.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(ModStateRestoreFallbackMs);
                    await FlushPendingResumeSignalAsync(clientId, "fallback");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reconnect: delayed resume signal failed for client {ClientId}.", clientId);
                }
            });
        }

        private sealed class PendingResumeSignal
        {
            public byte PlayerId { get; init; }

            /// <summary>ClientId this player used before dropping; its reports carry the mod wire info.</summary>
            public int OldClientId { get; init; }

            public string PlayerName { get; init; } = string.Empty;

            public string FriendCode { get; init; } = string.Empty;

            /// <summary>掉线时刻: 用来判断"掉线之后有没有开过会"(决定散会后的落位)。</summary>
            public DateTimeOffset DisconnectedAt { get; init; }

            /// <summary>Set once the host has been told to spawn the body.</summary>
            public int DirectiveSent;

            /// <summary>Set once the frame has been written to the client.</summary>
            public int Sent;
        }

        private readonly ConcurrentDictionary<int, PendingResumeSignal> _pendingResumeSignals = new();

        /// <summary>
        ///     Flushes both mod frames for a client whose character has just appeared.
        ///     Called from every place where the server confirms <c>player.Character</c>:
        ///     that is the earliest moment the receiver owns a PlayerControl, and a
        ///     PlayerControl is the only carrier the client's custom-RPC dispatcher sees.
        /// </summary>
        internal async ValueTask OnCharacterReadyAsync(int clientId, string trigger = "character")
        {
            // 重连者的身体到了: 开始计时, 稳定连接一段时间后保槽才正式消费。
            ScheduleResumeConfirmation(clientId);

            await FlushPendingResumeSignalAsync(clientId, trigger);
            await FlushPendingModStateRestoreAsync(clientId, trigger);

            // 会议中进来的重连者: 身体一到就告诉它这场会议以旧身份投票。
            await TrySendMeetingSeatAsync(clientId, trigger);

            // 该连接刚有身体 —— 名册只在"变化时"广播, 它进来时可能不在场(收不到),
            // 所以这里单播补发一次完整名册。
            if (_deadRoster.Count > 0)
            {
                await SendDeadRosterToAsync(clientId, "character");
            }

            // 死者重连(按原状恢复): 身体刚就位, 此刻才拿得到 PlayerInfo。
            // 名册里的死者要和服务端生死态一致, 否则会议投票永远等不到"全员已投或已死"。
            if (_deadRoster.Contains(clientId))
            {
                MarkServerSideDead(clientId, "ghost-roster-character");
            }

            // 观战者同理: 判定帧发出时它往往还没有身体, 这里补发一次;
            // 顺带把服务端生死态标死 —— 身体刚生成, 此时才第一次拿得到 PlayerInfo。
            if (_spectators.Contains(clientId))
            {
                MarkServerSideDead(clientId, "spectator-character");

                await SendSpectatorFlagAsync(clientId);
                await SendDeadRosterToAsync(clientId, "character");
            }

            if (_spectators.Count > 0)
            {
                await SendSpectatorRosterToAsync(clientId);
            }

            // 任何角色就位都意味着"载体可能刚出现", 把之前发不出去的观战判定再试一遍。
            await FlushPendingSpectatorFlagsAsync();
        }

        // ───────────────────────── 死亡名册 ─────────────────────────
        //
        // 需要它的原因: 香草的 NetworkedPlayerInfo.IsDead 同步路径在本服配置下不可用 ——
        // 房主标脏了, 但其他客户端收不到变更(实测其余三台 GameData 诊断里 dead=False),
        // 于是"别人眼里你是活人"。这里用自有通道补上这一环: 名册是权威, 客户端按名册
        // 在表现层灵魂化, 完全不依赖香草数据。

        /// <summary>ClientIds that must be shown as dead for the current round.</summary>
        private readonly HashSet<int> _deadRoster = new();

        /// <summary>
        ///     Records a ghost report. Identity comes from the connection, never from the
        ///     payload. Broadcasts only when the roster actually changes, because the
        ///     client repeats its report periodically to survive packet loss.
        /// </summary>
        internal async ValueTask RememberGhostReport(int clientId, string playerName)
        {
            if (!_deadRoster.Add(clientId))
            {
                return; // 已在名册里: 周期重发, 不需要再广播
            }

            _logger.LogInformation(
                "Reconnect: {Name} (clientId {ClientId}) 已记入死亡名册 (共 {Count} 人)。",
                playerName,
                clientId,
                _deadRoster.Count);

            // 诊断: 把服务端自己的认知也打出来, 便于判断"客户端上报"与"服务端状态"是否一致。
            var serverSideInfo = GetPlayerInfoByClientId(clientId);
            _logger.LogInformation(
                "Reconnect: 服务端侧 clientId {ClientId} 的 IsDead={IsDead} (死者重连自检)。",
                clientId,
                serverSideInfo?.IsDead.ToString() ?? "无 PlayerInfo");

            // 与客户端的认知保持一致: 名册里的死者在服务端也必须是死者
            // (否则会议投票永远等不到"全员已投或已死")。
            MarkServerSideDead(clientId, "ghost-report");

            await BroadcastDeadRosterAsync();
        }

        /// <summary>Full roster broadcast to every client that currently has a body.</summary>
        private async ValueTask BroadcastDeadRosterAsync()
        {
            foreach (var kv in _players)
            {
                await SendDeadRosterToAsync(kv.Key, "broadcast");
            }
        }

        /// <summary>
        ///     Sends the complete roster to one client.
        /// </summary>
        /// <remarks>
        ///     载体规则与 kind=3/4 一致: 帧必须挂在【该客户端本地存在】的 PlayerControl
        ///     netId 上, 否则客户端会静默丢弃。没有身体的连接先跳过 —— 它角色就位时
        ///     会由 OnCharacterReadyAsync 补发。
        ///
        ///     processHash 也必须是【该接收者自己】上报过的那个: 每个客户端的进程
        ///     哈希不同, 用别人的值客户端会认不出来。
        /// </remarks>
        internal async ValueTask SendDeadRosterToAsync(int clientId, string trigger)
        {
            if (!_players.TryGetValue(clientId, out var target))
            {
                return;
            }

            // 载体/hash 回退: 观战者刚进来时没有自己的角色, 也没有上报过 hash。
            // 借房主的角色作载体、借房里的 hash 照样能把名册送到它手上。
            var carrier = target.Character ?? Host?.Character;
            if (carrier == null)
            {
                return;
            }

            if (!Reconnect.TryGetWireInfoForClient(clientId, out var wire)
                && !Reconnect.TryGetWireInfo(null, out wire))
            {
                // 该连接还没上报过模组状态(香草客户端/刚加入), 拿不到它的进程哈希。
                _logger.LogDebug(
                    "Reconnect: no mod wire info for client {ClientId}; skipping dead roster ({Trigger}).",
                    clientId,
                    trigger);
                return;
            }

            try
            {
                var carrierNetId = carrier.NetId;
                var count = _deadRoster.Count;

                using (var writer = StartRpc(carrierNetId, (RpcCalls)NebulaResumeRpc.RpcId, clientId))
                {
                    writer.Write(wire.ProcessHash);

                    // 帧内 int 总数 = KindGhostRoster + N + N 个 clientId
                    writer.Write(count + 2);
                    writer.Write(NebulaResumeRpc.KindGhostRoster);
                    writer.Write(count);
                    foreach (var cid in _deadRoster)
                    {
                        writer.Write(cid);
                    }

                    await FinishRpcAsync(writer, clientId);
                }

                _logger.LogInformation(
                    "Reconnect: 已向 clientId {ClientId} 下发死亡名册 ({Count} 人)。",
                    clientId,
                    count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect: 下发死亡名册失败 (clientId {ClientId})。", clientId);
            }
        }

        /// <summary>Clears the roster at the end of a round.</summary>
        private void ResetDeadRoster()
        {
            if (_deadRoster.Count == 0)
            {
                return;
            }

            _deadRoster.Clear();
            _logger.LogInformation("Reconnect: 死亡名册已清空(对局结束)。");
        }

        // ───────────────────────── 观战席 ─────────────────────────
        //
        // 两条独立路径, 互不干扰 —— 判定依据只有一条: 这个人的身份在本局里出现过吗。
        //   掉线重连(身份出现过) → 恢复原职业 + 固定为灵魂
        //   全新玩家中途加入     → 中立观战职业 + 固定为灵魂

        /// <summary>ClientIds currently spectating this round.</summary>
        private readonly HashSet<int> _spectators = new();

        // ── 房主能力声明 (kind=9) ──
        //
        // 房间是否允许"对局中放行新玩家", 由【房主自己声明】。服务端只记录, 不做推断:
        // 没收到声明的房间一律按原版处理(拒绝新玩家), 这样即使服务端开了观战开关,
        // 也不会误放行没装插件的房间。

        /// <summary>Capability flags reported by the host. 0 = never declared.</summary>
        private int _roomCapability;

        /// <summary>Protocol version of the declaration. 0 = never declared.</summary>
        private int _roomProtocolVersion;

        /// <summary>Spectator limit chosen by the host. 0 = none.</summary>
        private int _spectatorLimit;

        /// <summary>
        ///     房主装了插件、但在设置里关掉了「帆船服断线重连」: 声明过能力, 却没有重连位。
        ///     这时掉线不保槽, 按普通离开处理。没装插件(从未声明)的房间不受影响。
        /// </summary>
        internal bool RoomUsesResume => _roomProtocolVersion > 0;

        internal bool HostDisabledReconnect =>
            _roomProtocolVersion > 0 && (_roomCapability & NebulaResumeRpc.CapabilityReconnect) == 0;

        /// <summary>
        ///     Records a capability declaration from the host.
        /// </summary>
        /// <remarks>
        ///     只有房主本人的声明才算数: 其他客户端发来的会被忽略, 否则任意玩家都能
        ///     把房间标记成"支持观战", 绕开服务端的准入控制。
        ///     房主在大厅阶段就周期上报, 所以正常情况下第一个新玩家尝试加入时就已经知道结果。
        /// </remarks>
        internal void RememberRoomCapability(int clientId, string playerName, int version, int flags, int limit)
        {
            // 无条件先记录"帧到达"这件事本身。
            //
            // 这一步是排障用的: 如果客户端说"我发了"而这里没有这条日志, 就说明帧根本
            // 没出客户端(它自己的发送前置条件把它挡掉了); 如果有这条日志但下面报
            // non-host, 就说明身份对不上。两种情况要改的地方完全不同。
            _logger.LogInformation(
                "RoomCapability: received from clientId {ClientId} (host={HostClientId}) version={Version} flags={Flags} limit={Limit}",
                clientId,
                Host?.Client.Id ?? -1,
                version,
                flags,
                limit);

            if (Host == null || Host.Client.Id != clientId)
            {
                _logger.LogWarning(
                    "RoomCapability: ignoring declaration from non-host {Name} (clientId {ClientId}, host={HostClientId}).",
                    playerName,
                    clientId,
                    Host?.Client.Id ?? -1);
                return;
            }

            // 重复声明且内容没变时不刷日志(房主每 4 秒上报一次)。
            if (version == _roomProtocolVersion && flags == _roomCapability && limit == _spectatorLimit)
            {
                return;
            }

            _roomProtocolVersion = version;
            _roomCapability = flags;
            _spectatorLimit = limit;

            _logger.LogInformation(
                "RoomCapability: host declared version={Version} reconnect={Reconnect} spectator={Spectator} limit={Limit}",
                version,
                (flags & NebulaResumeRpc.CapabilityReconnect) != 0,
                (flags & NebulaResumeRpc.CapabilitySpectator) != 0,
                limit);
        }

        /// <summary>
        ///     Clears the capability declaration. Called when the host leaves: the new host
        ///     has to declare again, otherwise the room would keep running on the old host's
        ///     promise and could admit players into a room that no longer supports it.
        /// </summary>
        internal void ForgetRoomCapability(string reason)
        {
            if (_roomCapability == 0 && _roomProtocolVersion == 0 && _spectatorLimit == 0)
            {
                return;
            }

            _roomCapability = 0;
            _roomProtocolVersion = 0;
            _spectatorLimit = 0;

            _logger.LogInformation("RoomCapability: 房主能力声明已清空 ({Reason})。", reason);
        }

        /// <summary>
        ///     Marks a connection dead on the SERVER side, so server logic agrees with what
        ///     the clients are told.
        /// </summary>
        /// <remarks>
        ///     为什么服务端也要标死 —— 只广播"你是鬼"到客户端还不够:
        ///     会议投票的结束判定是 <c>_playerStates.All(ps =&gt; ps.IsDead || ps.DidVote)</c>,
        ///     名册里的死者永远不会投票。只要它在服务端的 PlayerInfo 里仍是活人,
        ///     会议就会一直等到超时。
        ///
        ///     三个触发路径共用这一个方法(它们时机不同, 但动作完全相同):
        ///       · 死亡名册 —— 客户端上报 kind=5 之后
        ///       · 按原状恢复 —— 重连后确认为灵魂时
        ///       · 观战席 —— 判定为观战者时
        ///
        ///     幂等: 重复调用只是重复写同一个值, 没有副作用。PlayerInfo 此刻可能还不存在
        ///     (身体由房主端稍后补生成), 那就留给"角色就位"的第二次调用去写。
        /// </remarks>
        internal void MarkServerSideDead(int clientId, string reason)
        {
            var info = GetPlayerInfoByClientId(clientId);

            if (info == null)
            {
                _logger.LogDebug(
                    "Reconnect: no PlayerInfo yet for clientId {ClientId} ({Reason}); the server-side dead flag is set when the body arrives.",
                    clientId,
                    reason);
                return;
            }

            if (info.IsDead)
            {
                return; // 已经是死者, 不必重复写(也避免刷日志)
            }

            info.IsDead = true;

            _logger.LogInformation(
                "Reconnect: clientId {ClientId} 已在服务端标记为死者 ({Reason})。",
                clientId,
                reason);
        }

        /// <summary>
        ///     Finds the PlayerInfo of a connection: prefers the body's own PlayerInfo, and
        ///     falls back to the game-data index keyed by ClientId (the body may not exist
        ///     yet while a reserved slot waits for the host to spawn one).
        /// </summary>
        private InnerPlayerInfo? GetPlayerInfoByClientId(int clientId)
        {
            if (_players.TryGetValue(clientId, out var player)
                && player.Character?.PlayerInfo is { } fromBody)
            {
                return fromBody;
            }

            return GameNet.GameData.PlayersByClientId.TryGetValue(clientId, out var byClient)
                ? byClient
                : null;
        }

        /// <summary>
        ///     Decides whether a joining connection should become a spectator.
        /// </summary>
        /// <remarks>
        ///     调用点必须在"保槽匹配失败之后": 命中保槽的是重连者, 它有原职业要恢复,
        ///     绝不能当成观战者。
        ///
        ///     四道闸门, 缺一不可 —— 任何一道不满足都保持原行为(拒绝, 提示对局已开始):
        ///       ① 服务端自己开了观战 (EnableSpectator)
        ///       ② 房主声明过支持观战 (kind=9 的 bit1)
        ///       ③ 房主的协议版本与本服务端一致
        ///       ④ 观战人数未达上限 (取房主设置与服务端配置的较小值)
        ///
        ///     ②③ 是"绝不误放行"的关键: 没装插件的房间永远收不到 kind=9, 因此行为
        ///     与原版完全一致 —— 即使服务端开关是开的。
        ///
        ///     玩家 id 是位掩码(1 &lt;&lt; PlayerId), 所以玩家 + 观战者总数受 32 限制。
        /// </remarks>
        internal bool ShouldJoinAsSpectator()
        {
            // 逐条记录每个闸门的结果。
            // 这里的日志是排障的关键: 准入被拒时只看到 "GameStarted", 看不出是哪一条
            // 没通过; 这四行把四个值都摊开, 一次测试就能定位。
            // ① 服务端开关
            if (!_reconnectConfig.EnableSpectator)
            {
                _logger.LogInformation(
                    "SpectatorGate: 拒绝 —— ① 服务端 EnableSpectator=false");
                return false;
            }

            // 对局状态: Starting 与 Started 都算"已经开始", 都可以考虑观战。
            //
            // 只看 Started 是不够的: 开局后有一段 Starting 过渡期(所有人 spawn + 分配职业),
            // 而进入 Started 依赖房主的 SetRole 被服务端处理。只要那一步有任何延迟或卡顿,
            // 状态就会停在 Starting —— 此时玩家在客户端看来已经在对局里了, 观战者却会被
            // 判为"房间已开始"而进不来。把 Starting 一并放行, 行为与玩家感知一致。
            if (GameState != GameStates.Started && GameState != GameStates.Starting)
            {
                _logger.LogInformation(
                    "SpectatorGate: 拒绝 —— 对局状态={GameState} (Starting/Started 才放行观战)。",
                    GameState);
                return false;
            }

            // ② 房主声明过"支持观战"
            if ((_roomCapability & NebulaResumeRpc.CapabilitySpectator) == 0)
            {
                _logger.LogInformation(
                    "SpectatorGate: 拒绝 —— ② 房主未声明观战 (capability={Capability})。",
                    _roomCapability);
                return false;
            }

            // ③ 协议版本必须一致, 避免新旧混用
            if (_roomProtocolVersion != NebulaResumeRpc.ProtocolVersion)
            {
                _logger.LogInformation(
                    "SpectatorGate: 拒绝 —— ③ 协议版本不符 (房主={HostVersion}, 服务端={ServerVersion})。",
                    _roomProtocolVersion,
                    NebulaResumeRpc.ProtocolVersion);
                return false;
            }

            // ④ 人数上限: 房主设置的, 不得超过服务端配置
            var limit = Math.Min(_spectatorLimit, _reconnectConfig.MaxSpectators);
            if (limit <= 0 || _spectators.Count >= limit)
            {
                _logger.LogInformation(
                    "SpectatorGate: 拒绝 —— ④ 观战人数上限 (房主上限={HostLimit}, 服务端上限={ServerLimit}, 生效={Limit}, 当前={Current})。",
                    _spectatorLimit,
                    _reconnectConfig.MaxSpectators,
                    limit,
                    _spectators.Count);
                return false;
            }

            // 32 人预算: 超过会让玩家 id 位掩码溢出。
            if (_players.Count + _spectators.Count >= 32)
            {
                _logger.LogInformation(
                    "SpectatorGate: 拒绝 —— 32 人预算已满 (玩家={Players}, 观战={Spectators})。",
                    _players.Count,
                    _spectators.Count);
                return false;
            }

            _logger.LogInformation(
                "SpectatorGate: 通过 —— 以观战身份放行 (当前观战 {Current}/{Limit})。",
                _spectators.Count,
                limit);

            return true;
        }

        /// <summary>
        ///     Turns a just-joined connection into a spectator: tells it, asks the host to
        ///     spawn a body for it, puts it on the dead roster so everybody sees a ghost,
        ///     and publishes both rosters.
        /// </summary>
        internal async ValueTask AdmitAsSpectatorAsync(ClientPlayer player)
        {
            var clientId = player.Client.Id;

            if (!_spectators.Add(clientId))
            {
                return;
            }

            _logger.LogInformation(
                "Spectator: {Name} (clientId {ClientId}) joined as spectator (total {Count}).",
                player.Client.Name,
                clientId,
                _spectators.Count);

            // 观战者固定为灵魂 —— 与死亡名册同一套机制, 全场因此都当它是鬼。
            _deadRoster.Add(clientId);

            MarkServerSideDead(clientId, "spectator");

            // 观战判定(kind=7): 此时客户端通常还没有角色对象, SendSpectatorFlagAsync
            // 会挂起到角色就位再补发, 所以这里发了也不会丢。
            await SendSpectatorFlagAsync(clientId);

            // 补生成身体(kind=4)【不在这里发】: 这个指令是给房主的, 需要房主侧已经有
            // 该 clientId 的 PlayerInfo 才能处理。准入这一刻房主可能还没建好资料,
            // 提前发会让观战者永远拿不到身体。改由场景就绪后触发
            // (OnSpectatorSceneReadyAsync)。

            // ③④ 全量名册: 让全场立刻知道它是死者 / 是观战者。
            await BroadcastDeadRosterAsync();
            await BroadcastSpectatorRosterAsync();
        }

        /// <summary>ClientIds whose spawn directive has already been sent, so a repeated
        /// scene change does not create a second body.</summary>
        private readonly HashSet<int> _spectatorBodyRequested = new();

        /// <summary>
        ///     Runs the spectator hand-off once the realm is ready for it: the client has
        ///     reported its scene, which means the host has created this player's
        ///     PlayerInfo and can act on a spawn directive.
        /// </summary>
        internal async ValueTask OnSpectatorSceneReadyAsync(ClientPlayer player)
        {
            var clientId = player.Client.Id;

            if (!_spectators.Contains(clientId))
            {
                return;
            }

            // ① 观战判定 —— 幂等, 客户端重复收到无害。
            await SendSpectatorFlagAsync(clientId);

            // ② 补生成身体 —— 只发一次。重复发送会让房主再生成一具身体(残影)。
            //   会议中也立刻发: 支持会议中进入的房主会在会议里照常生成观战者的身体
            //   (观战者要有身体才能和死者聊天)。散会后后台循环再检查一次, 已发过的不会重发。
            if (UseMeetingJoin)
            {
                _meetingSpectators[clientId] = 0;
                EnsureMeetingJoinLoop();
            }

            if (_spectatorBodyRequested.Add(clientId))
            {
                await SendSpawnDirectiveForAsync(clientId, player.Client.Name);
            }

            // ③④ 再确认一次死亡状态: 资料刚就位, 补一次最稳。
            _deadRoster.Add(clientId);
            MarkServerSideDead(clientId, "spectator-scene");
            await BroadcastDeadRosterAsync();
            await BroadcastSpectatorRosterAsync();

            // 房主此刻一定已经有身体, 之前因"找不到载体/hash"挂起的判定可以重试了。
            await FlushPendingSpectatorFlagsAsync();
        }

        /// <summary>Spectators whose flag frame could not be sent yet (no carrier / no hash).</summary>
        private readonly HashSet<int> _pendingSpectatorFlag = new();

        /// <summary>Sends <c>[KindSpectator, 1]</c> to one client.</summary>
        /// <remarks>
        ///     载体与 hash 都有回退, 否则观战者永远收不到判定:
        ///     它是全新 clientId, 刚进来时【既没有角色对象, 也没上报过任何帧】。
        ///
        ///     载体: 自己的角色 → 房主的角色。
        ///       自定义帧必须挂在一个客户端本地真实存在的 PlayerControl 的 netId 上,
        ///       否则客户端的 RpcHandler 找不到目标会直接丢弃。观战者还没身体时用房主的
        ///       角色作载体 —— 帧照常广播, target 仍写观战者自己, 由它按 target 过滤处理。
        ///
        ///     hash: 自己的 → 房里任意一个已上报客户端的。
        ///       帧头要写插件进程名的常量哈希; 同一插件版本算出来完全相同, 所以借用即可。
        ///
        ///     两个都拿不到就挂进待发集合, 等下一次时机(场景就绪 / 角色就位)重试。
        /// </remarks>
        internal async ValueTask SendSpectatorFlagAsync(int clientId)
        {
            if (!_players.TryGetValue(clientId, out var target))
            {
                return;
            }

            // ── 载体: 自己的角色优先, 退而用房主的 ──
            var carrier = target.Character ?? Host?.Character;

            if (carrier == null)
            {
                _pendingSpectatorFlag.Add(clientId);
                _logger.LogInformation(
                    "Spectator: clientId {ClientId} 尚无可用载体(自己和房主都还没有角色), 观战判定挂起待发。",
                    clientId);
                return;
            }

            // ── hash: 自己的优先, 退而借用房里任一已上报的 ──
            if (!Reconnect.TryGetWireInfoForClient(clientId, out var wire)
                && !Reconnect.TryGetWireInfo(null, out wire))
            {
                _pendingSpectatorFlag.Add(clientId);
                _logger.LogInformation(
                    "Spectator: clientId {ClientId} 尚无可用模组 hash, 观战判定挂起待发。",
                    clientId);
                return;
            }

            try
            {
                using (var writer = StartRpc(carrier.NetId, (RpcCalls)NebulaResumeRpc.RpcId, clientId))
                {
                    writer.Write(wire.ProcessHash);
                    writer.Write(2); // KindSpectator + 值
                    writer.Write(NebulaResumeRpc.KindSpectator);
                    writer.Write(1); // 1 = 是观战者
                    await FinishRpcAsync(writer, clientId);
                }

                _pendingSpectatorFlag.Remove(clientId);

                _logger.LogInformation(
                    "Spectator: 已向 clientId {ClientId} 下发观战判定 (载体 netId {Carrier})。",
                    clientId,
                    carrier.NetId);
            }
            catch (Exception ex)
            {
                _pendingSpectatorFlag.Add(clientId);
                _logger.LogError(ex, "Spectator: 下发观战判定失败 (clientId {ClientId})。", clientId);
            }
        }

        /// <summary>
        ///     Retries every spectator flag that could not be delivered earlier. Called
        ///     whenever a character appears or a scene becomes ready - both are moments
        ///     where a carrier may have just become available.
        /// </summary>
        private async ValueTask FlushPendingSpectatorFlagsAsync()
        {
            if (_pendingSpectatorFlag.Count == 0)
            {
                return;
            }

            foreach (var pendingClientId in _pendingSpectatorFlag.ToArray())
            {
                await SendSpectatorFlagAsync(pendingClientId);
            }
        }

        /// <summary>Full spectator roster broadcast to every client that has a body.</summary>
        private async ValueTask BroadcastSpectatorRosterAsync()
        {
            foreach (var kv in _players)
            {
                await SendSpectatorRosterToAsync(kv.Key);
            }
        }

        /// <summary>Sends the complete spectator roster to one client.</summary>
        internal async ValueTask SendSpectatorRosterToAsync(int clientId)
        {
            if (!_players.TryGetValue(clientId, out var target))
            {
                return;
            }

            // 载体/hash 的回退与 SendSpectatorFlagAsync 相同: 观战者刚进来时自己既没有
            // 角色也没有 hash, 借用房主的载体与房里的 hash 才能把名单送到它手上。
            var carrier = target.Character ?? Host?.Character;
            if (carrier == null)
            {
                return;
            }

            if (!Reconnect.TryGetWireInfoForClient(clientId, out var wire)
                && !Reconnect.TryGetWireInfo(null, out wire))
            {
                return;
            }

            try
            {
                var count = _spectators.Count;

                using (var writer = StartRpc(carrier.NetId, (RpcCalls)NebulaResumeRpc.RpcId, clientId))
                {
                    writer.Write(wire.ProcessHash);
                    writer.Write(count + 2);
                    writer.Write(NebulaResumeRpc.KindSpectatorRoster);
                    writer.Write(count);
                    foreach (var cid in _spectators)
                    {
                        writer.Write(cid);
                    }

                    await FinishRpcAsync(writer, clientId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Spectator: 下发观战名单失败 (clientId {ClientId})。", clientId);
            }
        }

        /// <summary>Clears the spectator list at the end of a round.</summary>
        internal void ResetSpectators()
        {
            if (_spectators.Count == 0)
            {
                return;
            }

            _spectators.Clear();
            _logger.LogInformation("Spectator: 观战名单已清空(对局结束)。");
        }

        /// <summary>
        ///     Tells the host to spawn the character for a resumed client, carrying the
        ///     preserved player id - the vanilla spawn path would take a free id, and
        ///     only the server knows which id the reserved slot holds. Sent at most once.
        /// </summary>
        /// <remarks>
        ///     载体必须是房主自己的角色: 客户端对 callId 128 的分发器只挂在
        ///     PlayerControl.HandleRpc 上, 所以目标 netId 必须是一个在房主端存在的
        ///     PlayerControl 对象; 发给别人或已销毁的对象都会被静默丢弃。
        /// </remarks>
        private async ValueTask SendSpawnDirectiveAsync(PendingResumeSignal pending, int clientId)
        {
            if (Interlocked.Exchange(ref pending.DirectiveSent, 1) != 0)
            {
                return;
            }

            await SendSpawnDirectiveForAsync(clientId, pending.PlayerName, pending.OldClientId);
        }

        /// <summary>
        ///     Sends the host a spawn directive for <paramref name="clientId" />, telling it
        ///     to create a body with a free player id.
        /// </summary>
        /// <param name="wireInfoClientId">
        ///     Whose process hash to use for the frame. The receiving host must recognise
        ///     it, so it is normally the joining connection's own id; a resumed player uses
        ///     the id it had before dropping, because that is where its reports came from.
        /// </param>
        private async ValueTask SendSpawnDirectiveForAsync(int clientId, string playerName, int? wireInfoClientId = null)
        {
            try
            {
                // 房主在对局中一定有身体: 找 ClientId == HostId 且角色已就位的那条连接。
                ClientPlayer? host = null;

                foreach (var kv in _players)
                {
                    if (kv.Value.Client.Id == HostId && kv.Value.Character != null)
                    {
                        host = kv.Value;
                        break;
                    }
                }

                if (host == null)
                {
                    _logger.LogInformation(
                        "Reconnect: no host with a live character; cannot send the spawn directive for {Name}.",
                        playerName);
                    return;
                }

                if (!Reconnect.TryGetWireInfoForClient(host.Client.Id, out var wire))
                {
                    _logger.LogWarning(
                        "Reconnect: no mod wire info yet; cannot send the spawn directive for {Name}.",
                        playerName);
                    return;
                }

                var hostNetId = host.Character!.NetId;

                // -1 表示"用空闲 PlayerId 新建身份": 房主按香草的 GetAvailableId() 生成。
                // 不复用保槽里的旧 PlayerId —— 旧身体已销毁, 各客户端模组层残留的引用
                // 不会被刷新, 复用同一 PlayerId 会每帧空引用。
                const int preservedPlayerId = -1;

                using (var writer = StartRpc(hostNetId, (RpcCalls)NebulaResumeRpc.RpcId, host.Client.Id))
                {
                    writer.Write(wire.ProcessHash);
                    writer.Write(3); // count = 3: KindSpawn, clientId, preservedPlayerId
                    writer.Write(NebulaResumeRpc.KindSpawn);
                    writer.Write(clientId);
                    writer.Write(preservedPlayerId);
                    await FinishRpcAsync(writer, host.Client.Id);
                }

                _logger.LogInformation(
                    "Reconnect: sent spawn directive for {Name} (clientId {ClientId}, preserved playerId {PlayerId}) to host {HostClientId}.",
                    playerName,
                    clientId,
                    preservedPlayerId,
                    host.Client.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect: failed to send the spawn directive for client {ClientId}.", clientId);
            }
        }

        /// <summary>How often the fallback retry chain runs.</summary>
        private const int FrameRetryIntervalMs = 5000;

        /// <summary>How many times it tries before giving up (about a minute).</summary>
        private const int FrameRetryMaxAttempts = 12;

        private readonly ConcurrentDictionary<int, byte> _frameRetryScheduled = new();

        /// <summary>
        ///     Schedules one bounded retry chain per client while its character is still
        ///     missing. Returns true when this call started the chain, so the caller can
        ///     log the deferral once instead of on every attempt.
        /// </summary>
        private bool ScheduleFrameRetry(int clientId)
        {
            if (!_frameRetryScheduled.TryAdd(clientId, 0))
            {
                return false;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    for (var attempt = 1; attempt <= FrameRetryMaxAttempts; attempt++)
                    {
                        await Task.Delay(FrameRetryIntervalMs).ConfigureAwait(false);

                        if (!_players.TryGetValue(clientId, out var player) || player.Character == null)
                        {
                            continue; // 身体还没就位, 继续等
                        }

                        await OnCharacterReadyAsync(clientId, "retry");

                        if (!_pendingResumeSignals.ContainsKey(clientId)
                            && !_pendingModStateRestores.ContainsKey(clientId))
                        {
                            return; // 该发的都发完了
                        }
                    }

                    if (_pendingResumeSignals.ContainsKey(clientId)
                        || _pendingModStateRestores.ContainsKey(clientId))
                    {
                        _logger.LogWarning(
                            "Reconnect: gave up sending mod frames for client {ClientId} after {Attempts} retries (no character).",
                            clientId,
                            FrameRetryMaxAttempts);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reconnect: mod-frame retry chain failed for client {ClientId}.", clientId);
                }
                finally
                {
                    _frameRetryScheduled.TryRemove(clientId, out _);
                }
            });

            return true;
        }

        /// <summary>
        ///     Pushes the resume signal to a client, once. Frame layout matches the
        ///     client's NebulaStateWire and mirrors the restore frame, just shorter:
        ///     <c>[processHash:int32][count:int32][ KindResume, playerId ]</c>.
        ///     Carries no mod state - that arrives afterwards as <c>KindRestore</c>.
        /// </summary>
        internal async ValueTask FlushPendingResumeSignalAsync(int clientId, string trigger = "scene")
        {
            if (!_pendingResumeSignals.TryGetValue(clientId, out var pending))
            {
                return;
            }

            // 载体必须是一个【接收端本地存在】的 PlayerControl。
            //
            // 客户端模组的自定义 RPC 分发器只挂在 PlayerControl.HandleRpc 上:
            //   · 目标对象在接收端不存在 -> 客户端在网络层直接丢弃;
            //   · 目标是其它类型(比如 InnerPlayerInfo) -> 走的是那个类型的 HandleRpc,
            //     分发器不会触发, 同样是静默丢弃、无任何日志。
            // 所以只能用接收者自己的角色当载体 —— 生成消息会发给包括本人在内的所有客户端,
            // 它必然存在于接收者本地。
            //
            // 进场景时身体往往还不存在(房主端还没补生成), 此时【推迟】而不发送,
            // 也不要标记 Sent, 等角色就位的触发点再来一次。
            if (!_players.TryGetValue(clientId, out var current) || current.Character == null)
            {
                // 让房主把身体补生成出来(带保槽里的原 PlayerId, 只有服务端知道)。
                // 房主生成 -> SpawnFlag 上报 -> player.Character 就位 -> 本方法的
                // "character" 触发点会再来一次, 那时才真正下发续局信号与状态包。
                await SendSpawnDirectiveAsync(pending, clientId);

                if (ScheduleFrameRetry(clientId))
                {
                    _logger.LogInformation(
                        "Reconnect: deferring mod frames for {Name} (no character yet, client {ClientId}, trigger {Trigger}).",
                        pending.PlayerName,
                        clientId,
                        trigger);
                }
                else
                {
                    _logger.LogDebug(
                        "Reconnect: still deferring mod frames for {Name} (no character yet, client {ClientId}, trigger {Trigger}).",
                        pending.PlayerName,
                        clientId,
                        trigger);
                }

                return;
            }

            if (Interlocked.Exchange(ref pending.Sent, 1) != 0)
            {
                return; // 只发一次
            }

            try
            {
                // The process hash is part of the frame and can only be learned from the
                // player's own reports, so without wire info there is nothing valid to send.
                if (!Reconnect.TryGetWireInfoForClient(pending.OldClientId, out var wire))
                {
                    _logger.LogWarning(
                        "Reconnect: no mod wire info yet, cannot send the resume signal for {Name} (client {ClientId}, trigger {Trigger}).",
                        pending.PlayerName,
                        clientId,
                        trigger);
                    _pendingResumeSignals.TryRemove(clientId, out _);
                    return;
                }

                // 载体 = 接收者自己的角色, 这是唯一同时满足
                // "接收端本地存在" 与 "类型是 PlayerControl" 的对象。
                var targetNetId = current.Character.NetId;
                var meetingSince = MeetingHappenedSince(pending.DisconnectedAt);

                using (var writer = StartRpc(targetNetId, (RpcCalls)NebulaResumeRpc.RpcId, clientId))
                {
                    writer.Write(wire.ProcessHash);

                    // count = 后面跟的 int 个数。本帧是 [KindResume, playerId, 掉线后开过会] 三个,
                    // 写大了客户端会越过消息末尾读垃圾。旧客户端只读前两个, 第三个被忽略。
                    writer.Write(3);
                    writer.Write(NebulaResumeRpc.KindResume);
                    writer.Write((int)pending.PlayerId);
                    writer.Write(meetingSince ? 1 : 0);
                    await FinishRpcAsync(writer, clientId);
                }

                _pendingResumeSignals.TryRemove(clientId, out _);

                _logger.LogInformation(
                    "Reconnect: {Name} 掉线后{Meeting}。",
                    pending.PlayerName,
                    meetingSince ? "开过会 → 散会后不回掉线处, 用会议桌默认位置" : "没开过会 → 恢复掉线位置");

                // 全场横幅: "名字 (好友代码) 重连回本局游戏。"
                //   会议中进来的人在进会议时已经提示过, 散会后的正式续局不再重复。
                if (!_rejoinNoticeSent.TryRemove(clientId, out _))
                {
                    await BroadcastRejoinNoticeAsync(targetNetId, wire.ProcessHash, pending.PlayerId, pending.PlayerName, pending.FriendCode);
                }

                _logger.LogInformation(
                    "Reconnect: sent resume signal for {Name} (playerId {PlayerId}) to client {ClientId} via netId {NetId} (trigger: {Trigger}).",
                    pending.PlayerName,
                    pending.PlayerId,
                    clientId,
                    targetNetId,
                    trigger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect: failed to send the resume signal.");
            }
        }

        /// <summary>
        ///     掉线之后是否开过会: 掉线后有会议开始, 或掉线时会议正在进行(之后才结束), 或现在仍在会议窗口。
        /// </summary>
        private bool MeetingHappenedSince(DateTimeOffset disconnectedAt)
        {
            if (disconnectedAt == default)
            {
                return false;
            }

            var disc = disconnectedAt.UtcTicks;
            return _meetingHudAlive
                || Interlocked.Read(ref _meetingOpenedAtTicks) >= disc
                || Interlocked.Read(ref _meetingClosedAtTicks) >= disc
                || Interlocked.Read(ref _meetingFlowEndedAtTicks) >= disc;
        }

        /// <summary>
        ///     向全场广播重连横幅 kind=16: <c>[16, playerId, 名字长度, 名字..., 好友代码长度, 好友代码...]</c>。
        ///     载体 = 重连者自己的角色(每个客户端本地都有)。字符串按 UTF-16 码元逐个写成 int。
        /// </summary>
        /// <summary>已经在会议中提示过重连横幅的连接(散会后的续局不再重复)。</summary>
        private readonly ConcurrentDictionary<int, byte> _rejoinNoticeSent = new();

        private async ValueTask BroadcastRejoinNoticeAsync(uint carrierNetId, int processHash, byte playerId, string playerName, string friendCode)
        {
            try
            {
                var name = Clip(playerName, 32);
                var code = Clip(friendCode, 32);

                using (var writer = StartRpc(carrierNetId, (RpcCalls)NebulaResumeRpc.RpcId))
                {
                    writer.Write(processHash);
                    writer.Write(4 + name.Length + code.Length);
                    writer.Write(NebulaResumeRpc.KindRejoinNotice);
                    writer.Write((int)playerId);
                    writer.Write(name.Length);
                    foreach (var c in name)
                    {
                        writer.Write((int)c);
                    }

                    writer.Write(code.Length);
                    foreach (var c in code)
                    {
                        writer.Write((int)c);
                    }

                    await FinishRpcAsync(writer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect: 重连横幅广播失败 ({Name})。", playerName);
            }

            static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length > max ? s.Substring(0, max) : s);
        }

        /// <summary>
        ///     Sends the queued mod state to a client, once. Called right after the
        ///     client's own world sync (SceneChangeFlag) and, as a fallback, on a short
        ///     timer. Sending twice is harmless but pointless, so only the first wins.
        /// </summary>
        internal async ValueTask FlushPendingModStateRestoreAsync(int clientId, string trigger = "scene")
        {
            // 先推续局信号, 再推状态包: 客户端要先把 UI 切到对局中, 才能真正接收并应用
            // 模组状态; 反过来的话状态会作用在半初始化的对象上。
            await FlushPendingResumeSignalAsync(clientId, trigger);

            if (!_pendingModStateRestores.TryGetValue(clientId, out var pending))
            {
                return;
            }

            // 载体同续局信号: 必须是接收端本地存在的 PlayerControl。
            // 身体还没就位就推迟, 且不标记 Sent, 等"角色就位"的触发点再来一次。
            if (!_players.TryGetValue(clientId, out var player) || player.Character == null)
            {
                if (ScheduleFrameRetry(clientId))
                {
                    _logger.LogInformation(
                        "Reconnect: deferring mod frames for {Name} (no character yet).",
                        pending.PlayerName);
                }
                else
                {
                    _logger.LogDebug(
                        "Reconnect: still deferring mod frames for {Name} (no character yet).",
                        pending.PlayerName);
                }

                return;
            }

            if (Interlocked.Exchange(ref pending.Sent, 1) != 0)
            {
                return;
            }

            try
            {
                if (!Reconnect.TryGetWireInfoForClient(pending.OldClientId, out var wire))
                {
                    _logger.LogWarning(
                        "Reconnect: no mod wire info yet, cannot restore mod state for {Name} (client {ClientId}, trigger {Trigger}).",
                        pending.PlayerName,
                        clientId,
                        trigger);
                    return;
                }

                // Frame layout (must match the client's NebulaStateWire):
                //   [processHash:int32][count:int32][ KindRestore, playerId, ...stateChunks ]
                // The hash is the one the mod itself used - the server cannot compute it.
                var chunkCount = pending.Blob.Length / sizeof(int);
                var frameLength = 2 + chunkCount;

                // 载体 = 接收者自己的角色: 只有它同时满足"接收端本地存在"与
                // "类型是 PlayerControl"(客户端模组的自定义 RPC 分发器只挂在
                // PlayerControl.HandleRpc 上)。用别的 netId 都会被客户端静默丢弃。
                var targetNetId = player.Character!.NetId;

                using (var writer = StartRpc(targetNetId, (RpcCalls)NebulaResumeRpc.RpcId, clientId))
                {
                    writer.Write(wire.ProcessHash);
                    writer.Write(frameLength);
                    writer.Write(NebulaResumeRpc.KindRestore);
                    writer.Write((int)pending.PlayerId);

                    var chunks = new int[chunkCount];
                    Buffer.BlockCopy(pending.Blob, 0, chunks, 0, pending.Blob.Length);
                    for (var i = 0; i < chunks.Length; i++)
                    {
                        writer.Write(chunks[i]);
                    }

                    await FinishRpcAsync(writer, clientId);
                }

                _pendingModStateRestores.TryRemove(clientId, out _);

                _logger.LogInformation(
                    "Reconnect: sent mod-state restore for {Name} (playerId {PlayerId}, {Bytes} bytes) to client {ClientId} via netId {NetId} (trigger: {Trigger}).",
                    pending.PlayerName,
                    pending.PlayerId,
                    pending.Blob.Length,
                    clientId,
                    targetNetId,
                    trigger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect: failed to send mod-state restore to client {ClientId}.", clientId);
            }
        }
    }
}





