using System;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Keeps the knobs and switches on a vehicle's dashboard where the last player to touch them left them:
    /// the wipers, the interior light, the radio.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md): such a knob is a state
    /// machine with a variable that holds its position, and with states that make the game show a position:
    /// they move the lever, play the click and switch on whatever the knob works. Which machine, variable and
    /// states is in the vehicle's data file. A few have no such states: the lever of a parking brake is a
    /// number that another state machine turns into the lever's place and the brake on the wheels. The mod reads the variable to notice the local player turning a
    /// knob, and to show a knob turned by someone else it writes the variable and sends the machine an event of
    /// its own that leads to the state for that position.
    ///
    /// Anyone may turn a knob, at the wheel or not: like a door, it is where it was last put. To the session a
    /// position is a few two-state switches, the bits of its number, after the engines in the same list.
    ///
    /// Some knobs are on a part that is installed on the vehicle and taken off again (the Satsuma's wipers on its
    /// instrument panel, its radio's; data file, "under"). Those are always in the list, so that two machines whose
    /// cars are built differently for a moment agree on it, and are looked for below the vehicle while the part is
    /// not there (<see cref="PartFinder"/>). Once found, the knob is put where the session has it.
    /// </summary>
    internal sealed class KnobSync
    {
        private const string EventPrefix = "SUMMERSYNC KNOB ";

        // A knob is turned for someone else a moment after the news: its position is several switches, which do
        // not always arrive together, and a wheel that is scrolled sends one position after another.
        private const double SettleSeconds = 0.15;
        private const double RetrySeconds = 2.0;
        private const double LogEvery = 2.0;

        // A knob on a part that was just installed is put where the session has it a while later on a machine that does
        // not run the vehicle: the one that does says first how the part came.
        private const double AttachSeconds = 1.0;

        // A knob only the simulating machine tells whose copy here went elsewhere by itself (the turn signals of a copy
        // that cancel as its wheel turns back) is put back after this long, if the simulating machine has not said the
        // same by then: its word is a round trip behind what this copy does.
        private const double StraySeconds = 1.0;

        private readonly Logger log;
        private readonly PartFinder parts;
        private readonly VehiclePresenter vehicles;
        private readonly List<Knob> knobs = new List<Knob>();

        public KnobSync(Logger log, PartFinder parts, VehiclePresenter vehicles)
        {
            this.log = log;
            this.parts = parts;
            this.vehicles = vehicles;
        }

        public void Clear()
        {
            knobs.Clear();
        }

        /// <summary>
        /// Looks for the knobs of the vehicles that are in the scene and appends the switches that say where each
        /// is to the names and states of the session's list.
        /// </summary>
        public void Add(List<string> names, List<bool> states)
        {
            knobs.Clear();
            var sorted = new List<VehicleDefinition>(VehiclePresenter.Catalog);
            sorted.Sort(delegate(VehicleDefinition a, VehicleDefinition b) { return string.CompareOrdinal(a.RootName, b.RootName); });
            var found = new StringBuilder();
            for (int v = 0; v < sorted.Count; v++)
            {
                VehicleDefinition definition = sorted[v];
                Transform root = definition.Knobs.Length == 0 ? null : VehiclePresenter.FindRoot(definition.RootName);
                if (root == null)
                {
                    continue;
                }

                string what = DoorSync.ShortName(definition.RootName);
                for (int k = 0; k < definition.Knobs.Length; k++)
                {
                    VehicleKnob described = definition.Knobs[k];
                    var knob = new Knob { Definition = described, Label = what + " " + described.Name, FirstIndex = names.Count, Root = root };
                    bool onPart = described.Under != null;
                    Transform under = onPart ? PartFinder.Below(root, described.Under) : root;
                    string missing = under == null ? null : Attach(knob, under);
                    if (missing != null)
                    {
                        log.Warning(LogCategory.Fsm, "The " + described.Name + " of '" + definition.RootName + "' is not as expected (" + missing + "): it is not shared.");
                        if (!onPart)
                        {
                            continue;
                        }

                        // The list stays the same on every machine whatever this one's part is like.
                        knob.Broken = true;
                    }

                    int position = knob.Machine == null ? 0 : Local(knob);
                    knob.Known = position;
                    for (int bit = 0; bit < described.Bits; bit++)
                    {
                        names.Add(definition.RootName + "/" + (onPart ? described.Under + "/" : string.Empty) + described.Path + "#" + described.Name + "." + bit);
                        states.Add(((position >> bit) & 1) != 0);
                    }

                    knobs.Add(knob);
                    found.Append(found.Length == 0 ? string.Empty : ", ").Append(knob.Label)
                        .Append(knob.Machine == null ? " (its part is not installed)" : " at " + position.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            log.Info(LogCategory.Fsm, "Knobs shared: " + (found.Length == 0 ? "none" : found.ToString()) + ".");
        }

        /// <summary>Once per frame, after the session's switches were updated.</summary>
        public void Watch(SwitchReplication current, double now)
        {
            for (int i = 0; i < knobs.Count; i++)
            {
                Knob knob = knobs[i];

                if (knob.Broken)
                {
                    continue;
                }

                if (knob.Definition.Under != null && !Installed(knob, current, now))
                {
                    continue;
                }

                // Unity's null check is also true for a destroyed object.
                if (knob.Machine == null)
                {
                    continue;
                }

                if (knob.Definition.BySimulator)
                {
                    WatchSimulated(knob, current, now);
                }
                else
                {
                    Watch(knob, current, now);
                }
            }
        }

        // A knob that only the machine that simulates the vehicle tells: it says where its copy is; the others show that,
        // and put their copy back when it goes elsewhere by itself.
        private void WatchSimulated(Knob knob, SwitchReplication current, double now)
        {
            VehicleKnob described = knob.Definition;
            int shared = Shared(knob, current);
            bool awake = knob.Machine.enabled && knob.Machine.gameObject.activeInHierarchy;
            if (!awake)
            {
                // Far away the game switches it off: nothing is shown or told until it is back.
                knob.Known = shared;
                knob.Pending = false;
                return;
            }

            int local = Local(knob);
            bool simulates = vehicles.SimulatesRoot(knob.Root) || current.IsOnlyHere(knob.FirstIndex);
            if (simulates)
            {
                knob.Pending = false;
                knob.Tries = 0;
                if (local != shared)
                {
                    for (int bit = 0; bit < described.Bits; bit++)
                    {
                        bool value = ((local >> bit) & 1) != 0;
                        if (current.Get(knob.FirstIndex + bit) != value)
                        {
                            current.SetLocal(knob.FirstIndex + bit, value);
                        }
                    }

                    Note(knob, now, "The " + knob.Label + " is at " + local + " on this machine, which simulates the vehicle: the others are told.");
                }

                knob.Known = local;
                return;
            }

            if (shared != knob.Known)
            {
                // The simulating machine says it moved.
                knob.Known = shared;
                knob.Pending = local != shared;
                knob.ApplyAt = now + (described.Bits > 1 ? SettleSeconds : 0.0);
                knob.Tries = 0;
            }
            else if (local != shared && !knob.Pending)
            {
                // The copy here went elsewhere by itself, or the local player moved it: the simulating machine's word stands.
                knob.Pending = true;
                knob.ApplyAt = now + StraySeconds;
            }

            if (!knob.Pending)
            {
                return;
            }

            if (local == shared)
            {
                knob.Pending = false;
                return;
            }

            if (now < knob.ApplyAt)
            {
                return;
            }

            knob.ApplyAt = now + RetrySeconds;
            knob.Tries++;
            Show(knob, shared);
            if (knob.Broken)
            {
                return;
            }

            if (Local(knob) == shared)
            {
                knob.Pending = false;
                Note(knob, now, "The machine that simulates the vehicle has the " + knob.Label + " at " + shared + ": shown here (state now '" + knob.Machine.ActiveStateName + "').");
            }
            else if (knob.Tries >= 4)
            {
                knob.Broken = true;
                log.Warning(
                    LogCategory.Fsm,
                    "The " + knob.Label + " does not go to " + shared + " when told to (it is at " + Local(knob) + ", state '" + knob.Machine.ActiveStateName
                    + "'). It is not shared any more in this session.");
            }
        }

        // For a knob on a part: whether the part is on the vehicle, found again if it was put back. While it is not,
        // what the session says is only remembered; once it is, the knob is put there.
        private bool Installed(Knob knob, SwitchReplication current, double now)
        {
            // Unity's null check is also true for a destroyed object.
            if (knob.Machine != null && knob.Part != null && knob.Part.IsChildOf(knob.Root))
            {
                return true;
            }

            if (knob.Machine != null)
            {
                knob.Machine = null;
                log.Info(LogCategory.Fsm, "The part with the " + knob.Label + " came off: the knob is not shown until it is back.");
            }

            Transform part = knob.Root == null ? null : parts.Find(knob.Root, knob.Definition.Under, now);
            if (part == null)
            {
                knob.Known = Shared(knob, current);
                knob.Pending = false;
                return false;
            }

            string missing = Attach(knob, part);
            if (missing != null)
            {
                knob.Broken = true;
                log.Warning(LogCategory.Fsm, "The " + knob.Label + " is not as expected on its part (" + missing + "): it is not shared any more in this session.");
                return false;
            }

            // The session's position of a knob whose part was off means nothing yet: the machine that runs the vehicle
            // says where the part's knob is (the same part, loaded from the same save, on every machine), and the others
            // show that. A hazard switch at 0 is on.
            int local = Local(knob);
            knob.Tries = 0;

            // A knob the host's world does not have is this game's alone: nobody else says where it is.
            if (vehicles.SimulatesRoot(knob.Root) || current.IsOnlyHere(knob.FirstIndex))
            {
                VehicleKnob described = knob.Definition;
                for (int bit = 0; bit < described.Bits; bit++)
                {
                    bool value = ((local >> bit) & 1) != 0;
                    if (current.Get(knob.FirstIndex + bit) != value)
                    {
                        current.SetLocal(knob.FirstIndex + bit, value);
                    }
                }

                knob.Known = local;
                knob.Pending = false;
            }
            else
            {
                knob.Known = Shared(knob, current);
                knob.Pending = true;
                knob.ApplyAt = now + AttachSeconds;
            }

            log.Info(LogCategory.Fsm, "The part with the " + knob.Label + " is on the vehicle: the knob is shared there (session at " + Shared(knob, current) + ", here at " + local + ").");
            return true;
        }

        // The knob's machine below that object, made as the data file says. Returns what is missing, or null.
        private static string Attach(Knob knob, Transform under)
        {
            VehicleKnob described = knob.Definition;
            Transform at = under.Find(described.Path);
            knob.Part = under;
            knob.Machine = at == null ? null : DoorSync.FindMachine(at, described.Machine);
            string missing = knob.Machine == null ? "no state machine '" + described.Machine + "' at '" + described.Path + "'" : Bind(knob);
            for (int s = 0; missing == null && s < described.States.Length; s++)
            {
                if (!DoorSync.HasState(knob.Machine, described.States[s]))
                {
                    missing = "no state '" + described.States[s] + "'";
                }
            }

            if (missing != null)
            {
                knob.Machine = null;
            }

            return missing;
        }

        private static int Shared(Knob knob, SwitchReplication current)
        {
            VehicleKnob described = knob.Definition;
            int shared = 0;
            for (int bit = 0; bit < described.Bits; bit++)
            {
                shared |= current.Get(knob.FirstIndex + bit) ? 1 << bit : 0;
            }

            return shared > described.Highest ? described.Highest : shared;
        }

        private void Watch(Knob knob, SwitchReplication current, double now)
        {
            VehicleKnob described = knob.Definition;
            int shared = Shared(knob, current);
            if (shared != knob.Known)
            {
                // Someone else turned it.
                knob.Known = shared;
                knob.Pending = true;
                knob.ApplyAt = now + SettleSeconds;
                knob.Tries = 0;
            }

            // The game switches the dashboard of a far vehicle off. Nothing is turned there, and nothing that
            // changes there is a player's doing.
            bool awake = knob.Machine.enabled && knob.Machine.gameObject.activeInHierarchy;
            int local = Local(knob);
            if (knob.Pending)
            {
                if (local == shared)
                {
                    knob.Pending = false;
                    return;
                }

                if (!awake || now < knob.ApplyAt)
                {
                    return;
                }

                knob.ApplyAt = now + RetrySeconds;
                knob.Tries++;
                Show(knob, shared);
                if (knob.Broken)
                {
                    return;
                }

                if (Local(knob) == shared)
                {
                    knob.Pending = false;
                    Note(knob, now, "Someone else put the " + knob.Label + " at " + shared + ": shown here (state now '" + knob.Machine.ActiveStateName + "').");
                }
                else if (knob.Tries >= 4)
                {
                    knob.Broken = true;
                    log.Warning(
                        LogCategory.Fsm,
                        "The " + knob.Label + " does not go to " + shared + " when told to (it is at " + Local(knob) + ", state '" + knob.Machine.ActiveStateName
                        + "'). It is not shared any more in this session.");
                }

                return;
            }

            if (local == shared || !awake)
            {
                return;
            }

            // The local player turned it.
            for (int bit = 0; bit < described.Bits; bit++)
            {
                bool value = ((local >> bit) & 1) != 0;
                if (current.Get(knob.FirstIndex + bit) != value)
                {
                    current.SetLocal(knob.FirstIndex + bit, value);
                }
            }

            knob.Known = local;
            Note(knob, now, "The " + knob.Label + " was put at " + local + " here: the others are told.");
        }

        // Makes the game show the knob at a position: the variable, and the state that does the rest.
        private void Show(Knob knob, int position)
        {
            VehicleKnob described = knob.Definition;
            try
            {
                // Null for a knob the game shows straight from its variable: another state machine reads it on
                // every frame, as with the lever of a parking brake.
                string state = described.StateFor(position);
                string eventName = state == null ? null : EventPrefix + state;
                if (eventName != null && !DoorSync.HasGlobalTransition(knob.Machine, eventName))
                {
                    knob.Machine.AddGlobalTransition(eventName, state);
                    string missing = Bind(knob);
                    if (missing != null)
                    {
                        throw new InvalidOperationException(missing);
                    }
                }

                // The variable first: some states set it and some only show what it says. A state that flips it on the way
                // gets the other position. A knob read from its machine's state has none.
                int written = described.Toggles ? 1 - position : position;
                if (knob.Flag != null)
                {
                    knob.Flag.Value = written != 0;
                }
                else if (knob.Number != null)
                {
                    knob.Number.Value = written;
                }
                else if (knob.Amount != null)
                {
                    float value = described.ValueAt(position);
                    knob.Amount.Value = value;
                    for (int i = 0; i < knob.Copies.Length; i++)
                    {
                        knob.Copies[i].Value = described.Copies[i].Of(value);
                    }

                    if (knob.Moved != null)
                    {
                        Place(knob.Moved, described.Moves, value);
                    }
                }

                if (eventName != null)
                {
                    knob.Machine.SendEvent(eventName);
                }
            }
            catch (Exception exception)
            {
                knob.Broken = true;
                log.Warning(LogCategory.Fsm, "Could not put the " + knob.Label + " at " + position + ": " + exception.Message + " It is not shared any more in this session.");
            }
        }

        // Sets the angle or the coordinate that the game sets by the knob's float while the player works it.
        private static void Place(Transform moved, KnobMove move, float value)
        {
            float at = move.Of(value);
            if (move.Slides)
            {
                Vector3 position = moved.localPosition;
                position[move.Axis] = at;
                moved.localPosition = position;
            }
            else
            {
                Vector3 angles = moved.localEulerAngles;
                angles[move.Axis] = at;
                moved.localEulerAngles = angles;
            }
        }

        private static int Local(Knob knob)
        {
            if (knob.Definition.During != null)
            {
                // In a state that is no position's (the game's own setup), it is where it was last seen.
                int position = knob.Definition.PositionDuring(knob.Machine.ActiveStateName);
                if (position >= 0)
                {
                    knob.LastRead = position;
                }

                return knob.LastRead;
            }

            if (knob.Flag != null)
            {
                return knob.Flag.Value ? 1 : 0;
            }

            if (knob.Number != null)
            {
                int value = knob.Number.Value;
                return value < 0 ? 0 : value > knob.Definition.Highest ? knob.Definition.Highest : value;
            }

            return knob.Definition.PositionOf(knob.Amount.Value);
        }

        // The knob's variable, of whichever kind it is. Returns what is missing, or null. MSCLoader rebuilds a
        // machine's data when a transition is added, and a variable may be a new object after that.
        private static string Bind(Knob knob)
        {
            VehicleKnob described = knob.Definition;
            FsmVariables variables = knob.Machine.FsmVariables;
            knob.Flag = null;
            knob.Number = null;
            knob.Amount = null;
            if (described.Variable == null)
            {
                // Read from the machine's state.
                return null;
            }

            if (described.Step > 0f)
            {
                knob.Amount = variables.FindFsmFloat(described.Variable);
                if (knob.Amount == null)
                {
                    return "no float '" + described.Variable + "'";
                }

                // What else keeps the position, and what the game moves by it: below the same object as the knob.
                knob.Copies = new FsmFloat[described.Copies.Length];
                for (int i = 0; i < described.Copies.Length; i++)
                {
                    KnobCopy copy = described.Copies[i];
                    Transform at = knob.Part == null ? null : knob.Part.Find(copy.Path ?? described.Path);
                    PlayMakerFSM machine = at == null ? null : DoorSync.FindMachine(at, copy.Machine);
                    knob.Copies[i] = machine == null ? null : machine.FsmVariables.FindFsmFloat(copy.Variable);
                    if (knob.Copies[i] == null)
                    {
                        return "no float '" + copy.Variable + "' of a machine '" + copy.Machine + "' at '" + (copy.Path ?? described.Path) + "'";
                    }
                }

                knob.Moved = null;
                if (described.Moves != null)
                {
                    knob.Moved = knob.Part == null ? null : knob.Part.Find(described.Moves.Path);
                    if (knob.Moved == null)
                    {
                        return "nothing at '" + described.Moves.Path + "' to move";
                    }
                }

                return null;
            }

            knob.Flag = variables.FindFsmBool(described.Variable);
            if (knob.Flag != null)
            {
                return described.Highest == 1 ? null : "'" + described.Variable + "' is a boolean and the knob has " + (described.Highest + 1) + " positions";
            }

            knob.Number = variables.FindFsmInt(described.Variable);
            return knob.Number == null ? "no boolean or integer '" + described.Variable + "'" : null;
        }

        // A wheel that is scrolled is many positions in a row: the log gets the first and then one now and then.
        private void Note(Knob knob, double now, string text)
        {
            if (now < knob.NextLog)
            {
                return;
            }

            knob.NextLog = now + LogEvery;
            log.Info(LogCategory.Fsm, text);
        }

        private sealed class Knob
        {
            public VehicleKnob Definition { get; set; }

            public string Label { get; set; }

            public PlayMakerFSM Machine { get; set; }

            /// <summary>The vehicle's root, and the object the path is below: the root, or the part the knob is on.</summary>
            public Transform Root { get; set; }

            public Transform Part { get; set; }

            /// <summary>The variable that holds the position: one of these three.</summary>
            public FsmBool Flag { get; set; }

            public FsmInt Number { get; set; }

            public FsmFloat Amount { get; set; }

            /// <summary>For a float knob: the variables that keep the position too (<see cref="VehicleKnob.Copies"/>), in order.</summary>
            public FsmFloat[] Copies { get; set; } = new FsmFloat[0];

            /// <summary>For a float knob: the object the game moves by it, or null.</summary>
            public Transform Moved { get; set; }

            /// <summary>For a knob read from its machine's state: the position it was at when its machine was last in one.</summary>
            public int LastRead { get; set; }

            /// <summary>Where the first of its switches is in the session's list.</summary>
            public int FirstIndex { get; set; }

            /// <summary>The position the session had it at the last time this machine looked.</summary>
            public int Known { get; set; }

            /// <summary>Someone else turned it and the game has not shown that yet; when to try, and how often it was tried.</summary>
            public bool Pending { get; set; }

            public double ApplyAt { get; set; }

            public int Tries { get; set; }

            /// <summary>It does not do what this build expects: left alone for the rest of the session.</summary>
            public bool Broken { get; set; }

            public double NextLog { get; set; }
        }
    }
}
