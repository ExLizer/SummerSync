using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// The first moments after a vehicle's body starts to run its own physics on this machine (it was a kinematic copy of
    /// another machine's until then): measured, and with a limit on how hard it is pushed out of what it overlaps.
    ///
    /// Why: two logs of one session (2026-11-11) show the Satsuma thrown away within two steps of physics of that moment,
    /// its jointed parts torn off and its driver killed, on one machine only. For a second and a half after the moment, this
    /// writes what else is inside the box of the vehicle's colliders, which of its parts stand off their joints, the first
    /// things its body runs into, and any step in which its speed changes more than a crash would change it. A third log
    /// (2026-11-13, the host's, with the guest's lines) says what hit it: the guest took the wheel, the beer case and four
    /// empty bottles that rode in the car started their own physics inside it in the same frame, the car ran into them in
    /// the next step and left at 592,953 m/s. Unity pushes two bodies that overlap apart as fast as it takes, with no limit
    /// unless one is set (Rigidbody.maxDepenetrationVelocity), and the game sets none.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const float WatchSeconds = 1.5f;

        // A change of speed in one step of physics that no engine, brake or bump gives: 4 m/s in 0.02 s is 20 g.
        private const float WatchJump = 4f;
        private const int WatchMostHits = 6;
        private const int WatchMostJumps = 3;

        // While it is watched, the vehicle and its parts are pushed out of anything they overlap at 1 m/s at most: a few
        // centimetres in a few steps, where Unity's default puts any overlap right in one step, whatever the speed that takes.
        internal const float StartDepenetration = 1f;

        /// <summary>Test aid ("ss pushout off"): that limit is not held, as before it was, to see the difference.</summary>
        internal static bool LimitPushOut = true;

        // A joint whose two ends stand farther apart than this is named in the log.
        private const float StrainShown = 0.01f;
        private const int StrainMostShown = 6;

        private readonly List<Tracked> handedOver = new List<Tracked>();

        // The vehicle's body has just been given its own physics here.
        private void Watch(Tracked vehicle, string why)
        {
            if (vehicle.Body == null)
            {
                return;
            }

            vehicle.BeginWatch(Time.fixedTime + WatchSeconds, OnWatchedHit);
            if (!handedOver.Contains(vehicle))
            {
                handedOver.Add(vehicle);
            }

            var text = new StringBuilder(500);
            text.Append("Hand-over: '").Append(vehicle.Root.name).Append("' has its own physics here from now (").Append(why).Append("): speed ")
                .Append(Number(vehicle.Body.velocity.magnitude)).Append(" m/s, turning ").Append(Number(vehicle.Body.angularVelocity.magnitude))
                .Append(" rad/s, mass ").Append(Number(vehicle.Body.mass)).Append(" kg. Inside its box, not part of it: ");
            vehicle.DescribeInside(text, player);
            log.Info(LogCategory.Vehicle, text.ToString());
            LogStrain(vehicle, false);
        }

        // Its own line: the one above is often cut where another machine's lines are kept (LogRelay).
        private void LogStrain(Tracked vehicle, bool warn)
        {
            var text = new StringBuilder(300);
            text.Append("Hand-over: '").Append(vehicle.Root.name).Append("' is pushed out of what it overlaps at ")
                .Append(LimitPushOut ? Number(StartDepenetration) + " m/s at most for " + Number(WatchSeconds) + " s" : "any speed (test aid: no limit held)")
                .Append(" (the game's limit: ").Append(Limit(vehicle.DepenetrationBefore)).Append("); its parts off their joints: ");
            vehicle.DescribeStrain(text);
            if (warn)
            {
                log.Warning(LogCategory.Vehicle, text.ToString());
            }
            else
            {
                log.Info(LogCategory.Vehicle, text.ToString());
            }
        }

        /// <summary>
        /// The first solid collider of that vehicle that a thing's middle is inside of (its name), for the log; null if none
        /// or if the vehicle is not known.
        /// </summary>
        public string SolidAround(ushort vehicleId, Rigidbody thing)
        {
            Transform root = RootOf(vehicleId);
            return root == null || thing == null ? null : Tracked.SolidAround(root, thing);
        }

        // A limit of Unity's: "none" for the default, which is 1e32.
        internal static string Limit(float metresPerSecond)
        {
            return metresPerSecond > 1e6f ? "none" : Number(metresPerSecond) + " m/s";
        }

        // In FixedUpdate: the speed of each watched vehicle against the step before.
        private void WatchStep()
        {
            for (int i = handedOver.Count - 1; i >= 0; i--)
            {
                Tracked vehicle = handedOver[i];

                // Unity's null check is also true for a destroyed object.
                if (vehicle.Body == null || Time.fixedTime > vehicle.WatchUntil || vehicle.Body.isKinematic)
                {
                    vehicle.EndWatch();
                    handedOver.RemoveAt(i);
                    continue;
                }

                Vector3 now = vehicle.Body.velocity;
                Vector3 spin = vehicle.Body.angularVelocity;
                if (vehicle.WatchStarted && vehicle.WatchJumps < WatchMostJumps
                    && ((now - vehicle.WatchVelocity).magnitude > WatchJump || (spin - vehicle.WatchSpin).magnitude > WatchJump))
                {
                    vehicle.WatchJumps++;
                    var text = new StringBuilder(500);
                    text.Append("Hand-over: '").Append(vehicle.Root.name).Append("' was THROWN in one step of physics, ")
                        .Append(Number(Time.fixedTime - (vehicle.WatchUntil - WatchSeconds))).Append(" s after it got its own physics here: speed ")
                        .Append(Number(vehicle.WatchVelocity.magnitude)).Append(" to ").Append(Number(now.magnitude)).Append(" m/s (change (")
                        .Append(Number(now.x - vehicle.WatchVelocity.x)).Append(", ").Append(Number(now.y - vehicle.WatchVelocity.y)).Append(", ")
                        .Append(Number(now.z - vehicle.WatchVelocity.z)).Append(")), turning ").Append(Number(vehicle.WatchSpin.magnitude)).Append(" to ")
                        .Append(Number(spin.magnitude)).Append(" rad/s. Inside its box, not part of it: ");
                    vehicle.DescribeInside(text, player);
                    log.Warning(LogCategory.Vehicle, text.ToString());
                    LogStrain(vehicle, true);
                }

                vehicle.WatchVelocity = now;
                vehicle.WatchSpin = spin;
                vehicle.WatchStarted = true;
            }
        }

        private void OnWatchedHit(Tracked vehicle, Collision collision)
        {
            if (vehicle.WatchHits >= WatchMostHits || vehicle.Body == null || Time.fixedTime > vehicle.WatchUntil)
            {
                return;
            }

            vehicle.WatchHits++;
            Collider other = collision.collider;
            Rigidbody body = other == null ? null : other.attachedRigidbody;
            var text = new StringBuilder(300);
            text.Append("Hand-over: '").Append(vehicle.Root.name).Append("', ").Append(Number(Time.fixedTime - (vehicle.WatchUntil - WatchSeconds)))
                .Append(" s after it got its own physics here, ran into '").Append(other == null ? "?" : PathOf(other.transform)).Append("' (")
                .Append(other == null ? "?" : other.GetType().Name).Append(", layer ").Append(other == null ? -1 : other.gameObject.layer);
            if (body == null)
            {
                text.Append(", no body: the world");
            }
            else
            {
                text.Append(", body '").Append(body.name).Append("' ").Append(Number(body.mass)).Append(" kg").Append(body.isKinematic ? ", KINEMATIC" : ", gives way");
            }

            text.Append(") at ").Append(Number(collision.relativeVelocity.magnitude)).Append(" m/s");
            if (collision.contacts != null && collision.contacts.Length > 0)
            {
                Vector3 at = vehicle.Root.InverseTransformPoint(collision.contacts[0].point);
                text.Append(", at (").Append(Number(at.x)).Append(", ").Append(Number(at.y)).Append(", ").Append(Number(at.z)).Append(") of its own coordinates");
            }

            text.Append('.');
            log.Info(LogCategory.Vehicle, text.ToString());
        }

        private static string PathOf(Transform at)
        {
            string path = at.name;
            for (int depth = 0; depth < 3 && at.parent != null; depth++)
            {
                at = at.parent;
                path = at.name + "/" + path;
            }

            return path;
        }

        private static string Number(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private sealed partial class Tracked
        {
            private VehicleHandoverProbe watchProbe;

            // The bodies whose limit on being pushed out of an overlap is held while the vehicle is watched, and theirs
            // before (the vehicle's own is DepenetrationBefore).
            private Rigidbody[] depenetrationParts;
            private float[] depenetrationPartsBefore;

            public float DepenetrationBefore { get; private set; }

            public float WatchUntil { get; private set; }

            public bool WatchStarted { get; set; }

            public Vector3 WatchVelocity { get; set; }

            public Vector3 WatchSpin { get; set; }

            public int WatchHits { get; set; }

            public int WatchJumps { get; set; }

            public void BeginWatch(float until, System.Action<Tracked, Collision> hit)
            {
                WatchUntil = until;
                WatchStarted = false;
                WatchHits = 0;
                WatchJumps = 0;
                if (watchProbe == null)
                {
                    watchProbe = Root.gameObject.AddComponent<VehicleHandoverProbe>();
                    Tracked self = this;
                    watchProbe.Hit = collision => hit(self, collision);
                }

                watchProbe.enabled = true;
                HoldDepenetration();
            }

            public void EndWatch()
            {
                WatchUntil = 0f;

                // Unity's null check is also true for a destroyed component.
                if (watchProbe != null)
                {
                    watchProbe.enabled = false;
                }

                ReleaseDepenetration();
            }

            /// <summary>
            /// Appends the parts that stand off where their joint holds them, the farthest first: for a hinge (or any joint
            /// with anchors) how far apart its two anchors are, for a fixed joint how far the part is from where it was on the
            /// vehicle when its parts were last looked for (a fixed joint keeps the pose it was made in, not its anchors).
            /// A spring is meant to stretch, and a switched-off part holds nothing.
            /// </summary>
            public void DescribeStrain(StringBuilder text)
            {
                var apart = new List<KeyValuePair<float, string>>();
                for (int i = 0; i < parts.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (part == null || !part.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    Joint[] joints = part.GetComponents<Joint>();
                    for (int j = 0; j < joints.Length; j++)
                    {
                        Joint joint = joints[j];
                        if (joint.connectedBody == null || joint is SpringJoint)
                        {
                            continue;
                        }

                        float distance = joint is FixedJoint
                            ? (Root.TransformPoint(partHome[i]) - part.transform.position).magnitude
                            : (joint.transform.TransformPoint(joint.anchor) - joint.connectedBody.transform.TransformPoint(joint.connectedAnchor)).magnitude;
                        if (distance > StrainShown)
                        {
                            apart.Add(new KeyValuePair<float, string>(distance, "'" + joint.name + "' (" + joint.GetType().Name + " to '" + joint.connectedBody.name + "')"));
                        }
                    }
                }

                if (apart.Count == 0)
                {
                    text.Append("none.");
                    return;
                }

                apart.Sort((a, b) => b.Key.CompareTo(a.Key));
                for (int i = 0; i < apart.Count && i < StrainMostShown; i++)
                {
                    text.Append(i == 0 ? string.Empty : "; ").Append(apart[i].Value).Append(' ').Append(Number(apart[i].Key * 100f)).Append(" cm");
                }

                text.Append(apart.Count > StrainMostShown ? "; " + (apart.Count - StrainMostShown) + " more." : ".");
            }

            /// <summary>
            /// The first solid collider below <paramref name="root"/> that the middle of one of the thing's colliders is in, on a
            /// layer that collides with the thing's (the car's hull, "CarCollider", is on a layer that loose things go through).
            /// </summary>
            public static string SolidAround(Transform root, Rigidbody thing)
            {
                Collider[] own = thing.GetComponentsInChildren<Collider>();
                for (int i = 0; i < own.Length; i++)
                {
                    Collider collider = own[i];
                    if (collider.isTrigger || !collider.enabled)
                    {
                        continue;
                    }

                    Bounds bounds = collider.bounds;
                    Vector3 extents = bounds.extents;
                    float radius = Mathf.Max(0.005f, Mathf.Min(extents.x, Mathf.Min(extents.y, extents.z)) * 0.8f);
                    Collider[] around = Physics.OverlapSphere(bounds.center, radius);
                    for (int j = 0; j < around.Length; j++)
                    {
                        Collider other = around[j];
                        if (!other.isTrigger && other.attachedRigidbody != thing && other.transform.IsChildOf(root)
                            && !Physics.GetIgnoreLayerCollision(collider.gameObject.layer, other.gameObject.layer))
                        {
                            Rigidbody body = other.attachedRigidbody;
                            return body == null || body.transform == root ? other.name : other.name + "' of '" + body.name;
                        }
                    }
                }

                return null;
            }

            private void HoldDepenetration()
            {
                if (depenetrationParts != null)
                {
                    return;
                }

                DepenetrationBefore = Body.maxDepenetrationVelocity;
                if (!LimitPushOut)
                {
                    return;
                }

                Body.maxDepenetrationVelocity = Mathf.Min(DepenetrationBefore, StartDepenetration);
                depenetrationParts = parts;
                depenetrationPartsBefore = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    Rigidbody part = parts[i];

                    // Unity's null check is also true for a destroyed object.
                    if (part != null)
                    {
                        depenetrationPartsBefore[i] = part.maxDepenetrationVelocity;
                        part.maxDepenetrationVelocity = Mathf.Min(depenetrationPartsBefore[i], StartDepenetration);
                    }
                }
            }

            private void ReleaseDepenetration()
            {
                if (depenetrationParts == null)
                {
                    return;
                }

                // Unity's null check is also true for a destroyed object.
                if (Body != null)
                {
                    Body.maxDepenetrationVelocity = DepenetrationBefore;
                }

                for (int i = 0; i < depenetrationParts.Length; i++)
                {
                    if (depenetrationParts[i] != null)
                    {
                        depenetrationParts[i].maxDepenetrationVelocity = depenetrationPartsBefore[i];
                    }
                }

                depenetrationParts = null;
                depenetrationPartsBefore = null;
            }
        }
    }

    /// <summary>Tells <see cref="VehiclePresenter"/> what a vehicle's body runs into while it is watched after a hand-over.</summary>
    internal sealed class VehicleHandoverProbe : MonoBehaviour
    {
        public System.Action<Collision> Hit;

        private void OnCollisionEnter(Collision collision)
        {
            System.Action<Collision> hit = Hit;
            if (enabled && hit != null)
            {
                hit(collision);
            }
        }
    }
}
