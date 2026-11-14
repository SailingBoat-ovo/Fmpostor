using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Impostor.Hazel;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.Hazel
{
    /// <summary>
    ///     自己测每个连接的真实往返时间(RTT), 用于面板显示。
    /// </summary>
    /// <remarks>
    ///     为什么需要它:
    ///       网络库对外只给一个平均延迟 AveragePingMs, 而它在平滑时被
    ///       Math.Max(50, ...) 钳住 —— 局域网或同城玩家真实只有几毫秒, 面板也只能看到 50。
    ///       这个下限不能去掉: 网络库还用这个值计算"可靠包多久没确认就重发",
    ///       去掉的话低延迟连接的重发等待只剩几毫秒, 会造成大量重复发包。
    ///
    ///     做法:
    ///       每 2 秒给连接发一个网络库标准的保活 Ping 包(和网络库自己发的保活包完全相同,
    ///       所有客户端 —— 包括原版 —— 收到都会自动回确认), 发的时候挂一个"收到确认"的回调,
    ///       从发出到回调触发的时间就是真实往返时间。按 0.7/0.3 平滑, 不设下限。
    ///       用的是网络库内部的可靠发送方法(反射调用), 不改它的任何代码与行为。
    ///
    ///     规则:
    ///       · 面板第一次查询某个连接时才开始测, 连接断开自动停止, 没人看面板就不额外发包;
    ///       · 超过 30 秒没有新样本就视为没有数据, 交给其它来源(ICMP / 平均值);
    ///       · 任何一步失败都只是"没有这个读数", 绝不影响收发本身。
    /// </remarks>
    internal static class HazelRttProbe
    {
        private sealed class RttState
        {
            public double Smoothed = -1;
            public long UpdatedAt;
        }

        private static readonly ConditionalWeakTable<Connection, RttState> States = new();

        /// <summary>每个连接两次测量之间的间隔(毫秒)。</summary>
        private const int IntervalMs = 2000;

        /// <summary>样本多久不更新就视为过期(毫秒)。</summary>
        private const long StaleAfterMs = 30_000;

        /// <summary>网络库协议里的保活包类型。</summary>
        private const byte PingSendOption = 12;

        private static MethodInfo? _reliableSend;
        private static bool _resolved;
        private static ILogger? _logger;

        /// <summary>找到网络库的可靠发送方法。找不到时整套探针不启用, 面板照旧走 ICMP / 平均值。</summary>
        public static void Initialize(ILogger logger)
        {
            _logger = logger;

            try
            {
                _reliableSend = typeof(Impostor.Hazel.Udp.UdpConnection).GetMethod(
                    "ReliableSend",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(byte), typeof(byte[]), typeof(Action) },
                    null);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Latency] 真实往返时间探针初始化失败。");
            }

            _resolved = true;

            if (_reliableSend != null)
            {
                logger.LogInformation("[Latency] 真实往返时间探针已启用: 面板延迟可以显示 50ms 以下的真实数值。");
            }
            else
            {
                logger.LogWarning("[Latency] 真实往返时间探针未启用: 网络库内部结构与预期不符, 面板延迟继续使用 ICMP / 平均值。");
            }
        }

        public static bool Available => _resolved && _reliableSend != null;

        /// <summary>
        ///     取这个连接的真实往返时间(毫秒, 已平滑)。第一次查询会开始测量,
        ///     还没有样本时返回 null(由调用方回退到其它来源)。
        /// </summary>
        public static int? TryGetRttMs(Connection? connection)
        {
            if (!Available || connection is not Impostor.Hazel.Udp.UdpConnection)
            {
                return null;
            }

            if (!States.TryGetValue(connection, out var state))
            {
                state = new RttState();
                if (States.TryAdd(connection, state))
                {
                    _ = MeasureLoopAsync(connection, state);
                }

                return null;
            }

            lock (state)
            {
                if (state.Smoothed < 0 || Environment.TickCount64 - state.UpdatedAt > StaleAfterMs)
                {
                    return null;
                }

                return (int)Math.Round(state.Smoothed);
            }
        }

        private static async Task MeasureLoopAsync(Connection connection, RttState state)
        {
            try
            {
                while (connection.State == ConnectionState.Connected)
                {
                    await SendProbeAsync(connection, state);
                    await Task.Delay(IntervalMs);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Latency] RTT measurement loop ended.");
            }
        }

        private static async Task SendProbeAsync(Connection connection, RttState state)
        {
            var stopwatch = Stopwatch.StartNew();

            void OnAcknowledged()
            {
                var rtt = stopwatch.Elapsed.TotalMilliseconds;
                if (rtt < 0 || rtt > 10_000)
                {
                    return;
                }

                lock (state)
                {
                    state.Smoothed = state.Smoothed < 0 ? rtt : (state.Smoothed * 0.7) + (rtt * 0.3);
                    state.UpdatedAt = Environment.TickCount64;
                }
            }

            try
            {
                // 与网络库自己的保活包相同: [Ping][可靠编号 2 字节], 没有正文。
                if (_reliableSend!.Invoke(connection, new object[] { PingSendOption, Array.Empty<byte>(), (Action)OnAcknowledged }) is ValueTask pending)
                {
                    await pending;
                }
            }
            catch
            {
                // 连接正在断开等情况: 本轮没有样本即可。
            }
        }
    }
}
