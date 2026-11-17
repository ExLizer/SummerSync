using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using SummerSync.Core.Replication;
using UnityEngine;

namespace SummerSync.Unity.Vehicles
{
    /// <summary>
    /// How evenly the vehicles another machine runs are shown here, and whether the one the local player drives is ever put
    /// somewhere its speed does not take it, with the place it happened: a window of following that was rough goes to the log
    /// (<see cref="FollowQuality"/>), and so does a step of the driven vehicle that its physics does not explain.
    ///
    /// Why (docs/GAME_NOTES.md, "El rubber banding cerca de los edificios", 2026-11-17): players see vehicles snap back near
    /// the house, the store and the repair shop, and the log could not say who saw it, where, or whether the stream, a hitch
    /// of a game or something moving the vehicle was to blame.
    /// </summary>
    internal sealed partial class VehiclePresenter
    {
        // Rough windows written down per session, and steps of the driven vehicle its speed does not explain.
        private const int RoughLogged = 40;
        private const int PutLogged = 20;
        private const float PutLogEvery = 2f;

        // A step of the driven vehicle this much off what its speed takes it in a step is not physics.
        private const float PutBeyond = 0.5f;

        // The places players name, by the game's own spawn points for them (the debug console's "Spawn to …" states).
        private static readonly string[] LandmarkNames = { "home", "Teimo's store and pub", "Fleetari's repair shop", "the drag strip", "the cottage" };
        private static readonly string[] LandmarkPaths = { "YARD/SpawnToHome", "STORE/SpawnToStore", "REPAIRSHOP/SpawnToRepair", "DRAGRACE/SpawnToDrag", "COTTAGE/SpawnToCottage" };
        private readonly Transform[] landmarks = new Transform[LandmarkPaths.Length];
        private float nextLandmarkLook;

        private int roughSaid;
        private float longestFrame;
        private Tracked drivenStepOf;
        private Vector3 drivenStepAt;
        private Vector3 drivenStepSpeed;
        private int putSaid;
        private int puts;
        private float nextPutLog;

        // In FixedUpdate, after a step of a followed vehicle that moves.
        private static void NoteFollowStep(Tracked vehicle)
        {
            SnapshotBuffer<BodyPose> buffer = vehicle.Shared.Buffer;
            // A step of 2 mm is 0.1 m/s: less, the vehicle stood there.
            bool moving = vehicle.JoltedNow || vehicle.LastStepLength > 0.002f;
            vehicle.Quality.Step(buffer == null ? SnapshotSource.None : buffer.Source, vehicle.JoltedNow, vehicle.JoltSize, vehicle.JoltAfter, moving);
        }

        // Once a frame: the windows of following that end now, written down if rough.
        private void NoteFollowQuality(double now)
        {
            float frame = Time.unscaledDeltaTime;
            if (frame > longestFrame)
            {
                longestFrame = frame;
            }

            bool closed = false;
            for (int i = 0; i < tracked.Count; i++)
            {
                Tracked vehicle = tracked[i];
                SharedBody shared = vehicle.Shared;
                if (!shared.HasId || shared.Role != BodyRole.Follow || vehicle.InContact)
                {
                    vehicle.Quality.Stop();
                    continue;
                }

                FollowReport report;
                if (!vehicle.Quality.Close(now, FollowCounters.Of(shared.Buffer), out report))
                {
                    continue;
                }

                closed = true;
                if (!report.Rough || roughSaid >= RoughLogged)
                {
                    continue;
                }

                roughSaid++;
                Vector3 at = vehicle.Root.position;
                log.Info(
                    LogCategory.Vehicle,
                    "Follow: '" + vehicle.Root.name + "', run by player " + shared.Owner + ", was shown unevenly here over " + report.Describe() + ". At "
                    + Place(at) + ", " + Metres(player == null ? 0f : (at - player.position).magnitude) + " from this player, "
                    + vehicle.Quality.RoughWindows + " rough window(s) of " + vehicle.Quality.Windows + "; this game's longest frame meanwhile "
                    + (longestFrame * 1000f).ToString("0", CultureInfo.InvariantCulture) + " ms" + (roughSaid == RoughLogged ? " (no more of these lines this session)." : "."));
            }

            if (closed)
            {
                longestFrame = 0f;
            }
        }

        // In FixedUpdate: whether the vehicle the local player drives moved in the last step as its speed takes it. Something
        // that writes its place (the game or the mod) shows as a step its speed does not explain.
        private void NoteDrivenStep()
        {
            Tracked vehicle = driven;
            if (vehicle == null || vehicle.Body == null || vehicle.Body.isKinematic || vehicle.Shared.Role != BodyRole.Simulate)
            {
                drivenStepOf = null;
                return;
            }

            Rigidbody body = vehicle.Body;
            Vector3 at = body.position;
            Vector3 speed = body.velocity;
            if (drivenStepOf == vehicle)
            {
                Vector3 expected = (drivenStepSpeed + speed) * (0.5f * Time.fixedDeltaTime);
                Vector3 moved = at - drivenStepAt;
                float off = (moved - expected).magnitude;
                if (off > PutBeyond)
                {
                    puts++;
                    if (putSaid < PutLogged && Time.time >= nextPutLog)
                    {
                        putSaid++;
                        nextPutLog = Time.time + PutLogEvery;
                        log.Warning(
                            LogCategory.Vehicle,
                            "Driving: '" + vehicle.Root.name + "' went " + Metres(moved.magnitude) + " in a step of physics in which its speed ("
                            + speed.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s) takes it " + Metres(expected.magnitude)
                            + ": something put it " + Metres(off) + " off, at " + Place(at) + " (" + puts + " such step(s) this session)"
                            + (putSaid == PutLogged ? " (no more of these lines this session)." : "."));
                    }
                }
            }

            drivenStepOf = vehicle;
            drivenStepAt = at;
            drivenStepSpeed = speed;
        }

        // The nearest of the places players name, and the map coordinates.
        private string Place(Vector3 at)
        {
            if (Time.time >= nextLandmarkLook)
            {
                nextLandmarkLook = Time.time + 30f;
                for (int i = 0; i < landmarks.Length; i++)
                {
                    // Unity's null check is also true for a destroyed object.
                    if (landmarks[i] == null)
                    {
                        GameObject found = GameObject.Find(LandmarkPaths[i]);
                        landmarks[i] = found == null ? null : found.transform;
                    }
                }
            }

            int nearest = -1;
            float best = float.MaxValue;
            for (int i = 0; i < landmarks.Length; i++)
            {
                if (landmarks[i] == null)
                {
                    continue;
                }

                Vector3 gap = landmarks[i].position - at;
                gap.y = 0f;
                float distance = gap.magnitude;
                if (distance < best)
                {
                    best = distance;
                    nearest = i;
                }
            }

            string where = "(" + at.x.ToString("0", CultureInfo.InvariantCulture) + ", " + at.z.ToString("0", CultureInfo.InvariantCulture) + ")";
            return nearest < 0 ? where : where + ", " + Metres(best) + " from " + LandmarkNames[nearest];
        }

        private static string Metres(float value)
        {
            return value.ToString(value < 1f ? "0.00" : value < 100f ? "0.0" : "0", CultureInfo.InvariantCulture) + " m";
        }
    }
}
