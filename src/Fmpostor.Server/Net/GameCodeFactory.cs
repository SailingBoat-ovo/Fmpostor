using Fmpostor.Api.Games;

namespace Fmpostor.Server.Net
{
    public class GameCodeFactory : IGameCodeFactory
    {
        public GameCode Create()
        {
            return GameCode.Create();
        }
    }
}
