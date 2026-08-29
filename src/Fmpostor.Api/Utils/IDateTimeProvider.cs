using System;

namespace Fmpostor.Api.Utils
{
    public interface IDateTimeProvider
    {
        DateTimeOffset UtcNow { get; }
    }
}
