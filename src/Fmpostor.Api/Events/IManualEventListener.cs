using System.Threading.Tasks;

namespace Fmpostor.Api.Events
{
    public interface IManualEventListener : IEventListener
    {
        EventPriority Priority { get; set; }

        public bool CanExecute<T>();

        public ValueTask Execute(IEvent @event);
    }
}
