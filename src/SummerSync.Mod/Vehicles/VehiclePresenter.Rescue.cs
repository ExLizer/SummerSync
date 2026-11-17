using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The game's own rescue of a vehicle fallen under the map, written down when it happens: the Satsuma's "Setup", with the
    /// player more than 200 m away and the car below y = -10, puts it back where the scene had it before the save loaded,
    /// the garage at home (data/vehicles, "rescues").
    ///
    /// Why (docs/GAME_NOTES.md, "El Satsuma que vuelve a la casa", 2026-11-17): a car handed over far away and thrown by its
    /// parts' joints fell under the map in the game that ran it, and that game put it in the home garage; the others
    /// followed, and the host saved it there, without the parts it had lost on the way. The log said neither that it was the
    /// game nor where the car had been.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const int RescuesLogged = 10;

        // The machines hooked in this world (by instance), and the presenter the hooks report to.
        private static readonly HashSet<int> RescueHooked = new HashSet<int>();
        private static VehiclePresenter rescueListener;
        private int rescuesSaid;

        // Once, when the vehicle is found: its rescue states hooked, the mod's action first in each.
        private void HookRescues(Tracked vehicle)
        {
            rescueListener = this;
            string[] rescues = vehicle.Definition.Rescues;
            for (int i = 0; rescues != null && i < rescues.Length; i++)
            {
                string[] parts;
                if (!VehicleDefinition.TrySplitRescue(rescues[i], out parts))
                {
                    continue;
                }

                Transform at = parts[0].Length == 0 ? vehicle.Root : vehicle.Root.Find(parts[0]);
                PlayMakerFSM machine = FindMachine(at, parts[1]);
                if (machine == null)
                {
                    log.Warning(LogCategory.Vehicle, "Rescue: '" + vehicle.Root.name + "' has no machine '" + rescues[i] + "' in this game; its rescue is not watched.");
                    continue;
                }

                int id = machine.GetInstanceID();
                if (RescueHooked.Contains(id))
                {
                    continue;
                }

                try
                {
                    if (machine.Fsm == null || !machine.Fsm.Initialized)
                    {
                        machine.InitializeFSM();
                    }

                    string state = parts[2];
                    string root = vehicle.Root.name;
                    if (machine.FsmInject(state, () => Rescued(root, machine), false, 0, false))
                    {
                        RescueHooked.Add(id);
                    }
                    else
                    {
                        log.Warning(LogCategory.Vehicle, "Rescue: '" + rescues[i] + "' of '" + root + "' has no such state in this game; its rescue is not watched.");
                    }
                }
                catch (Exception exception)
                {
                    log.Warning(LogCategory.Vehicle, "Rescue: '" + rescues[i] + "' of '" + vehicle.Root.name + "' could not be watched: " + exception.Message);
                }
            }
        }

        private static PlayMakerFSM FindMachine(Transform at, string name)
        {
            // Unity's null check is also true for a destroyed object.
            if (at == null)
            {
                return null;
            }

            PlayMakerFSM[] machines = at.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }

        // The game entered a rescue state: the vehicle is still where it fell (the hook runs first).
        private static void Rescued(string root, PlayMakerFSM machine)
        {
            VehiclePresenter listener = rescueListener;
            if (listener == null)
            {
                return;
            }

            try
            {
                listener.SayRescued(root, machine);
            }
            catch (Exception exception)
            {
                listener.log.Warning(LogCategory.Vehicle, "Rescue: the line about '" + root + "' failed: " + exception.Message);
            }
        }

        private void SayRescued(string root, PlayMakerFSM machine)
        {
            if (rescuesSaid >= RescuesLogged)
            {
                return;
            }

            Tracked vehicle = null;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Root != null && tracked[i].Root.name == root)
                {
                    vehicle = tracked[i];
                    break;
                }
            }

            Transform car = vehicle != null ? vehicle.Root : machine.transform;
            Vector3 at = car.position;
            FsmVector3 start = machine.FsmVariables.FindFsmVector3("StartPos");
            Rigidbody body = vehicle != null ? vehicle.Body : car.GetComponent<Rigidbody>();
            string runs;
            if (vehicle == null || !vehicle.Shared.HasId)
            {
                runs = "no session shares it";
            }
            else if (vehicle.Shared.Role == BodyRole.Follow)
            {
                runs = "this machine follows it (player " + vehicle.Shared.Owner + " runs it)";
            }
            else
            {
                runs = "this machine runs it (" + (vehicle == driven ? "the local player drives it" : vehicle.Shared.Held ? "held here" : "parked")
                    + "; vehicles handed over to this machine this session: " + handOvers + ")";
            }

            rescuesSaid++;
            log.Warning(
                LogCategory.Vehicle,
                "Rescue: the game puts '" + root + "' back where the scene had it" + (start == null ? string.Empty : " (" + Point(start.Value) + ")")
                + ": it was at " + Point(at) + ", " + Metres(player == null ? 0f : (at - player.position).magnitude) + " from this player, at "
                + (body == null ? 0f : body.velocity.magnitude).ToString("0.0", CultureInfo.InvariantCulture) + " m/s; " + runs
                + (rescuesSaid == RescuesLogged ? " (no more of these lines this session)." : "."));
        }

        private static string Point(Vector3 at)
        {
            return "(" + at.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " + at.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + at.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }
    }
}
