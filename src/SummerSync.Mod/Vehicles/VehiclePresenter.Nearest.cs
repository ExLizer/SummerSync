using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// A vehicle nobody drives is simulated by the machine of the player nearest to it.
    ///
    /// Why (docs/GAME_NOTES.md, "La llave, y por qué la camioneta puede no estar", and DECISIONS 2026-10-16): far from its
    /// player, a game switches off what hangs from a vehicle's "LOD" (its dashboard, the handbrake's lever, the starter's
    /// instruments). A parked vehicle went back to the host wherever the host was, and then the handbrake someone else
    /// pulled did not reach the wheels, and its engine could not be started or stopped.
    ///
    /// How: the replication of the vehicles already lets a client take what is near it from the host and give it back when
    /// it no longer is (<see cref="ObjectReplication.SetNear"/>, as with loose objects). Here "near" is "this player is the
    /// nearest to it", with a margin so that it does not go back and forth: a client asks for a vehicle at rest when it is
    /// <see cref="NearestMargin"/> nearer than the player whose machine has it, and keeps it until another player is that
    /// much nearer; then it goes back to the host at rest, and from there to whoever is nearest. Only at rest: every change
    /// of machine moves a vehicle a little. A vehicle someone drives or holds is theirs, as always.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        private const float NearestMargin = 30f;

        private readonly Dictionary<ushort, bool> nearestSaid = new Dictionary<ushort, bool>();

        // Once per frame on a client, after the vehicles are driven: which of them this player is the nearest to.
        private void UpdateNearest()
        {
            if (current == null || manager.Mode == Core.Net.NetworkMode.Host || manager.Session == null || player == null)
            {
                return;
            }

            byte me = manager.Session.LocalPlayerId;
            Vector3 here = player.position;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                SharedBody shared = vehicle.Shared;
                if (vehicle.Body == null || !shared.HasId || vehicle == driven || shared.Held)
                {
                    continue;
                }

                Vector3 at = vehicle.Root.position;
                float mine = (here - at).magnitude;
                float owner;
                float others = NearestOther(at, me, shared.Owner, out owner);
                bool near;
                if (shared.Role == BodyRole.Simulate)
                {
                    // Kept until another player is clearly nearer.
                    near = mine <= others + NearestMargin;
                }
                else
                {
                    // Asked for when clearly nearer than whoever has it, nearest of all, and only at rest.
                    near = mine + NearestMargin < owner && mine <= others && shared.AtRest && vehicle.AtRest;
                }

                bool had = shared.Role == BodyRole.Simulate;
                current.SetNear(shared, near);
                bool said;
                if (!nearestSaid.TryGetValue(shared.Id, out said) || said != near)
                {
                    nearestSaid[shared.Id] = near;
                    if (near != had)
                    {
                        log.Info(
                            LogCategory.Vehicle,
                            near
                                ? "'" + vehicle.Root.name + "' stands " + mine.ToString("0", CultureInfo.InvariantCulture) + " m from this player, the nearest; player "
                                    + shared.Owner + " has it " + Distance(owner) + " away: asked for, to be simulated here."
                                : "'" + vehicle.Root.name + "' is " + mine.ToString("0", CultureInfo.InvariantCulture) + " m from this player, and another is "
                                    + Distance(others) + " from it: it goes back to the host once it is at rest.");
                    }
                }
            }
        }

        // How far the nearest other player in the game world is from that point, and how far the given owner is (infinity
        // if not in the game world).
        private float NearestOther(Vector3 at, byte me, byte ownerId, out float owner)
        {
            owner = float.PositiveInfinity;
            float nearest = float.PositiveInfinity;
            IList<RemotePlayer> remotes = manager.Players == null ? null : manager.Players.Remotes;
            for (int i = 0; remotes != null && i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                if (!remote.InWorld || remote.Id == me)
                {
                    continue;
                }

                Vector3 where;
                if (!WhereIs(remote.Shown, out where))
                {
                    continue;
                }

                float distance = (where - at).magnitude;
                nearest = Mathf.Min(nearest, distance);
                if (remote.Id == ownerId)
                {
                    owner = distance;
                }
            }

            return nearest;
        }

        // A player's place in the world, from a pose that may be relative to a vehicle or to a moving NPC.
        private bool WhereIs(PlayerPose pose, out Vector3 where)
        {
            if (pose.Parent == 0)
            {
                where = new Vector3(pose.X, pose.Y, pose.Z);
                return true;
            }

            Tracked vehicle = pose.Parent < PlayerPose.NpcParentBase ? Find(pose.Parent) : null;
            if (vehicle != null && vehicle.Body != null)
            {
                where = vehicle.Root.TransformPoint(new Vector3(pose.X, pose.Y, pose.Z));
                return true;
            }

            where = Vector3.zero;
            return false;
        }

        private static string Distance(float metres)
        {
            return float.IsInfinity(metres) ? "out of the world" : metres.ToString("0", CultureInfo.InvariantCulture) + " m";
        }
    }
}
