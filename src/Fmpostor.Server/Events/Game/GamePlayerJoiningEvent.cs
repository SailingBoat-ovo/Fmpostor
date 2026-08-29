using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events
{
    public class GamePlayerJoiningEvent : IGamePlayerJoiningEvent
    {
        public GamePlayerJoiningEvent(IGame game, IClientPlayer player)
        {
            Game = game;
            Player = player;
        }

        public IGame Game { get; }

        public IClientPlayer Player { get; }

        public GameJoinResult? JoinResult { get; set; }
    }
}
