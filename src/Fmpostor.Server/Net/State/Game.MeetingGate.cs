using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Fmpostor.Server.Net.Inner.Objects;
using Fmpostor.Server.Net.Reconnect;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.State
{
    /// <summary>
    ///     会议闸门 + 保槽确认。
    /// </summary>
    /// <remarks>
    ///     两件事解决的是同一个问题: 一次【没成功】的重连不能把保槽(以及里面的模组状态)用掉。
    ///
    ///     ① 会议闸门
    ///        会议界面是开会那一刻按在场玩家一次性搭好的, 中途插入的人没有位置,
    ///        所以会议期间来的重连/观战本来就会被房主移出房间。但如果先放进来,
    ///        匹配保槽的那一刻保槽就已经被取走 —— 人被移出后, 散会再进就什么都恢复不了。
    ///        因此在【加入入口】就拒绝, 保槽完全不碰。
    ///        会议状态由服务端自己判断(房主生成/销毁会议对象), 不依赖任何客户端插件,
    ///        原版客户端与其它模组同样生效。
    ///
    ///     ② 保槽确认
    ///        匹配保槽 ≠ 重连成功。从匹配到玩家真正回到对局之间还有生成身体、
    ///        下发状态等步骤, 任何一步之前断线都应当把保槽还回去。
    ///        这里把"消费"推迟到【身体就位后再稳定连接若干秒】; 在那之前断开就归还。
    ///        踢出与封禁不归还 —— 与"被踢的人不保留位置"的既有规则一致。
    /// </remarks>
    internal partial class Game
    {
        // ───────────────────────── ① 会议闸门 ─────────────────────────

        /// <summary>会议对象是否存在(房主已生成、尚未销毁)。</summary>
        private volatile bool _meetingHudAlive;

        /// <summary>会议对象生成的时刻(UTC ticks), 用来识别"销毁消息丢失"的陈旧状态。</summary>
        private long _meetingOpenedAtTicks;

        /// <summary>会议对象销毁的时刻(UTC ticks); 0 = 本局还没有结束过会议。</summary>
        private long _meetingClosedAtTicks;

        /// <summary>会议对象存在超过这个时长就视为陈旧(销毁消息丢失 / 房主异常)。</summary>
        private static readonly TimeSpan MeetingStaleAfter = TimeSpan.FromMinutes(10);

        /// <summary>当前这场会议的会议对象。旧会议对象的迟到消息(例如它自己的计时器)不能关掉新会议。</summary>
        private InnerMeetingHud? _currentMeetingHud;

        private void NoteMeetingHudSpawned(InnerMeetingHud meetingHud)
        {
            _currentMeetingHud = meetingHud;
            _meetingHudAlive = true;
            Interlocked.Exchange(ref _meetingOpenedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            Interlocked.Exchange(ref _hostZeroSinceTicks, 0);
            _logger.LogInformation("{0} - MeetingGate: 会议开始, 暂停对局中途加入。", Code);
        }

        /// <summary>
        ///     会议结束(投票完成)。缓冲期从这一刻算起。
        /// </summary>
        /// <remarks>
        ///     这是原版房间的信号。模组房间会取消原版的投票结束消息, 那里以房主的会议状态广播为准
        ///     (见 NoteHostMeetingFlag), 两者取先到者。
        ///     会议对象只在各客户端本地销毁, 销毁消息通常不会到服务端; 真的到了也按会议结束处理。
        ///     只接受【当前这场】会议对象发来的结束: 上一场会议的计时器可能在下一场会议中途才到点。
        /// </remarks>
        internal void NoteMeetingClosed(string source, InnerMeetingHud? meetingHud = null)
        {
            if (!_meetingHudAlive)
            {
                return;
            }

            if (meetingHud != null && !ReferenceEquals(meetingHud, _currentMeetingHud))
            {
                return;
            }

            _meetingHudAlive = false;
            Interlocked.Exchange(ref _meetingClosedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            _logger.LogInformation(
                "{0} - MeetingGate: 会议结束({1}), 最晚 {2} 秒后恢复对局中途加入(房主报告散会流程结束会更早)。",
                Code,
                source,
                Math.Max(0, _reconnectConfig.MeetingJoinGraceSeconds));
        }

        private void NoteMeetingHudDestroyed(InnerMeetingHud meetingHud) => NoteMeetingClosed("会议对象销毁", meetingHud);

        // ── 散会流程真正结束的时刻 ──
        //
        //   投票结束之后还有: 结果展示 → 放逐动画 → 选出生点。这些都只在各客户端本地进行,
        //   服务端看不到任何消息, 所以只能按固定缓冲期等(MeetingJoinGraceSeconds)。
        //   但装了本服插件的房主每秒都会广播会议状态: 只要它还显示会议或放逐/出生点界面就是 1,
        //   全部结束才变成 0。投票结束后第一次收到房主的 0, 就说明散会流程已经走完,
        //   可以提前开放, 不必干等满缓冲期。没有这条广播(原版/旧版房主)时照旧按缓冲期。

        /// <summary>收到房主"散会流程已结束"后再等多久开放(秒), 给各端收尾留一点余量。</summary>
        private const double HostFlowEndMarginSeconds = 2;

        /// <summary>房主报告散会流程结束的时刻(UTC ticks); 小于本次会议结束时刻 = 本次还没报告。</summary>
        private long _meetingFlowEndedAtTicks;

        /// <summary>房主广播开始连续为 0 的时刻(UTC ticks); 0 = 当前没有在确认中。</summary>
        private long _hostZeroSinceTicks;

        /// <summary>
        ///     收到房主的会议状态广播。
        /// </summary>
        /// <remarks>
        ///     ⚠️ 这是判断"会议结束"的【主信号】, 不能依赖"投票结束"消息:
        ///     模组会取消原版的投票结束消息, 改用自己的通道宣布结果 —— 服务端在模组房间里
        ///     根本收不到原版的投票结束, 只靠它的话会议窗口永远关不上(散会多久都提示会议中)。
        ///     房主的广播在会议、放逐动画、选出生点期间一直是 1, 全部结束才是 0,
        ///     所以"会议开始后, 房主连续两次报 0"就等于整个会议流程真正结束。
        ///     要求连续两次, 是为了避开会议界面与放逐动画交接瞬间可能出现的单次 0。
        /// </remarks>
        internal void NoteHostMeetingFlag(int clientId, bool active)
        {
            var host = Host;
            if (host == null || host.Client.Id != clientId)
            {
                return;   // 只认房主
            }

            if (active)
            {
                Interlocked.Exchange(ref _hostZeroSinceTicks, 0);
                return;
            }

            var openedTicks = Interlocked.Read(ref _meetingOpenedAtTicks);
            if (openedTicks == 0 || Interlocked.Read(ref _meetingFlowEndedAtTicks) >= openedTicks)
            {
                return;   // 本局还没开过会 / 这次会议已经确认结束过了
            }

            // 会议刚开始时, 房主开会前发出的 0 可能还在路上, 前 3 秒的 0 一律不算。
            var now = DateTimeOffset.UtcNow;
            var opened = new DateTimeOffset(openedTicks, TimeSpan.Zero);
            if (now - opened < TimeSpan.FromSeconds(3))
            {
                return;
            }

            var zeroSince = Interlocked.Read(ref _hostZeroSinceTicks);
            if (zeroSince < openedTicks)
            {
                Interlocked.Exchange(ref _hostZeroSinceTicks, now.UtcTicks);   // 第一次 0: 等下一次确认
                return;
            }

            if (now - new DateTimeOffset(zeroSince, TimeSpan.Zero) < TimeSpan.FromSeconds(0.9))
            {
                return;
            }

            // 确认: 会议、放逐动画、选出生点全部结束。
            Interlocked.Exchange(ref _meetingFlowEndedAtTicks, zeroSince);
            if (_meetingHudAlive)
            {
                _meetingHudAlive = false;
                Interlocked.Exchange(ref _meetingClosedAtTicks, zeroSince);
            }

            _logger.LogInformation(
                "{0} - MeetingGate: 房主报告会议及散会流程已全部结束(会议开始后 {1:F0} 秒), {2} 秒后恢复对局中途加入。",
                Code,
                (new DateTimeOffset(zeroSince, TimeSpan.Zero) - opened).TotalSeconds,
                HostFlowEndMarginSeconds);
        }

        /// <summary>
        ///     一场会议最长可能持续多久(讨论 + 投票 + 动画), 用作没有房主信号时的兜底。
        ///     投票时间不限(0)或无法读取设置时, 退回陈旧判定时长。
        /// </summary>
        private TimeSpan MaxMeetingDuration()
        {
            try
            {
                if (Options is Fmpostor.Api.Innersloth.GameOptions.NormalGameOptions o && o.VotingTime > 0)
                {
                    return TimeSpan.FromSeconds(11 + Math.Max(0, o.DiscussionTime) + o.VotingTime);
                }
            }
            catch { }

            return MeetingStaleAfter;
        }

        /// <summary>新一局开始 / 对局结束时调用。</summary>
        private void ResetMeetingWindow()
        {
            _meetingHudAlive = false;
            Interlocked.Exchange(ref _meetingOpenedAtTicks, 0);
            Interlocked.Exchange(ref _meetingClosedAtTicks, 0);
            Interlocked.Exchange(ref _meetingFlowEndedAtTicks, 0);
            Interlocked.Exchange(ref _hostZeroSinceTicks, 0);
        }

        /// <summary>当前是否处于会议窗口(会议进行中, 或刚散会的缓冲期内)。</summary>
        internal bool IsInMeetingWindow
        {
            get
            {
                if (GameState != GameStates.Started && GameState != GameStates.Starting)
                {
                    return false;
                }

                var now = DateTimeOffset.UtcNow;

                var closedTicks = Interlocked.Read(ref _meetingClosedAtTicks);

                if (_meetingHudAlive)
                {
                    // 兜底: 没有房主信号、也没收到投票结束时, 按"会议最长时长"推算结束时刻,
                    // 之后照常走缓冲期 —— 保证窗口总有关闭的一刻, 不会一直提示会议中。
                    var opened = new DateTimeOffset(Interlocked.Read(ref _meetingOpenedAtTicks), TimeSpan.Zero);
                    var assumedEnd = opened + MaxMeetingDuration();
                    if (now < assumedEnd)
                    {
                        return true;
                    }

                    closedTicks = assumedEnd.UtcTicks;
                }
                if (closedTicks == 0)
                {
                    return false;
                }

                // 开放时刻 = min(投票结束 + 缓冲期, 房主报告散会结束 + 余量)
                var closed = new DateTimeOffset(closedTicks, TimeSpan.Zero);
                var reopenAt = closed + TimeSpan.FromSeconds(Math.Max(0, _reconnectConfig.MeetingJoinGraceSeconds));

                var flowEndedTicks = Interlocked.Read(ref _meetingFlowEndedAtTicks);
                if (flowEndedTicks >= closedTicks)
                {
                    var byHost = new DateTimeOffset(flowEndedTicks, TimeSpan.Zero) + TimeSpan.FromSeconds(HostFlowEndMarginSeconds);
                    if (byHost < reopenAt)
                    {
                        reopenAt = byHost;
                    }
                }

                return now < reopenAt;
            }
        }

        /// <summary>
        ///     会议窗口内拒绝"对局中途的加入"(重连 / 观战)。返回 null 表示放行。
        /// </summary>
        /// <remarks>
        ///     只拦截【本来会被放进来】的那两类加入: 有保槽的重连者, 以及服务端允许观战时的新玩家。
        ///     其它人照旧走原来的"对局已开始"拒绝, 不改变原有提示。
        ///     这里只做只读检查, 不认领保槽, 也不走观战闸门(那会打出误导性的拒绝日志)。
        /// </remarks>
        private GameJoinResult? CheckMeetingGate(ClientBase client)
        {
            if (!IsInMeetingWindow)
            {
                return null;
            }

            var identityKey = ReconnectRegistry.BuildIdentityKey(client.Puid, client.FriendCode, client.Name);
            var hasSlot = Reconnect.Enabled && Reconnect.HasPending(identityKey, DateTimeOffset.UtcNow);
            var mayWatch = _reconnectConfig.EnableSpectator;

            if (!hasSlot && !mayWatch)
            {
                return null;
            }

            _logger.LogInformation(
                "{0} - MeetingGate: 会议进行中, 拒绝 {1} 加入(保槽={2}, 保槽保持不动)。",
                Code,
                client.Name,
                hasSlot);

            if (hasSlot)
            {
                _reconnectLogs.Record(
                    "meeting-refused",
                    Code.Code,
                    client.Name,
                    client.FriendCode ?? string.Empty,
                    client.Puid ?? string.Empty,
                    client.Connection.EndPoint.Address.ToString(),
                    playerId: -1,
                    oldClientId: client.Id,
                    detail: "join refused during meeting; slot kept");
            }

            return GameJoinResult.CreateCustomError(hasSlot
                ? "会议（含散会动画）进行中，暂时无法回到对局。请在散会后稍等片刻再重新输入房间号进入，你的位置会一直保留。\nA meeting (or its exile scene) is in progress. Rejoin shortly after it ends - your slot is kept."
                : "会议（含散会动画）进行中，暂时无法加入。请在散会后稍等片刻再试。\nA meeting (or its exile scene) is in progress. Please try again shortly after it ends.");
        }

        // ───────────────────────── ② 保槽确认 ─────────────────────────

        /// <summary>
        ///     身体生成后, 等多久再下发续局信号与状态包(毫秒)。
        ///     要让房主的生成消息先转发到重连者那里, 否则帧会先于身体到达而被丢弃(见 Game.Data.cs)。
        /// </summary>
        private const int CharacterReadyDelayMs = 500;

        /// <summary>已匹配保槽、但还没确认成功的重连: 新 clientId → 保槽。</summary>
        private readonly ConcurrentDictionary<int, PendingSlot> _unconfirmedResumes = new();

        /// <summary>已经排过确认计时的 clientId(身体就位可能被通知多次, 只计一次)。</summary>
        private readonly ConcurrentDictionary<int, byte> _resumeConfirmScheduled = new();

        /// <summary>重连匹配上保槽时调用: 先记账, 暂不视为成功。</summary>
        private void TrackUnconfirmedResume(int clientId, PendingSlot slot)
        {
            _unconfirmedResumes[clientId] = slot;
            _resumeConfirmScheduled.TryRemove(clientId, out _);
        }

        /// <summary>
        ///     身体就位时调用: 再稳定连接 ResumeConfirmSeconds 秒, 就把保槽正式消费掉。
        /// </summary>
        private void ScheduleResumeConfirmation(int clientId)
        {
            if (!_unconfirmedResumes.ContainsKey(clientId) || !_resumeConfirmScheduled.TryAdd(clientId, 0))
            {
                return;
            }

            var delay = TimeSpan.FromSeconds(Math.Max(0, _reconnectConfig.ResumeConfirmSeconds));

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay);

                    if (!_players.TryGetValue(clientId, out var player) || player.Disconnected)
                    {
                        return;   // 期间已经断开: 由移除路径决定是否归还
                    }

                    if (_unconfirmedResumes.TryRemove(clientId, out var slot))
                    {
                        _resumeConfirmScheduled.TryRemove(clientId, out _);
                        slot.MarkConsumed();
                        _logger.LogInformation(
                            "Reconnect: {Name} (clientId {ClientId}) 已稳定回到对局, 保槽正式消费。",
                            slot.Snapshot.PlayerName,
                            clientId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reconnect: resume confirmation failed for clientId {ClientId}.", clientId);
                }
            });
        }

        /// <summary>
        ///     连接被移除时调用。若这条连接是一次尚未确认的重连, 且不是踢出/封禁,
        ///     就把原保槽还回去。返回 true 表示已归还(调用方不应再为这条连接另建保槽)。
        /// </summary>
        private bool TryHandBackUnconfirmedResume(int clientId, bool isBan, bool kickBlocksReserve)
        {
            _resumeConfirmScheduled.TryRemove(clientId, out _);

            if (!_unconfirmedResumes.TryRemove(clientId, out var slot))
            {
                return false;
            }

            if (isBan || kickBlocksReserve)
            {
                slot.MarkConsumed();
                _logger.LogInformation(
                    "Reconnect: {Name} (clientId {ClientId}) 在确认前被{How}, 保槽作废(不归还)。",
                    slot.Snapshot.PlayerName,
                    clientId,
                    isBan ? "封禁" : "踢出");
                return false;
            }

            if (!Reconnect.Restore(slot, DateTimeOffset.UtcNow))
            {
                _logger.LogInformation(
                    "Reconnect: {Name} (clientId {ClientId}) 在确认前断开, 但保槽已过期或已被新的保槽取代, 不归还。",
                    slot.Snapshot.PlayerName,
                    clientId);
                return false;
            }

            _reconnectLogs.Record(
                "handed-back",
                Code.Code,
                slot.Snapshot.PlayerName,
                slot.Snapshot.FriendCode,
                slot.Snapshot.Puid,
                slot.Snapshot.IpAddress,
                slot.Snapshot.PlayerId,
                slot.Snapshot.OldClientId,
                newClientId: clientId,
                detail: "connection lost before the resume was confirmed; slot handed back");

            return true;
        }

        /// <summary>对局结束: 未确认的重连一并作废(保槽本身已由 ReleaseAllReservedSlotsAsync 处理)。</summary>
        private void ResetUnconfirmedResumes()
        {
            _unconfirmedResumes.Clear();
            _resumeConfirmScheduled.Clear();
        }
    }
}
