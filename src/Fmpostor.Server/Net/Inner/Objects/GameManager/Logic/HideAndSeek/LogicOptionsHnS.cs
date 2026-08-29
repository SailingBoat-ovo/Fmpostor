using Fmpostor.Api.Events.Managers;
using Fmpostor.Server.Net.State;

namespace Fmpostor.Server.Net.Inner.Objects.GameManager.Logic.HideAndSeek;

internal class LogicOptionsHnS : LogicOptions
{
    public LogicOptionsHnS(Game game, IEventManager eventManager) : base(game, eventManager)
    {
    }
}
