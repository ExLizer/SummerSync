using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// A passenger dies in a crash as the game's driver does.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Morir al volante" and "El modo pasajero"): a driver's head is
    /// "DriverHeadPivot", a body of 0.5 kg with no collider hung from the vehicle by a ConfigurableJoint. A state machine of
    /// the drive trigger gives the joint its breaking force: "HeadForce" (van, Gifu, Ferndale, Satsuma: "ForceBase" plus
    /// "ForceSeatbelt" while the belt is on, times 1.5 with "PlayerHelmet") or "HelmetSim" (Ruscko, Kekmet, Jonnez: one
    /// force without a helmet and another with one). The numbers differ by vehicle and are in its file ("crash"). When a
    /// crash breaks the joint, the head's "Death" kills the driver. The bus does the same for whoever rides it.
    ///
    /// The crash happens on the machine that runs the vehicle's physics, so that is where every passenger gets a head: the
    /// driver's own, copied, at the passenger's eyes, with the same joint and the same force for their belt and helmet. A
    /// head that breaks kills its player: here if it is the local one, and otherwise through the tally "crash/(player)",
    /// which says the vehicle and a number of its own; the player's machine takes it as news only (not in a catch-up), and
    /// only while that player rides that vehicle. A head is not armed for its first second (the game's waits ten at the
    /// start), so that sitting down or a vehicle coming to this machine is not a crash.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const string HeadPivotName = "DriverHeadPivot";
        private const string HeadMachine = "HeadForce";
        private const string CrashTally = "crash/";
        private const float HeadArmSeconds = 1f;

        // A standing passenger who walks this far from where their head was made gets a new one.
        private const float HeadMoved = 0.4f;

        // For how long after a crash killed a player no head is made for them again: they are on their way out.
        private const double CrashedSeconds = 60.0;

        private readonly List<Head> heads = new List<Head>();
        private readonly Dictionary<byte, double> crashed = new Dictionary<byte, double>();
        private readonly List<Occupant> occupants = new List<Occupant>();
        private TallyReplication crashTallies;
        private Tracked knocked;
        private Tracked cruising;
        private float cruiseSpeed;
        private float cruiseUntil;
        private bool cruiseCoasts;
        private VehicleContactProbe cruiseProbe;
        private Tracked driveAid;
        private float driveAidThrottle;
        private float driveAidSteer;
        private float driveAidUntil;

        // How fast "ss cruise … coast" speeds the vehicle up, in metres per second each second: about what the game's cars do.
        private const float CruiseRamp = 10f;
        private Vector3 knockedVelocity;
        private int knockSteps;
        private int lastCrashToken = int.MinValue;
        private double ridingUntil;
        private ushort lastRidden;

        private struct Occupant
        {
            public byte Player;
            public bool Local;
            public Tracked Vehicle;
            public Vector3 Eyes;
            public int Seat;
            public bool Belted;
            public bool Helmet;
        }

        private sealed class Head
        {
            public byte Player;
            public bool Local;
            public Tracked Vehicle;
            public GameObject Object;
            public ConfigurableJoint Joint;
            public Vector3 Eyes;
            public double ArmAt;
            public bool Armed;
            public float Force;
            public int Seat;
            public bool Belted;
            public bool Helmet;
        }

        // Once per frame, after the vehicles are driven: a head for everyone on board of what this machine simulates.
        private void UpdateHeads(double now)
        {
            ListenForCrashes();
            occupants.Clear();
            byte me = manager.Session == null ? (byte)0 : manager.Session.LocalPlayerId;
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                PlayerPose pose = remote.Shown;
                if (!remote.InWorld || pose.Parent == 0 || pose.Parent >= PlayerPose.NpcParentBase || pose.Driving)
                {
                    continue;
                }

                Tracked vehicle = Find(pose.Parent);
                if (vehicle == null || !SimulatesHere(vehicle))
                {
                    continue;
                }

                occupants.Add(new Occupant
                {
                    Player = remote.Id,
                    Vehicle = vehicle,
                    Eyes = new Vector3(pose.X, pose.Y + pose.EyeHeight, pose.Z),
                    Seat = pose.Seat,
                    Belted = pose.Belted,
                    Helmet = pose.Helmet,
                });
            }

            // The local player, seated or standing in a vehicle this machine simulates (nobody drives it).
            Tracked aboard = seatedIn ?? stayedIn;
            if (aboard != null && aboard.Body != null && SimulatesHere(aboard) && aboard.Shared.HasId && LookUpGlobals() != null)
            {
                Transform eyes = Camera.main == null ? null : Camera.main.transform;
                occupants.Add(new Occupant
                {
                    Player = me,
                    Local = true,
                    Vehicle = aboard,
                    Eyes = aboard.Root.InverseTransformPoint(eyes != null ? eyes.position : player.position + (Vector3.up * 0.6f)),
                    Seat = seatedIn == aboard ? seatIndex + 1 : 0,
                    Belted = seatedIn == aboard && seatBelted,
                    Helmet = helmetWorn != null && helmetWorn.Value,
                });
            }

            // Heads that broke, and heads of nobody.
            for (int i = heads.Count - 1; i >= 0; i--)
            {
                Head head = heads[i];

                // Unity's null check is also true for a destroyed object: a broken joint is gone.
                if (head.Armed && head.Joint == null && head.Object != null && head.Vehicle.Body != null)
                {
                    Crashed(head, now);
                    DropHead(i);
                    continue;
                }

                int found = -1;
                for (int o = 0; o < occupants.Count && found < 0; o++)
                {
                    if (occupants[o].Player == head.Player && occupants[o].Local == head.Local)
                    {
                        found = o;
                    }
                }

                if (found < 0 || head.Object == null || head.Joint == null || occupants[found].Vehicle != head.Vehicle
                    || (occupants[found].Eyes - head.Eyes).sqrMagnitude > HeadMoved * HeadMoved)
                {
                    DropHead(i);
                }
            }

            for (int o = 0; o < occupants.Count; o++)
            {
                Occupant occupant = occupants[o];
                double since;
                if (crashed.TryGetValue(occupant.Player, out since) && now - since < CrashedSeconds)
                {
                    continue;
                }

                Head head = null;
                for (int i = 0; i < heads.Count && head == null; i++)
                {
                    if (heads[i].Player == occupant.Player && heads[i].Local == occupant.Local)
                    {
                        head = heads[i];
                    }
                }

                if (head == null)
                {
                    head = MakeHead(occupant, now);
                    if (head == null)
                    {
                        continue;
                    }

                    heads.Add(head);
                }

                head.Seat = occupant.Seat;
                head.Belted = occupant.Belted;
                head.Helmet = occupant.Helmet;
                head.Force = head.Vehicle.Definition.HeadHolds(occupant.Belted, occupant.Helmet);
                if (!head.Armed && now >= head.ArmAt)
                {
                    head.Armed = true;
                }

                if (head.Armed && head.Joint != null && head.Joint.breakForce != head.Force)
                {
                    head.Joint.breakForce = head.Force;
                    head.Joint.breakTorque = head.Force;
                }
            }
        }

        private static bool SimulatesHere(Tracked vehicle)
        {
            return vehicle.Body != null && vehicle.Shared.HasId && vehicle.Shared.Role == BodyRole.Simulate && !vehicle.Following && !vehicle.Body.isKinematic;
        }

        // A copy of the driver's head at the passenger's eyes, hung from the vehicle the same way, not yet able to break.
        private Head MakeHead(Occupant occupant, double now)
        {
            Tracked vehicle = occupant.Vehicle;
            ConfigurableJoint pattern = vehicle.HeadPattern;
            if (pattern == null || vehicle.Definition.CrashHead <= 0f)
            {
                return null;
            }

            Rigidbody patternBody = pattern.GetComponent<Rigidbody>();
            var made = new GameObject("SummerSync head of player " + occupant.Player.ToString(CultureInfo.InvariantCulture));
            made.layer = pattern.gameObject.layer;
            made.transform.parent = vehicle.Root;
            made.transform.position = vehicle.Root.TransformPoint(occupant.Eyes);
            made.transform.rotation = pattern.transform.rotation;
            Rigidbody body = made.AddComponent<Rigidbody>();
            body.mass = patternBody != null ? patternBody.mass : 0.5f;
            body.drag = patternBody != null ? patternBody.drag : 1f;
            body.angularDrag = patternBody != null ? patternBody.angularDrag : 0.05f;
            body.useGravity = patternBody == null || patternBody.useGravity;
            body.interpolation = RigidbodyInterpolation.None;
            body.velocity = vehicle.VelocityAt(made.transform.position);
            body.angularVelocity = vehicle.Body.angularVelocity;
            ConfigurableJoint joint = made.AddComponent<ConfigurableJoint>();
            CopyJoint(pattern, joint);
            joint.autoConfigureConnectedAnchor = true;
            joint.connectedBody = vehicle.Body;
            joint.breakForce = float.PositiveInfinity;
            joint.breakTorque = float.PositiveInfinity;
            log.Info(
                LogCategory.Passenger,
                "A head for " + (occupant.Local ? "the local player" : "player " + occupant.Player) + " in '" + vehicle.Root.name + "' ("
                + (occupant.Seat > 0 ? vehicle.Definition.Seats[occupant.Seat - 1].Name + " seat" : "standing") + ", belt " + (occupant.Belted ? "on" : "off")
                + ", helmet " + (occupant.Helmet ? "on" : "off") + "), copied from its '" + HeadPivotName + "': it breaks from "
                + vehicle.Definition.HeadHolds(occupant.Belted, occupant.Helmet).ToString("0", CultureInfo.InvariantCulture) + " N now (without belt or helmet "
                + vehicle.Definition.CrashHead.ToString("0", CultureInfo.InvariantCulture) + " N, as its file says"
                + (vehicle.HeadForceBase == null ? string.Empty : "; the game's 'ForceBase' is " + vehicle.HeadForceBase.Value.ToString("0", CultureInfo.InvariantCulture))
                + "), armed in " + HeadArmSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s.");
            return new Head
            {
                Player = occupant.Player,
                Local = occupant.Local,
                Vehicle = vehicle,
                Object = made,
                Joint = joint,
                Eyes = occupant.Eyes,
                ArmAt = now + HeadArmSeconds,
            };
        }

        // Every setting of the game's joint that the mod's head takes as it is; the bodies and anchors are its own.
        private static void CopyJoint(ConfigurableJoint from, ConfigurableJoint to)
        {
            PropertyInfo[] settings = typeof(ConfigurableJoint).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < settings.Length; i++)
            {
                PropertyInfo setting = settings[i];
                if (!setting.CanRead || !setting.CanWrite || setting.GetIndexParameters().Length != 0 || setting.DeclaringType == typeof(Component)
                    || setting.DeclaringType == typeof(UnityEngine.Object))
                {
                    continue;
                }

                switch (setting.Name)
                {
                    case "connectedBody":
                    case "connectedAnchor":
                    case "autoConfigureConnectedAnchor":
                    case "breakForce":
                    case "breakTorque":
                    case "name":
                    case "tag":
                    case "hideFlags":
                        continue;
                }

                setting.SetValue(to, setting.GetValue(from, null), null);
            }
        }

        private void DropHead(int index)
        {
            Head head = heads[index];
            heads.RemoveAt(index);

            // Unity's null check is also true for a destroyed object.
            if (head.Object != null)
            {
                UnityEngine.Object.Destroy(head.Object);
            }
        }

        private void DropHeads()
        {
            for (int i = heads.Count - 1; i >= 0; i--)
            {
                DropHead(i);
            }
        }

        // A head broke: its player dies, here or on their own machine.
        private void Crashed(Head head, double now)
        {
            Tracked vehicle = head.Vehicle;
            crashed[head.Player] = now;
            log.Info(
                LogCategory.Passenger,
                "Crash in '" + vehicle.Root.name + "': the head of " + (head.Local ? "the local player" : "player " + head.Player) + " ("
                + (head.Seat > 0 ? vehicle.Definition.Seats[head.Seat - 1].Name + " seat" : "standing") + ", belt " + (head.Belted ? "on" : "off") + ", helmet "
                + (head.Helmet ? "on" : "off") + ") broke off at " + head.Force.ToString("0", CultureInfo.InvariantCulture) + " N; the vehicle goes at "
                + vehicle.Body.velocity.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s now. "
                + (head.Local ? "The local player dies, the game's way." : "Their machine is told."));
            if (head.Local)
            {
                CrashDeath.Kill(vehicle.Root, head.Eyes, player, log);
                return;
            }

            if (crashTallies != null)
            {
                int token = (int)((long)(now * 1000.0) & 0x7FFFFFFF);
                crashTallies.Set(CrashTally + head.Player.ToString(CultureInfo.InvariantCulture), new[] { (int)vehicle.Shared.Id, token });
            }
        }

        // The tallies of the session, heard for a crash that killed the local player on another machine.
        private void ListenForCrashes()
        {
            TallyReplication tallies = current == null ? null : manager.Tallies;
            if (tallies == crashTallies)
            {
                return;
            }

            if (crashTallies != null)
            {
                crashTallies.StateWanted -= OnCrashTally;
            }

            crashTallies = tallies;
            lastCrashToken = int.MinValue;
            crashed.Clear();
            if (crashTallies != null)
            {
                crashTallies.StateWanted += OnCrashTally;
            }
        }

        private void OnCrashTally(string name, int[] values)
        {
            if (!name.StartsWith(CrashTally, StringComparison.Ordinal) || values.Length < 2 || manager.Session == null)
            {
                return;
            }

            byte who;
            if (!byte.TryParse(name.Substring(CrashTally.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out who)
                || who != manager.Session.LocalPlayerId)
            {
                return;
            }

            bool news = !crashTallies.CatchingUp && values[1] != lastCrashToken;
            lastCrashToken = values[1];
            if (!news)
            {
                return;
            }

            // Only while riding that vehicle, or a moment after: an old word must not kill someone who got out.
            ushort vehicleId = (ushort)values[0];
            Tracked vehicle = Find(vehicleId);
            bool aboard = vehicle != null && vehicle.Body != null && (seatedIn == vehicle || ridden == vehicle || (lastRidden == vehicleId && frameNow < ridingUntil));
            log.Info(
                LogCategory.Passenger,
                "The machine that runs vehicle " + vehicleId + " says a crash broke the local player's head"
                + (aboard ? ": the local player dies, the game's way." : ", but the local player does not ride it: nothing happens."));
            if (aboard)
            {
                Vector3 eyes = Camera.main != null ? vehicle.Root.InverseTransformPoint(Camera.main.transform.position) : vehicle.Root.InverseTransformPoint(player.position);
                CrashDeath.Kill(vehicle.Root, eyes, player, log);
            }
        }

        /// <summary>
        /// A test aid ("ss crash"): a knock, as running into something: the vehicle the local player is in, or the nearest
        /// one within 10 m, goes that many metres per second faster along its nose for one physics step and back to its
        /// speed in the next, if this machine runs its physics. A head breaks at about 12 m/s gained or lost in one step
        /// (300 N on 0.5 kg in a fiftieth of a second).
        /// </summary>
        public string CrashTest(string what)
        {
            if (current == null || player == null)
            {
                return "There is no game world yet.";
            }

            float change;
            if (!float.TryParse((what ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out change) || Mathf.Abs(change) > 100f)
            {
                return "ss crash <m/s>: how much faster (or slower, negative) along its nose, up to 100.";
            }

            Tracked vehicle = driven ?? seatedIn ?? ridden ?? stayedIn;
            for (int i = 0; vehicle == null && i < tracked.Count; i++)
            {
                if (tracked[i].Body != null && (tracked[i].Root.position - player.position).sqrMagnitude < 100f)
                {
                    vehicle = tracked[i];
                }
            }

            if (vehicle == null)
            {
                return "No vehicle within 10 m.";
            }

            if (vehicle.Body.isKinematic || vehicle.Following)
            {
                return "Another machine runs the physics of '" + vehicle.Root.name + "': run it there.";
            }

            Vector3 before = vehicle.Body.velocity;
            vehicle.Body.velocity = before + (vehicle.Root.forward * change);
            knocked = vehicle;
            knockedVelocity = before;
            knockSteps = 1;
            log.Info(
                LogCategory.Passenger,
                "Test aid: '" + vehicle.Root.name + "' knocked: from " + before.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " to "
                + vehicle.Body.velocity.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s for a physics step, and back. Heads aboard: " + heads.Count + ".");
            return "'" + vehicle.Root.name + "' knocked by " + change.ToString("0.#", CultureInfo.InvariantCulture) + " m/s for one physics step (" + heads.Count + " passenger head(s) aboard).";
        }

        /// <summary>
        /// A test aid ("ss cruise"): the vehicle the local player is in, or the nearest one within 10 m, goes along its nose
        /// at that many metres per second for so many seconds, as if driven at an even speed, if this machine runs its
        /// physics. For measuring how evenly the other machines see it go. Then it stops, or with "coast" rolls on as the
        /// game's physics takes it: to run it into something without the aid pushing it through the crash.
        /// </summary>
        public string CruiseTest(string speedText, string secondsText, string coastText)
        {
            if (current == null || player == null)
            {
                return "There is no game world yet.";
            }

            float speed;
            float seconds = 6f;
            if (!float.TryParse((speedText ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out speed) || Mathf.Abs(speed) > 40f
                || (secondsText != null && (!float.TryParse(secondsText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds <= 0f || seconds > 60f)))
            {
                return "ss cruise <m/s> [seconds] [coast]: up to 40 m/s, for up to 60 s (6 if not said); \"coast\" rolls on afterwards.";
            }

            Tracked vehicle = driven ?? seatedIn ?? ridden ?? stayedIn;
            for (int i = 0; vehicle == null && i < tracked.Count; i++)
            {
                if (tracked[i].Body != null && (tracked[i].Root.position - player.position).sqrMagnitude < 100f)
                {
                    vehicle = tracked[i];
                }
            }

            if (vehicle == null)
            {
                return "No vehicle within 10 m.";
            }

            if (vehicle.Body.isKinematic || vehicle.Following)
            {
                return "Another machine runs the physics of '" + vehicle.Root.name + "': run it there.";
            }

            cruising = vehicle;
            cruiseSpeed = speed;
            cruiseUntil = Time.fixedTime + seconds;
            cruiseCoasts = string.Equals((coastText ?? string.Empty).Trim(), "coast", System.StringComparison.OrdinalIgnoreCase);
            DropCruiseProbe();
            if (cruiseCoasts)
            {
                // It lets go at the first thing it runs into: the aid does not push it on through the crash.
                cruiseProbe = vehicle.Root.gameObject.AddComponent<VehicleContactProbe>();
                cruiseProbe.Hit = OnCruiseHit;
            }

            log.Info(LogCategory.Vehicle, "Test aid: '" + vehicle.Root.name + "' goes at " + speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s for " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s.");
            return "'" + vehicle.Root.name + "' goes at " + speed.ToString("0.#", CultureInfo.InvariantCulture) + " m/s along its nose for " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s.";
        }

        // After every physics step: the cruise of the test aid, and its end.
        private void Cruise()
        {
            // Unity's null check is also true for a destroyed object.
            if (cruising == null)
            {
                return;
            }

            if (cruising.Body == null || cruising.Body.isKinematic || Time.fixedTime >= cruiseUntil)
            {
                if (cruising.Body != null && !cruising.Body.isKinematic && !cruiseCoasts)
                {
                    Vector3 left = cruising.Body.velocity;
                    cruising.Body.velocity = cruising.Root.up * Vector3.Dot(left, cruising.Root.up);
                }

                cruising = null;
                DropCruiseProbe();
                return;
            }

            Vector3 velocity = cruising.Body.velocity;
            Vector3 up = cruising.Root.up;
            Vector3 forward = cruising.Root.forward;

            // Coasting into something, it gets to its speed as a car does: all at once is a blow that breaks a driver's head
            // from about 12 m/s (2026-10-17).
            float speed = cruiseCoasts ? Mathf.MoveTowards(Vector3.Dot(velocity, forward), cruiseSpeed, CruiseRamp * Time.fixedDeltaTime) : cruiseSpeed;
            cruising.Body.velocity = (forward * speed) + (up * Vector3.Dot(velocity, up));
        }

        /// <summary>
        /// A test aid ("ss drive"): the vehicle the local player drives gets that throttle (0 to 1) and steering (-1 to 1)
        /// from its controls for so many seconds, as if from the keys, and first gear if it stands in neutral. The game's
        /// own physics takes it, which "ss cruise" pushes past: to see how a vehicle goes, leans and turns when driven.
        /// </summary>
        public string DriveTest(string throttleText, string steerText, string secondsText)
        {
            if (current == null || player == null)
            {
                return "There is no game world yet.";
            }

            float throttle;
            float steer;
            float seconds = 6f;
            if (!float.TryParse((throttleText ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out throttle) || throttle < 0f || throttle > 1f
                || !float.TryParse((steerText ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out steer) || steer < -1f || steer > 1f
                || (secondsText != null && (!float.TryParse(secondsText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds <= 0f || seconds > 60f)))
            {
                return "ss drive <throttle 0 to 1> <steering -1 to 1> [seconds]: for up to 60 s (6 if not said).";
            }

            if (driven == null)
            {
                return "You are not at the wheel of a vehicle.";
            }

            if (driven.Body.isKinematic || driven.Following)
            {
                return "Another machine runs the physics of '" + driven.Root.name + "'.";
            }

            driveAid = driven;
            driveAidThrottle = throttle;
            driveAidSteer = steer;
            driveAidUntil = Time.time + seconds;
            string said = "'" + driven.Root.name + "' is driven at " + throttle.ToString("0.##", CultureInfo.InvariantCulture) + " throttle and "
                + steer.ToString("0.##", CultureInfo.InvariantCulture) + " steering for " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s.";
            log.Info(LogCategory.Vehicle, "Test aid: " + said);
            return said;
        }

        // After the game's controls have read the keys in this frame: the test aid's throttle and steering, which the
        // controls use in the physics steps of the next frame.
        private void DriveAid()
        {
            // Unity's null check is also true for a destroyed object.
            if (driveAid == null)
            {
                return;
            }

            if (driveAid.Body == null || driveAid != driven || Time.time >= driveAidUntil || !driveAid.DriveByAid(driveAidThrottle, driveAidSteer))
            {
                driveAid = null;
            }
        }

        // The coasting cruise ran into something: it lets go now (collisions are told before the step's end is).
        private void OnCruiseHit(Collision collision)
        {
            if (cruising != null && collision.relativeVelocity.sqrMagnitude > 1f)
            {
                cruiseUntil = 0f;
                log.Info(
                    LogCategory.Vehicle,
                    "Test aid: '" + cruising.Root.name + "' ran into '" + (collision.collider == null ? "?" : collision.collider.name) + "' at "
                    + collision.relativeVelocity.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s and is let go.");
            }
        }

        private void DropCruiseProbe()
        {
            // Unity's null check is also true for a destroyed object.
            if (cruiseProbe != null)
            {
                cruiseProbe.Hit = null;
                UnityEngine.Object.Destroy(cruiseProbe);
            }

            cruiseProbe = null;
        }

        // After every physics step: the knock of the test aid is over after its step.
        private void EndKnock()
        {
            if (knocked == null || --knockSteps > 0)
            {
                return;
            }

            // Unity's null check is also true for a destroyed object.
            if (knocked.Body != null && !knocked.Body.isKinematic)
            {
                knocked.Body.velocity = knockedVelocity;
            }

            knocked = null;
        }

        // What the local player rode last and until when it counts, for a crash told a moment late.
        private void NoteRide()
        {
            Tracked aboard = seatedIn ?? ridden ?? stayedIn;
            if (aboard != null && aboard.Shared.HasId)
            {
                lastRidden = aboard.Shared.Id;
                ridingUntil = frameNow + 2.0;
            }
        }
    }
}
