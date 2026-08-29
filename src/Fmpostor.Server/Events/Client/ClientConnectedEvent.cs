using Fmpostor.Api.Events.Client;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events.Client
{
    public class ClientConnectedEvent : IClientConnectedEvent
    {
        public ClientConnectedEvent(IHazelConnection connection, IClient client)
        {
            Connection = connection;
            Client = client;
        }

        public IHazelConnection Connection { get; }

        public IClient Client { get; }
    }
}
