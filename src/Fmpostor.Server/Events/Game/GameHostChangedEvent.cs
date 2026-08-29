using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events
{
    public class GameHostChangedEvent : IGameHostChangedEvent
    {
        public GameHostChangedEvent(IGame game, IClientPlayer previousHost, IClientPlayer? newHost)
        {
            Game = game;
            PreviousHost = previousHost;
            NewHost = newHost;
        }

        public IGame Game { get; }

        public IClientPlayer PreviousHost { get; }

        public IClientPlayer? NewHost { get; }
    }
}
