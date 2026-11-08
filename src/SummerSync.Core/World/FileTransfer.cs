using System;
using System.Collections;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>What every channel of <see cref="FileTransfer"/> has in common.</summary>
    public static class FileWire
    {
        /// <summary>Bytes of a file in one part. With the message's own bytes it fits one packet of any transport.</summary>
        public const int PartSize = 960;

        /// <summary>Files per message of the list.</summary>
        public const int FilesPerMessage = 60;
    }

    /// <summary>Messages of the songs' <see cref="FileTransfer"/>.</summary>
    public static class SongWire
    {
        /// <summary>Host to client, reliable: how many songs the host has, and the number and size of some of them.</summary>
        public const byte MessageSongs = WorldWire.MessageRadio + 1;

        /// <summary>
        /// Client to host, reliable: "send me these parts of that song": the first of a stretch of parts, how
        /// long the stretch is, and a bit for each part of it that is wanted. Replaces what the client asked for
        /// before.
        /// </summary>
        public const byte MessageWant = WorldWire.MessageRadio + 2;

        /// <summary>Host to client, unreliable: one part of a song.</summary>
        public const byte MessagePart = WorldWire.MessageRadio + 3;

        public const int PartSize = FileWire.PartSize;
    }

    /// <summary>
    /// Messages of the CDs' <see cref="FileTransfer"/>: the tracks a host put into the game's "CD1" to "CD3" folders, for the
    /// Satsuma's CD player. After the dents' two, which end at 108.
    /// </summary>
    public static class CdWire
    {
        /// <summary>Host to client, reliable: the host's CD tracks, as the songs' list.</summary>
        public const byte MessageTracks = 109;

        /// <summary>Client to host, reliable: which parts of a track to send.</summary>
        public const byte MessageWant = 110;

        /// <summary>Host to client, unreliable: one part of a track.</summary>
        public const byte MessagePart = 111;
    }

    /// <summary>Messages of the host's car picture (<see cref="FileChannel.Art"/>), after the CDs'.</summary>
    public static class ArtWire
    {
        /// <summary>Host to client, reliable: the host's picture, as the songs' list (none, or one).</summary>
        public const byte MessageList = 112;

        /// <summary>Client to host, reliable: which parts of it to send.</summary>
        public const byte MessageWant = 113;

        /// <summary>Host to client, unreliable: one part of it.</summary>
        public const byte MessagePart = 114;
    }

    /// <summary>
    /// One kind of files a <see cref="FileTransfer"/> carries: its three messages (the list, then "send me", then a
    /// part), how big a file and how many files it takes, and what to call them in the log.
    /// </summary>
    public sealed class FileChannel
    {
        /// <summary>The host's songs, for the second radio channel.</summary>
        public static readonly FileChannel Songs = new FileChannel(SongWire.MessageSongs, 48 * 1024 * 1024, 200, 6, "song", "Songs", "songs");

        /// <summary>The host's CD tracks, for the Satsuma's CD player: three CDs of up to 16 each, as the game reads them.</summary>
        public static readonly FileChannel Cds = new FileChannel(CdWire.MessageTracks, 48 * 1024 * 1024, 48, 6, "CD track", "CDs", "CD tracks");

        /// <summary>The host's "Images/car.png", the Satsuma's "Art" paint (the host's for everyone).</summary>
        public static readonly FileChannel Art = new FileChannel(ArtWire.MessageList, 16 * 1024 * 1024, 1, 6, "car picture", "Car picture", "car pictures");

        public FileChannel(byte firstMessage, int maxFileBytes, int maxFiles, int partsPerFlush, string noun, string label, string plural)
        {
            MessageList = firstMessage;
            MessageWant = (byte)(firstMessage + 1);
            MessagePart = (byte)(firstMessage + 2);
            MaxFileBytes = maxFileBytes;
            MaxFiles = maxFiles;
            PartsPerFlush = partsPerFlush;
            Noun = noun;
            Label = label;
            Plural = plural;
        }

        /// <summary>Host to client, reliable: the files the host has.</summary>
        public byte MessageList { get; private set; }

        /// <summary>Client to host, reliable: which parts of a file to send.</summary>
        public byte MessageWant { get; private set; }

        /// <summary>Host to client, unreliable: one part of a file.</summary>
        public byte MessagePart { get; private set; }

        public int MaxFileBytes { get; private set; }

        public int MaxFiles { get; private set; }

        /// <summary>Parts the host sends per flush, to all clients together: six are about 170 kB a second.</summary>
        public int PartsPerFlush { get; private set; }

        /// <summary>One of them, for the log: "song".</summary>
        public string Noun { get; private set; }

        /// <summary>What the overlay line begins with: "Songs".</summary>
        public string Label { get; private set; }

        /// <summary>More than one, for the log: "songs".</summary>
        public string Plural { get; private set; }
    }

    /// <summary>One of the host's files: the number that names it, which is a hash of its bytes, and how long it is.</summary>
    public struct SharedFile
    {
        public ulong Id;

        public int Size;

        public SharedFile(ulong id, int size)
        {
            Id = id;
            Size = size;
        }

        /// <summary>How the id is written wherever a person or a file name needs it: sixteen hex digits.</summary>
        public static string Name(ulong id)
        {
            return id.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Where the bytes of the files are kept: files on the host, a cache on a client. Nothing here does I/O itself.</summary>
    public interface IFileStore
    {
        /// <summary>Host: copies bytes of that file into the buffer. Returns how many it copied; fewer than asked for is a failure.</summary>
        int Read(ulong id, int offset, byte[] buffer, int bufferOffset, int count);

        /// <summary>Client: whether that file is here already, whole and of that size.</summary>
        bool Has(ulong id, int size);

        /// <summary>Client: a part of a file arrived.</summary>
        void Write(ulong id, int size, int offset, byte[] data, int dataOffset, int count);

        /// <summary>Client: every part arrived. True if what was written is that file: its bytes hash to its id.</summary>
        bool Complete(ulong id, int size);

        /// <summary>Client: what was written of that file is not it; forget it.</summary>
        void Discard(ulong id);
    }

    /// <summary>
    /// Gets files that are on the host's disk and nowhere else to the other players: the music a player has put
    /// into the game for its second radio channel, and the host's saved world.
    ///
    /// The host says which files it has. A client asks for the parts of those it lacks, one file at a time: first
    /// the one it is told it wants most, then the rest in the host's order. Parts travel unreliably, a few per
    /// flush, so that a lost one never holds up what the game itself has to say; the client asks again for what did
    /// not arrive. A file is named by the hash of its bytes: the client checks that what it put together is the file
    /// it asked for, and one it already has from an earlier session is never sent again.
    /// </summary>
    public sealed class FileTransfer
    {
        private const int WindowParts = 256;
        private const double RetrySeconds = 1.5;
        private const int MaxFailures = 2;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly FileChannel channel;
        private readonly PacketWriter writer = new PacketWriter(1100);
        private readonly PacketReader reader = new PacketReader();
        private readonly byte[] part = new byte[FileWire.PartSize];
        private readonly List<SharedFile> files = new List<SharedFile>();
        private readonly Dictionary<ulong, int> sizes = new Dictionary<ulong, int>();
        private readonly List<byte> subscribers = new List<byte>();
        private readonly List<Request> requests = new List<Request>();
        private readonly Dictionary<ulong, int> failures = new Dictionary<ulong, int>();
        // Client: whether each of the host's files is here, asked of the store once and noted.
        private readonly Dictionary<ulong, bool> here = new Dictionary<ulong, bool>();
        private IFileStore store;
        private double clock;
        private bool detached;
        private bool offered;
        private int expected;
        private ulong wanted;
        private int turn;

        // Client: the file that is being fetched.
        private bool fetching;
        private SharedFile fetched;
        private BitArray got;
        private int gotCount;
        private int askedFrom;
        private int askedCount;
        private int askedLast;
        private double askAgainAt;
        private readonly byte[] wantBits = new byte[(WindowParts + 7) / 8];

        public FileTransfer(NetSession session, Logger log)
            : this(session, log, FileChannel.Songs)
        {
        }

        public FileTransfer(NetSession session, Logger log, FileChannel channel)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            if (channel == null)
            {
                throw new ArgumentNullException("channel");
            }

            this.session = session;
            this.log = log ?? new Logger();
            this.channel = channel;
            PartsPerFlush = channel.PartsPerFlush;
            session.MessageReceived += OnMessage;
            session.BeforeFlush += OnBeforeFlush;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
        }

        /// <summary>Parts the host sends per flush, to all clients together: six are about 170 kB a second.</summary>
        public int PartsPerFlush { get; set; }

        public FileChannel Channel
        {
            get { return channel; }
        }

        /// <summary>The host's files, as offered on the host and as heard on a client.</summary>
        public IList<SharedFile> Files
        {
            get { return files; }
        }

        /// <summary>The host's files, under the name the radio knows them by.</summary>
        public IList<SharedFile> Songs
        {
            get { return files; }
        }

        /// <summary>Counts up whenever <see cref="Files"/> changes.</summary>
        public int FilesSerial { get; private set; }

        /// <summary>Counts up whenever <see cref="Songs"/> changes.</summary>
        public int SongsSerial
        {
            get { return FilesSerial; }
        }

        /// <summary>Parts sent, on the host, or received and kept, on a client.</summary>
        public long Parts { get; private set; }

        /// <summary>On a client: files that arrived whole in this session.</summary>
        public int Fetched { get; private set; }

        public long Malformed { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>Where the bytes are. Without one, nothing is offered and nothing is fetched.</summary>
        public void SetStore(IFileStore fileStore)
        {
            store = fileStore;
            here.Clear();
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
        }

        /// <summary>
        /// On the host: the files it has, in its own order. The others are told. Returns how many are offered: a file
        /// that is empty, too big or listed twice is left out.
        /// </summary>
        public int Offer(IList<SharedFile> offer)
        {
            files.Clear();
            sizes.Clear();
            for (int i = 0; offer != null && i < offer.Count && files.Count < channel.MaxFiles; i++)
            {
                SharedFile file = offer[i];
                if (file.Size > 0 && file.Size <= channel.MaxFileBytes && !sizes.ContainsKey(file.Id))
                {
                    files.Add(file);
                    sizes.Add(file.Id, file.Size);
                }
            }

            FilesSerial++;
            offered = true;
            requests.Clear();
            for (int i = 0; i < subscribers.Count; i++)
            {
                SendList(subscribers[i]);
            }

            return files.Count;
        }

        /// <summary>On a client: the file to fetch before any other (the song on the air). 0 for none.</summary>
        public void Want(ulong id)
        {
            wanted = id;
        }

        /// <summary>On a client: whether that file of the host's is here, whole.</summary>
        public bool IsHere(ulong id)
        {
            bool known;
            if (here.TryGetValue(id, out known))
            {
                return known;
            }

            int size;
            if (store == null || !sizes.TryGetValue(id, out size))
            {
                return false;
            }

            known = store.Has(id, size);
            here[id] = known;
            return known;
        }

        /// <summary>On a client: how much of that file has arrived, from 0 to 1.</summary>
        public float Progress(ulong id)
        {
            if (IsHere(id))
            {
                return 1f;
            }

            return fetching && fetched.Id == id && got != null && got.Length > 0 ? (float)gotCount / got.Length : 0f;
        }

        /// <summary>On a client: whether that file was given up on, because what arrived kept not being it.</summary>
        public bool HasFailed(ulong id)
        {
            return GaveUp(id);
        }

        /// <summary>One line for the overlay, or null if there is nothing to say.</summary>
        public string Describe()
        {
            if (files.Count == 0)
            {
                return null;
            }

            if (IsHost)
            {
                return channel.Label + ": " + files.Count + " of this game's offered to the others, " + Parts + " part(s) sent";
            }

            int have = 0;
            for (int i = 0; i < files.Count; i++)
            {
                have += IsHere(files[i].Id) ? 1 : 0;
            }

            return channel.Label + ": " + have + " of the host's " + files.Count + " here"
                + (fetching ? ", receiving " + SharedFile.Name(fetched.Id).Substring(0, 6) + " " + (int)(Progress(fetched.Id) * 100f) + " %" : string.Empty);
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.BeforeFlush -= OnBeforeFlush;
            session.PlayerJoined -= OnPlayerJoined;
            session.PlayerLeft -= OnPlayerLeft;
            requests.Clear();
            subscribers.Clear();
            fetching = false;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (IsHost && player.Id != session.LocalPlayerId && !subscribers.Contains(player.Id))
            {
                subscribers.Add(player.Id);
                if (offered)
                {
                    SendList(player.Id);
                }
            }
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            subscribers.Remove(player.Id);
            for (int i = requests.Count - 1; i >= 0; i--)
            {
                if (requests[i].Client == player.Id)
                {
                    requests.RemoveAt(i);
                }
            }
        }

        private void SendList(byte client)
        {
            int first = 0;
            do
            {
                int count = Math.Min(FileWire.FilesPerMessage, files.Count - first);
                writer.Reset();
                writer.WriteByte(channel.MessageList);
                writer.WriteUInt16((ushort)files.Count);
                writer.WriteUInt16((ushort)first);
                writer.WriteByte((byte)count);
                for (int i = 0; i < count; i++)
                {
                    writer.WriteUInt64(files[first + i].Id);
                    writer.WriteUInt32((uint)files[first + i].Size);
                }

                session.Send(client, writer.Buffer, 0, writer.Length, true);
                first += count;
            }
            while (first < files.Count);
        }

        private void OnBeforeFlush()
        {
            if (store == null || session.State != SessionState.Connected)
            {
                return;
            }

            if (IsHost)
            {
                Serve();
            }
            else
            {
                Fetch();
            }
        }

        // Host: a few parts per flush, to one client after another, and none to a client whose last flush did not
        // all fit: the parts would sit in front of what the players say.
        private void Serve()
        {
            int skipped = 0;
            for (int sent = 0; sent < PartsPerFlush && requests.Count > 0 && skipped < requests.Count; sent++)
            {
                turn = turn >= requests.Count ? 0 : turn;
                Request request = requests[turn];
                if (session.UnreliableWaiting(request.Client) > 0)
                {
                    skipped++;
                    turn++;
                    sent--;
                    continue;
                }

                while (request.Next < request.Count && (request.Wanted[request.Next >> 3] & (1 << (request.Next & 7))) == 0)
                {
                    request.Next++;
                }

                if (request.Next >= request.Count)
                {
                    requests.RemoveAt(turn);
                    sent--;
                    continue;
                }

                int index = request.First + request.Next;
                int size = sizes[request.Id];
                int offset = index * FileWire.PartSize;
                int count = Math.Min(FileWire.PartSize, size - offset);
                int read = count <= 0 ? 0 : store.Read(request.Id, offset, part, 0, count);
                if (read != count)
                {
                    log.Warning(
                        LogCategory.Net,
                        "Part " + index + " of the " + channel.Noun + " " + SharedFile.Name(request.Id) + " could not be read here (" + read + " of " + count
                        + " bytes): player " + request.Client + " is not sent the rest of what they asked for.");
                    requests.RemoveAt(turn);
                    continue;
                }

                writer.Reset();
                writer.WriteByte(channel.MessagePart);
                writer.WriteUInt64(request.Id);
                writer.WriteUInt32((uint)index);
                writer.WriteBytes(part, 0, count);
                session.Send(request.Client, writer.Buffer, 0, writer.Length, false);
                Parts++;
                request.Next++;
                requests[turn] = request;
                turn++;
            }
        }

        // Client: which file, and which of its parts to ask for.
        private void Fetch()
        {
            if (fetching && wanted != 0 && wanted != fetched.Id && sizes.ContainsKey(wanted) && !IsHere(wanted) && !GaveUp(wanted))
            {
                // The one wanted most comes first. What arrived of the other stays in the store's care.
                fetching = false;
            }

            if (!fetching && !Choose())
            {
                return;
            }

            // Every part asked for arrived, or it is time to say again what is missing.
            bool windowDone = askedCount > 0 && MissingIn(askedFrom, askedCount) == 0;
            if (askedCount > 0 && !windowDone && clock < askAgainAt)
            {
                return;
            }

            int from = FirstMissing();
            if (from < 0)
            {
                return;
            }

            // A stretch from the first part that is missing, and in it every part that is: what was lost on the
            // way is asked for together with what was never sent.
            askedFrom = from;
            askedCount = Math.Min(WindowParts, got.Length - from);
            askedLast = from;
            Array.Clear(wantBits, 0, wantBits.Length);
            for (int i = 0; i < askedCount; i++)
            {
                if (!got[from + i])
                {
                    wantBits[i >> 3] |= (byte)(1 << (i & 7));
                    askedLast = from + i;
                }
            }

            askAgainAt = clock + RetrySeconds + (askedCount / (30.0 * Math.Max(1, PartsPerFlush)));
            writer.Reset();
            writer.WriteByte(channel.MessageWant);
            writer.WriteUInt64(fetched.Id);
            writer.WriteUInt32((uint)askedFrom);
            writer.WriteUInt16((ushort)askedCount);
            writer.WriteBytes(wantBits, 0, (askedCount + 7) / 8);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        private bool Choose()
        {
            int pick = -1;
            for (int i = 0; i < files.Count; i++)
            {
                SharedFile file = files[i];
                if (IsHere(file.Id) || GaveUp(file.Id))
                {
                    continue;
                }

                if (file.Id == wanted)
                {
                    pick = i;
                    break;
                }

                pick = pick < 0 ? i : pick;
            }

            if (pick < 0)
            {
                return false;
            }

            fetched = files[pick];
            int parts = (fetched.Size + FileWire.PartSize - 1) / FileWire.PartSize;
            got = new BitArray(parts);
            gotCount = 0;
            askedFrom = 0;
            askedCount = 0;
            fetching = true;
            log.Info(
                LogCategory.Net,
                "Fetching the host's " + channel.Noun + " " + SharedFile.Name(fetched.Id) + " (" + (fetched.Size / 1024) + " kB, " + parts + " part(s))"
                + (fetched.Id == wanted ? ", which is wanted first." : "."));
            return true;
        }

        private bool GaveUp(ulong id)
        {
            int failed;
            return failures.TryGetValue(id, out failed) && failed >= MaxFailures;
        }

        private int FirstMissing()
        {
            for (int i = 0; i < got.Length; i++)
            {
                if (!got[i])
                {
                    return i;
                }
            }

            return -1;
        }

        private int MissingIn(int from, int count)
        {
            int missing = 0;
            for (int i = from; i < from + count && i < got.Length; i++)
            {
                missing += got[i] ? 0 : 1;
            }

            return missing;
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != channel.MessageList && type != channel.MessageWant && type != channel.MessagePart)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == channel.MessageWant)
                {
                    HandleWant(sender);
                }
                else if (IsHost || sender != Protocol.HostPlayerId)
                {
                    throw new PacketFormatException("A " + channel.Noun + ", or a list of " + channel.Plural + ", sent by someone who is not the host.");
                }
                else if (type == channel.MessageList)
                {
                    HandleList();
                }
                else
                {
                    HandlePart(data.Length - 1);
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (Malformed == 1)
                {
                    log.Warning(LogCategory.Net, "Dropped a message about " + channel.Plural + " from player " + sender + ": " + exception.Message);
                }
            }
        }

        private void HandleWant(byte sender)
        {
            if (!IsHost)
            {
                throw new PacketFormatException("A request for a " + channel.Noun + " sent to a client.");
            }

            ulong id = reader.ReadUInt64();
            long from = reader.ReadUInt32();
            int count = reader.ReadUInt16();
            int size;
            if (!sizes.TryGetValue(id, out size))
            {
                // Asked for before the list changed: nothing to send.
                return;
            }

            int parts = (size + FileWire.PartSize - 1) / FileWire.PartSize;
            if (from >= parts || count == 0 || count > WindowParts || from + count > parts)
            {
                throw new PacketFormatException("A request for " + count + " parts from " + from + " of a " + channel.Noun + " of " + parts + ".");
            }

            byte[] wantedParts = reader.ReadBytes((count + 7) / 8);

            for (int i = requests.Count - 1; i >= 0; i--)
            {
                if (requests[i].Client == sender)
                {
                    requests.RemoveAt(i);
                }
            }

            requests.Add(new Request { Client = sender, Id = id, First = (int)from, Count = count, Wanted = wantedParts });
        }

        private void HandleList()
        {
            int total = reader.ReadUInt16();
            int first = reader.ReadUInt16();
            int count = reader.ReadByte();
            if (total > channel.MaxFiles || first + count > total)
            {
                throw new PacketFormatException("A list of " + total + " " + channel.Plural + ", " + count + " of them from " + first + ".");
            }

            if (first == 0)
            {
                files.Clear();
                sizes.Clear();
                expected = total;
            }
            else if (first != files.Count || total != expected)
            {
                throw new PacketFormatException("A piece of a list of " + channel.Plural + " that does not follow the one before.");
            }

            for (int i = 0; i < count; i++)
            {
                ulong id = reader.ReadUInt64();
                long size = reader.ReadUInt32();
                if (size <= 0 || size > channel.MaxFileBytes || sizes.ContainsKey(id))
                {
                    throw new PacketFormatException("A " + channel.Noun + " of " + size + " bytes, or one that is listed twice.");
                }

                files.Add(new SharedFile(id, (int)size));
                sizes.Add(id, (int)size);
            }

            if (files.Count == expected)
            {
                FilesSerial++;
                here.Clear();
                if (fetching && !sizes.ContainsKey(fetched.Id))
                {
                    fetching = false;
                }

                log.Info(LogCategory.Net, "The host offers " + files.Count + " " + channel.Plural + ".");
            }
        }

        private void HandlePart(int length)
        {
            ulong id = reader.ReadUInt64();
            long index = reader.ReadUInt32();
            int count = length - 12;
            if (!fetching || id != fetched.Id || store == null)
            {
                // A part of a file that is not being fetched any more: late, not wrong.
                return;
            }

            int offset = (int)index * FileWire.PartSize;
            if (index >= got.Length || count != Math.Min(FileWire.PartSize, fetched.Size - offset))
            {
                throw new PacketFormatException("Part " + index + " of a " + channel.Noun + " of " + got.Length + ", with " + count + " bytes.");
            }

            if (got[(int)index])
            {
                return;
            }

            byte[] bytes = reader.ReadBytes(count);
            store.Write(id, fetched.Size, offset, bytes, 0, count);
            got[(int)index] = true;
            gotCount++;
            Parts++;
            if (index >= askedLast)
            {
                // The last of what was asked for: whatever is still missing from before it was lost.
                askAgainAt = clock;
            }

            if (gotCount < got.Length)
            {
                return;
            }

            fetching = false;
            if (store.Complete(id, fetched.Size))
            {
                here[id] = true;
                Fetched++;
                log.Info(LogCategory.Net, "The host's " + channel.Noun + " " + SharedFile.Name(id) + " is here, whole (" + (fetched.Size / 1024) + " kB).");
                return;
            }

            int failed;
            failures.TryGetValue(id, out failed);
            failures[id] = failed + 1;
            store.Discard(id);
            log.Warning(
                LogCategory.Net,
                "What arrived as the host's " + channel.Noun + " " + SharedFile.Name(id) + " is not that " + channel.Noun + ": its bytes do not hash to its number. "
                + (failed + 1 >= MaxFailures ? "Given up on it." : "Fetched again."));
        }

        private struct Request
        {
            public byte Client;
            public ulong Id;

            /// <summary>The first part of the stretch asked for, how many parts it is, and a bit for each that is wanted.</summary>
            public int First;
            public int Count;
            public byte[] Wanted;

            /// <summary>The next part of the stretch to look at.</summary>
            public int Next;
        }
    }
}
