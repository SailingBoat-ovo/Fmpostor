using Fmpostor.Api.Events;
using Fmpostor.Api.Games;

namespace Fmpostor.Server.Events
{
    public class GameAlterEvent : IGameAlterEvent
    {
        public GameAlterEvent(IGame game, bool isPublic)
        {
            Game = game;
            IsPublic = isPublic;
        }

        public IGame Game { get; }

        public bool IsPublic { get; }
    }
}
