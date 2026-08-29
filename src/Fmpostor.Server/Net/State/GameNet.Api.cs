using Fmpostor.Api.Net.Inner;
using Fmpostor.Api.Net.Inner.Objects;
using Fmpostor.Api.Net.Inner.Objects.GameManager;
using Fmpostor.Api.Net.Inner.Objects.ShipStatus;

namespace Fmpostor.Server.Net.State
{
    /// <inheritdoc />
    internal partial class GameNet : IGameNet
    {
        IInnerGameManager? IGameNet.GameManager => GameManager;

        IInnerLobbyBehaviour? IGameNet.LobbyBehaviour => LobbyBehaviour;

        IInnerGameData IGameNet.GameData => GameData;

        IInnerVoteBanSystem? IGameNet.VoteBan => VoteBan;

        IInnerShipStatus? IGameNet.ShipStatus => ShipStatus;
    }
}
