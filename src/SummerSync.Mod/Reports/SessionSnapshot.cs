using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Unity.Objects;
using SummerSync.Unity.Vehicles;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity.Reports
{
    /// <summary>
    /// What the session looks like in this game at the moment a bug report is made, as text for the report's snapshot.txt
    /// and the log: the build and the frames; the session as "ss status" says it; each player with their place, vehicle,
    /// seat and what they hold; each vehicle with who runs it, where and how fast, and whether it is under the ground (and
    /// "ss car" for the ones near); the loose things held, moving, near the local player or under the map, with who runs
    /// each and why it is kinematic; the Satsuma's tyres; and the last slow frames.
    ///
    /// Why each part (the reports that could not be reproduced): a host whose things went back where they were when he
    /// took them (who ran each thing near him and whether a claim was pending), tyres that went flat in one game only (each
    /// hub's state and health, and which game runs the car), a trunk lid that shook (the bodies hanging from the vehicle and
    /// how far each is from its place), a vehicle under the map (the ground under each vehicle).
    /// </summary>
    internal static class SessionSnapshot
    {
        /// <summary>The snapshot; every part on its own, so that one that fails says so and the rest are still there.</summary>
        public static string Write(NetworkManager manager, VehiclePresenter vehicles, ObjectPresenter objects, Transform localPlayer)
        {
            var text = new StringBuilder(16 * 1024);
            text.Append("SummerSync bug report: the session as this game had it\n");
            text.Append("Time: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC (")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" here)\n");
            Part(text, "build", () => Build(text, manager));
            Part(text, "session", () =>
            {
                text.Append("Session (as \"ss status\" says it):\n");
                var status = new StringBuilder();
                ModRuntime.Network.AppendStatus(status);
                Indent(text, status.ToString());
                var world = new StringBuilder();
                HostWorld.AppendStatus(world);
                Indent(text, world.ToString());
            });
            Part(text, "players", () => Players(text, manager, localPlayer));
            Part(text, "vehicles", () => vehicles.Snapshot(text, localPlayer));
            Part(text, "loose objects", () => objects.Snapshot(text, localPlayer));
            Part(text, "the Satsuma's tyres", () => CarValueSync.AppendTyres(text));
            Part(text, "slow frames", () =>
            {
                text.Append("The last slow frames and minutes of memory (FrameMeter):\n");
                FrameMeter.AppendRecent(text);
            });
            return text.ToString();
        }

        private static void Part(StringBuilder text, string what, Action write)
        {
            try
            {
                write();
            }
            catch (Exception exception)
            {
                text.Append("(Describing ").Append(what).Append(" failed: ").Append(exception.GetType().Name).Append(": ").Append(exception.Message).Append(")\n");
            }
        }

        private static void Build(StringBuilder text, NetworkManager manager)
        {
            text.Append("Build: ").Append(BuildInfo.ProductName).Append(' ').Append(BuildInfo.InformationalVersion).Append(Edition.Mark)
                .Append(", MSCLoader ").Append(MSCLoader.ModLoader.MSCLoader_Ver).Append(", Unity ").Append(Application.unityVersion)
                .Append(", game ").Append(manager.GameVersion).Append(", protocol ").Append(Protocol.Version).Append('\n');
            text.Append("System: ").Append(SystemInfo.operatingSystem).Append(", ").Append(SystemInfo.processorType).Append(" x").Append(SystemInfo.processorCount)
                .Append(", ").Append(SystemInfo.systemMemorySize).Append(" MB, ").Append(SystemInfo.graphicsDeviceName).Append(" (").Append(SystemInfo.graphicsMemorySize)
                .Append(" MB), screen ").Append(Screen.width).Append('x').Append(Screen.height).Append('\n');
            float frame = Time.smoothDeltaTime;
            text.Append("Scene: ").Append(Application.loadedLevelName).Append(", ").Append(frame > 0f ? (1f / frame).ToString("0", CultureInfo.InvariantCulture) : "?")
                .Append(" frames per second, ").Append(Time.realtimeSinceStartup.ToString("0", CultureInfo.InvariantCulture)).Append(" s since the game started, the heap at ")
                .Append(GC.GetTotalMemory(false) / (1024 * 1024)).Append(" MB\n");
        }

        private static void Players(StringBuilder text, NetworkManager manager, Transform localPlayer)
        {
            ObjectReplication objects = manager.Objects;
            ObjectReplication vehicles = manager.Vehicles;
            text.Append("Players:\n");
            NetSession session = manager.Session;
            PlayerReplication local = manager.LocalPlayers;
            byte self = session == null ? Protocol.HostPlayerId : session.LocalPlayerId;
            text.Append("  You (player ").Append(self).Append(session == null ? ", no session" : session.Role == SessionRole.Host ? ", the host" : ", a guest").Append("): ");
            if (localPlayer == null)
            {
                text.Append("not in a world\n");
            }
            else
            {
                Vector3 at = localPlayer.position;
                text.Append("at ").Append(Place(at)).Append(localPlayer.parent != null ? ", under '" + localPlayer.parent.name + "'" : string.Empty);
                if (local != null)
                {
                    Pose(text, local.LocalPose, vehicles);
                }

                text.Append(", holding ").Append(Held(objects, self)).Append('\n');
            }

            PlayerReplication players = manager.Players;
            if (players == null)
            {
                return;
            }

            IList<RemotePlayer> remotes = players.Remotes;
            for (int i = 0; i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                text.Append("  '").Append(remote.Name).Append("' (player ").Append(remote.Id).Append("): ")
                    .Append(remote.InWorld ? "in the world" : "in the menu or loading")
                    .Append(remote.Visible ? ", shown" : ", not shown").Append(", heard ")
                    .Append((ModRuntime.Network.Now - remote.LastHeard).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s ago, ")
                    .Append(remote.StatesReceived).Append(" states");
                if (remote.HasState)
                {
                    PlayerPose pose = remote.Shown;
                    text.Append(", at ").Append(Place(new Vector3(pose.X, pose.Y, pose.Z))).Append(pose.Parent != 0 ? " (of vehicle #" + pose.Parent + ")" : string.Empty);
                    Pose(text, pose, vehicles);
                    if (localPlayer != null && pose.Parent == 0)
                    {
                        text.Append(", ").Append((new Vector3(pose.X, pose.Y, pose.Z) - localPlayer.position).magnitude.ToString("0.0", CultureInfo.InvariantCulture)).Append(" m from you");
                    }
                }

                text.Append(", holding ").Append(Held(objects, remote.Id)).Append('\n');
            }
        }

        // Where a pose says its player is: on foot, at a wheel, in a seat; crouching, drinking; the helmet.
        private static void Pose(StringBuilder text, PlayerPose pose, ObjectReplication vehicles)
        {
            if (pose.Parent != 0)
            {
                string name = VehicleName(vehicles, pose.Parent);
                text.Append(pose.Driving ? ", at the wheel of " : pose.Seat != 0 ? ", in seat " + pose.Seat + " of " : ", aboard ").Append(name);
                if (pose.Belted)
                {
                    text.Append(", belted");
                }
            }
            else
            {
                text.Append(", on foot");
            }

            if (pose.Activity != PlayerActivity.None)
            {
                text.Append(", ").Append(pose.Activity);
            }

            if (pose.Pushing)
            {
                text.Append(", pushing");
            }

            if (pose.Helmet)
            {
                text.Append(", helmet on");
            }
        }

        private static string VehicleName(ObjectReplication vehicles, ushort id)
        {
            SharedBody vehicle = vehicles == null ? null : vehicles.Find(id);
            return vehicle == null ? "vehicle #" + id : "'" + vehicle.Name + "' (#" + id + ")";
        }

        // What that player holds as the loose objects' replication has it: the shared things held with that owner.
        private static string Held(ObjectReplication objects, byte player)
        {
            if (objects == null)
            {
                return "(no shared objects here)";
            }

            var held = new StringBuilder();
            IList<SharedBody> things = objects.Locals;
            for (int i = 0; i < things.Count; i++)
            {
                SharedBody thing = things[i];
                if (thing.HasId && thing.Held && thing.Owner == player)
                {
                    held.Append(held.Length == 0 ? string.Empty : ", ").Append("'").Append(thing.Name).Append("' (#").Append(thing.Id).Append(')');
                }
            }

            return held.Length == 0 ? "nothing shared" : held.ToString();
        }

        private static void Indent(StringBuilder text, string lines)
        {
            string[] split = lines.Split('\n');
            for (int i = 0; i < split.Length; i++)
            {
                if (split[i].Length > 0)
                {
                    text.Append("  ").Append(split[i].TrimEnd('\r')).Append('\n');
                }
            }
        }

        private static string Place(Vector3 at)
        {
            return "(" + at.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " + at.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + at.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }
    }
}
