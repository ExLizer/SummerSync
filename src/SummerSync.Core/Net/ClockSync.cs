using System;

namespace SummerSync.Core.Net
{
    /// <summary>
    /// Estimates the offset between the local clock and a remote one from request/response timestamps, the way
    /// NTP does: the remote time is assumed to have been read halfway through the round trip.
    ///
    /// That assumption is only as good as the trip was symmetric, and the trips with the least delay are the
    /// most symmetric ones, so the estimate comes from the fastest of the recent samples.
    /// </summary>
    public sealed class ClockSync
    {
        private readonly double[] roundTrips;
        private readonly double[] offsets;
        private int count;
        private int next;

        public ClockSync()
            : this(16)
        {
        }

        public ClockSync(int window)
        {
            if (window < 1)
            {
                throw new ArgumentOutOfRangeException("window");
            }

            roundTrips = new double[window];
            offsets = new double[window];
        }

        public bool HasEstimate
        {
            get { return count > 0; }
        }

        /// <summary>Seconds to add to a local time to get the remote time.</summary>
        public double Offset { get; private set; }

        /// <summary>Round trip time, in seconds, of the sample the current estimate comes from.</summary>
        public double BestRoundTrip { get; private set; }

        /// <summary>Number of samples received so far.</summary>
        public int Samples { get; private set; }

        /// <param name="localSend">Local time when the request left.</param>
        /// <param name="remoteTime">Remote time written into the response.</param>
        /// <param name="localReceive">Local time when the response arrived.</param>
        public void AddSample(double localSend, double remoteTime, double localReceive)
        {
            double roundTrip = localReceive - localSend;
            if (!(roundTrip >= 0.0))
            {
                // A response to a request from the future: stale or corrupt.
                return;
            }

            roundTrips[next] = roundTrip;
            offsets[next] = remoteTime + (roundTrip / 2.0) - localReceive;
            next = (next + 1) % roundTrips.Length;
            count = Math.Min(count + 1, roundTrips.Length);
            Samples++;

            int best = 0;
            for (int i = 1; i < count; i++)
            {
                if (roundTrips[i] < roundTrips[best])
                {
                    best = i;
                }
            }

            Offset = offsets[best];
            BestRoundTrip = roundTrips[best];
        }

        public double ToRemote(double localTime)
        {
            return localTime + Offset;
        }
    }

    /// <summary>Turns ever-growing totals into per-second rates, refreshed once per second.</summary>
    public sealed class RateSampler
    {
        private double windowStart = double.NaN;
        private long totalAtWindowStart;

        /// <summary>Units per second during the last completed one-second window.</summary>
        public double PerSecond { get; private set; }

        public void Update(double now, long total)
        {
            if (double.IsNaN(windowStart))
            {
                windowStart = now;
                totalAtWindowStart = total;
                return;
            }

            double elapsed = now - windowStart;
            if (elapsed >= 1.0)
            {
                PerSecond = (total - totalAtWindowStart) / elapsed;
                windowStart = now;
                totalAtWindowStart = total;
            }
        }
    }
}
