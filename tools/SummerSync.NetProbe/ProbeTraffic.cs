using System.Globalization;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.World;

/// <summary>
/// A client that says it is on the highway or the dirt roads, the way a game says where its player is, and watches
/// the host's traffic: how many of its cars are on and how fast they move as it plays them back.
/// </summary>
internal sealed class ProbeTraffic
{
    private const double WatchFrom = 4.0;

    private readonly int cars;
    private readonly uint shape;
    private readonly bool onHighway;
    private readonly bool people;
    private readonly bool npcs;
    private readonly BodyPose[] last;
    private readonly bool[] seen;
    private TrafficReplication? traffic;
    private bool told;
    private double lastAt = double.NaN;

    /// <param name="people">Watch the people who walk the roads instead of the traffic.</param>
    /// <param name="npcs">Watch the rest of what moves by itself (train, boats, moose…) instead.</param>
    public ProbeTraffic(int cars, uint shape, bool onHighway, bool people = false, bool npcs = false)
    {
        this.cars = cars;
        this.shape = shape;
        this.onHighway = onHighway;
        this.people = people || npcs;
        this.npcs = npcs;
        last = new BodyPose[cars];
        seen = new bool[cars];
    }

    public int MostActive { get; private set; }

    public double FastestShown { get; private set; }

    public double Moved { get; private set; }

    public byte Groups { get; private set; }

    public bool Passed => traffic != null && traffic.HasState && traffic.Malformed == 0 && MostActive > 0 && Moved > (people ? 5.0 : 10.0)
        && (people || (Groups & (onHighway ? TrafficWire.GroupHighway : TrafficWire.GroupDirtRoads)) != 0);

    public void Step(NetworkManager manager, double now)
    {
        TrafficReplication? watched = npcs ? manager.Npcs : people ? manager.People : manager.Traffic;
        if (manager.Session == null || manager.Session.State != SessionState.Connected || watched == null || manager.Tallies == null)
        {
            return;
        }

        if (watched != traffic)
        {
            traffic = watched;
            traffic.Define(cars, shape);
        }

        if (!told && !people)
        {
            told = true;
            manager.Tallies.Set("traffic/zone/" + manager.Session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), new[] { onHighway ? 1 : 0 });
            Console.WriteLine("Traffic: this probe says it is on " + (onHighway ? "the highway." : "the dirt roads."));
        }

        if (!traffic.HasState || now < WatchFrom)
        {
            return;
        }

        Groups = traffic.Groups;
        int active = 0;
        double step = double.IsNaN(lastAt) ? 0.0 : now - lastAt;
        for (int i = 0; i < cars; i++)
        {
            BodyPose pose;
            if (!traffic.Sample(i, now, out pose))
            {
                seen[i] = false;
                continue;
            }

            active++;
            if (seen[i] && step > 0.0)
            {
                double distance = pose.DistanceTo(last[i]);
                Moved += distance;
                FastestShown = Math.Max(FastestShown, distance / step);
            }

            last[i] = pose;
            seen[i] = true;
        }

        lastAt = now;
        MostActive = Math.Max(MostActive, active);
    }

    /// <summary>
    /// As a host: a made-up traffic for a real game to show, the first three of its cars driving round a point at
    /// 8 m/s, a third of the way apart, the first with its brake lights on every other three seconds.
    /// </summary>
    public static void Drive(NetworkManager manager, double now, int cars, uint shape, float x, float y, float z, ref TrafficCar[]? state)
    {
        if (manager.Traffic == null || manager.Session == null || manager.Session.State != SessionState.Connected)
        {
            return;
        }

        if (state == null)
        {
            manager.Traffic.Define(cars, shape);
            state = new TrafficCar[cars];
        }

        const float Radius = 14f;
        const float Speed = 8f;
        for (int i = 0; i < Math.Min(3, cars); i++)
        {
            double angle = (now * Speed / Radius) + (i * 2.0 * Math.PI / 3.0);
            float px = x + (float)(Math.Cos(angle) * Radius);
            float pz = z + (float)(Math.Sin(angle) * Radius);

            // Facing along the circle: the direction of travel is (-sin, cos).
            double heading = Math.Atan2(-Math.Sin(angle), Math.Cos(angle));
            float half = (float)(heading / 2.0);
            state[i] = new TrafficCar
            {
                Active = true,
                Pose = new BodyPose(px, y, pz, 0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half)),
                Speed = Speed,
                Rpm = 2500f + (i * 500f),
                Flags = i == 0 && ((int)(now / 3.0) % 2) == 1 ? TrafficWire.LightBrake : (byte)0,
            };
        }

        manager.Traffic.Report(now, TrafficWire.GroupHighway | TrafficWire.GroupDirtRoads, state);
    }

    /// <summary>
    /// As a host: made-up walkers for a real game to show, each walking back and forth along its own line by that
    /// point, a few seconds of walking and one of standing; and, at <paramref name="fallAt"/> seconds, one of them
    /// said to be run over.
    /// </summary>
    public static void Walk(NetworkManager manager, double now, int count, uint shape, float x, float y, float z, string? fallName, double fallAt, ref TrafficCar[]? state, ref bool fell)
    {
        if (manager.People == null || manager.Tallies == null || manager.Session == null || manager.Session.State != SessionState.Connected)
        {
            return;
        }

        if (state == null)
        {
            manager.People.Define(count, shape);
            state = new TrafficCar[count];
        }

        for (int i = 0; i < count; i++)
        {
            // Six seconds out, one standing, six back, one standing.
            double t = (now + i) % 14.0;
            double along = t < 6.0 ? t : t < 7.0 ? 6.0 : t < 13.0 ? 13.0 - t : 0.0;
            bool walking = t < 6.0 || (t >= 7.0 && t < 13.0);
            bool back = t >= 7.0 && t < 13.0;
            float half = back ? (float)Math.PI / 2f : 0f;
            state[i] = new TrafficCar
            {
                Active = true,
                Pose = new BodyPose(x + (i * 2.5f), y, z + (float)(along * 1.2), 0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half)),
                Speed = walking ? 1.2f : 0f,
                Flags = walking ? PeopleWire.Walking : PeopleWire.Standing,
            };
        }

        if (fallName != null && !fell && now >= fallAt)
        {
            fell = true;
            manager.Tallies.Set("people/fallen/" + fallName, new[] { 1 });
            Console.WriteLine("People: " + fallName + " was run over (made up).");
        }

        manager.People.Report(now, 0, state);
    }

    /// <summary>
    /// As a host: made-up NPCs for a real game to show, each going round its own point, 30 m apart along x from the
    /// one given, at 6 m/s, with every listed child on and an engine at 3000 rpm; and, at <paramref name="goneAt"/>
    /// seconds, the one named <paramref name="goneName"/> said to be destroyed.
    /// </summary>
    public static void Show(NetworkManager manager, double now, int count, uint shape, float x, float y, float z, string? goneName, double goneAt, double still, ref TrafficCar[]? state, ref bool gone)
    {
        if (manager.Npcs == null || manager.Tallies == null || manager.Session == null || manager.Session.State != SessionState.Connected)
        {
            return;
        }

        if (state == null)
        {
            manager.Npcs.Define(count, shape);
            state = new TrafficCar[count];
        }

        const float Radius = 10f;
        const float Speed = 6f;
        // Standing still until a while has passed (a rider can get on), then going round.
        double moving = Math.Max(0.0, now - still);
        for (int i = 0; i < count; i++)
        {
            double angle = moving * Speed / Radius;
            double heading = Math.Atan2(-Math.Sin(angle), Math.Cos(angle));
            float half = (float)(heading / 2.0);
            state[i] = new TrafficCar
            {
                Active = true,
                Pose = new BodyPose(x + (i * 30f) + (float)(Math.Cos(angle) * Radius), y, z + (float)(Math.Sin(angle) * Radius), 0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half)),
                Speed = now < still ? 0f : Speed,
                Rpm = 3000f,
                Flags = 0xFF,
            };
        }

        if (goneName != null && !gone && now >= goneAt)
        {
            gone = true;
            manager.Tallies.Set("npc/gone/" + goneName, new[] { 1 });
            Console.WriteLine("NPCs: " + goneName + " was destroyed (made up).");
        }

        manager.Npcs.Report(now, 0, state);
    }

    public string Describe()
    {
        return (npcs ? "NPCs: " : people ? "People: " : "Traffic: ") + (traffic == null || !traffic.HasState ? "never heard of" : "groups " + Groups + ", at most " + MostActive + " car(s) on, "
            + Moved.ToString("0", CultureInfo.InvariantCulture) + " m driven in all as shown here, fastest "
            + FastestShown.ToString("0.0", CultureInfo.InvariantCulture) + " m/s, " + traffic.Received + " message(s), " + traffic.Malformed + " malformed") + "\n";
    }
}
