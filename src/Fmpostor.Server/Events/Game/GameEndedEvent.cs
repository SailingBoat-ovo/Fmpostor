using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth;

namespace Fmpostor.Server.Events
{
    public class GameEndedEvent : IGameEndedEvent
    {
        public GameEndedEvent(IGame game, GameOverReason gameOverReason)
        {
            Game = game;
            GameOverReason = gameOverReason;
        }

        public IGame Game { get; }

        public GameOverReason GameOverReason { get; }
    }
}
