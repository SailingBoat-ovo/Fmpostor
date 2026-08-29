using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Events.Player
{
    public class PlayerSetStartCounterEvent : IPlayerSetStartCounterEvent
    {
        public PlayerSetStartCounterEvent(IGame game, IClientPlayer clientPlayer, IInnerPlayerControl playerControl, byte secondsLeft)
        {
            Game = game;
            ClientPlayer = clientPlayer;
            PlayerControl = playerControl;
            SecondsLeft = secondsLeft;
        }

        public byte SecondsLeft { get; }

        public IClientPlayer ClientPlayer { get; }

        public IInnerPlayerControl PlayerControl { get; }

        public IGame Game { get; }
    }
}
