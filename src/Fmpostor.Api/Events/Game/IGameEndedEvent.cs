using Fmpostor.Api.Innersloth;

namespace Fmpostor.Api.Events
{
    public interface IGameEndedEvent : IGameEvent
    {
        public GameOverReason GameOverReason { get; }
    }
}
