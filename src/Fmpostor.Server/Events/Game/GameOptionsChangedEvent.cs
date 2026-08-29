using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using static Fmpostor.Api.Events.IGameOptionsChangedEvent;

namespace Fmpostor.Server.Events
{
    public class GameOptionsChangedEvent : IGameOptionsChangedEvent
    {
        public GameOptionsChangedEvent(IGame game, ChangeReason changedBy)
        {
            Game = game;
            ChangedBy = changedBy;
        }

        public ChangeReason ChangedBy { get; }

        public IGame Game { get; }
    }
}
