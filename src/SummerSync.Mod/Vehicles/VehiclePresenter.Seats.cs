using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Players;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// Passenger mode: the local player sits in a passenger seat the way the game seats a driver.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "El modo pasajero", dump of 2026-10-15 and the game with the
    /// bridge on 2026-10-16): driving mode is offered by "ENTER DRIVING MODE" (globals "GUIinteraction" and "GUIdrive")
    /// and taken with the button "DrivingMode" (cInput, as the game's GetButtonDown reads it). Seated, the game hangs
    /// "PLAYER" from the vehicle where it stands, sets "PlayerStop" (which switches its CharacterController off) and leaves
    /// the yaw as it was; the player's state machine "Crouch" holds the view 0.6 m above the player while its
    /// "PlayerInCar" is true. Getting out takes the same button, not with the belt on and not faster than 0.15 m/s along
    /// any axis. The game's own passenger seats (Kylajani's car, the bus) are the driver's seat without the controls.
    ///
    /// The mod does the same with the seats listed in the vehicle's file: it hangs the player from an object of its own
    /// at the seat (a kinematic body, as the game's driver hangs from one), keeps "PlayerStop" and "Crouch.PlayerInCar"
    /// on while seated and gives back what they were on getting out. The player's state says which seat, and whether the
    /// belt is on and a helmet worn, for the machine that runs the vehicle's physics (the passenger's head) and for the
    /// others. A seat someone else sits in is not offered.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const string SeatButton = "DrivingMode";
        private const string SeatOffer = "ENTER PASSENGER MODE";
        private const string SeatObjectName = "SummerSync passenger seat";

        // Getting out is refused faster than this along any axis, as the game refuses it to a driver.
        private const float SeatLeaveSpeed = 0.15f;

        // How far above a seat the player's root may be to be offered it: standing, it is higher. How far below is the
        // seat's own.
        private const float SeatAbove = 1.4f;

        private Tracked seatedIn;
        private Tracked diedDriving;
        private bool localDead;
        private int seatIndex = -1;
        private Transform seatObject;
        private double seatedAt;
        private bool seatBelted;
        private bool crouchWasInCar;
        private bool seatPrompting;
        private FsmBool playerStop;
        private FsmBool crouchInCar;
        private FsmBool helmetWorn;
        private FsmString seatInteraction;
        private FsmBool seatDriveIcon;

        /// <summary>
        /// Where the local player is: the vehicle they are in (0 on foot), their passenger seat from 1 (255 at the wheel, 0
        /// standing), and their place, in the vehicle's coordinates in one and in the world's on foot.
        /// </summary>
        public void WhereLocalPlayerIs(out ushort vehicle, out int seat, out Vector3 place)
        {
            vehicle = 0;
            seat = 0;
            place = player == null ? Vector3.zero : player.position;
            Tracked aboard = seatedIn ?? driven ?? ridden ?? stayedIn;
            if (aboard == null || aboard.Body == null || !aboard.Shared.HasId || player == null)
            {
                return;
            }

            vehicle = aboard.Shared.Id;
            seat = aboard == seatedIn ? seatIndex + 1 : aboard == driven ? World.DeathSync.DriverSeat : 0;
            place = aboard.Root.InverseTransformPoint(player.position);
        }

        /// <summary>
        /// After a death that the player comes back from in place (a session without permanent death): out of the passenger
        /// seat or the wheel they died in, and every vehicle's driver's head and dead body as they were if the game broke
        /// them (a driver who dies in a crash breaks the head's joint and lets the dead body go; the game alone loads the
        /// world again after a death, so it never puts them back). Returns what was done, for the log.
        /// </summary>
        public string AfterLocalDeath()
        {
            var done = new System.Text.StringBuilder();
            if (seatedIn != null)
            {
                LeaveSeat(true, "the player died");
                done.Append("out of the passenger seat; ");
            }

            // The game's death takes the player off the wheel without the driving mode knowing: out of it as the game lets
            // a driver out.
            Tracked wheel = driven ?? diedDriving;
            diedDriving = null;
            localDead = false;
            if (ridden != null)
            {
                StopRiding(false, "the player died");
                done.Append("off the vehicle they stood in; ");
            }

            stayedIn = null;

            // Unity's null check is also true for a destroyed object.
            if (wheel != null && wheel.Body != null)
            {
                wheel.Eject(false);
                done.Append("out of the driving seat of '").Append(wheel.Root.name).Append("'; ");
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (tracked[i].Body != null && tracked[i].RepairDeath())
                {
                    done.Append("the driver's head and dead body of '").Append(tracked[i].Root.name).Append("' put back; ");
                }
            }

            return done.Length == 0 ? "nothing of a vehicle to undo" : done.ToString().TrimEnd(' ', ';');
        }

        /// <summary>When the local player dies: the vehicle they drive, to take them out of it after (<see cref="AfterLocalDeath"/>).</summary>
        public void NoteLocalDeath()
        {
            diedDriving = driven;
            localDead = true;
        }

        /// <summary>Whether the local player sits in a passenger seat.</summary>
        public bool Seated
        {
            get { return seatedIn != null; }
        }

        // Once per frame, before the ride: gets the player into a seat they ask for and out of it.
        private void UpdateSeat()
        {
            if (seatedIn != null)
            {
                KeepSeated();
                return;
            }

            Tracked offeredIn;
            int offered = OfferedSeat(out offeredIn);
            if (offered < 0)
            {
                StopOffering();
                return;
            }

            Offer();
            if (ButtonDown())
            {
                StopOffering();
                Sit(offeredIn, offered, "the player asked for it");
            }
        }

        // The seat the local player stands at, free and there, or -1.
        private int OfferedSeat(out Tracked vehicle)
        {
            vehicle = null;

            // Unity's null check is also true for a destroyed object.
            if (player.parent != null || driven != null || !player.gameObject.activeInHierarchy || LookUpGlobals() == null || playerStop.Value)
            {
                return -1;
            }

            int best = -1;
            float nearest = float.MaxValue;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked candidate = tracked[i];
                if (candidate.Body == null || !candidate.Shared.HasId || candidate.Definition.Seats.Length == 0)
                {
                    continue;
                }

                // Standing at its wheel, the game offers the driver's seat: that one first.
                if (candidate.OffersDriving)
                {
                    return -1;
                }

                Vector3 local = candidate.Root.InverseTransformPoint(player.position);
                VehicleSeat[] seats = candidate.Definition.Seats;
                for (int s = 0; s < seats.Length; s++)
                {
                    float[] at = seats[s].Player;
                    float x = local.x - at[0];
                    float z = local.z - at[2];
                    float across = (x * x) + (z * z);
                    float up = local.y - at[1];
                    if (across > seats[s].Reach * seats[s].Reach || up < -seats[s].Below || up > SeatAbove || across >= nearest)
                    {
                        continue;
                    }

                    if (!SeatThere(candidate, s) || SeatTaken(candidate, s))
                    {
                        continue;
                    }

                    nearest = across;
                    best = s;
                    vehicle = candidate;
                }
            }

            return best;
        }

        // Whether the seat is in the vehicle: one that is a part (the Satsuma's) has to be on its pivot.
        private static bool SeatThere(Tracked vehicle, int seat)
        {
            string[] needs = vehicle.Definition.Seats[seat].Needs;
            if (needs.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < needs.Length; i++)
            {
                Transform pivot = vehicle.Root.Find(needs[i]);
                if (pivot == null)
                {
                    continue;
                }

                for (int c = 0; c < pivot.childCount; c++)
                {
                    if (pivot.GetChild(c).gameObject.activeSelf)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Whether another player sits there.
        private bool SeatTaken(Tracked vehicle, int seat)
        {
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                PlayerPose pose = remotes[i].Shown;
                if (remotes[i].InWorld && pose.Parent == vehicle.Shared.Id && !pose.Driving && pose.Seat == seat + 1)
                {
                    return true;
                }
            }

            return false;
        }

        private void Offer()
        {
            seatPrompting = true;
            if (seatInteraction != null)
            {
                seatInteraction.Value = SeatOffer;
            }

            if (seatDriveIcon != null)
            {
                seatDriveIcon.Value = true;
            }
        }

        private void StopOffering()
        {
            if (!seatPrompting)
            {
                return;
            }

            seatPrompting = false;
            if (seatInteraction != null && seatInteraction.Value == SeatOffer)
            {
                seatInteraction.Value = string.Empty;
            }

            if (seatDriveIcon != null)
            {
                seatDriveIcon.Value = false;
            }
        }

        private static bool ButtonDown()
        {
            try
            {
                return cInput.GetKeyDown(SeatButton);
            }
            catch (Exception)
            {
                // cInput not set up yet (the very first frames).
                return false;
            }
        }

        // The game's globals and the player's "Crouch", once found; null if something is missing.
        private FsmBool LookUpGlobals()
        {
            if (playerStop != null && crouchInCar != null)
            {
                return playerStop;
            }

            playerStop = FsmVariables.GlobalVariables.FindFsmBool("PlayerStop");
            helmetWorn = FsmVariables.GlobalVariables.FindFsmBool("PlayerHelmet");
            seatInteraction = FsmVariables.GlobalVariables.FindFsmString("GUIinteraction");
            seatDriveIcon = FsmVariables.GlobalVariables.FindFsmBool("GUIdrive");
            PlayMakerFSM[] machines = player.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == "Crouch")
                {
                    crouchInCar = machines[i].FsmVariables.FindFsmBool("PlayerInCar");
                }
            }

            return playerStop != null && crouchInCar != null ? playerStop : null;
        }

        // Hangs the local player from the seat, as the game hangs a driver from the wheel.
        private void Sit(Tracked vehicle, int seat, string why)
        {
            if (ridden != null)
            {
                StopRiding(false, "the player sat down");
            }

            stayedIn = null;
            StopOffering();
            VehicleSeat place = vehicle.Definition.Seats[seat];
            var made = new GameObject(SeatObjectName);
            seatObject = made.transform;
            seatObject.parent = vehicle.Root;
            seatObject.localPosition = new Vector3(place.Player[0], place.Player[1], place.Player[2]);
            seatObject.localRotation = Quaternion.identity;

            // The player hangs from a body of its own, as the driver from the head pivot: what of the player has a collider
            // and no body does not become part of the vehicle.
            Rigidbody body = made.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;

            playerStop.Value = true;
            crouchWasInCar = crouchInCar.Value;
            crouchInCar.Value = true;
            player.parent = seatObject;
            player.localPosition = Vector3.zero;
            Vector3 turned = player.localEulerAngles;
            player.localEulerAngles = new Vector3(0f, turned.y, 0f);
            seatedIn = vehicle;
            seatIndex = seat;
            seatedAt = frameNow;
            seatBelted = false;
            log.Info(
                LogCategory.Passenger,
                "The local player sits in the " + place.Name + " seat of '" + vehicle.Root.name + "' (vehicle " + vehicle.Shared.Id + "), as a passenger: "
                + why + ". Its belt: " + Belt(place) + ".");
        }

        // While seated: in place, still seated as the game keeps a driver, and out at the button.
        private void KeepSeated()
        {
            Tracked vehicle = seatedIn;

            // Unity's null check is also true for a destroyed object.
            if (vehicle.Body == null || !tracked.Contains(vehicle) || seatObject == null)
            {
                LeaveSeat(false, "the vehicle is gone");
                return;
            }

            if (player.parent != seatObject)
            {
                LeaveSeat(false, "the game hung the player from '" + (player.parent == null ? "nothing" : player.parent.name) + "'");
                return;
            }

            if (!SeatThere(vehicle, seatIndex))
            {
                LeaveSeat(true, "the seat was taken out");
                return;
            }

            if (SeatTakenBefore(vehicle, seatIndex))
            {
                LeaveSeat(true, "another player sat in it at the same moment and keeps it");
                return;
            }

            // What the game's own machines may have changed meanwhile (a menu, sleeping): seated is seated.
            playerStop.Value = true;
            crouchInCar.Value = true;
            if (carriedVelocity == null)
            {
                carriedVelocity = FsmVariables.GlobalVariables.FindFsmVector3(CarriedVelocityVariable);
            }

            if (carriedVelocity != null)
            {
                // As the driver's seat does: what the hand lets go of starts at the vehicle's speed.
                carriedVelocity.Value = vehicle.VelocityAt(player.position);
            }

            if (!ButtonDown())
            {
                return;
            }

            // As the game's driver's seat: not with the belt on, and not while it moves.
            if (seatBelted)
            {
                return;
            }

            Vector3 velocity = vehicle.Following ? vehicle.VelocityAt(seatObject.position) : vehicle.Body.velocity;
            if (Mathf.Abs(velocity.x) > SeatLeaveSpeed || Mathf.Abs(velocity.y) > SeatLeaveSpeed || Mathf.Abs(velocity.z) > SeatLeaveSpeed)
            {
                return;
            }

            LeaveSeat(true, "the player got up");
        }

        // Whether a player with a smaller number sits in the same seat: two who sat down at once, the smaller number keeps it.
        private bool SeatTakenBefore(Tracked vehicle, int seat)
        {
            byte me = manager.Session == null ? (byte)0 : manager.Session.LocalPlayerId;
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                PlayerPose pose = remotes[i].Shown;
                if (remotes[i].Id < me && remotes[i].InWorld && pose.Parent == vehicle.Shared.Id && !pose.Driving && pose.Seat == seat + 1)
                {
                    return true;
                }
            }

            return false;
        }

        // Back on the player's feet where the seat is, as the game lets a driver out.
        private void LeaveSeat(bool standUp, string why)
        {
            Tracked vehicle = seatedIn;
            VehicleSeat place = vehicle.Definition.Seats[seatIndex];
            if (dragging != null)
            {
                LetGoOfBelt(false);
            }

            seatedIn = null;
            seatIndex = -1;
            seatBelted = false;

            // Unity's null check is also true for a destroyed object.
            if (player != null && seatObject != null && player.parent == seatObject)
            {
                player.parent = null;
                Vector3 turned = player.eulerAngles;
                player.eulerAngles = new Vector3(0f, turned.y, 0f);
            }

            if (playerStop != null && standUp)
            {
                playerStop.Value = false;
            }

            if (crouchInCar != null)
            {
                crouchInCar.Value = crouchWasInCar;
            }

            if (carriedVelocity != null)
            {
                carriedVelocity.Value = Vector3.zero;
            }

            if (seatObject != null)
            {
                UnityEngine.Object.Destroy(seatObject.gameObject);
            }

            seatObject = null;
            log.Info(
                LogCategory.Passenger,
                "The local player no longer sits in the " + place.Name + " seat of '" + (vehicle.Body == null ? "a vehicle" : vehicle.Root.name) + "': " + why
                + ". They sat " + (frameNow - seatedAt).ToString("0.0", CultureInfo.InvariantCulture) + " s.");
        }

        /// <summary>
        /// A test aid, and what the console's "ss seat" does: the seats of the vehicle the local player is in or next to,
        /// sitting in one (as the button would, wherever in the vehicle they stand), getting up, and the belt.
        /// </summary>
        public string SeatCommand(string what)
        {
            if (current == null || player == null)
            {
                return "There is no game world yet.";
            }

            string wanted = (what ?? string.Empty).Trim().ToLowerInvariant();
            if (wanted == "off" || wanted == "up")
            {
                if (seatedIn == null)
                {
                    return "You are not sitting in a passenger seat.";
                }

                LeaveSeat(true, "test aid");
                return "You got up.";
            }

            if (wanted == "belt")
            {
                if (seatedIn == null)
                {
                    return "Sit down first.";
                }

                if (seatedIn.Definition.Seats[seatIndex].Belt == SeatBelt.None)
                {
                    return "That seat has no belt.";
                }

                seatBelted = !seatBelted;
                log.Info(LogCategory.Passenger, "Test aid: the local player's belt is " + (seatBelted ? "on" : "off") + ".");
                return seatBelted ? "Belt on." : "Belt off.";
            }

            Tracked vehicle = seatedIn;
            for (int i = 0; vehicle == null && i < tracked.Count; i++)
            {
                if (tracked[i].Body != null && tracked[i].Shared.HasId && tracked[i].Definition.Seats.Length > 0
                    && (tracked[i].Root.position - player.position).sqrMagnitude < 25f)
                {
                    vehicle = tracked[i];
                }
            }

            if (vehicle == null)
            {
                return "No vehicle with passenger seats within 5 m.";
            }

            VehicleSeat[] seats = vehicle.Definition.Seats;
            int number;
            if (int.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                if (number < 1 || number > seats.Length)
                {
                    return "'" + vehicle.Root.name + "' has seats 1 to " + seats.Length + ".";
                }

                if (seatedIn != null)
                {
                    return "Get up first ('ss seat off').";
                }

                if (driven != null || player.parent != null)
                {
                    return "You are at a wheel.";
                }

                if (!SeatThere(vehicle, number - 1))
                {
                    return "That seat is not in the vehicle.";
                }

                if (SeatTaken(vehicle, number - 1))
                {
                    return "Someone sits there.";
                }

                if (LookUpGlobals() == null)
                {
                    return "The game's player was not found as expected.";
                }

                Sit(vehicle, number - 1, "test aid");
                return "You sit in the " + seats[number - 1].Name + " seat of '" + vehicle.Root.name + "'.";
            }

            var text = new System.Text.StringBuilder("'" + vehicle.Root.name + "':");
            for (int s = 0; s < seats.Length; s++)
            {
                text.Append(' ').Append(s + 1).Append(' ').Append(seats[s].Name).Append(" (")
                    .Append(!SeatThere(vehicle, s) ? "not in the vehicle" : seatedIn == vehicle && seatIndex == s ? "you" + (seatBelted ? ", belted" : string.Empty) : SeatTaken(vehicle, s) ? "taken" : "free")
                    .Append(", belt: ").Append(Belt(seats[s])).Append(')');
            }

            return text.Append(". 'ss seat 1' (or 2, 3...) sits down, 'ss seat off' gets up, 'ss seat belt' puts the belt on or off.").ToString();
        }

        private static string Belt(VehicleSeat seat)
        {
            return seat.Belt == SeatBelt.Handle ? "a handle, like the driver's" : seat.Belt == SeatBelt.Buckle ? "a buckle, like the driver's" : "none";
        }
    }
}
