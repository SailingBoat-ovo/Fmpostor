using Fmpostor.Api.Events.Meeting;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Events.Meeting
{
    public class MeetingStartedEvent : IMeetingStartedEvent
    {
        public MeetingStartedEvent(IGame game, IInnerMeetingHud meetingHud)
        {
            Game = game;
            MeetingHud = meetingHud;
        }

        public IGame Game { get; }

        public IInnerMeetingHud MeetingHud { get; }
    }
}
