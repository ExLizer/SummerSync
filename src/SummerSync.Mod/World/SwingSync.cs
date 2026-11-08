using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The garage doors of the house, swung alike for every player.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md): each is a body
    /// ("YARD/Building/Garage/GarageDoors/DoorLeft" and "DoorRight") that the state machine "Use" of its child
    /// "Coll" pushes with a torque about its own Z for as long as a mouse button is held, in the states "Open" and
    /// "Close". Where it is, is its own rotation about Z: that is what the game saves and loads.
    ///
    /// The machine whose player pushes a door, or whose physics moves it, says its angle about ten times a second
    /// while it moves and once more where it stops (a tally of <see cref="TallyReplication"/>, in hundredths of a
    /// degree). The others make their door kinematic and turn it there, and give it back to the physics once it has
    /// stopped. A door pushed here is this machine's, whatever the others say, until it stops.
    /// </summary>
    internal sealed class SwingSync
    {
        private const string KeyPrefix = "swing/";
        private const string UseChild = "Coll";
        private const string UseMachine = "Use";
        private static readonly string[] DoorPaths = { "YARD/Building/Garage/GarageDoors/DoorLeft", "YARD/Building/Garage/GarageDoors/DoorRight" };
        private static readonly string[] PushStates = { "Open", "Close" };

        private const double SearchInterval = 2.0;
        private const double TellInterval = 0.1;
        private const int TellStep = 50;
        private const float RestSpeed = 3f;
        private const double RestSeconds = 0.4;
        private const int DriftStep = 200;
        private const float FollowSpeed = 240f;
        private const double LetGoAfter = 0.6;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Swing> swings = new List<Swing>();
        private readonly List<Heard> heard = new List<Heard>();
        private TallyReplication current;
        private double nextSearch;
        private bool found;
        private bool searchReported;

        public SwingSync(Logger log, NetworkManager manager)
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
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                if (current != null)
                {
                    current.StateWanted -= OnStateWanted;
                    current.ChangeWanted -= OnChangeWanted;
                }

                Release();
                current = tallies;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                    current.ChangeWanted += OnChangeWanted;
                }

                swings.Clear();
                heard.Clear();
                found = false;
                searchReported = false;
                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            if (!found)
            {
                return;
            }

            for (int i = 0; i < heard.Count; i++)
            {
                Hear(heard[i], now);
            }

            heard.Clear();
            for (int i = 0; i < swings.Count; i++)
            {
                Step(swings[i], now);
            }
        }

        /// <summary>Every door goes back to the game's physics.</summary>
        public void Release()
        {
            for (int i = 0; i < swings.Count; i++)
            {
                LetGo(swings[i]);
            }
        }

        private void Search()
        {
            for (int i = 0; i < DoorPaths.Length; i++)
            {
                GameObject door = GameObject.Find(DoorPaths[i]);
                Rigidbody body = door == null ? null : door.GetComponent<Rigidbody>();
                Transform coll = door == null ? null : door.transform.Find(UseChild);
                PlayMakerFSM use = coll == null ? null : DoorSync.FindMachine(coll, UseMachine);
                if (body == null || use == null)
                {
                    continue;
                }

                var swing = new Swing { Key = KeyPrefix + door.name, Door = door.transform, Body = body, Use = use, WasKinematic = body.isKinematic };
                swing.Known = Angle(swing);
                swings.Add(swing);
            }

            found = swings.Count == DoorPaths.Length;
            if (found)
            {
                log.Info(LogCategory.Fsm, "The garage doors are shared: " + Describe(swings[0]) + ", " + Describe(swings[1]) + ".");
                if (manager.Mode == NetworkMode.Host)
                {
                    // Where the host's doors are now is the session's from the start: they may have been swung since the
                    // save the guests load, or before hosting; only a swing from here on would be told otherwise.
                    for (int i = 0; i < swings.Count; i++)
                    {
                        current.Tell(swings[i].Key, new[] { swings[i].Known }, new[] { swings[i].Known });
                    }

                    log.Info(LogCategory.Fsm, "The garage doors told where they are now, for whoever joins.");
                }
                else
                {
                    // What the host said before this world was ready: asked here too, not left to the others that ask.
                    current.RequestAll();
                }
            }
            else
            {
                swings.Clear();
                if (!searchReported)
                {
                    searchReported = true;
                    log.Warning(LogCategory.Fsm, "The garage doors were not found as this build expects them ('" + DoorPaths[0] + "' with a rigidbody and a child '" + UseChild + "' with a state machine '" + UseMachine + "'): they are each game's own.");
                }
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(KeyPrefix, StringComparison.Ordinal) && values.Length > 0)
            {
                heard.Add(new Heard { Key = key, Value = values[0], CatchUp = current.CatchingUp });
            }
        }

        private void OnChangeWanted(string key, int[] change, byte player)
        {
            if (key.StartsWith(KeyPrefix, StringComparison.Ordinal) && change.Length > 0)
            {
                heard.Add(new Heard { Key = key, Value = change[0], IsChange = true, Player = player });
            }
        }

        // Another machine moved a door: unless this one is moving it, it follows.
        private void Hear(Heard news, double now)
        {
            Swing swing = null;
            for (int i = 0; i < swings.Count; i++)
            {
                if (swings[i].Key == news.Key)
                {
                    swing = swings[i];
                }
            }

            if (swing == null || swing.Active)
            {
                return;
            }

            int before = swing.Known;
            int target = news.IsChange ? before + news.Value : news.Value;
            swing.Known = target;
            if (news.IsChange)
            {
                // The host passes it on to everyone, the one moving it too.
                current.Tell(swing.Key, new[] { before }, new[] { target });
            }

            if (!swing.Following)
            {
                swing.Following = true;
                swing.Body.isKinematic = true;
                log.Info(LogCategory.Fsm, "'" + swing.Door.name + "' is being moved in another game; it follows.");
            }

            swing.Target = target / 100f;
            swing.HeardAt = now;
            if (news.CatchUp)
            {
                // Where the host's door is now, not a swing to watch: put there at once, and let go as any followed door.
                Vector3 euler = swing.Door.localEulerAngles;
                swing.Door.localRotation = Quaternion.Euler(euler.x, euler.y, swing.Target);
                log.Info(LogCategory.Fsm, "'" + swing.Door.name + "' put at " + swing.Target.ToString("0.0", CultureInfo.InvariantCulture) + " degrees, where the host has it (a catch-up on joining).");
            }
        }

        private void Step(Swing swing, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (swing.Door == null || swing.Body == null || swing.Use == null)
            {
                return;
            }

            bool pushing = Array.IndexOf(PushStates, swing.Use.ActiveStateName) >= 0;
            int angle = Angle(swing);
            if (pushing && !swing.Active)
            {
                // The local player has it: whatever the others said, it is this machine's to move now.
                LetGo(swing);
                swing.Active = true;
                swing.RestSince = now;
            }

            if (swing.Following)
            {
                Follow(swing, now);
                return;
            }

            if (!swing.Active && Math.Abs(Mathf.DeltaAngle(swing.Known / 100f, angle / 100f)) * 100f >= DriftStep)
            {
                // Moved here by something else than the player (a car, a push): told like a push.
                swing.Active = true;
                swing.RestSince = now;
            }

            if (!swing.Active)
            {
                return;
            }

            bool moving = pushing || swing.Body.angularVelocity.magnitude * Mathf.Rad2Deg > RestSpeed;
            if (moving)
            {
                swing.RestSince = now;
            }

            bool atRest = now - swing.RestSince >= RestSeconds;
            if (angle != swing.Known && (atRest || (Math.Abs(angle - swing.Known) >= TellStep && now - swing.ToldAt >= TellInterval)))
            {
                current.Tell(swing.Key, new[] { swing.Known }, new[] { angle });
                swing.Known = angle;
                swing.ToldAt = now;
            }

            if (atRest)
            {
                swing.Active = false;
                log.Info(LogCategory.Fsm, "'" + swing.Door.name + "' was moved in this game and stopped at " + (angle / 100f).ToString("0.0", CultureInfo.InvariantCulture) + " degrees; told.");
            }
        }

        // Turned towards where the other machine has it; let go once it has been there a moment with no news.
        private void Follow(Swing swing, double now)
        {
            Vector3 euler = swing.Door.localEulerAngles;
            float step = FollowSpeed * Time.deltaTime;
            float z = Mathf.MoveTowardsAngle(euler.z, swing.Target, step);
            swing.Door.localRotation = Quaternion.Euler(euler.x, euler.y, z);
            if (Mathf.Abs(Mathf.DeltaAngle(z, swing.Target)) < 0.05f && now - swing.HeardAt >= LetGoAfter)
            {
                LetGo(swing);
            }
        }

        private void LetGo(Swing swing)
        {
            if (!swing.Following)
            {
                return;
            }

            swing.Following = false;

            // Unity's null check is also true for a destroyed object.
            if (swing.Body != null)
            {
                swing.Body.isKinematic = swing.WasKinematic;
                if (!swing.Body.isKinematic)
                {
                    swing.Body.velocity = Vector3.zero;
                    swing.Body.angularVelocity = Vector3.zero;
                    swing.Body.WakeUp();
                }
            }
        }

        // Its rotation about its own Z, in hundredths of a degree from -180 to 180.
        private static int Angle(Swing swing)
        {
            return Mathf.RoundToInt(Mathf.DeltaAngle(0f, swing.Door.localEulerAngles.z) * 100f);
        }

        private static string Describe(Swing swing)
        {
            return "'" + swing.Door.name + "' at " + (swing.Known / 100f).ToString("0.0", CultureInfo.InvariantCulture) + " degrees" + (swing.WasKinematic ? ", kinematic" : string.Empty);
        }

        private sealed class Swing
        {
            public string Key;
            public Transform Door;
            public Rigidbody Body;
            public PlayMakerFSM Use;
            public bool WasKinematic;
            public int Known;
            public bool Active;
            public double RestSince;
            public double ToldAt = double.NegativeInfinity;
            public bool Following;
            public float Target;
            public double HeardAt;
        }

        private struct Heard
        {
            public string Key;
            public int Value;
            public bool IsChange;
            public byte Player;

            /// <summary>Part of the host's answer to this game's request: where the door is, not a swing.</summary>
            public bool CatchUp;
        }
    }
}
