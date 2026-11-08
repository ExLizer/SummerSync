using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="GuestReplication"/>.</summary>
    public static class GuestWire
    {
        /// <summary>Client to host, reliable: what this guest is now (its needs and where it stands); the first says it just came.</summary>
        public const byte MessageRecord = 105;

        /// <summary>Host to client, reliable: what the host's save has of that guest, or nothing.</summary>
        public const byte MessageRestore = 106;

        public const int MaxKeyBytes = 32;
        public const int MaxNameBytes = 64;
        public const int MaxValueNameBytes = 32;
        public const int MaxValues = 24;
    }

    /// <summary>What a guest is in the host's world, kept with the host's save: its needs and where it stands.</summary>
    public sealed class GuestRecord
    {
        public GuestRecord()
        {
            Values = new Dictionary<string, float>();
            Key = string.Empty;
            Name = string.Empty;
        }

        /// <summary>Who the guest is, the same every time it comes: its Steam id, or its name without Steam.</summary>
        public string Key { get; set; }

        public string Name { get; set; }

        /// <summary>Its needs and what is its own (cigarettes), by the game's global variable.</summary>
        public Dictionary<string, float> Values { get; private set; }

        public bool HasPlace { get; set; }

        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }

        public float Yaw { get; set; }

        public GuestRecord Copy()
        {
            var copy = new GuestRecord { Key = Key, Name = Name, HasPlace = HasPlace, X = X, Y = Y, Z = Z, Yaw = Yaw };
            foreach (KeyValuePair<string, float> value in Values)
            {
                copy.Values[value.Key] = value.Value;
            }

            return copy;
        }
    }

    /// <summary>
    /// The host's book of its guests, as text: one line each, "key|name|x|y|z|yaw|name=value;name=value". A line that does
    /// not read is left out.
    /// </summary>
    public static class GuestBook
    {
        public static string Write(IEnumerable<GuestRecord> records)
        {
            var text = new StringBuilder();
            text.Append("# SummerSync: the guests of this world, as the last save left them.\n");
            foreach (GuestRecord record in records)
            {
                text.Append(Clean(record.Key)).Append('|').Append(Clean(record.Name)).Append('|');
                if (record.HasPlace)
                {
                    text.Append(F(record.X)).Append('|').Append(F(record.Y)).Append('|').Append(F(record.Z)).Append('|').Append(F(record.Yaw));
                }
                else
                {
                    text.Append("|||");
                }

                text.Append('|');
                bool first = true;
                foreach (KeyValuePair<string, float> value in record.Values)
                {
                    text.Append(first ? string.Empty : ";").Append(Clean(value.Key)).Append('=').Append(F(value.Value));
                    first = false;
                }

                text.Append('\n');
            }

            return text.ToString();
        }

        public static List<GuestRecord> Read(string text)
        {
            var records = new List<GuestRecord>();
            string[] lines = (text ?? string.Empty).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] fields = line.Split('|');
                if (fields.Length != 7 || fields[0].Length == 0)
                {
                    continue;
                }

                var record = new GuestRecord { Key = fields[0], Name = fields[1] };
                float x;
                float y;
                float z;
                float yaw;
                if (Parse(fields[2], out x) && Parse(fields[3], out y) && Parse(fields[4], out z) && Parse(fields[5], out yaw))
                {
                    record.HasPlace = true;
                    record.X = x;
                    record.Y = y;
                    record.Z = z;
                    record.Yaw = yaw;
                }

                string[] values = fields[6].Split(';');
                for (int v = 0; v < values.Length; v++)
                {
                    int at = values[v].IndexOf('=');
                    float value;
                    if (at > 0 && Parse(values[v].Substring(at + 1), out value))
                    {
                        record.Values[values[v].Substring(0, at)] = value;
                    }
                }

                records.Add(record);
            }

            return records;
        }

        private static string Clean(string text)
        {
            return (text ?? string.Empty).Replace('|', ' ').Replace(';', ' ').Replace('=', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool Parse(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// The guests' own state, kept by the host with its save (Fase 8): a guest says now and then what it is (its needs,
    /// what is its own, where it stands); the host keeps the last word of each and writes them all when its game saves.
    /// When a guest comes into the host's world, the host tells it what the save has of it, and the guest takes it: it
    /// does not come in with the needs and the place of the host's player, which is what the save's player has.
    ///
    /// This class only carries the news and keeps the book. Reading and writing the game is the game side's.
    /// </summary>
    public sealed class GuestReplication
    {
        private const double MalformedLogInterval = 5.0;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(1200);
        private readonly PacketReader reader = new PacketReader();
        private readonly Dictionary<string, GuestRecord> saved = new Dictionary<string, GuestRecord>();
        private readonly Dictionary<string, GuestRecord> live = new Dictionary<string, GuestRecord>();
        private readonly HashSet<byte> answered = new HashSet<byte>();
        private double clock;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;

        public GuestReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
            session.PlayerLeft += OnPlayerLeft;
        }

        /// <summary>A client: what the host's save has of this guest (null: nothing, it comes as the save's player).</summary>
        public event Action<GuestRecord> RestoreWanted;

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

        /// <summary>The host: the book its save has, read from where it keeps it.</summary>
        public void LoadSaved(IEnumerable<GuestRecord> records)
        {
            saved.Clear();
            foreach (GuestRecord record in records)
            {
                saved[record.Key] = record;
            }
        }

        /// <summary>
        /// The host: the book as it should be saved now, every guest's last word over what the save had. What a guest said
        /// becomes what the save has from here on.
        /// </summary>
        public List<GuestRecord> Snapshot()
        {
            foreach (KeyValuePair<string, GuestRecord> record in live)
            {
                saved[record.Key] = record.Value.Copy();
            }

            return new List<GuestRecord>(saved.Values);
        }

        /// <summary>The host: the guests heard this session.</summary>
        public int LiveCount
        {
            get { return live.Count; }
        }

        /// <summary>A client: what this guest is now. The first of a session asks the host what its save has of it.</summary>
        public void Report(GuestRecord record, bool first)
        {
            if (IsHost || session.State != SessionState.Connected || record == null || string.IsNullOrEmpty(record.Key))
            {
                return;
            }

            writer.Reset();
            writer.WriteByte(GuestWire.MessageRecord);
            writer.WriteByte(first ? (byte)1 : (byte)0);
            Write(record);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.PlayerLeft -= OnPlayerLeft;
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            answered.Remove(player.Id);
        }

        private void Write(GuestRecord record)
        {
            writer.WriteString(Protocol.LimitText(record.Key, GuestWire.MaxKeyBytes));
            writer.WriteString(Protocol.LimitText(record.Name ?? string.Empty, GuestWire.MaxNameBytes));
            writer.WriteByte(record.HasPlace ? (byte)1 : (byte)0);
            writer.WriteSingle(record.X);
            writer.WriteSingle(record.Y);
            writer.WriteSingle(record.Z);
            writer.WriteSingle(record.Yaw);
            int count = Math.Min(record.Values.Count, GuestWire.MaxValues);
            writer.WriteByte((byte)count);
            int written = 0;
            foreach (KeyValuePair<string, float> value in record.Values)
            {
                if (written++ >= count)
                {
                    break;
                }

                writer.WriteString(Protocol.LimitText(value.Key, GuestWire.MaxValueNameBytes));
                writer.WriteSingle(value.Value);
            }
        }

        private GuestRecord Read()
        {
            var record = new GuestRecord
            {
                Key = reader.ReadString(GuestWire.MaxKeyBytes),
                Name = reader.ReadString(GuestWire.MaxNameBytes),
                HasPlace = reader.ReadByte() != 0,
                X = reader.ReadSingle(),
                Y = reader.ReadSingle(),
                Z = reader.ReadSingle(),
                Yaw = reader.ReadSingle(),
            };
            int count = reader.ReadByte();
            if (record.Key.Length == 0 || count > GuestWire.MaxValues || !Finite(record.X) || !Finite(record.Y) || !Finite(record.Z) || !Finite(record.Yaw))
            {
                throw new PacketFormatException("A guest's record with no key, too many values or a place that is not a number.");
            }

            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString(GuestWire.MaxValueNameBytes);
                float value = reader.ReadSingle();
                if (name.Length == 0 || !Finite(value))
                {
                    throw new PacketFormatException("A guest's value with no name or that is not a number.");
                }

                record.Values[name] = value;
            }

            return record;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != GuestWire.MessageRecord && type != GuestWire.MessageRestore)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if ((type == GuestWire.MessageRecord) != IsHost || (!IsHost && sender != Protocol.HostPlayerId))
                {
                    throw new PacketFormatException("Guest message " + type + " sent the wrong way.");
                }

                if (type == GuestWire.MessageRecord)
                {
                    bool first = reader.ReadByte() != 0;
                    GuestRecord record = Read();
                    if (first)
                    {
                        // Its world was just loaded from the save: what it has now is the save's player's, not its own.
                        Answer(sender, record.Key);
                        return;
                    }

                    live[record.Key] = record;
                    return;
                }

                bool has = reader.ReadByte() != 0;
                GuestRecord restore = has ? Read() : null;
                Action<GuestRecord> handler = RestoreWanted;
                if (handler != null)
                {
                    handler(restore);
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Net, "Dropped a guest message from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        // The host: what its save has of a guest who just came, once a session.
        private void Answer(byte player, string key)
        {
            if (!answered.Add(player))
            {
                return;
            }

            GuestRecord record;
            bool has = live.TryGetValue(key, out record) || saved.TryGetValue(key, out record);
            writer.Reset();
            writer.WriteByte(GuestWire.MessageRestore);
            writer.WriteByte(has ? (byte)1 : (byte)0);
            if (has)
            {
                Write(record);
            }

            session.Send(player, writer.Buffer, 0, writer.Length, true);
            log.Info(LogCategory.Net, "Guests: player " + player + " came into this world; " + (has ? "it takes up what it was (" + record.Values.Count + " value(s)" + (record.HasPlace ? ", its place" : string.Empty) + ")." : "this save has nothing of it yet."));
        }
    }
}
