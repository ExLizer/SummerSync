using System;

namespace SummerSync.Core.Net
{
    /// <summary>
    /// Seconds that only go forward, made of two readings: a fine stopwatch that may jump, and a coarse tick count in
    /// milliseconds that wraps but never jumps.
    ///
    /// The game's Mono turns Windows' performance counter into hundreds of nanoseconds by multiplying before dividing,
    /// which overflows once the machine has been up for about a day: from then on its stopwatch jumps about 51 hours back
    /// once every 51 hours of uptime (184467 s: 2^64 hundreds of nanoseconds over a counter of 10 MHz). Measured on
    /// 2026-10-15 (docs/GAME_NOTES.md): in the middle of a session the host's network time went from 80 minutes to minus
    /// 49.9 hours, and from then on the host sent nothing to anyone. Each reading adds the stopwatch's step since the last
    /// one; a step that differs by more than a second from the tick count's (read across its own wrap) is taken from the
    /// tick count instead.
    /// </summary>
    public sealed class SteadyTime
    {
        /// <summary>How far the two steps may disagree before the stopwatch's is not believed.</summary>
        public const double MostDisagreement = 1.0;

        private bool started;
        private double lastWatch;
        private int lastTicks;
        private double seconds;

        /// <summary>How many times the stopwatch jumped and the tick count was taken instead.</summary>
        public int Jumps { get; private set; }

        /// <summary>Seconds since the first reading.</summary>
        public double Seconds
        {
            get { return seconds; }
        }

        /// <summary>Takes a reading: the stopwatch in seconds and the tick count in milliseconds. Returns <see cref="Seconds"/>.</summary>
        public double Read(double watchSeconds, int tickMilliseconds)
        {
            if (!started)
            {
                started = true;
                lastWatch = watchSeconds;
                lastTicks = tickMilliseconds;
                return seconds;
            }

            double watchStep = watchSeconds - lastWatch;
            double tickStep = unchecked((uint)(tickMilliseconds - lastTicks)) / 1000.0;
            lastWatch = watchSeconds;
            lastTicks = tickMilliseconds;
            if (Math.Abs(watchStep - tickStep) > MostDisagreement)
            {
                Jumps++;
                watchStep = tickStep;
            }

            if (watchStep > 0)
            {
                seconds += watchStep;
            }

            return seconds;
        }
    }
}
