using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The parts of a vehicle that have a body of their own (doors, seats, bumpers, the fire extinguisher in its holder): kept
    /// where they belong on it while this machine follows it and when its physics starts here, and every joint of theirs that
    /// breaks written down.
    ///
    /// Why (docs/GAME_NOTES.md, "El Satsuma desarmado a 3 km", 2026-11-14): in a guest's game the copy of the Satsuma had its
    /// fire extinguisher, held to it by a joint, an absurd distance away and its passenger seat 101 m off; when the car's
    /// physics started there, those joints threw it at 6,930 m/s, fourteen parts came off and were taken off in the host's game
    /// too. And the lock of a shut door is a joint the game makes anew each time the door is shut: one made after this machine
    /// started following the vehicle was not held, and the door of a passenger's copy came loose (2026-11-14 report).
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        // A part with a body that stands this far from where it belongs on the vehicle is not where any crash leaves it: it is
        // put back.
        private const float PartAstray = 0.5f;

        // How often a followed vehicle's parts are looked at: joints the game made since, and parts gone astray.
        private const float JointLookEvery = 0.5f;

        // A change of speed in one step of physics after a hand-over that no crash gives (1,250 m/s², about 130 g): the
        // vehicle is put back where it was handed over, at most this many times.
        private const float ThrowBack = 25f;
        private const float ThrowBackSpin = 25f;
        private const int ThrowBacksMost = 2;

        // Lines about joints that break and parts put back, for each vehicle in a session.
        private const int BreaksLogged = 12;
        private const int AstrayLogged = 6;

        private int handOvers;

        /// <summary>How many vehicles started their physics here after another machine's (it only counts up).</summary>
        public int HandOvers
        {
            get { return handOvers; }
        }

        /// <summary>
        /// Whether a thing at that point rides a vehicle whose physics runs here and is awake: held still where it lies (far
        /// from the player), it would be a wall inside the vehicle when it moves (2026-11-14: the car just handed over ran
        /// into the warning triangle that rode in it).
        /// </summary>
        public bool RidesAwake(Vector3 position)
        {
            ushort id;
            Transform root;
            if (!TryGetCarrier(position, out id, out root))
            {
                return false;
            }

            Tracked vehicle = Find(id);
            return vehicle != null && SimulatedHere(vehicle) && !vehicle.HasHeldBodies && !vehicle.Body.IsSleeping();
        }

        // The parts of the vehicle far off their places are put back, said in the log.
        private void PutPartsBack(Tracked vehicle, string when)
        {
            var said = new StringBuilder(200);
            int put = vehicle.PutPartsBackNow(said);
            if (put > 0)
            {
                log.Warning(
                    LogCategory.Vehicle,
                    "Hand-over: '" + vehicle.Root.name + "' had " + put + " part(s) far off their places on it, put back " + when + ": " + said + ".");
            }
        }

        private sealed partial class Tracked
        {
            // Each part's joints, as they were when its parts were last looked for.
            private Joint[][] partOwnJoints = new Joint[0][];
            private float nextJointLook;
            private bool partsHeldThroughWatch;
            private Vector3 handOverAt;
            private Quaternion handOverTurn;
            private bool handOverKnown;
            private int throwBacks;
            private int breaksSaid;
            private int astraySaid;
            private int astrayPut;
            private int newJoints;
            private readonly List<JointBreakProbe> breakProbes = new List<JointBreakProbe>();

            /// <summary>
            /// The game makes some joints while another machine runs the vehicle (the lock of a door, every time it is shut):
            /// those of the parts that swing on a hinge are looked for again, and held like the others.
            /// </summary>
            private void FindNewJoints()
            {
                for (int i = 0; i < parts.Length && i < partOwnJoints.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (part == null || !Swings(i))
                    {
                        continue;
                    }

                    Joint[] joints = part.GetComponents<Joint>();
                    for (int j = 0; j < joints.Length; j++)
                    {
                        if (partJoints.Contains(joints[j]))
                        {
                            continue;
                        }

                        partJoints.Add(joints[j]);
                        newJoints++;
                        if (newJoints <= AstrayLogged)
                        {
                            log.Info(
                                LogCategory.Vehicle,
                                "'" + Root.name + "': a " + joints[j].GetType().Name + " the game made on '" + part.name + "' after this machine started following it (a door's lock: "
                                + joints[j].breakForce.ToString("0", CultureInfo.InvariantCulture) + " N) is held too.");
                        }
                    }
                }
            }

            // Whether that part hangs on a hinge of its own (a door, the bonnet, the boot lid).
            private bool Swings(int index)
            {
                Joint[] joints = partOwnJoints[index];
                for (int j = 0; joints != null && j < joints.Length; j++)
                {
                    if (joints[j] is HingeJoint)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// While following: a part that went astray is put back on the vehicle (it is kinematic here, and its parts hang
            /// on joints that do not break), and the first few times are written down.
            /// </summary>
            private void PutAstrayPartsBack()
            {
                StringBuilder said = astraySaid < AstrayLogged ? new StringBuilder(120) : null;
                int put = PutPartsBackNow(said);
                if (put == 0)
                {
                    return;
                }

                astrayPut += put;
                if (said != null)
                {
                    astraySaid++;
                    log.Warning(
                        LogCategory.Vehicle,
                        "'" + Root.name + "', which another machine simulates, had " + put + " part(s) far off their places on it: " + said + "; put back ("
                        + astrayPut + " so far; the vehicle " + (AtRest ? "at rest" : "moving") + (InContact ? ", with its own physics near a vehicle simulated here" : string.Empty)
                        + ")" + (astraySaid == AstrayLogged ? " (no more of these lines for it)." : "."));
                }
            }

            /// <summary>
            /// Puts every part with a body that stands farther than <see cref="PartAstray"/> from its place on the vehicle back
            /// there, at rest: by where it was on the vehicle when its parts were looked for (a fixed joint keeps that pose) or
            /// by its joint's two anchors (a hinge). Returns how many; their names and distances go to <paramref name="said"/>.
            /// </summary>
            public int PutPartsBackNow(StringBuilder said)
            {
                int put = 0;
                for (int i = 0; i < parts.Length && i < partOwnJoints.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object. A kinematic part does not follow its joint:
                    // only what holds it there moves it (a dynamic one is pulled back by its joint in the next step).
                    if (part == null || !part.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    Joint joint = HoldingJoint(partOwnJoints[i]);
                    if (joint == null)
                    {
                        continue;
                    }

                    Transform at = part.transform;
                    float off;
                    if (joint is FixedJoint)
                    {
                        Vector3 home = Root.TransformPoint(partHome[i]);
                        off = (home - at.position).magnitude;
                        if (!(off <= PartAstray))
                        {
                            at.position = home;
                            at.rotation = Root.rotation * partHomeTurn[i];
                        }
                    }
                    else
                    {
                        Vector3 gap = Body.transform.TransformPoint(joint.connectedAnchor) - at.TransformPoint(joint.anchor);
                        off = gap.magnitude;
                        if (!(off <= PartAstray))
                        {
                            // A broken pose (not a number) is not moved by a gap: it goes where it was on the vehicle.
                            at.position = IsNumber(gap) ? at.position + gap : Root.TransformPoint(partHome[i]);
                        }
                    }

                    if (off <= PartAstray)
                    {
                        continue;
                    }

                    if (!part.isKinematic)
                    {
                        part.velocity = Vector3.zero;
                        part.angularVelocity = Vector3.zero;
                    }

                    put++;
                    if (said != null && put <= 4)
                    {
                        said.Append(put == 1 ? string.Empty : ", ").Append('\'').Append(part.name).Append("' ").Append(Metres(off))
                            .Append(part.isKinematic ? " (kinematic, layer " + part.gameObject.layer + ")" : string.Empty);
                    }
                }

                if (said != null && put > 4)
                {
                    said.Append(" and ").Append(put - 4).Append(" more");
                }

                return put;
            }

            // The joint that holds a part to the vehicle's body: its hinge if it swings on one (a shut door's lock comes and
            // goes, and an open door stands elsewhere than shut), or else the first other joint to the body. A spring is meant
            // to stretch.
            private Joint HoldingJoint(Joint[] joints)
            {
                Joint holding = null;
                for (int j = 0; joints != null && j < joints.Length; j++)
                {
                    Joint joint = joints[j];

                    // Unity's null check is also true for a destroyed object.
                    if (joint == null || joint.connectedBody != Body || joint is SpringJoint)
                    {
                        continue;
                    }

                    if (joint is HingeJoint)
                    {
                        return joint;
                    }

                    if (holding == null)
                    {
                        holding = joint;
                    }
                }

                return holding;
            }

            /// <summary>Where it was handed over to this machine: where it goes back to if its first steps throw it.</summary>
            public void NoteHandOverPlace()
            {
                handOverAt = Root.position;
                handOverTurn = Root.rotation;
                handOverKnown = true;
                throwBacks = 0;
            }

            /// <summary>
            /// Whether a change of speed or spin in one step, just after the hand-over, is one no crash gives; if so the
            /// vehicle is put back where it was handed over, at rest, with its parts on it. At most <see cref="ThrowBacksMost"/>
            /// times for one hand-over.
            /// </summary>
            public bool ThrowBackIfThrown(Vector3 velocityChange, Vector3 spinChange)
            {
                if (!handOverKnown || Following || throwBacks >= ThrowBacksMost
                    || (velocityChange.magnitude <= ThrowBack && spinChange.magnitude <= ThrowBackSpin && IsNumber(Body.position)))
                {
                    return false;
                }

                throwBacks++;
                Root.position = handOverAt;
                Root.rotation = handOverTurn;
                Body.velocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                PutPartsBackNow(null);
                return true;
            }

            /// <summary>The hand-over is over: a later throw is the vehicle's own business.</summary>
            public void ForgetHandOverPlace()
            {
                handOverKnown = false;
            }

            // A probe on each part that has joints: Unity tells it when one breaks.
            private void WatchJointBreaks()
            {
                for (int i = 0; i < parts.Length && i < partOwnJoints.Length; i++)
                {
                    Rigidbody part = parts[i];
                    if (part == null || partOwnJoints[i] == null || partOwnJoints[i].Length == 0 || part.GetComponent<JointBreakProbe>() != null)
                    {
                        continue;
                    }

                    JointBreakProbe probe = part.gameObject.AddComponent<JointBreakProbe>();
                    Tracked self = this;
                    string name = part.name;
                    probe.Broke = force => self.OnJointBreak(name, force);
                    breakProbes.Add(probe);
                }
            }

            private void OnJointBreak(string part, float force)
            {
                breaksSaid++;
                if (breaksSaid > BreaksLogged)
                {
                    return;
                }

                // Unity's null check is also true for a destroyed object.
                string speed = Body == null ? "?" : Body.velocity.magnitude.ToString("0.#", CultureInfo.InvariantCulture);
                log.Info(
                    LogCategory.Vehicle,
                    "'" + part + "' of '" + Root.name + "' came off a joint at " + force.ToString("0", CultureInfo.InvariantCulture) + " N, on a machine that "
                    + (Following ? "follows the vehicle (" + (InContact ? "a copy with its own physics" : "kinematic") + (Ridden ? ", the local player aboard" : string.Empty) + ")" : "simulates it")
                    + ", at " + speed + " m/s, " + (lastStep.magnitude * 50f).ToString("0.#", CultureInfo.InvariantCulture) + " m/s in its last step"
                    + (breaksSaid == BreaksLogged ? " (no more of these lines for it)." : "."));
            }

            /// <summary>Takes the mod's probes off its parts: the session is over, or the world is.</summary>
            public void DropBreakProbes()
            {
                for (int i = 0; i < breakProbes.Count; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (breakProbes[i] != null)
                    {
                        breakProbes[i].Broke = null;
                        UnityEngine.Object.Destroy(breakProbes[i]);
                    }
                }

                breakProbes.Clear();
            }

            private static bool IsNumber(Vector3 value)
            {
                return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
                    && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
            }

            private static string Metres(float metres)
            {
                return metres > 1e5f || float.IsNaN(metres) ? "an absurd distance" : metres.ToString("0.##", CultureInfo.InvariantCulture) + " m";
            }
        }
    }

    /// <summary>Tells <see cref="VehiclePresenter"/> when a joint of the part it is on breaks.</summary>
    internal sealed class JointBreakProbe : MonoBehaviour
    {
        public System.Action<float> Broke;

        private void OnJointBreak(float breakForce)
        {
            System.Action<float> broke = Broke;
            if (broke != null)
            {
                broke(breakForce);
            }
        }
    }
}
