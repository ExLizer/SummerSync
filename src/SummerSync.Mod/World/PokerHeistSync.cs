using System;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The video poker torn out of the pub and broken open, the same in every game.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 and the game with the bridge (docs/GAME_NOTES.md "La
    /// máquina de póker arrancada"): the machine ("STORE/LOD/VideoPoker") is a body held to the world by a joint. A towing
    /// rope tied to its slot ("HookSlot :: Logic") makes that joint give at 40000 N; pulled off by a car, its machine
    /// "Data" sees the joint gone and, once the machine is two metres off, breaks it loose ("Break off": no parent, the
    /// name "machine", a thing the hand can take), switches its game off, and has Teimo charge for it ("Break"). Its money
    /// ("Data" :: "Cash") is taken out of it with clicks on its open front, each a sum drawn at random that goes to the
    /// player's money.
    ///
    /// Where it breaks loose is decided where the rope pulls: the machine that simulates the car (<see cref="RopeSync"/>),
    /// whose game alone has the joint give, and goes on its own way, Teimo's charge included. That game tells (the tally
    /// "poker/broken"), and the others break it loose by the game's state that does it, past the charge: the money is one,
    /// and it is charged once. Broken loose it is a loose object like any other, shared by its name. What is left inside is
    /// one number for everyone already (<see cref="PokerSync"/>): a click takes from it in the game of whoever clicks, and the
    /// money goes to that game's player, which is everyone's.
    /// </summary>
    internal sealed class PokerHeistSync
    {
        private const string MachinePath = "STORE/LOD/VideoPoker";
        private const string StoreRoot = "STORE";
        private const string MachineBelowStore = "LOD/VideoPoker";
        private const string DataMachine = "Data";
        private const string CashVariable = "Cash";
        private const string BreakOffState = "Break off";
        private const string BrokenTally = "poker/broken";
        private const string OpenTally = "poker/open";
        private const string LockPath = "Disabled/Money/Lock";
        private const string LockMachine = "Use";
        private const string OpenState = "State 1";
        private const string OpenEvent = "SUMMERSYNC POKER OPEN";

        // The lock's states while its hatch opens and stays open (dump of the game with the bridge, 2026-10-17).
        private static readonly string[] OpenStates = { "State 1", "State 2" };
        private const string BreakEvent = "SUMMERSYNC POKER BREAK OFF";
        private const double SearchEvery = 5.0;

        // What the game sets the joint to hold while a rope is tied to the slot (the slot's "BreakForce").
        private const float RopedHold = 40000f;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly RopeSync ropes;
        private TallyReplication current;
        private bool found;
        private double nextSearch;
        private Transform machine;
        private Rigidbody body;
        private PlayMakerFSM data;
        private FsmInt cash;
        private PlayMakerFSM lockMachine;
        private bool wasOpen;
        private bool heardOpen;
        private Transform slot;
        private FixedJoint hold;
        private float holdBefore;
        private bool holdChanged;
        private bool wasBroken;
        private bool heardBroken;

        public PokerHeistSync(Logger log, NetworkManager manager, RopeSync ropes)
        {
            this.log = log;
            this.manager = manager;
            this.ropes = ropes;
        }

        /// <summary>Once per frame. <paramref name="localPlayer"/> is the player's root object, or null while there is no game world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            TallyReplication tallies = localPlayer == null ? null : manager.Tallies;
            if (tallies != current)
            {
                Release();
                current = tallies;
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
                return;
            }

            // Unity's null check is also true for a destroyed object.
            if (machine == null || data == null || cash == null)
            {
                return;
            }

            bool broken = machine.parent == null;
            if (broken != wasBroken)
            {
                wasBroken = broken;
                if (broken && !heardBroken)
                {
                    // Torn out in this game, where the rope pulls: its own charge and all. The others are told.
                    current.Set(BrokenTally, new[] { 1 });
                    log.Info(LogCategory.Vehicle, "Video poker: torn out of the pub in this game, where the rope pulls it; the others are told.");
                }
            }

            // A machine that is switched off does not hear its events: torn out once the pub is near again.
            if (!broken && heardBroken && data.enabled && data.gameObject.activeInHierarchy)
            {
                BreakOff();
            }

            Hold(broken);
            WatchLock();
        }

        // Opened with the wrench in one game, it opens in all, where there is money left: the hatch, and the coins to take.
        private void WatchLock()
        {
            // Unity's null check is also true for a destroyed object.
            if (lockMachine == null || !lockMachine.enabled || !lockMachine.gameObject.activeInHierarchy)
            {
                return;
            }

            bool open = Array.IndexOf(OpenStates, lockMachine.ActiveStateName) >= 0;
            if (open && !wasOpen && !heardOpen)
            {
                heardOpen = true;
                current.Set(OpenTally, new[] { 1 });
                log.Info(LogCategory.Vehicle, "Video poker: opened here; the others are told.");
            }
            else if (heardOpen && !open && !wasOpen && cash.Value > 0 && lockMachine.ActiveStateName != "State 3")
            {
                try
                {
                    if (!DoorSync.HasGlobalTransition(lockMachine, OpenEvent))
                    {
                        lockMachine.AddGlobalTransition(OpenEvent, OpenState);
                    }

                    lockMachine.SendEvent(OpenEvent);
                    open = Array.IndexOf(OpenStates, lockMachine.ActiveStateName) >= 0;
                    log.Info(LogCategory.Vehicle, "Video poker: opened in another game; opened here too (state now '" + lockMachine.ActiveStateName + "').");
                }
                catch (Exception exception)
                {
                    log.Warning(LogCategory.Vehicle, "Video poker: could not open it as another game did: " + exception.Message);
                    lockMachine = null;
                }
            }

            wasOpen = open;
        }

        /// <summary>Lets go of the session; the machine stays as it is.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            current = null;
            RestoreHold();
            found = false;
            heardBroken = false;
            heardOpen = false;
        }

        private void Find()
        {
            found = true;
            // Far from the pub its part of the store is switched off, and the machine with it: looked for below the store.
            GameObject store = GameObject.Find(StoreRoot);
            machine = store == null ? null : store.transform.Find(MachineBelowStore);
            if (machine == null)
            {
                // Already broken loose when the world was loaded: it lies at the root by its new name.
                GameObject loose = GameObject.Find("machine");
                machine = loose == null ? null : loose.transform;
            }

            data = machine == null ? null : DoorSync.FindMachine(machine, DataMachine);
            cash = data == null ? null : data.FsmVariables.FindFsmInt(CashVariable);
            body = machine == null ? null : machine.GetComponent<Rigidbody>();
            slot = machine == null ? null : machine.Find("HookSlot");
            Transform lockObject = machine == null ? null : machine.Find(LockPath);
            lockMachine = lockObject == null ? null : DoorSync.FindMachine(lockObject, LockMachine);
            if (cash == null || body == null || !DoorSync.HasState(data, BreakOffState))
            {
                log.Warning(LogCategory.Vehicle, "Video poker: the machine is not at '" + MachinePath + "' as this build expects (its body, machine '" + DataMachine + "' and money): tearing it out is each game's own.");
                data = null;
                return;
            }

            wasBroken = machine.parent == null;

            log.Info(LogCategory.Vehicle, "Video poker: " + (wasBroken ? "already torn out" : "in the pub") + ", " + cash.Value.ToString(CultureInfo.InvariantCulture) + " mk inside.");
        }

        // While a rope is tied to the slot, the joint gives at what the game sets only where the rope pulls: elsewhere it holds
        // as it did, and the machine waits to be told.
        private void Hold(bool broken)
        {
            FixedJoint joint = broken || body == null ? null : body.GetComponent<FixedJoint>();
            if (joint != hold)
            {
                RestoreHold();
                hold = joint;
                holdChanged = false;
            }

            // Unity's null check is also true for a destroyed object.
            if (hold == null || slot == null)
            {
                return;
            }

            bool pullsHere;
            bool tied = ropes.Tied(slot, out pullsHere);
            if (!tied)
            {
                RestoreHold();
                return;
            }

            if (!holdChanged)
            {
                holdChanged = true;
                holdBefore = hold.breakForce;
            }

            float wanted = pullsHere ? RopedHold : float.PositiveInfinity;
            if (hold.breakForce != wanted)
            {
                hold.breakForce = wanted;
                hold.breakTorque = wanted;
            }
        }

        private void RestoreHold()
        {
            // Unity's null check is also true for a destroyed object.
            if (holdChanged && hold != null)
            {
                hold.breakForce = holdBefore;
                hold.breakTorque = holdBefore;
            }

            holdChanged = false;
        }

        // Another game tore it out: torn out here by the game's state for it, past the charge, which was that game's.
        private void BreakOff()
        {
            try
            {
                if (!DoorSync.HasGlobalTransition(data, BreakEvent))
                {
                    data.AddGlobalTransition(BreakEvent, BreakOffState);

                    // MSCLoader rebuilds a machine's data when a transition is added.
                    cash = data.FsmVariables.FindFsmInt(CashVariable) ?? cash;
                }

                data.SendEvent(BreakEvent);
                FixedJoint joint = body.GetComponent<FixedJoint>();

                // Unity's null check is also true for a destroyed object.
                if (joint != null)
                {
                    UnityEngine.Object.Destroy(joint);
                }

                hold = null;
                holdChanged = false;
                wasBroken = machine.parent == null;
                log.Info(LogCategory.Vehicle, "Video poker: torn out in another game; torn out here too (state now '" + data.ActiveStateName + "').");
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Vehicle, "Video poker: could not tear it out as another game did: " + exception.Message);
                heardBroken = false;
            }
        }

        private void OnStateWanted(string name, int[] values)
        {
            if (values.Length < 1)
            {
                return;
            }

            if (name == BrokenTally)
            {
                heardBroken = values[0] != 0;
            }
            else if (name == OpenTally)
            {
                heardOpen = values[0] != 0;
            }
        }
    }
}
