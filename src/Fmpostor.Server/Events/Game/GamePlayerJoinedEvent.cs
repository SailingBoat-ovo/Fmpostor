using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events
{
    public class GamePlayerJoinedEvent : IGamePlayerJoinedEvent
    {
        public GamePlayerJoinedEvent(IGame game, IClientPlayer player)
        {
            Game = game;
            Player = player;
        }

        public IGame Game { get; }

        public IClientPlayer Player { get; }
    }
}
