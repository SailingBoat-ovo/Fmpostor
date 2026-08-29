using Fmpostor.Api.Innersloth.Maps;

namespace Fmpostor.Api.Events.Player
{
    /// <summary>
    ///     Called whenever a player exits a vent.
    /// </summary>
    public interface IPlayerExitVentEvent : IPlayerEvent
    {
        /// <summary>
        ///     Gets the exited vent.
        /// </summary>
        public VentData Vent { get; }
    }
}
