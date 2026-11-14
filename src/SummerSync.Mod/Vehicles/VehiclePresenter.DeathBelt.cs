using System;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The driver's belt of the vehicle the local player died at the wheel of, undone the game's way once they are back.
    ///
    /// Why (seen on 2026-11-14: a player who died belted could neither undo the belt nor get out until the game started again;
    /// measured with the bridge the same day): the game loads the world again after every death, and
    /// so never undoes a belt. Back at home in a session, the van's belt stayed fastened with nobody in it ("SeatbeltLock ::
    /// Use" in "Wait player 2", its handle's "Locked" true, "PlayerSeatbeltsOn" true). Getting out reads the handle's
    /// "Locked" ("PlayerTrigger", state "Seatbelt"): fastened, it keeps the driver in. And once the vehicle's "LOD" is
    /// switched off and on (the player woke up far from it), the lock starts again offering to fasten, the handle stays
    /// hidden, and the belt can neither be undone nor taken.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const string UnfastenState = "State 2";
        private const string UnfastenEvent = "SUMMERSYNC UNFASTEN";
        private const string BuckleClickState = "Position";
        private const string BuckleClickEvent = "SUMMERSYNC UNBUCKLE";
        private const double UnbuckleEvery = 0.5;

        private double nextUnbuckle;

        // Once per frame, now and then: the belts left fastened by a death, undone once their vehicle is switched on here.
        private void UnbuckleAfterDeath(double now)
        {
            if (now < nextUnbuckle)
            {
                return;
            }

            nextUnbuckle = now + UnbuckleEvery;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];

                // Unity's null check is also true for a destroyed object.
                if (!vehicle.UnbucklePending || vehicle.Body == null || vehicle == driven)
                {
                    continue;
                }

                // Someone else at its wheel has their own belt shown on it here: it waits until they are out.
                if (vehicle.Shared.Held)
                {
                    continue;
                }

                string done = vehicle.TryUnbuckle();
                if (done != null)
                {
                    vehicle.UnbucklePending = false;
                    log.Info(LogCategory.Vehicle, "Deaths: the driver's belt of '" + vehicle.Root.name + "', fastened when the local player died at its wheel, " + done);
                }
            }
        }

        private sealed partial class Tracked
        {
            private bool unbuckleHooked;

            /// <summary>Whether its driver's belt is to be undone: the local player died at its wheel.</summary>
            public bool UnbucklePending { get; set; }

            /// <summary>
            /// Undoes its driver's belt the game's way if it is fastened: a handle belt's lock to its "unfasten" state (the
            /// sound, "PlayerSeatbeltsOn" and the head's belt force to nothing, the handle back to its pillar), a lap belt
            /// clicked open. Null while its belt is switched off here (far from this game's player); otherwise what was done.
            /// </summary>
            public string TryUnbuckle()
            {
                VehicleSeat[] seats = Definition.Seats;
                for (int s = 0; s < seats.Length; s++)
                {
                    if (seats[s].BeltPath == null || seats[s].Belt == SeatBelt.None)
                    {
                        continue;
                    }

                    Transform belt = Root.Find(seats[s].BeltPath);
                    if (belt == null)
                    {
                        // A Satsuma without its belt installed, or a path not there: nothing to undo.
                        return "has none to undo.";
                    }

                    if (!belt.gameObject.activeInHierarchy)
                    {
                        return null;
                    }

                    return seats[s].Belt == SeatBelt.Handle ? UnfastenHandle(belt) : UnbuckleLap(belt);
                }

                return "has none to undo.";
            }

            private string UnfastenHandle(Transform belt)
            {
                Transform handle = belt.Find("HandleUpPivot/SeatbeltHandle");
                Transform lockAt = belt.Find("SeatbeltLock");
                PlayMakerFSM handleUse = handle == null ? null : World.DoorSync.FindMachine(handle, "Use");
                FsmBool locked = handleUse == null ? null : handleUse.FsmVariables.FindFsmBool("Locked");
                PlayMakerFSM lockUse = lockAt == null ? null : World.DoorSync.FindMachine(lockAt, "Use");
                if (locked == null || lockUse == null)
                {
                    return "is not made as expected (handle, lock): left as it is.";
                }

                if (!locked.Value)
                {
                    return "was not fastened.";
                }

                // As the handle switches the lock on when it is taken to it.
                if (!lockAt.gameObject.activeSelf)
                {
                    lockAt.gameObject.SetActive(true);
                }

                if (!World.DoorSync.HasState(lockUse, UnfastenState))
                {
                    return "has no state '" + UnfastenState + "': left as it is.";
                }

                try
                {
                    if (!unbuckleHooked)
                    {
                        unbuckleHooked = true;
                        lockUse.AddGlobalTransition(UnfastenEvent, UnfastenState);
                    }

                    lockUse.SendEvent(UnfastenEvent);
                }
                catch (Exception exception)
                {
                    return "could not be undone: " + exception.Message;
                }

                return "is undone (its lock in '" + lockUse.ActiveStateName + "', handle locked " + (locked.Value ? "still" : "no more") + ").";
            }

            private string UnbuckleLap(Transform belt)
            {
                Transform buckle = belt.Find("BuckleUp");
                PlayMakerFSM use = buckle == null ? null : World.DoorSync.FindMachine(buckle, "Use");
                FsmBool buckled = use == null ? null : use.FsmVariables.FindFsmBool("IsBuckled");
                if (buckled == null)
                {
                    return "is not made as expected (no buckle): left as it is.";
                }

                if (!buckled.Value)
                {
                    return "was not buckled.";
                }

                if (!World.DoorSync.HasState(use, BuckleClickState))
                {
                    return "has no state '" + BuckleClickState + "': left as it is.";
                }

                try
                {
                    // The click: it flips the buckle open, with its sound.
                    if (!unbuckleHooked)
                    {
                        unbuckleHooked = true;
                        use.AddGlobalTransition(BuckleClickEvent, BuckleClickState);
                    }

                    use.SendEvent(BuckleClickEvent);
                }
                catch (Exception exception)
                {
                    return "could not be undone: " + exception.Message;
                }

                return "is unbuckled (buckled " + (buckled.Value ? "still" : "no more") + ").";
            }
        }
    }
}
