using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The Satsuma's short circuit and its fire: every game shows them as the game that runs the car has them.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "El cortocircuito y el fuego del Satsuma"):
    /// - "SATSUMA(557kg, 248)/Wiring :: Status" looks about once a second: with the battery in and charged over 90, its plus
    ///   terminal not bolted, its minus terminal bolted, the ground wire and the battery harness made, it switches
    ///   "Wiring/FireElectric" on; otherwise it sets "Off" in that object's machine "Init".
    /// - "FireElectric :: Init" starts when the object is switched on: it puts the object at a wire picked at random,
    ///   "Sparks" (child "Sparks" on, the battery drained) for 8 to 15 s, "Fire" (a part destroyed, "Sparks" off, "FireSmall"
    ///   and "FireTrigger" on), "Spread" ("SATSUMA(557kg, 248)/FIRE" switched on) and "Off" (the object off). With "Off" set
    ///   while in "Sparks" it goes straight to "Off".
    /// - "FireElectric/FireTrigger :: PutOut" and "FIRE/FireTrigger :: PutOut" go through "Fire off" when the fire is put
    ///   out (the extinguisher, water, or the event PUTOUT) and switch their fire off in "Reset". The car's leaves "FIRE"
    ///   itself switched on, with "FireSmall", "InnerCore", the light and the trigger off.
    /// - "FIRE :: Init", which starts when "FIRE" is switched on, switches "FIRE/InnerCore" off and "FIRE/FireSmall", the
    ///   light and the trigger on; the big fire is "InnerCore" switched on, some seconds later.
    ///
    /// "Init" decides by chance, so the mod keeps it switched off in every game but the one that runs the car
    /// (data/satsuma/values.json, "electric fire"). There "Status" still switches "FireElectric" on, with its sparks, and
    /// nothing ever switches it off; and "FIRE" is never switched on. So the game that runs the car says what it has, and
    /// the others show that: the sparks, the small fire and where they are, and the car's fire, small or big. A fire put
    /// out in another game is put out in the game that runs the car, which says so to everyone.
    ///
    /// Offline and in the loopback test nothing here touches the game.
    /// </summary>
    internal sealed class CarFireSync
    {
        private const string Key = "car/fire";
        private const string OutKey = "car/fire/out";
        private const string CarPath = "/SATSUMA(557kg, 248)";
        private const string PutOutMachine = "PutOut";
        private const string PutOutState = "Fire off";
        private const string PutOutEvent = "PUTOUT";
        private const double SearchInterval = 3.0;

        private const int Short = 0;
        private const int X = 1;
        private const int Y = 2;
        private const int Z = 3;
        private const int Fire = 4;
        private const int Writer = 5;
        private const int Count = 6;

        private const int OutShort = 1;
        private const int OutFire = 2;

        // The game's machines that carry a hook of this class, by instance: a hook is put in once and calls whichever
        // instance is current.
        private static readonly HashSet<int> Hooked = new HashSet<int>();
        private static CarFireSync live;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private TallyReplication current;
        private double nextSearch;
        private bool found;
        private bool owned;
        private bool first;
        private int[] known;
        private int[] said;
        private int outCount;
        private int outWanted;

        private GameObject shortRoot;
        private GameObject sparks;
        private GameObject shortFire;
        private GameObject shortTrigger;
        private PlayMakerFSM shortPutOut;
        private GameObject fire;
        private GameObject flames;
        private GameObject core;
        private PlayMakerFSM firePutOut;

        // Set inside the game's own machines when a fire is put out in this game.
        private bool shortPutOutHere;
        private bool firePutOutHere;

        public CarFireSync(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
            live = this;
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

                found = false;
                said = null;
                known = null;
                outWanted = 0;
                nextSearch = now + 1.0;
            }

            AssemblyReplication assembly = manager.Assembly;
            if (current == null || assembly == null)
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

                first = true;
            }

            // Unity's null check is also true for a destroyed object.
            if (shortRoot == null || fire == null)
            {
                found = false;
                return;
            }

            bool owns = assembly.OwnsValues;
            if (first || owns != owned)
            {
                if (!first && !owns && known != null)
                {
                    // Until the machine that runs the car now says what it has: as it was here.
                    said = known;
                }

                first = false;
                owned = owns;
                known = null;
                if (owns && !shortRoot.activeSelf && sparks.transform.parent == shortRoot.transform)
                {
                    // As the game has them while there is no short circuit: the sparks of a wire made with the battery on
                    // are these, hung from somewhere else for a moment.
                    sparks.SetActive(true);
                }
            }

            if (owns)
            {
                Tell();
            }
            else
            {
                Show();
            }

            shortPutOutHere = false;
            firePutOutHere = false;
        }

        // In the game that runs the car: what it has is said when it changes, and a fire put out elsewhere is put out here.
        private void Tell()
        {
            if (outWanted != 0)
            {
                PlayMakerFSM machine = outWanted == OutShort ? shortPutOut : firePutOut;
                bool burning = outWanted == OutShort ? shortRoot.activeSelf && shortFire.activeSelf : Burning();
                if (burning && machine != null && machine.gameObject.activeInHierarchy)
                {
                    machine.SendEvent(PutOutEvent);
                    log.Info(LogCategory.Vehicle, "Car fire: " + (outWanted == OutShort ? "the wiring's fire" : "the car's fire") + " was put out in another game; put out here, where the car runs.");
                }

                outWanted = 0;
            }

            int[] now = Read();
            if (known != null && Same(now, known))
            {
                return;
            }

            bool news = known == null || now[Short] != known[Short] || now[Fire] != known[Fire];
            known = now;
            current.Set(Key, (int[])now.Clone());
            if (news)
            {
                log.Info(LogCategory.Vehicle, "Car fire: here, where the car runs, " + Describe(now) + "; told.");
            }
        }

        // In every other game: the fire is shown as said, and one put out here is said.
        private void Show()
        {
            outWanted = 0;
            int[] want = said;
            int wantShort = want == null ? 0 : want[Short];
            int wantFire = want == null ? 0 : want[Fire];

            if (shortPutOutHere && wantShort == 2)
            {
                SayOut(OutShort);
                want[Short] = wantShort = 0;
            }

            if (firePutOutHere && wantFire > 0)
            {
                SayOut(OutFire);
                want[Fire] = wantFire = 0;
            }

            bool home = sparks.transform.parent == shortRoot.transform;
            if (!home)
            {
                // A wire made with the battery on: the game shows these sparks somewhere else for a moment.
                Switch(sparks, true);
            }
            else
            {
                Switch(sparks, wantShort == 1);
            }

            Switch(shortFire, wantShort == 2);
            Switch(shortTrigger, wantShort == 2);
            if (wantShort > 0)
            {
                var at = new Vector3(want[X] / 1000f, want[Y] / 1000f, want[Z] / 1000f);
                if ((shortRoot.transform.localPosition - at).sqrMagnitude > 1e-8f)
                {
                    shortRoot.transform.localPosition = at;
                }
            }

            Switch(shortRoot, wantShort > 0);

            if (wantFire == 0)
            {
                Switch(fire, false);
            }
            else
            {
                if (!Burning())
                {
                    // Switched on, its own machine lights the small fire; one put out here is still switched on.
                    Switch(fire, false);
                    fire.SetActive(true);
                }

                Switch(core, wantFire == 2);
            }

            int[] now = { wantShort, 0, 0, 0, wantFire, 0 };
            if (known == null || now[Short] != known[Short] || now[Fire] != known[Fire])
            {
                bool news = known != null || wantShort != 0 || wantFire != 0;
                known = now;
                if (news)
                {
                    log.Info(LogCategory.Vehicle, "Car fire: shown here as in the game that runs the car: " + Describe(now) + ".");
                }
            }
        }

        private void SayOut(int what)
        {
            outCount++;
            current.Set(OutKey, new[] { what, outCount, LocalId() });
            log.Info(LogCategory.Vehicle, "Car fire: " + (what == OutShort ? "the wiring's fire" : "the car's fire") + " was put out here; the game that runs the car is told.");
        }

        private int[] Read()
        {
            var values = new int[Count];
            if (shortRoot.activeSelf)
            {
                values[Short] = shortFire.activeSelf ? 2 : sparks.activeSelf && sparks.transform.parent == shortRoot.transform ? 1 : 0;
            }

            if (values[Short] != 0)
            {
                Vector3 at = shortRoot.transform.localPosition;
                values[X] = Mathf.RoundToInt(at.x * 1000f);
                values[Y] = Mathf.RoundToInt(at.y * 1000f);
                values[Z] = Mathf.RoundToInt(at.z * 1000f);
            }

            // Put out, the small fire stays a second more ("Fire off"): out already.
            bool goingOut = firePutOut != null && firePutOut.gameObject.activeInHierarchy && firePutOut.ActiveStateName == PutOutState;
            values[Fire] = !Burning() || goingOut ? 0 : core.activeSelf ? 2 : 1;
            values[Writer] = LocalId();
            return values;
        }

        private bool Burning()
        {
            return fire.activeSelf && (flames.activeSelf || core.activeSelf);
        }

        private void Search()
        {
            GameObject car = GameObject.Find(CarPath);
            Transform wiring = car == null ? null : car.transform.Find("Wiring/FireElectric");
            Transform burning = car == null ? null : car.transform.Find("FIRE");
            if (wiring == null || burning == null)
            {
                return;
            }

            Transform sparksAt = wiring.Find("Sparks");
            Transform smallAt = wiring.Find("FireSmall");
            Transform triggerAt = wiring.Find("FireTrigger");
            Transform coreAt = burning.Find("InnerCore");
            Transform flamesAt = burning.Find("FireSmall");
            Transform fireTriggerAt = burning.Find("FireTrigger");
            if (sparksAt == null || smallAt == null || triggerAt == null || coreAt == null || flamesAt == null)
            {
                // The sparks hang elsewhere for half a second after a wire made with the battery on: looked for again.
                return;
            }

            shortRoot = wiring.gameObject;
            sparks = sparksAt.gameObject;
            shortFire = smallAt.gameObject;
            shortTrigger = triggerAt.gameObject;
            fire = burning.gameObject;
            core = coreAt.gameObject;
            flames = flamesAt.gameObject;
            shortPutOut = DoorSync.FindMachine(triggerAt, PutOutMachine);
            firePutOut = fireTriggerAt == null ? null : DoorSync.FindMachine(fireTriggerAt, PutOutMachine);
            bool hooked = Hook(shortPutOut, true) & Hook(firePutOut, false);
            found = true;
            log.Info(
                LogCategory.Vehicle,
                "Car fire: the Satsuma's short circuit and fire are shared" + (hooked ? "." : "; a fire put out in a game that does not run the car is not told (no '" + PutOutState + "' in its machine)."));
        }

        private bool Hook(PlayMakerFSM machine, bool wiring)
        {
            if (machine == null || !DoorSync.HasState(machine, PutOutState))
            {
                return false;
            }

            if (Hooked.Contains(machine.GetInstanceID()))
            {
                return true;
            }

            try
            {
                if (!machine.FsmInject(PutOutState, () => OnPutOut(wiring), false, 0, false))
                {
                    return false;
                }

                Hooked.Add(machine.GetInstanceID());
                return true;
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Vehicle, "Car fire: '" + PutOutState + "' could not take the mod's action: " + exception.Message);
                return false;
            }
        }

        private static void OnPutOut(bool wiring)
        {
            CarFireSync sync = live;
            if (sync == null)
            {
                return;
            }

            if (wiring)
            {
                sync.shortPutOutHere = true;
            }
            else
            {
                sync.firePutOutHere = true;
            }
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key == Key && values.Length >= Count)
            {
                if (values[Writer] != LocalId())
                {
                    said = (int[])values.Clone();
                }
            }
            else if (key == OutKey && values.Length >= 3 && !current.CatchingUp && values[2] != LocalId())
            {
                outWanted = values[0];
            }
        }

        private int LocalId()
        {
            NetSession session = manager.Session;
            return session == null ? 0 : session.LocalPlayerId;
        }

        private static void Switch(GameObject target, bool on)
        {
            if (target.activeSelf != on)
            {
                target.SetActive(on);
            }
        }

        private static bool Same(int[] a, int[] b)
        {
            for (int i = 0; i < Count; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string Describe(int[] values)
        {
            string wiring = values[Short] == 1 ? "the wiring sparks" : values[Short] == 2 ? "the wiring burns" : "no short circuit";
            string car = values[Fire] == 1 ? "the car burns (small fire)" : values[Fire] == 2 ? "the car burns (big fire)" : "no fire";
            return wiring + ", " + car;
        }
    }
}
