using System.Threading.Tasks;
using Fmpostor.Api.Net.Inner;

namespace Fmpostor.Api.Net.Custom
{
    public interface ICustomRpc : ICustomMessage
    {
        ValueTask<bool> HandleRpcAsync(IInnerNetObject innerNetObject, IClientPlayer sender, IClientPlayer? target, IMessageReader reader);
    }
}
