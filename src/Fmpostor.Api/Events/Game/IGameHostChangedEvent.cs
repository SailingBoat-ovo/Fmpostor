using Fmpostor.Api.Net;

namespace Fmpostor.Api.Events
{
    public interface IGameHostChangedEvent : IGameEvent
    {
        IClientPlayer PreviousHost { get; }

        IClientPlayer? NewHost { get; }
    }
}
