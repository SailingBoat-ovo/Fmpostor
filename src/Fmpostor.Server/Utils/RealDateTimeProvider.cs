using System;
using Fmpostor.Api.Utils;

namespace Fmpostor.Server.Utils
{
    public class RealDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
