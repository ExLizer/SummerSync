using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The host's CDs in every game.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Los CD"): "Radio/CD", kept from the menu on, has three lists
    /// ("CD1" to "CD3") that "Radio/CD/CDn :: LoadSongs" fills, when the music is imported, with the files "CDn/track1.ogg" to
    /// "track16.ogg" of the game's folder that load. When the world loads, a CD's case destroys itself and its disc if its
    /// list is empty, and the Satsuma's player plays the clips of the disc's list in order.
    ///
    /// The host offers its CD tracks (<see cref="FileChannel.Cds"/>) and says which go on which CD and in what order: the
    /// tally "cd/(n)" is how many tracks the CD has and, four to a number, their places in the list offered. It says it again
    /// every few seconds, for whoever connected since. A guest who plays the host's world has the host's CDs in its lists from
    /// the menu on: the tracks that are here, and a silent stand-in for each one still on its way, so the cases are there
    /// and the player keeps its place; each track goes into its place when it arrives. Back in its own world, the game loads
    /// its own CDs again, its own way.
    /// </summary>
    internal static class CdSync
    {
        private const string ListObject = "Radio/CD";
        private const string LoaderMachine = "LoadSongs";
        private const string LoaderStart = "Init";
        private const string ReloadEvent = "SUMMERSYNC CD RELOAD";
        private const string TallyPrefix = "cd/";
        private const string CacheFolder = "CdCache";
        private const int MaxTracks = 16;
        private const int HashPerFrame = 2 * 1024 * 1024;
        private const double TellEvery = 5.0;
        private const double CheckEvery = 0.5;

        // A track still on its way: silent, long enough that the player does not run through the rest of the CD meanwhile,
        // and small (a thousand samples a second).
        private const int SilenceRate = 1000;
        private const int SilenceSeconds = 600;

        private static readonly string[] Lists = { "CD1", "CD2", "CD3" };

        private static Logger log;
        private static SongFiles files;
        private static string[] folders;
        private static FileTransfer current;
        private static TallyReplication tallies;
        private static double clock;

        // The host: its CDs' numbers, in the game's order, once worked out and offered.
        private static bool scanned;
        private static bool offered;
        private static List<ulong>[] own;
        private static double nextTell;

        // A guest: what the host said of each CD, as places in its list offered (null until said).
        private static readonly int[][] heard = new int[3][];
        private static bool usingHost;
        private static double nextCheck;
        private static AudioClip silence;
        private static readonly Dictionary<ulong, AudioClip> loaded = new Dictionary<ulong, AudioClip>();
        private static readonly HashSet<ulong> unplayable = new HashSet<ulong>();
        private static WWW loading;
        private static ulong loadingId;

        /// <summary>Whether it broke: then each game keeps its own CDs until it is closed.</summary>
        public static bool Failed { get; private set; }

        /// <summary>Every frame, in every scene.</summary>
        public static void Update(NetworkManager manager, Logger logger, double now, string settingsFolder)
        {
            if (Failed)
            {
                return;
            }

            try
            {
                Run(manager, logger, now, settingsFolder);
            }
            catch (Exception exception)
            {
                Failed = true;
                logger.Error(LogCategory.General, "Sharing the host's CDs failed; each game has its own until it is closed. " + exception);
                try
                {
                    UseOwn();
                }
                catch (Exception)
                {
                    // Whatever broke may break the clean-up too.
                }
            }
        }

        /// <summary>One line for the overlay, or null.</summary>
        public static string Describe()
        {
            return current == null ? null : current.Describe();
        }

        private static void Run(NetworkManager manager, Logger logger, double now, string settingsFolder)
        {
            log = logger;
            clock = now;
            if (files == null)
            {
                string game = Path.GetDirectoryName(Application.dataPath) ?? ".";
                folders = new[] { Path.Combine(game, "CD1"), Path.Combine(game, "CD2"), Path.Combine(game, "CD3") };
                files = new SongFiles(logger, Path.Combine(settingsFolder ?? ".", CacheFolder), FileChannel.Cds);
            }

            FileTransfer transfer = manager.Cds;
            if (transfer != current)
            {
                current = transfer;
                scanned = false;
                offered = false;
                own = null;
                for (int i = 0; i < heard.Length; i++)
                {
                    heard[i] = null;
                }

                if (current != null)
                {
                    current.SetStore(files);
                    if (manager.Mode != NetworkMode.Host)
                    {
                        files.Tidy();
                    }
                }
            }

            TallyReplication said = current == null ? null : manager.Tallies;
            if (said != tallies)
            {
                if (tallies != null)
                {
                    tallies.StateWanted -= OnStateWanted;
                }

                tallies = said;
                if (tallies != null)
                {
                    tallies.StateWanted += OnStateWanted;
                }
            }

            if (current != null && manager.Mode == NetworkMode.Host)
            {
                Offer(manager);
            }

            if (now < nextCheck)
            {
                return;
            }

            nextCheck = now + CheckEvery;
            if (current != null && manager.Mode == NetworkMode.Client && HostWorld.PlaysHostWorld && Heard())
            {
                UseHost();
            }
            else if (usingHost)
            {
                UseOwn();
            }
        }

        // The host: its CD folders found, numbered a little per frame, offered, and the order of each CD told now and then.
        private static void Offer(NetworkManager manager)
        {
            if (!scanned)
            {
                scanned = true;
                files.Scan(folders, MaxTracks);
                log.Info(
                    LogCategory.General,
                    files.Count == 0
                        ? "This game has no CD tracks of its own (CD1 to CD3): the CDs have nothing to send to the others."
                        : "This game has " + files.Count + " CD track(s) of its own, " + files.Megabytes.ToString("0.#", CultureInfo.InvariantCulture) + " MB: working out their numbers.");
            }

            if (!files.Ready)
            {
                files.HashSome(HashPerFrame);
                return;
            }

            if (!offered)
            {
                offered = true;
                current.Offer(files.Library);
                own = new List<ulong>[Lists.Length];
                for (int i = 0; i < own.Length; i++)
                {
                    own[i] = files.IdsIn(i);
                }

                if (files.Count > 0)
                {
                    log.Info(
                        LogCategory.General,
                        "This game's CDs are offered to the other players: " + own[0].Count + ", " + own[1].Count + " and " + own[2].Count + " track(s) on CD1 to CD3.");
                }
            }

            NetSession session = manager.Session;
            if (tallies == null || session == null || session.State != SessionState.Connected || clock < nextTell)
            {
                return;
            }

            nextTell = clock + TellEvery;
            var places = new Dictionary<ulong, int>();
            IList<SharedFile> list = current.Files;
            for (int i = 0; i < list.Count; i++)
            {
                places[list[i].Id] = i;
            }

            for (int n = 0; n < own.Length; n++)
            {
                tallies.Set(TallyPrefix + (n + 1).ToString(CultureInfo.InvariantCulture), Pack(own[n], places));
            }
        }

        // How many tracks, and their places in the list offered, four to a number (each place plus one; 0 for none).
        private static int[] Pack(List<ulong> ids, Dictionary<ulong, int> places)
        {
            var values = new int[1 + ((MaxTracks + 3) / 4)];
            int count = 0;
            for (int i = 0; i < ids.Count && count < MaxTracks; i++)
            {
                int place;
                if (!places.TryGetValue(ids[i], out place) || place > 254)
                {
                    continue;
                }

                values[1 + (count / 4)] |= (place + 1) << (8 * (count % 4));
                count++;
            }

            values[0] = count;
            return values;
        }

        private static int[] Unpack(int[] values)
        {
            int count = values.Length > 0 ? Math.Max(0, Math.Min(MaxTracks, values[0])) : 0;
            var places = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int word = 1 + (i / 4);
                int place = word < values.Length ? ((values[word] >> (8 * (i % 4))) & 0xFF) - 1 : -1;
                if (place >= 0)
                {
                    places.Add(place);
                }
            }

            return places.ToArray();
        }

        private static void OnStateWanted(string name, int[] values)
        {
            if (values == null || !name.StartsWith(TallyPrefix, StringComparison.Ordinal))
            {
                return;
            }

            int number;
            if (!int.TryParse(name.Substring(TallyPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 1 || number > heard.Length)
            {
                return;
            }

            int[] places = Unpack(values);
            int[] was = heard[number - 1];
            heard[number - 1] = places;
            if (was == null || was.Length != places.Length)
            {
                log.Info(LogCategory.General, "The host's CD" + number + " has " + places.Length + " track(s).");
            }
        }

        private static bool Heard()
        {
            for (int i = 0; i < heard.Length; i++)
            {
                if (heard[i] == null)
                {
                    return false;
                }
            }

            return true;
        }

        // A guest who plays the host's world: its lists are the host's CDs, with what has arrived of them.
        private static void UseHost()
        {
            GameObject listObject = GameObject.Find(ListObject);
            if (listObject == null)
            {
                return;
            }

            PlayMakerArrayListProxy[] proxies = listObject.GetComponents<PlayMakerArrayListProxy>();
            IList<SharedFile> offeredFiles = current.Files;
            bool changed = false;
            for (int n = 0; n < Lists.Length; n++)
            {
                ArrayList list = List(proxies, Lists[n]);
                if (list == null)
                {
                    continue;
                }

                int[] places = heard[n];
                var wanted = new List<object>(places.Length);
                for (int i = 0; i < places.Length; i++)
                {
                    ulong id = places[i] < offeredFiles.Count ? offeredFiles[places[i]].Id : 0UL;
                    AudioClip clip;
                    wanted.Add(id != 0 && loaded.TryGetValue(id, out clip) && clip != null ? clip : Silence());
                    if (id != 0 && !loaded.ContainsKey(id))
                    {
                        Load(id);
                    }
                }

                if (!Same(list, wanted))
                {
                    list.Clear();
                    list.AddRange(wanted);
                    changed = true;
                }
            }

            if (changed || !usingHost)
            {
                if (!usingHost)
                {
                    log.Info(
                        LogCategory.General,
                        "The CDs here are the host's now: " + heard[0].Length + ", " + heard[1].Length + " and " + heard[2].Length + " track(s), " + loaded.Count + " of them here.");
                }

                usingHost = true;
            }
        }

        // Back in this player's own world: the game loads this game's own CDs again, as when the music is imported.
        private static void UseOwn()
        {
            if (!usingHost)
            {
                return;
            }

            usingHost = false;
            GameObject listObject = GameObject.Find(ListObject);
            if (listObject != null)
            {
                for (int n = 0; n < Lists.Length; n++)
                {
                    Transform loaderObject = listObject.transform.Find(Lists[n]);
                    PlayMakerFSM loader = loaderObject == null ? null : DoorSync.FindMachine(loaderObject, LoaderMachine);
                    if (loaderObject != null && loader != null)
                    {
                        // From its start, which empties the list: it ends switched off, and may not start over when switched on.
                        loaderObject.gameObject.SetActive(true);
                        loader.enabled = true;
                        if (loader.GetGlobalTransition(ReloadEvent) == null)
                        {
                            loader.AddGlobalTransition(ReloadEvent, LoaderStart);
                        }

                        loader.SendEvent(ReloadEvent);
                    }
                }
            }

            if (loading != null)
            {
                loading.Dispose();
                loading = null;
            }

            foreach (AudioClip clip in loaded.Values)
            {
                // Unity's null check is also true for a destroyed object.
                if (clip != null)
                {
                    UnityEngine.Object.Destroy(clip);
                }
            }

            loaded.Clear();
            unplayable.Clear();
            log.Info(LogCategory.General, "The CDs here are this game's own again: the game loads them from CD1 to CD3.");
        }

        // One of the host's tracks, from the file that arrived, one at a time.
        private static void Load(ulong id)
        {
            if (unplayable.Contains(id))
            {
                return;
            }

            if (loading == null)
            {
                string path;
                if (!files.TryGetPath(id, out path))
                {
                    return;
                }

                loadingId = id;
                loading = new WWW("file:///" + path.Replace('\\', '/'));
                return;
            }

            if (!loading.isDone)
            {
                return;
            }

            ulong done = loadingId;
            AudioClip clip = null;
            string error = loading.error;
            if (string.IsNullOrEmpty(error))
            {
                clip = loading.GetAudioClipCompressed(false);
                if (clip == null)
                {
                    clip = loading.GetAudioClip(false, true, AudioType.OGGVORBIS);
                }
            }

            loading.Dispose();
            loading = null;
            if (clip == null)
            {
                unplayable.Add(done);
                log.Warning(LogCategory.General, "The host's CD track " + SharedFile.Name(done) + " arrived and the game cannot load it" + (string.IsNullOrEmpty(error) ? "." : ": " + error));
                return;
            }

            clip.name = SharedFile.Name(done);
            loaded[done] = clip;
            log.Info(LogCategory.General, "The host's CD track " + clip.name + " is here and goes into its place.");
        }

        private static AudioClip Silence()
        {
            // Unity's null check is also true for a destroyed object.
            if (silence == null)
            {
                silence = AudioClip.Create("SummerSync CD track on its way", SilenceRate * SilenceSeconds, 1, SilenceRate, false);
            }

            return silence;
        }

        private static bool Same(ArrayList list, List<object> wanted)
        {
            if (list.Count != wanted.Count)
            {
                return false;
            }

            for (int i = 0; i < wanted.Count; i++)
            {
                if (!ReferenceEquals(list[i], wanted[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static ArrayList List(PlayMakerArrayListProxy[] proxies, string name)
        {
            for (int i = 0; i < proxies.Length; i++)
            {
                if (proxies[i] != null && proxies[i].referenceName == name)
                {
                    return proxies[i].arrayList;
                }
            }

            return null;
        }
    }
}
