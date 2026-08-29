using Fmpostor.Api.Events;
using Fmpostor.Api.Games;

namespace Fmpostor.Server.Events
{
    public class GameDestroyedEvent : IGameDestroyedEvent
    {
        public GameDestroyedEvent(IGame game)
        {
            Game = game;
        }

        public IGame Game { get; }
    }
}
