using System.Collections.Generic;

namespace Fmpostor.Api.Net.Manager
{
    public interface IClientManager
    {
        IEnumerable<IClient> Clients { get; }
    }
}
