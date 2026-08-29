using System.Collections.Generic;
using System.Threading.Tasks;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Innersloth.GameOptions;

namespace Fmpostor.Api.Games.Managers
{
    public interface IGameManager
    {
        IEnumerable<IGame> Games { get; }

        IGame? Find(GameCode code);

        /// <summary>
        /// Creates a new game.
        /// </summary>
        /// <param name="options">Game options.</param>
        /// <param name="filterOptions">Filter options.</param>
        /// <returns>Created game or null if creation was cancelled by a plugin.</returns>
        /// <exception cref="FmpostorException">Thrown when game creation failed.</exception>
        ValueTask<IGame?> CreateAsync(IGameOptions options, GameFilterOptions filterOptions);

        /// <summary>
        /// Destroys an existing game (used by automatic room cleanup).
        /// </summary>
        /// <param name="code">Code of the game to destroy.</param>
        ValueTask RemoveAsync(GameCode code);
    }
}
