using Fmpostor.Api.Innersloth;

namespace Fmpostor.Api
{
    public static class SystemTypesExtensions
    {
        public static string GetFriendlyName(this SystemTypes type)
        {
            return SystemTypeHelpers.Names[(int)type];
        }
    }
}
