using System.Threading.Tasks;
using Fmpostor.Api.Events;

namespace Fmpostor.Api.Plugins
{
    public interface IPlugin : IEventListener
    {
        ValueTask EnableAsync();

        ValueTask DisableAsync();

        ValueTask ReloadAsync();
    }
}
