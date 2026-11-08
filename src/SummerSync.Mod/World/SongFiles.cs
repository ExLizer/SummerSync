using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SummerSync.Core.Hashing;
using SummerSync.Core.Logging;
using SummerSync.Core.World;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The songs of the second radio channel as files: on the host, the ones the player put into the game's
    /// "Radio" folder ("track1.ogg" and on, which is what the game itself reads, dump of 2026-10-12); on a client,
    /// a folder of the mod's own where the host's songs are kept once they have arrived. The same for the CDs, from the
    /// folders "CD1" to "CD3" (<see cref="FileChannel.Cds"/>), and for the host's car picture (<see cref="FileChannel.Art"/>, a PNG
    /// file, <see cref="ScanFiles"/>).
    ///
    /// A song received from another player is data and stays data: it is written under a name made here from
    /// its number, only into the mod's own folder, has to hash to that number and to begin like an Ogg file, and
    /// is only ever handed to the game's sound loader.
    /// </summary>
    internal sealed class SongFiles : IFileStore
    {
        /// <summary>As many tracks as the game looks for.</summary>
        public const int MaxTracks = 200;

        private const string TrackPrefix = "track";
        private const string Extension = ".ogg";
        private const string PartExtension = ".part";
        private const int HashBuffer = 256 * 1024;

        // All the host's songs a client keeps. Past this, the ones not used for longest go first.
        private const long MaxKeptBytes = 1536L * 1024 * 1024;

        private readonly Logger log;
        private readonly string cacheFolder;
        private readonly FileChannel channel;
        private readonly string extension;
        private readonly Func<byte[], int, string> kindOf;
        private readonly List<Track> tracks = new List<Track>();
        private readonly Dictionary<ulong, string> ownPaths = new Dictionary<ulong, string>();
        private readonly List<SharedFile> library = new List<SharedFile>();
        private readonly byte[] buffer = new byte[HashBuffer];
        private int hashing;
        private FileStream hashStream;
        private ulong hash;
        private FileStream readStream;
        private ulong readId;
        private FileStream writeStream;
        private ulong writeId;
        private bool complained;

        public SongFiles(Logger log, string cacheFolder)
            : this(log, cacheFolder, FileChannel.Songs)
        {
        }

        public SongFiles(Logger log, string cacheFolder, FileChannel channel)
            : this(log, cacheFolder, channel, Extension, KindOf)
        {
        }

        /// <param name="kindOf">What a file is if it is not what the game reads, from its first bytes; null if it is.</param>
        public SongFiles(Logger log, string cacheFolder, FileChannel channel, string extension, Func<byte[], int, string> kindOf)
        {
            this.log = log;
            this.cacheFolder = cacheFolder;
            this.channel = channel;
            this.extension = extension;
            this.kindOf = kindOf;
        }

        /// <summary>A PNG picture's first bytes, or what else the file is.</summary>
        public static string KindOfPicture(byte[] head, int count)
        {
            byte[] mark = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
            if (count < mark.Length)
            {
                return "not a PNG picture";
            }

            for (int i = 0; i < mark.Length; i++)
            {
                if (head[i] != mark[i])
                {
                    return "not a PNG picture";
                }
            }

            return null;
        }

        /// <summary>Whether every file found by <see cref="Scan"/> has been read through and has its number.</summary>
        public bool Ready { get; private set; }

        /// <summary>How many files <see cref="Scan"/> found.</summary>
        public int Count
        {
            get { return tracks.Count; }
        }

        /// <summary>The songs of this machine that can be offered, in the order the game has them. Complete once <see cref="Ready"/>.</summary>
        public IList<SharedFile> Library
        {
            get { return library; }
        }

        /// <summary>Megabytes of all the files found.</summary>
        public double Megabytes { get; private set; }

        /// <summary>Looks for this machine's own songs: the files the game reads at start, in the order it reads them.</summary>
        public void Scan(string radioFolder)
        {
            Scan(new[] { radioFolder }, MaxTracks);
        }

        /// <summary>
        /// Looks for this machine's own files in several folders, the first folder first, at most so many in each: the CDs.
        /// </summary>
        public void Scan(IList<string> folders, int maxTracks)
        {
            CloseAll();
            tracks.Clear();
            ownPaths.Clear();
            library.Clear();
            hashing = 0;
            Megabytes = 0.0;
            string folder = string.Empty;
            try
            {
                for (int f = 0; f < folders.Count; f++)
                {
                    folder = folders[f];
                    for (int number = 1; number <= maxTracks && Directory.Exists(folder); number++)
                    {
                        string path = Path.Combine(folder, TrackPrefix + number.ToString(CultureInfo.InvariantCulture) + Extension);
                        if (!File.Exists(path))
                        {
                            continue;
                        }

                        // The game only plays Ogg files with Vorbis in them, and leaves out of its list the ones it
                        // cannot load: a file that is left out here too keeps the two lists in step. An Ogg file with
                        // Opus in it, which is what most downloads are today, looks the same from outside (seen on
                        // 2026-10-13: two of those, and the game had loaded neither).
                        string kind = KindOfFile(path);
                        if (kind != null)
                        {
                            log.Warning(
                                LogCategory.General,
                                "'" + path + "' is " + kind + ", not an Ogg file with Vorbis sound: the game cannot play it, and it is not offered to the others. "
                                + "Convert it to Ogg Vorbis and start the game again.");
                            continue;
                        }

                        long length = new FileInfo(path).Length;
                        tracks.Add(new Track { Path = path, Size = length, Folder = f });
                        Megabytes += length / 1048576.0;
                    }
                }
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.General, "Could not look for this game's own " + channel.Plural + " in '" + folder + "': " + exception.Message);
            }

            Ready = tracks.Count == 0;
        }

        /// <summary>Looks for these files of this machine's own, in this order (the ones that are not there are left out).</summary>
        public void ScanFiles(IList<string> paths)
        {
            CloseAll();
            tracks.Clear();
            ownPaths.Clear();
            library.Clear();
            hashing = 0;
            Megabytes = 0.0;
            for (int i = 0; i < paths.Count; i++)
            {
                try
                {
                    if (!File.Exists(paths[i]))
                    {
                        continue;
                    }

                    string kind = KindOfFile(paths[i]);
                    if (kind != null)
                    {
                        log.Warning(LogCategory.General, "'" + paths[i] + "' is " + kind + ": it is not offered to the others.");
                        continue;
                    }

                    long length = new FileInfo(paths[i]).Length;
                    tracks.Add(new Track { Path = paths[i], Size = length, Folder = 0 });
                    Megabytes += length / 1048576.0;
                }
                catch (Exception exception)
                {
                    log.Warning(LogCategory.General, "Could not look at this game's own " + channel.Noun + " '" + paths[i] + "': " + exception.Message);
                }
            }

            Ready = tracks.Count == 0;
        }

        /// <summary>
        /// Reads on through the files to work out their numbers, at most <paramref name="budget"/> bytes of them:
        /// a few megabytes of music per frame, so that no frame waits for a disk.
        /// </summary>
        public void HashSome(int budget)
        {
            while (!Ready && budget > 0)
            {
                Track track = tracks[hashing];
                try
                {
                    if (hashStream == null)
                    {
                        hashStream = new FileStream(track.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        hash = Fnv1a.OffsetBasis;
                    }

                    int read = hashStream.Read(buffer, 0, Math.Min(buffer.Length, budget));
                    if (read > 0)
                    {
                        hash = Fnv1a.Hash64(buffer, 0, read, hash);
                        budget -= read;
                        continue;
                    }

                    track.Size = hashStream.Length;
                    track.Id = hash;
                }
                catch (Exception exception)
                {
                    track.Id = 0;
                    log.Warning(LogCategory.General, "Could not read this game's song '" + track.Path + "': " + exception.Message + " It is not offered to the others.");
                }

                Close(ref hashStream);
                tracks[hashing] = track;
                if (track.Id != 0 && track.Size > 0 && track.Size <= channel.MaxFileBytes && !ownPaths.ContainsKey(track.Id))
                {
                    ownPaths.Add(track.Id, track.Path);
                    library.Add(new SharedFile(track.Id, (int)track.Size));
                }

                hashing++;
                Ready = hashing >= tracks.Count;
            }
        }

        /// <summary>
        /// The numbers of this machine's files found in one of the folders scanned, in the game's order, once <see cref="Ready"/>:
        /// the same number twice if the same file is there twice. A file that cannot be offered is left out, as the game does.
        /// </summary>
        public List<ulong> IdsIn(int folder)
        {
            var ids = new List<ulong>();
            for (int i = 0; i < tracks.Count && i < hashing; i++)
            {
                if (tracks[i].Folder == folder && tracks[i].Id != 0 && ownPaths.ContainsKey(tracks[i].Id))
                {
                    ids.Add(tracks[i].Id);
                }
            }

            return ids;
        }

        /// <summary>The number of this machine's song that the game has at that place in its list; 0 if it has none yet.</summary>
        public ulong IdAt(int index)
        {
            return index >= 0 && index < tracks.Count && index < hashing ? tracks[index].Id : 0UL;
        }

        /// <summary>Where the file of that song is on this machine: one of its own, or one that arrived whole from the host.</summary>
        public bool TryGetPath(ulong id, out string path)
        {
            if (ownPaths.TryGetValue(id, out path))
            {
                return true;
            }

            path = Kept(id);
            return File.Exists(path);
        }

        /// <summary>
        /// Makes room in the folder of songs received: unfinished ones go, and, past what the folder may hold, the
        /// ones that were not used for longest.
        /// </summary>
        public void Tidy()
        {
            try
            {
                if (!Directory.Exists(cacheFolder))
                {
                    return;
                }

                string[] parts = Directory.GetFiles(cacheFolder, "*" + PartExtension);
                for (int i = 0; i < parts.Length; i++)
                {
                    File.Delete(parts[i]);
                }

                var kept = new List<FileInfo>();
                long total = 0;
                string[] whole = Directory.GetFiles(cacheFolder, "*" + extension);
                for (int i = 0; i < whole.Length; i++)
                {
                    var info = new FileInfo(whole[i]);
                    kept.Add(info);
                    total += info.Length;
                }

                kept.Sort(delegate(FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
                int removed = 0;
                for (int i = 0; i < kept.Count && total > MaxKeptBytes; i++)
                {
                    total -= kept[i].Length;
                    kept[i].Delete();
                    removed++;
                }

                log.Info(
                    LogCategory.General,
                    channel.Label + " received from hosts are kept in '" + cacheFolder + "': " + (kept.Count - removed) + " file(s), "
                    + (total / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB" + (removed > 0 ? "; " + removed + " old one(s) removed to make room." : "."));
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.General, "Could not tidy the folder of songs received, '" + cacheFolder + "': " + exception.Message);
            }
        }

        /// <summary>Lets go of every open file.</summary>
        public void CloseAll()
        {
            Close(ref hashStream);
            Close(ref readStream);
            Close(ref writeStream);
            readId = 0;
            writeId = 0;
        }

        public int Read(ulong id, int offset, byte[] target, int targetOffset, int count)
        {
            try
            {
                if (readStream == null || readId != id)
                {
                    Close(ref readStream);
                    string path;
                    if (!ownPaths.TryGetValue(id, out path))
                    {
                        return 0;
                    }

                    readStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    readId = id;
                }

                readStream.Seek(offset, SeekOrigin.Begin);
                int total = 0;
                while (total < count)
                {
                    int read = readStream.Read(target, targetOffset + total, count - total);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }

                return total;
            }
            catch (Exception exception)
            {
                Complain("read a song for another player", exception);
                Close(ref readStream);
                return 0;
            }
        }

        public bool Has(ulong id, int size)
        {
            try
            {
                if (ownPaths.ContainsKey(id))
                {
                    return true;
                }

                var info = new FileInfo(Kept(id));
                if (!info.Exists || info.Length != size)
                {
                    return false;
                }

                // Used again: it is not among the first to go when room is needed.
                info.LastWriteTimeUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Write(ulong id, int size, int offset, byte[] data, int dataOffset, int count)
        {
            try
            {
                if (writeStream == null || writeId != id)
                {
                    Close(ref writeStream);
                    Directory.CreateDirectory(cacheFolder);
                    writeStream = new FileStream(Unfinished(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    writeStream.SetLength(size);
                    writeId = id;
                }

                writeStream.Seek(offset, SeekOrigin.Begin);
                writeStream.Write(data, dataOffset, count);
            }
            catch (Exception exception)
            {
                Complain("write a part of the host's song", exception);
                Close(ref writeStream);
            }
        }

        public bool Complete(ulong id, int size)
        {
            Close(ref writeStream);
            writeId = 0;
            string unfinished = Unfinished(id);
            try
            {
                ulong found = Fnv1a.OffsetBasis;
                bool right = false;
                long length = 0;
                using (var stream = new FileStream(unfinished, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (length == 0)
                        {
                            right = kindOf(buffer, read) == null;
                        }

                        found = Fnv1a.Hash64(buffer, 0, read, found);
                        length += read;
                    }
                }

                if (found != id || length != size || !right)
                {
                    if (found == id && !right)
                    {
                        log.Warning(LogCategory.General, "The host's " + channel.Noun + " " + SharedFile.Name(id) + " arrived whole and is not what the game reads: it is not kept.");
                    }

                    return false;
                }

                string kept = Kept(id);
                if (File.Exists(kept))
                {
                    File.Delete(kept);
                }

                File.Move(unfinished, kept);
                return true;
            }
            catch (Exception exception)
            {
                Complain("check the host's song that arrived", exception);
                return false;
            }
        }

        public void Discard(ulong id)
        {
            Close(ref writeStream);
            writeId = 0;
            try
            {
                File.Delete(Unfinished(id));
            }
            catch (Exception)
            {
                // It is overwritten the next time it is fetched, or removed the next time the folder is tidied.
            }
        }

        // What a file is, if it is not what the game reads: null for an Ogg file with Vorbis sound in it (or what this kind is).
        private string KindOfFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[64];
                return kindOf(head, stream.Read(head, 0, head.Length));
            }
        }

        private static string KindOf(byte[] head, int count)
        {
            if (count < 4 || head[0] != (byte)'O' || head[1] != (byte)'g' || head[2] != (byte)'g' || head[3] != (byte)'S')
            {
                return "not an Ogg file (an MP3 or something else under that name)";
            }

            // The first page of an Ogg file says what is inside.
            if (Contains(head, count, "\u0001vorbis"))
            {
                return null;
            }

            return Contains(head, count, "OpusHead") ? "an Ogg file with Opus sound" : "an Ogg file with something other than Vorbis in it";
        }

        private static bool Contains(byte[] head, int count, string mark)
        {
            for (int at = 0; at + mark.Length <= count; at++)
            {
                int same = 0;
                while (same < mark.Length && head[at + same] == (byte)mark[same])
                {
                    same++;
                }

                if (same == mark.Length)
                {
                    return true;
                }
            }

            return false;
        }

        // The names are made here, from the number alone: nothing a peer sends is ever part of a path.
        private string Kept(ulong id)
        {
            return Path.Combine(cacheFolder, SharedFile.Name(id) + extension);
        }

        private string Unfinished(ulong id)
        {
            return Path.Combine(cacheFolder, SharedFile.Name(id) + PartExtension);
        }

        private void Complain(string what, Exception exception)
        {
            if (complained)
            {
                return;
            }

            complained = true;
            log.Warning(LogCategory.General, "Could not " + what + ": " + exception.GetType().Name + ": " + exception.Message + " Further failures of the kind are not logged.");
        }

        private static void Close(ref FileStream stream)
        {
            if (stream == null)
            {
                return;
            }

            try
            {
                stream.Close();
            }
            catch (Exception)
            {
                // Nothing to do about a file that does not close.
            }

            stream = null;
        }

        private struct Track
        {
            public string Path;
            public long Size;
            public ulong Id;
            public int Folder;
        }
    }
}
