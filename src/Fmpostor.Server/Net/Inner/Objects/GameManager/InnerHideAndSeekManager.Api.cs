using Fmpostor.Api.Net.Inner.Objects.GameManager;
using Fmpostor.Api.Net.Inner.Objects.GameManager.Logic.HideAndSeek;

namespace Fmpostor.Server.Net.Inner.Objects.GameManager;

internal partial class InnerHideAndSeekManager
{
    ILogicGameFlowHnS IInnerHideAndSeekManager.LogicFlowHnS => LogicFlowHnS;
}
