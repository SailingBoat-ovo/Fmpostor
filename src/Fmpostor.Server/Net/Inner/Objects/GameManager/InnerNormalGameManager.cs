using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Net.Custom;
using Fmpostor.Api.Net.Inner.Objects.GameManager;
using Fmpostor.Server.Net.Inner.Objects.GameManager.Logic;
using Fmpostor.Server.Net.Inner.Objects.GameManager.Logic.Normal;
using Fmpostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.Inner.Objects.GameManager;

internal class InnerNormalGameManager : InnerGameManager, IInnerNormalGameManager
{
    public InnerNormalGameManager(ICustomMessageManager<ICustomRpc> customMessageManager, Game game, ILogger<InnerGameManager> logger, IEventManager eventManager) : base(customMessageManager, game, logger)
    {
        LogicFlow = AddGameLogic(new LogicGameFlowNormal());
        LogicMinigame = AddGameLogic(new LogicMinigame());
        LogicRoleSelection = AddGameLogic(new LogicRoleSelectionNormal());
        LogicUsables = AddGameLogic(new LogicUsablesBasic());
        LogicOptions = AddGameLogic(new LogicOptionsNormal(game, eventManager));
    }
}
