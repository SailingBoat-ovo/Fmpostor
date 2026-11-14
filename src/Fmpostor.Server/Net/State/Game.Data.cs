using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fmpostor.Api;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net.Inner;
using Fmpostor.Api.Unity;
using Fmpostor.Server.Events.Meeting;
using Fmpostor.Server.Events.Player;
using Fmpostor.Server.Net.Inner;
using Fmpostor.Server.Net.Inner.Objects;
using Fmpostor.Server.Net.Inner.Objects.Components;
using Fmpostor.Server.Net.Inner.Objects.GameManager;
using Fmpostor.Server.Net.Inner.Objects.ShipStatus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.State
{
    internal partial class Game
    {
        /// <summary>
        ///     Used for global object, spawned by the host.
        /// </summary>
        private const int InvalidClient = -2;

        /// <summary>
        ///     Used internally to set the OwnerId to the current ClientId.
        ///     i.e: <code>ownerId = ownerId == -3 ? this.ClientId : ownerId;</code>
        /// </summary>
        private const int CurrentClient = -3;

        /// <summary>
        ///     Used to list objects that are managed by the game server.
        /// </summary>
        private const int ServerOwned = -4;

        /// <summary>
        ///     The first NetId that is considered as a server owned Network ID that the client will not allocate by default.
        /// </summary>
        private const int MinServerNetId = 100000;

        private static readonly Dictionary<uint, Type> SpawnableObjects = new()
        {
            [0] = typeof(InnerSkeldShipStatus),
            [1] = typeof(InnerMeetingHud),
            [2] = typeof(InnerLobbyBehaviour),
            [4] = typeof(InnerPlayerControl),
            [5] = typeof(InnerMiraShipStatus),
            [6] = typeof(InnerPolusShipStatus),
            [7] = typeof(InnerDleksShipStatus),
            [8] = typeof(InnerAirshipStatus),
            [9] = typeof(InnerHideAndSeekManager),
            [10] = typeof(InnerNormalGameManager),
            [11] = typeof(InnerPlayerInfo),
            [12] = typeof(InnerVoteBanSystem),
            [13] = typeof(InnerFungleShipStatus),
        };

        private static readonly Dictionary<Type, uint> SpawnableObjectIds = SpawnableObjects.ToDictionary((i) => i.Value, (i) => i.Key);

        private readonly ConcurrentDictionary<uint, InnerNetObject> _allObjects = new ConcurrentDictionary<uint, InnerNetObject>();

        private uint _nextNetId = MinServerNetId;

        public T? FindObjectByNetId<T>(uint netId)
            where T : IInnerNetObject
        {
            if (_allObjects.TryGetValue(netId, out var obj))
            {
                return (T)(IInnerNetObject)obj;
            }

            return default;
        }

        public async ValueTask<bool> HandleGameDataAsync(IMessageReader parent, ClientPlayer sender, bool toPlayer)
        {
            // Find target player.
            ClientPlayer? target = null;

            if (toPlayer)
            {
                var targetId = parent.ReadPackedInt32();
                if (!TryGetPlayer(targetId, out target))
                {
                    _logger.LogWarning("Player {0} tried to send GameData to unknown player {1}.", sender.Client.Id, targetId);
                    return false;
                }

                _logger.LogTrace("Received GameData for target {0}.", targetId);
            }

            // Parse GameData messages.
            while (parent.Position < parent.Length)
            {
                using var reader = parent.ReadMessage();

                switch (reader.Tag)
                {
                    case GameDataTag.DataFlag:
                    {
                        var netId = reader.ReadPackedUInt32();
                        if (_allObjects.TryGetValue(netId, out var obj))
                        {
                            await obj.DeserializeAsync(sender, target, reader, false);
                        }
                        else
                        {
                            _logger.LogWarning("Received DataFlag for unregistered NetId {0}.", netId);
                        }

                        break;
                    }

                    case GameDataTag.RpcFlag:
                    {
                        var netId = reader.ReadPackedUInt32();
                        if (_allObjects.TryGetValue(netId, out var obj))
                        {
                            if (!await obj.HandleRpcAsync(sender, target, (RpcCalls)reader.ReadByte(), reader))
                            {
                                parent.RemoveMessage(reader);
                                continue;
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Received RpcFlag for unregistered NetId {0}.", netId);
                        }

                        break;
                    }

                    case GameDataTag.SpawnFlag:
                    {
                        // Only the host is allowed to spawn objects.
                        if (!sender.IsHost)
                        {
                            if (await sender.Client.ReportCheatAsync(new CheatContext(nameof(GameDataTag.SpawnFlag)), CheatCategory.MustBeHost, "Tried to send SpawnFlag as non-host."))
                            {
                                return false;
                            }
                        }

                        var objectId = reader.ReadPackedUInt32();
                        if (SpawnableObjects.TryGetValue(objectId, out var spawnableObjectType))
                        {
                            var innerNetObject = (InnerNetObject)ActivatorUtilities.CreateInstance(_serviceProvider, spawnableObjectType, this);
                            var ownerClientId = reader.ReadPackedInt32();

                            innerNetObject.SpawnFlags = (SpawnFlags)reader.ReadByte();

                            var components = innerNetObject.GetComponentsInChildren<InnerNetObject>();
                            var componentsCount = reader.ReadPackedInt32();

                            if (componentsCount != components.Count)
                            {
                                _logger.LogError(
                                    "Children didn't match for spawnable {0}, name {1} ({2} != {3})",
                                    objectId,
                                    innerNetObject.GetType().Name,
                                    componentsCount,
                                    components.Count);
                                continue;
                            }

                            _logger.LogDebug(
                                "Spawning {0} components, SpawnFlags {1}",
                                innerNetObject.GetType().Name,
                                innerNetObject.SpawnFlags);

                            for (var i = 0; i < componentsCount; i++)
                            {
                                var obj = components[i];

                                obj.NetId = reader.ReadPackedUInt32();
                                obj.OwnerId = ownerClientId;

                                _logger.LogDebug(
                                    "- {0}, NetId {1}, OwnerId {2}",
                                    obj.GetType().Name,
                                    obj.NetId,
                                    obj.OwnerId);

                                if (!AddNetObject(obj))
                                {
                                    // 这一个失败会把对象标成 uint.MaxValue, 之后针对它的
                                    // 所有 RPC 都找不到目标 —— 包括房主的 SetRole, 而对局
                                    // 只有处理到 SetRole 才会从 Starting 进 Started。
                                    // 所以这条必须能在默认日志级别看到。
                                    _logger.LogWarning(
                                        "{Code} - Failed to register {Type} NetId {NetId} (owner {OwnerId}): it already exists.",
                                        Code.Code,
                                        obj.GetType().Name,
                                        obj.NetId,
                                        obj.OwnerId);

                                    obj.NetId = uint.MaxValue;
                                    break;
                                }

                                using var readerSub = reader.ReadMessage();
                                if (readerSub.Length > 0)
                                {
                                    await obj.DeserializeAsync(sender, target, readerSub, true);
                                }

                                await OnSpawnAsync(sender, obj);
                            }

                            continue;
                        }

                        _logger.LogWarning("Couldn't find spawnable object {0}.", objectId);
                        break;
                    }

                    // Only the host is allowed to despawn objects.
                    case GameDataTag.DespawnFlag:
                    {
                        var netId = reader.ReadPackedUInt32();
                        if (_allObjects.TryGetValue(netId, out var obj))
                        {
                            if (sender.Client.Id != obj.OwnerId && !sender.IsHost)
                            {
                                _logger.LogWarning(
                                    "Player {0} ({1}) tried to send DespawnFlag for {2} but was denied.",
                                    sender.Client.Name,
                                    sender.Client.Id,
                                    netId);
                                return false;
                            }

                            RemoveNetObject(obj);
                            await OnDestroyAsync(obj);
                            _logger.LogDebug("Destroyed InnerNetObject {0} ({1}), OwnerId {2}", obj.GetType().Name, netId, obj.OwnerId);
                        }
                        else
                        {
                            _logger.LogDebug(
                                "Player {0} ({1}) sent DespawnFlag for unregistered NetId {2}.",
                                sender.Client.Name,
                                sender.Client.Id,
                                netId);
                        }

                        break;
                    }

                    case GameDataTag.SceneChangeFlag:
                    {
                        // Sender is only allowed to change his own scene.
                        var clientId = reader.ReadPackedInt32();
                        var scene = reader.ReadString();

                        if (clientId != sender.Client.Id)
                        {
                            _logger.LogWarning(
                                "Player {0} ({1}) tried to send SceneChangeFlag for another player.",
                                sender.Client.Name,
                                sender.Client.Id);
                            return false;
                        }

                        // According to game assembly, sender is only allowed to send OnlineGame.
                        if (scene != "OnlineGame")
                        {
                            _logger.LogWarning(
                                "Player {PlayerName} ({ClientId}) tried to send SceneChangeFlag with disallowed scene \"{Scene}\".",
                                sender.Client.Name,
                                sender.Client.Id,
                                scene);
                            return false;
                        }

                        sender.Scene = scene;

                        _logger.LogTrace("> Scene {0} to {1}", clientId, sender.Scene);

                        await SyncServerObjectsAsync(sender);
                        await SpawnPlayerInfoAsync(sender);

                        // 让重连者(以及任何中途加入的连接)拿到完整的玩家资料。
                        //
                        // 开启主机权威时 InnerPlayerInfo 由房主客户端生成(OwnerId = 房主),
                        // 而 SyncServerObjectsAsync 只补发【服务端自有】对象 —— 这些资料因此
                        // 不会被发出去; 客户端拿不到资料, 会议界面就只能显示没有名字/颜色的
                        // 默认小人(投票格全是"无信息小绿人")。
                        // InnerPlayerInfo.SerializeAsync 是本类已实现的(与角色对象不同),
                        // 可以安全补发; 客户端对已存在的 NetId 有重复生成保护, 重复发送无害。
                        var playerInfoCount = 0;

                        foreach (var obj in _allObjects.Values)
                        {
                            if (obj is InnerPlayerInfo info)
                            {
                                await SendObjectSpawnAsync(info, sender.Client.Id);
                                playerInfoCount++;
                            }
                        }

                        // Reconnect: the resumed client has now been given the world,
                        // which is the first moment a mod-state restore frame can be
                        // addressed to one of its objects.
                        _logger.LogInformation(
                            "[PlayerInfo] 已向 clientId {ClientId} 补发 {Count} 条玩家资料。",
                            sender.Client.Id,
                            playerInfoCount);

                        await FlushPendingModStateRestoreAsync(sender.Client.Id);

                        // 观战者: 到这里房主已经建好它的 PlayerInfo, 补生成指令(kind=4)
                        // 只有此刻发出才能被正确处理 —— 准入时发太早, 会拿不到身体。
                        await OnSpectatorSceneReadyAsync(sender);

                        break;
                    }

                    case GameDataTag.ReadyFlag:
                    {
                        var clientId = reader.ReadPackedInt32();

                        if (clientId != sender.Client.Id)
                        {
                            _logger.LogWarning(
                                "Player {0} ({1}) tried to send ReadyFlag for another player.",
                                sender.Client.Name,
                                sender.Client.Id);
                            return false;
                        }

                        _logger.LogTrace("> IsReady {0}", clientId);
                        break;
                    }

                    case GameDataTag.ConsoleDeclareClientPlatformFlag:
                    {
                        var clientId = reader.ReadPackedInt32();
                        var platform = (RuntimePlatform)reader.ReadPackedInt32();

                        if (clientId != sender.Client.Id)
                        {
                            if (await sender.Client.ReportCheatAsync(new CheatContext(nameof(GameDataTag.ConsoleDeclareClientPlatformFlag)), CheatCategory.Ownership, "Client sent info with wrong client id"))
                            {
                                return false;
                            }
                        }

                        sender.Platform = platform;

                        break;
                    }

                    default:
                    {
                        _logger.LogWarning("Bad GameData tag {0}", reader.Tag);
                        break;
                    }
                }

                if (sender.Client.Player == null)
                {
                    // Disconnect handler was probably invoked, cancel the rest.
                    return false;
                }
            }

            return true;
        }

        private async ValueTask OnSpawnAsync(ClientPlayer sender, InnerNetObject netObj)
        {
            switch (netObj)
            {
                case InnerGameManager innerGameManager:
                {
                    GameNet.GameManager = innerGameManager;
                    break;
                }

                case InnerLobbyBehaviour lobby:
                {
                    GameNet.LobbyBehaviour = lobby;
                    break;
                }

                case InnerPlayerInfo playerInfo:
                {
                    if (!GameNet.GameData.AddPlayer(playerInfo))
                    {
                        _logger.LogWarning(
                            "Could not add PlayerInfo for playerId {PlayerId} with NetId {newId}, already have NetId {oldNetId}",
                            playerInfo.PlayerId,
                            playerInfo.NetId,
                            GameNet.GameData.GetPlayerById(playerInfo.PlayerId)?.NetId);
                    }

                    break;
                }

                case InnerVoteBanSystem voteBan:
                {
                    GameNet.VoteBan = voteBan;
                    break;
                }

                case InnerShipStatus shipStatus:
                {
                    GameNet.ShipStatus = shipStatus;
                    break;
                }

                case InnerPlayerControl control:
                {
                    // Hook up InnerPlayerControl <-> IClientPlayer.
                    if (TryGetPlayer(control.OwnerId, out var player))
                    {
                        player.Character = control;
                        player.DisableSpawnTimeout();

                        // 角色就位 —— 重连帧(续局信号/状态包)要以这个 PlayerControl 为载体。
                        //
                        // ⚠️ 不能在这里立刻发: 此刻服务端还在处理房主发来的生成消息,
                        //   这条生成消息要等本方法返回后才会转发给其他客户端(包括重连者本人)。
                        //   立刻发出去的帧会比"生成"先到, 重连者本地还没有这个对象, 帧被静默丢弃,
                        //   而服务端已经把它记成"已发送"、不会再发 —— 表现为"服务器没有找到你的
                        //   保留位置、职业和任务都没恢复"。所以稍等片刻, 让生成消息先送达。
                        var readyClientId = control.OwnerId;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(CharacterReadyDelayMs);
                                await OnCharacterReadyAsync(readyClientId, "character");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Reconnect: character-ready hand-off failed for client {ClientId}.", readyClientId);
                            }
                        });
                    }
                    else
                    {
                        await sender.Client.ReportCheatAsync(new CheatContext(nameof(GameDataTag.SpawnFlag)), CheatCategory.GameFlow, "Failed to find player that spawned the InnerPlayerControl");
                    }

                    // Hook up InnerPlayerControl <-> InnerPlayerControl.PlayerInfo.
                    var playerInfo = GameNet.GameData.GetPlayerById(control.PlayerId);

                    if (playerInfo != null)
                    {
                        playerInfo.Controller = control;
                        control.PlayerInfo = playerInfo;
                    }

                    if (player != null)
                    {
                        await _eventManager.CallAsync(new PlayerSpawnedEvent(this, player, control));
                    }

                    break;
                }

                case InnerMeetingHud meetingHud:
                {
                    // 会议开始: 关闭对局中途加入的入口(见 Game.MeetingGate.cs)。
                    NoteMeetingHudSpawned(meetingHud);

                    // Character is NULLABLE here - the '!' that used to be on this line
                    // was a latent server crash. A connection can legitimately be in the
                    // room without a body yet (a reconnecting player whose slot is
                    // reserved). Meeting start walks EVERY player, so one body-less
                    // connection took the whole server down.
                    foreach (var player in _players.Values)
                    {
                        var character = player.Character;
                        if (character == null || GameNet.ShipStatus == null)
                        {
                            continue;
                        }

                        await character.NetworkTransform.SetPositionAsync(
                            player,
                            GameNet.ShipStatus.GetSpawnLocation(character, PlayerCount, false));
                    }

                    await _eventManager.CallAsync(new MeetingStartedEvent(this, meetingHud));
                    break;
                }
            }

            await netObj.OnSpawnAsync();
        }

        private async ValueTask OnDestroyAsync(InnerNetObject netObj)
        {
            switch (netObj)
            {
                case InnerMeetingHud destroyedHud:
                {
                    // 会议界面关闭(放逐动画随即开始): 缓冲期过后重新放行中途加入。
                    NoteMeetingHudDestroyed(destroyedHud);
                    break;
                }

                case InnerLobbyBehaviour:
                {
                    GameNet.LobbyBehaviour = null;
                    break;
                }

                case InnerVoteBanSystem:
                {
                    GameNet.VoteBan = null;
                    break;
                }

                case InnerShipStatus:
                {
                    GameNet.ShipStatus = null;
                    break;
                }

                case InnerPlayerInfo playerInfo:
                {
                    if (GameState != GameStates.Started && GameState != GameStates.Starting)
                    {
                        GameNet.GameData.RemovePlayer(playerInfo.PlayerId);
                    }

                    break;
                }

                case InnerPlayerControl control:
                {
                    // Remove InnerPlayerControl <-> IClientPlayer.
                    if (TryGetPlayer(control.OwnerId, out var player))
                    {
                        player.Character = null;
                        await _eventManager.CallAsync(new PlayerDestroyedEvent(this, player, control));
                    }

                    break;
                }
            }
        }

        private async ValueTask SyncServerObjectsAsync(ClientPlayer sender)
        {
            foreach (var obj in _allObjects.Values)
            {
                if (obj.OwnerId == ServerOwned)
                {
                    _logger.LogTrace("Syncing {Type} {NetId}", obj.GetType(), obj.NetId);
                    await SendObjectSpawnAsync(obj, sender.Client.Id);
                }
            }
        }

        private async ValueTask SpawnPlayerInfoAsync(ClientPlayer sender)
        {
            // Hosts spawn PlayerInfo objects if they requested authority
            if (IsHostAuthoritive)
            {
                return;
            }

            // Only spawn a new PlayerInfo if one has not yet been spawned
            if (GameNet.GameData.PlayersByClientId.ContainsKey(sender.Client.Id))
            {
                return;
            }

            var playerInfo = (InnerPlayerInfo)ActivatorUtilities.CreateInstance(_serviceProvider, typeof(InnerPlayerInfo), this);
            playerInfo.SpawnFlags = SpawnFlags.None;
            playerInfo.NetId = _nextNetId++;
            playerInfo.OwnerId = ServerOwned;
            playerInfo.ClientId = sender.Client.Id;
            playerInfo.FriendCode = sender.Client.FriendCode;
            playerInfo.Puid = sender.Client.Puid;
            playerInfo.PlayerId = GameNet.GameData.GetNextAvailablePlayerId();

            // If player played a previous game, restore their color
            var prevColor = sender.Client.PreviousColor;
            if (prevColor.HasValue)
            {
                _logger.LogTrace("Color restored to {Color}", prevColor.Value);
                playerInfo.CurrentOutfit.Color = prevColor.Value;
            }

            if (!AddNetObject(playerInfo))
            {
                _logger.LogError("Couldn't spawn PlayerInfo for {Name} ({ClientId})", sender.Client.Name, sender.Client.Id);
                playerInfo.NetId = uint.MaxValue;
                return;
            }

            _logger.LogTrace("Spawning PlayerInfo (netId {Netid})", playerInfo.NetId);
            await OnSpawnAsync(sender, playerInfo);
            await SendObjectSpawnAsync(playerInfo);
        }

        private async ValueTask DespawnPlayerInfoAsync(InnerPlayerInfo playerInfo)
        {
            if (playerInfo.OwnerId == ServerOwned)
            {
                _logger.LogDebug("Despawning PlayerInfo {nid}", playerInfo.NetId);
                GameNet.GameData.RemovePlayer(playerInfo.PlayerId);
                RemoveNetObject(playerInfo);

                await SendObjectDespawnAsync(playerInfo);
            }
        }

        private bool AddNetObject(InnerNetObject obj)
        {
            return _allObjects.TryAdd(obj.NetId, obj);
        }

        private void RemoveNetObject(InnerNetObject obj)
        {
            _allObjects.TryRemove(obj.NetId, out _);
        }
    }
}
