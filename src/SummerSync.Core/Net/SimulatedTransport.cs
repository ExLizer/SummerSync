using System;
using System.Collections.Generic;

namespace SummerSync.Core.Net
{
    /// <summary>
    /// Bad network conditions to impose on outgoing datagrams. The values can be changed while running.
    /// Each side of a connection applies its own, so latency adds up over a round trip.
    /// </summary>
    public sealed class NetworkConditions
    {
        /// <summary>Delay added to every datagram, in milliseconds.</summary>
        public float LatencyMs { get; set; }

        /// <summary>Random variation of the delay, plus or minus, in milliseconds. Causes reordering.</summary>
        public float JitterMs { get; set; }

        /// <summary>Chance, 0 to 100, that a datagram is dropped.</summary>
        public float LossPercent { get; set; }

        /// <summary>Chance, 0 to 100, that a datagram is delivered twice.</summary>
        public float DuplicatePercent { get; set; }

        public bool IsPerfect
        {
            get { return LatencyMs <= 0f && JitterMs <= 0f && LossPercent <= 0f && DuplicatePercent <= 0f; }
        }
    }

    /// <summary>
    /// Wraps any transport and degrades what it sends: delay, jitter, loss and duplication. Because it sits
    /// between the protocol and the real transport, the same bad conditions can be tried over loopback, UDP or
    /// Steam, on a LAN or on a single machine.
    /// </summary>
    public sealed class SimulatedTransport : ITransport
    {
        private readonly ITransport inner;
        private readonly NetworkConditions conditions;
        private readonly Random random;
        private readonly List<Pending> pending = new List<Pending>();
        private double now;
        private long order;

        public SimulatedTransport(ITransport inner, NetworkConditions conditions, int seed)
        {
            if (inner == null)
            {
                throw new ArgumentNullException("inner");
            }

            if (conditions == null)
            {
                throw new ArgumentNullException("conditions");
            }

            this.inner = inner;
            this.conditions = conditions;
            random = new Random(seed);
        }

        public int MaxDatagramSize
        {
            get { return inner.MaxDatagramSize; }
        }

        public NetworkConditions Conditions
        {
            get { return conditions; }
        }

        /// <summary>Datagrams the simulator decided to drop so far.</summary>
        public long Dropped { get; private set; }

        /// <summary>Datagrams currently being held back.</summary>
        public int InFlight
        {
            get { return pending.Count; }
        }

        /// <summary>Advances time and releases the datagrams that are due. Call once per frame, before sending.</summary>
        public void Update(double time)
        {
            now = time;
            if (pending.Count == 0)
            {
                return;
            }

            // Released in due order; ties keep the order they were sent in.
            pending.Sort(Pending.Compare);
            int released = 0;
            while (released < pending.Count && pending[released].Due <= now)
            {
                inner.Send(pending[released].Peer, pending[released].Data, pending[released].Data.Length);
                released++;
            }

            pending.RemoveRange(0, released);
        }

        /// <summary>
        /// Sends everything that is still being held back, right now. For shutting down: a goodbye that sits in
        /// the delay queue of a transport about to be closed would never leave.
        /// </summary>
        public void ReleaseAll()
        {
            pending.Sort(Pending.Compare);
            for (int i = 0; i < pending.Count; i++)
            {
                inner.Send(pending[i].Peer, pending[i].Data, pending[i].Data.Length);
            }

            pending.Clear();
        }

        public void Send(ulong peer, byte[] data, int count)
        {
            if (conditions.IsPerfect && pending.Count == 0)
            {
                inner.Send(peer, data, count);
                return;
            }

            if (Chance(conditions.LossPercent))
            {
                Dropped++;
                return;
            }

            Schedule(peer, data, count);
            if (Chance(conditions.DuplicatePercent))
            {
                Schedule(peer, data, count);
            }
        }

        public bool Receive(byte[] buffer, out int count, out ulong peer)
        {
            return inner.Receive(buffer, out count, out peer);
        }

        private bool Chance(float percent)
        {
            return percent > 0f && random.NextDouble() * 100.0 < percent;
        }

        private void Schedule(ulong peer, byte[] data, int count)
        {
            double jitter = conditions.JitterMs <= 0f ? 0.0 : ((random.NextDouble() * 2.0) - 1.0) * conditions.JitterMs;
            double delay = Math.Max(0.0, conditions.LatencyMs + jitter) / 1000.0;
            var copy = new byte[count];
            Buffer.BlockCopy(data, 0, copy, 0, count);
            pending.Add(new Pending(now + delay, order++, peer, copy));
        }

        private struct Pending
        {
            public readonly double Due;
            public readonly long Order;
            public readonly ulong Peer;
            public readonly byte[] Data;

            public Pending(double due, long order, ulong peer, byte[] data)
            {
                Due = due;
                Order = order;
                Peer = peer;
                Data = data;
            }

            public static int Compare(Pending a, Pending b)
            {
                int byDue = a.Due.CompareTo(b.Due);
                return byDue != 0 ? byDue : a.Order.CompareTo(b.Order);
            }
        }
    }
}
