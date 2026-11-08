using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// Kills the local player in a crash, the way the game kills a driver whose head broke off.
    ///
    /// What is assumed about the game (dump of 2026-10-15, docs/GAME_NOTES.md "Morir al volante"): every vehicle the
    /// player drives has a "DeadBody" under its root, a body sitting where the driver sits with its parts switched off,
    /// held by a FixedJoint, with a "CameraPivot" below it. The head's state machine "Death" sets the global
    /// "VehicleDeadBody" to that "CameraPivot", switches the dead body on with everything under it, takes it out of the
    /// vehicle, sends "DEATH" to the camera's "Death" (which marks the crash in "Systems/Death" and starts it) and to the
    /// windscreen, and a tenth of a second later takes the FixedJoint away.
    ///
    /// For a passenger the mod does the same with a copy of that dead body, moved from the driver's place to the
    /// passenger's. The windscreen is left alone: it is shared, and the machine that runs the vehicle breaks it in the
    /// crash itself if the crash is hard enough.
    /// </summary>
    internal static class CrashDeath
    {
        private const string DeadBodyName = "DeadBody";
        private const string DeadViewName = "CameraPivot";
        private const string DeadViewVariable = "VehicleDeadBody";
        private const string CameraPath = "Pivot/AnimPivot/Camera/FPSCamera/FPSCamera";
        private const string DriverViewPath = "DriverHeadPivot/CameraPivot/Pivot";
        private const float LetGoAfter = 0.1f;

        /// <summary>The copy of a dead body made for the last crash here, to take away if the player comes back.</summary>
        public static GameObject LastBody { get; set; }

        /// <summary>
        /// The local player dies in <paramref name="vehicle"/>, their eyes at <paramref name="eyes"/> in its coordinates.
        /// False if the game's pieces for it were not found (said in the log).
        /// </summary>
        public static bool Kill(Transform vehicle, Vector3 eyes, Transform player, Logger log)
        {
            Transform dead = vehicle.Find(DeadBodyName);
            Transform camera = player == null ? null : player.Find(CameraPath);
            PlayMakerFSM death = camera == null ? null : Machine(camera.gameObject, "Death");
            FsmGameObject deadView = FsmVariables.GlobalVariables.FindFsmGameObject(DeadViewVariable);
            if (dead == null || death == null || deadView == null)
            {
                log.Error(
                    LogCategory.Passenger,
                    "A crash should kill the local player in '" + vehicle.name + "', but the game's pieces for it were not found: dead body "
                    + (dead == null ? "MISSING" : "found") + ", the camera's 'Death' " + (death == null ? "MISSING" : "found") + ", global '" + DeadViewVariable + "' "
                    + (deadView == null ? "MISSING" : "found") + ". The player lives on.");
                return false;
            }

            // From where the driver sits to where this player sits: the driver's view hangs from the head pivot.
            Transform driverView = FindBelow(vehicle, DriverViewPath);
            Vector3 shift = driverView == null ? Vector3.zero : eyes - vehicle.InverseTransformPoint(driverView.position);
            GameObject body = Object.Instantiate(dead.gameObject);
            LastBody = body;
            body.name = DeadBodyName + " (SummerSync passenger)";
            body.transform.parent = vehicle;
            body.transform.localPosition = vehicle.InverseTransformPoint(dead.position) + shift;
            body.transform.localRotation = Quaternion.Inverse(vehicle.rotation) * dead.rotation;
            Transform view = body.transform.Find(DeadViewName);
            deadView.Value = view != null ? view.gameObject : body;
            SwitchOn(body.transform);
            body.transform.parent = null;
            FixedJoint hold = body.GetComponent<FixedJoint>();
            if (hold != null)
            {
                Object.Destroy(hold, LetGoAfter);
            }

            death.SendEvent("DEATH");
            log.Info(
                LogCategory.Passenger,
                "The local player died in a crash in '" + vehicle.name + "': a copy of its dead body sits " + shift.magnitude.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " m from the driver's place, and the camera's 'Death' was sent DEATH" + (driverView == null ? " (the driver's view was not found: the body sits where the driver's does)" : string.Empty) + ".");
            return true;
        }

        private static void SwitchOn(Transform at)
        {
            at.gameObject.SetActive(true);
            for (int i = 0; i < at.childCount; i++)
            {
                SwitchOn(at.GetChild(i));
            }
        }

        private static PlayMakerFSM Machine(GameObject on, string name)
        {
            PlayMakerFSM[] machines = on.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }

        // A path below the root, wherever in it (most vehicles have it under "LOD", the Satsuma right under the root).
        private static Transform FindBelow(Transform root, string path)
        {
            Transform found = root.Find(path);
            for (int i = 0; found == null && i < root.childCount; i++)
            {
                found = root.GetChild(i).Find(path);
            }

            return found;
        }
    }
}
