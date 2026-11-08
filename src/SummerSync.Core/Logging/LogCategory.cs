using System;

namespace SummerSync.Core.Logging
{
    /// <summary>Subsystem a log entry belongs to. Categories can be enabled independently.</summary>
    [Flags]
    public enum LogCategory
    {
        None = 0,
        General = 1 << 0,
        Net = 1 << 1,
        Authority = 1 << 2,
        Vehicle = 1 << 3,
        Fsm = 1 << 4,
        Passenger = 1 << 5,
        Player = 1 << 6,
        All = General | Net | Authority | Vehicle | Fsm | Passenger | Player,
    }
}
