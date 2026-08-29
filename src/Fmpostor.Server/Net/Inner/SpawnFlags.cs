using System;

namespace Fmpostor.Server.Net.Inner
{
    [Flags]
    public enum SpawnFlags : byte
    {
        None = 0,
        IsClientCharacter = 1,
    }
}
