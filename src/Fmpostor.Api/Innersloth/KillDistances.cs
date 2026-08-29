using System;

namespace Fmpostor.Api.Innersloth
{
    [Flags]
    public enum KillDistances : byte
    {
        Short = 0,
        Normal = 1,
        Long = 2,
    }
}
