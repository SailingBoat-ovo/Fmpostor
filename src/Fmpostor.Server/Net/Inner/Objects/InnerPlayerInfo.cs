using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Innersloth.Customization;
using Fmpostor.Api.Innersloth.GameOptions;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Custom;
using Fmpostor.Api.Net.Inner;
using Fmpostor.Api.Net.Messages.Rpcs;
using Fmpostor.Api.Utils;
using Fmpostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.Inner.Objects
{
    internal partial class InnerPlayerInfo
    {
        private readonly IEventManager _eventManager;
        private readonly ILogger<InnerPlayerInfo> _logger;

        public InnerPlayerInfo(ICustomMessageManager<ICustomRpc> customMessageManager, IEventManager eventManager, Game game, ILogger<InnerPlayerInfo> logger) : base(customMessageManager, game)
        {
            Components.Add(this);
            _eventManager = eventManager;
            _logger = logger;
        }

        public InnerPlayerControl? Controller { get; internal set; }

        public byte PlayerId { get; internal set; }

        public int ClientId { get; internal set; }

        public string PlayerName => CurrentOutfit.PlayerName;

        public Dictionary<PlayerOutfitType, PlayerOutfit> Outfits { get; } = new()
        {
            [PlayerOutfitType.Default] = new PlayerOutfit(),
        };

        public PlayerOutfitType CurrentOutfitType { get; set; } = PlayerOutfitType.Default;

        public PlayerOutfit CurrentOutfit => Outfits[CurrentOutfitType];

        public RoleTypes? RoleType { get; internal set; }

        public RoleTypes? RoleWhenAlive { get; internal set; }

        public bool Disconnected { get; internal set; }

        public bool IsImpostor => RoleType is RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.ImpostorGhost or RoleTypes.Phantom;

        public bool CanVent => RoleType is RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.Phantom or RoleTypes.Engineer;

        public bool IsDead { get; internal set; }

        public DeathReason LastDeathReason { get; internal set; }

        public List<TaskInfo> Tasks { get; internal set; } = new List<TaskInfo>(0);

        public DateTimeOffset LastMurder { get; set; }

        public uint PlayerLevel { get; internal set; }

        /// <summary>
        ///     Friend code recorded for this player info (set when the server spawns it, or
        ///     taken from the host's data when the host owns the object).
        /// </summary>
        public string? FriendCode { get; internal set; }

        /// <summary>PUID recorded for this player info, same rules as <see cref="FriendCode" />.</summary>
        public string? Puid { get; internal set; }

        /// <summary>
        ///     The identifiers written into the player info that clients receive.
        /// </summary>
        /// <remarks>
        ///     游戏大厅右上角的社交面板(手机按钮)显示的好友代码, 来自这个对象里的 FriendCode。
        ///     原版客户端在服务端权威模式下, 这个对象由服务端生成并序列化 —— 以前这里固定写空串,
        ///     所以任何人都看不到别人的代码。
        ///
        ///     优先用该玩家连接上服务端已知的身份(登录时换取的真实代码); 连接已不在
        ///     (例如重连后的旧身份)时退回本对象记录的值。占位代码不下发。
        /// </remarks>
        private (string FriendCode, string Puid) ResolveIdentifiers()
        {
            var owner = Game.GetClientPlayer(ClientId);

            var friendCode = owner?.Client.FriendCode;
            if (string.IsNullOrWhiteSpace(friendCode))
            {
                friendCode = FriendCode;
            }

            var puid = owner?.Client.Puid;
            if (string.IsNullOrWhiteSpace(puid))
            {
                puid = Puid;
            }

            return (FriendCodeFilter.ForClients(friendCode), puid?.Trim() ?? string.Empty);
        }

        public bool CanMurder(IGame game, IDateTimeProvider dateTimeProvider)
        {
            if (!IsImpostor)
            {
                return false;
            }

            // In 2021.11.9 the Guardian Angel was created, which was unfortunately implemented in such a way that
            // it is hard to determine accurately whether a kill was prevented or not. When a kill was prevented,
            // the impostor has a cooldown of half the usual duration. As a workaround we always assume the kill was
            // prevented and the impostor only has half of its cooldown.
            // FIXME when the base game improved their implementation.
            if (game.Options.GameMode is GameModes.Normal or GameModes.NormalFools)
            {
                var options = (NormalGameOptions)game.Options;
                return dateTimeProvider.UtcNow.Subtract(LastMurder).TotalSeconds >= options.KillCooldown / 2;
            }
            else
            {
                return true;
            }
        }

        public override ValueTask<bool> SerializeAsync(IMessageWriter writer, bool initialState)
        {
            writer.Write(PlayerId);
            writer.WritePacked(ClientId);

            writer.Write((byte)Outfits.Count);
            foreach (var outfit in Outfits)
            {
                writer.Write((byte)outfit.Key);
                outfit.Value.Serialize(writer);
            }

            writer.WritePacked(PlayerLevel);

            var flag = 0;
            if (Disconnected)
            {
                flag = (byte)(flag | 1u);
            }

            if (IsDead)
            {
                flag = (byte)(flag | 4u);
            }

            writer.Write((byte)flag);

            writer.Write((ushort)(RoleType ?? 0));
            writer.Write(RoleWhenAlive.HasValue);
            if (RoleWhenAlive.HasValue)
            {
                writer.Write((ushort)RoleWhenAlive.Value);
            }

            writer.Write((byte)Tasks.Count);
            for (var i = 0; i < Tasks.Count; i++)
            {
                Tasks[i].Serialize(writer);
            }

            var (friendCode, puid) = ResolveIdentifiers();
            writer.Write(friendCode);
            writer.Write(puid);
            return new ValueTask<bool>(true);
        }

        public override ValueTask DeserializeAsync(IClientPlayer sender, IClientPlayer? target, IMessageReader reader, bool initialState)
        {
            PlayerId = reader.ReadByte();
            ClientId = reader.ReadPackedInt32();

            Outfits.Clear();
            var b = reader.ReadByte();
            for (var i = 0; i < b; i++)
            {
                var key = (PlayerOutfitType)reader.ReadByte();
                Outfits[key] = new PlayerOutfit();
                Outfits[key].Deserialize(reader);
            }

            PlayerLevel = reader.ReadPackedUInt32();

            var flag = reader.ReadByte();
            Disconnected = (flag & 1) != 0;
            IsDead = (flag & 4) != 0;

            // Ignore the RoleType here and only trust the SetRole RPC, as
            // RoleType is not nullable in vanilla, while Fmpostor checks game
            // starts based on assigned roles.
            _ = (RoleTypes)reader.ReadUInt16();

            if (reader.ReadBoolean())
            {
                RoleWhenAlive = (RoleTypes)reader.ReadUInt16();
            }

            var taskCount = reader.ReadByte();

            if (Tasks.Count < taskCount)
            {
                taskCount = (byte)Tasks.Count;
            }

            for (var i = 0; i < taskCount; i++)
            {
                Tasks[i].Deserialize(reader);
            }

            // 房主拥有这个对象时, 它声明的代码只在服务端自己不知道时才采用:
            // 服务端登录时换到的身份才是权威值, 房主的声明不能覆盖它。
            var claimedFriendCode = reader.ReadString();
            var claimedPuid = reader.ReadString();

            if (string.IsNullOrWhiteSpace(FriendCode) && !FriendCodeFilter.IsPlaceholder(claimedFriendCode))
            {
                FriendCode = claimedFriendCode;
            }

            if (string.IsNullOrWhiteSpace(Puid) && !string.IsNullOrWhiteSpace(claimedPuid))
            {
                Puid = claimedPuid;
            }

            return ValueTask.CompletedTask;
        }

        public override async ValueTask<bool> HandleRpcAsync(ClientPlayer sender, ClientPlayer? target, RpcCalls call, IMessageReader reader)
        {
            switch (call)
            {
                case RpcCalls.SetTasks:
                    Rpc29SetTasks.Deserialize(reader, out var taskTypeIds);
                    SetTasks(taskTypeIds);
                    break;

                default:
                    return await base.HandleRpcAsync(sender, target, call, reader);
            }

            return true;
        }

        private void SetTasks(ReadOnlyMemory<byte> taskTypeIds)
        {
            if (Disconnected)
            {
                return;
            }

            Tasks = new List<TaskInfo>(taskTypeIds.Length);

            var taskId = 0u;
            foreach (var taskTypeId in taskTypeIds.Span)
            {
                var mapTasks = Game.GameNet!.ShipStatus?.Data.Tasks;
                var taskType = (mapTasks != null && mapTasks.ContainsKey(taskTypeId)) ? mapTasks[taskTypeId] : null;
                Tasks.Add(new TaskInfo(this, _eventManager, taskId++, taskType));
            }
        }

        public byte GetNextRpcSequenceId(RpcCalls rpc)
        {
            var defaultOutfit = this.Outfits[PlayerOutfitType.Default];

            byte nextRpcSequenceId = 0;
            const byte SequenceIdAddition = 5;

            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch (rpc)
            {
                case RpcCalls.SetHatStr:
                    defaultOutfit.HatSequenceId += SequenceIdAddition;
                    nextRpcSequenceId = defaultOutfit.HatSequenceId;
                    break;
                case RpcCalls.SetSkinStr:
                    defaultOutfit.SkinSequenceId += SequenceIdAddition;
                    nextRpcSequenceId = defaultOutfit.SkinSequenceId;
                    break;
                case RpcCalls.SetPetStr:
                    defaultOutfit.PetSequenceId += SequenceIdAddition;
                    nextRpcSequenceId = defaultOutfit.PetSequenceId;
                    break;
                case RpcCalls.SetVisorStr:
                    defaultOutfit.VisorSequenceId += SequenceIdAddition;
                    nextRpcSequenceId = defaultOutfit.VisorSequenceId;
                    break;
                case RpcCalls.SetNamePlateStr:
                    defaultOutfit.NamePlateSequenceId += SequenceIdAddition;
                    nextRpcSequenceId = defaultOutfit.NamePlateSequenceId;
                    break;
                default:
                    _logger.LogError(
                        "{NextRpcSequenceIdName} was called for an RPC it doesn\'t know how to handle: {Rpc}",
                        nameof(GetNextRpcSequenceId),
                        rpc);
                    break;
            }

            return nextRpcSequenceId;
        }
    }
}
