using Fmpostor.Api.Games;

namespace Fmpostor.Api.Events
{
    /// <summary>
    ///     Called whenever a new <see cref="IGame" /> is destroyed.
    /// </summary>
    public interface IGameDestroyedEvent : IGameEvent
    {
    }
}
