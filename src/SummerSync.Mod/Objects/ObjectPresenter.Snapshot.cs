using System.Globalization;
using System.Text;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>The loose objects as this game has them, for a bug report's snapshot.</summary>
    internal sealed partial class ObjectPresenter
    {
        private const int SnapshotMoving = 40;
        private const int SnapshotNear = 30;
        private const float SnapshotNearDistance = 4f;
        private const int SnapshotBelow = 20;
        private const float SnapshotBelowY = -10f;

        /// <summary>
        /// What the local hand holds and took last, what anyone holds, what moves, what lies within reach of the local player
        /// (who runs it, whether it is kinematic and why), and what lies under the map.
        /// </summary>
        public void Snapshot(StringBuilder text, Transform localPlayer)
        {
            ObjectReplication replication = current;
            text.Append("Loose objects: ").Append(tracked.Count).Append(" tracked here");
            if (replication == null)
            {
                text.Append(", no session with a world\n");
            }
            else
            {
                text.Append(", ").Append(replication.Shared).Append(" shared, this game simulates ").Append(replication.Simulated).Append(", ")
                    .Append(replication.Moving).Append(" moving, ").Append(replication.MissingHere).Append(" of the host's world missing here, ")
                    .Append(replication.OnlyHere).Append(" only here\n");
            }

            Tracked inHand = null;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Body != null && replication != null && tracked[i].Shared == replication.InHand)
                {
                    inHand = tracked[i];
                }
            }

            text.Append("  In your hand: ").Append(inHand == null ? "nothing shared" : SnapDescribe(inHand, localPlayer));
            text.Append("; the game's hand ").Append(hand == null ? "not found" : hand.connectedBody == null ? "holds nothing" : "holds '" + hand.connectedBody.name + "'").Append('\n');
            if (handTook != null)
            {
                text.Append("  Last taken by your hand: '").Append(handTook.Shared.Name).Append("', ")
                    .Append(SnapNumber((float)(frameNow - handTookAt))).Append(" s ago, from ").Append(SnapPlace(handTookFrom))
                    .Append(", run by ").Append(handTookWhose ?? "?").Append(" then\n");
            }

            int held = 0;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked thing = tracked[i];
                if (thing.Body != null && thing.Shared.HasId && thing.Shared.Held && thing != inHand)
                {
                    text.Append(held == 0 ? "  Held by someone:\n" : string.Empty).Append("    ").Append(SnapDescribe(thing, localPlayer)).Append('\n');
                    held++;
                }
            }

            int moving = 0;
            for (int i = 0; i < tracked.Count && moving < SnapshotMoving; i++)
            {
                Tracked thing = tracked[i];
                if (thing.Body == null || thing.Shared.Held)
                {
                    continue;
                }

                bool moves = thing.Shared.Role == BodyRole.Simulate ? !thing.Body.isKinematic && !thing.Body.IsSleeping() && thing.Body.velocity.sqrMagnitude > 0.01f
                    : thing.Shared.Role == BodyRole.Follow && !thing.Shared.AtRest;
                if (moves)
                {
                    text.Append(moving == 0 ? "  Moving:\n" : string.Empty).Append("    ").Append(SnapDescribe(thing, localPlayer)).Append('\n');
                    moving++;
                }
            }

            if (localPlayer != null)
            {
                int near = 0;
                for (int i = 0; i < tracked.Count && near < SnapshotNear; i++)
                {
                    Tracked thing = tracked[i];
                    if (thing.Body != null && (thing.Transform.position - localPlayer.position).magnitude <= SnapshotNearDistance)
                    {
                        text.Append(near == 0 ? "  Within " + SnapshotNearDistance.ToString(CultureInfo.InvariantCulture) + " m of you:\n" : string.Empty)
                            .Append("    ").Append(SnapDescribe(thing, localPlayer)).Append('\n');
                        near++;
                    }
                }
            }

            int below = 0;
            for (int i = 0; i < tracked.Count && below < SnapshotBelow; i++)
            {
                Tracked thing = tracked[i];
                if (thing.Body != null && thing.Transform.position.y < SnapshotBelowY)
                {
                    text.Append(below == 0 ? "  Under the map (y below " + SnapshotBelowY.ToString(CultureInfo.InvariantCulture) + "):\n" : string.Empty)
                        .Append("    ").Append(SnapDescribe(thing, localPlayer)).Append('\n');
                    below++;
                }
            }
        }

        // "#12 'beer case(itemx)' (beercase0): run by the host, followed here, at rest, kinematic (the mod's), layer 19, at (…), 1.2 m".
        private string SnapDescribe(Tracked thing, Transform localPlayer)
        {
            SharedBody shared = thing.Shared;
            Rigidbody body = thing.Body;
            var text = new StringBuilder(200);
            text.Append('#').Append(shared.HasId ? shared.Id.ToString(CultureInfo.InvariantCulture) : "-").Append(" '").Append(shared.Name).Append('\'');
            if (!string.IsNullOrEmpty(shared.Key))
            {
                text.Append(" (").Append(shared.Key).Append(')');
            }

            text.Append(": ").Append(shared.Role == BodyRole.Simulate ? "simulated here" : shared.Role == BodyRole.Follow ? "followed here" : "not shared")
                .Append(", run by ").Append(shared.Owner == Protocol.HostPlayerId ? "the host" : "player " + shared.Owner.ToString(CultureInfo.InvariantCulture))
                .Append(shared.Held ? ", in its owner's hand" : string.Empty)
                .Append(shared.Role == BodyRole.Follow ? (shared.AtRest ? ", shown at rest" : ", shown on its way") : string.Empty)
                .Append(shared.PendingClaim ? ", CLAIM PENDING" : string.Empty)
                .Append(shared.HasHandOff ? ", HAND-OVER WAITING" : string.Empty)
                .Append(body.isKinematic ? (thing.MadeKinematic ? ", kinematic (the mod's)" : ", kinematic (the game's)") : ", with its physics")
                .Append(thing.Ghost ? ", collisions off near a vehicle" : string.Empty)
                .Append(thing.FrozenFar ? ", frozen far away" : string.Empty)
                .Append(thing.Hidden ? ", HIDDEN (not in the host's world)" : string.Empty)
                .Append(thing.PutAway ? ", put away by the hand" : string.Empty)
                .Append(thing.ShownOn != 0 ? ", shown on vehicle #" + thing.ShownOn.ToString(CultureInfo.InvariantCulture) : string.Empty)
                .Append(", layer ").Append(thing.Transform.gameObject.layer)
                .Append(", at ").Append(SnapPlace(thing.Transform.position));
            if (!body.isKinematic)
            {
                text.Append(", speed ").Append(SnapNumber(body.velocity.magnitude)).Append(" m/s");
            }

            if (localPlayer != null)
            {
                text.Append(", ").Append(SnapNumber((thing.Transform.position - localPlayer.position).magnitude)).Append(" m from you");
            }

            return text.ToString();
        }

        private static string SnapPlace(Vector3 at)
        {
            return "(" + SnapNumber(at.x) + ", " + SnapNumber(at.y) + ", " + SnapNumber(at.z) + ")";
        }

        private static string SnapNumber(float value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
