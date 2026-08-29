using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Events.Player
{
    public class PlayerSpawnedEvent : IPlayerSpawnedEvent
    {
        public PlayerSpawnedEvent(IGame game, IClientPlayer clientPlayer, IInnerPlayerControl playerControl)
        {
            Game = game;
            ClientPlayer = clientPlayer;
            PlayerControl = playerControl;
        }

        public IGame Game { get; }

        public IClientPlayer ClientPlayer { get; }

        public IInnerPlayerControl PlayerControl { get; }
    }
}
