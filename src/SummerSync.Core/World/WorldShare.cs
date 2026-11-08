using System;
using System.Collections.Generic;
using SummerSync.Core.Hashing;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="WorldShare"/>.</summary>
    public static class WorldShareWire
    {
        /// <summary>The first of the three messages of the world's <see cref="FileTransfer"/> channel: list, "send me", part.</summary>
        public const byte MessageFiles = TallyWire.MessageSet + 1;

        /// <summary>Host to client, reliable: which of the host's files is which, and when the world was saved.</summary>
        public const byte MessageManifest = MessageFiles + 3;
    }

    /// <summary>The files a saved world is made of, as the game names them.</summary>
    public static class WorldFiles
    {
        /// <summary>
        /// Every file of a world. Its place in this list is what goes over the network: a name never comes from
        /// another player. Never reorder it, only add at the end. The player's own settings (options.txt) are not here.
        /// </summary>
        public static readonly string[] Names =
        {
            "defaultES2File.txt",
            "items.txt",
            "meshsave.txt",
            "graveyard.txt",
            "trophies.txt",
            "leaderboard.txt",
        };

        /// <summary>The one without which there is no world: the game's "Continue" looks for it.</summary>
        public const int Main = 0;

        /// <summary>The biggest file of a world that is shared. A world of a few hours is under 2 MB.</summary>
        public const int MaxFileBytes = 96 * 1024 * 1024;

        /// <summary>The channel of the world's files, as fast as the songs': more crowds out the players' movement.</summary>
        public static readonly FileChannel Channel = new FileChannel(WorldShareWire.MessageFiles, MaxFileBytes, 8, 6, "world file", "World files", "world files");

        /// <summary>Where that name is in <see cref="Names"/>, or -1.</summary>
        public static int IndexOf(string name)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>One file of a world: which one (its place in <see cref="WorldFiles.Names"/>), its hash and its size.</summary>
    public struct WorldFile
    {
        public int Name;

        public ulong Id;

        public int Size;

        public WorldFile(int name, ulong id, int size)
        {
            Name = name;
            Id = id;
            Size = size;
        }
    }

    /// <summary>A saved world as the host offers it.</summary>
    public sealed class WorldManifest
    {
        public WorldManifest(uint serial, long savedAt, WorldFile[] files, ulong key = 0UL)
        {
            Serial = serial;
            SavedAt = savedAt;
            Key = key;
            Files = files ?? new WorldFile[0];
            ulong hash = Fnv1a.OffsetBasis;
            var bytes = new byte[16];
            for (int i = 0; i < Files.Length; i++)
            {
                bytes[0] = (byte)Files[i].Name;
                for (int b = 0; b < 8; b++)
                {
                    bytes[1 + b] = (byte)(Files[i].Id >> (8 * b));
                }

                for (int b = 0; b < 4; b++)
                {
                    bytes[9 + b] = (byte)(Files[i].Size >> (8 * b));
                }

                hash = Fnv1a.Hash64(bytes, 0, 13, hash);
            }

            Id = Files.Length == 0 ? 0UL : (hash == 0UL ? 1UL : hash);
        }

        /// <summary>Counts up each time the host offers a world.</summary>
        public uint Serial { get; private set; }

        /// <summary>When the host's game wrote it: seconds since 1970, UTC. 0 if not known.</summary>
        public long SavedAt { get; private set; }

        public WorldFile[] Files { get; private set; }

        /// <summary>A hash of which files it has and of their contents: the same world gives the same number. 0 for none.</summary>
        public ulong Id { get; private set; }

        /// <summary>
        /// Which of the host's saved worlds it is, whatever was saved in it since (the host's game and the mod's worlds: the
        /// main menu's "HOST"). 0 when not said (an older build). Two offers with different keys are different worlds: the
        /// second is not a newer save of the first.
        /// </summary>
        public ulong Key { get; private set; }

        /// <summary>Whether there is a world at all: the host may never have saved, or its player may have died.</summary>
        public bool HasWorld
        {
            get { return Find(WorldFiles.Main) >= 0; }
        }

        public long TotalBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Files.Length; i++)
                {
                    total += Files[i].Size;
                }

                return total;
            }
        }

        /// <summary>Where the file of that name is in <see cref="Files"/>, or -1.</summary>
        public int Find(int name)
        {
            for (int i = 0; i < Files.Length; i++)
            {
                if (Files[i].Name == name)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Gets the host's saved world to the other players, so that everyone plays in it without passing files around.
    ///
    /// The host says which world it has (<see cref="WorldManifest"/>: which file is which, by hash, and when it was
    /// saved) and offers the bytes through a <see cref="FileTransfer"/> channel of its own. A client fetches what it
    /// lacks and checks it: a file it has from an earlier session is not sent again. When the host saves again, it
    /// offers the new world, and a client fetches it in the background; what the client's game loads is decided by
    /// the Unity side, which also keeps that from changing while the client is in the world.
    /// </summary>
    public sealed class WorldShare
    {
        private readonly NetSession session;
        private readonly Logger log;
        private readonly FileTransfer files;
        private readonly PacketWriter writer = new PacketWriter(256);
        private readonly PacketReader reader = new PacketReader();
        private readonly List<byte> subscribers = new List<byte>();
        private WorldManifest manifest;
        private uint serial;
        private bool detached;

        public WorldShare(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            files = new FileTransfer(session, this.log, WorldFiles.Channel);
            session.MessageReceived += OnMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
        }

        /// <summary>The bytes, under the world's own channel.</summary>
        public FileTransfer Files
        {
            get { return files; }
        }

        /// <summary>The world offered, on the host, or the last one heard of, on a client. Null before either.</summary>
        public WorldManifest Manifest
        {
            get { return manifest; }
        }

        /// <summary>Counts up whenever <see cref="Manifest"/> changes.</summary>
        public int ManifestSerial { get; private set; }

        public long Malformed { get; private set; }

        /// <summary>On a client: the host has a world, and every file of it is here, whole.</summary>
        public bool IsReady
        {
            get
            {
                if (manifest == null || !manifest.HasWorld || session.Role == SessionRole.Host)
                {
                    return false;
                }

                for (int i = 0; i < manifest.Files.Length; i++)
                {
                    if (!files.IsHere(manifest.Files[i].Id))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>On a client: some file of the world kept arriving wrong and was given up on.</summary>
        public bool HasFailed
        {
            get
            {
                for (int i = 0; manifest != null && i < manifest.Files.Length; i++)
                {
                    if (files.HasFailed(manifest.Files[i].Id))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>On a client: how much of the world has arrived, from 0 to 1.</summary>
        public float Progress
        {
            get
            {
                if (manifest == null || manifest.TotalBytes <= 0)
                {
                    return 0f;
                }

                double here = 0;
                for (int i = 0; i < manifest.Files.Length; i++)
                {
                    here += manifest.Files[i].Size * (double)files.Progress(manifest.Files[i].Id);
                }

                return (float)(here / manifest.TotalBytes);
            }
        }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        public void SetStore(IFileStore store)
        {
            files.SetStore(store);
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            files.Update(now);
        }

        /// <summary>
        /// On the host: the world it has now, or none. The others are told and fetch what they lack. Returns false,
        /// and offers no world, if a file cannot be shared (listed twice, unknown, empty or too big).
        /// </summary>
        public bool Offer(IList<WorldFile> world, long savedAt)
        {
            return Offer(world, savedAt, 0UL);
        }

        /// <summary>The same, saying which of the host's worlds it is (<see cref="WorldManifest.Key"/>).</summary>
        public bool Offer(IList<WorldFile> world, long savedAt, ulong key)
        {
            var chosen = new List<WorldFile>();
            var shared = new List<SharedFile>();
            string problem = null;
            for (int i = 0; world != null && i < world.Count; i++)
            {
                WorldFile file = world[i];
                if (file.Name < 0 || file.Name >= WorldFiles.Names.Length)
                {
                    problem = "a file the world list does not know (" + file.Name + ")";
                }
                else if (file.Size <= 0 || file.Size > WorldFiles.MaxFileBytes)
                {
                    problem = WorldFiles.Names[file.Name] + " has " + file.Size + " bytes, and the most that is shared is " + WorldFiles.MaxFileBytes;
                }
                else
                {
                    for (int j = 0; j < chosen.Count && problem == null; j++)
                    {
                        if (chosen[j].Name == file.Name)
                        {
                            problem = WorldFiles.Names[file.Name] + " is listed twice";
                        }
                    }
                }

                if (problem != null)
                {
                    break;
                }

                // Two files with the same bytes travel once.
                bool known = false;
                for (int j = 0; j < shared.Count; j++)
                {
                    known |= shared[j].Id == file.Id;
                }

                chosen.Add(file);
                if (!known)
                {
                    shared.Add(new SharedFile(file.Id, file.Size));
                }
            }

            if (problem != null)
            {
                log.Warning(LogCategory.Net, "The world of this game cannot be shared: " + problem + ". The others are told there is none.");
                chosen.Clear();
                shared.Clear();
            }

            serial++;
            manifest = new WorldManifest(serial, savedAt, chosen.ToArray(), key);
            ManifestSerial++;
            files.Offer(shared);
            for (int i = 0; i < subscribers.Count; i++)
            {
                SendManifest(subscribers[i]);
            }

            log.Info(
                LogCategory.Net,
                manifest.HasWorld
                    ? "Offering this game's world " + SharedFile.Name(manifest.Id) + ": " + manifest.Files.Length + " file(s), " + (manifest.TotalBytes / 1024) + " kB" + SavedText(manifest) + "."
                    : "This game has no world to offer" + (problem == null ? " (no " + WorldFiles.Names[WorldFiles.Main] + ")." : "."));
            return problem == null;
        }

        /// <summary>One line for the overlay, or null if there is nothing to say.</summary>
        public string Describe()
        {
            if (manifest == null)
            {
                return IsHost ? null : "World: waiting to hear of the host's";
            }

            if (!manifest.HasWorld)
            {
                return IsHost ? "World: this game has none saved to offer" : "World: the host has none saved";
            }

            string size = (manifest.TotalBytes / 1024) + " kB" + SavedText(manifest);
            if (IsHost)
            {
                return "World: " + SharedFile.Name(manifest.Id).Substring(0, 6) + " offered, " + size + ", " + files.Parts + " part(s) sent";
            }

            if (IsReady)
            {
                return "World: the host's " + SharedFile.Name(manifest.Id).Substring(0, 6) + " is here, " + size;
            }

            return HasFailed
                ? "World: the host's kept arriving wrong; leave and join again"
                : "World: receiving the host's, " + (int)(Progress * 100f) + " % of " + size;
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.PlayerJoined -= OnPlayerJoined;
            session.PlayerLeft -= OnPlayerLeft;
            subscribers.Clear();
            files.Detach();
        }

        private static string SavedText(WorldManifest world)
        {
            if (world.SavedAt <= 0)
            {
                return string.Empty;
            }

            DateTime saved = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(world.SavedAt).ToLocalTime();
            return ", saved " + saved.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (IsHost && player.Id != session.LocalPlayerId && !subscribers.Contains(player.Id))
            {
                subscribers.Add(player.Id);
                if (manifest != null)
                {
                    SendManifest(player.Id);
                }
            }
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            subscribers.Remove(player.Id);
        }

        private void SendManifest(byte client)
        {
            writer.Reset();
            writer.WriteByte(WorldShareWire.MessageManifest);
            writer.WriteUInt32(manifest.Serial);
            writer.WriteUInt64((ulong)manifest.SavedAt);
            writer.WriteByte((byte)manifest.Files.Length);
            for (int i = 0; i < manifest.Files.Length; i++)
            {
                writer.WriteByte((byte)manifest.Files[i].Name);
                writer.WriteUInt64(manifest.Files[i].Id);
                writer.WriteUInt32((uint)manifest.Files[i].Size);
            }

            // Last, so that an older build, which reads up to the files, still reads the rest.
            writer.WriteUInt64(manifest.Key);
            session.Send(client, writer.Buffer, 0, writer.Length, true);
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            if (data[0] != WorldShareWire.MessageManifest)
            {
                return;
            }

            try
            {
                if (IsHost || sender != Protocol.HostPlayerId)
                {
                    throw new PacketFormatException("A world sent by someone who is not the host.");
                }

                reader.SetBuffer(data, 1, data.Length - 1);
                uint heardSerial = reader.ReadUInt32();
                long savedAt = (long)reader.ReadUInt64();
                int count = reader.ReadByte();
                if (count > WorldFiles.Names.Length)
                {
                    throw new PacketFormatException("A world of " + count + " files.");
                }

                var world = new WorldFile[count];
                for (int i = 0; i < count; i++)
                {
                    int name = reader.ReadByte();
                    ulong id = reader.ReadUInt64();
                    long size = reader.ReadUInt32();
                    if (name >= WorldFiles.Names.Length || size <= 0 || size > WorldFiles.MaxFileBytes)
                    {
                        throw new PacketFormatException("A world file " + name + " of " + size + " bytes.");
                    }

                    for (int j = 0; j < i; j++)
                    {
                        if (world[j].Name == name)
                        {
                            throw new PacketFormatException("A world with " + WorldFiles.Names[name] + " twice.");
                        }
                    }

                    world[i] = new WorldFile(name, id, (int)size);
                }

                ulong key = reader.Remaining >= 8 ? reader.ReadUInt64() : 0UL;
                manifest = new WorldManifest(heardSerial, savedAt, world, key);
                ManifestSerial++;
                log.Info(
                    LogCategory.Net,
                    manifest.HasWorld
                        ? "The host's world is " + SharedFile.Name(manifest.Id) + ": " + count + " file(s), " + (manifest.TotalBytes / 1024) + " kB" + SavedText(manifest) + "."
                        : "The host has no saved world to offer.");
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (Malformed == 1)
                {
                    log.Warning(LogCategory.Net, "Dropped a message about the world from player " + sender + ": " + exception.Message);
                }
            }
        }
    }
}
