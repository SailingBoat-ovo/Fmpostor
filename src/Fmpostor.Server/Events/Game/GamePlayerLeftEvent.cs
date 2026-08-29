using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events
{
    public class GamePlayerLeftEvent : IGamePlayerLeftEvent
    {
        public GamePlayerLeftEvent(IGame game, IClientPlayer player, bool isBan)
        {
            Game = game;
            Player = player;
            IsBan = isBan;
        }

        public IGame Game { get; }

        public IClientPlayer Player { get; }

        public bool IsBan { get; }
    }
}
