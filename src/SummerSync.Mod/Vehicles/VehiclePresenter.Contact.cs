using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using SummerSync.Core.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// Two vehicles that different machines simulate, crashing into each other (the other
    /// vehicle weighs what it weighs).
    ///
    /// Why: a followed vehicle is a kinematic copy, which gives way to nothing. A vehicle simulated here that runs into one
    /// stops dead in a step of physics, as against a wall, and its driver's head (a joint the game breaks at 300 N) went:
    /// both players died, each in their own game (seen on 2026-10-17).
    ///
    /// How: when a vehicle simulated here comes within reach of a followed one, the copy gets its own physics with its own
    /// mass (<see cref="Tracked.BeginContact"/>), and each physics step its velocity is set so that it gets to where its
    /// owner's machine has it (<see cref="Track"/>): a crash in that step is between two bodies of their real weight. The
    /// owner's report of the crash arrives later than the crash happens here (the network and the buffer of states), and
    /// following it in the meantime would drive the copy on into the vehicle at its speed of before. So a copy that is hit
    /// is let loose for a moment (<see cref="LooseSeconds"/>): it goes where the crash here sends it, and then comes back
    /// to where its owner has it, no faster than <see cref="MaxCorrection"/> on top of how its owner moves it. Each machine
    /// works out the crash of its own vehicle, and its driver lives or dies by it. Out of reach again and back where its
    /// owner has it, the copy is kinematic again; still off a while later (its own wheels hold it a little off while its
    /// owner drives on), it is put there. A copy turned far from how its owner has it outside a crash fell over here by
    /// itself (the moped, with nobody on it to keep it up) and is put as its owner has it.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        // Around a vehicle simulated here, how far a followed one counts as within reach: this, and what the two cover
        // towards each other in this many seconds.
        private const float ContactMargin = 0.75f;
        private const float ContactSeconds = 0.4f;

        // For how long a copy stays with its own physics after it was last within reach, and how close to where its owner
        // has it it has to be to become a kinematic copy again.
        private const double ContactLinger = 1.0;
        private const float ContactSettled = 0.05f;
        private const float ContactSettledTurn = 1f;

        // Out of reach for this long and still not back where its owner has it, a copy is put there and is a kinematic copy
        // again: its own wheels may hold it a few centimetres or a degree off for as long as its owner drives on, or it fell
        // over here (2026-10-18: the moped passed by the host's van and was a copy with its own physics, lying on its side,
        // for the rest of the ride).
        private const double ContactGiveUp = 2.0;

        // Turned this far from how its owner has it, with no crash here letting it go, a copy fell over or rolled here by
        // itself: it is put as its owner has it.
        private const float FarTurn = 30f;

        // Hit faster than this, a copy is let loose for this long: about what the owner's report of the crash needs to get
        // here (a tenth of a second of buffer, the network, and the owner's next report). Slower is a push, and goes on
        // being followed: faster than the copy is brought back with, so that its own coming back is not a crash.
        private const float LooseSpeed = 3f;
        private const float LooseSeconds = 1f;

        // How fast a copy is brought back to where its owner has it, on top of how its owner moves it, in metres and
        // radians per second: a crash that came out differently here is mended without driving the copy into anything.
        private const float MaxCorrection = 2f;
        private const float MaxTurnCorrection = 1.5f;

        // Faster than this between two steps, a followed vehicle was put somewhere, not driven there.
        private const float MaxContactSpeed = 70f;

        // Once per frame, after the vehicles are driven: which followed vehicles are within reach of one simulated here.
        private void UpdateContacts(double now)
        {
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked copy = tracked[i];
                if (copy.Body == null || !copy.Following)
                {
                    continue;
                }

                // The local player rides it: they are carried by where it is put, and their body inside it would push it.
                // A copy whose colliders are on bodies held kinematic (the boat) has nothing to give way with.
                bool near = false;
                if (simulatesAny && copy.Placed && !copy.Ridden && !copy.HasHeldBodies && copy.Shared.Role == BodyRole.Follow)
                {
                    OrientedBox other = copy.Box(0f);
                    for (int j = 0; j < tracked.Count && !near; j++)
                    {
                        Tracked mine = tracked[j];
                        if (mine == copy || !SimulatedHere(mine))
                        {
                            continue;
                        }

                        float closing = mine.Body.velocity.magnitude + Mathf.Abs(copy.Speed);
                        OrientedBox reach = mine.Box(ContactMargin + (closing * ContactSeconds));
                        near = OrientedBox.Meet(ref reach, ref other);
                    }
                }

                if (near)
                {
                    copy.NearUntil = now + ContactLinger;
                }

                bool wanted = now < copy.NearUntil || Time.fixedTime < copy.LooseUntil;
                if (wanted && !copy.InContact)
                {
                    copy.BeginContact(Time.fixedDeltaTime, OnCopyHit);
                }
                else if (!wanted && copy.InContact)
                {
                    if (copy.Settled(ContactSettled, ContactSettledTurn))
                    {
                        copy.EndContact(false);
                    }
                    else if (now >= copy.NearUntil + ContactGiveUp)
                    {
                        copy.EndContact(true);
                    }
                }
            }
        }

        private static bool SimulatedHere(Tracked vehicle)
        {
            return vehicle.Body != null && vehicle.Shared.HasId && vehicle.Shared.Role == BodyRole.Simulate && !vehicle.Following && !vehicle.Body.isKinematic;
        }

        // In FixedUpdate, instead of Move, for a copy that has its own physics.
        private static void Track(Tracked copy)
        {
            SharedBody shared = copy.Shared;
            if (shared.Role != BodyRole.Follow || !shared.HasTarget)
            {
                return;
            }

            BodyPose target = shared.Target;
            var position = new Vector3(target.X, target.Y, target.Z);
            var rotation = new Quaternion(target.QX, target.QY, target.QZ, target.QW);
            float step = Time.fixedDeltaTime;
            Vector3 ownerVelocity;
            Vector3 ownerSpin;
            copy.NoteTarget(position, rotation, step, out ownerVelocity, out ownerSpin);

            Rigidbody body = copy.Body;
            if (Time.fixedTime < copy.LooseUntil || step <= 0f)
            {
                // Where the crash here sends it.
                copy.Speed = Vector3.Dot(body.velocity, body.rotation * Vector3.forward);
                return;
            }

            float turned = Quaternion.Angle(body.rotation, rotation);
            if ((position - body.position).sqrMagnitude > TeleportDistance * TeleportDistance || turned > FarTurn)
            {
                // Far from where its owner has it, or turned far from how its owner has it (put somewhere, a crash that came out
                // very differently here, or a vehicle that fell over here by itself: the moped, with nobody on it to keep it
                // up): put there, as a kinematic copy would be.
                if (turned > FarTurn)
                {
                    copy.NoteTurnedBack(turned);
                }

                copy.Root.position = position;
                copy.Root.rotation = rotation;
                body.velocity = ownerVelocity;
                body.angularVelocity = ownerSpin;
                copy.NoteStep(Vector3.zero);
                return;
            }

            Vector3 wanted = (position - body.position) / step;
            body.velocity = ownerVelocity + Vector3.ClampMagnitude(wanted - ownerVelocity, MaxCorrection);
            body.angularVelocity = ownerSpin + Vector3.ClampMagnitude(Spin(body.rotation, rotation, step) - ownerSpin, MaxTurnCorrection);
            copy.Speed = Vector3.Dot(ownerVelocity, rotation * Vector3.forward);
            copy.NoteStep(ownerVelocity * step);
        }

        // The spin that takes a body from one way to another in that many seconds.
        private static Vector3 Spin(Quaternion from, Quaternion to, float seconds)
        {
            Quaternion delta = to * Quaternion.Inverse(from);
            float angle;
            Vector3 axis;
            delta.ToAngleAxis(out angle, out axis);
            if (angle > 180f)
            {
                angle -= 360f;
            }

            if (Mathf.Abs(angle) < 0.0001f || float.IsNaN(axis.x) || float.IsInfinity(axis.x))
            {
                return Vector3.zero;
            }

            return axis * (angle * Mathf.Deg2Rad / seconds);
        }

        // A copy with its own physics ran into something: if it is a vehicle simulated here and they met fast, it is let loose.
        private void OnCopyHit(Tracked copy, Collision collision)
        {
            if (!copy.InContact || copy.Body == null)
            {
                return;
            }

            Collider collider = collision.collider;
            Rigidbody body = collider == null ? null : collider.attachedRigidbody;
            Tracked mine = null;
            for (int i = 0; body != null && i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                if (vehicle != copy && SimulatedHere(vehicle) && (vehicle.Body == body || body.transform.IsChildOf(vehicle.Root)))
                {
                    mine = vehicle;
                    break;
                }
            }

            if (mine == null)
            {
                return;
            }

            float speed = collision.relativeVelocity.magnitude;
            if (speed < LooseSpeed)
            {
                return;
            }

            bool already = Time.fixedTime < copy.LooseUntil;
            copy.LooseUntil = Time.fixedTime + LooseSeconds;
            copy.Crashes++;
            if (!already)
            {
                log.Info(
                    LogCategory.Vehicle,
                    "Vehicles: '" + mine.Root.name + "', simulated here, ran into '" + copy.Root.name + "', which another machine simulates, at "
                    + speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s: here the crash is between the two of them with their own weight ("
                    + mine.Body.mass.ToString("0", CultureInfo.InvariantCulture) + " and " + copy.Body.mass.ToString("0", CultureInfo.InvariantCulture)
                    + " kg), and the copy goes where it sends it for " + LooseSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s.");
            }
        }

        /// <summary>The part of one vehicle that is about another machine's vehicles running into it.</summary>
        private sealed partial class Tracked
        {
            private VehicleContactProbe probe;
            private bool interpolationNoted;
            private RigidbodyInterpolation contactInterpolation;
            private bool targetKnown;
            private Vector3 lastTarget;
            private Quaternion lastTargetTurn;
            private int contacts;
            private int turnedBack;

            /// <summary>Whether this copy has its own physics because a vehicle simulated here is within reach.</summary>
            public bool InContact { get; private set; }

            /// <summary>Until when (the session's clock) it counts as within reach of a vehicle simulated here.</summary>
            public double NearUntil { get; set; }

            /// <summary>Until when (the game's physics clock) it is let go where a crash here sends it.</summary>
            public float LooseUntil { get; set; }

            /// <summary>Crashes with a vehicle simulated here since it is shown here.</summary>
            public int Crashes { get; set; }

            /// <summary>The box of its colliders where it is now, reaching <paramref name="margin"/> further on every side.</summary>
            public OrientedBox Box(float margin)
            {
                Vector3 centre = Root.TransformPoint(shell.center);
                Vector3 right = Root.right;
                Vector3 up = Root.up;
                Vector3 forward = Root.forward;
                Vector3 scale = Root.lossyScale;
                Vector3 half = shell.extents;
                return new OrientedBox
                {
                    X = centre.x,
                    Y = centre.y,
                    Z = centre.z,
                    RightX = right.x,
                    RightY = right.y,
                    RightZ = right.z,
                    UpX = up.x,
                    UpY = up.y,
                    UpZ = up.z,
                    ForwardX = forward.x,
                    ForwardY = forward.y,
                    ForwardZ = forward.z,
                    HalfRight = (half.x * Mathf.Abs(scale.x)) + margin,
                    HalfUp = (half.y * Mathf.Abs(scale.y)) + margin,
                    HalfForward = (half.z * Mathf.Abs(scale.z)) + margin,
                };
            }

            /// <summary>Gives the copy its own physics, going on at the speed it was moved at.</summary>
            public void BeginContact(float step, System.Action<Tracked, Collision> hit)
            {
                if (probe == null)
                {
                    probe = Root.gameObject.AddComponent<VehicleContactProbe>();
                    Tracked self = this;
                    probe.Hit = collision => hit(self, collision);
                }

                InContact = true;
                targetKnown = false;
                LooseUntil = 0f;
                turnedBack = 0;
                contacts++;
                contactInterpolation = Body.interpolation;
                interpolationNoted = true;
                Body.interpolation = RigidbodyInterpolation.Interpolate;
                Body.isKinematic = false;

                // At the speed it was moved at, unless that step was a jump (a vehicle brought or put somewhere).
                Vector3 moving = step > 0f ? lastStep / step : Vector3.zero;
                Body.velocity = moving.sqrMagnitude > MaxContactSpeed * MaxContactSpeed ? Vector3.zero : moving;
                Body.angularVelocity = Vector3.zero;
                Body.WakeUp();
                log.Info(
                    LogCategory.Vehicle,
                    "Vehicles: '" + Root.name + "', which another machine simulates, is within reach of a vehicle simulated here: it has its own physics ("
                    + Body.mass.ToString("0", CultureInfo.InvariantCulture) + " kg) and is driven to where its owner has it. Time " + contacts + " since it is shown here.");
            }

            /// <summary>
            /// A kinematic copy again: nothing simulated here is within reach, and it is where its owner has it, or with
            /// <paramref name="putThere"/> it did not get there by itself and is put there.
            /// </summary>
            public void EndContact(bool putThere)
            {
                string off = putThere && targetKnown
                    ? " and was still " + (Body.position - lastTarget).magnitude.ToString("0.00", CultureInfo.InvariantCulture) + " m and "
                        + Quaternion.Angle(Body.rotation, lastTargetTurn).ToString("0", CultureInfo.InvariantCulture) + " degrees from where its owner has it: put there,"
                    : ":";
                InContact = false;
                Body.isKinematic = true;
                if (interpolationNoted)
                {
                    Body.interpolation = contactInterpolation;
                    interpolationNoted = false;
                }

                AtRest = false;
                if (putThere)
                {
                    // The next step puts it where its owner has it instead of moving it there.
                    Placed = false;
                }

                log.Info(LogCategory.Vehicle, "Vehicles: '" + Root.name + "' is out of reach of the vehicles simulated here" + off + " a kinematic copy again (" + Crashes + " crash(es) with them so far).");
            }

            /// <summary>A copy that turned far from how its owner has it is put that way: the log has the first time in each contact.</summary>
            public void NoteTurnedBack(float degrees)
            {
                turnedBack++;
                if (turnedBack == 1)
                {
                    log.Info(
                        LogCategory.Vehicle,
                        "Vehicles: '" + Root.name + "', a copy with its own physics, was " + degrees.ToString("0", CultureInfo.InvariantCulture)
                            + " degrees from how its owner has it (it fell over or rolled here by itself): put as its owner has it.");
                }
            }

            /// <summary>Forgets the contact when this machine stops following the vehicle.</summary>
            public void ForgetContact()
            {
                if (InContact && interpolationNoted)
                {
                    Body.interpolation = contactInterpolation;
                }

                InContact = false;
                interpolationNoted = false;
                NearUntil = 0.0;
                LooseUntil = 0f;
                targetKnown = false;
            }

            /// <summary>Takes the mod's component off the vehicle: the session is over, or the world is.</summary>
            public void DropProbe()
            {
                // Unity's null check is also true for a destroyed object.
                if (probe != null)
                {
                    probe.Hit = null;
                    UnityEngine.Object.Destroy(probe);
                }

                probe = null;
            }

            /// <summary>
            /// Notes where its owner has it for this step, and how its owner moves it: from the place of the step before.
            /// </summary>
            public void NoteTarget(Vector3 position, Quaternion rotation, float step, out Vector3 velocity, out Vector3 spin)
            {
                if (targetKnown && step > 0f)
                {
                    velocity = (position - lastTarget) / step;
                    spin = Spin(lastTargetTurn, rotation, step);

                    // A jump of the owner's states, not a speed.
                    if (velocity.sqrMagnitude > MaxSteppedSpeed * MaxSteppedSpeed)
                    {
                        velocity = Vector3.zero;
                        spin = Vector3.zero;
                    }
                }
                else
                {
                    // The first step: it goes on as it went when it got its physics.
                    velocity = Body.velocity;
                    spin = Body.angularVelocity;
                }

                lastTarget = position;
                lastTargetTurn = rotation;
                targetKnown = true;
            }

            /// <summary>Whether the copy is within that distance and that many degrees of where its owner has it.</summary>
            public bool Settled(float distance, float degrees)
            {
                if (!targetKnown || Time.fixedTime < LooseUntil)
                {
                    return false;
                }

                return (Body.position - lastTarget).sqrMagnitude <= distance * distance && Quaternion.Angle(Body.rotation, lastTargetTurn) <= degrees;
            }
        }
    }
}
