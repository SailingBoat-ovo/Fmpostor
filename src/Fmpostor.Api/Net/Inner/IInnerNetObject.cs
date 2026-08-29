using Fmpostor.Api.Games;

namespace Fmpostor.Api.Net.Inner
{
    public interface IInnerNetObject
    {
        public uint NetId { get; }

        public int OwnerId { get; }

        public IGame Game { get; }
    }
}
