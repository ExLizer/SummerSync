using System;
using System.Collections.Generic;

namespace SummerSync.Core.Net
{
    /// <summary>
    /// Sends and receives datagrams to and from peers identified by a number (a Steam ID, or a synthetic id for
    /// local transports). Delivery is unreliable and unordered by contract: reliability lives above, in
    /// <see cref="Connection"/>, so it behaves the same over every transport.
    /// </summary>
    public interface ITransport
    {
        /// <summary>Largest datagram, in bytes, that <see cref="Send"/> accepts.</summary>
        int MaxDatagramSize { get; }

        /// <summary>Sends the first <paramref name="count"/> bytes of <paramref name="data"/>. Never blocks.</summary>
        void Send(ulong peer, byte[] data, int count);

        /// <summary>
        /// Reads the next pending datagram into <paramref name="buffer"/>, which must hold
        /// <see cref="MaxDatagramSize"/> bytes. Returns false when there is none.
        /// </summary>
        bool Receive(byte[] buffer, out int count, out ulong peer);
    }

    /// <summary>16-bit sequence numbers that wrap around.</summary>
    public static class SequenceMath
    {
        /// <summary>Whether <paramref name="a"/> comes after <paramref name="b"/>, allowing for wrap-around.</summary>
        public static bool IsNewer(ushort a, ushort b)
        {
            return a != b && (ushort)(a - b) < 0x8000;
        }

        /// <summary>How far <paramref name="newer"/> is ahead of <paramref name="older"/>.</summary>
        public static int Distance(ushort newer, ushort older)
        {
            return (ushort)(newer - older);
        }
    }

    /// <summary>
    /// An in-memory network. Endpoints created from the same instance can exchange datagrams, which makes it
    /// possible to run a host and a client inside one process: in unit tests, and in the game's self test.
    /// </summary>
    public sealed class LoopbackNetwork
    {
        private readonly Dictionary<ulong, Endpoint> endpoints = new Dictionary<ulong, Endpoint>();
        private readonly int maxDatagramSize;

        public LoopbackNetwork()
            : this(1200)
        {
        }

        public LoopbackNetwork(int maxDatagramSize)
        {
            this.maxDatagramSize = maxDatagramSize;
        }

        public ITransport CreateEndpoint(ulong id)
        {
            var endpoint = new Endpoint(this, id);
            endpoints[id] = endpoint;
            return endpoint;
        }

        /// <summary>Makes an endpoint unreachable, as if its machine had dropped off the network.</summary>
        public void RemoveEndpoint(ulong id)
        {
            endpoints.Remove(id);
        }

        private sealed class Endpoint : ITransport
        {
            private readonly LoopbackNetwork network;
            private readonly ulong id;
            private readonly Queue<KeyValuePair<ulong, byte[]>> inbox = new Queue<KeyValuePair<ulong, byte[]>>();

            public Endpoint(LoopbackNetwork network, ulong id)
            {
                this.network = network;
                this.id = id;
            }

            public int MaxDatagramSize
            {
                get { return network.maxDatagramSize; }
            }

            public void Send(ulong peer, byte[] data, int count)
            {
                if (count > network.maxDatagramSize)
                {
                    throw new ArgumentException("Datagram of " + count + " bytes exceeds the limit of " + network.maxDatagramSize + ".");
                }

                Endpoint target;
                if (!network.endpoints.TryGetValue(peer, out target))
                {
                    // Like UDP: sending to nobody is not an error.
                    return;
                }

                var copy = new byte[count];
                Buffer.BlockCopy(data, 0, copy, 0, count);
                target.inbox.Enqueue(new KeyValuePair<ulong, byte[]>(id, copy));
            }

            public bool Receive(byte[] buffer, out int count, out ulong peer)
            {
                if (inbox.Count == 0)
                {
                    count = 0;
                    peer = 0;
                    return false;
                }

                KeyValuePair<ulong, byte[]> datagram = inbox.Dequeue();
                Buffer.BlockCopy(datagram.Value, 0, buffer, 0, datagram.Value.Length);
                count = datagram.Value.Length;
                peer = datagram.Key;
                return true;
            }
        }
    }
}
