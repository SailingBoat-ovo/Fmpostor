using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events
{
    public class GameCreatedEvent : IGameCreatedEvent
    {
        public GameCreatedEvent(IGame game, IClient? host)
        {
            Game = game;
            Host = host;
        }

        public IGame Game { get; }

        public IClient? Host { get; }
    }
}
