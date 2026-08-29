using Fmpostor.Api.Net;

namespace Fmpostor.Api.Events
{
    public interface IGamePlayerJoinedEvent : IGameEvent
    {
        IClientPlayer Player { get; }
    }
}
