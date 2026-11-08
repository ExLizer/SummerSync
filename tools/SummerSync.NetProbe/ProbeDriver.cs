using System.Globalization;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Core.Vehicles;

namespace SummerSync.NetProbe;

/// <summary>
/// The vehicles of a probe, which has no game world: copies of the host's, and a driver who takes the first one
/// round in circles, lets it stand, and takes it again. To a game on the other end it is a player at the wheel.
///
/// The circle is flat, at the height the vehicle stood at. On a slope the vehicle floats or sinks, which is
/// enough to see whether it moves smoothly and whether whoever rides it stays in it.
/// </summary>
internal sealed class ProbeDriver
{
    public const string VanName = "HAYOSIKO(1500kg, 250)";

    private const double WaitSeconds = 6.0;
    private const double DriveSeconds = 24.0;
    private const double BrakeSeconds = 5.0;
    private const double Radius = 12.0;
    private const double TopSpeed = 8.0;
    private const double Acceleration = 2.0;

    private readonly bool drives;
    private readonly List<Mirror> vehicles = new();
    private bool listRequested;
    private Mirror? driven;
    private double phaseStarted = double.NaN;
    private double lastStep = double.NaN;
    private BodyPose start;
    private double startYaw;
    private double travelled;
    private double speed;

    public ProbeDriver(bool drives, string? only = null, double radius = Radius, double topSpeed = TopSpeed, float[]? seat = null, bool lesson = false)
    {
        this.drives = drives;
        this.only = only;
        radiusUsed = radius;
        topSpeedUsed = topSpeed;
        this.seat = seat;
        this.lesson = lesson;
        seats = LoadSeats();
    }

    // Where the driver's feet are in the vehicle and how high their eyes (given, or else from the vehicle's file, or else
    // the van's of old), and whether they work the controls as a person would (the wheel from side to side, each pedal in
    // turn, the clutch down to change gear) instead of holding them.
    private static readonly float[] AnySeat = { -0.33f, 0.4f, 0.8f, 0.9f };
    private readonly float[]? seat;
    private readonly bool lesson;
    private readonly Dictionary<string, float[]> seats;

    /// <summary>The repository's vehicle files, which say where the game seats a driver.</summary>
    public static string DefaultVehicles
    {
        get { return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "vehicles")); }
    }

    private static Dictionary<string, float[]> LoadSeats()
    {
        var found = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (!Directory.Exists(DefaultVehicles))
        {
            return found;
        }

        foreach (string file in Directory.GetFiles(DefaultVehicles, "*.json"))
        {
            VehicleDefinition vehicle = VehicleDefinition.Parse(File.ReadAllText(file));
            if (vehicle.Seat != null)
            {
                found[vehicle.RootName] = vehicle.Seat;
            }
        }

        return found;
    }

    // The circle driven, and how fast at most.
    private readonly double radiusUsed;
    private readonly double topSpeedUsed;

    // Only a vehicle whose name starts with this is taken ("SATSUMA"); any, if null.
    private readonly string? only;

    public int Drives { get; private set; }

    public int Refused { get; private set; }

    public int Count => vehicles.Count;

    /// <summary>While at the wheel: where the driver sits in the vehicle and what they do with it.</summary>
    public PlayerPose? Seat { get; private set; }

    /// <summary>As a host: a van of the probe's own, for another probe to drive.</summary>
    /// <summary>As a host: the vehicles of a file that <see cref="Save"/> wrote, standing where they were. Returns how many.</summary>
    public int Load(ObjectReplication shared, string path)
    {
        int count = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 8)
            {
                continue;
            }

            var n = new float[7];
            for (int i = 0; i < 7; i++)
            {
                n[i] = float.Parse(fields[i + 1], CultureInfo.InvariantCulture);
            }

            var pose = new BodyPose(n[0], n[1], n[2], n[3], n[4], n[5], n[6]);
            vehicles.Add(new Mirror(shared.AddLocal(fields[0], pose), pose));
            count++;
        }

        return count;
    }

    /// <summary>The vehicles here, name and place, one to a line, for <see cref="Load"/>. Returns how many.</summary>
    public int Save(string path)
    {
        var lines = new List<string>();
        foreach (Mirror vehicle in vehicles)
        {
            BodyPose p = vehicle.Pose;
            lines.Add(string.Join("\t", vehicle.Shared.Name, F(p.X), F(p.Y), F(p.Z), F(p.QX), F(p.QY), F(p.QZ), F(p.QW)));
        }

        File.WriteAllLines(path, lines);
        return lines.Count;
    }

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    public void AddVan(ObjectReplication shared, PlayerPose near)
    {
        BodyPose pose = BodyPose.At(near.X + 4f, near.Y, near.Z);
        vehicles.Add(new Mirror(shared.AddLocal(VanName, pose), pose));
    }

    /// <summary>Print each vehicle another machine moves, as it moves (every 5 mm, at most five times a second).</summary>
    public bool WatchVehicles { get; set; }

    /// <summary>A file to write every state of a vehicle another machine moves to, as it arrives; null for none.</summary>
    public string? StepsFile { get; set; }

    private System.IO.StreamWriter? steps;
    private ObjectReplication? stepsFrom;

    public void CloseSteps()
    {
        steps?.Dispose();
        steps = null;
    }

    private void OnStateHeard(SharedBody body, double senderTime, BodyPose pose)
    {
        steps?.WriteLine(body.Name + "," + senderTime.ToString("0.0000", CultureInfo.InvariantCulture) + "," + pose.X.ToString("0.0000", CultureInfo.InvariantCulture)
            + "," + pose.Y.ToString("0.0000", CultureInfo.InvariantCulture) + "," + pose.Z.ToString("0.0000", CultureInfo.InvariantCulture));
    }

    public void Step(NetworkManager manager, double now, bool inWorld)
    {
        Seat = null;
        ObjectReplication? shared = manager.Vehicles;
        if (shared == null || manager.Session!.State != SessionState.Connected)
        {
            return;
        }

        if (StepsFile != null && stepsFrom != shared)
        {
            if (stepsFrom != null)
            {
                stepsFrom.StateHeard -= OnStateHeard;
            }

            stepsFrom = shared;
            steps ??= new System.IO.StreamWriter(StepsFile, false) { AutoFlush = true };
            shared.StateHeard += OnStateHeard;
        }

        if (manager.Mode == NetworkMode.Client)
        {
            if (!listRequested)
            {
                listRequested = true;
                shared.RequestList();
            }

            // Whatever vehicle the host has, this world now has too.
            foreach (KeyValuePair<string, BodyPose> missing in shared.Missing)
            {
                vehicles.Add(new Mirror(shared.AddLocal(missing.Key, missing.Value), missing.Value));
            }
        }

        double step = double.IsNaN(lastStep) ? 0.0 : Math.Min(0.1, now - lastStep);
        lastStep = now;
        shared.Sample(now);
        foreach (Mirror vehicle in vehicles)
        {
            SharedBody body = vehicle.Shared;
            if (body.Role == BodyRole.Follow)
            {
                if (body.HasTarget)
                {
                    vehicle.Pose = body.Target;
                    if (WatchVehicles && vehicle.Pose.DistanceTo(vehicle.Printed) > 0.005 && now - vehicle.PrintedAt >= 0.2)
                    {
                        vehicle.Printed = vehicle.Pose;
                        vehicle.PrintedAt = now;
                        Console.WriteLine("Vehicle '" + body.Name + "' at (" + vehicle.Pose.X.ToString("0.000", CultureInfo.InvariantCulture) + ", "
                            + vehicle.Pose.Y.ToString("0.000", CultureInfo.InvariantCulture) + ", " + vehicle.Pose.Z.ToString("0.000", CultureInfo.InvariantCulture)
                            + "), " + now.ToString("0.00", CultureInfo.InvariantCulture) + " s.");
                    }
                }
            }
            else if (body.Role == BodyRole.Simulate)
            {
                if (body.TakeHandOff(out BodyPose handedOver, out _))
                {
                    vehicle.Pose = handedOver;
                }

                if (vehicle != driven)
                {
                    // Nobody at the wheel on this machine: it stands where it is.
                    body.Report(vehicle.Pose, default, false);
                }
            }
        }

        if (!drives || !inWorld)
        {
            return;
        }

        if (driven == null)
        {
            Mirror? free = vehicles.FirstOrDefault(v => v.Shared.HasId && !(v.Shared.Role == BodyRole.Follow && v.Shared.Held)
                && (only == null || v.Shared.Name.StartsWith(only, StringComparison.Ordinal)));
            if (free == null)
            {
                phaseStarted = double.NaN;
                return;
            }

            if (double.IsNaN(phaseStarted))
            {
                phaseStarted = now;
            }

            if (now - phaseStarted < WaitSeconds)
            {
                return;
            }

            driven = free;
            start = free.Pose;
            startYaw = YawOf(start);
            travelled = 0;
            speed = 0;
            phaseStarted = now;
            shared.SetHeld(free.Shared);
            Drives++;
            Console.WriteLine("The bot takes the wheel of '" + free.Shared.Name + "' (vehicle " + free.Shared.Id + ") at " + Describe(start) + ".");
        }

        if (driven.Shared.Role != BodyRole.Simulate)
        {
            // Someone else was at the wheel, or got there first.
            Refused++;
            Console.WriteLine("'" + driven.Shared.Name + "' is driven by player " + driven.Shared.Owner + "; the bot lets go of it.");
            shared.SetHeld(null);
            driven = null;
            phaseStarted = now;
            return;
        }

        double elapsed = now - phaseStarted;
        bool braking = elapsed >= DriveSeconds - BrakeSeconds;
        speed = braking ? Math.Max(0.0, speed - (Acceleration * step)) : Math.Min(topSpeedUsed, speed + (Acceleration * step));
        travelled += speed * step;

        // A circle to the right of where the vehicle pointed, starting along its nose.
        double turned = travelled / radiusUsed;
        double forwardX = Math.Sin(startYaw);
        double forwardZ = Math.Cos(startYaw);
        double rightX = Math.Cos(startYaw);
        double rightZ = -Math.Sin(startYaw);
        double x = start.X + (radiusUsed * ((rightX * (1 - Math.Cos(turned))) + (forwardX * Math.Sin(turned))));
        double z = start.Z + (radiusUsed * ((rightZ * (1 - Math.Cos(turned))) + (forwardZ * Math.Sin(turned))));
        double yaw = startYaw + turned;
        var pose = new BodyPose((float)x, start.Y, (float)z, 0f, (float)Math.Sin(yaw / 2), 0f, (float)Math.Cos(yaw / 2));
        var motion = new BodyMotion
        {
            VX = (float)(speed * Math.Sin(yaw)),
            VZ = (float)(speed * Math.Cos(yaw)),
            AY = (float)(speed / radiusUsed),
        };
        driven.Pose = pose;
        driven.Shared.Report(pose, motion, true);

        float[] place = seat ?? (seats.TryGetValue(driven.Shared.Name, out float[]? measured) ? measured : AnySeat);
        var at = new PlayerPose(place[0], place[1], place[2], 0f, 0f, place[3]) { Parent = driven.Shared.Id, Driving = true };
        at.Controls = lesson
            ? Lesson(elapsed, speed)
            : new VehicleControls
            {
                Steering = speed > 0.1 ? 0.35f : 0f,
                Throttle = braking ? 0f : 0.6f,
                Brake = braking && speed > 0 ? 0.5f : 0f,
                Rpm = (float)(900 + (speed * 350)),
                Gear = 2,
            };
        Seat = at;

        if (elapsed >= DriveSeconds && speed <= 0.0)
        {
            if (only != null)
            {
                // A probe drives on a flat circle at one height, through whatever stands there: the vehicle is left
                // where it was taken, for the game that takes it back to find it on its wheels.
                pose = start;
                driven.Pose = start;
            }

            Console.WriteLine("The bot gets out of '" + driven.Shared.Name + "' at " + Describe(pose) + ", " + travelled.ToString("0", CultureInfo.InvariantCulture) + " m later.");
            shared.SetHeld(null);
            driven.Shared.Report(pose, default, false);
            driven = null;
            Seat = null;
            phaseStarted = now;
        }
    }

    // A driver who works the controls, in a cycle of 12 s: the wheel from side to side every 6 s; the throttle, then the
    // brake, twice; and every 4 s the clutch down, a gear up or down (first, second, third, second) and the clutch up.
    private static VehicleControls Lesson(double elapsed, double speed)
    {
        double cycle = elapsed % 12.0;
        double shift = elapsed % 4.0;
        int changes = (int)Math.Floor((elapsed + 2.0) / 4.0);
        int[] gears = { 2, 3, 4, 3 };
        bool braking = (cycle >= 4.0 && cycle < 5.5) || (cycle >= 9.0 && cycle < 11.0);
        return new VehicleControls
        {
            Steering = (float)(0.6 * Math.Sin(2.0 * Math.PI * elapsed / 6.0)),
            Throttle = braking || (shift >= 1.5 && shift < 2.5) ? 0f : 0.7f,
            Brake = braking ? 0.8f : 0f,
            Clutch = shift >= 1.5 && shift < 2.5 ? 1f : 0f,
            Rpm = (float)(900 + (speed * 350)),
            Gear = gears[changes % gears.Length],
        };
    }

    public string Describe()
    {
        return "Vehicles here: " + vehicles.Count + (drives ? ", driven " + Drives + " time(s), found taken " + Refused + " time(s)" : string.Empty)
            + (driven != null ? ", at the wheel of '" + driven.Shared.Name + "'" : string.Empty);
    }

    private static double YawOf(BodyPose pose)
    {
        return Math.Atan2(2.0 * ((pose.QW * pose.QY) + (pose.QX * pose.QZ)), 1.0 - (2.0 * ((pose.QY * pose.QY) + (pose.QX * pose.QX))));
    }

    private static string Describe(BodyPose pose)
    {
        return "(" + pose.X.ToString("0.0", CultureInfo.InvariantCulture) + ", " + pose.Y.ToString("0.0", CultureInfo.InvariantCulture)
            + ", " + pose.Z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
    }

    private sealed class Mirror(SharedBody shared, BodyPose pose)
    {
        public SharedBody Shared { get; } = shared;

        public BodyPose Pose { get; set; } = pose;

        public BodyPose Printed { get; set; } = pose;

        public double PrintedAt { get; set; } = double.NegativeInfinity;
    }
}
