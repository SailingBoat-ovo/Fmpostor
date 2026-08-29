using Fmpostor.Api.Net.Inner.Objects.GameManager.Logic.HideAndSeek;

namespace Fmpostor.Api.Net.Inner.Objects.GameManager;

public interface IInnerHideAndSeekManager : IInnerGameManager
{
    ILogicGameFlowHnS LogicFlowHnS { get; }
}
