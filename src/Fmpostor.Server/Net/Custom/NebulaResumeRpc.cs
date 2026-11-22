using System;
using System.Threading.Tasks;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Custom;
using Fmpostor.Api.Net.Inner;
using Fmpostor.Server.Net.State;

namespace Fmpostor.Server.Net.Custom
{
    /// <summary>
    ///     Receiver for the mod-state protocol used by the disconnect-reconnect
    ///     feature (NebulaResume).
    ///
    ///     ── Why this exists ────────────────────────────────────────────────
    ///     Modded roles (NoS and similar) are invisible to the server: it only
    ///     ever learns the vanilla <c>RoleTypes</c> slot. The mod's real state is
    ///     exchanged between clients over its own RPC channel, so every in-room
    ///     client holds a full view of it - but the server has to be told in order
    ///     to restore it after a reconnect.
    ///
    ///     This is the single place where those reports arrive. The payload is
    ///     treated as an OPAQUE blob: never interpreted, only stored. That is what
    ///     keeps the server independent of the mod version.
    ///
    ///     ── CRITICAL: observe, never consume ───────────────────────────────
    ///     This handler is registered on the RPC id the mod itself uses, and
    ///     <see cref="Fmpostor.Server.Net.Inner.InnerNetObject.HandleRpcAsync"/>
    ///     consults the custom table FIRST. From now on this mod RPC therefore no
    ///     longer falls through to <c>UnregisteredCall</c> - which means the mod
    ///     stops being reported as a protocol-extension cheat. Two rules follow,
    ///     and breaking either one breaks the mod for everyone in the room:
    ///
    ///       1. The reader position MUST be restored before returning, because the
    ///          caller relays the remaining bytes to the other clients
    ///          (<c>Game.Data.cs</c> -> <c>packed.CopyTo(writer)</c>).
    ///       2. The handler MUST return true, otherwise the caller strips the
    ///          packet from the relay (<c>parent.RemoveMessage(reader)</c>).
    ///
    ///     ── How a packet is recognised as ours ─────────────────────────────
    ///     The channel multiplexes every mod RPC behind one id, each prefixed with
    ///     the constant hash of its name. Rather than recomputing that hash on the
    ///     server (it is a mod-internal extension method the server cannot call),
    ///     the frame is validated STRUCTURALLY:
    ///
    ///         [hash:int32][count:int32][count * int32]
    ///
    ///     which must consume the payload EXACTLY. A foreign RPC would have to
    ///     match that arithmetic by accident to be mistaken for ours.
    /// </summary>
    internal sealed class NebulaResumeRpc : ICustomRpc
    {
        /// <summary>
        ///     The RPC id used by NoS. A free slot in the vanilla
        ///     <c>RpcCalls</c> enum, shared by the whole mod protocol.
        /// </summary>
        public const byte RpcId = 128;

        /// <summary>Frame kind for a mod-state report (C→S).</summary>
        internal const int KindReport = 1;

        /// <summary>
        ///     Frame kind for a mod-state restore (S→C). Mirrors the value the client
        ///     plugin uses in its NebulaStateWire; both ends must agree.
        /// </summary>
        internal const int KindRestore = 2;

        /// <summary>
        ///     Frame kind for the resume signal (S→C). Tells the reconnecting client
        ///     that the room it just joined is the one it dropped out of, so it may
        ///     run its resume transition. Carries no payload - the mod state itself
        ///     arrives separately as <see cref="KindRestore" />, always after this.
        /// </summary>
        internal const int KindResume = 3;

        /// <summary>
        ///     Server -> all: rejoin banner, <c>[16, playerId, nameLen, name..., codeLen, code...]</c>.
        /// </summary>
        internal const int KindRejoinNotice = 16;

        /// <summary>
        ///     Frame kind for the spawn directive (S→host). Asks the host to create the
        ///     character for a resumed connection and carries the preserved player id,
        ///     which only the server knows. Payload:
        ///     <c>[KindSpawn, clientId, preservedPlayerId]</c>.
        /// </summary>
        internal const int KindSpawn = 4;

        /// <summary>
        ///     Frame kind for a ghost report (C→S): the resumed client says "I am dead,
        ///     record me". Payload is only <c>[KindGhostReport, playerId]</c>. Repeated
        ///     every few seconds by the client, so it is idempotent.
        /// </summary>
        internal const int KindGhostReport = 5;

        /// <summary>
        ///     Frame kind for the authoritative dead roster (S→all). Payload:
        ///     <c>[KindGhostRoster, N, clientId × N]</c>. A full roster, not an event:
        ///     applying it twice is harmless and a dropped packet self-heals on the next.
        /// </summary>
        internal const int KindGhostRoster = 6;

        /// <summary>
        ///     Frame kind telling one client whether it joined as a spectator (S→C):
        ///     <c>[KindSpectator, 1|0]</c>.
        /// </summary>
        internal const int KindSpectator = 7;

        /// <summary>
        ///     Frame kind for the authoritative spectator roster (S→all):
        ///     <c>[KindSpectatorRoster, N, clientId × N]</c>. A full roster, for the same
        ///     reasons as <see cref="KindGhostRoster" />.
        /// </summary>
        internal const int KindSpectatorRoster = 8;

        /// <summary>
        ///     Frame kind for the host's room capability declaration (C→S):
        ///     <c>[KindCapability, protocolVersion, flags, spectatorLimit]</c>.
        /// </summary>
        /// <remarks>
        ///     房主在大厅阶段就周期上报, 服务端因此能在有人尝试加入之前就知道这个房间
        ///     支不支持观战 —— 这是"绝不误放行原版房间"的前提。
        ///     只接受房主本人发来的声明。
        /// </remarks>
        internal const int KindCapability = 9;

        /// <summary>
        ///     Host -> all: meeting state, <c>[KindMeetingFlag, 0/1, timerMs, cooldownMs]</c>.
        ///     1 while the host still shows the meeting or the exile/respawn scene.
        ///     Older hosts send only two ints, which the size check above already skips.
        /// </summary>
        internal const int KindMeetingFlag = 10;

        /// <summary>
        ///     S→C: a reconnecter admitted while a meeting is running. Payload
        ///     <c>[KindMeetingSeat, oldGamePlayerId]</c>: until the meeting ends the client
        ///     votes on behalf of its previous identity (whose vote area is still alive).
        ///     The normal resume follows after the meeting.
        /// </summary>
        internal const int KindMeetingSeat = 13;

        /// <summary>Room capability protocol version understood by this server.</summary>
        internal const int ProtocolVersion = 1;

        /// <summary>Capability flag: the host's mod supports reconnect.</summary>
        internal const int CapabilityReconnect = 1;

        /// <summary>Capability flag: the host's mod supports joining a running round as a spectator.</summary>
        internal const int CapabilitySpectator = 2;

        /// <summary>
        ///     Capability flag: the host's mod copes with players arriving during a meeting
        ///     (the joiner sits the meeting out on its old identity's seat, resume follows after).
        ///     Bit 4 is intentionally skipped: an earlier, withdrawn experiment used it.
        /// </summary>
        internal const int CapabilityMeetingJoin = 8;

        /// <summary>
        ///     Sanity bounds for the declared int count. The real blob is a handful
        ///     of ints; anything outside this window is malformed or hostile.
        /// </summary>
        /// <summary>
        ///     Smallest frame we are willing to look at. A report carries at least
        ///     <c>[KindReport, playerId]</c> plus a little state, but the ghost report
        ///     (kind 5) is only <c>[kind, playerId]</c> - two ints - so the floor has
        ///     to allow that or every ghost report would be dropped as malformed.
        /// </summary>
        private const int MinFrameInts = 2;
        private const int MaxFrameInts = 512;

        public byte Id => RpcId;

        public ValueTask<bool> HandleRpcAsync(
            IInnerNetObject innerNetObject,
            IClientPlayer sender,
            IClientPlayer? target,
            IMessageReader reader)
        {
            // Rule 1: remember where the payload starts. Everything we do must be
            // invisible to the relay that runs after us.
            var start = reader.Position;

            try
            {
                Observe(innerNetObject, sender, reader);
            }
            catch (Exception)
            {
                // Our bookkeeping must never break the mod's own traffic.
            }
            finally
            {
                reader.Seek(start);
            }

            // Rule 2: always let the mod's packet through.
            return new ValueTask<bool>(true);
        }

        private static void Observe(IInnerNetObject innerNetObject, IClientPlayer sender, IMessageReader reader)
        {
            if (innerNetObject.Game is not Game game)
            {
                return;
            }

            var remaining = reader.Length - reader.Position;

            // Smallest frame we accept: hash + count + MinFrameInts ints of body.
            if (remaining < (2 + MinFrameInts) * sizeof(int))
            {
                return;
            }

            // ── [hash:int32] ── Not interpreted, but REMEMBERED: the server cannot
            // replicate the mod's process hash, so the value seen in a real report is
            // reused verbatim when a restore frame is sent back to a client.
            var processHash = reader.ReadInt32();

            // ── [count:int32] ──
            var count = reader.ReadInt32();
            if (count < MinFrameInts || count > MaxFrameInts)
            {
                return;
            }

            // ── Structural check: the frame must consume the payload exactly ──
            if (remaining != (2 + count) * sizeof(int))
            {
                return;
            }

            var frame = new int[count];
            for (var i = 0; i < count; i++)
            {
                frame[i] = reader.ReadInt32();
            }

            if (frame[0] == KindGhostReport)
            {
                // 身份取自【连接本身】, 不信客户端自报的 clientId —— 否则任何客户端
                // 都能把别人标成死者。
                game.RememberGhostReport(sender.Client.Id, sender.Client.Name);
                return;
            }

            if (frame[0] == KindCapability)
            {
                // [9, 协议版本, 能力标志, 观战上限]
                if (count < 4)
                {
                    return;
                }

                // 身份同样取自连接: 只有房主的声明才算数(由 Game 侧校验)。
                game.RememberRoomCapability(
                    sender.Client.Id,
                    sender.Client.Name,
                    frame[1],
                    frame[2],
                    frame[3]);
                return;
            }

            if (frame[0] == KindMeetingFlag)
            {
                // [10, 0/1, timerMs, cooldownMs] —— 房主每秒广播的会议状态。
                // 服务端只用它判断"散会流程(放逐动画、选出生点)何时真正结束";
                // 只认房主本人(由 Game 侧校验)。
                game.NoteHostMeetingFlag(sender.Client.Id, frame[1] != 0);
                return;
            }

            if (frame[0] != KindReport)
            {
                return;
            }

            var gamePlayerId = frame[1];
            if (gamePlayerId < 0 || gamePlayerId > byte.MaxValue)
            {
                return;
            }

            // ── Hand the opaque part over as bytes ──
            var chunkCount = count - 2;
            var blob = new byte[chunkCount * sizeof(int)];
            Buffer.BlockCopy(frame, 2 * sizeof(int), blob, 0, blob.Length);

            // Identity comes from the CONNECTION, never from the packet: a client
            // cannot impersonate another by lying in the payload.
            game.RememberModWireInfo(processHash, innerNetObject.NetId, sender.Client.Id);
            game.StoreReportedModState(sender.Client.Id, (byte)gamePlayerId, blob);
        }
    }
}
