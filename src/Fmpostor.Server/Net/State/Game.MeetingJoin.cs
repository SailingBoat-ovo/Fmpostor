using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner;
using Fmpostor.Api.Net.Messages;
using Fmpostor.Server.Net.Custom;
using Fmpostor.Server.Net.Reconnect;
using Impostor.Hazel;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.State
{
    /// <summary>
    ///     会议中进入: 有保槽的重连者(以及允许观战时的新玩家)在会议期间也能进房。
    /// </summary>
    /// <remarks>
    ///     只有房主的模组声明了 <see cref="NebulaResumeRpc.CapabilityMeetingJoin" /> 才启用;
    ///     否则维持原来的会议闸门(会议中拒绝、散会后开放)。
    ///
    ///     做法 —— 续局整体推迟到散会之后, 会议期间什么状态都不动:
    ///       · 进房、生成身体照常(由房主客户端负责, 与平时一样);
    ///       · 不移除旧身份、不消费保槽: 旧身份在所有人眼里一直在场, 它的投票格仍然有效;
    ///       · 告诉重连者旧身份的编号(<see cref="NebulaResumeRpc.KindMeetingSeat" />),
    ///         它在会议里以旧身份的名义投票 —— 房主按投票人编号找投票格计票;
    ///       · 会议(含放逐动画)结束后, 走与平时完全相同的续局流程。
    ///         这期间其他客户端仍替缺席的旧身份上报状态, 续局用的是最新的一份,
    ///         所以旧身份若在这场会议里被投出, 回来就是灵魂。
    ///     会议期间断线: 保槽原样还回去(踢出/封禁除外), 与"未确认的重连"规则一致。
    /// </remarks>
    internal partial class Game
    {
        private sealed class MeetingResume
        {
            public PendingSlot Slot { get; init; } = null!;

            public DateTimeOffset LastSeatSentAt;
        }

        /// <summary>会议中进来、等散会再续局的重连者: 新 clientId → 保槽。</summary>
        private readonly ConcurrentDictionary<int, MeetingResume> _meetingResumes = new();

        /// <summary>会议中进来、等散会再入座的观战者。</summary>
        private readonly ConcurrentDictionary<int, byte> _meetingSpectators = new();

        private int _meetingJoinLoopRunning;

        /// <summary>座位帧的重发间隔: 防丢包, 客户端处理是幂等的。</summary>
        private static readonly TimeSpan MeetingSeatResendInterval = TimeSpan.FromSeconds(3);

        /// <summary>房主的模组是否支持会议中进入。</summary>
        internal bool HostSupportsMeetingJoin => (_roomCapability & NebulaResumeRpc.CapabilityMeetingJoin) != 0;

        /// <summary>现在进来的对局中途加入, 是否要按"会议中进入"处理。</summary>
        private bool UseMeetingJoin => HostSupportsMeetingJoin && IsInMeetingWindow;

        /// <summary>
        ///     会议中的重连: 进房、等身体, 续局推迟到散会。
        /// </summary>
        private async ValueTask HandleJoinGameResumeInMeeting(ClientPlayer player, PendingSlot slot)
        {
            _logger.LogInformation(
                "{0} - Player {1} ({2}) reconnected during a meeting; resume of playerId {3} is deferred until the meeting ends.",
                Code,
                player.Client.Name,
                player.Client.Id,
                slot.Snapshot.PlayerId);

            player.Disconnected = false;

            await PlayerAdd(player);

            // 不挂生成超时: 房主可能要等散会才给身体(会议期间不生成), 这是有意的等待,
            // 不能因为"几秒内没身体"就把人踢掉。散会后的续局会照常让房主补生成。
            player.DisableSpawnTimeout();

            using (var message = MessageWriter.Get(MessageType.Reliable))
            {
                WriteJoinedGameMessage(message, false, player);
                WriteAlterGameMessage(message, false, IsPublic);

                player.Limbo = LimboStates.NotLimbo;

                await SendToAsync(message, player.Client.Id);
                await BroadcastJoinMessage(message, true, player);
            }

            _meetingResumes[player.Client.Id] = new MeetingResume { Slot = slot };

            _reconnectLogs.Record(
                "meeting-joined",
                Code.Code,
                player.Client.Name,
                player.Client.FriendCode ?? string.Empty,
                player.Client.Puid ?? string.Empty,
                player.Client.Connection.EndPoint.Address.ToString(),
                slot.Snapshot.PlayerId,
                slot.Snapshot.OldClientId,
                newClientId: player.Client.Id,
                detail: "joined during a meeting; votes on the old seat, resume after the meeting");

            EnsureMeetingJoinLoop();
        }

        /// <summary>
        ///     会议中进来的观战者: 进房照常、当场判为观战者(观战职业立刻生效),
        ///     补生成身体的指令也当场发(房主会议中照常生成), 散会后再检查一次。
        /// </summary>
        private void DeferSpectatorToMeetingEnd(ClientPlayer player)
        {
            // 同重连者: 会议期间可能拿不到身体, 不按生成超时踢人。
            player.DisableSpawnTimeout();
            _meetingSpectators[player.Client.Id] = 0;
            _logger.LogInformation(
                "{0} - Spectator {1} ({2}) joined during a meeting; spectator now, body requested now and re-checked after the meeting.",
                Code,
                player.Client.Name,
                player.Client.Id);
            EnsureMeetingJoinLoop();
        }

        /// <summary>
        ///     告诉会议中的重连者: 这场会议请以旧身份的编号投票。没有身体时稍后再发。
        /// </summary>
        private async ValueTask TrySendMeetingSeatAsync(int clientId, string trigger)
        {
            if (!_meetingResumes.TryGetValue(clientId, out var resume) || !_players.TryGetValue(clientId, out var target))
            {
                return;
            }

            // 载体必须是接收者本地已经存在的 PlayerControl: 它自己的新身体。
            var carrier = target.Character;
            if (carrier == null)
            {
                return;
            }

            // 刚进来的连接还没上报过进程哈希, 借房里任意一份(与死亡名册的做法相同)。
            if (!Reconnect.TryGetWireInfoForClient(clientId, out var wire)
                && !Reconnect.TryGetWireInfo(null, out wire))
            {
                return;
            }

            try
            {
                using (var writer = StartRpc(carrier.NetId, (RpcCalls)NebulaResumeRpc.RpcId, clientId))
                {
                    writer.Write(wire.ProcessHash);
                    writer.Write(2);
                    writer.Write(NebulaResumeRpc.KindMeetingSeat);
                    writer.Write((int)resume.Slot.Snapshot.PlayerId);
                    await FinishRpcAsync(writer, clientId);
                }

                var first = resume.LastSeatSentAt == default;
                resume.LastSeatSentAt = DateTimeOffset.UtcNow;
                if (first)
                {
                    // 会议中就提示全场"某某重连回本局游戏", 不等散会。
                    //   载体 = 重连者的新身体(房主已生成, 各客户端都有); 编号用旧座位, 会议里大家看到的就是它。
                    if (_rejoinNoticeSent.TryAdd(clientId, 0))
                    {
                        await BroadcastRejoinNoticeAsync(
                            carrier.NetId,
                            wire.ProcessHash,
                            resume.Slot.Snapshot.PlayerId,
                            resume.Slot.Snapshot.PlayerName,
                            resume.Slot.Snapshot.FriendCode);
                    }

                    _logger.LogInformation(
                        "{0} - MeetingJoin: told clientId {1} to vote as playerId {2} in this meeting ({3}).",
                        Code,
                        clientId,
                        resume.Slot.Snapshot.PlayerId,
                        trigger);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{0} - MeetingJoin: sending the meeting seat to clientId {1} failed.", Code, clientId);
            }
        }

        /// <summary>
        ///     散会后补完续局: 与平时的续局完全相同, 只是用最新的模组状态。
        /// </summary>
        private async ValueTask FinishMeetingResumeAsync(int clientId, MeetingResume resume)
        {
            if (!_players.TryGetValue(clientId, out var player) || player.Disconnected)
            {
                // 期间已经离开: 移除路径已经把保槽还回去了。
                return;
            }

            var slot = resume.Slot;

            // 会议期间其他客户端仍替缺席的旧身份上报状态(包括被投出), 用更新的那份。
            if (Reconnect.TryGetLiveModState(slot.Snapshot.PlayerId, out var live, out var seq)
                && seq > slot.Snapshot.ModStateSeq)
            {
                slot.Snapshot.ModState = live;
                slot.Snapshot.ModStateSeq = seq;
                slot.Snapshot.ModStateAt = DateTimeOffset.UtcNow;
            }

            _logger.LogInformation(
                "{0} - MeetingJoin: meeting over, resuming {1} (clientId {2}, playerId {3}).",
                Code,
                player.Client.Name,
                clientId,
                slot.Snapshot.PlayerId);

            // 以下与 HandleJoinGameResume 的后半段一致。
            if (slot.Snapshot.OldClientId != player.Client.Id &&
                _players.TryRemove(slot.Snapshot.OldClientId, out var stale))
            {
                stale.Client.Player = null;
            }

            await AttachReconnectSlotAsync(slot, player);

            // 会议期间房主已经给了新身体, 不能再去接管旧身体。
            if (player.Character == null)
            {
                await ReattachResumedCharacterAsync(player, slot);
            }

            QueueModStateRestore(player, slot);
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
                gapSeconds: (DateTimeOffset.UtcNow - slot.Snapshot.DisconnectedAt).TotalSeconds,
                detail: "resumed after the meeting it joined during");

            // 身体早就在了: 直接把续局信号和状态包送出去。
            if (player.Character != null)
            {
                await OnCharacterReadyAsync(player.Client.Id, "meeting-end");
            }
        }

        /// <summary>
        ///     移除路径调用: 会议中进来的重连者在散会前离开, 把保槽还回去。返回 true 表示已归还。
        /// </summary>
        private bool TryHandBackMeetingResume(int clientId, bool isBan, bool kickBlocksReserve)
        {
            _meetingSpectators.TryRemove(clientId, out _);

            if (!_meetingResumes.TryRemove(clientId, out var resume))
            {
                return false;
            }

            if (isBan || kickBlocksReserve)
            {
                resume.Slot.MarkConsumed();
                return false;
            }

            if (!Reconnect.Restore(resume.Slot, DateTimeOffset.UtcNow))
            {
                return false;
            }

            _logger.LogInformation(
                "{0} - MeetingJoin: {1} left during the meeting; slot handed back.",
                Code,
                resume.Slot.Snapshot.PlayerName);
            return true;
        }

        /// <summary>对局结束: 会议中进入的待办一并作废(保槽由 ReleaseAllReservedSlotsAsync 处理)。</summary>
        private void ResetMeetingJoins()
        {
            _meetingResumes.Clear();
            _meetingSpectators.Clear();
        }

        /// <summary>
        ///     后台循环: 会议期间定时重发座位帧; 会议窗口一结束, 依次补完续局与观战入座。
        /// </summary>
        private void EnsureMeetingJoinLoop()
        {
            if (Interlocked.Exchange(ref _meetingJoinLoopRunning, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_meetingResumes.IsEmpty || !_meetingSpectators.IsEmpty)
                    {
                        await Task.Delay(1000);

                        if (GameState != GameStates.Started && GameState != GameStates.Starting)
                        {
                            ResetMeetingJoins();
                            break;
                        }

                        if (IsInMeetingWindow)
                        {
                            var now = DateTimeOffset.UtcNow;
                            foreach (var kv in _meetingResumes)
                            {
                                if (now - kv.Value.LastSeatSentAt >= MeetingSeatResendInterval)
                                {
                                    await TrySendMeetingSeatAsync(kv.Key, "loop");
                                }
                            }

                            continue;
                        }

                        foreach (var clientId in _meetingResumes.Keys.ToArray())
                        {
                            if (_meetingResumes.TryRemove(clientId, out var resume))
                            {
                                await FinishMeetingResumeAsync(clientId, resume);
                            }
                        }

                        foreach (var clientId in _meetingSpectators.Keys.ToArray())
                        {
                            if (_meetingSpectators.TryRemove(clientId, out _) && _players.TryGetValue(clientId, out var spectator))
                            {
                                await AdmitAsSpectatorAsync(spectator);

                                // (已经是观战者时上面这句什么都不做。)
                                // 会议期间补生成指令被压住了, 现在补一次: 观战判定 + 补生成指令
                                // (房主发现已有身体会跳过, 不会多生成)。
                                // 还没上报场景的, 等它上报时照常走这一步。
                                if (spectator.Scene != null)
                                {
                                    await OnSpectatorSceneReadyAsync(spectator);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{0} - MeetingJoin: background loop failed.", Code);
                }
                finally
                {
                    Interlocked.Exchange(ref _meetingJoinLoopRunning, 0);
                    if (!_meetingResumes.IsEmpty || !_meetingSpectators.IsEmpty)
                    {
                        EnsureMeetingJoinLoop();
                    }
                }
            });
        }
    }
}
