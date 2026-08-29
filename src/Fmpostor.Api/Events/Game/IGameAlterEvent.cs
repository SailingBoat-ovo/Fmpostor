namespace Fmpostor.Api.Events
{
    public interface IGameAlterEvent : IGameEvent
    {
        bool IsPublic { get; }
    }
}
