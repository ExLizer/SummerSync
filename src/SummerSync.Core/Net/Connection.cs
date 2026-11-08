using System;
using System.Collections.Generic;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.Net
{
    public sealed class ConnectionConfig
    {
        public ConnectionConfig()
        {
            MaxDatagramSize = 1100;
            MaxPacketsPerFlush = 8;
            KeepAliveInterval = 0.1;
            MinResendDelay = 0.05;
            MaxResendDelay = 1.0;
            MaxReliableQueue = 4096;
        }

        /// <summary>Datagram size limit of the transport in use.</summary>
        public int MaxDatagramSize { get; set; }

        /// <summary>Cap on datagrams per flush, so a backlog is drained over several flushes instead of in a burst.</summary>
        public int MaxPacketsPerFlush { get; set; }

        /// <summary>When there is nothing to send, a bare packet still goes out this often to carry acks.</summary>
        public double KeepAliveInterval { get; set; }

        /// <summary>Shortest wait before a reliable message is sent a second time.</summary>
        public double MinResendDelay { get; set; }

        /// <summary>Longest wait between two tries of the same message; each try doubles the wait up to this.</summary>
        public double MaxResendDelay { get; set; }

        /// <summary>Reliable messages that may wait for an ack. Beyond this the peer is considered gone.</summary>
        public int MaxReliableQueue { get; set; }
    }

    /// <summary>A message handed up by <see cref="Connection.Receive"/>.</summary>
    public struct ReceivedMessage
    {
        public ReceivedMessage(byte[] data, bool reliable)
        {
            Data = data;
            Reliable = reliable;
        }

        public byte[] Data { get; }

        public bool Reliable { get; }
    }

    /// <summary>
    /// One end of a connection to a single peer, over unreliable datagrams.
    ///
    /// Every packet carries a sequence number and acknowledges the packets seen from the other side (the latest
    /// one plus a bitfield for the 32 before it). From that come the round trip time and the loss estimate.
    /// Messages are either unreliable (sent once) or reliable and ordered: those are numbered, resent until the
    /// peer's cumulative ack covers them, and delivered in order exactly once.
    ///
    /// The class has no clock and no socket: time is passed in and packets are handed to a callback, which keeps
    /// it deterministic under test.
    /// </summary>
    public sealed class Connection
    {
        /// <summary>First byte of every packet this class writes.</summary>
        public const byte DataKind = 4;

        // kind + connection id + sequence + ack + ack bits + ack delay + reliable ack
        private const int HeaderSize = 1 + 4 + 2 + 2 + 4 + 2 + 2;
        private const int SentWindow = 256;
        private const int AckBits = 32;
        private const int ReliableReceiveWindow = 1024;
        private const byte FlagReliable = 1;

        /// <summary>Value of the ack delay field meaning "nothing received yet, ignore the ack fields".</summary>
        private const ushort NoAckYet = ushort.MaxValue;
        private const double RttSmoothing = 0.125;

        // Each packet moves the estimate by this much. Larger values react faster but make the figure jump:
        // at 5 % real loss, 0.05 wanders between about 1 and 12 %, which is useless on a debug overlay.
        private const double LossSmoothing = 0.02;
        private const double ResendDelayBeforeFirstRtt = 0.25;

        private readonly ConnectionConfig config;
        private readonly uint connectionId;
        private readonly PacketWriter writer;
        private readonly SentPacket[] sent = new SentPacket[SentWindow];
        private readonly List<OutgoingReliable> reliableOut = new List<OutgoingReliable>();
        private readonly Queue<byte[]> unreliableOut = new Queue<byte[]>();
        private readonly Dictionary<ushort, byte[]> reliableHeld = new Dictionary<ushort, byte[]>();

        private ushort nextSequence;
        private ushort lossCursor;
        private ushort nextReliableId;
        private ushort nextExpectedReliable;

        private bool anyReceived;
        private ushort remoteSequence;
        private uint receivedBits;
        private double remoteSequenceTime;
        private bool ackOwed;

        private double lastSendTime = double.NegativeInfinity;
        private double roundTripTime;
        private double packetLoss;

        public Connection(ConnectionConfig config)
            : this(config, 0)
        {
        }

        /// <param name="id">
        /// A number both ends agreed on for this connection and no other. Every packet carries it, and a packet
        /// with another one is not ours: a transport may still deliver what was sent to an earlier connection
        /// with the same peer.
        /// </param>
        public Connection(ConnectionConfig config, uint id)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            this.config = config;
            connectionId = id;
            writer = new PacketWriter(config.MaxDatagramSize);
            LastReceiveTime = double.NegativeInfinity;
        }

        /// <summary>Largest message that fits in one packet. Bigger payloads need splitting by the caller.</summary>
        public int MaxMessageSize
        {
            get { return config.MaxDatagramSize - HeaderSize - 1 - 2 - PacketWriter.VarUInt32Size((uint)config.MaxDatagramSize); }
        }

        /// <summary>Smoothed round trip time in seconds; 0 until the first measurement.</summary>
        public double RoundTripTime
        {
            get { return roundTripTime; }
        }

        public bool HasRoundTripTime { get; private set; }

        /// <summary>Recent fraction of our packets the peer did not acknowledge, 0 to 1.</summary>
        public double PacketLoss
        {
            get { return packetLoss; }
        }

        public double LastReceiveTime { get; private set; }

        public long PacketsSent { get; private set; }

        public long PacketsReceived { get; private set; }

        public long BytesSent { get; private set; }

        public long BytesReceived { get; private set; }

        /// <summary>Times a reliable message had to be sent again.</summary>
        public long ReliableResends { get; private set; }

        /// <summary>Reliable messages sent but not yet acknowledged.</summary>
        public int ReliableQueueLength
        {
            get { return reliableOut.Count; }
        }

        /// <summary>Unreliable messages the last flush could not fit, and that are still waiting to go out.</summary>
        public int UnreliableLeftOver { get; private set; }

        /// <summary>Whether the peer stopped acknowledging to the point the reliable queue overflowed.</summary>
        public bool IsOverloaded
        {
            get { return reliableOut.Count > config.MaxReliableQueue; }
        }

        public void QueueReliable(byte[] data, int offset, int count)
        {
            CheckSize(count);
            reliableOut.Add(new OutgoingReliable(nextReliableId++, Copy(data, offset, count)));
        }

        public void QueueUnreliable(byte[] data, int offset, int count)
        {
            CheckSize(count);
            unreliableOut.Enqueue(Copy(data, offset, count));
        }

        /// <summary>
        /// Builds the packets that should go out now and passes each to <paramref name="send"/> as
        /// (buffer, length). The buffer is reused: the callback must consume it before returning.
        /// </summary>
        public void Flush(double now, Action<byte[], int> send)
        {
            FlushPackets(now, send);
            UnreliableLeftOver = unreliableOut.Count;
        }

        private void FlushPackets(double now, Action<byte[], int> send)
        {
            double resendDelay = ResendDelay();
            int packets = 0;
            int nextReliable = 0;

            while (packets < config.MaxPacketsPerFlush)
            {
                writer.Reset();
                WriteHeader(now);
                bool hasContent = false;

                for (; nextReliable < reliableOut.Count; nextReliable++)
                {
                    OutgoingReliable message = reliableOut[nextReliable];
                    if (now - message.LastSent < message.Wait)
                    {
                        continue;
                    }

                    if (RecordSize(message.Data.Length, true) > writer.Remaining)
                    {
                        break;
                    }

                    if (double.IsNegativeInfinity(message.LastSent))
                    {
                        message.Wait = resendDelay;
                    }
                    else
                    {
                        // Every try that goes unanswered doubles the wait. A peer on a loading screen answers
                        // nothing for seconds, and sending it the whole queue again every 70 ms all that time
                        // only fills the transport's buffers.
                        ReliableResends++;
                        message.Wait = Math.Min(config.MaxResendDelay, Math.Max(resendDelay, message.Wait * 2.0));
                    }

                    WriteRecord(message.Data, true, message.Id);
                    message.LastSent = now;
                    hasContent = true;
                }

                while (unreliableOut.Count > 0 && RecordSize(unreliableOut.Peek().Length, false) <= writer.Remaining)
                {
                    WriteRecord(unreliableOut.Dequeue(), false, 0);
                    hasContent = true;
                }

                if (!hasContent && (packets > 0 || (!ackOwed && now - lastSendTime < config.KeepAliveInterval)))
                {
                    return;
                }

                RegisterSent(now);
                send(writer.Buffer, writer.Length);
                PacketsSent++;
                BytesSent += writer.Length;
                lastSendTime = now;
                ackOwed = false;
                packets++;

                if (nextReliable >= reliableOut.Count && unreliableOut.Count == 0)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Processes one received packet. <paramref name="reader"/> must be positioned right after the kind byte.
        /// Messages that are ready for the application are appended to <paramref name="output"/>.
        /// Returns false, having changed nothing, for a packet of another connection.
        /// </summary>
        /// <exception cref="PacketFormatException">The packet is malformed; the caller should drop it.</exception>
        public bool Receive(double now, PacketReader reader, int datagramSize, List<ReceivedMessage> output)
        {
            if (reader.ReadUInt32() != connectionId)
            {
                return false;
            }

            ushort sequence = reader.ReadUInt16();
            ushort ack = reader.ReadUInt16();
            uint ackBits = reader.ReadUInt32();
            ushort ackDelayMs = reader.ReadUInt16();
            ushort reliableAck = reader.ReadUInt16();

            LastReceiveTime = now;
            PacketsReceived++;
            BytesReceived += datagramSize;

            // A packet that carries messages is answered at the next flush. Bare packets are not, or two idle
            // peers would keep acknowledging each other's acknowledgements at full rate.
            if (reader.Remaining > 0)
            {
                ackOwed = true;
            }

            if (ackDelayMs != NoAckYet)
            {
                ProcessAcks(now, ack, ackBits, ackDelayMs);
            }

            ProcessReliableAck(reliableAck);

            if (!MarkReceived(sequence, now))
            {
                // A duplicate, or too old to tell: everything in it was either handled already or will be resent.
                return true;
            }

            while (reader.Remaining > 0)
            {
                byte flags = reader.ReadByte();
                bool reliable = (flags & FlagReliable) != 0;
                ushort id = reliable ? reader.ReadUInt16() : (ushort)0;
                byte[] data = reader.ReadBytes(reader.ReadLength(config.MaxDatagramSize));
                if (reliable)
                {
                    AcceptReliable(id, data, output);
                }
                else
                {
                    output.Add(new ReceivedMessage(data, false));
                }
            }

            return true;
        }

        private static byte[] Copy(byte[] data, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            return copy;
        }

        private static int RecordSize(int dataLength, bool reliable)
        {
            return 1 + (reliable ? 2 : 0) + PacketWriter.VarUInt32Size((uint)dataLength) + dataLength;
        }

        private double ResendDelay()
        {
            if (!HasRoundTripTime)
            {
                return ResendDelayBeforeFirstRtt;
            }

            return Math.Min(config.MaxResendDelay, Math.Max(config.MinResendDelay, (roundTripTime * 1.5) + 0.02));
        }

        private void CheckSize(int count)
        {
            if (count < 0 || count > MaxMessageSize)
            {
                throw new ArgumentException("A message of " + count + " bytes does not fit in one packet (limit " + MaxMessageSize + ").");
            }
        }

        private void WriteHeader(double now)
        {
            // Until something arrives there is nothing to acknowledge, and the peer must not read the ack fields.
            ushort ackDelay = NoAckYet;
            if (anyReceived)
            {
                ackDelay = (ushort)Math.Max(0.0, Math.Min(NoAckYet - 1, (now - remoteSequenceTime) * 1000.0));
            }

            writer.WriteByte(DataKind);
            writer.WriteUInt32(connectionId);
            writer.WriteUInt16(nextSequence);
            writer.WriteUInt16(remoteSequence);
            writer.WriteUInt32(anyReceived ? receivedBits : 0);
            writer.WriteUInt16(ackDelay);
            writer.WriteUInt16(nextExpectedReliable);
        }

        private void WriteRecord(byte[] data, bool reliable, ushort id)
        {
            writer.WriteByte(reliable ? FlagReliable : (byte)0);
            if (reliable)
            {
                writer.WriteUInt16(id);
            }

            writer.WriteVarUInt32((uint)data.Length);
            writer.WriteBytes(data, 0, data.Length);
        }

        private void RegisterSent(double now)
        {
            int slot = nextSequence % SentWindow;
            if (sent[slot].Pending)
            {
                // Still unacknowledged a whole window later.
                RecordLoss(true);
            }

            sent[slot] = new SentPacket(nextSequence, now);
            nextSequence++;
        }

        private void ProcessAcks(double now, ushort ack, uint ackBits, ushort ackDelayMs)
        {
            // An ack for a packet we never sent means the header is garbage.
            if (PacketsSent == 0 || SequenceMath.IsNewer(ack, (ushort)(nextSequence - 1)))
            {
                return;
            }

            for (int i = 0; i <= AckBits; i++)
            {
                if (i > 0 && (ackBits & (1u << (i - 1))) == 0)
                {
                    continue;
                }

                ushort sequence = (ushort)(ack - i);
                int slot = sequence % SentWindow;
                if (!sent[slot].Pending || sent[slot].Sequence != sequence)
                {
                    continue;
                }

                sent[slot].Pending = false;
                RecordLoss(false);
                if (i == 0)
                {
                    // The peer tells how long it sat on the packet before answering; that is not network time.
                    double sample = now - sent[slot].Time - (ackDelayMs / 1000.0);
                    if (sample >= 0.0)
                    {
                        roundTripTime = HasRoundTripTime ? roundTripTime + (RttSmoothing * (sample - roundTripTime)) : sample;
                        HasRoundTripTime = true;
                    }
                }
            }

            // Anything older than what the ack bitfield can still cover and not acknowledged is lost.
            while (SequenceMath.IsNewer(ack, lossCursor) && SequenceMath.Distance(ack, lossCursor) > AckBits)
            {
                int slot = lossCursor % SentWindow;
                if (sent[slot].Pending && sent[slot].Sequence == lossCursor)
                {
                    sent[slot].Pending = false;
                    RecordLoss(true);
                }

                lossCursor++;
            }
        }

        private void RecordLoss(bool lost)
        {
            packetLoss += LossSmoothing * ((lost ? 1.0 : 0.0) - packetLoss);
        }

        /// <summary>The peer has received every reliable message before <paramref name="remoteNextExpected"/>.</summary>
        private void ProcessReliableAck(ushort remoteNextExpected)
        {
            int acknowledged = 0;
            while (acknowledged < reliableOut.Count && SequenceMath.IsNewer(remoteNextExpected, reliableOut[acknowledged].Id))
            {
                acknowledged++;
            }

            if (acknowledged > 0)
            {
                reliableOut.RemoveRange(0, acknowledged);
            }
        }

        /// <summary>Records a packet sequence as seen. Returns false for duplicates and packets too old to track.</summary>
        private bool MarkReceived(ushort sequence, double now)
        {
            if (!anyReceived)
            {
                anyReceived = true;
                remoteSequence = sequence;
                receivedBits = 0;
                remoteSequenceTime = now;
                return true;
            }

            if (SequenceMath.IsNewer(sequence, remoteSequence))
            {
                int shift = SequenceMath.Distance(sequence, remoteSequence);

                // The previous latest moves to bit (shift - 1). A uint shift of 32 or more is not a shift to zero in C#.
                receivedBits = shift > AckBits ? 0 : shift == AckBits ? 0x80000000u : (receivedBits << shift) | (1u << (shift - 1));
                remoteSequence = sequence;
                remoteSequenceTime = now;
                return true;
            }

            int back = SequenceMath.Distance(remoteSequence, sequence);
            if (back == 0 || back > AckBits)
            {
                return false;
            }

            uint mask = 1u << (back - 1);
            if ((receivedBits & mask) != 0)
            {
                return false;
            }

            receivedBits |= mask;
            return true;
        }

        private void AcceptReliable(ushort id, byte[] data, List<ReceivedMessage> output)
        {
            if (id == nextExpectedReliable)
            {
                output.Add(new ReceivedMessage(data, true));
                nextExpectedReliable++;

                byte[] held;
                while (reliableHeld.TryGetValue(nextExpectedReliable, out held))
                {
                    reliableHeld.Remove(nextExpectedReliable);
                    output.Add(new ReceivedMessage(held, true));
                    nextExpectedReliable++;
                }

                return;
            }

            // Ahead of what is expected: hold it until the gap is filled. Behind: already delivered.
            if (SequenceMath.IsNewer(id, nextExpectedReliable)
                && SequenceMath.Distance(id, nextExpectedReliable) < ReliableReceiveWindow
                && !reliableHeld.ContainsKey(id))
            {
                reliableHeld[id] = data;
            }
        }

        private struct SentPacket
        {
            public readonly ushort Sequence;
            public readonly double Time;
            public bool Pending;

            public SentPacket(ushort sequence, double time)
            {
                Sequence = sequence;
                Time = time;
                Pending = true;
            }
        }

        private sealed class OutgoingReliable
        {
            public OutgoingReliable(ushort id, byte[] data)
            {
                Id = id;
                Data = data;
                LastSent = double.NegativeInfinity;
            }

            public ushort Id { get; }

            public byte[] Data { get; }

            public double LastSent { get; set; }

            /// <summary>How long after <see cref="LastSent"/> the message goes out again if still unacknowledged.</summary>
            public double Wait { get; set; }
        }
    }
}
