using Fmpostor.Api.Events;
using Fmpostor.Api.Games;

namespace Fmpostor.Server.Events
{
    public class GameStartedEvent : IGameStartedEvent
    {
        public GameStartedEvent(IGame game)
        {
            Game = game;
        }

        public IGame Game { get; }
    }
}
