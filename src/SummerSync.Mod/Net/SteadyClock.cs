using System;
using System.Diagnostics;
using SummerSync.Core.Net;

namespace SummerSync.Unity.Net
{
    /// <summary>
    /// Seconds that only go forward, at the stopwatch's resolution: the game's stopwatch, checked against the system's
    /// tick count (<see cref="SteadyTime"/>, which says why).
    /// </summary>
    internal sealed class SteadyClock
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly SteadyTime time = new SteadyTime();

        public SteadyClock()
        {
            time.Read(watch.Elapsed.TotalSeconds, Environment.TickCount);
        }

        /// <summary>How many times the stopwatch jumped and the tick count was taken instead.</summary>
        public int Jumps
        {
            get { return time.Jumps; }
        }

        /// <summary>Seconds since this clock was made.</summary>
        public double Seconds
        {
            get { return time.Read(watch.Elapsed.TotalSeconds, Environment.TickCount); }
        }
    }
}
