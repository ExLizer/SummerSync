using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Makes every player's radio play what the host's plays, on both of its channels.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md). The game has one object
    /// that plays the radio, "RadioChannels", which hangs from the speaker of whichever vehicle had its volume
    /// knob touched last. Its children "Channel1" and "Folk" each have a sound source and a state machine
    /// "Update". Channel1 plays a song picked at random when the one before ends, or the programme that the state
    /// machine "Play" of "RADIO/Paikallisradio" puts on by day and hour. Folk plays what the state machine
    /// "Kansanradio" of "Radio/Folk" picks: one of the songs the player imported, which that object keeps in a
    /// list called "Songs", or an advert. The sources play whether or not a radio is on: a radio that is off is
    /// its source muted.
    ///
    /// So what is on the air is the host's to say, like the time: the host reads its own sources, and a client
    /// switches off those state machines for as long as it is in the session and puts the host's recording in
    /// its own source, at the second the host's is at. Volume, muting and which speaker it comes out of stay
    /// with the game and with the knobs, which are shared as knobs.
    ///
    /// Heard from outside a vehicle whose doors are all shut, the radio sounds muffled: each machine works that
    /// out for its own player, from the doors, which are shared, and turns down the low-pass filter the game
    /// already has on both channels for its better and worse speakers.
    ///
    /// A recording that comes with the game is told by its name. A song the host imported is told by the number
    /// its bytes hash to; the session gets the file to the client (<see cref="FileTransfer"/>), which loads it from
    /// the mod's own folder.
    /// </summary>
    internal sealed class RadioSync
    {
        private const string ChannelsName = "RadioChannels";
        private const string ChannelMachine = "Update";
        private const string SongListPath = "Radio/Folk";
        private const string SongListName = "Songs";
        private const string RadioFolder = "Radio";
        private const string CacheFolder = "RadioCache";
        private const char SongMark = '#';
        private const double FirstSearchDelay = 2.0;
        private const double SearchInterval = 3.0;

        // Further apart than this, in seconds, a client's recording is moved to where the host's is.
        private const float SeekTolerance = 0.6f;

        // Shorter than this, in seconds, what a channel plays is its way of playing nothing.
        private const float ShortestRecording = 0.5f;

        // This close to its end, a recording is left to end: the host's next one is on its way.
        private const float EndMargin = 0.3f;

        // A recording that had to be moved this often is one that cannot be moved: it is left to play as it is.
        private const int MaxMoves = 6;
        private const double CatalogueInterval = 10.0;
        private const double LogEvery = 5.0;

        // Muffled: the pitch above which sound does not get through shut doors, to begin with and within which
        // bounds it can be set; the pitch that stands for "everything gets through"; and how long the change takes.
        private const float DefaultMuffle = 800f;
        private const float LowestMuffle = 100f;
        private const float OpenPitch = 22000f;
        private const float MuffleSeconds = 0.3f;
        private const string SpeakerMachine = "Settings";
        private const string SpeakerLowPass = "LowPass";

        // Music read through per frame on the host to work out the numbers of its songs.
        private const int HashPerFrame = 1024 * 1024;

        // Songs of the host's kept loaded on a client: the one on the air and the one before it.
        private const int LoadedSongs = 2;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly Dictionary<string, AudioClip> recordings = new Dictionary<string, AudioClip>();
        private readonly List<string> missing = new List<string>();
        private readonly List<ulong> loadedIds = new List<ulong>();
        private readonly List<AudioClip> loadedClips = new List<AudioClip>();
        private readonly Station[] stations;
        private readonly SongFiles files;
        private readonly string radioFolder;
        private readonly VehiclePresenter vehicles;
        private readonly DoorSync doors;
        private float mufflePitch = DefaultMuffle;
        private bool muffles = true;
        private float muffled;
        private bool muffledSaid;
        private Transform speaker;
        private Transform speakerVehicle;
        private FsmBool speakerLowPass;
        private WorldReplication current;
        private FileTransfer songs;
        private bool found;
        private bool searchReported;
        private double nextSearch;
        private double nextCatalogue;
        private ArrayList songList;
        private bool scanned;
        private bool offered;
        private WWW loading;
        private ulong loadingId;
        private ulong failedId;

        public RadioSync(Logger log, NetworkManager manager, string settingsFolder, VehiclePresenter vehicles, DoorSync doors)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            this.doors = doors;
            files = new SongFiles(log, Path.Combine(settingsFolder ?? ".", CacheFolder));
            radioFolder = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", RadioFolder);
            stations = new[]
            {
                new Station { Channel = WorldReplication.MainChannel, Label = "radio station", SourceName = "Channel1", PickerPath = "RADIO/Paikallisradio", PickerMachine = "Play" },
                new Station { Channel = WorldReplication.FolkChannel, Label = "second radio channel", SourceName = "Folk", PickerPath = SongListPath, PickerMachine = "Kansanradio" },
            };
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null while there is no game world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            WorldReplication world = localPlayer == null ? null : manager.World;
            if (world != current)
            {
                Release();
                current = world;
                songs = world == null ? null : manager.Songs;
                found = false;
                searchReported = false;
                nextSearch = now + FirstSearchDelay;
                scanned = false;
                offered = false;
                recordings.Clear();
                missing.Clear();
                for (int i = 0; i < stations.Length; i++)
                {
                    stations[i].Forget();
                }

                if (songs != null)
                {
                    songs.SetStore(files);
                    if (manager.Mode != NetworkMode.Host)
                    {
                        files.Tidy();
                    }
                }
            }

            if (current == null)
            {
                return;
            }

            if (manager.Mode == NetworkMode.Host)
            {
                OfferSongs();
            }

            if (!found)
            {
                if (now < nextSearch)
                {
                    return;
                }

                nextSearch = now + SearchInterval;
                if (!Search())
                {
                    return;
                }
            }

            for (int i = 0; i < stations.Length; i++)
            {
                Station station = stations[i];

                // Unity's null check is also true for a destroyed object.
                if (station.Source == null)
                {
                    continue;
                }

                if (manager.Mode == NetworkMode.Host)
                {
                    Tell(station);
                }
                else
                {
                    Follow(station, now);
                }
            }

            Muffle(localPlayer);
        }

        /// <summary>
        /// The "ss muffle" console command: how muffled a vehicle's radio sounds from outside with its doors shut.
        /// A number is the highest pitch that gets through, in hertz; "off" is no muffling at all; nothing says what
        /// it is set to. Returns what it did, for the console.
        /// </summary>
        public string SetMuffle(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return muffles
                    ? "Through shut doors, a vehicle's radio gets through up to " + mufflePitch.ToString("0", CultureInfo.InvariantCulture) + " Hz. Right now it is "
                      + (muffled > 0.5f ? "muffled" : "clear") + " for you."
                    : "A vehicle's radio is not muffled by its doors.";
            }

            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
            {
                muffles = false;
                return "A vehicle's radio is not muffled by its doors any more. This is for this game only, until it is closed.";
            }

            float pitch;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out pitch) || pitch < LowestMuffle || pitch > OpenPitch)
            {
                return "A pitch in hertz from " + LowestMuffle.ToString("0", CultureInfo.InvariantCulture) + " to " + OpenPitch.ToString("0", CultureInfo.InvariantCulture)
                    + ", or off, please. Lower is more muffled: 400 is a thick wall, 800 a shut car, 2000 a thin door.";
            }

            muffles = true;
            mufflePitch = pitch;
            log.Info(LogCategory.Fsm, "Test aid: through shut doors a vehicle's radio gets through up to " + pitch.ToString("0", CultureInfo.InvariantCulture) + " Hz now.");
            return "Through shut doors, a vehicle's radio gets through up to " + pitch.ToString("0", CultureInfo.InvariantCulture) + " Hz now. This is for this game only, until it is closed.";
        }

        // Whether the radio is in a vehicle with every door shut and the local player outside it, and the filter
        // of both channels a little further towards what that sounds like.
        private void Muffle(Transform localPlayer)
        {
            Transform channels = null;
            for (int i = 0; i < stations.Length && channels == null; i++)
            {
                // Unity's null check is also true for a destroyed object.
                channels = stations[i].Source == null ? null : stations[i].Source.transform.parent;
            }

            if (channels == null)
            {
                return;
            }

            // The game hangs the radio from the speaker of whichever vehicle had its volume touched last.
            Transform hangsFrom = channels.parent;
            if (hangsFrom != speaker)
            {
                speaker = hangsFrom;
                speakerVehicle = vehicles.RootOf(hangsFrom);
                PlayMakerFSM settings = hangsFrom == null ? null : Machine(hangsFrom.gameObject, SpeakerMachine);
                speakerLowPass = settings == null ? null : settings.FsmVariables.FindFsmBool(SpeakerLowPass);
            }

            bool shut = false;
            if (muffles && speakerVehicle != null)
            {
                bool anyOpen;
                shut = doors.CountDoors(speakerVehicle, out anyOpen) > 0 && !anyOpen && !vehicles.IsInside(speakerVehicle, localPlayer.position);
            }

            float before = muffled;
            muffled = Mathf.MoveTowards(muffled, shut ? 1f : 0f, Time.deltaTime / MuffleSeconds);
            if (shut != muffledSaid && (muffled == 0f || muffled == 1f))
            {
                muffledSaid = shut;
                log.Info(
                    LogCategory.Fsm,
                    shut
                        ? "The radio is in '" + speakerVehicle.name + "', its doors are shut and the local player is outside: it sounds muffled here."
                        : "The radio sounds clear here again: a door is open, the local player is inside, or it is not in a vehicle.");
            }

            // The speaker's own setting stands when nothing is muffled: some of the game's speakers are dull as they are.
            bool dull = speakerLowPass != null && speakerLowPass.Value;
            for (int i = 0; i < stations.Length; i++)
            {
                Station station = stations[i];
                if (station.Source == null || station.Filter == null)
                {
                    continue;
                }

                float open = dull ? station.FilterPitch : OpenPitch;
                if (muffled <= 0f)
                {
                    if (before > 0f)
                    {
                        station.Filter.cutoffFrequency = station.FilterPitch;
                        station.Filter.enabled = dull;
                    }

                    continue;
                }

                // Pitch is heard in ratios: halfway is the pitch in the middle of the two by ear, not by number.
                float closed = Mathf.Min(mufflePitch, open);
                station.Filter.cutoffFrequency = open * Mathf.Pow(closed / open, muffled);
                if (!station.Filter.enabled)
                {
                    station.Filter.enabled = true;
                }
            }
        }

        /// <summary>Gives the radio back to the game: its own state machines decide what plays again.</summary>
        public void Release()
        {
            bool any = false;
            for (int i = 0; i < stations.Length; i++)
            {
                any |= stations[i].SwitchOn();
            }

            if (any)
            {
                log.Info(LogCategory.Fsm, "The radio is this game's own again.");
            }

            // The filters go back to what the speaker the radio hangs from is like.
            for (int i = 0; i < stations.Length; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (muffled > 0f && stations[i].Filter != null)
                {
                    stations[i].Filter.cutoffFrequency = stations[i].FilterPitch;
                    stations[i].Filter.enabled = speakerLowPass != null && speakerLowPass.Value;
                }
            }

            muffled = 0f;
            muffledSaid = false;
            speaker = null;
            speakerVehicle = null;
            speakerLowPass = null;

            if (loading != null)
            {
                loading.Dispose();
                loading = null;
            }

            for (int i = 0; i < loadedClips.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (loadedClips[i] != null)
                {
                    UnityEngine.Object.Destroy(loadedClips[i]);
                }
            }

            loadedClips.Clear();
            loadedIds.Clear();
            files.CloseAll();
        }

        // The host: its own songs, found, numbered a little per frame, and then offered to the others.
        private void OfferSongs()
        {
            if (!scanned)
            {
                scanned = true;
                files.Scan(radioFolder);
                log.Info(
                    LogCategory.General,
                    files.Count == 0
                        ? "This game has no songs of its own in '" + radioFolder + "': the second radio channel has nothing to send to the others."
                        : "This game has " + files.Count + " song(s) of its own in '" + radioFolder + "', "
                          + files.Megabytes.ToString("0.#", CultureInfo.InvariantCulture) + " MB: working out their numbers.");
            }

            if (!files.Ready)
            {
                files.HashSome(HashPerFrame);
                return;
            }

            if (!offered && songs != null)
            {
                offered = true;
                songs.Offer(files.Library);
                if (files.Count > 0)
                {
                    log.Info(LogCategory.General, files.Library.Count + " of this game's " + files.Count + " song(s) are offered to the other players.");
                }
            }
        }

        // The host: what one of its channels plays, for the others.
        private void Tell(Station station)
        {
            AudioSource source = station.Source;
            AudioClip clip = source.clip;
            if (clip != null && clip.length < ShortestRecording)
            {
                // With no songs of the player's own, the second channel plays a recording of no length over and
                // over, more than once a second: that is the channel being silent, not something to tell.
                clip = null;
            }

            if (clip != station.ToldClip)
            {
                // The name of a recording is a new string every time it is asked for.
                station.ToldClip = clip;
                station.ToldName = clip == null ? null : NameOf(station, clip);
                if (clip != null && station.ToldName != station.Logged)
                {
                    station.Logged = station.ToldName;
                    log.Info(
                        LogCategory.Fsm,
                        "The " + station.Label + " plays '" + (station.ToldName ?? "a song of this game's own that has no number yet") + "' now (" + Clock(clip.length)
                        + " long): the others are told.");
                }
            }
            else if (clip != null && station.ToldName == null)
            {
                // One of this game's own songs, played before its number was worked out.
                station.ToldName = NameOf(station, clip);
            }

            bool playing = clip != null && station.ToldName != null && source.isPlaying;
            current.SetRadio(station.Channel, new RadioState { Clip = playing ? station.ToldName : null, Position = playing ? source.time : 0f, Playing = playing });
        }

        // What the others are told a recording is: its name, or for one of this game's own songs the number of its file.
        private string NameOf(Station station, AudioClip clip)
        {
            int index = station.Channel == WorldReplication.FolkChannel && songList != null ? songList.IndexOf(clip) : -1;
            if (index < 0)
            {
                return clip.name;
            }

            if (songList.Count != files.Count)
            {
                if (!station.Complained)
                {
                    station.Complained = true;
                    log.Warning(
                        LogCategory.Fsm,
                        "The game has " + songList.Count + " song(s) of the player's own loaded and '" + radioFolder + "' has " + files.Count
                        + " file(s): which file is which song cannot be told, and the others do not hear them.");
                }

                return null;
            }

            ulong id = files.IdAt(index);
            return id == 0 ? null : SongMark + SharedFile.Name(id);
        }

        // A client: the host's recording, at the second the host's is at.
        private void Follow(Station station, double now)
        {
            RadioState heard;
            if (!current.TryGetRadio(station.Channel, out heard))
            {
                return;
            }

            // From the host's first word on, what plays is not for this game to decide.
            if (station.SwitchOff())
            {
                log.Info(LogCategory.Fsm, "The " + station.Label + " plays what the host's plays from now on: this game's own choice is switched off.");
            }

            AudioSource source = station.Source;
            if (!heard.Playing || string.IsNullOrEmpty(heard.Clip))
            {
                Silence(station);
                return;
            }

            AudioClip recording = heard.Clip[0] == SongMark ? HostSong(station, heard.Clip, now) : Find(heard.Clip, now);
            if (recording == null)
            {
                Silence(station);
                return;
            }

            if (heard.Position >= recording.length - EndMargin)
            {
                return;
            }

            if (source.clip != recording || station.ShownName != heard.Clip)
            {
                // Started, then moved: a source that is not playing does not always keep the place it is given.
                source.clip = recording;
                source.Play();
                source.time = heard.Position;
                station.ShownName = heard.Clip;
                station.Moved = 0;
                if (heard.Clip != station.Logged)
                {
                    station.Logged = heard.Clip;
                    log.Info(LogCategory.Fsm, "The host's " + station.Label + " plays '" + heard.Clip + "', " + Clock(heard.Position) + " into it: played here from there.");
                }

                return;
            }

            if (!source.isPlaying)
            {
                source.Play();
                source.time = heard.Position;
                return;
            }

            float apart = source.time - heard.Position;
            if (Mathf.Abs(apart) <= SeekTolerance || station.Moved > MaxMoves)
            {
                return;
            }

            source.time = heard.Position;
            station.Moved++;
            if (station.Moved > MaxMoves)
            {
                log.Warning(
                    LogCategory.Fsm,
                    "'" + heard.Clip + "' does not stay where it is put on this machine (" + apart.ToString("0.0", CultureInfo.InvariantCulture)
                    + " s from the host after " + MaxMoves + " moves): it is left to play as it is.");
            }
            else if (now >= station.NextLog)
            {
                station.NextLog = now + LogEvery;
                log.Info(
                    LogCategory.Fsm,
                    "This game was " + apart.ToString("0.0", CultureInfo.InvariantCulture) + " s " + (apart > 0f ? "ahead of" : "behind") + " the host in '" + heard.Clip
                    + "': moved to where the host is. " + station.Moved + " time(s) in this recording.");
            }
        }

        private static void Silence(Station station)
        {
            if (station.Source.isPlaying)
            {
                station.Source.Stop();
            }

            station.ShownName = null;
        }

        // One of the host's own songs: asked of the session if it is not here, loaded from its file if it is.
        private AudioClip HostSong(Station station, string name, double now)
        {
            ulong id;
            if (name.Length != 17 || !ulong.TryParse(name.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id) || id == 0)
            {
                return null;
            }

            int at = loadedIds.IndexOf(id);
            if (at >= 0 && loadedClips[at] != null)
            {
                return Ready(loadedClips[at]) ? loadedClips[at] : null;
            }

            if (songs != null)
            {
                songs.Want(id);
            }

            string path;
            if (id == failedId || !files.TryGetPath(id, out path))
            {
                if (now >= station.NextLog && songs != null)
                {
                    station.NextLog = now + LogEvery;
                    log.Info(
                        LogCategory.Fsm,
                        "The host's " + station.Label + " plays its own song " + name + ", which is " + (id == failedId ? "a file this game cannot play" : "not here yet ("
                        + ((int)(songs.Progress(id) * 100f)).ToString(CultureInfo.InvariantCulture) + " % arrived)") + ": silent until it is.");
                }

                return null;
            }

            if (loading == null)
            {
                loadingId = id;
                loading = new WWW("file:///" + path.Replace('\\', '/'));
                return null;
            }

            if (!loading.isDone)
            {
                return null;
            }

            if (loadingId != id)
            {
                // The host has gone on to another song while this one was being loaded.
                loading.Dispose();
                loading = null;
                return null;
            }

            AudioClip clip = null;
            string error = loading.error;
            if (string.IsNullOrEmpty(error))
            {
                // Kept compressed: a song takes its few megabytes, and can be played from any second of it.
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
                failedId = id;
                log.Warning(LogCategory.Fsm, "The host's song " + name + " arrived and the game cannot load it" + (string.IsNullOrEmpty(error) ? "." : ": " + error));
                return null;
            }

            clip.name = name;
            loadedIds.Add(id);
            loadedClips.Add(clip);
            while (loadedClips.Count > LoadedSongs)
            {
                // Unity's null check is also true for a destroyed object.
                if (loadedClips[0] != null)
                {
                    UnityEngine.Object.Destroy(loadedClips[0]);
                }

                loadedClips.RemoveAt(0);
                loadedIds.RemoveAt(0);
            }

            log.Info(LogCategory.Fsm, "Loaded the host's song " + name + " (" + Clock(clip.length) + " long).");
            return Ready(clip) ? clip : null;
        }

        // The recording the game calls that, among all it has loaded. Null if this game has none by that name.
        private AudioClip Find(string name, double now)
        {
            AudioClip recording;
            if (recordings.TryGetValue(name, out recording) && recording != null)
            {
                return recording;
            }

            if (now >= nextCatalogue)
            {
                // Once, and again if a name turns up that was not there: the game loads some recordings late.
                nextCatalogue = now + CatalogueInterval;
                recordings.Clear();
                UnityEngine.Object[] loaded = Resources.FindObjectsOfTypeAll(typeof(AudioClip));
                for (int i = 0; i < loaded.Length; i++)
                {
                    var clip = loaded[i] as AudioClip;
                    if (clip == null)
                    {
                        continue;
                    }

                    string key = Protocol.LimitText(clip.name, RadioState.MaxClipBytes);
                    if (key.Length > 0 && key[0] != SongMark && !recordings.ContainsKey(key))
                    {
                        recordings.Add(key, clip);
                    }
                }

                if (recordings.TryGetValue(name, out recording) && recording != null)
                {
                    return recording;
                }
            }

            if (!missing.Contains(name))
            {
                missing.Add(name);
                log.Warning(
                    LogCategory.Fsm,
                    "The host's radio plays '" + name + "', and this game has no recording by that name among the " + recordings.Count
                    + " it has loaded: the radio is silent here until the next one.");
            }

            return null;
        }

        private bool Search()
        {
            int sources = 0;
            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(AudioSource));
            for (int i = 0; i < all.Length; i++)
            {
                var candidate = all[i] as AudioSource;
                if (candidate == null || candidate.transform.parent == null || candidate.transform.parent.name != ChannelsName)
                {
                    continue;
                }

                string name = candidate.name;
                for (int s = 0; s < stations.Length; s++)
                {
                    if (stations[s].Source == null && name == stations[s].SourceName)
                    {
                        stations[s].Source = candidate;
                        sources++;
                    }
                }
            }

            if (sources == 0)
            {
                if (!searchReported)
                {
                    searchReported = true;
                    log.Info(LogCategory.Fsm, "No '" + ChannelsName + "' with its sound sources yet: the radio is shared once it is there.");
                }

                return false;
            }

            found = true;
            for (int s = 0; s < stations.Length; s++)
            {
                Station station = stations[s];
                if (station.Source == null)
                {
                    log.Warning(LogCategory.Fsm, "The " + station.Label + " has no sound source '" + ChannelsName + "/" + station.SourceName + "' as this build expects: it is not shared.");
                    continue;
                }

                station.Filter = station.Source.GetComponent<AudioLowPassFilter>();
                station.FilterPitch = station.Filter == null ? OpenPitch : station.Filter.cutoffFrequency;
                if (station.Filter == null)
                {
                    log.Warning(LogCategory.Fsm, "The " + station.Label + " has no low-pass filter on its sound source as this build expects: it does not sound muffled through shut doors.");
                }

                station.Channel1 = Machine(station.Source.gameObject, ChannelMachine);
                GameObject picker = GameObject.Find(station.PickerPath);
                station.Picker = picker == null ? null : Machine(picker, station.PickerMachine);
                if (station.Channel1 == null || station.Picker == null)
                {
                    log.Warning(
                        LogCategory.Fsm,
                        "The " + station.Label + " was found, but not " + (station.Channel1 == null ? "its state machine '" + ChannelMachine + "'" : string.Empty)
                        + (station.Channel1 == null && station.Picker == null ? " nor " : string.Empty)
                        + (station.Picker == null ? "'" + station.PickerPath + "' :: '" + station.PickerMachine + "'" : string.Empty)
                        + " as this build expects: on a client the game may put on something of its own now and then.");
                }

                log.Info(
                    LogCategory.Fsm,
                    "The " + station.Label + ": sound source on '" + ChannelsName + "/" + station.SourceName + "', "
                    + (station.Source.clip == null ? "nothing on" : "'" + station.Source.clip.name + "' on") + (station.Source.isPlaying ? ", playing" : ", not playing") + ". "
                    + (manager.Mode == NetworkMode.Host ? "This machine says what plays." : "The host says what plays."));
            }

            songList = null;
            GameObject keeper = GameObject.Find(SongListPath);
            PlayMakerArrayListProxy[] lists = keeper == null ? new PlayMakerArrayListProxy[0] : keeper.GetComponents<PlayMakerArrayListProxy>();
            for (int i = 0; i < lists.Length; i++)
            {
                if (lists[i].referenceName == SongListName)
                {
                    songList = lists[i].arrayList;
                }
            }

            if (manager.Mode == NetworkMode.Host)
            {
                log.Info(
                    LogCategory.Fsm,
                    songList == null
                        ? "No list '" + SongListName + "' on '" + SongListPath + "' as this build expects: the player's own songs cannot be told to the others."
                        : "The game has " + songList.Count + " song(s) of the player's own loaded for the second radio channel.");
            }

            return true;
        }

        // Whether a song that was loaded from a file can be played yet.
        private static bool Ready(AudioClip clip)
        {
            AudioDataLoadState state = clip.loadState;
            return state != AudioDataLoadState.Loading && state != AudioDataLoadState.Failed;
        }

        private static PlayMakerFSM Machine(GameObject on, string name)
        {
            PlayMakerFSM[] machines = on.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }

        // "3:05" for 185 seconds.
        private static string Clock(float seconds)
        {
            int whole = seconds < 0f ? 0 : (int)seconds;
            return (whole / 60).ToString(CultureInfo.InvariantCulture) + ":" + (whole % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>One channel of the radio: its sound source, and the two state machines that decide what it plays.</summary>
        private sealed class Station
        {
            // Paused, not switched off: on again with "RestartOnEnable" the folk channel's picker starts from "Check", finds
            // its song database already there and destroys its own object (2026-10-18: a guest that left a session in the
            // world lost its second channel).
            private PausedMachine channelOff;
            private PausedMachine pickerOff;

            public int Channel { get; set; }

            public string Label { get; set; }

            public string SourceName { get; set; }

            public string PickerPath { get; set; }

            public string PickerMachine { get; set; }

            public AudioSource Source { get; set; }

            /// <summary>The low-pass filter the game has on the source, and the pitch the game had it at.</summary>
            public AudioLowPassFilter Filter { get; set; }

            public float FilterPitch { get; set; }

            /// <summary>The state machine on the source that plays one recording after another.</summary>
            public PlayMakerFSM Channel1 { get; set; }

            /// <summary>The state machine elsewhere that chooses programmes or songs.</summary>
            public PlayMakerFSM Picker { get; set; }

            /// <summary>On the host: the recording last told, and what it was told as.</summary>
            public AudioClip ToldClip { get; set; }

            public string ToldName { get; set; }

            /// <summary>On a client: what the host's recording was last played here as, and how often it was moved.</summary>
            public string ShownName { get; set; }

            public int Moved { get; set; }

            public double NextLog { get; set; }

            public bool Complained { get; set; }

            /// <summary>The recording the log last had a line about: the same one again is not said twice in a row.</summary>
            public string Logged { get; set; }

            /// <summary>Stops this game deciding what the channel plays. True if that changed something.</summary>
            public bool SwitchOff()
            {
                bool changed = false;

                // Unity's null check is also true for a destroyed object.
                if (Channel1 != null && Channel1.enabled)
                {
                    channelOff = new PausedMachine(Channel1);
                    changed = true;
                }

                if (Picker != null && Picker.enabled)
                {
                    pickerOff = new PausedMachine(Picker);
                    changed = true;
                }

                return changed;
            }

            /// <summary>Gives the channel back to the game. True if that changed something.</summary>
            public bool SwitchOn()
            {
                bool changed = channelOff != null || pickerOff != null;
                if (channelOff != null)
                {
                    channelOff.Resume();
                }

                if (pickerOff != null)
                {
                    pickerOff.Resume();
                }

                channelOff = null;
                pickerOff = null;
                return changed;
            }

            /// <summary>A new session, or a new game world: nothing found before counts.</summary>
            public void Forget()
            {
                Source = null;
                Filter = null;
                Channel1 = null;
                Picker = null;
                ToldClip = null;
                ToldName = null;
                ShownName = null;
                Moved = 0;
                NextLog = 0.0;
                Complained = false;
                Logged = null;
            }
        }
    }
}
