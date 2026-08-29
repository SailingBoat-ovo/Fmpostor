using System;
using Fmpostor.Api.Utils;

namespace Fmpostor.Tools.ServerReplay
{
    public class FakeDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
