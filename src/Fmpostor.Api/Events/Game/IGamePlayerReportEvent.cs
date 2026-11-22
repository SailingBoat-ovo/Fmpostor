using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;

namespace Fmpostor.Api.Events
{
    /// <summary>
    ///     A player reported another player with the game's own report menu
    ///     (the button next to the chat box). Works for vanilla and modded clients alike.
    /// </summary>
    public interface IGamePlayerReportEvent : IGameEvent
    {
        /// <summary>The player who sent the report.</summary>
        IClientPlayer Reporter { get; }

        /// <summary>The reported player, or null when no such player is in the room.</summary>
        IClientPlayer? Reported { get; }

        /// <summary>The client id the report was aimed at (kept even when the player is gone).</summary>
        int ReportedClientId { get; }

        /// <summary>The reason picked in the menu.</summary>
        ReportReasons Reason { get; }

        /// <summary>
        ///     The result sent back to the reporter. Defaults to <see cref="ReportOutcome.Reported" />
        ///     (or NotFound when the target is gone); listeners may change it, e.g. to RateLimit.
        /// </summary>
        ReportOutcome Outcome { get; set; }
    }
}
