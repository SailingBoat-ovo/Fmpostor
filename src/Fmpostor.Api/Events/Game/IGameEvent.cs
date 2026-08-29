using Fmpostor.Api.Games;

namespace Fmpostor.Api.Events
{
    public interface IGameEvent : IEvent
    {
        /// <summary>
        ///     Gets the <see cref="IGame" /> this event belongs to.
        /// </summary>
        IGame Game { get; }
    }
}
