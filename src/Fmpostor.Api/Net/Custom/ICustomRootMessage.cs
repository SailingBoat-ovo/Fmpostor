using System.Threading.Tasks;

namespace Fmpostor.Api.Net.Custom
{
    public interface ICustomRootMessage : ICustomMessage
    {
        ValueTask HandleMessageAsync(IClient client, IMessageReader reader, MessageType messageType);
    }
}
