using System.Collections.Generic;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Net.Inner.Objects
{
    internal partial class InnerPlayerInfo : InnerNetObject, IInnerPlayerInfo
    {
        IEnumerable<ITaskInfo> IInnerPlayerInfo.Tasks => Tasks;
    }
}
