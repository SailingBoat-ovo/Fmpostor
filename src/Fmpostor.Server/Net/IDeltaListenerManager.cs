using System.Threading.Tasks;

namespace Fmpostor.Server.Net;

/// <summary>
///     Public interface for managing dynamically allocated UDP listeners on delta ports.
///     Implemented by Matchmaker; injected into HTTP controllers that need to start
///     listeners after port allocation.
/// </summary>
public interface IDeltaListenerManager
{
    /// <summary>
    ///     Starts a UDP listener on a specific port. Returns true when a listener is
    ///     running on the port afterwards (started now or already running).
    /// </summary>
    ValueTask<bool> StartDeltaListenerAsync(int port);

    /// <summary>
    ///     Stops and disposes the UDP listener on a specific port.
    /// </summary>
    ValueTask StopDeltaListenerAsync(int port);
}