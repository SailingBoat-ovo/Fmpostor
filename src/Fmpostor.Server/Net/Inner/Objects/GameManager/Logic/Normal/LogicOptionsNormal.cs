using Fmpostor.Api.Events.Managers;
using Fmpostor.Server.Net.State;

namespace Fmpostor.Server.Net.Inner.Objects.GameManager.Logic.Normal;

internal class LogicOptionsNormal : LogicOptions
{
    public LogicOptionsNormal(Game game, IEventManager eventManager) : base(game, eventManager)
    {
    }
}
