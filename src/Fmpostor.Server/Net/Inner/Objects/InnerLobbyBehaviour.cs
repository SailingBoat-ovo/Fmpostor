using System;
using System.Threading.Tasks;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Custom;
using Fmpostor.Api.Net.Inner.Objects;
using Fmpostor.Server.Net.State;

namespace Fmpostor.Server.Net.Inner.Objects
{
    internal class InnerLobbyBehaviour : InnerNetObject, IInnerLobbyBehaviour
    {
        public InnerLobbyBehaviour(ICustomMessageManager<ICustomRpc> customMessageManager, Game game) : base(customMessageManager, game)
        {
            Components.Add(this);
        }

        public override ValueTask<bool> SerializeAsync(IMessageWriter writer, bool initialState)
        {
            throw new NotImplementedException();
        }

        public override ValueTask DeserializeAsync(IClientPlayer sender, IClientPlayer? target, IMessageReader reader, bool initialState)
        {
            throw new NotImplementedException();
        }
    }
}
