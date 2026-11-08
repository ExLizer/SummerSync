using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The people who walk the roads by themselves, as the host's game has them (like the traffic).
    /// Run over in any game, they fall in every game.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 (docs/GAME_NOTES.md, "Los peatones"): they are the
    /// children of "HUMANS/Randomizer/Walkers" (Alpo, Julli, Kale, Kristian, Rauno, Unto). Each has no body: its state
    /// machines "Randomizer" (puts it at a random place of the map), "Move" (walks towards "TargetPoint", two metres
    /// ahead, or stands looking at the player), "Obstacle", "Raycast" (keeps it on the ground, finds what is ahead) and
    /// "SafeTarget" move its transform, and "Move" plays the walk or the stand on "Pivot/Char/skeleton". Hit by a car
    /// ("Pivot/HumanTriggerCrime :: CarHit", or the event "FALLDOWN"), its rag doll is let go and the walker destroyed.
    ///
    /// The host's game moves them and says, about fifteen times a second, where each is and whether it walks
    /// (<see cref="NetworkManager.People"/>). A client's walkers are moved by nothing of their own: they are put
    /// where the host had them a moment ago and walk or stand as it says. Their "Randomizer" goes on: it lets cars hit
    /// them near the local player ("Pivot/HumanTriggerCrime" on within 300 m). A car of any game hits them as the game
    /// does; one that falls in a game is said ("people/fallen/&lt;name&gt;") and made to fall in the others.
    ///
    /// The games may not have the same walkers: one run over before the session is gone from that game. They are shared
    /// by name (<see cref="TrafficReplication.Define(IList{string})"/>), and the host's word is what counts: a walker the
    /// host's game no longer has is said gone at the host's first look ("people/fallen/&lt;name&gt;" at 2) and is hidden
    /// in the others, not made to fall. For that, each game notes the walkers it has had since its world loaded.
    /// </summary>
    internal sealed class PeopleSync
    {
        private const string WalkersPath = "HUMANS/Randomizer/Walkers";
        private const string FallenKey = "people/fallen/";
        private const string FallEvent = "FALLDOWN";
        private const double SearchInterval = 2.0;
        private const float TeleportDistance = 10f;

        // What "people/fallen/<name>" says: run over during the session (made to fall where it is heard as it happens),
        // or gone from the host's game already when the session started (the state of the world: hidden, not played).
        private const int FellNow = 1;
        private const int GoneBefore = 2;


        // What moves a walker, switched off where it is only shown. "Randomizer" stays: past where it first puts the
        // walker, it lets cars hit it while the local player is within 300 m.
        private static readonly string[] Moving = { "Move", "Obstacle", "Raycast", "SafeTarget", "Direction" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Walker> walkers = new List<Walker>();
        private readonly List<KeyValuePair<string, int[]>> heard = new List<KeyValuePair<string, int[]>>();

        // Whether each of those came in the host's answer to a request (a catch-up), as it was heard.
        private readonly List<bool> heardCatchUp = new List<bool>();

        // Every walker this game has had since its world loaded, by name, with or without a session.
        private readonly List<string> census = new List<string>();
        private TrafficCar[] report = new TrafficCar[0];
        private TrafficReplication current;
        private TallyReplication tallies;
        private bool host;
        private double nextSearch;
        private bool found;
        private bool searchReported;
        private bool censusTaken;
        private double nextCensus;
        private bool differenceLogged;

        public PeopleSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            if (localPlayer != null && !censusTaken && now >= nextCensus)
            {
                // Before any session: a walker run over before one starts is known by the name it had.
                nextCensus = now + SearchInterval;
                TakeCensus();
            }

            TrafficReplication people = localPlayer == null ? null : manager.People;
            TallyReplication fallen = localPlayer == null ? null : manager.Tallies;
            if (people != current || fallen != tallies)
            {
                if (tallies != null)
                {
                    tallies.StateWanted -= OnStateWanted;
                }

                Release();
                current = people;
                tallies = fallen;
                host = manager.Mode == NetworkMode.Host;
                if (tallies != null)
                {
                    tallies.StateWanted += OnStateWanted;
                }

                found = false;
                heard.Clear();
                nextSearch = now;
                differenceLogged = false;
            }

            if (current == null || tallies == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search(now);
            }

            if (!found)
            {
                return;
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i].Key, heard[i].Value, heardCatchUp[i]);
            }

            heard.Clear();
            heardCatchUp.Clear();
            WatchFalls();
            if (host)
            {
                Report(now);
            }
            else if (current.HasState)
            {
                LogDifference();
                Show(now);
            }
        }

        /// <summary>Every walker still about goes back to how the game had it.</summary>
        public void Release()
        {
            for (int i = 0; i < walkers.Count; i++)
            {
                walkers[i].Restore();
            }

            found = false;
        }

        // The walkers of the game as they are now, in the order of their names. Empty while the game has none as this
        // build expects.
        private static void Collect(List<Walker> into)
        {
            int slash = WalkersPath.IndexOf('/');
            GameObject root = GameObject.Find("/" + WalkersPath.Substring(0, slash));
            Transform group = root == null ? null : root.transform.Find(WalkersPath.Substring(slash + 1));
            into.Clear();
            for (int i = 0; group != null && i < group.childCount; i++)
            {
                Walker walker = Walker.Find(group.GetChild(i));
                if (walker != null)
                {
                    into.Add(walker);
                }
            }

            into.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        // Once the world is there: which walkers it has, before any of them can have been run over.
        private void TakeCensus()
        {
            var present = new List<Walker>();
            Collect(present);
            Note(present);
        }

        private void Note(List<Walker> seen)
        {
            for (int i = 0; i < seen.Count; i++)
            {
                censusTaken = true;
                if (!census.Contains(seen[i].Name))
                {
                    census.Add(seen[i].Name);
                }
            }
        }

        private void Search(double now)
        {
            Collect(walkers);
            if (walkers.Count == 0 || walkers.Count > TrafficWire.MaxCars)
            {
                if (!searchReported)
                {
                    searchReported = true;
                    log.Warning(LogCategory.Fsm, "People: '" + WalkersPath + "' and its walkers are not as this build expects: each game moves its own.");
                }

                walkers.Clear();
                return;
            }

            Note(walkers);
            var names = new List<string>(walkers.Count);
            for (int i = 0; i < walkers.Count; i++)
            {
                names.Add(walkers[i].Name);
            }

            current.Define(names);
            report = new TrafficCar[walkers.Count];
            found = true;
            log.Info(LogCategory.Fsm, "People: " + walkers.Count + " walker(s) found (shape " + TrafficReplication.ShapeOf(names).ToString("x8", CultureInfo.InvariantCulture) + "); "
                + (host ? "this game moves them for everyone." : "the host moves them."));
            if (host)
            {
                TellGone();
            }
            else
            {
                // What the host's world already has (who is gone there) comes in the answer.
                tallies.RequestAll();
            }
        }

        // The host: the walkers this game had and has no longer were run over before the session. They are gone for
        // everyone; a game that has them hides them.
        private void TellGone()
        {
            var gone = new StringBuilder();
            for (int c = 0; c < census.Count; c++)
            {
                string name = census[c];
                bool here = false;
                for (int i = 0; i < walkers.Count && !here; i++)
                {
                    here = walkers[i].Name == name;
                }

                if (!here)
                {
                    tallies.Set(FallenKey + name, new[] { GoneBefore });
                    gone.Append(gone.Length == 0 ? string.Empty : ", ").Append(name);
                }
            }

            if (gone.Length > 0)
            {
                log.Info(LogCategory.Fsm, "People: " + gone + " left this game before the session (run over earlier): told as gone, and hidden in the games that have them.");
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(FallenKey, StringComparison.Ordinal))
            {
                heard.Add(new KeyValuePair<string, int[]>(key, values));
                heardCatchUp.Add(tallies != null && tallies.CatchingUp);
            }
        }

        // Gone in another game: falls here, the game's way; or, if that is how the host's world already was, is
        // hidden here without falling.
        private void Hear(string key, int[] values, bool catchUp)
        {
            int said = values == null || values.Length == 0 ? 0 : values[0];
            if (said != FellNow && said != GoneBefore)
            {
                return;
            }

            string name = key.Substring(FallenKey.Length);
            bool asState = !host && (said == GoneBefore || catchUp);
            for (int i = 0; i < walkers.Count; i++)
            {
                Walker walker = walkers[i];
                if (walker.Name != name || walker.Fallen)
                {
                    continue;
                }

                walker.Fallen = true;
                walker.Told = true;
                if (!walker.Alive)
                {
                    continue;
                }

                if (asState)
                {
                    walker.Hide();
                    log.Info(LogCategory.Fsm, "People: " + name + " is gone in the host's game" + (said == GoneBefore ? " since before the session" : string.Empty) + "; hidden here.");
                }
                else
                {
                    walker.Fall();
                    log.Info(LogCategory.Fsm, "People: " + name + " was run over in another game; falls here too.");
                }
            }
        }

        // A client, once it hears the host: says which walkers the two games do not both have.
        private void LogDifference()
        {
            if (differenceLogged)
            {
                return;
            }

            differenceLogged = true;
            if (!current.ByName)
            {
                return;
            }

            var missing = new StringBuilder();
            for (int i = 0; i < walkers.Count; i++)
            {
                if (walkers[i].Alive && !current.IsActive(i))
                {
                    missing.Append(missing.Length == 0 ? string.Empty : ", ").Append(walkers[i].Name);
                }
            }

            log.Warning(
                LogCategory.Fsm,
                "People: the host's walkers are not this game's " + walkers.Count + "; those both have are shared by name. Not moved by the host: "
                + (missing.Length == 0 ? "none" : missing.ToString()) + "; the host has " + current.Unmatched + " this game does not.");
        }

        // Run over here: the others are told.
        private void WatchFalls()
        {
            for (int i = 0; i < walkers.Count; i++)
            {
                Walker walker = walkers[i];
                if (!walker.Told && !walker.Alive)
                {
                    walker.Fallen = true;
                    walker.Told = true;
                    tallies.Set(FallenKey + walker.Name, new[] { FellNow });
                    log.Info(LogCategory.Fsm, "People: " + walker.Name + " was run over in this game; told.");
                }
            }
        }

        private void Report(double now)
        {
            for (int i = 0; i < walkers.Count; i++)
            {
                Walker walker = walkers[i];
                bool about = walker.Alive && walker.Root.gameObject.activeInHierarchy;
                report[i].Active = about;
                if (!about)
                {
                    continue;
                }

                Vector3 position = walker.Root.position;
                Quaternion rotation = walker.Root.rotation;
                report[i].Pose = new BodyPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
                report[i].Flags = walker.IsWalking() ? PeopleWire.Walking : PeopleWire.Standing;
            }

            current.Report(now, 0, report);
        }

        private void Show(double now)
        {
            for (int i = 0; i < walkers.Count; i++)
            {
                Walker walker = walkers[i];
                BodyPose pose;
                if (!walker.Alive || walker.Hidden || !current.Sample(i, now, out pose))
                {
                    continue;
                }

                if (!walker.Following)
                {
                    walker.StartFollowing();
                }

                var position = new Vector3(pose.X, pose.Y, pose.Z);
                var rotation = new Quaternion(pose.QX, pose.QY, pose.QZ, pose.QW);
                walker.Root.position = position;
                walker.Root.rotation = rotation;
                walker.Show(current.Latest(i).Flags);
            }
        }

        private sealed class Walker
        {
            private readonly List<PlayMakerFSM> switchedOff = new List<PlayMakerFSM>();
            private readonly Animation skeleton;
            private readonly string walk;
            private readonly string stand;
            private readonly PlayMakerFSM hit;
            private byte shown;

            private Walker(Transform root, Animation skeleton, string walk, string stand, PlayMakerFSM hit)
            {
                Root = root;
                Name = root.name;
                this.skeleton = skeleton;
                this.walk = walk;
                this.stand = stand;
                this.hit = hit;
            }

            public Transform Root { get; }

            public string Name { get; }

            public bool Following { get; private set; }

            public bool Fallen { get; set; }

            public bool Told { get; set; }

            /// <summary>Switched off by the mod: gone in the host's game already. Switched on again by <see cref="Restore"/>.</summary>
            public bool Hidden { get; private set; }

            /// <summary>The game destroys a walker that is run over.</summary>
            public bool Alive
            {
                // Unity's null check is also true for a destroyed object.
                get { return Root != null; }
            }

            public static Walker Find(Transform root)
            {
                PlayMakerFSM move = DoorSync.FindMachine(root, "Move");
                Transform skeletonAt = root.Find("Pivot/Char/skeleton");
                Transform hitAt = root.Find("Pivot/HumanTriggerCrime");
                Animation skeleton = skeletonAt == null ? null : skeletonAt.GetComponent<Animation>();
                PlayMakerFSM hit = hitAt == null ? null : DoorSync.FindMachine(hitAt, "CarHit");
                string walk = Played(move, "Walking");
                string stand = Played(move, "Standing");
                return move == null || skeleton == null || hit == null || walk == null || stand == null ? null : new Walker(root, skeleton, walk, stand, hit);
            }

            // The animation a state of "Move" plays.
            private static string Played(PlayMakerFSM move, string stateName)
            {
                FsmState[] states = move == null || move.Fsm == null ? null : move.Fsm.States;
                for (int s = 0; states != null && s < states.Length; s++)
                {
                    if (states[s].Name != stateName)
                    {
                        continue;
                    }

                    FsmStateAction[] actions = states[s].Actions;
                    for (int a = 0; actions != null && a < actions.Length; a++)
                    {
                        var play = actions[a] as PlayAnimation;
                        if (play != null && play.animName != null && !string.IsNullOrEmpty(play.animName.Value))
                        {
                            return play.animName.Value;
                        }
                    }
                }

                return null;
            }

            public bool IsWalking()
            {
                // Unity's null check is also true for a destroyed object.
                return skeleton != null && skeleton.IsPlaying(walk);
            }

            public void StartFollowing()
            {
                Following = true;
                shown = 0;
                PlayMakerFSM[] machines = Root.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < machines.Length; i++)
                {
                    if (machines[i].enabled && Array.IndexOf(Moving, machines[i].FsmName) >= 0)
                    {
                        machines[i].enabled = false;
                        switchedOff.Add(machines[i]);
                    }
                }
            }

            public void Show(byte flags)
            {
                if (flags == shown || skeleton == null)
                {
                    return;
                }

                shown = flags;
                string clip = flags == PeopleWire.Walking ? walk : stand;
                if (skeleton[clip] != null)
                {
                    skeleton.CrossFade(clip, 0.3f);
                }
            }

            public void Fall()
            {
                // Unity's null check is also true for a destroyed object.
                if (hit != null)
                {
                    // Far from the local player the game switches the hit off; on, for its machine to take the event.
                    hit.gameObject.SetActive(true);
                    hit.SendEvent(FallEvent);
                }
            }

            /// <summary>Gone from the host's world, where it is not shown falling: switched off, nothing of it running.</summary>
            public void Hide()
            {
                Hidden = true;
                Root.gameObject.SetActive(false);
            }

            public void Restore()
            {
                for (int i = 0; i < switchedOff.Count; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (switchedOff[i] != null)
                    {
                        switchedOff[i].enabled = true;
                    }
                }

                switchedOff.Clear();
                Following = false;

                // Unity's null check is also true for a destroyed object.
                if (Hidden && Root != null)
                {
                    Root.gameObject.SetActive(true);
                }

                Hidden = false;
            }
        }
    }
}
