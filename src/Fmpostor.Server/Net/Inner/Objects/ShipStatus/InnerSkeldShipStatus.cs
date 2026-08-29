using System.Collections.Generic;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net.Custom;
using Fmpostor.Api.Net.Inner.Objects.ShipStatus;
using Fmpostor.Server.Net.Inner.Objects.Systems;
using Fmpostor.Server.Net.Inner.Objects.Systems.ShipStatus;
using Fmpostor.Server.Net.State;

namespace Fmpostor.Server.Net.Inner.Objects.ShipStatus
{
    internal class InnerSkeldShipStatus : InnerShipStatus, IInnerSkeldShipStatus
    {
        public InnerSkeldShipStatus(ICustomMessageManager<ICustomRpc> customMessageManager, Game game) : base(customMessageManager, game, MapTypes.Skeld)
        {
        }

        protected override void AddSystems(Dictionary<SystemTypes, ISystemType> systems)
        {
            base.AddSystems(systems);

            systems.Add(SystemTypes.Doors, new AutoDoorsSystemType(Doors));
            systems.Add(SystemTypes.Comms, new HudOverrideSystemType());
            systems.Add(SystemTypes.Security, new SecurityCameraSystemType());
            systems.Add(SystemTypes.Reactor, new ReactorSystemType());
            systems.Add(SystemTypes.LifeSupp, new LifeSuppSystemType());
        }
    }
}
