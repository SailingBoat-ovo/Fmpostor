using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Thread-safe pool of UDP ports from the configured delta range.
///     Allocates a unique port per authenticated player to act as a nonce
///     for matching the TCP auth session to the subsequent UDP connection.
///     The range/enabled state is read from webadmin_settings.json (hot reloadable
///     through the web panel).
/// </summary>
public sealed class DeltaPortPoolService : IDisposable
{
    private readonly ILogger<DeltaPortPoolService> _logger;
    private readonly ConcurrentBag<int> _availablePorts = new();
    private readonly ConcurrentDictionary<int, PortLease> _activeLeases = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _timeouts = new();
    private readonly int _listenPort;
    private readonly WebAdminSettingsService _settingsService;

    private int _deltaPortStart;
    private int _deltaPortEnd;
    private bool _enabled;

    public bool IsEnabled => _enabled && _deltaPortStart > 0 && _deltaPortEnd >= _deltaPortStart;

    /// <summary>
    ///     Invoked when a port is returned to the pool (via disconnect or timeout expiry).
    ///     Subscribers should stop the delta listener.
    /// </summary>
    public event Action<int>? OnPortReturned;

    public DeltaPortPoolService(
        ILogger<DeltaPortPoolService> logger,
        WebAdminSettingsService settingsService,
        IOptions<Fmpostor.Api.Config.ServerConfig> serverConfig)
    {
        _logger = logger;
        _settingsService = settingsService;
        _listenPort = serverConfig.Value.ListenPort;

        ApplySettings(_settingsService.GetDeltaPorts());
    }

    /// <summary>
    ///     Re-applies the delta port range/enabled settings (called at startup and
    ///     whenever the panel saves new values). Already-allocated ports are kept
    ///     alive so online players are not interrupted.
    /// </summary>
    public void ApplySettings(DeltaPortSettings settings)
    {
        lock (_availablePorts)
        {
            // Clamp to a sane port range even if the panel/config send bogus values.
            var start = Math.Clamp(settings.Start, 1, 65535);
            var end = Math.Clamp(settings.End, 1, 65535);
            if (end < start)
            {
                end = start;
            }

            _deltaPortStart = start;
            _deltaPortEnd = end;
            _enabled = settings.Enabled;

            // Rebuild the pool while preserving active leases.
            _availablePorts.Clear();
            for (var port = _deltaPortStart; port <= _deltaPortEnd; port++)
            {
                if (port == _listenPort || _activeLeases.ContainsKey(port))
                {
                    continue;
                }

                _availablePorts.Add(port);
            }

            _logger.LogInformation(
                "DeltaPortPool settings applied: enabled={Enabled}, range={Start}-{End}, available={Count}, active={Active}",
                _enabled, _deltaPortStart, _deltaPortEnd, _availablePorts.Count, _activeLeases.Count);
        }
    }

    /// <summary>
    ///     Returns 0 if the pool is empty or disabled. A PUID that already holds an
    ///     active lease always receives the SAME port, so repeated /api/user calls
    ///     cannot exhaust the pool.
    /// </summary>
    public int AllocatePort(string puid)
    {
        if (!IsEnabled)
        {
            return 0;
        }

        if (!string.IsNullOrEmpty(puid))
        {
            foreach (var kv in _activeLeases)
            {
                if (string.Equals(kv.Value.ProductUserId, puid, StringComparison.Ordinal))
                {
                    return kv.Key;
                }
            }
        }

        if (!_availablePorts.TryTake(out var port))
        {
            _logger.LogWarning("DeltaPortPool exhausted for PUID={Puid}", puid);
            return 0;
        }

        var lease = new PortLease
        {
            Port = port,
            ProductUserId = puid,
            AllocatedAt = DateTime.UtcNow,
        };

        _activeLeases[port] = lease;

        // Start 5-minute timeout: if no connection arrives, return port to pool.
        var cts = new CancellationTokenSource();
        _timeouts[port] = cts;
        _ = TimeoutAsync(port, cts.Token);

        _logger.LogDebug("DeltaPortPool allocated port {Port} to PUID={Puid}", port, puid);
        return port;
    }

    public void ReturnPort(int port)
    {
        if (port <= 0)
        {
            return;
        }

        if (_timeouts.TryRemove(port, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        _activeLeases.TryRemove(port, out _);
        _availablePorts.Add(port);

        _logger.LogInformation("DeltaPortPool returned port {Port} to pool", port);

        OnPortReturned?.Invoke(port);
    }

    /// <summary>
    ///     Cancels the 5-minute allocation timeout so the port stays allocated
    ///     while the player is connected.
    /// </summary>
    public void ConfirmPort(int port)
    {
        if (port <= 0)
        {
            return;
        }

        if (_timeouts.TryRemove(port, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _logger.LogDebug("DeltaPortPool port {Port} confirmed (timeout cancelled)", port);
        }
    }

    public bool HasLease(int port)
    {
        return _activeLeases.ContainsKey(port);
    }

    private async Task TimeoutAsync(int port, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);

            // Timer fired — no connection came in.
            _logger.LogWarning("DeltaPortPool port {Port} lease expired (no connection), returning to pool", port);
            ReturnPort(port);
        }
        catch (OperationCanceledException)
        {
            // Normal — port was used or explicitly returned.
        }
    }

    public void Dispose()
    {
        foreach (var (_, cts) in _timeouts)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _timeouts.Clear();
        _activeLeases.Clear();
    }

    private sealed class PortLease
    {
        public int Port { get; init; }
        public string ProductUserId { get; init; } = string.Empty;
        public DateTime AllocatedAt { get; init; }
    }
}