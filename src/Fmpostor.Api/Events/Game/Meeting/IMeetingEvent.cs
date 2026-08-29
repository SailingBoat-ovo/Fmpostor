using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Api.Events.Meeting
{
    public interface IMeetingEvent : IGameEvent
    {
        IInnerMeetingHud MeetingHud { get; }
    }
}
