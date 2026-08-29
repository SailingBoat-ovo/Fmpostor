using Fmpostor.Api.Games;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Net.State
{
    internal partial class ClientPlayer
    {
        /// <inheritdoc />
        IClient IClientPlayer.Client => Client;

        /// <inheritdoc />
        IGame IClientPlayer.Game => Game;

        /// <inheritdoc />
        IInnerPlayerControl? IClientPlayer.Character => Character;
    }
}
