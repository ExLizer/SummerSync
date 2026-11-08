using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// Sounds a vehicle's horn on every machine while someone sounds it on one.
    ///
    /// What is assumed about the game, from the dump of 2026-10-15 and the level file (docs/GAME_NOTES.md): a horn is an
    /// object, switched off, with a looping sound that plays when it is switched on ("CarHorn"). The horn button's state
    /// machine switches it on in one state while the player holds the mouse on the button, and off again as it leaves that
    /// state. The Ruscko's faulty wiring also switches it on by itself while the car leans over, until the button is used.
    /// Which object and which buttons are in the vehicle's data file ("horn").
    ///
    /// To the session a horn is one switch after the knobs, on while it sounds. A press and a release of this machine's
    /// player are told by this machine; while the local player still holds the button and someone else let go, it is told
    /// again. What the game does to the horn by itself is told by the machine that simulates the vehicle. Shown for someone
    /// else, the object is switched on and off by the mod, as the button's state does.
    /// </summary>
    internal sealed class HornSync
    {
        // A button on a part that is not installed is looked for again this often (the Satsuma has thousands of objects).
        private const double LookEvery = 2.0;

        private readonly Logger log;
        private readonly VehiclePresenter vehicles;
        private readonly List<Horn> horns = new List<Horn>();

        public HornSync(Logger log, VehiclePresenter vehicles)
        {
            this.log = log;
            this.vehicles = vehicles;
        }

        /// <summary>Forgets the horns, and silences any this machine was sounding for someone else.</summary>
        public void Clear()
        {
            for (int i = 0; i < horns.Count; i++)
            {
                Horn horn = horns[i];

                // Unity's null check is also true for a destroyed object.
                if (horn.Shown && horn.Sound != null && horn.Sound.activeSelf)
                {
                    horn.Sound.SetActive(false);
                }
            }

            horns.Clear();
        }

        /// <summary>
        /// Looks for the horns of the vehicles that are in the scene and appends the switch that says whether each sounds to
        /// the names and states of the session's list.
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
                Transform root = definition.Horn == null ? null : VehiclePresenter.FindRoot(definition.RootName);
                if (root == null)
                {
                    continue;
                }

                // Listed whatever this machine's copy is like, so that the list is the same everywhere.
                Transform sound = root.Find(definition.Horn.Path);
                var horn = new Horn
                {
                    Definition = definition.Horn,
                    Root = root,
                    Sound = sound == null ? null : sound.gameObject,
                    Index = names.Count,
                    Label = DoorSync.ShortName(definition.RootName) + " horn",
                    Buttons = new PlayMakerFSM[definition.Horn.Buttons.Length],
                    NextLook = new double[definition.Horn.Buttons.Length],
                };
                horn.WasSounding = horn.Sound != null && horn.Sound.activeSelf;
                names.Add(definition.RootName + "/" + definition.Horn.Path + "#horn");
                states.Add(horn.Sound != null && horn.Sound.activeSelf);
                horns.Add(horn);
                if (horn.Sound == null)
                {
                    log.Warning(LogCategory.Fsm, "The horn of '" + definition.RootName + "' is not at '" + definition.Horn.Path + "' as expected: it is not shared.");
                    continue;
                }

                int buttons = 0;
                for (int b = 0; b < horn.Buttons.Length; b++)
                {
                    horn.Buttons[b] = FindButton(root, definition.Horn.Buttons[b]);
                    buttons += horn.Buttons[b] == null ? 0 : 1;
                }

                found.Append(found.Length == 0 ? string.Empty : ", ").Append(horn.Label)
                    .Append(" (").Append(buttons).Append(" of ").Append(horn.Buttons.Length).Append(" button(s) here)");
            }

            log.Info(LogCategory.Fsm, "Horns shared: " + (found.Length == 0 ? "none" : found.ToString()) + ".");
        }

        /// <summary>Once per frame, after the session's switches were updated.</summary>
        public void Watch(SwitchReplication current, double now)
        {
            for (int i = 0; i < horns.Count; i++)
            {
                Horn horn = horns[i];

                // Unity's null check is also true for a destroyed object.
                if (horn.Sound == null || horn.Root == null)
                {
                    continue;
                }

                Watch(horn, current, now);
            }
        }

        private void Watch(Horn horn, SwitchReplication current, double now)
        {
            bool pressed = Pressed(horn, now);
            bool sounding = horn.Sound.activeSelf;
            bool shared = current.Get(horn.Index);
            if (pressed != horn.WasPressed)
            {
                // The local player pressed the button or let go of it.
                horn.WasPressed = pressed;
                horn.Shown = false;
                if (shared != pressed)
                {
                    current.SetLocal(horn.Index, pressed);
                    shared = pressed;
                }

                if (pressed)
                {
                    log.Info(LogCategory.Fsm, "The " + horn.Label + " is sounded here: the others are told.");
                }
            }
            else if (pressed && !shared)
            {
                // Someone else let go while the local player still holds it.
                current.SetLocal(horn.Index, true);
                shared = true;
            }
            else if (!pressed && sounding != horn.WasSounding && sounding != shared && (vehicles.SimulatesRoot(horn.Root) || current.IsOnlyHere(horn.Index)))
            {
                // The game sounded or silenced it by itself (the Ruscko's wiring as the car leans): the machine that runs the
                // vehicle's physics says.
                current.SetLocal(horn.Index, sounding);
                shared = sounding;
                log.Info(LogCategory.Fsm, "The " + horn.Label + " " + (sounding ? "sounds" : "went quiet") + " by itself on this machine, which simulates the vehicle: the others are told.");
            }

            if (shared && !sounding)
            {
                horn.Sound.SetActive(true);
                horn.Shown = true;
            }
            else if (!shared && sounding && !pressed)
            {
                horn.Sound.SetActive(false);
                horn.Shown = false;
            }

            // What the game does to it from here on, without the button, is the game's own doing.
            horn.WasSounding = horn.Sound.activeSelf;
        }

        // Whether this machine's player holds one of the horn's buttons. A button on a part that is not installed is looked
        // for now and then.
        private bool Pressed(Horn horn, double now)
        {
            bool pressed = false;
            for (int b = 0; b < horn.Buttons.Length; b++)
            {
                VehicleHornButton described = horn.Definition.Buttons[b];
                PlayMakerFSM machine = horn.Buttons[b];

                // Unity's null check is also true for a destroyed object.
                if (machine == null || (described.Under != null && !machine.transform.IsChildOf(horn.Root)))
                {
                    horn.Buttons[b] = null;
                    if (described.Under == null || now < horn.NextLook[b])
                    {
                        continue;
                    }

                    horn.NextLook[b] = now + LookEvery;
                    machine = FindButton(horn.Root, described);
                    horn.Buttons[b] = machine;
                    if (machine == null)
                    {
                        continue;
                    }
                }

                pressed |= machine.enabled && machine.gameObject.activeInHierarchy && machine.ActiveStateName == described.Pressed;
            }

            return pressed;
        }

        private static PlayMakerFSM FindButton(Transform root, VehicleHornButton described)
        {
            Transform holder = described.Under == null ? root : PartFinder.Below(root, described.Under);
            Transform at = holder == null ? null : described.Path == null ? holder : holder.Find(described.Path);
            PlayMakerFSM machine = at == null ? null : DoorSync.FindMachine(at, described.Machine);
            return machine != null && DoorSync.HasState(machine, described.Pressed) ? machine : null;
        }

        private sealed class Horn
        {
            public VehicleHorn Definition { get; set; }

            public Transform Root { get; set; }

            /// <summary>The object that sounds while it is switched on.</summary>
            public GameObject Sound { get; set; }

            /// <summary>The buttons' machines, by the data file's order; null while not found.</summary>
            public PlayMakerFSM[] Buttons { get; set; }

            public double[] NextLook { get; set; }

            /// <summary>Its switch in the session's list.</summary>
            public int Index { get; set; }

            public string Label { get; set; }

            /// <summary>Whether this machine's player held a button the last time it looked.</summary>
            public bool WasPressed { get; set; }

            /// <summary>Whether it sounded when this machine last looked, after showing what the session says.</summary>
            public bool WasSounding { get; set; }

            /// <summary>Whether the mod switched it on here for someone else.</summary>
            public bool Shown { get; set; }
        }
    }
}
