using Fmpostor.Api.Net;

namespace Fmpostor.Api.Events.Client
{
    public interface IClientEvent : IEvent
    {
        IHazelConnection Connection { get; }
    }
}
