using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Players;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The passengers' seatbelts, and every belt worn shown in every game.
    ///
    /// What is assumed about the game (dump of 2026-10-15, level file "level2" read on 2026-10-16, docs/GAME_NOTES.md "El
    /// cinturón"): the van's, the Gifu's and the Satsuma's driver's belt is a container with the belt's meshes, a
    /// "HandleUpPivot" on the pillar holding the "SeatbeltHandle" (and the straps, skinned to it), a "Fastened_mesh" and a
    /// "SeatbeltLock". The handle's "Use" shows "TAKE SEATBELT", hangs the handle from the view while the left button is
    /// held, lets it go back ("seatbelt_retract") further than 1.5 m from its pivot, and fastens when let go over the
    /// lock ("FASTEN SEATBELT"): the fastened mesh shows and the handle's pivot hides, with "seatbelt_fasten". The lock
    /// then offers "UNFASTEN SEATBELT" ("seatbelt_unfasten"). The Ferndale's is a lap belt: a click on "BuckleUp" ("BUCKLE
    /// UP LAPBELT", "UNBUCKLE LAP BELT", "harness_lock") swaps its meshes "Open" and "Close"; it has a mesh "Passenger" too.
    /// Either way the global "PlayerSeatbeltsOn" says whether the driver's is on.
    ///
    /// No game has a passenger's belt, so the mod makes one: the meshes of the driver's, mirrored across the vehicle, with
    /// nothing of the game's machines in them, and the same gesture done by the mod. Whether a passenger's belt is on is
    /// in their state; so is a driver's, and the machines that do not run that driver's game show the driver's own belt
    /// fastened while it is.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const string TakeBelt = "TAKE SEATBELT";
        private const string FastenBelt = "FASTEN SEATBELT";
        private const string UnfastenBelt = "UNFASTEN SEATBELT";
        private const string BuckleBelt = "BUCKLE UP LAPBELT";
        private const string UnbuckleBelt = "UNBUCKLE LAP BELT";
        private const string BeltSounds = "CarFoley";
        private const float BeltReach = 1f;
        private const float BeltLetGo = 1.5f;
        private const float BeltPickRadius = 0.07f;

        private readonly List<MirroredBelt> belts = new List<MirroredBelt>();
        private MirroredBelt dragging;
        private bool beltPrompting;
        private FsmBool beltUseIcon;
        private FsmBool driverBelted;

        private sealed class MirroredBelt
        {
            public Tracked Vehicle;
            public int Seat;
            public SeatBelt Kind;
            public GameObject Copy;

            // A belt with a handle: what moves and what shows.
            public Transform Pivot;
            public Transform Handle;
            public Vector3 HandleRest;
            public Quaternion HandleTurn;
            public GameObject Fastened;
            public Transform Lock;

            // A lap belt: the vehicle's own mesh of the passenger's belt, and the mod's fastened one.
            public GameObject Unbuckled;
            public Transform Buckle;

            public bool Shown;
        }

        // Once per frame, after the seat: the belt the local passenger works, and every belt as the states say.
        private void UpdateBelts()
        {
            if (beltUseIcon == null)
            {
                beltUseIcon = FsmVariables.GlobalVariables.FindFsmBool("GUIuse");
                driverBelted = FsmVariables.GlobalVariables.FindFsmBool("PlayerSeatbeltsOn");
            }

            for (int i = belts.Count - 1; i >= 0; i--)
            {
                // Unity's null check is also true for a destroyed object.
                if (belts[i].Vehicle.Body == null || belts[i].Copy == null || !tracked.Contains(belts[i].Vehicle))
                {
                    DropBelt(i);
                }
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                if (vehicle.Body == null || !vehicle.Shared.HasId)
                {
                    continue;
                }

                VehicleSeat[] seats = vehicle.Definition.Seats;
                for (int s = 0; s < seats.Length; s++)
                {
                    if (seats[s].Belt == SeatBelt.None)
                    {
                        continue;
                    }

                    MirroredBelt belt = BeltOf(vehicle, s);
                    if (belt == null)
                    {
                        continue;
                    }

                    bool on = seatedIn == vehicle && seatIndex == s ? seatBelted : RemoteBelted(vehicle, s + 1);
                    if (on != belt.Shown)
                    {
                        ShowBelt(belt, on);
                    }
                }

                vehicle.ShowDriverBelt(driven != vehicle && RemoteDriverBelted(vehicle));
            }

            WorkBelt();
        }

        // The mirrored belt of that seat, made the first time; null if the driver's to copy is not there.
        private MirroredBelt BeltOf(Tracked vehicle, int seat)
        {
            for (int i = 0; i < belts.Count; i++)
            {
                if (belts[i].Vehicle == vehicle && belts[i].Seat == seat)
                {
                    return belts[i];
                }
            }

            VehicleSeat place = vehicle.Definition.Seats[seat];
            Transform original = vehicle.Root.Find(place.BeltPath);
            if (original == null || vehicle.BeltMissing)
            {
                if (!vehicle.BeltMissing)
                {
                    vehicle.BeltMissing = true;
                    log.Warning(LogCategory.Passenger, "The driver's belt of '" + vehicle.Root.name + "' is not at '" + place.BeltPath + "': its passengers have none.");
                }

                return null;
            }

            var belt = new MirroredBelt { Vehicle = vehicle, Seat = seat, Kind = place.Belt };
            if (place.Belt == SeatBelt.Handle)
            {
                belt.Copy = MirrorCopy(vehicle.Root, original, original.name + " (SummerSync passenger)");
                belt.Pivot = belt.Copy.transform.Find("HandleUpPivot");
                belt.Handle = belt.Pivot == null ? null : belt.Pivot.Find("SeatbeltHandle");
                Transform fastened = belt.Copy.transform.Find("Fastened_mesh");
                belt.Fastened = fastened == null ? null : fastened.gameObject;
                belt.Lock = belt.Copy.transform.Find("SeatbeltLock");
                if (belt.Handle == null || belt.Fastened == null || belt.Lock == null)
                {
                    UnityEngine.Object.Destroy(belt.Copy);
                    vehicle.BeltMissing = true;
                    log.Warning(LogCategory.Passenger, "The driver's belt of '" + vehicle.Root.name + "' is not made as expected (handle, fastened mesh, lock): its passengers have none.");
                    return null;
                }

                belt.HandleRest = belt.Handle.localPosition;
                belt.HandleTurn = belt.Handle.localRotation;
                belt.Fastened.SetActive(false);
                belt.Pivot.gameObject.SetActive(true);
            }
            else
            {
                // The lap belt: the driver's closed one, mirrored, and a point mirrored from the driver's buckle.
                Transform closed = original.Find("Close");
                Transform buckle = original.Find("BuckleUp");
                Transform passenger = original.Find("Passenger");
                if (closed == null || buckle == null)
                {
                    vehicle.BeltMissing = true;
                    log.Warning(LogCategory.Passenger, "The lap belt of '" + vehicle.Root.name + "' is not made as expected (Close, BuckleUp): its passengers have none.");
                    return null;
                }

                belt.Copy = MirrorCopy(vehicle.Root, closed, "Close (SummerSync passenger)");
                belt.Copy.SetActive(false);
                belt.Fastened = belt.Copy;
                belt.Unbuckled = passenger == null ? null : passenger.gameObject;
                var point = new GameObject("BuckleUp (SummerSync passenger)");
                point.transform.parent = belt.Copy.transform.parent;
                Vector3 at = vehicle.Root.InverseTransformPoint(buckle.position);
                point.transform.position = vehicle.Root.TransformPoint(new Vector3(-at.x, at.y, at.z));
                belt.Buckle = point.transform;
            }

            belts.Add(belt);
            log.Info(
                LogCategory.Passenger,
                "The " + place.Name + " seat of '" + vehicle.Root.name + "' has a belt: the driver's '" + place.BeltPath + "', mirrored ("
                + (place.Belt == SeatBelt.Handle ? "a handle, taken to the lock" : "a lap belt, a click") + ").");
            return belt;
        }

        private void ShowBelt(MirroredBelt belt, bool on)
        {
            belt.Shown = on;
            if (belt.Kind == SeatBelt.Handle)
            {
                belt.Fastened.SetActive(on);
                belt.Pivot.gameObject.SetActive(!on);
                if (dragging != belt)
                {
                    RestHandle(belt);
                }
            }
            else
            {
                belt.Fastened.SetActive(on);
                if (belt.Unbuckled != null)
                {
                    belt.Unbuckled.SetActive(!on);
                }
            }
        }

        private static void RestHandle(MirroredBelt belt)
        {
            belt.Handle.parent = belt.Pivot;
            belt.Handle.localPosition = belt.HandleRest;
            belt.Handle.localRotation = belt.HandleTurn;
        }

        private bool RemoteBelted(Tracked vehicle, int seat)
        {
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                PlayerPose pose = remotes[i].Shown;
                if (remotes[i].InWorld && pose.Parent == vehicle.Shared.Id && !pose.Driving && pose.Seat == seat)
                {
                    return pose.Belted;
                }
            }

            return false;
        }

        private bool RemoteDriverBelted(Tracked vehicle)
        {
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                PlayerPose pose = remotes[i].Shown;
                if (remotes[i].InWorld && pose.Parent == vehicle.Shared.Id && pose.Driving)
                {
                    return pose.Belted;
                }
            }

            return false;
        }

        // The local passenger's hands on their belt: the game's gesture, done by the mod.
        private void WorkBelt()
        {
            MirroredBelt belt = null;
            if (seatedIn != null)
            {
                for (int i = 0; i < belts.Count && belt == null; i++)
                {
                    if (belts[i].Vehicle == seatedIn && belts[i].Seat == seatIndex)
                    {
                        belt = belts[i];
                    }
                }
            }

            Camera view = Camera.main;
            if (belt == null || view == null)
            {
                if (dragging != null)
                {
                    LetGoOfBelt(false);
                }

                StopBeltPrompt();
                return;
            }

            Transform eye = view.transform;
            if (belt.Kind == SeatBelt.Buckle)
            {
                if (Aimed(eye, belt.Buckle.position, BeltPickRadius * 2f))
                {
                    BeltPrompt(seatBelted ? UnbuckleBelt : BuckleBelt);
                    if (Input.GetMouseButtonDown(0))
                    {
                        seatBelted = !seatBelted;
                        ShowBelt(belt, seatBelted);
                        Sound(belt.Buckle, "harness_lock");
                        log.Info(LogCategory.Passenger, "The local passenger " + (seatBelted ? "buckled up" : "unbuckled") + " the lap belt.");
                    }
                }
                else
                {
                    StopBeltPrompt();
                }

                return;
            }

            if (dragging == belt)
            {
                bool overLock = Aimed(eye, belt.Lock.position, BeltPickRadius * 1.5f);
                if (overLock)
                {
                    BeltPrompt(FastenBelt);
                }
                else
                {
                    StopBeltPrompt();
                }

                if ((belt.Handle.position - belt.Pivot.position).magnitude > BeltLetGo)
                {
                    LetGoOfBelt(false);
                }
                else if (!Input.GetMouseButton(0))
                {
                    LetGoOfBelt(overLock);
                }

                return;
            }

            if (seatBelted)
            {
                if (Aimed(eye, belt.Lock.position, BeltPickRadius * 1.5f))
                {
                    BeltPrompt(UnfastenBelt);
                    if (Input.GetMouseButtonDown(0))
                    {
                        seatBelted = false;
                        ShowBelt(belt, false);
                        Sound(belt.Lock, "seatbelt_unfasten");
                        log.Info(LogCategory.Passenger, "The local passenger unfastened their belt.");
                    }
                }
                else
                {
                    StopBeltPrompt();
                }

                return;
            }

            if (Aimed(eye, belt.Handle.position, BeltPickRadius))
            {
                BeltPrompt(TakeBelt);
                if (Input.GetMouseButtonDown(0))
                {
                    dragging = belt;

                    // As the game's: the handle hangs from the view while the button is held.
                    belt.Handle.parent = eye;
                }
            }
            else
            {
                StopBeltPrompt();
            }
        }

        // The handle let go: fastened if it was over the lock, rolled back to the pillar if not.
        private void LetGoOfBelt(bool fasten)
        {
            MirroredBelt belt = dragging;
            dragging = null;

            // Unity's null check is also true for a destroyed object.
            if (belt.Handle == null)
            {
                return;
            }

            RestHandle(belt);
            if (fasten && seatedIn == belt.Vehicle && seatIndex == belt.Seat)
            {
                seatBelted = true;
                ShowBelt(belt, true);
                Sound(belt.Lock, "seatbelt_fasten");
                log.Info(LogCategory.Passenger, "The local passenger fastened their belt.");
            }
            else
            {
                Sound(belt.Pivot, "seatbelt_retract");
            }

            StopBeltPrompt();
        }

        private static bool Aimed(Transform eye, Vector3 point, float radius)
        {
            Vector3 to = point - eye.position;
            float along = Vector3.Dot(to, eye.forward);
            if (along < 0f || along > BeltReach)
            {
                return false;
            }

            return (to - (eye.forward * along)).sqrMagnitude <= radius * radius;
        }

        private void BeltPrompt(string text)
        {
            beltPrompting = true;
            if (seatInteraction != null)
            {
                seatInteraction.Value = text;
            }

            if (beltUseIcon != null)
            {
                beltUseIcon.Value = true;
            }
        }

        private void StopBeltPrompt()
        {
            if (!beltPrompting)
            {
                return;
            }

            beltPrompting = false;
            if (seatInteraction != null && (seatInteraction.Value == TakeBelt || seatInteraction.Value == FastenBelt || seatInteraction.Value == UnfastenBelt
                || seatInteraction.Value == BuckleBelt || seatInteraction.Value == UnbuckleBelt))
            {
                seatInteraction.Value = string.Empty;
            }

            if (beltUseIcon != null)
            {
                beltUseIcon.Value = false;
            }
        }

        private void Sound(Transform at, string variation)
        {
            try
            {
                MasterAudio.PlaySound3DFollowTransformAndForget(BeltSounds, at, 1f, 1f, 0f, variation);
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Passenger, "The belt's sound '" + BeltSounds + "/" + variation + "' could not be played: " + exception.Message);
            }
        }

        private void DropBelt(int index)
        {
            MirroredBelt belt = belts[index];
            belts.RemoveAt(index);
            if (dragging == belt)
            {
                dragging = null;
            }

            // Unity's null check is also true for a destroyed object.
            if (belt.Unbuckled != null)
            {
                belt.Unbuckled.SetActive(true);
            }

            if (belt.Buckle != null)
            {
                UnityEngine.Object.Destroy(belt.Buckle.gameObject);
            }

            if (belt.Copy != null)
            {
                UnityEngine.Object.Destroy(belt.Copy);
            }
        }

        private void DropBelts()
        {
            StopBeltPrompt();
            for (int i = belts.Count - 1; i >= 0; i--)
            {
                DropBelt(i);
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Body != null)
                {
                    tracked[i].ShowDriverBelt(false);
                }
            }
        }

        /// <summary>
        /// A copy of what is drawn of <paramref name="original"/> and below it, as the mirror image of it across the vehicle
        /// (its x to -x): the same meshes and materials, skinned ones on the copy's own bones, and nothing else of the game's
        /// (no machine, no collider), so that the copy does nothing by itself. It hangs where the original does.
        /// </summary>
        private static GameObject MirrorCopy(Transform vehicle, Transform original, string name)
        {
            var map = new Dictionary<Transform, Transform>();
            GameObject copy = CopyShape(original, null, map);
            copy.name = name;
            CopySkins(original, map);

            // In the vehicle's own coordinates, a reflection across x = 0: the place's x and the turn about y and z change
            // sign, and so does the size along x, which mirrors everything below it.
            Transform made = copy.transform;
            made.parent = vehicle;
            Vector3 place = vehicle.InverseTransformPoint(original.position);
            Quaternion turn = Quaternion.Inverse(vehicle.rotation) * original.rotation;
            Vector3 size = original.lossyScale;
            made.localPosition = new Vector3(-place.x, place.y, place.z);
            made.localRotation = new Quaternion(turn.x, -turn.y, -turn.z, turn.w);
            made.localScale = new Vector3(-size.x, size.y, size.z);
            if (original.parent != null && original.parent != vehicle)
            {
                // Under the same parent, so that it is switched off with the vehicle's details far from the player.
                made.SetParent(original.parent, true);
            }

            return copy;
        }

        private static GameObject CopyShape(Transform original, Transform parent, Dictionary<Transform, Transform> map)
        {
            var made = new GameObject(original.name);
            made.layer = original.gameObject.layer;
            made.SetActive(original.gameObject.activeSelf);
            Transform at = made.transform;
            if (parent != null)
            {
                at.parent = parent;
                at.localPosition = original.localPosition;
                at.localRotation = original.localRotation;
                at.localScale = original.localScale;
            }

            map[original] = at;
            MeshFilter filter = original.GetComponent<MeshFilter>();
            MeshRenderer drawn = original.GetComponent<MeshRenderer>();
            if (filter != null && drawn != null)
            {
                made.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                MeshRenderer copy = made.AddComponent<MeshRenderer>();
                copy.sharedMaterials = drawn.sharedMaterials;
                copy.shadowCastingMode = drawn.shadowCastingMode;
                copy.receiveShadows = drawn.receiveShadows;
            }

            for (int i = 0; i < original.childCount; i++)
            {
                CopyShape(original.GetChild(i), at, map);
            }

            return made;
        }

        // The skinned meshes last, once every bone has its copy.
        private static void CopySkins(Transform original, Dictionary<Transform, Transform> map)
        {
            SkinnedMeshRenderer skin = original.GetComponent<SkinnedMeshRenderer>();
            Transform made;
            if (skin != null && map.TryGetValue(original, out made))
            {
                SkinnedMeshRenderer copy = made.gameObject.AddComponent<SkinnedMeshRenderer>();
                Transform[] bones = skin.bones;
                var copied = new Transform[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    Transform bone;
                    copied[i] = bones[i] != null && map.TryGetValue(bones[i], out bone) ? bone : bones[i];
                }

                Transform root;
                copy.sharedMesh = skin.sharedMesh;
                copy.bones = copied;
                copy.rootBone = skin.rootBone != null && map.TryGetValue(skin.rootBone, out root) ? root : skin.rootBone;
                copy.sharedMaterials = skin.sharedMaterials;
                copy.localBounds = skin.localBounds;
                copy.updateWhenOffscreen = true;
            }

            for (int i = 0; i < original.childCount; i++)
            {
                CopySkins(original.GetChild(i), map);
            }
        }
    }
}
