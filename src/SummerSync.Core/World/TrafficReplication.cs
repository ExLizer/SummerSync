using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;
using SummerSync.Core.Replication;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="TrafficReplication"/>.</summary>
    public static class TrafficWire
    {
        /// <summary>Host to every client, unreliable: which of the traffic's groups and cars are on, and where each car is.</summary>
        public const byte MessageState = PanelWire.MessageRequest + 1;

        public const int MaxCars = 32;

        /// <summary>The cars of the highway are on.</summary>
        public const byte GroupHighway = 1;

        /// <summary>The cars of the dirt roads are on.</summary>
        public const byte GroupDirtRoads = 2;

        /// <summary>The rally driver of the dirt roads is on (a group of its own inside the dirt roads').</summary>
        public const byte GroupRally = 4;

        /// <summary>A car's brake lights are on.</summary>
        public const byte LightBrake = 1;

        /// <summary>
        /// Not a group: set in the message's byte of groups by a sender whose list was defined by names. Each car of the
        /// message then carries its key (<see cref="TrafficReplication.KeyOf"/>) after its place in the sender's list.
        /// </summary>
        public const byte Keyed = 0x80;
    }

    /// <summary>Messages of the people who walk the roads by themselves: a <see cref="TrafficReplication"/> of their own.</summary>
    public static class PeopleWire
    {
        /// <summary>
        /// Host to every client, unreliable: which of them are about and where each one is, each with the key of its name
        /// (<see cref="TrafficWire.Keyed"/>): the games may not have the same ones.
        /// </summary>
        public const byte MessageState = TrafficWire.MessageState + 1;

        /// <summary>One walks.</summary>
        public const byte Walking = 1;

        /// <summary>One stands.</summary>
        public const byte Standing = 2;
    }

    /// <summary>
    /// Messages of the rest of the host's people, animals and machines that move by themselves (the train, the boats
    /// of the lake, the moose…): a <see cref="TrafficReplication"/> of their own, listed by the data files.
    /// </summary>
    public static class NpcWire
    {
        /// <summary>Host to every client, unreliable: which of them are about and where each one is.</summary>
        public const byte MessageState = PeopleWire.MessageState + 1;
    }

    /// <summary>One car of the traffic, as the host's game has it.</summary>
    public struct TrafficCar
    {
        public bool Active;

        public BodyPose Pose;

        /// <summary>Metres a second along its own nose; negative backwards.</summary>
        public float Speed;

        public float Rpm;

        /// <summary>A car's lights (<see cref="TrafficWire.LightBrake"/>); what a person does (<see cref="PeopleWire.Walking"/>…).</summary>
        public byte Flags;

        /// <summary>Which animation it plays, one up from the game side's list; 0 for none or not said.</summary>
        public byte Clip;
    }

    /// <summary>
    /// The traffic of the host's game, shown in every game: the cars that drive the highway and the dirt roads by
    /// themselves. The host's game drives them all, and switches each group on while any player is where it drives;
    /// about fifteen times a second it says which groups and cars are on, where each car is, how fast it goes, its
    /// engine's speed and its lights. A client plays each car back a little behind, as it does the other players,
    /// and its own traffic drives nothing.
    ///
    /// Both sides list the cars the same way (<see cref="Define(int, uint)"/>); the list's shape travels with every
    /// message and a message of another shape is dropped. A list defined by names (<see cref="Define(IList{string})"/>:
    /// the people, which the games may not all have) is shared even when its shape is not the host's: each car then
    /// carries a key made of its name, and a client shows those of its own the host has, by key. One only the client has
    /// is never on; one only the host has is counted (<see cref="Unmatched"/>) and left out. This class only carries
    /// the news.
    /// </summary>
    public sealed class TrafficReplication
    {
        /// <summary>Seconds between two messages of the host: about fifteen a second.</summary>
        public const double SendInterval = 1.0 / 15.0;

        private const double MalformedLogInterval = 5.0;

        private static readonly SnapshotBufferConfig BufferConfig = new SnapshotBufferConfig { MaxExtrapolation = 0.2 };

        private readonly NetSession session;
        private readonly Logger log;
        private readonly byte messageType;
        private readonly PacketWriter writer = new PacketWriter(1200);
        private readonly PacketReader reader = new PacketReader();
        private SnapshotBuffer<BodyPose>[] buffers = new SnapshotBuffer<BodyPose>[0];
        private TrafficCar[] latest = new TrafficCar[0];

        // For a list defined by names, the key of each; null otherwise.
        private uint[] keys;
        private uint shape;
        private bool defined;
        private double clock;
        private double nextSend;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;

        public TrafficReplication(NetSession session, Logger log)
            : this(session, log, TrafficWire.MessageState)
        {
        }

        /// <summary>One more of its kind, on its own message (<see cref="PeopleWire.MessageState"/>).</summary>
        public TrafficReplication(NetSession session, Logger log, byte messageType)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            this.messageType = messageType;
            session.MessageReceived += OnMessage;
        }

        /// <summary>On a client: whether anything has been heard from the host.</summary>
        public bool HasState { get; private set; }

        /// <summary>On a client: the groups the host has on (<see cref="TrafficWire.GroupHighway"/> and so on).</summary>
        public byte Groups { get; private set; }

        public long Sent { get; private set; }

        public long Received { get; private set; }

        public long Malformed { get; private set; }

        /// <summary>
        /// On a client whose list was defined by names: whether the host's last message was of another list, and so
        /// was matched by name.
        /// </summary>
        public bool ByName { get; private set; }

        /// <summary>On a client matching by name: cars of the host's last message that are not on this machine's list.</summary>
        public int Unmatched { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>The game side's list of the traffic's cars: how many, and its shape.</summary>
        public void Define(int cars, uint listShape)
        {
            DefineList(cars, listShape, null);
        }

        /// <summary>
        /// The game side's list by the names of what is on it, in an order that is the same on every machine that has
        /// the same names. Its shape is <see cref="ShapeOf"/>. A message of a list of other names is matched by name.
        /// </summary>
        public void Define(IList<string> names)
        {
            if (names == null)
            {
                throw new ArgumentNullException("names");
            }

            var named = new uint[names.Count];
            bool unique = true;
            for (int i = 0; i < named.Length; i++)
            {
                named[i] = KeyOf(names[i]);
                for (int j = 0; j < i; j++)
                {
                    unique &= named[j] != named[i];
                }
            }

            if (!unique)
            {
                log.Warning(LogCategory.Vehicle, "Two of the " + names.Count + " names of a list have the same key: it is shared only with machines whose list is the same.");
            }

            DefineList(names.Count, ShapeOf(names), unique ? named : null);
        }

        /// <summary>The shape of a list of names: it changes with any name, their order and their number.</summary>
        public static uint ShapeOf(IList<string> names)
        {
            uint hash = 2166136261;
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                for (int c = 0; c < name.Length; c++)
                {
                    hash = (hash ^ name[c]) * 16777619;
                }

                hash = (hash ^ '\n') * 16777619;
            }

            return hash;
        }

        /// <summary>The key by which one name of a list is known on every machine.</summary>
        public static uint KeyOf(string name)
        {
            uint hash = 2166136261;
            for (int c = 0; c < name.Length; c++)
            {
                hash = (hash ^ name[c]) * 16777619;
            }

            return hash;
        }

        private void DefineList(int cars, uint listShape, uint[] named)
        {
            if (cars < 0 || cars > TrafficWire.MaxCars)
            {
                throw new ArgumentOutOfRangeException("cars");
            }

            keys = named;
            if (defined && cars == latest.Length && listShape == shape)
            {
                return;
            }

            shape = listShape;
            defined = true;
            HasState = false;
            ByName = false;
            Unmatched = 0;
            latest = new TrafficCar[cars];
            buffers = new SnapshotBuffer<BodyPose>[cars];
            for (int i = 0; i < cars; i++)
            {
                buffers[i] = new SnapshotBuffer<BodyPose>(BufferConfig);
            }
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
        }

        /// <summary>
        /// The host: what its game's traffic is now. Sent to every client at most every <see cref="SendInterval"/>;
        /// returns whether it was.
        /// </summary>
        public bool Report(double now, byte groups, TrafficCar[] cars)
        {
            if (!IsHost || !defined || cars == null || cars.Length != latest.Length || session.State != SessionState.Connected || now < nextSend)
            {
                return false;
            }

            // Keeps the beat whatever the frames are; after a pause, starts it again.
            nextSend = now - nextSend > SendInterval ? now + SendInterval : nextSend + SendInterval;
            uint active = 0;
            int count = 0;
            for (int i = 0; i < cars.Length; i++)
            {
                if (cars[i].Active)
                {
                    active |= 1u << i;
                    count++;
                }
            }

            writer.Reset();
            writer.WriteByte(messageType);
            writer.WriteUInt32(shape);
            writer.WriteDouble(now);
            writer.WriteByte((byte)((groups & ~TrafficWire.Keyed) | (keys != null ? TrafficWire.Keyed : 0)));
            writer.WriteUInt32(active);
            writer.WriteByte((byte)count);
            for (int i = 0; i < cars.Length; i++)
            {
                if (!cars[i].Active)
                {
                    continue;
                }

                BodyPose pose = cars[i].Pose;
                pose.Parent = 0;
                writer.WriteByte((byte)i);
                if (keys != null)
                {
                    writer.WriteUInt32(keys[i]);
                }

                writer.WriteByte(cars[i].Flags);
                writer.WriteByte(cars[i].Clip);
                pose.Write(writer);
                writer.WriteUInt16(unchecked((ushort)(short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(cars[i].Speed * 100.0)))));
                writer.WriteUInt16((ushort)Math.Max(0.0, Math.Min(ushort.MaxValue, Math.Round(cars[i].Rpm))));
            }

            session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, false);
            Sent++;
            return true;
        }

        /// <summary>On a client: whether the host has that car on.</summary>
        public bool IsActive(int car)
        {
            return car >= 0 && car < latest.Length && latest[car].Active;
        }

        /// <summary>On a client: where to show that car now; false while nothing is known of it.</summary>
        public bool Sample(int car, double now, out BodyPose pose)
        {
            pose = default(BodyPose);
            return car >= 0 && car < buffers.Length && latest[car].Active && buffers[car].Sample(now, out pose);
        }

        /// <summary>On a client: the last speed, engine speed and lights heard of that car.</summary>
        public TrafficCar Latest(int car)
        {
            return car >= 0 && car < latest.Length ? latest[car] : default(TrafficCar);
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
        }

        // The place on this list of the car with that key, or -1.
        private int IndexOf(uint key)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] == key)
                {
                    return i;
                }
            }

            return -1;
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            if (data[0] != messageType)
            {
                return;
            }

            try
            {
                if (IsHost || sender != Protocol.HostPlayerId)
                {
                    throw new PacketFormatException("The traffic, said by someone who is not the host.");
                }

                if (!defined)
                {
                    // The game here has not listed its traffic yet: the next message will do.
                    return;
                }

                reader.SetBuffer(data, 1, data.Length - 1);
                bool sameList = reader.ReadUInt32() == shape;
                double senderTime = reader.ReadDouble();
                byte groups = reader.ReadByte();
                bool keyed = (groups & TrafficWire.Keyed) != 0;
                groups = (byte)(groups & ~TrafficWire.Keyed);

                // Another list is matched by name, when both lists were defined by names.
                bool byName = !sameList;
                if (byName && (!keyed || keys == null))
                {
                    throw new PacketFormatException("The host's build lists the traffic's cars differently.");
                }

                uint active = reader.ReadUInt32();
                int count = reader.ReadByte();
                if (!byName && latest.Length < 32 && (active >> latest.Length) != 0)
                {
                    throw new PacketFormatException("A car past the end of the traffic's list.");
                }

                uint heard = 0;
                int unmatched = 0;
                for (int n = 0; n < count; n++)
                {
                    int car = reader.ReadByte();
                    uint key = keyed ? reader.ReadUInt32() : 0u;
                    byte lights = reader.ReadByte();
                    byte clip = reader.ReadByte();
                    BodyPose pose = BodyPose.Read(reader);
                    float speed = (short)reader.ReadUInt16() / 100f;
                    float rpm = reader.ReadUInt16();
                    if (car >= (byName ? TrafficWire.MaxCars : latest.Length) || (active & (1u << car)) == 0)
                    {
                        throw new PacketFormatException("Car " + car + " of the traffic, which is not on or not on the list.");
                    }

                    if (byName)
                    {
                        // The sender's place is not this list's: the car is the one of this list with its key.
                        car = IndexOf(key);
                        if (car < 0)
                        {
                            unmatched++;
                            continue;
                        }
                    }

                    heard |= 1u << car;
                    latest[car].Pose = pose;
                    latest[car].Speed = speed;
                    latest[car].Rpm = rpm;
                    latest[car].Flags = lights;
                    latest[car].Clip = clip;
                    buffers[car].Add(senderTime, pose, clock);
                }

                for (int car = 0; car < latest.Length; car++)
                {
                    bool on = ((byName ? heard : active) & (1u << car)) != 0;
                    if (!on && latest[car].Active)
                    {
                        buffers[car].Clear();
                    }

                    latest[car].Active = on;
                }

                Groups = groups;
                ByName = byName;
                Unmatched = unmatched;
                HasState = true;
                Received++;
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Vehicle, "Dropped a message about the traffic from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }
    }
}
