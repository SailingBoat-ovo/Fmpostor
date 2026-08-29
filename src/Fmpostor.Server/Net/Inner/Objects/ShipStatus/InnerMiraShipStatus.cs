using System.Collections.Generic;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net.Custom;
using Fmpostor.Api.Net.Inner.Objects.ShipStatus;
using Fmpostor.Server.Net.Inner.Objects.Systems;
using Fmpostor.Server.Net.Inner.Objects.Systems.ShipStatus;
using Fmpostor.Server.Net.State;

namespace Fmpostor.Server.Net.Inner.Objects.ShipStatus
{
    internal class InnerMiraShipStatus : InnerShipStatus, IInnerMiraShipStatus
    {
        public InnerMiraShipStatus(ICustomMessageManager<ICustomRpc> customMessageManager, Game game) : base(customMessageManager, game, MapTypes.MiraHQ)
        {
        }

        protected override void AddSystems(Dictionary<SystemTypes, ISystemType> systems)
        {
            base.AddSystems(systems);

            systems.Add(SystemTypes.Comms, new HudOverrideSystemType());
            systems.Add(SystemTypes.Reactor, new ReactorSystemType());
            systems.Add(SystemTypes.LifeSupp, new LifeSuppSystemType());
        }
    }
}
