using Fmpostor.Api.Events;
using Fmpostor.Api.Games;

namespace Fmpostor.Server.Events
{
    public class GameStartingEvent : IGameStartingEvent
    {
        public GameStartingEvent(IGame game)
        {
            Game = game;
        }

        public IGame Game { get; }
    }
}
