using System.Linq;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Innersloth;

namespace Fmpostor.Api.Games
{
    public static class GameManagerExtensions
    {
        public static int GetGameCount(this IGameManager manager, MapFlags map)
        {
            return manager.Games.Count(game => map.HasFlag((MapFlags)(1 << (byte)game.Options.Map)));
        }
    }
}
