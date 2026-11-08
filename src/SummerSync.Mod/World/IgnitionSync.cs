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
    /// Keeps the ignition and the engine of every vehicle alike for every player.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 (docs/GAME_NOTES.md). Every vehicle has a
    /// state machine "Starter" that is never switched off: it cranks while its boolean "Starting" is set, goes
    /// through "Start engine", which switches the engine component on, to "Motor running", and leaves that
    /// state when its boolean "ShutOff" is set or the engine speed it reads off the dashboard falls under 50.
    /// The Satsuma's starter is made the same way with other names (dump of 2026-10-14): it runs in "Running",
    /// which it reaches from "Start engine" through "Crank up", and its running state comes from its data file.
    /// All but the moped have an ignition lock too: a state machine "Use" on the dashboard with the booleans
    /// "ACC" and "MotorOn", from which the vehicle's electricity is worked out, and a state "Motor OFF" that
    /// takes the key out and stops the engine. The starter keeps a boolean "ACC" of its own for the key being
    /// turned.
    ///
    /// The machine that simulates a vehicle is the one whose engine is real: it reports those four things and
    /// the others show them, each through its own copy of the two state machines, so that the key, the lamps
    /// on the dashboard, the exhaust and whatever else the game hangs on them follow by themselves. Whoever
    /// turns the key of a vehicle that nobody else drives becomes the machine that simulates it.
    ///
    /// Cranking is shared as a fifth thing, the starter's "Starting": a machine that shows the vehicle sets it
    /// on its own starter, which then cranks along with the sounds the game plays for it, and gets to "Motor
    /// running" by itself if the engine catches.
    /// </summary>
    internal sealed class IgnitionSync
    {
        private const string StartState = "Start engine";

        // Where the Satsuma's starter goes from "Start engine" before it runs (dump of 2026-10-14: 0.3 s in "Start
        // engine", 0.2 s here).
        private const string CrankUpState = "Crank up";
        private const string IdleState = "Wait for start";
        private const string ShutOffVariable = "ShutOff";
        private const string StartingVariable = "Starting";
        private const string KeyTurnedVariable = "ACC";
        private const string AccVariable = "ACC";
        private const string MotorVariable = "MotorOn";

        // The ignition lock: where it rests, where it is with the mouse over it, the state that takes the key
        // out, and the one that puts it in, which is the first of these that the vehicle has.
        private const string RestState = "Wait player";
        private const string HoverState = "Wait button";
        private const string OffState = "Motor OFF";
        private static readonly string[] KeyStates = { "Sound", "Test" };

        // The states a starter goes through while it cranks, on one vehicle or another.
        private static readonly string[] CrankStates = { "Check clutch", "Gear", "Turn key", "Starting engine", "Start or not", "Start or not 2" };

        private const string RunEvent = "SUMMERSYNC ENGINE RUN";
        private const string KeyInEvent = "SUMMERSYNC KEY IN";
        private const string KeyOutEvent = "SUMMERSYNC KEY OUT";
        private const string RestEvent = "SUMMERSYNC KEY REST";

        // For how long after a vehicle came to be simulated here its engine may still be getting started here
        // before this machine says that it does not run.
        private const double AdoptSeconds = 1.5;

        // How long after the dashboard of a vehicle came back into view its starter is taken to "running", so
        // that the gauge it reads the engine speed from has been read once; and how long before another try.
        private const double WakeSeconds = 0.5;
        private const double RetrySeconds = 2.0;
        private const double KeySeconds = 0.5;

        // For how long after the machine that simulates a vehicle stopped cranking this machine's starter may
        // still be ahead of or behind that one's, and how long it is given to catch on by itself once that
        // one's engine runs, before it is taken there.
        private const double CrankSeconds = 2.0;
        private const double CatchSeconds = 3.0;

        // For how long what the local player did to a key stands on a machine that does not simulate the
        // vehicle yet, while the vehicle comes to it.
        private const double OwnSeconds = 0.5;

        private readonly Logger log;
        private readonly VehiclePresenter vehicles;
        private readonly List<Engine> engines = new List<Engine>();

        public IgnitionSync(Logger log, VehiclePresenter vehicles)
        {
            this.log = log;
            this.vehicles = vehicles;
        }

        public void Clear()
        {
            engines.Clear();
        }

        /// <summary>
        /// Looks for the engines of the vehicles that are in the scene and appends what is shared about each to
        /// the names and states of the session's list of switches.
        /// </summary>
        public void Add(List<string> names, List<bool> states)
        {
            engines.Clear();
            var sorted = new List<VehicleDefinition>(VehiclePresenter.Catalog);
            sorted.Sort(delegate(VehicleDefinition a, VehicleDefinition b) { return string.CompareOrdinal(a.RootName, b.RootName); });
            var found = new StringBuilder();
            for (int v = 0; v < sorted.Count; v++)
            {
                VehicleDefinition definition = sorted[v];
                Transform root = definition.StarterPath == null ? null : VehiclePresenter.FindRoot(definition.RootName);
                if (root == null)
                {
                    continue;
                }

                string what = DoorSync.ShortName(definition.RootName);
                Transform starterAt = root.Find(definition.StarterPath);
                PlayMakerFSM starter = starterAt == null ? null : DoorSync.FindMachine(starterAt, definition.StarterMachine);
                FsmBool shutOff = starter == null ? null : starter.FsmVariables.FindFsmBool(ShutOffVariable);
                if (shutOff == null || !DoorSync.HasState(starter, definition.RunningState) || !DoorSync.HasState(starter, StartState))
                {
                    log.Warning(LogCategory.Fsm, "The starter of '" + definition.RootName + "' is not at '" + definition.StarterPath + "' as expected: its engine is not shared.");
                    continue;
                }

                var engine = new Engine
                {
                    Root = root, Label = what, Starter = starter, RunningState = definition.RunningState, KeyIndex = -1, AccIndex = -1, MotorIndex = -1, StartingIndex = -1,
                };
                Transform detail = definition.DetailPath == null ? null : root.Find(definition.DetailPath);
                engine.Detail = detail == null ? null : detail.gameObject;
                Transform lockAt = definition.IgnitionPath == null ? null : root.Find(definition.IgnitionPath);
                PlayMakerFSM ignition = lockAt == null ? null : DoorSync.FindMachine(lockAt, definition.IgnitionMachine);
                if (ignition != null && ignition.FsmVariables.FindFsmBool(AccVariable) != null && ignition.FsmVariables.FindFsmBool(MotorVariable) != null
                    && DoorSync.HasState(ignition, OffState) && DoorSync.HasState(ignition, RestState))
                {
                    engine.Ignition = ignition;
                    engine.KeyState = DoorSync.FirstState(ignition, KeyStates);
                }
                else if (definition.IgnitionPath != null)
                {
                    log.Warning(
                        LogCategory.Fsm,
                        "The ignition of '" + definition.RootName + "' is not at '" + definition.IgnitionPath + "' as expected: only whether its engine runs is shared.");
                }

                Bind(engine);
                if (engine.Ignition != null)
                {
                    string lockName = definition.RootName + "/" + definition.IgnitionPath;
                    if (engine.KeyTurned != null)
                    {
                        engine.KeyIndex = names.Count;
                        names.Add(lockName + "#key");
                        states.Add(engine.KeyTurned.Value);
                    }

                    engine.AccIndex = names.Count;
                    names.Add(lockName + "#acc");
                    states.Add(engine.Acc.Value);
                    engine.MotorIndex = names.Count;
                    names.Add(lockName + "#motor");
                    states.Add(engine.MotorOn.Value);
                    engine.KeyShown = engine.Acc.Value || engine.MotorOn.Value || (engine.KeyTurned != null && engine.KeyTurned.Value);
                }

                engine.KeyMirror = engine.KeyTurned != null && engine.KeyTurned.Value;
                engine.ShutOffWas = engine.ShutOff != null && engine.ShutOff.Value;
                bool running = starter.ActiveStateName == engine.RunningState;
                engine.RunningIndex = names.Count;
                names.Add(definition.RootName + "/" + definition.StarterPath + "#running");
                states.Add(running);
                if (engine.Starting != null)
                {
                    engine.StartingIndex = names.Count;
                    names.Add(definition.RootName + "/" + definition.StarterPath + "#starting");
                    states.Add(engine.Starting.Value);
                }

                engine.Reported = running;
                engine.Shown = running;
                engines.Add(engine);
                found.Append(found.Length == 0 ? string.Empty : "; ").Append(what)
                    .Append(engine.Ignition == null ? " (no ignition lock)" : engine.KeyShown ? " (key in)" : " (key out)")
                    .Append(running ? ", running" : ", stopped, starter in '" + starter.ActiveStateName + "'");
            }

            log.Info(LogCategory.Fsm, "Engines shared: " + (found.Length == 0 ? "none" : found.ToString()) + ".");
        }

        /// <summary>Once per frame, after the session's switches were updated.</summary>
        public void Watch(SwitchReplication current, double now)
        {
            for (int i = 0; i < engines.Count; i++)
            {
                Engine engine = engines[i];

                // Unity's null check is also true for a destroyed object.
                if (engine.Root == null || engine.Starter == null)
                {
                    continue;
                }

                Watch(engine, current, now);
            }
        }

        private void Watch(Engine engine, SwitchReplication current, double now)
        {
            PlayMakerFSM ignition = engine.Ignition;
            bool lockAwake = ignition != null && ignition.enabled && ignition.gameObject.activeInHierarchy;
            string lockState = lockAwake ? ignition.ActiveStateName : RestState;

            // The key being turned, as opposed to the mouse resting on it.
            bool turning = lockState != RestState && lockState != HoverState;
            // The local player's own cranking, and not the one this machine shows for someone else.
            bool cranking = engine.Starting != null && engine.Starting.Value && !engine.CrankShown;

            // Taking the key out is over within a frame: the ignition is back at rest by the time this runs
            // (seen on 2026-10-13: a guest who took the key out standing by the van saw it go and the engine
            // stay on). What it leaves behind tells: the starter's note of the key, which nothing else writes,
            // and the starter told to stop, which is also all there is on a vehicle without a key.
            bool turned = engine.KeyTurned != null && engine.KeyTurned.Value != engine.KeyMirror;
            if (!engine.ShutOff.Value)
            {
                engine.StopShown = false;
            }

            // The order to stop as it is given, not for as long as it stays: after the key is taken out with the starter at rest
            // the game leaves it given until the next start, and a machine that took that for a player's doing went on holding
            // the vehicle (seen on 2026-10-17: the van brought by a test aid stayed the host's, "someone is driving" it).
            bool stopped = engine.ShutOff.Value && !engine.ShutOffWas && !engine.StopShown;
            engine.ShutOffWas = engine.ShutOff.Value;
            bool acted = turning || cranking || turned || stopped;
            bool heldElsewhere = vehicles.HeldElsewhere(engine.Root);
            if (acted && !heldElsewhere)
            {
                // From the next frame the vehicle is this machine's, unless it is someone's by then. Not for the
                // mouse merely passing over the key: the vehicle would change machines every time someone
                // reached for a knob next to it (seen on 2026-10-13).
                vehicles.Touch(engine.Root, now);
            }

            string starterState = engine.Starter.ActiveStateName;
            bool running = starterState == engine.RunningState;
            bool shared = current.Get(engine.RunningIndex);

            // An engine the host's world does not have is this game's alone, as if this machine simulated its vehicle.
            if (vehicles.SimulatesRoot(engine.Root) || current.IsOnlyHere(engine.RunningIndex))
            {
                Report(engine, current, now, running, starterState, shared);
                return;
            }

            if (engine.Simulated)
            {
                engine.Simulated = false;
                engine.Adopting = false;
            }

            if ((turned || stopped) && !heldElsewhere && !engine.OwnPending)
            {
                engine.OwnPending = true;
                engine.OwnUntil = now + OwnSeconds;
                log.Info(
                    LogCategory.Fsm,
                    "The local player turned the key of '" + engine.Label + "', or stopped its engine, on a machine that does not simulate it: it stands as they left it "
                    + "while the vehicle comes here (starter in '" + starterState + "').");
            }

            if (engine.OwnPending)
            {
                if (now < engine.OwnUntil && !heldElsewhere)
                {
                    // Nothing of the other machine's is shown over what the local player just did.
                    vehicles.SetEngine(engine.Root, shared && !engine.ShutOff.Value);
                    return;
                }

                // The vehicle did not come to this machine: it is shown as its simulator has it again.
                engine.OwnPending = false;
            }

            if (cranking && heldElsewhere)
            {
                engine.Starting.Value = false;
                Refuse(engine);
            }

            bool crank = engine.StartingIndex >= 0 && current.Get(engine.StartingIndex);
            if (crank)
            {
                engine.CrankUntil = now + CrankSeconds;
                if (!engine.CrankShown && !cranking)
                {
                    // Once per attempt: if this machine's starter gives up by itself, it is not made to start
                    // over while the other one is halfway through.
                    engine.CrankShown = true;
                    engine.Starting.Value = true;
                    log.Info(LogCategory.Fsm, "'" + engine.Label + "' is being cranked on the machine that simulates it: its starter here cranks along (it is in '" + starterState + "').");
                }
            }
            else if (engine.CrankShown && !(shared && IsCranking(starterState)))
            {
                // The other machine let go of the key. If its engine caught and this starter is still at it,
                // this one is left to finish.
                engine.CrankShown = false;
                if (engine.Starting.Value)
                {
                    engine.Starting.Value = false;
                }
            }

            // While both starters crank, this one may get to "running" a moment before or after the other.
            bool cranked = crank || now < engine.CrankUntil;
            vehicles.SetEngine(engine.Root, shared || (cranked && (running || IsStarting(starterState))));
            ShowEngine(engine, now, shared, running, starterState, cranked);
            if (ignition == null)
            {
                return;
            }

            if (turning)
            {
                // The local player is at it. If the vehicle is someone else's, it is put back when they let go.
                if (heldElsewhere)
                {
                    Refuse(engine);
                }

                return;
            }

            bool key = engine.KeyIndex >= 0 && current.Get(engine.KeyIndex);
            bool acc = current.Get(engine.AccIndex);
            bool motor = current.Get(engine.MotorIndex);
            bool keyIn = key || acc || motor;
            bool keyHere = engine.Acc.Value || engine.MotorOn.Value || (engine.KeyTurned != null && engine.KeyTurned.Value);
            if (lockAwake && now >= engine.KeyAt && (keyIn != engine.KeyShown || keyIn != keyHere))
            {
                engine.KeyAt = now + KeySeconds;
                ShowKey(engine, keyIn);
                engine.KeyShown = keyIn;
            }

            if (engine.KeyTurned != null && engine.KeyIndex >= 0 && engine.KeyTurned.Value != key)
            {
                engine.KeyTurned.Value = key;
            }

            if (engine.Acc.Value != acc)
            {
                engine.Acc.Value = acc;
            }

            if (engine.MotorOn.Value != motor)
            {
                engine.MotorOn.Value = motor;
            }

            if (engine.KeyTurned != null)
            {
                engine.KeyMirror = engine.KeyTurned.Value;
            }
        }

        // This machine simulates the vehicle: its engine is the real one, and the others are told about it.
        private void Report(Engine engine, SwitchReplication current, double now, bool running, string starterState, bool shared)
        {
            if (!engine.Simulated)
            {
                engine.Simulated = true;
                engine.Refused = false;
                if (engine.CrankShown)
                {
                    // It was cranking along with the machine that had the vehicle until now.
                    engine.CrankShown = false;
                    engine.Starting.Value = false;
                }

                if (engine.ShutOff.Value && starterState == IdleState)
                {
                    // Left set while this machine showed someone else's engine; the next start would stop at once.
                    engine.ShutOff.Value = false;
                }

                engine.Tries = 0;
                engine.WakeAt = 0.0;
                // Not if the vehicle came here because the local player turned its key: what they left is it.
                engine.Adopting = running != shared && !engine.OwnPending;
                engine.OwnPending = false;
                engine.AdoptTried = false;
                engine.AdoptUntil = now + AdoptSeconds;
            }

            if (engine.Adopting)
            {
                bool detail = engine.Detail == null || engine.Detail.activeInHierarchy;
                if (running == shared)
                {
                    engine.Adopting = false;
                }
                else if (!detail)
                {
                    // The local player is far from it and the game has its dashboard switched off, with the
                    // gauge the starter reads. Nothing is decided from here: it stays as it was left.
                    engine.AdoptUntil = now + AdoptSeconds;
                    running = shared;
                }
                else if (now < engine.AdoptUntil)
                {
                    if (!engine.AdoptTried)
                    {
                        engine.AdoptTried = true;
                        if (shared && !IsStarting(starterState))
                        {
                            Run(engine);
                            log.Info(LogCategory.Fsm, "'" + engine.Label + "' is simulated here now and its engine was running: started here (its starter was in '" + starterState + "').");
                        }
                        else if (!shared && !engine.ShutOff.Value)
                        {
                            engine.ShutOff.Value = true;
                            engine.StopShown = true;
                            log.Info(LogCategory.Fsm, "'" + engine.Label + "' is simulated here now and its engine was stopped: stopped here.");
                        }
                    }

                    running = shared;
                }
                else
                {
                    engine.Adopting = false;
                    log.Warning(
                        LogCategory.Fsm,
                        "'" + engine.Label + "' came to be simulated here with its engine " + (shared ? "running" : "stopped") + ", and here it is "
                        + (running ? "running" : "stopped") + " (starter in '" + starterState + "'). The others are told what it is here.");
                }
            }

            vehicles.SetEngine(engine.Root, running);
            if (running != engine.Reported)
            {
                engine.Reported = running;
                log.Info(LogCategory.Fsm, "The engine of '" + engine.Label + "' " + (running ? "runs" : "stopped") + ", on this machine, which simulates it.");
            }

            if (engine.KeyTurned != null)
            {
                engine.KeyMirror = engine.KeyTurned.Value;
            }

            Tell(current, engine.RunningIndex, running);
            if (engine.StartingIndex >= 0)
            {
                Tell(current, engine.StartingIndex, engine.Starting.Value);
            }

            engine.Shown = running;
            if (engine.Ignition == null)
            {
                return;
            }

            bool key = engine.KeyTurned != null && engine.KeyTurned.Value;
            bool acc = engine.Acc.Value;
            bool motor = engine.MotorOn.Value;
            if (engine.KeyIndex >= 0)
            {
                Tell(current, engine.KeyIndex, key);
            }

            Tell(current, engine.AccIndex, acc);
            Tell(current, engine.MotorIndex, motor);
            engine.KeyShown = key || acc || motor;
        }

        // On its way from cranking to running, by itself.
        private static bool IsStarting(string starterState)
        {
            return starterState == StartState || starterState == CrankUpState;
        }

        private static bool IsCranking(string starterState)
        {
            for (int i = 0; i < CrankStates.Length; i++)
            {
                if (starterState == CrankStates[i])
                {
                    return true;
                }
            }

            return false;
        }

        private static void Tell(SwitchReplication current, int index, bool value)
        {
            if (current.Get(index) != value)
            {
                current.SetLocal(index, value);
            }
        }

        // Another machine simulates the vehicle: this machine's starter is taken to where that one's is.
        private void ShowEngine(Engine engine, double now, bool shared, bool running, string starterState, bool cranked)
        {
            if (shared != engine.Shown)
            {
                engine.Shown = shared;
                engine.ShownAt = now;
                engine.Tries = 0;
                engine.RetryAt = 0.0;
                log.Info(LogCategory.Fsm, "The engine of '" + engine.Label + "' " + (shared ? "runs" : "stopped") + " on the machine that simulates it: shown here.");
            }

            if (!shared)
            {
                engine.WakeAt = 0.0;
                if (running || IsStarting(starterState))
                {
                    // Not while it cranks along: there it may have caught a moment before the real one does.
                    if (!cranked && !engine.ShutOff.Value)
                    {
                        engine.ShutOff.Value = true;
                        engine.StopShown = true;
                    }
                }
                else if (engine.ShutOff.Value && starterState == IdleState)
                {
                    // Left set by the state that takes the key out; the next start would stop at once.
                    engine.ShutOff.Value = false;
                }

                return;
            }

            if (engine.ShutOff.Value)
            {
                engine.ShutOff.Value = false;
            }

            if (running || IsStarting(starterState))
            {
                return;
            }

            if (cranked && now < engine.ShownAt + CatchSeconds && IsCranking(starterState))
            {
                // It is cranking along and gets there by itself, with the sounds of an engine that catches.
                return;
            }

            if (engine.Detail != null && !engine.Detail.activeInHierarchy)
            {
                // Far from the local player: the gauge the starter reads is switched off, and with what it
                // last said the starter would stop again. It is started when the vehicle is near.
                engine.WakeAt = 0.0;
                return;
            }

            if (engine.WakeAt == 0.0)
            {
                engine.WakeAt = now + WakeSeconds;
                return;
            }

            if (now < engine.WakeAt || now < engine.RetryAt)
            {
                return;
            }

            engine.RetryAt = now + RetrySeconds;
            Run(engine);
            engine.Tries++;
            if (engine.Tries == 4 && !engine.Complained)
            {
                engine.Complained = true;
                log.Warning(
                    LogCategory.Fsm,
                    "The starter of '" + engine.Label + "' does not stay in '" + engine.RunningState + "' when taken there (it is in '" + engine.Starter.ActiveStateName
                    + "'): its engine runs on the machine that simulates it and looks stopped here.");
            }
        }

        // Takes this machine's starter to the state that switches the engine on, the way the game gets there
        // after cranking.
        private void Run(Engine engine)
        {
            try
            {
                if (!engine.StarterHooked)
                {
                    engine.StarterHooked = true;
                    if (!DoorSync.HasGlobalTransition(engine.Starter, RunEvent))
                    {
                        engine.Starter.AddGlobalTransition(RunEvent, StartState);
                    }

                    Bind(engine);
                }

                engine.ShutOff.Value = false;
                engine.Starter.SendEvent(RunEvent);
            }
            catch (Exception exception)
            {
                if (!engine.Complained)
                {
                    engine.Complained = true;
                    log.Warning(LogCategory.Fsm, "Could not start the engine of '" + engine.Label + "' for show: " + exception.Message);
                }
            }
        }

        // Makes the game put the key in and turn it, or take it out, with what it does on the dashboard then.
        private void ShowKey(Engine engine, bool keyIn)
        {
            PlayMakerFSM machine = engine.Ignition;
            try
            {
                if (!engine.LockHooked)
                {
                    engine.LockHooked = true;
                    if (!DoorSync.HasGlobalTransition(machine, KeyOutEvent))
                    {
                        machine.AddGlobalTransition(KeyOutEvent, OffState);
                    }

                    if (!DoorSync.HasGlobalTransition(machine, RestEvent))
                    {
                        machine.AddGlobalTransition(RestEvent, RestState);
                    }

                    if (engine.KeyState != null && !DoorSync.HasGlobalTransition(machine, KeyInEvent))
                    {
                        machine.AddGlobalTransition(KeyInEvent, engine.KeyState);
                    }

                    Bind(engine);
                }

                if (!keyIn)
                {
                    machine.SendEvent(KeyOutEvent);
                }
                else if (engine.KeyState != null)
                {
                    // From there the game goes on by itself to the state that turns the key, which is the one it
                    // reaches with both of these unset.
                    engine.Acc.Value = false;
                    engine.MotorOn.Value = false;
                    machine.SendEvent(KeyInEvent);
                }

                // The states this ends in wait for the mouse: left there, the key would go on to crank the
                // engine by itself, or come out again the next time the local player lets go of a button.
                if (machine.ActiveStateName != RestState)
                {
                    machine.SendEvent(RestEvent);
                }

                log.Info(
                    LogCategory.Fsm,
                    "The key of '" + engine.Label + "' is " + (keyIn ? "in" : "out") + " on the machine that simulates it: shown here (ignition now in '"
                    + machine.ActiveStateName + "').");
            }
            catch (Exception exception)
            {
                if (!engine.LockComplained)
                {
                    engine.LockComplained = true;
                    log.Warning(LogCategory.Fsm, "Could not show the key of '" + engine.Label + "' " + (keyIn ? "in" : "out") + ": " + exception.Message);
                }
            }
        }

        private void Refuse(Engine engine)
        {
            if (engine.Refused)
            {
                return;
            }

            engine.Refused = true;
            log.Info(LogCategory.Fsm, "Someone else drives '" + engine.Label + "': what the local player does to its ignition is put back.");
        }

        // The variables of the two state machines. MSCLoader rebuilds a machine's data when a transition is
        // added, and a variable may be a new object after that.
        private static void Bind(Engine engine)
        {
            engine.ShutOff = engine.Starter.FsmVariables.FindFsmBool(ShutOffVariable);
            engine.Starting = engine.Starter.FsmVariables.FindFsmBool(StartingVariable);
            engine.KeyTurned = engine.Starter.FsmVariables.FindFsmBool(KeyTurnedVariable);
            if (engine.Ignition != null)
            {
                engine.Acc = engine.Ignition.FsmVariables.FindFsmBool(AccVariable);
                engine.MotorOn = engine.Ignition.FsmVariables.FindFsmBool(MotorVariable);
            }
        }

        /// <summary>The engine of one vehicle, and where its entries are in the session's list of switches.</summary>
        private sealed class Engine
        {
            public Transform Root { get; set; }

            public string Label { get; set; }

            /// <summary>The object the game switches off while the local player is far from the vehicle; null if none is known.</summary>
            public GameObject Detail { get; set; }

            public PlayMakerFSM Starter { get; set; }

            /// <summary>The starter's state while the engine runs: "Motor running", or the vehicle's own.</summary>
            public string RunningState { get; set; }

            public FsmBool ShutOff { get; set; }

            /// <summary>Set while the local player cranks. Null on a vehicle whose starter has no such thing.</summary>
            public FsmBool Starting { get; set; }

            /// <summary>The starter's own note of the key being turned. Null on a vehicle without a key.</summary>
            public FsmBool KeyTurned { get; set; }

            /// <summary>The ignition lock; null on a vehicle that is started without one.</summary>
            public PlayMakerFSM Ignition { get; set; }

            public FsmBool Acc { get; set; }

            public FsmBool MotorOn { get; set; }

            /// <summary>The state of the ignition lock that puts the key in; null if it has none.</summary>
            public string KeyState { get; set; }

            public int KeyIndex { get; set; }

            public int AccIndex { get; set; }

            public int MotorIndex { get; set; }

            public int RunningIndex { get; set; }

            /// <summary>Place in the list of "being cranked"; -1 on a vehicle whose starter has no such thing.</summary>
            public int StartingIndex { get; set; }

            /// <summary>Whether this machine's starter was set cranking to show someone else's attempt, and until when that attempt still counts.</summary>
            public bool CrankShown { get; set; }

            public double CrankUntil { get; set; }

            /// <summary>When what the machine that simulates the vehicle says about its engine last changed.</summary>
            public double ShownAt { get; set; }

            /// <summary>Whether this machine simulated the vehicle the last time it was looked at.</summary>
            public bool Simulated { get; set; }

            /// <summary>
            /// The vehicle came to be simulated here with its engine otherwise than this machine's starter has
            /// it, and until when the starter may still get there.
            /// </summary>
            public bool Adopting { get; set; }

            public bool AdoptTried { get; set; }

            public double AdoptUntil { get; set; }

            /// <summary>What this machine last said about its engine running, and what it last showed of someone else's.</summary>
            public bool Reported { get; set; }

            public bool Shown { get; set; }

            /// <summary>Whether the key was last shown in.</summary>
            public bool KeyShown { get; set; }

            /// <summary>The starter's note of the key as this machine last left it: a change is the local player's doing.</summary>
            public bool KeyMirror { get; set; }

            /// <summary>Whether it was this class that told the starter to stop, and not the game.</summary>
            public bool StopShown { get; set; }

            /// <summary>The starter's order to stop as this machine last saw it.</summary>
            public bool ShutOffWas { get; set; }

            /// <summary>What the local player did to the key stands although another machine simulates the vehicle, and until when.</summary>
            public bool OwnPending { get; set; }

            public double OwnUntil { get; set; }

            public double KeyAt { get; set; }

            public double WakeAt { get; set; }

            public double RetryAt { get; set; }

            public int Tries { get; set; }

            public bool StarterHooked { get; set; }

            public bool LockHooked { get; set; }

            public bool Complained { get; set; }

            public bool LockComplained { get; set; }

            public bool Refused { get; set; }
        }
    }
}
