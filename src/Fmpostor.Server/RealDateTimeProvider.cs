using System;
using Fmpostor.Api.Utils;

namespace Fmpostor.Server
{
    public class RealDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
