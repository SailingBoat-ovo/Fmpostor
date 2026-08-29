using System.Collections.Generic;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Manager;

namespace Fmpostor.Server.Net.Manager
{
    internal partial class ClientManager : IClientManager
    {
        IEnumerable<IClient> IClientManager.Clients => _clients.Values;
    }
}
