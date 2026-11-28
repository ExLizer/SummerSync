using System.Globalization;
using System.Text;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>The vehicles as this game has them, for a bug report's snapshot.</summary>
    internal sealed partial class VehiclePresenter
    {
        // How near the local player a vehicle has to be for the whole "ss car" description to go in.
        private const float SnapshotDetailDistance = 40f;

        // How far down a vehicle is looked under for something to stand on, and the parent of the map's ground meshes.
        private const float SnapshotFall = 50f;
        private const string TerrainParent = "TERRAIN_OBJ";

        /// <summary>
        /// One line per shared vehicle (who runs it, who is at the wheel, where, how fast, whether it is under the ground),
        /// and the whole "ss car" description of the ones near the local player.
        /// </summary>
        public void Snapshot(StringBuilder text, Transform localPlayer)
        {
            text.Append("Vehicles: ").Append(tracked.Count).Append(" shared here").Append(current == null ? " (no session with a world)" : string.Empty).Append('\n');
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];

                // Unity's null check is also true for a destroyed object.
                if (vehicle.Body == null || vehicle.Root == null)
                {
                    text.Append("  (one destroyed)\n");
                    continue;
                }

                SharedBody shared = vehicle.Shared;
                Vector3 at = vehicle.Root.position;
                text.Append("  #").Append(shared.HasId ? shared.Id.ToString(CultureInfo.InvariantCulture) : "-").Append(" '").Append(vehicle.Root.name).Append("': ")
                    .Append(shared.Role == BodyRole.Simulate ? "its physics runs in this game" : shared.Role == BodyRole.Follow ? "followed, run by " + SnapOwner(shared.Owner) : "not shared")
                    .Append(shared.Held ? ", someone at the wheel" : ", nobody at the wheel")
                    .Append(", at ").Append(SnapPlace(at)).Append(", speed ")
                    .Append(SnapNumber(vehicle.Body.isKinematic ? vehicle.Speed : vehicle.Body.velocity.magnitude)).Append(" m/s")
                    .Append(vehicle.Body.isKinematic ? ", kinematic" : string.Empty);
                if (localPlayer != null)
                {
                    text.Append(", ").Append(SnapNumber((at - localPlayer.position).magnitude)).Append(" m from you");
                }

                if (vehicle == driven)
                {
                    text.Append(", YOU DRIVE IT");
                }
                else if (vehicle == ridden)
                {
                    text.Append(", you ride in it");
                }

                float ground;
                bool terrain = TerrainAt(vehicle.Root, out ground);
                if (terrain && ground > at.y + 1f)
                {
                    text.Append(", UNDER THE GROUND by ").Append(SnapNumber(ground - at.y)).Append(" m (the terrain at y ").Append(SnapNumber(ground)).Append(')');
                }

                if (!SomethingUnder(vehicle.Root))
                {
                    text.Append(", NOTHING UNDER IT within ").Append(SnapshotFall.ToString(CultureInfo.InvariantCulture)).Append(" m");
                }

                text.Append('\n');
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                if (vehicle.Body == null || vehicle.Root == null || localPlayer == null)
                {
                    continue;
                }

                bool near = vehicle == driven || vehicle == ridden || (vehicle.Root.position - localPlayer.position).magnitude <= SnapshotDetailDistance;
                if (near)
                {
                    text.Append("Near you, as \"ss car\" says it: ").Append(vehicle.DescribeState(localPlayer)).Append('\n');
                }
            }
        }

        // The map's ground where the vehicle is: the highest point of the terrain (the meshes under "MAP/MESH/TERRAIN_OBJ",
        // docs/GAME_NOTES.md) that a ray from high above meets there. Buildings and roofs are not the ground.
        private static bool TerrainAt(Transform root, out float ground)
        {
            ground = float.NegativeInfinity;
            Vector3 at = root.position;
            RaycastHit[] hits = Physics.RaycastAll(new Vector3(at.x, at.y + 1000f, at.z), Vector3.down, 2500f);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider != null && !collider.isTrigger && collider.transform.parent != null && collider.transform.parent.name == TerrainParent
                    && hits[i].point.y > ground)
                {
                    ground = hits[i].point.y;
                }
            }

            return !float.IsNegativeInfinity(ground);
        }

        // Whether anything solid that is not the vehicle lies under it within a fall's reach: a vehicle that went through the
        // map has nothing.
        private static bool SomethingUnder(Transform root)
        {
            Vector3 at = root.position;
            RaycastHit[] hits = Physics.RaycastAll(at + (Vector3.up * 0.5f), Vector3.down, SnapshotFall);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider != null && !collider.isTrigger && !collider.transform.IsChildOf(root))
                {
                    return true;
                }
            }

            return false;
        }

        private static string SnapOwner(byte owner)
        {
            return owner == Protocol.HostPlayerId ? "the host" : "player " + owner.ToString(CultureInfo.InvariantCulture);
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
