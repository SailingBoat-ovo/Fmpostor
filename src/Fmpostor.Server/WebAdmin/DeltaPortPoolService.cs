using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Thread-safe pool of UDP ports from the configured delta range.
///     Allocates a unique port per authenticated player (PUID) to act as a nonce
///     for matching the HTTP auth session to the subsequent UDP connection, so
///     players behind one NAT/CDN IP are never confused with each other.
///     The range/enabled state is read from webadmin_settings.json (hot reloadable
///     through the web panel).
/// </summary>
/// <remarks>
///     Lease lifetime:
///     <list type="bullet">
///         <item>Allocated on /api/user; idle timeout starts (no connection yet).</item>
///         <item>Every UDP connection arriving on the port attaches to it and cancels the timeout.</item>
///         <item>When the LAST connection on the port leaves, the idle timeout starts again
///               instead of returning the port at once. A player who drops and comes back
///               (reconnect, or several game windows sharing one account) keeps the same
///               port, and one connection leaving never tears down the listener that other
///               live connections are still using.</item>
///         <item>Only an idle lease that times out is returned to the pool (listener stopped).</item>
///     </list>
/// </remarks>
public sealed class DeltaPortPoolService : IDisposable
{
    /// <summary>How long a lease with no live connection is kept before the port is freed.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger<DeltaPortPoolService> _logger;
    private readonly object _lock = new();
    private readonly HashSet<int> _availablePorts = new();
    private readonly Dictionary<int, PortLease> _activeLeases = new();
    private readonly Dictionary<string, int> _portByPuid = new(StringComparer.Ordinal);
    private readonly HashSet<int> _badPorts = new();
    private readonly int _listenPort;
    private readonly WebAdminSettingsService _settingsService;

    private int _deltaPortStart;
    private int _deltaPortEnd;
    private bool _enabled;

    public bool IsEnabled => _enabled && _deltaPortStart > 0 && _deltaPortEnd >= _deltaPortStart;

    /// <summary>
    ///     Invoked when a port is returned to the pool (idle timeout or explicit release).
    ///     Subscribers should stop the delta listener. Never fired while a connection
    ///     is still attached to the port.
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
        lock (_lock)
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
            _badPorts.Clear();
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
    ///     cannot exhaust the pool, and a reconnecting player lands on the port it had.
    /// </summary>
    public int AllocatePort(string? puid)
    {
        lock (_lock)
        {
            if (!IsEnabled)
            {
                return 0;
            }

            // Without a PUID the port cannot be tied to anyone; such clients use the main port.
            if (string.IsNullOrEmpty(puid))
            {
                return 0;
            }

            if (_portByPuid.TryGetValue(puid, out var existing) && _activeLeases.TryGetValue(existing, out var held))
            {
                if (held.Connections == 0)
                {
                    StartIdleTimeoutLocked(held);
                }

                return existing;
            }

            var port = 0;
            foreach (var candidate in _availablePorts)
            {
                port = candidate;
                break;
            }

            if (port == 0)
            {
                _logger.LogWarning("DeltaPortPool exhausted for PUID={Puid}; the client will use the main port", puid);
                return 0;
            }

            _availablePorts.Remove(port);

            var lease = new PortLease
            {
                Port = port,
                ProductUserId = puid,
                AllocatedAt = DateTime.UtcNow,
            };

            _activeLeases[port] = lease;
            _portByPuid[puid] = port;
            StartIdleTimeoutLocked(lease);

            _logger.LogDebug("DeltaPortPool allocated port {Port} to PUID={Puid}", port, puid);
            return port;
        }
    }

    /// <summary>The port currently leased to <paramref name="puid" />, or 0.</summary>
    public int GetPortFor(string? puid)
    {
        if (string.IsNullOrEmpty(puid))
        {
            return 0;
        }

        lock (_lock)
        {
            return _portByPuid.TryGetValue(puid, out var port) && _activeLeases.ContainsKey(port) ? port : 0;
        }
    }

    /// <summary>True when <paramref name="port" /> is leased and (if given) to that PUID.</summary>
    public bool IsLeasedTo(int port, string? puid)
    {
        lock (_lock)
        {
            if (!_activeLeases.TryGetValue(port, out var lease))
            {
                return false;
            }

            return string.IsNullOrEmpty(puid) || string.Equals(lease.ProductUserId, puid, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Frees a lease immediately (listener stopped). Ignored while connections are
    ///     still attached.
    /// </summary>
    public void ReturnPort(int port)
    {
        ReturnPortCore(port, force: false, markBad: false);
    }

    /// <summary>
    ///     The listener for <paramref name="port" /> could not be started (port in use by
    ///     something else). Drops the lease and keeps the port out of the pool until the
    ///     settings are applied again.
    /// </summary>
    public void MarkPortUnusable(int port)
    {
        ReturnPortCore(port, force: true, markBad: true);
    }

    private void ReturnPortCore(int port, bool force, bool markBad)
    {
        if (port <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_activeLeases.TryGetValue(port, out var lease))
            {
                return;
            }

            if (lease.Connections > 0 && !force)
            {
                return;
            }

            CancelTimeoutLocked(lease);
            _activeLeases.Remove(port);
            if (_portByPuid.TryGetValue(lease.ProductUserId, out var owned) && owned == port)
            {
                _portByPuid.Remove(lease.ProductUserId);
            }

            if (markBad)
            {
                _badPorts.Add(port);
            }
            else if (IsInRangeLocked(port) && !_badPorts.Contains(port))
            {
                _availablePorts.Add(port);
            }
        }

        _logger.LogInformation("DeltaPortPool returned port {Port} to pool{Bad}", port, markBad ? " (marked unusable)" : string.Empty);

        try
        {
            OnPortReturned?.Invoke(port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeltaPortPool OnPortReturned handler failed for port {Port}", port);
        }
    }

    /// <summary>
    ///     A UDP connection arrived on the port: keep the lease alive while it is connected.
    /// </summary>
    public void AttachConnection(int port)
    {
        if (port <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_activeLeases.TryGetValue(port, out var lease))
            {
                return;
            }

            lease.Connections++;
            CancelTimeoutLocked(lease);
            _logger.LogDebug("DeltaPortPool port {Port} attached (connections={Count})", port, lease.Connections);
        }
    }

    /// <summary>
    ///     A connection on the port left. When it was the last one the lease goes idle
    ///     and is freed after the idle timeout, unless the player comes back first.
    /// </summary>
    public void ReleaseConnection(int port)
    {
        if (port <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_activeLeases.TryGetValue(port, out var lease))
            {
                return;
            }

            if (lease.Connections > 0)
            {
                lease.Connections--;
            }

            if (lease.Connections == 0)
            {
                StartIdleTimeoutLocked(lease);
                _logger.LogDebug("DeltaPortPool port {Port} idle; freed in {Minutes} min unless reused", port, IdleTimeout.TotalMinutes);
            }
        }
    }

    /// <summary>Kept for callers of the old API: same as <see cref="AttachConnection" />.</summary>
    public void ConfirmPort(int port) => AttachConnection(port);

    public bool HasLease(int port)
    {
        lock (_lock)
        {
            return _activeLeases.ContainsKey(port);
        }
    }

    private bool IsInRangeLocked(int port) =>
        port >= _deltaPortStart && port <= _deltaPortEnd && port != _listenPort;

    private void StartIdleTimeoutLocked(PortLease lease)
    {
        CancelTimeoutLocked(lease);
        var cts = new CancellationTokenSource();
        lease.Timeout = cts;
        _ = TimeoutAsync(lease.Port, cts, cts.Token);
    }

    private static void CancelTimeoutLocked(PortLease lease)
    {
        var cts = lease.Timeout;
        lease.Timeout = null;
        if (cts == null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    private async Task TimeoutAsync(int port, CancellationTokenSource cts, CancellationToken token)
    {
        try
        {
            await Task.Delay(IdleTimeout, token);
        }
        catch (OperationCanceledException)
        {
            return;   // connection arrived or lease refreshed
        }

        lock (_lock)
        {
            // Only the timer that is still current may free the lease.
            if (!_activeLeases.TryGetValue(port, out var lease) || !ReferenceEquals(lease.Timeout, cts) || lease.Connections > 0)
            {
                return;
            }
        }

        _logger.LogInformation("DeltaPortPool port {Port} idle lease expired, returning to pool", port);
        ReturnPort(port);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var lease in _activeLeases.Values)
            {
                CancelTimeoutLocked(lease);
            }

            _activeLeases.Clear();
            _portByPuid.Clear();
        }
    }

    private sealed class PortLease
    {
        public int Port { get; init; }
        public string ProductUserId { get; init; } = string.Empty;
        public DateTime AllocatedAt { get; init; }
        public int Connections { get; set; }
        public CancellationTokenSource? Timeout { get; set; }
    }
}
