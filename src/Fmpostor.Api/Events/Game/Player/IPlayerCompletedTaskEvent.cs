using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Api.Events.Player
{
    public interface IPlayerCompletedTaskEvent : IPlayerEvent
    {
        ITaskInfo Task { get; }
    }
}
