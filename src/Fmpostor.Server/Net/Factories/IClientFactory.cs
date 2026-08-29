using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Net.Factories
{
    internal interface IClientFactory
    {
        ClientBase Create(IHazelConnection connection, string name, GameVersion clientVersion, Language language, QuickChatModes chatMode, PlatformSpecificData platformSpecificData);
    }
}
