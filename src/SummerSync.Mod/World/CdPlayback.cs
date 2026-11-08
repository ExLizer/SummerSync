using System;
using System.Collections;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The Satsuma's CD player sounds the same in every game: what plays is what the game that simulates the car plays.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Los CD"): the player is "CDPlayer" below the Satsuma (it hangs
    /// from one of the speakers); its machine "Update" takes the clip at "Index" of the list of "Radio/CD" that "CurrentCD"
    /// names ("CD1" to "CD3"), notes it as "CurrentTrack" (from 1) and plays it on the object's sound source, and at its end
    /// the next one; "Fetch" gives the source the speakers' volume.
    ///
    /// The game that simulates the Satsuma says what its player plays (the tally "cd/play": the CD, the track, whether it
    /// plays, the session's clock in milliseconds when the track began, and the volume in hundredths). In the others the
    /// player's machine is paused and the mod plays that track from that second at that volume. The lists are the same in
    /// every game (<see cref="CdSync"/>), so a track is the same at the same place.
    /// </summary>
    internal sealed class CdPlayback
    {
        private const string CarName = "SATSUMA(557kg, 248)";
        private const string PlayerName = "CDPlayer";
        private const string PlayerMachine = "Update";
        private const string ListObject = "Radio/CD";
        private const string Tally = "cd/play";
        private const double FindEvery = 2.0;
        private const double DriftSeconds = 0.3;
        private const int VolumeStep = 2;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private TallyReplication current;
        private Transform car;
        private Transform player;
        private PlayMakerFSM machine;
        private AudioSource source;
        private FsmString currentCd;
        private FsmInt track;
        private PausedMachine paused;
        private double nextFind;
        private int[] told;
        private int[] heard;
        private int shownSaid = -1;

        public CdPlayback(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null while there is no game world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                }

                Resume();
                current = tallies;
                told = null;
                heard = null;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            NetSession session = manager.Session;
            if (current == null || session == null || session.State != SessionState.Connected || !session.IsClockSynced)
            {
                Resume();
                return;
            }

            // Unity's null check is also true for a destroyed object.
            if (source == null && now >= nextFind)
            {
                nextFind = now + FindEvery;
                Find();
            }

            if (source == null)
            {
                return;
            }

            if (vehicles.SimulatesRoot(car))
            {
                Resume();
                Tell(session.SessionTime);
            }
            else
            {
                Pause();
                Show(session.SessionTime);
            }
        }

        /// <summary>Gives the player back to the game.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            current = null;
            Resume();
        }

        private void Find()
        {
            GameObject found = GameObject.Find(CarName);
            car = found == null ? null : found.transform;
            player = car == null ? null : PartFinder.Below(car, PlayerName);
            machine = player == null ? null : DoorSync.FindMachine(player, PlayerMachine);
            source = machine == null ? null : player.GetComponent<AudioSource>();
            currentCd = machine == null ? null : machine.FsmVariables.FindFsmString("CurrentCD");
            track = machine == null ? null : machine.FsmVariables.FindFsmInt("CurrentTrack");
            if (source != null && (currentCd == null || track == null))
            {
                log.Warning(LogCategory.Fsm, "CD player: its machine '" + PlayerMachine + "' has no 'CurrentCD' or 'CurrentTrack'; it plays in each game on its own.");
                source = null;
                nextFind = double.PositiveInfinity;
            }
        }

        // The game that simulates the car: what its player plays, told when it changes.
        private void Tell(double sessionTime)
        {
            int cd = CdNumber(currentCd.Value);
            bool playing = source.isPlaying && source.clip != null && cd > 0;
            int start = playing ? (int)((sessionTime - source.time) * 1000.0) : 0;
            int volume = Mathf.RoundToInt(source.volume * 100f);
            // The track that plays is "CurrentTrack" (from 1): "Index" is already -1 while the last one plays, for the next to
            // be the first (seen on 2026-10-18).
            var now = new[] { cd, track.Value - 1, playing ? 1 : 0, start, volume };
            if (told != null && told[0] == now[0] && told[1] == now[1] && told[2] == now[2] && (!playing || Math.Abs(told[3] - now[3]) <= DriftSeconds * 1000.0)
                && Math.Abs(told[4] - now[4]) < VolumeStep)
            {
                return;
            }

            told = now;
            current.Set(Tally, now);
        }

        private void OnStateWanted(string name, int[] values)
        {
            if (name == Tally && values != null && values.Length >= 5)
            {
                heard = values;
            }
        }

        // Another game simulates the car: its player's track, from its second, at its volume.
        private void Show(double sessionTime)
        {
            if (heard == null)
            {
                return;
            }

            AudioClip clip = heard[2] != 0 ? Clip(heard[0], heard[1]) : null;
            source.volume = Mathf.Clamp01(heard[4] / 100f);
            if (clip == null)
            {
                if (source.isPlaying)
                {
                    source.Stop();
                }

                return;
            }

            float at = (float)(sessionTime - (heard[3] / 1000.0));
            if (at < 0f || at >= clip.length)
            {
                // Before it began here, or past its end while the next one's word is on its way.
                if (source.isPlaying && source.clip == clip)
                {
                    source.Stop();
                }

                return;
            }

            if (source.clip != clip)
            {
                source.clip = clip;
            }

            if (!source.isPlaying)
            {
                source.time = at;
                source.Play();
                int shown = (heard[0] * 100) + heard[1];
                if (shown != shownSaid)
                {
                    shownSaid = shown;
                    log.Info(
                        LogCategory.Fsm,
                        "CD player: plays CD" + heard[0] + " track " + (heard[1] + 1) + " from " + at.ToString("0.0", CultureInfo.InvariantCulture)
                        + " s, as the game that simulates the Satsuma does.");
                }
            }
            else if (Math.Abs(source.time - at) > DriftSeconds)
            {
                source.time = at;
            }
        }

        private static AudioClip Clip(int cd, int track)
        {
            GameObject listObject = GameObject.Find(ListObject);
            if (listObject == null || cd < 1 || cd > 3 || track < 0)
            {
                return null;
            }

            string name = "CD" + cd.ToString(CultureInfo.InvariantCulture);
            PlayMakerArrayListProxy[] proxies = listObject.GetComponents<PlayMakerArrayListProxy>();
            for (int i = 0; i < proxies.Length; i++)
            {
                if (proxies[i] != null && proxies[i].referenceName == name)
                {
                    ArrayList list = proxies[i].arrayList;
                    return list != null && track < list.Count ? list[track] as AudioClip : null;
                }
            }

            return null;
        }

        private static int CdNumber(string name)
        {
            int number;
            return name != null && name.Length == 3 && name.StartsWith("CD", StringComparison.Ordinal)
                && int.TryParse(name.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= 3 ? number : 0;
        }

        private void Pause()
        {
            // Unity's null check is also true for a destroyed object.
            if (paused == null && machine != null && machine.enabled)
            {
                paused = new PausedMachine(machine);
                log.Info(LogCategory.Fsm, "CD player: another game simulates the Satsuma; its player is paused here and plays what that game plays.");
            }
        }

        private void Resume()
        {
            if (paused == null)
            {
                return;
            }

            paused.Resume();
            paused = null;
            shownSaid = -1;
            log.Info(LogCategory.Fsm, "CD player: this game's own again.");
        }
    }
}
