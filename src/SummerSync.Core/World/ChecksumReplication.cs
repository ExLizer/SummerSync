using System;
using System.Collections.Generic;
using System.Text;
using SummerSync.Core.Hashing;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="ChecksumReplication"/>.</summary>
    public static class ChecksumWire
    {
        /// <summary>Host to every client, reliable: a round's number and, for each section, its digest and how many entries it has.</summary>
        public const byte MessageDigests = 101;

        /// <summary>Client to host, reliable: a section whose digest differs here, twice in a row; the host sends its entries.</summary>
        public const byte MessageAsk = 102;

        /// <summary>Host to one client, reliable: part of a section's entries (key and value), and whether it is the last part.</summary>
        public const byte MessageDetail = 103;

        /// <summary>Client to host, reliable: the entries of a section that differ here; the host sends what they are again.</summary>
        public const byte MessageFix = 104;

        /// <summary>Entries in one detail or fix message (16 bytes each).</summary>
        public const int EntriesPerMessage = 55;

        /// <summary>Most sections a session checks.</summary>
        public const int MaxSections = 32;
    }

    /// <summary>
    /// A place as a section's value: each coordinate in 5 cm steps, 21 bits each (52 km either way of the map's middle).
    /// The digest sees it in whole cells (<see cref="Coarse"/>); a closer look compares the places themselves.
    /// </summary>
    public static class CheckPlace
    {
        public const float Step = 0.05f;
        private const int Bits = 21;
        private const long Half = 1L << (Bits - 1);
        private const ulong Mask = (1UL << Bits) - 1;

        public static ulong Pack(float x, float y, float z)
        {
            return Part(x) | (Part(y) << Bits) | (Part(z) << (2 * Bits));
        }

        public static void Unpack(ulong value, out float x, out float y, out float z)
        {
            x = Back(value & Mask);
            y = Back((value >> Bits) & Mask);
            z = Back((value >> (2 * Bits)) & Mask);
        }

        /// <summary>The same place, put in the middle of its cell of that size.</summary>
        public static ulong Coarse(ulong value, float cell)
        {
            float x;
            float y;
            float z;
            Unpack(value, out x, out y, out z);
            return Pack(Cell(x, cell), Cell(y, cell), Cell(z, cell));
        }

        /// <summary>Whether two places are within that many metres of each other.</summary>
        public static bool Near(ulong a, ulong b, float within)
        {
            float ax;
            float ay;
            float az;
            float bx;
            float by;
            float bz;
            Unpack(a, out ax, out ay, out az);
            Unpack(b, out bx, out by, out bz);
            float dx = ax - bx;
            float dy = ay - by;
            float dz = az - bz;
            return (dx * dx) + (dy * dy) + (dz * dz) <= within * within;
        }

        private static ulong Part(float value)
        {
            long steps = (long)Math.Round(value / Step);
            steps = Math.Max(-(Half - 1), Math.Min(Half - 1, steps));
            return (ulong)(steps + Half) & Mask;
        }

        private static float Back(ulong part)
        {
            return ((long)part - Half) * Step;
        }

        private static float Cell(float value, float cell)
        {
            return ((float)Math.Floor(value / cell) * cell) + (cell * 0.5f);
        }
    }

    /// <summary>The sections every machine checks, by number.</summary>
    public static class CheckSections
    {
        /// <summary>Where the loose objects the host simulates rest.</summary>
        public const byte Places = 1;

        /// <summary>Where the vehicles the host simulates are parked.</summary>
        public const byte Vehicles = 2;

        /// <summary>What each loose thing holds (contents, renames, food's spoiling).</summary>
        public const byte Contents = 3;

        /// <summary>The doors and what is switched with a click, as each game shows them.</summary>
        public const byte Switches = 4;

        /// <summary>The makers' numbers.</summary>
        public const byte Makers = 5;

        /// <summary>The house's electricity.</summary>
        public const byte Electricity = 6;

        /// <summary>The flyer job's mailboxes.</summary>
        public const byte Mailboxes = 7;

        /// <summary>The named tallies the host remembers.</summary>
        public const byte Tallies = 8;
    }

    /// <summary>
    /// The named tallies as a section: what the host remembers against what a client heard last. A tally a client lost or
    /// has otherwise is told to it again; its game does with it what it does with any word of the host's.
    /// </summary>
    public sealed class TallyCheckSection : ICheckSection
    {
        private readonly TallyReplication tallies;
        private readonly List<KeyValuePair<string, int[]>> known = new List<KeyValuePair<string, int[]>>();

        public TallyCheckSection(TallyReplication tallies)
        {
            this.tallies = tallies;
        }

        public byte Id
        {
            get { return CheckSections.Tallies; }
        }

        public string Name
        {
            get { return "tallies"; }
        }

        public bool Ready
        {
            get { return tallies.HasHeardAnswer; }
        }

        public bool CountsMissing
        {
            get { return true; }
        }

        public bool SendsEntries
        {
            get { return false; }
        }

        public void Read(List<CheckEntry> entries)
        {
            known.Clear();
            tallies.ReadKnown(known);
            for (int i = 0; i < known.Count; i++)
            {
                entries.Add(new CheckEntry(ChecksumReplication.KeyOf(known[i].Key), ChecksumReplication.ValueOf(known[i].Value)));
            }
        }

        public ulong Coarse(ulong value)
        {
            return value;
        }

        public bool Same(ulong host, ulong here)
        {
            return host == here;
        }

        public void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere)
        {
        }

        public void FixFor(byte player, IList<ulong> keys)
        {
            known.Clear();
            tallies.ReadKnown(known);
            for (int i = 0; i < known.Count; i++)
            {
                if (keys.Contains(ChecksumReplication.KeyOf(known[i].Key)))
                {
                    tallies.ResendTo(player, known[i].Key);
                }
            }
        }
    }

    /// <summary>One thing of a section: what it is called (the same on every machine) and what state it is in.</summary>
    public struct CheckEntry
    {
        public ulong Key;
        public ulong Value;

        public CheckEntry(ulong key, ulong value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>
    /// A part of the shared world that can be checked: what the game of this machine shows of it, as entries, and how to
    /// bring entries back to the host's. The same section has the same number on every machine.
    /// </summary>
    public interface ICheckSection
    {
        byte Id { get; }

        string Name { get; }

        /// <summary>Whether this machine's game has what the section reads (its world is loaded and the section found it).</summary>
        bool Ready { get; }

        /// <summary>Adds this machine's entries. The order does not matter.</summary>
        void Read(List<CheckEntry> entries);

        /// <summary>The value as the digest sees it: coarse enough that what rounds differently is not a difference.</summary>
        ulong Coarse(ulong value);

        /// <summary>Whether a value here is the host's value, near enough.</summary>
        bool Same(ulong host, ulong here);

        /// <summary>Whether an entry that only one machine has is a difference (things that move in and out of a section are not).</summary>
        bool CountsMissing { get; }

        /// <summary>
        /// Whether the host sends the entries themselves every round instead of a digest: for places, which a digest cannot
        /// compare within a distance. An entry is set right once it differs two rounds in a row.
        /// </summary>
        bool SendsEntries { get; }

        /// <summary>A client: these entries differ from the host's (keys); bring this game to the host's where it can by itself.</summary>
        void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere);

        /// <summary>The host: a client says these entries differ there; tell it what they are here.</summary>
        void FixFor(byte player, IList<ulong> keys);
    }

    /// <summary>
    /// Checksums by subsystem, with a partial resync (Fase 8). Every few seconds the host sends a digest of each section of
    /// what its game shows; a client compares it with its own game's. A section that differs twice in a row (a change on its
    /// way would differ once) is asked for in detail: the host sends its entries, the client finds which differ, brings what
    /// it can back to the host's by itself, and asks the host to tell it the rest again. Only those entries travel.
    ///
    /// This class only carries the news and compares. What a section's entries are and how one is set right is the game
    /// side's (<see cref="ICheckSection"/>).
    /// </summary>
    public sealed class ChecksumReplication
    {
        /// <summary>Seconds between two rounds of digests.</summary>
        public const double IntervalSeconds = 10.0;

        /// <summary>Rounds in a row a section must differ before it is asked for.</summary>
        public const int Strikes = 2;

        private const double MalformedLogInterval = 5.0;
        private const int KeysLogged = 4;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(1000);
        private readonly PacketReader reader = new PacketReader();
        private readonly List<ICheckSection> sections = new List<ICheckSection>();
        private readonly Dictionary<byte, int> strikes = new Dictionary<byte, int>();
        private readonly Dictionary<byte, List<CheckEntry>> details = new Dictionary<byte, List<CheckEntry>>();
        private readonly HashSet<byte> asking = new HashSet<byte>();
        private readonly List<CheckEntry> scratch = new List<CheckEntry>();
        private readonly Dictionary<ulong, ulong> mine = new Dictionary<ulong, ulong>();
        private readonly List<ulong> different = new List<ulong>();
        private readonly List<ulong> onlyHost = new List<ulong>();
        private readonly List<ulong> onlyHere = new List<ulong>();
        private readonly List<ulong> fixKeys = new List<ulong>();

        // A client: how many rounds in a row each entry of a section that sends its entries has differed.
        private readonly Dictionary<byte, Dictionary<ulong, int>> entryStrikes = new Dictionary<byte, Dictionary<ulong, int>>();
        private readonly List<ulong> settled = new List<ulong>();
        private double clock;
        private double nextRound = double.NaN;
        private uint round;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;

        public ChecksumReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
        }

        /// <summary>Rounds of digests sent (host) or compared (client).</summary>
        public long Rounds { get; private set; }

        /// <summary>Sections asked for in detail because their digests differed.</summary>
        public long Asked { get; private set; }

        /// <summary>Entries found different on a closer look, and set right.</summary>
        public long Fixed { get; private set; }

        public long Malformed { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>Adds a section. Every machine adds the same sections with the same numbers.</summary>
        public void Add(ICheckSection section)
        {
            if (section == null)
            {
                throw new ArgumentNullException("section");
            }

            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].Id == section.Id)
                {
                    throw new InvalidOperationException("Section " + section.Id + " is there already.");
                }
            }

            if (sections.Count >= ChecksumWire.MaxSections)
            {
                throw new InvalidOperationException("At most " + ChecksumWire.MaxSections + " sections.");
            }

            sections.Add(section);
        }

        /// <summary>Removes every section: the game side's world is gone.</summary>
        public void Clear()
        {
            sections.Clear();
            strikes.Clear();
            details.Clear();
            asking.Clear();
            entryStrikes.Clear();
        }

        /// <summary>Call once per frame with the session's clock. The host sends a round when it is due.</summary>
        public void Update(double now)
        {
            clock = now;
            if (!IsHost || session.State != SessionState.Connected)
            {
                return;
            }

            if (double.IsNaN(nextRound))
            {
                nextRound = now + IntervalSeconds;
                return;
            }

            if (now < nextRound)
            {
                return;
            }

            nextRound = now + IntervalSeconds;
            SendRound();
        }

        /// <summary>The digest of a section's entries as this machine reads them now.</summary>
        public static ulong Digest(ICheckSection section, List<CheckEntry> entries)
        {
            entries.Sort(CompareKeys);
            ulong hash = Fnv1a.OffsetBasis;
            var bytes = new byte[16];
            for (int i = 0; i < entries.Count; i++)
            {
                WriteUInt64(bytes, 0, entries[i].Key);
                WriteUInt64(bytes, 8, section.Coarse(entries[i].Value));
                hash = Fnv1a.Hash64(bytes, 0, 16, hash);
            }

            return hash;
        }

        /// <summary>A key for a thing known by name on every machine.</summary>
        public static ulong KeyOf(string name)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
            return Fnv1a.Hash64(bytes, 0, bytes.Length, Fnv1a.OffsetBasis);
        }

        /// <summary>A value for a few numbers that must be equal on every machine.</summary>
        public static ulong ValueOf(params int[] numbers)
        {
            var bytes = new byte[4];
            ulong hash = Fnv1a.OffsetBasis;
            for (int i = 0; i < numbers.Length; i++)
            {
                bytes[0] = (byte)numbers[i];
                bytes[1] = (byte)(numbers[i] >> 8);
                bytes[2] = (byte)(numbers[i] >> 16);
                bytes[3] = (byte)(numbers[i] >> 24);
                hash = Fnv1a.Hash64(bytes, 0, 4, hash);
            }

            return hash;
        }

        /// <summary>One line for the overlay, or null before the first round.</summary>
        public string Describe()
        {
            if (Rounds == 0)
            {
                return null;
            }

            return "Checks: " + sections.Count + " section(s), " + Rounds + " round(s), " + Asked + " asked for, " + Fixed + " entr(ies) set right"
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
            Clear();
        }

        private void SendRound()
        {
            writer.Reset();
            writer.WriteByte(ChecksumWire.MessageDigests);
            round++;
            writer.WriteUInt32(round);
            int countAt = writer.Length;
            writer.WriteByte(0);
            int count = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                ICheckSection section = sections[i];
                if (!section.Ready || section.SendsEntries)
                {
                    continue;
                }

                scratch.Clear();
                section.Read(scratch);
                writer.WriteByte(section.Id);
                writer.WriteUInt64(Digest(section, scratch));
                writer.WriteUInt16((ushort)Math.Min(scratch.Count, ushort.MaxValue));
                count++;
            }

            bool any = count > 0;
            if (any)
            {
                writer.Buffer[countAt] = (byte)count;
                session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
            }

            for (int i = 0; i < sections.Count; i++)
            {
                ICheckSection section = sections[i];
                if (section.Ready && section.SendsEntries)
                {
                    scratch.Clear();
                    section.Read(scratch);
                    SendEntries(Protocol.Broadcast, section.Id, scratch, true);
                    any = true;
                }
            }

            if (any)
            {
                Rounds++;
            }
        }

        // A section's entries, in parts: as the answer to a client's question, or as a round's entries.
        private void SendEntries(byte target, byte id, List<CheckEntry> entries, bool ofRound)
        {
            int part = 0;
            for (int start = 0; start < entries.Count || start == 0; start += ChecksumWire.EntriesPerMessage)
            {
                int count = Math.Min(ChecksumWire.EntriesPerMessage, entries.Count - start);
                bool last = start + count >= entries.Count;
                writer.Reset();
                writer.WriteByte(ChecksumWire.MessageDetail);
                writer.WriteByte(id);
                writer.WriteByte((byte)Math.Min(part, byte.MaxValue));
                writer.WriteByte((byte)((last ? 1 : 0) | (ofRound ? 2 : 0)));
                writer.WriteByte((byte)count);
                for (int i = 0; i < count; i++)
                {
                    writer.WriteUInt64(entries[start + i].Key);
                    writer.WriteUInt64(entries[start + i].Value);
                }

                session.Send(target, writer.Buffer, 0, writer.Length, true);
                part++;
                if (last)
                {
                    break;
                }
            }
        }

        private ICheckSection Find(byte id)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].Id == id)
                {
                    return sections[i];
                }
            }

            return null;
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type < ChecksumWire.MessageDigests || type > ChecksumWire.MessageFix)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                bool toHost = type == ChecksumWire.MessageAsk || type == ChecksumWire.MessageFix;
                if (toHost != IsHost || (!IsHost && sender != Protocol.HostPlayerId))
                {
                    throw new PacketFormatException("Checksum message " + type + " sent the wrong way.");
                }

                switch (type)
                {
                    case ChecksumWire.MessageDigests:
                        HandleDigests();
                        break;
                    case ChecksumWire.MessageAsk:
                        HandleAsk(sender);
                        break;
                    case ChecksumWire.MessageDetail:
                        HandleDetail();
                        break;
                    default:
                        HandleFix(sender);
                        break;
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Net, "Dropped a checksum message from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        // A client: the host's digests; a section that differs twice in a row is asked for.
        private void HandleDigests()
        {
            uint heardRound = reader.ReadUInt32();
            int count = reader.ReadByte();
            bool compared = false;
            for (int i = 0; i < count; i++)
            {
                byte id = reader.ReadByte();
                ulong digest = reader.ReadUInt64();
                reader.ReadUInt16();
                ICheckSection section = Find(id);
                if (section == null || !section.Ready || asking.Contains(id))
                {
                    continue;
                }

                compared = true;
                scratch.Clear();
                section.Read(scratch);
                int strike;
                strikes.TryGetValue(id, out strike);
                if (Digest(section, scratch) == digest)
                {
                    strikes[id] = 0;
                    continue;
                }

                strike++;
                strikes[id] = strike;
                if (strike < Strikes)
                {
                    continue;
                }

                asking.Add(id);
                details[id] = new List<CheckEntry>();
                Asked++;
                writer.Reset();
                writer.WriteByte(ChecksumWire.MessageAsk);
                writer.WriteByte(id);
                writer.WriteUInt32(heardRound);
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
                log.Info(LogCategory.Net, "Checksums: '" + section.Name + "' differs from the host's " + strike + " round(s) in a row; its entries are asked for.");
            }

            if (compared)
            {
                Rounds++;
            }
        }

        // The host: a client asks for a section's entries.
        private void HandleAsk(byte sender)
        {
            byte id = reader.ReadByte();
            reader.ReadUInt32();
            ICheckSection section = Find(id);
            if (section == null)
            {
                return;
            }

            scratch.Clear();
            if (section.Ready)
            {
                section.Read(scratch);
            }

            SendEntries(sender, id, scratch, false);

            log.Info(LogCategory.Net, "Checksums: player " + sender + " asked for '" + section.Name + "': " + scratch.Count + " entr(ies) sent.");
        }

        // A client: a part of the host's entries; with the last, the comparison.
        private void HandleDetail()
        {
            byte id = reader.ReadByte();
            reader.ReadByte();
            byte flags = reader.ReadByte();
            bool last = (flags & 1) != 0;
            bool ofRound = (flags & 2) != 0;
            int count = reader.ReadByte();
            List<CheckEntry> host;
            if (!details.TryGetValue(id, out host))
            {
                host = new List<CheckEntry>();
                details[id] = host;
            }

            for (int i = 0; i < count; i++)
            {
                host.Add(new CheckEntry(reader.ReadUInt64(), reader.ReadUInt64()));
            }

            if (!last)
            {
                return;
            }

            details.Remove(id);
            ICheckSection section = Find(id);
            if (ofRound)
            {
                if (section != null && section.Ready && section.SendsEntries)
                {
                    Rounds++;
                    Compare(section, host, true);
                }

                return;
            }

            asking.Remove(id);
            strikes[id] = 0;
            if (section == null || !section.Ready)
            {
                return;
            }

            Compare(section, host, false);
        }

        private void Compare(ICheckSection section, List<CheckEntry> host, bool ofRound)
        {
            scratch.Clear();
            section.Read(scratch);
            mine.Clear();
            for (int i = 0; i < scratch.Count; i++)
            {
                mine[scratch[i].Key] = scratch[i].Value;
            }

            different.Clear();
            onlyHost.Clear();
            onlyHere.Clear();
            for (int i = 0; i < host.Count; i++)
            {
                ulong here;
                if (mine.TryGetValue(host[i].Key, out here))
                {
                    if (!section.Same(host[i].Value, here))
                    {
                        different.Add(host[i].Key);
                    }

                    mine.Remove(host[i].Key);
                }
                else if (section.CountsMissing)
                {
                    onlyHost.Add(host[i].Key);
                }
            }

            if (section.CountsMissing)
            {
                foreach (ulong key in mine.Keys)
                {
                    onlyHere.Add(key);
                }
            }

            if (ofRound)
            {
                // A round's entries: what differs is set right only the second time in a row (it may be on its way).
                Dictionary<ulong, int> counts;
                if (!entryStrikes.TryGetValue(section.Id, out counts))
                {
                    counts = new Dictionary<ulong, int>();
                    entryStrikes[section.Id] = counts;
                }

                // What no longer differs starts again from nothing.
                settled.Clear();
                foreach (ulong key in counts.Keys)
                {
                    if (!different.Contains(key) && !onlyHost.Contains(key) && !onlyHere.Contains(key))
                    {
                        settled.Add(key);
                    }
                }

                for (int i = 0; i < settled.Count; i++)
                {
                    counts.Remove(settled[i]);
                }

                KeepStruck(counts, different);
                KeepStruck(counts, onlyHost);
                KeepStruck(counts, onlyHere);
                Struck(counts, different);
                Struck(counts, onlyHost);
                Struck(counts, onlyHere);
            }

            int total = different.Count + onlyHost.Count + onlyHere.Count;
            if (total == 0)
            {
                if (!ofRound)
                {
                    log.Info(LogCategory.Net, "Checksums: '" + section.Name + "' is the host's on a closer look (" + host.Count + " entr(ies)); it only rounded differently.");
                }

                return;
            }

            Fixed += total;
            log.Warning(
                LogCategory.Net,
                "Checksums: '" + section.Name + "' differed from the host's in " + total + " entr(ies) (" + different.Count + " different, " + onlyHost.Count + " only the host's, "
                + onlyHere.Count + " only here; " + Keys(different, onlyHost) + "); set right.");
            section.FixHere(different, onlyHost, onlyHere);
            fixKeys.Clear();
            fixKeys.AddRange(different);
            fixKeys.AddRange(onlyHost);
            for (int start = 0; start < fixKeys.Count; start += ChecksumWire.EntriesPerMessage)
            {
                int entries = Math.Min(ChecksumWire.EntriesPerMessage, fixKeys.Count - start);
                writer.Reset();
                writer.WriteByte(ChecksumWire.MessageFix);
                writer.WriteByte(section.Id);
                writer.WriteByte((byte)entries);
                for (int i = 0; i < entries; i++)
                {
                    writer.WriteUInt64(fixKeys[start + i]);
                }

                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            }
        }

        // The host: a client found these entries different; it is told them again.
        private void HandleFix(byte sender)
        {
            byte id = reader.ReadByte();
            int count = reader.ReadByte();
            fixKeys.Clear();
            for (int i = 0; i < count; i++)
            {
                fixKeys.Add(reader.ReadUInt64());
            }

            ICheckSection section = Find(id);
            if (section == null || !section.Ready)
            {
                return;
            }

            section.FixFor(sender, fixKeys);
            log.Info(LogCategory.Net, "Checksums: player " + sender + " had " + count + " entr(ies) of '" + section.Name + "' different; told them again.");
        }

        // Counts a round against each key; keeps in the list only those that differed in the round before too.
        private static void KeepStruck(Dictionary<ulong, int> counts, List<ulong> keys)
        {
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                int count;
                counts.TryGetValue(keys[i], out count);
                counts[keys[i]] = count + 1;
                if (count + 1 < Strikes)
                {
                    keys.RemoveAt(i);
                }
            }
        }

        // What is set right now starts its count again.
        private static void Struck(Dictionary<ulong, int> counts, List<ulong> keys)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                counts.Remove(keys[i]);
            }
        }

        private static string Keys(List<ulong> first, List<ulong> second)
        {
            var text = new StringBuilder("keys ");
            int listed = 0;
            for (int i = 0; i < first.Count && listed < KeysLogged; i++, listed++)
            {
                text.Append(listed == 0 ? string.Empty : ", ").Append(Fnv1a.ToHex(first[i]));
            }

            for (int i = 0; i < second.Count && listed < KeysLogged; i++, listed++)
            {
                text.Append(listed == 0 ? string.Empty : ", ").Append(Fnv1a.ToHex(second[i]));
            }

            if (first.Count + second.Count > KeysLogged)
            {
                text.Append(", …");
            }

            return listed == 0 ? "only here" : text.ToString();
        }

        private static int CompareKeys(CheckEntry a, CheckEntry b)
        {
            return a.Key.CompareTo(b.Key);
        }

        private static void WriteUInt64(byte[] bytes, int at, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                bytes[at + i] = (byte)(value >> (8 * i));
            }
        }
    }
}
