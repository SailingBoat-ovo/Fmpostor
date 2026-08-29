using Fmpostor.Api.Net;

namespace Fmpostor.Api.Events
{
    public interface IGamePlayerLeftEvent : IGameEvent
    {
        IClientPlayer Player { get; }

        bool IsBan { get; }
    }
}
