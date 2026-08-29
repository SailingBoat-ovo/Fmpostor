using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Events.Player
{
    public class PlayerStartMeetingEvent : IPlayerStartMeetingEvent
    {
        public PlayerStartMeetingEvent(IGame game, IClientPlayer clientPlayer, IInnerPlayerControl playerControl, IInnerPlayerControl? body)
        {
            Game = game;
            ClientPlayer = clientPlayer;
            PlayerControl = playerControl;
            Body = body;
        }

        public IGame Game { get; }

        public IClientPlayer ClientPlayer { get; }

        public IInnerPlayerControl PlayerControl { get; }

        public IInnerPlayerControl? Body { get; }
    }
}
