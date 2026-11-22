using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events
{
    public class GamePlayerReportEvent : IGamePlayerReportEvent
    {
        public GamePlayerReportEvent(IGame game, IClientPlayer reporter, IClientPlayer? reported, int reportedClientId, ReportReasons reason)
        {
            Game = game;
            Reporter = reporter;
            Reported = reported;
            ReportedClientId = reportedClientId;
            Reason = reason;
            Outcome = reported == null ? ReportOutcome.NotReportedNotFound : ReportOutcome.Reported;
        }

        public IGame Game { get; }

        public IClientPlayer Reporter { get; }

        public IClientPlayer? Reported { get; }

        public int ReportedClientId { get; }

        public ReportReasons Reason { get; }

        public ReportOutcome Outcome { get; set; }
    }
}
