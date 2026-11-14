using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Measures real round-trip time to each connected player and caches it for the panel.
/// </summary>
/// <remarks>
///     为什么要单独测量:
///     Hazel 暴露的 AveragePingMs 是它自己的可靠重传计时值, 平滑时被 Math.Max(50, ...) 钳住 ——
///     局域网或本机玩家的真实 RTT 只有 1~2ms, 它也只会报 50, 面板永远显示不出 50 以下的数字。
///
///     本服务对玩家 IP 做 ICMP 探测, 结果按 IP 缓存, 面板只读缓存(不在渲染时发探测)。
///     ICMP 不通时由调用方回退到 Hazel 的值并标注 ">="。
///
///     对云环境友好:
///       · 只测【有玩家在线】的地址, 玩家全部断开即停止;
///       · 同一 IP 上的多个玩家共用一次探测(引用计数), 不重复发;
///       · 一轮内并发有上限, 人再多也不会瞬间打出一堆包;
///       · 探测频率按在线人数自适应, 人越多间隔越长。
/// </remarks>
public sealed class LatencyProbeService : BackgroundService
{
    /// <summary>最短探测间隔(在线人数很少时)。</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    /// <summary>最长探测间隔(人多时退让, 避免给网络和云防火墙添负担)。</summary>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(15);

    /// <summary>Per-probe timeout. Longer is pointless: an RTT above this is unusable anyway.</summary>
    private const int ProbeTimeoutMs = 1000;

    /// <summary>一轮内最多同时探测多少个地址。</summary>
    private const int MaxConcurrentProbes = 8;

    private readonly ILogger<LatencyProbeService> _logger;

    /// <summary>Latest successful measurement per address, in milliseconds.</summary>
    private readonly ConcurrentDictionary<IPAddress, int> _latency = new();

    /// <summary>
    ///     Address -> 在线连接数。同一 IP 可能有多个玩家(同一 NAT 出口),
    ///     必须按引用计数管理, 否则一个人断开就会把整条探测停掉。
    /// </summary>
    private readonly ConcurrentDictionary<IPAddress, int> _refCounts = new();

    public LatencyProbeService(ILogger<LatencyProbeService> logger)
    {
        _logger = logger;

        // 游戏连接上的真实往返时间(首选来源, 见 HazelRttProbe)。
        Fmpostor.Server.Net.Hazel.HazelRttProbe.Initialize(logger);
    }

    /// <summary>Registers an address to probe (called when a client connects).</summary>
    public void Track(IPAddress? address)
    {
        if (address == null)
        {
            return;
        }

        var key = Normalize(address);
        _refCounts.AddOrUpdate(key, 1, (_, n) => n + 1);

        // 首次连接立即探测一次: 否则玩家要等"初始延迟 + 一轮间隔"才看得到真实值,
        // 这段时间面板会退回 Hazel 的下限值(50ms), 看起来就像没生效。
        _ = Task.Run(() => ProbeAsync(key, CancellationToken.None));
    }

    /// <summary>Drops one reference to an address; stops probing when the last one goes.</summary>
    public void Untrack(IPAddress? address)
    {
        if (address == null)
        {
            return;
        }

        var key = Normalize(address);

        while (true)
        {
            if (!_refCounts.TryGetValue(key, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _refCounts.TryRemove(key, out _);
                _latency.TryRemove(key, out _);
                return;
            }

            if (_refCounts.TryUpdate(key, count - 1, count))
            {
                return; // 还有人用这个地址, 继续探测
            }
            // 竞争失败: 重读计数再试。
        }
    }

    /// <summary>
    ///     Cached round-trip time in milliseconds, or null when there is no usable
    ///     measurement (ICMP blocked, or not probed yet).
    /// </summary>
    public int? TryGetLatency(IPAddress? address)
    {
        if (address == null)
        {
            return null;
        }

        return _latency.TryGetValue(Normalize(address), out var ms) ? ms : null;
    }

    /// <summary>当前有多少个地址在被探测(诊断用)。</summary>
    public int TrackedAddressCount => _refCounts.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动自检: 直接 ping 回环地址。
        // ICMP 现在是第二来源 —— 首选是游戏连接上自己测的往返时间(HazelRttProbe)。
        // 只有两者都拿不到时, 才退回 Hazel 那个带 50ms 下限的平均值。
        try
        {
            var self = await ProbeOnceAsync(IPAddress.Loopback).ConfigureAwait(false);
            _logger.LogInformation(
                self.HasValue
                    ? "[Latency] ICMP 自检通过 (127.0.0.1 = {Ms}ms)，可作为延迟的第二来源。"
                    : "[Latency] ICMP 自检失败 —— 宿主机发不出 ICMP；面板延迟以游戏连接实测为准，不受影响。",
                self ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Latency] ICMP 自检异常，面板延迟将退回 Hazel。");
        }

        // A tiny delay lets the host finish starting before the first round.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan interval;

            try
            {
                var targets = _refCounts.Keys.ToArray();

                if (targets.Length > 0)
                {
                    // 并发上限: 人多时分批, 避免一次打出大量 ICMP。
                    using var gate = new SemaphoreSlim(MaxConcurrentProbes);

                    await Task.WhenAll(targets.Select(async t =>
                    {
                        await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
                        try
                        {
                            await ProbeAsync(t, stoppingToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    })).ConfigureAwait(false);
                }

                // 自适应间隔: 人越多越慢(3s → 15s 线性增长)。
                var factor = Math.Min(1.0, Math.Max(0.0, targets.Length / 30.0));
                interval = MinInterval + (MaxInterval - MinInterval) * factor;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let the probe loop kill the host.
                _logger.LogWarning(ex, "[Latency] probe round failed.");
                interval = MinInterval;
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Probes one address and returns its round-trip time, or null on failure.</summary>
    private static async Task<int?> ProbeOnceAsync(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, ProbeTimeoutMs).ConfigureAwait(false);

            return reply.Status == IPStatus.Success
                ? (int)Math.Max(0, reply.RoundtripTime)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>提醒过的地址，避免 ICMP 不通时每个周期都刷一条日志。</summary>
    private readonly ConcurrentDictionary<IPAddress, byte> _reported = new();

    private async Task ProbeAsync(IPAddress address, CancellationToken token)
    {
        try
        {
            var ms = await ProbeOnceAsync(address).ConfigureAwait(false);

            if (ms.HasValue)
            {
                _latency[address] = ms.Value;

                // 首次测到就打一条：让服主确认"这条连接确实在用实测值"。
                if (_reported.TryAdd(address, 0))
                {
                    _logger.LogInformation(
                        "[Latency] {Address} 实测 {Ms}ms（面板将显示该值）。",
                        address,
                        ms.Value);
                }
            }
            else
            {
                // ICMP 被拦或主机不可达: 丢掉旧值, 让调用方退回 Hazel,
                // 而不是继续显示一个过期的数字。
                _latency.TryRemove(address, out _);

                if (_reported.TryAdd(address, 1))
                {
                    _logger.LogInformation(
                        "[Latency] {Address} 无法 ICMP 探测（可能被防火墙拦截），延迟退回 Hazel 值。",
                        address);
                }
            }
        }
        catch (Exception)
        {
            _latency.TryRemove(address, out _);
        }
        finally
        {
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    ///     IPv4-mapped IPv6 addresses arrive for dual-stack sockets; normalise so the same
    ///     player is not tracked twice under two different keys.
    /// </summary>
    private static IPAddress Normalize(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
