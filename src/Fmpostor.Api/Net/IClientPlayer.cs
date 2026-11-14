using System;
using System.Threading.Tasks;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net.Inner;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Api.Net
{
    /// <summary>
    ///     Represents a player in <see cref="IGame" />.
    /// </summary>
    public interface IClientPlayer
    {
        /// <summary>
        ///     Gets the client that belongs to the player.
        /// </summary>
        IClient Client { get; }

        /// <summary>
        ///     Gets the game where the <see cref="IClientPlayer" /> belongs to.
        /// </summary>
        IGame Game { get; }

        /// <summary>
        ///     Gets or sets the current limbo state of the player.
        /// </summary>
        LimboStates Limbo { get; set; }

        IInnerPlayerControl? Character { get; }

        public bool IsHost { get; }

        /// <summary>
        ///     Gets a value indicating whether this player's connection is gone
        ///     but the slot is still reserved for a reconnect (grace period
        ///     running). Always false while the feature is disabled.
        /// </summary>
        bool IsAwaitingReconnect { get; }

        /// <summary>
        ///     Gets the moment the reserved slot expires, or null when the player
        ///     is not awaiting a reconnect.
        /// </summary>
        DateTimeOffset? ReconnectExpiresAt { get; }

        /// <summary>
        ///     Checks if the specified <see cref="IInnerNetObject" /> is owned by <see cref="IClientPlayer" />.
        /// </summary>
        /// <param name="netObject">The <see cref="IInnerNetObject" />.</param>
        /// <returns>Returns true if owned by <see cref="IClientPlayer" />.</returns>
        bool IsOwner(IInnerNetObject netObject);

        ValueTask KickAsync();

        ValueTask BanAsync();
    }
}
