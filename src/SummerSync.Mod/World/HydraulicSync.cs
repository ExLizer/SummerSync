using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The hydraulic levers of a vehicle (the tractor's front loader), the same on every machine.
    ///
    /// What is assumed about the game, from the dump of 2026-10-15 (docs/GAME_NOTES.md): each lever is a state machine
    /// with a float that is the angle of the part it moves. While the lever is held up or down (the mouse on it, or a key)
    /// the machine is in a state of its own that moves the float by the engine's speed ("RPMvalmet") and turns the part by
    /// it in its own coordinates, plays the hydraulics' sound on the way in, and goes on to a state that plays it again as
    /// the lever is let go. Which machine, float, states and part is in the vehicle's data file ("hydraulics").
    ///
    /// Where the part ends up depends on how long the lever is held and on the engine, which is the simulating machine's: so
    /// that machine says, as the tally "hyd/(vehicle)/(lever)" (how the lever is held, and the angle in hundredths of a
    /// degree; ten times a second while it moves). The others turn their part towards that angle smoothly, hold their
    /// lever the same way and play the sound as it is pulled and let go, with their own machine at rest. A player who works
    /// a lever on a vehicle another machine simulates and nobody drives takes the vehicle, as with its key
    /// (<see cref="VehiclePresenter.Touch"/>); with someone at the wheel elsewhere, what they did is undone.
    /// </summary>
    internal sealed class HydraulicSync
    {
        private const string TallyPrefix = "hyd/";
        private const double SearchEvery = 3.0;

        // While the part moves on the simulating machine, its angle is told this often, if it changed by this much (in
        // hundredths of a degree).
        private const double TellEvery = 0.1;
        private const int LeastChange = 10;

        // How fast a copy closes on the angle it was told, as a share of the gap per second; and from how far it is put there.
        private const float Closing = 8f;
        private const float JumpDegrees = 30f;

        // A player this near a lever counts as working it; further away, a key pressed elsewhere is not theirs to make count.
        private const float ReachMetres = 3f;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly List<Lever> levers = new List<Lever>();

        // What the session said of levers not found yet, by tally.
        private readonly Dictionary<string, int[]> heardEarly = new Dictionary<string, int[]>();
        private TallyReplication current;
        private Transform player;
        private bool found;
        private double nextSearch;

        public HydraulicSync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null while there is no game world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current || localPlayer != player)
            {
                Release();
                current = tallies;
                player = localPlayer;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                nextSearch = now + SearchEvery;
            }

            NetSession session = manager.Session;
            if (current == null || session == null || session.State != SessionState.Connected)
            {
                return;
            }

            if (!found)
            {
                if (now < nextSearch)
                {
                    return;
                }

                nextSearch = now + SearchEvery;
                Find();
            }

            for (int i = 0; i < levers.Count; i++)
            {
                Lever lever = levers[i];

                // Unity's null check is also true for a destroyed object.
                if (lever.Machine == null || lever.Root == null || lever.Part == null)
                {
                    continue;
                }

                Watch(lever, now);
            }
        }

        /// <summary>Lets go of the session; the parts stay where they are.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            current = null;
            levers.Clear();
            heardEarly.Clear();
            found = false;
        }

        private void Find()
        {
            levers.Clear();
            var described = new StringBuilder();
            VehicleDefinition[] catalog = VehiclePresenter.Catalog;
            for (int v = 0; v < catalog.Length; v++)
            {
                VehicleDefinition definition = catalog[v];
                Transform root = definition.Hydraulics.Length == 0 ? null : VehiclePresenter.FindRoot(definition.RootName);
                for (int h = 0; root != null && h < definition.Hydraulics.Length; h++)
                {
                    VehicleHydraulic hydraulic = definition.Hydraulics[h];
                    Transform at = root.Find(hydraulic.Path);
                    PlayMakerFSM machine = at == null ? null : DoorSync.FindMachine(at, hydraulic.Machine);
                    FsmFloat angle = machine == null ? null : machine.FsmVariables.FindFsmFloat(hydraulic.Variable);
                    Transform part = root.Find(hydraulic.Moves.Path);
                    Transform handle = root.Find(hydraulic.LeverPath);
                    if (angle == null || part == null || handle == null || !DoorSync.HasState(machine, hydraulic.Sound) || !DoorSync.HasState(machine, hydraulic.Up))
                    {
                        log.Warning(LogCategory.Vehicle, "Hydraulics: the " + hydraulic.Name + " of '" + definition.RootName + "' is not as this build expects (its machine, float, part or lever): it is each game's own.");
                        continue;
                    }

                    var lever = new Lever
                    {
                        Definition = hydraulic,
                        Root = root,
                        Machine = machine,
                        Angle = angle,
                        Part = part,
                        Handle = handle,
                        Name = TallyPrefix + definition.Nick + "/" + hydraulic.Name,
                        Label = DoorSync.ShortName(definition.RootName) + " " + hydraulic.Name,
                    };
                    levers.Add(lever);
                    described.Append(described.Length == 0 ? string.Empty : ", ").Append(lever.Label).Append(" at ").Append(Degrees(lever.Angle.Value));
                }
            }

            found = true;
            log.Info(LogCategory.Vehicle, "Hydraulics shared: " + (described.Length == 0 ? "none" : described.ToString()) + ".");
            if (current != null)
            {
                // What the session already said before these were found.
                for (int i = 0; i < levers.Count; i++)
                {
                    int[] values;
                    if (heardEarly.TryGetValue(levers[i].Name, out values))
                    {
                        Hear(levers[i], values, true);
                    }
                }
            }

            heardEarly.Clear();
        }

        private void Watch(Lever lever, double now)
        {
            VehicleHydraulic described = lever.Definition;
            int held = described.LeverDuring(lever.Machine.ActiveStateName);
            if (vehicles.SimulatesRoot(lever.Root))
            {
                lever.Undoing = false;
                lever.SoundDue = false;
                Tell(lever, held < 0 ? lever.ToldLever : held, now);
                return;
            }

            if (held == 1 || held == 2)
            {
                // This machine's player works it: the machine here is never taken to those states for someone else.
                if (!vehicles.HeldElsewhere(lever.Root) && Near(lever))
                {
                    vehicles.Touch(lever.Root, now);
                    if (!lever.Touching)
                    {
                        lever.Touching = true;
                        log.Info(LogCategory.Vehicle, "Hydraulics: the " + lever.Label + " is worked here, and nobody drives it elsewhere: it comes to this machine.");
                    }

                    return;
                }

                if (!lever.Undoing)
                {
                    lever.Undoing = true;
                    log.Info(LogCategory.Vehicle, "Hydraulics: the " + lever.Label + " was worked here while another machine runs the vehicle with someone at its wheel (or from afar): undone.");
                }

                Send(lever, described.Sound);
                lever.Angle.Value = lever.Shown;
                return;
            }

            lever.Touching = false;
            lever.Undoing = false;
            Show(lever, now);
        }

        // The simulating machine: how the lever is held and where the part is, when that changed.
        private void Tell(Lever lever, int held, double now)
        {
            int angle = Mathf.RoundToInt(lever.Angle.Value * 100f);
            lever.Shown = lever.Angle.Value;
            bool changed = !lever.Told || held != lever.ToldLever || (held == 0 && angle != lever.ToldAngle);
            bool moved = Math.Abs(angle - lever.ToldAngle) >= LeastChange && now >= lever.NextTell;
            if (!changed && !moved)
            {
                return;
            }

            lever.Told = true;
            lever.ToldLever = held;
            lever.ToldAngle = angle;
            lever.NextTell = now + TellEvery;
            current.Set(lever.Name, new[] { held, angle });
            if (changed)
            {
                log.Info(LogCategory.Vehicle, "Hydraulics: the " + lever.Label + " is " + Holding(held) + " here, at " + Degrees(lever.Angle.Value) + "; the others are told.");
            }
        }

        // Another machine simulates the vehicle: the part closes on the angle it has, the lever is held as it is there.
        private void Show(Lever lever, double now)
        {
            if (!lever.Heard)
            {
                lever.Shown = lever.Angle.Value;
                return;
            }

            VehicleHydraulic described = lever.Definition;
            float target = Mathf.Clamp(lever.HeardAngle / 100f, described.Min, described.Max);
            float shown = lever.Shown;
            float gap = target - shown;
            shown = Mathf.Abs(gap) > JumpDegrees || lever.PutThere ? target : shown + (gap * Mathf.Min(1f, Time.deltaTime * Closing));
            lever.PutThere = false;
            lever.Shown = shown;
            lever.Angle.Value = shown;
            Vector3 angles = lever.Part.localEulerAngles;
            angles[described.Moves.Axis] = described.Moves.Of(shown);
            lever.Part.localEulerAngles = angles;

            Vector3 handle = lever.Handle.localEulerAngles;
            handle[described.LeverAxis] = lever.HeardLever == 1 ? described.LeverUp : lever.HeardLever == 2 ? described.LeverDown : 0f;
            lever.Handle.localEulerAngles = handle;

            if (lever.SoundDue)
            {
                // Pulled or let go there: the game's own sound, from its machine here, which goes back to rest.
                lever.SoundDue = false;
                Send(lever, described.Sound);
            }
        }

        private void OnStateWanted(string name, int[] values)
        {
            if (!name.StartsWith(TallyPrefix, StringComparison.Ordinal) || values.Length < 2)
            {
                return;
            }

            for (int i = 0; i < levers.Count; i++)
            {
                if (levers[i].Name == name)
                {
                    Hear(levers[i], values, current != null && current.CatchingUp);
                    return;
                }
            }

            // Not found yet: kept for when it is.
            heardEarly[name] = values;
        }

        private void Hear(Lever lever, int[] values, bool catchingUp)
        {
            int held = values[0];
            if (lever.Heard && !catchingUp && (held == 0) != (lever.HeardLever == 0))
            {
                lever.SoundDue = true;
            }

            if (!lever.Heard || catchingUp)
            {
                lever.PutThere = true;
            }

            lever.Heard = true;
            lever.HeardLever = held;
            lever.HeardAngle = values[1];

            // Should this machine take the vehicle over, it has nothing new to tell. On the simulating machine this is its own
            // word coming back, older than what it has.
            if (catchingUp || !vehicles.SimulatesRoot(lever.Root))
            {
                lever.Told = true;
                lever.ToldLever = held;
                lever.ToldAngle = values[1];
            }
        }

        private bool Near(Lever lever)
        {
            // Unity's null check is also true for a destroyed object.
            return player != null && (vehicles.IsInside(lever.Root, player.position) || (player.position - lever.Handle.position).sqrMagnitude < ReachMetres * ReachMetres);
        }

        private void Send(Lever lever, string state)
        {
            try
            {
                string eventName = "SUMMERSYNC HYDRAULICS " + state;
                if (!DoorSync.HasGlobalTransition(lever.Machine, eventName))
                {
                    lever.Machine.AddGlobalTransition(eventName, state);

                    // MSCLoader rebuilds a machine's data when a transition is added.
                    lever.Angle = lever.Machine.FsmVariables.FindFsmFloat(lever.Definition.Variable) ?? lever.Angle;
                }

                lever.Machine.SendEvent(eventName);
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Vehicle, "Hydraulics: could not take the " + lever.Label + " to '" + state + "': " + exception.Message);
            }
        }

        private static string Holding(int held)
        {
            return held == 1 ? "held up" : held == 2 ? "held down" : "let go";
        }

        private static string Degrees(float degrees)
        {
            return degrees.ToString("0.0", CultureInfo.InvariantCulture) + " degrees";
        }

        private sealed class Lever
        {
            public VehicleHydraulic Definition { get; set; }

            public Transform Root { get; set; }

            public PlayMakerFSM Machine { get; set; }

            public FsmFloat Angle { get; set; }

            /// <summary>The part the game turns by the angle, and the lever.</summary>
            public Transform Part { get; set; }

            public Transform Handle { get; set; }

            /// <summary>The tally's name, and what the log calls it.</summary>
            public string Name { get; set; }

            public string Label { get; set; }

            /// <summary>What this machine last told, or heard, and when it may tell the angle again.</summary>
            public bool Told { get; set; }

            public int ToldLever { get; set; }

            public int ToldAngle { get; set; }

            public double NextTell { get; set; }

            /// <summary>What the simulating machine says.</summary>
            public bool Heard { get; set; }

            public int HeardLever { get; set; }

            public int HeardAngle { get; set; }

            /// <summary>The angle shown here, closing on what was heard.</summary>
            public float Shown { get; set; }

            /// <summary>The next angle is put there at once (the first one heard, or one of a catch-up).</summary>
            public bool PutThere { get; set; }

            /// <summary>The lever was pulled or let go there and the sound is still to play here.</summary>
            public bool SoundDue { get; set; }

            public bool Touching { get; set; }

            public bool Undoing { get; set; }
        }
    }
}
