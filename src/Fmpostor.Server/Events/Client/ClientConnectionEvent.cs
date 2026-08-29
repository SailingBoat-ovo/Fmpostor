using Fmpostor.Api.Events.Client;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events.Client
{
    public class ClientConnectionEvent : IClientConnectionEvent
    {
        public ClientConnectionEvent(IHazelConnection connection, IMessageReader handshakeData)
        {
            Connection = connection;
            HandshakeData = handshakeData;
        }

        public IHazelConnection Connection { get; }

        public IMessageReader HandshakeData { get; }
    }
}
