using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// A measurement, not a fix: what happens to a vehicle in the first moments after its body starts to run its own physics
    /// on this machine (it was a kinematic copy of another machine's until then).
    ///
    /// Why: two logs of one session (2026-11-11) show the Satsuma thrown away within two steps of physics of that moment,
    /// its jointed parts torn off and its driver killed, on one machine only, and nothing in them says what hit it. For a
    /// second and a half after the moment, this writes what else is inside the box of the vehicle's colliders, the first
    /// things its body runs into, and any step in which its speed changes more than a crash would change it.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const float WatchSeconds = 1.5f;

        // A change of speed in one step of physics that no engine, brake or bump gives: 4 m/s in 0.02 s is 20 g.
        private const float WatchJump = 4f;
        private const int WatchMostHits = 6;
        private const int WatchMostJumps = 3;

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

            var text = new StringBuilder(400);
            text.Append("Hand-over: '").Append(vehicle.Root.name).Append("' has its own physics here from now (").Append(why).Append("): speed ")
                .Append(Number(vehicle.Body.velocity.magnitude)).Append(" m/s, turning ").Append(Number(vehicle.Body.angularVelocity.magnitude))
                .Append(" rad/s, mass ").Append(Number(vehicle.Body.mass)).Append(" kg. Inside its box, not part of it: ");
            vehicle.DescribeInside(text, player);
            log.Info(LogCategory.Vehicle, text.ToString());
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
            }

            public void EndWatch()
            {
                WatchUntil = 0f;

                // Unity's null check is also true for a destroyed component.
                if (watchProbe != null)
                {
                    watchProbe.enabled = false;
                }
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
