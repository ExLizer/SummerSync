using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.World;

namespace SummerSync.Core.Objects
{
    /// <summary>Messages of <see cref="ItemReplication"/>.</summary>
    public static class ItemWire
    {
        /// <summary>
        /// Either way, reliable: a loose object's number, its name and the values of its state machine's variables,
        /// as the game that changed it has them. The host passes it on to every client, the sender too, so that
        /// whatever arrives last everywhere is the same.
        /// </summary>
        public const byte MessageState = TallyWire.MessageRequest + 1;

        /// <summary>
        /// Either way, reliable: a player's game threw something away by itself (an empty bottle after a drink):
        /// what it was made from, its name, where it is and how it moves. The host passes it on with the player.
        /// </summary>
        public const byte MessageThrown = TallyWire.MessageRequest + 2;

        public const int MaxNameBytes = 64;
        public const int MaxValues = 24;
        public const int MaxValueNameBytes = 32;

        /// <summary>A message about an object's state is at most this long, so that it fits in one packet.</summary>
        public const int MaxStateBytes = 900;

        public const byte KindBool = 0;
        public const byte KindInt = 1;
        public const byte KindFloat = 2;
    }

    /// <summary>One variable of a loose object's state machine.</summary>
    public struct ItemValue
    {
        public string Name;

        /// <summary><see cref="ItemWire.KindBool"/>, <see cref="ItemWire.KindInt"/> or <see cref="ItemWire.KindFloat"/>.</summary>
        public byte Kind;

        /// <summary>A bool as 0 or 1, or an int.</summary>
        public int Whole;

        public float Number;

        public static ItemValue Bool(string name, bool value)
        {
            return new ItemValue { Name = name, Kind = ItemWire.KindBool, Whole = value ? 1 : 0 };
        }

        public static ItemValue Int(string name, int value)
        {
            return new ItemValue { Name = name, Kind = ItemWire.KindInt, Whole = value };
        }

        public static ItemValue Float(string name, float value)
        {
            return new ItemValue { Name = name, Kind = ItemWire.KindFloat, Number = value };
        }
    }

    /// <summary>Something a player's game threw away by itself, as it was a moment after.</summary>
    public struct ThrownItem
    {
        /// <summary>The prefab it was made from, by name.</summary>
        public string Prefab;

        /// <summary>The name the game gave it.</summary>
        public string Name;

        public BodyPose Pose;

        public BodyMotion Motion;

        /// <summary>Whose game threw it. Filled in by the host.</summary>
        public byte Player;
    }

    /// <summary>
    /// What happens to a loose object besides moving: a can drunk empty, a bottle taken out of a case, renamed by
    /// the game as it goes. The game keeps that in the variables of the object's state machine; the machine whose
    /// game changed them tells them, and the others write them into their copy. And what a player's game throws
    /// away by itself, which is new: the others make it too, from the same prefab.
    ///
    /// This class only carries the news. Reading and writing the game's objects is the game side's.
    /// </summary>
    public sealed class ItemReplication
    {
        private const double MalformedLogInterval = 5.0;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(1000);
        private readonly PacketReader reader = new PacketReader();
        private double clock;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;

        public ItemReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
        }

        /// <summary>Another machine says what the object with this number is now: its name and values.</summary>
        public event Action<ushort, string, IList<ItemValue>> StateWanted;

        /// <summary>Another player's game threw this away: make it here too.</summary>
        public event Action<ThrownItem> ThrownWanted;

        public long StatesTold { get; private set; }

        public long StatesHeard { get; private set; }

        public long ThrownTold { get; private set; }

        public long ThrownHeard { get; private set; }

        public long Malformed { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
        }

        /// <summary>Bytes a message about this object's state takes, without its type byte.</summary>
        public static int StateSize(string name, IList<ItemValue> values)
        {
            int size = 2 + StringSize(name) + 1;
            for (int i = 0; i < values.Count && i < ItemWire.MaxValues; i++)
            {
                size += StringSize(values[i].Name) + 1 + 4;
            }

            return size;
        }

        /// <summary>This machine's game changed the object. The others are told.</summary>
        public bool TellState(ushort id, string name, IList<ItemValue> values)
        {
            if (session.State != SessionState.Connected || values == null)
            {
                return false;
            }

            name = Protocol.LimitText(name ?? string.Empty, ItemWire.MaxNameBytes);
            if (values.Count > ItemWire.MaxValues || StateSize(name, values) > ItemWire.MaxStateBytes)
            {
                log.Warning(LogCategory.Authority, "Object " + id + " '" + name + "' has more to say than fits in one message; the others do not hear of it.");
                return false;
            }

            StatesTold++;
            writer.Reset();
            writer.WriteByte(ItemWire.MessageState);
            WriteState(id, name, values);
            Send(Protocol.HostPlayerId);
            return true;
        }

        /// <summary>
        /// On the host: what the object is in this game, told to one client only (one whose world has just loaded, from a
        /// save the host has played on from). The client takes it like any other news of the object.
        /// </summary>
        public bool TellStateTo(byte player, ushort id, string name, IList<ItemValue> values)
        {
            if (!IsHost || session.State != SessionState.Connected || values == null)
            {
                return false;
            }

            name = Protocol.LimitText(name ?? string.Empty, ItemWire.MaxNameBytes);
            if (values.Count > ItemWire.MaxValues || StateSize(name, values) > ItemWire.MaxStateBytes)
            {
                return false;
            }

            StatesTold++;
            writer.Reset();
            writer.WriteByte(ItemWire.MessageState);
            WriteState(id, name, values);
            session.Send(player, writer.Buffer, 0, writer.Length, true);
            return true;
        }

        /// <summary>This machine's game threw something away by itself. The others are told.</summary>
        public void TellThrown(ThrownItem thrown)
        {
            if (session.State != SessionState.Connected || string.IsNullOrEmpty(thrown.Prefab))
            {
                return;
            }

            thrown.Prefab = Protocol.LimitText(thrown.Prefab, ItemWire.MaxNameBytes);
            thrown.Name = Protocol.LimitText(thrown.Name ?? string.Empty, ItemWire.MaxNameBytes);
            thrown.Pose.Parent = 0;
            thrown.Player = session.LocalPlayerId;
            ThrownTold++;
            writer.Reset();
            writer.WriteByte(ItemWire.MessageThrown);
            WriteThrown(thrown);
            Send(Protocol.HostPlayerId);
        }

        /// <summary>One line for the overlay, or null before anything happened.</summary>
        public string Describe()
        {
            if (StatesTold == 0 && StatesHeard == 0 && ThrownTold == 0 && ThrownHeard == 0)
            {
                return null;
            }

            return "Items: " + StatesTold + " change(s) told, " + StatesHeard + " heard; " + ThrownTold + " thing(s) thrown here, " + ThrownHeard + " by the others"
                + (Malformed > 0 ? ", " + Malformed + " malformed" : string.Empty);
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

        // To the host from a client; to every client from the host, except the one it came from.
        private void Send(byte except)
        {
            if (!IsHost)
            {
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
                return;
            }

            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                byte id = players[i].Id;
                if (id != session.LocalPlayerId && id != except)
                {
                    session.Send(id, writer.Buffer, 0, writer.Length, true);
                }
            }
        }

        private void WriteState(ushort id, string name, IList<ItemValue> values)
        {
            writer.WriteUInt16(id);
            writer.WriteString(name);
            writer.WriteByte((byte)values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                ItemValue value = values[i];
                writer.WriteString(Protocol.LimitText(value.Name ?? string.Empty, ItemWire.MaxValueNameBytes));
                writer.WriteByte(value.Kind);
                if (value.Kind == ItemWire.KindFloat)
                {
                    writer.WriteSingle(value.Number);
                }
                else
                {
                    writer.WriteInt32(value.Whole);
                }
            }
        }

        private List<ItemValue> ReadState(out ushort id, out string name)
        {
            id = reader.ReadUInt16();
            name = reader.ReadString(ItemWire.MaxNameBytes);
            int count = reader.ReadByte();
            if (count > ItemWire.MaxValues)
            {
                throw new PacketFormatException("An object with " + count + " values.");
            }

            var values = new List<ItemValue>(count);
            for (int i = 0; i < count; i++)
            {
                var value = new ItemValue { Name = reader.ReadString(ItemWire.MaxValueNameBytes), Kind = reader.ReadByte() };
                if (value.Name.Length == 0 || value.Kind > ItemWire.KindFloat)
                {
                    throw new PacketFormatException("A value with no name, or of kind " + value.Kind + ".");
                }

                if (value.Kind == ItemWire.KindFloat)
                {
                    value.Number = reader.ReadSingle();
                    if (float.IsNaN(value.Number) || float.IsInfinity(value.Number))
                    {
                        throw new PacketFormatException("A value that is not a number.");
                    }
                }
                else
                {
                    value.Whole = reader.ReadInt32();
                }

                values.Add(value);
            }

            return values;
        }

        private void WriteThrown(ThrownItem thrown)
        {
            writer.WriteByte(thrown.Player);
            writer.WriteString(thrown.Prefab);
            writer.WriteString(thrown.Name);
            thrown.Pose.Write(writer);
            thrown.Motion.Write(writer);
        }

        private ThrownItem ReadThrown()
        {
            var thrown = new ThrownItem
            {
                Player = reader.ReadByte(),
                Prefab = reader.ReadString(ItemWire.MaxNameBytes),
                Name = reader.ReadString(ItemWire.MaxNameBytes),
                Pose = BodyPose.Read(reader),
                Motion = BodyMotion.Read(reader),
            };
            if (thrown.Prefab.Length == 0 || thrown.Pose.Parent != 0)
            {
                throw new PacketFormatException("Something thrown that was made from nothing, or onto a vehicle.");
            }

            return thrown;
        }

        private static int StringSize(string value)
        {
            int bytes = string.IsNullOrEmpty(value) ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);
            return PacketWriter.VarUInt32Size((uint)bytes) + bytes;
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != ItemWire.MessageState && type != ItemWire.MessageThrown)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (!IsHost && sender != Protocol.HostPlayerId)
                {
                    throw new PacketFormatException("News of an object from someone who is not the host.");
                }

                if (type == ItemWire.MessageState)
                {
                    ushort id;
                    string name;
                    List<ItemValue> values = ReadState(out id, out name);
                    if (IsHost)
                    {
                        // To every client, the sender too: the host may have told it something else a moment ago.
                        writer.Reset();
                        writer.WriteByte(ItemWire.MessageState);
                        WriteState(id, name, values);
                        Send(Protocol.HostPlayerId);
                    }

                    StatesHeard++;
                    Action<ushort, string, IList<ItemValue>> handler = StateWanted;
                    if (handler != null)
                    {
                        handler(id, name, values);
                    }
                }
                else
                {
                    ThrownItem thrown = ReadThrown();
                    if (IsHost)
                    {
                        thrown.Player = sender;
                        writer.Reset();
                        writer.WriteByte(ItemWire.MessageThrown);
                        WriteThrown(thrown);
                        Send(sender);
                    }

                    ThrownHeard++;
                    Action<ThrownItem> handler = ThrownWanted;
                    if (handler != null)
                    {
                        handler(thrown);
                    }
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Authority, "Dropped a message about an object from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }
    }
}
