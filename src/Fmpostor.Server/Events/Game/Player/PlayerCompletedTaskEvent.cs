using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Events.Player
{
    public class PlayerCompletedTaskEvent : IPlayerCompletedTaskEvent
    {
        public PlayerCompletedTaskEvent(IGame game, IClientPlayer clientPlayer, IInnerPlayerControl playerControl, ITaskInfo task)
        {
            Game = game;
            ClientPlayer = clientPlayer;
            PlayerControl = playerControl;
            Task = task;
        }

        public IGame Game { get; }

        public IClientPlayer ClientPlayer { get; }

        public IInnerPlayerControl PlayerControl { get; }

        public ITaskInfo Task { get; }
    }
}
