using System;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Keeps a trailer hitched or not alike on every machine, and has whoever runs the towing vehicle's physics run the
    /// trailer's too.
    ///
    /// What is assumed about the game, from the dump of 2026-10-15 and the level file (docs/GAME_NOTES.md): the tractor's
    /// flatbed is a vehicle of its own (car physics, wheels, its own body) held to the tractor by a joint on it. A state
    /// machine on the tractor's hitch ("Trailer/Hook", "Distance") hitches it when it comes near enough, from its state
    /// "Sound" on, and says so with a boolean ("TrailerAttached"); a click on the hitch's other machine
    /// ("Trailer/Remove", "Use", state "Close door") lets it go. Which machines and states is in the towing vehicle's
    /// data file ("hitches").
    ///
    /// A joint between two vehicles only works on a machine that runs the physics of both: on another, one of them is a
    /// copy that does not give, and a tractor held to a copy cannot pull it. So a hitched trailer is held by the machine
    /// that simulates its tractor, as the hand holds what it carries (<see cref="ObjectReplication.SetHung"/>), and goes
    /// to whoever drives the tractor. On the other machines both are copies, and the joint between them does nothing.
    ///
    /// To the session a hitch is one switch, after the horns: whether it is hitched. Whoever's game hitches or unhitches
    /// it tells, as with a knob, except that a game that does not simulate the tractor hitches only because its copies
    /// came near: that is not told, and is undone a moment later if the simulating machine has not hitched it too.
    /// Shown for someone else, the game is taken to its own states that hitch and unhitch.
    /// </summary>
    internal sealed class TowSync
    {
        private const string EventPrefix = "SUMMERSYNC HITCH ";

        // How long a hitch this game made by itself is left before it is undone, if the simulating machine does not hitch
        // it too: its copies are a round trip behind the real ones.
        private const double StraySeconds = 1.5;
        private const double RetrySeconds = 2.0;

        // How near its hitch the towed copy has to be for this game to hitch it for someone else (the game hitches at 0.3 m),
        // and how often that is looked at while it is not.
        private const float HitchReach = 0.5f;
        private const double NearLookSeconds = 0.2;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly List<Hitch> hitches = new List<Hitch>();

        public TowSync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
        }

        /// <summary>Forgets the hitches; what this machine held for them is let go of.</summary>
        public void Clear()
        {
            for (int i = 0; i < hitches.Count; i++)
            {
                Hang(hitches[i], false);
            }

            hitches.Clear();
        }

        /// <summary>
        /// Looks for the hitches of the vehicles that are in the scene and appends the switch that says whether each holds
        /// what it tows to the names and states of the session's list.
        /// </summary>
        public void Add(List<string> names, List<bool> states)
        {
            Clear();
            var sorted = new List<VehicleDefinition>(VehiclePresenter.Catalog);
            sorted.Sort(delegate(VehicleDefinition a, VehicleDefinition b) { return string.CompareOrdinal(a.RootName, b.RootName); });
            var found = new StringBuilder();
            for (int v = 0; v < sorted.Count; v++)
            {
                VehicleDefinition definition = sorted[v];
                Transform root = definition.Hitches.Length == 0 ? null : VehiclePresenter.FindRoot(definition.RootName);
                for (int h = 0; root != null && h < definition.Hitches.Length; h++)
                {
                    VehicleHitch described = definition.Hitches[h];
                    var hitch = new Hitch
                    {
                        Definition = described,
                        Root = root,
                        Towed = VehiclePresenter.FindRoot(described.Towed),
                        Index = names.Count,
                        Label = DoorSync.ShortName(definition.RootName) + "'s hitch for " + DoorSync.ShortName(described.Towed),
                    };
                    Transform at = root.Find(described.Path);
                    hitch.Machine = at == null ? null : DoorSync.FindMachine(at, described.Machine);
                    hitch.Hitched = hitch.Machine == null ? null : hitch.Machine.FsmVariables.FindFsmBool(described.Variable);
                    hitch.TowedPoint = hitch.Towed == null ? null : hitch.Towed.Find(described.TowedPoint);
                    Transform release = root.Find(described.ReleasePath);
                    hitch.Release = release == null ? null : DoorSync.FindMachine(release, described.ReleaseMachine);
                    bool local = hitch.Hitched != null && hitch.Hitched.Value;
                    names.Add(definition.RootName + "/" + described.Path + "#hitch " + described.Towed);
                    states.Add(local);
                    hitches.Add(hitch);
                    hitch.Known = local;
                    hitch.WasHitched = local;
                    if (hitch.Hitched == null || hitch.Towed == null || hitch.TowedPoint == null || !DoorSync.HasState(hitch.Machine, described.Attach)
                        || (hitch.Release != null && !DoorSync.HasState(hitch.Release, described.Release)))
                    {
                        hitch.Broken = true;
                        log.Warning(
                            LogCategory.Fsm,
                            "The " + hitch.Label + " is not as expected (" + (hitch.Towed == null ? "no '" + described.Towed + "' in the scene" : "its machines or states")
                            + "): it is not shared.");
                        continue;
                    }

                    found.Append(found.Length == 0 ? string.Empty : ", ").Append(hitch.Label).Append(local ? " (hitched)" : " (free)");
                }
            }

            log.Info(LogCategory.Fsm, "Hitches shared: " + (found.Length == 0 ? "none" : found.ToString()) + ".");
        }

        /// <summary>Once per frame, after the session's switches were updated.</summary>
        public void Watch(SwitchReplication current, double now)
        {
            for (int i = 0; i < hitches.Count; i++)
            {
                Hitch hitch = hitches[i];

                // Unity's null check is also true for a destroyed object.
                if (hitch.Broken || hitch.Machine == null || hitch.Root == null || hitch.Towed == null)
                {
                    continue;
                }

                Watch(hitch, current, now);
            }
        }

        private void Watch(Hitch hitch, SwitchReplication current, double now)
        {
            bool local = hitch.Hitched.Value;
            bool shared = current.Get(hitch.Index);
            bool simulates = vehicles.SimulatesRoot(hitch.Root) || current.IsOnlyHere(hitch.Index);
            if (shared != hitch.Known)
            {
                // Someone else's game hitched or unhitched it.
                hitch.Known = shared;
                hitch.Pending = local != shared;
                hitch.ApplyAt = now;
                hitch.Tries = 0;
            }
            else if (local != hitch.WasHitched && !hitch.Pending)
            {
                if (simulates || !local)
                {
                    // This game hitched it (on the machine whose physics it is) or a player here let it go.
                    current.SetLocal(hitch.Index, local);
                    hitch.Known = local;
                    log.Info(LogCategory.Fsm, "The " + hitch.Label + " was " + (local ? "hitched" : "let go of") + " here: the others are told.");
                }
                else
                {
                    // Hitched only because the copies came near: the simulating machine's word stands.
                    hitch.Pending = true;
                    hitch.ApplyAt = now + StraySeconds;
                }
            }

            hitch.WasHitched = local;
            if (hitch.Pending)
            {
                if (local == shared)
                {
                    hitch.Pending = false;
                }
                else if (now >= hitch.ApplyAt)
                {
                    Show(hitch, shared, now);
                    hitch.WasHitched = hitch.Hitched.Value;
                }
            }

            Hang(hitch, simulates && hitch.Hitched.Value);
        }

        // Takes the game to its own state that hitches or unhitches.
        private void Show(Hitch hitch, bool hitched, double now)
        {
            if (hitched && (hitch.TowedPoint.position - hitch.Machine.transform.position).sqrMagnitude > HitchReach * HitchReach)
            {
                // The game's joint takes where the two are as its rest: hitched with the copy still on its way, it would hold the
                // trailer there should this machine run them later. Hitched once the copy is at the hitch.
                hitch.ApplyAt = now + NearLookSeconds;
                return;
            }

            hitch.ApplyAt = now + RetrySeconds;
            hitch.Tries++;
            PlayMakerFSM machine = hitched ? hitch.Machine : hitch.Release;
            string state = hitched ? hitch.Definition.Attach : hitch.Definition.Release;

            // Unity's null check is also true for a destroyed object.
            if (machine == null || !machine.enabled || !machine.gameObject.activeInHierarchy)
            {
                // The release is switched off while nothing is hitched; there is nothing to let go of then.
                if (!hitched && !hitch.Hitched.Value)
                {
                    hitch.Pending = false;
                }

                return;
            }

            try
            {
                string eventName = EventPrefix + state;
                if (!DoorSync.HasGlobalTransition(machine, eventName))
                {
                    machine.AddGlobalTransition(eventName, state);
                    if (machine == hitch.Machine)
                    {
                        // MSCLoader rebuilds a machine's data when a transition is added.
                        hitch.Hitched = machine.FsmVariables.FindFsmBool(hitch.Definition.Variable) ?? hitch.Hitched;
                    }
                }

                machine.SendEvent(eventName);
            }
            catch (Exception exception)
            {
                hitch.Broken = true;
                log.Warning(LogCategory.Fsm, "Could not " + (hitched ? "hitch" : "let go of") + " the " + hitch.Label + ": " + exception.Message + " It is not shared any more in this session.");
                return;
            }

            if (hitch.Hitched.Value == hitched)
            {
                hitch.Pending = false;
                log.Info(LogCategory.Fsm, "The " + hitch.Label + " was " + (hitched ? "hitched" : "let go of") + " by someone else: done here too (state now '" + hitch.Machine.ActiveStateName + "').");
            }
            else if (hitch.Tries >= 4)
            {
                hitch.Broken = true;
                log.Warning(
                    LogCategory.Fsm,
                    "The " + hitch.Label + " does not " + (hitched ? "hitch" : "let go") + " when told to (state '" + hitch.Machine.ActiveStateName + "'). It is not shared any more in this session.");
            }
        }

        // While hitched on the machine that runs the towing vehicle, what is towed is held there.
        private void Hang(Hitch hitch, bool hung)
        {
            ObjectReplication replication = manager.Vehicles;
            ushort id;

            // Unity's null check is also true for a destroyed object.
            if (replication == null || hitch.Towed == null || !vehicles.IdOf(hitch.Towed, out id))
            {
                hitch.Hung = false;
                return;
            }

            SharedBody body = replication.Find(id);
            if (body == null || (hung == hitch.Hung && !hung))
            {
                hitch.Hung = hung && body != null;
                return;
            }

            replication.SetHung(body, hung);
            if (hung != hitch.Hung)
            {
                log.Info(LogCategory.Vehicle, "'" + hitch.Towed.name + "' is " + (hung ? "hitched to '" + hitch.Root.name + "', whose physics runs here: it is simulated here with it." : "no longer held here by the " + hitch.Label + "."));
            }

            hitch.Hung = hung;
        }

        private sealed class Hitch
        {
            public VehicleHitch Definition { get; set; }

            /// <summary>The towing vehicle's root, and what it tows.</summary>
            public Transform Root { get; set; }

            public Transform Towed { get; set; }

            public PlayMakerFSM Machine { get; set; }

            public FsmBool Hitched { get; set; }

            /// <summary>The point of the towed vehicle that meets the hitch.</summary>
            public Transform TowedPoint { get; set; }

            public PlayMakerFSM Release { get; set; }

            public int Index { get; set; }

            public string Label { get; set; }

            /// <summary>What the session said the last time this machine looked, and what this game had.</summary>
            public bool Known { get; set; }

            public bool WasHitched { get; set; }

            /// <summary>The game here is to be taken to what the session says; when, and how often it was tried.</summary>
            public bool Pending { get; set; }

            public double ApplyAt { get; set; }

            public int Tries { get; set; }

            /// <summary>Whether what it tows is held by this machine for it.</summary>
            public bool Hung { get; set; }

            public bool Broken { get; set; }
        }
    }
}
