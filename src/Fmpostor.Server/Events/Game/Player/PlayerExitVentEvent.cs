using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Innersloth.Maps;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Events.Player
{
    public class PlayerExitVentEvent : IPlayerExitVentEvent
    {
        public PlayerExitVentEvent(IGame game, IClientPlayer sender, IInnerPlayerControl innerPlayerPhysics, VentData vent)
        {
            Game = game;
            ClientPlayer = sender;
            PlayerControl = innerPlayerPhysics;
            Vent = vent;
        }

        public IGame Game { get; }

        public IClientPlayer ClientPlayer { get; }

        public IInnerPlayerControl PlayerControl { get; }

        public VentData Vent { get; }
    }
}
