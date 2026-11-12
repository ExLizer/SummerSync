using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The shop's window and the pub's: broken in one game, broken in every game.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Las ventanas de la tienda y del pub"): each window is a group
    /// ("STORE/LOD/GFX_Store/BreakableWindows", "STORE/LOD/GFX_Pub/BreakableWindowsPub") with a pane
    /// ("BreakableWindow", "BreakableWindowPub") held by a FixedJoint. The pane's machine "Break" waits in "State 1" for
    /// the joint to be gone and goes to "Shatter": the whole glass off, the pieces on, and "WindowStoreBroken" (or
    /// "WindowPubBroken") of "STORE :: Fuelprices" set, which the save keeps. From there it looks at how far the player
    /// is: near, Teimo shouts and charges the glass, or the break-in starts; far, "State 4" waits and the group is
    /// destroyed. A save with the window broken loads without the group.
    ///
    /// The game where it breaks goes on as the game has it, with whatever follows for its player. The others take the
    /// joint away, which is what breaks it in the game, and their machine is taken from "Shatter" to "State 4": the
    /// same look, and nothing happens to a player who did not break it. Where the shop is not drawn (the player is far)
    /// the group is destroyed at once.
    ///
    /// Offline and in the loopback test nothing here touches the game.
    /// </summary>
    internal sealed class WindowSync
    {
        private const string StorePath = "STORE";
        private const string FlagMachine = "Fuelprices";
        private const string BreakMachine = "Break";
        private const string ShatterState = "Shatter";
        private const string QuietState = "State 4";
        private const string EventPrefix = "SUMMERSYNC WINDOW ";
        private const double SearchInterval = 2.0;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly Pane[] panes =
        {
            new Pane("window/store", "the shop's window", "LOD/GFX_Store/BreakableWindows", "BreakableWindow", "WindowStoreBroken"),
            new Pane("window/pub", "the pub's window", "LOD/GFX_Pub/BreakableWindowsPub", "BreakableWindowPub", "WindowPubBroken"),
        };

        private TallyReplication current;
        private double nextSearch;
        private bool found;

        public WindowSync(Logger log, NetworkManager manager)
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
                }

                current = tallies;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }

                for (int i = 0; i < panes.Length; i++)
                {
                    panes[i].Forget();
                }

                found = false;
                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            if (!found)
            {
                if (now < nextSearch)
                {
                    return;
                }

                nextSearch = now + SearchInterval;
                Search();
                if (!found)
                {
                    return;
                }
            }

            for (int i = 0; i < panes.Length; i++)
            {
                Pane pane = panes[i];
                if (pane.Wanted && !pane.Broken)
                {
                    BreakHere(pane);
                }

                Watch(pane);
            }
        }

        private void Search()
        {
            GameObject store = GameObject.Find(StorePath);
            if (store == null)
            {
                return;
            }

            PlayMakerFSM flags = DoorSync.FindMachine(store.transform, FlagMachine);
            int whole = 0;
            for (int i = 0; i < panes.Length; i++)
            {
                Pane pane = panes[i];
                pane.Flag = flags == null ? null : flags.FsmVariables.FindFsmBool(pane.FlagName);
                Transform group = store.transform.Find(pane.GroupPath);
                Transform glass = group == null ? null : group.Find(pane.GlassName);
                pane.Group = group == null ? null : group.gameObject;
                pane.Joint = glass == null ? null : glass.GetComponent<FixedJoint>();
                pane.Machine = glass == null ? null : DoorSync.FindMachine(glass, BreakMachine);

                // A save with the window broken loads without the group.
                pane.Broken = pane.Joint == null;
                if (!pane.Broken)
                {
                    whole++;
                }
            }

            found = true;
            log.Info(LogCategory.Fsm, "Windows: the shop's and the pub's are shared; " + whole + " of " + panes.Length + " whole in this world.");
        }

        // This game's own window: the joint gone is the window broken.
        private void Watch(Pane pane)
        {
            // Unity's null check is also true for a destroyed object.
            if (!pane.Broken && (pane.Joint == null || pane.Group == null))
            {
                pane.Broken = true;
                current.Set(pane.Key, new[] { 1 });
                log.Info(LogCategory.Fsm, "Windows: " + pane.Name + " broke in this game; told.");
            }

            if (pane.ByAnother && pane.Machine != null && pane.Machine.ActiveStateName == ShatterState)
            {
                ToState(pane.Machine, QuietState);
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            for (int i = 0; i < panes.Length; i++)
            {
                if (key == panes[i].Key && values.Length > 0 && values[0] != 0)
                {
                    panes[i].Wanted = true;
                }
            }
        }

        private void BreakHere(Pane pane)
        {
            pane.Broken = true;
            pane.ByAnother = true;
            if (pane.Group == null)
            {
                return;
            }

            if (pane.Machine != null && pane.Machine.enabled && pane.Machine.gameObject.activeInHierarchy && pane.Joint != null)
            {
                // The game's own way: its machine sees the joint gone and shatters the glass.
                Object.Destroy(pane.Joint);
                log.Info(LogCategory.Fsm, "Windows: " + pane.Name + " broke in another game; broken here, in sight.");
                return;
            }

            if (pane.Flag != null)
            {
                pane.Flag.Value = true;
            }

            Object.Destroy(pane.Group);
            log.Info(LogCategory.Fsm, "Windows: " + pane.Name + " broke in another game; gone here (the shop is not drawn now).");
        }

        // Takes a machine to one of its states through an event of the mod's.
        private static void ToState(PlayMakerFSM machine, string state)
        {
            if (!DoorSync.HasState(machine, state))
            {
                return;
            }

            string eventName = EventPrefix + state;
            if (!DoorSync.HasGlobalTransition(machine, eventName))
            {
                machine.AddGlobalTransition(eventName, state);
            }

            machine.SendEvent(eventName);
        }

        private sealed class Pane
        {
            public readonly string Key;
            public readonly string Name;
            public readonly string GroupPath;
            public readonly string GlassName;
            public readonly string FlagName;

            public GameObject Group;
            public FixedJoint Joint;
            public PlayMakerFSM Machine;
            public FsmBool Flag;

            /// <summary>Broken in this game, by its own player or after another's.</summary>
            public bool Broken;

            /// <summary>Another game said it broke; done here once the shop is found.</summary>
            public bool Wanted;

            /// <summary>Broken here because it broke in another game: nothing follows for this player.</summary>
            public bool ByAnother;

            public Pane(string key, string name, string groupPath, string glassName, string flagName)
            {
                Key = key;
                Name = name;
                GroupPath = groupPath;
                GlassName = glassName;
                FlagName = flagName;
            }

            public void Forget()
            {
                Group = null;
                Joint = null;
                Machine = null;
                Flag = null;
                Broken = false;
                Wanted = false;
                ByAnother = false;
            }
        }
    }
}
