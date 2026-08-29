using System;
using System.Collections.Generic;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Net.Inner.Objects
{
    internal partial class InnerMeetingHud : IInnerMeetingHud
    {
        IReadOnlyCollection<IInnerMeetingHud.IPlayerVoteArea> IInnerMeetingHud.PlayerStates => Array.AsReadOnly(_playerStates);

        IInnerPlayerInfo? IInnerMeetingHud.Reporter => Reporter;
    }
}
