using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Objects;

/// <summary>
/// One machine's game world, reduced to what the replication needs from it: a list of loose objects that
/// are where their physics, or the network, puts them.
/// </summary>
internal sealed class FakeWorld
{
    private readonly List<Thing> things = new();
    private Thing? inHand;

    public FakeWorld(SessionNet.Node node, params (string Name, BodyPose Pose)[] objects)
        : this(node, ObjectWire.MessageRequest, null, objects)
    {
    }

    /// <summary>A world of another kind of thing on the same machine, with a replication of its own.</summary>
    public FakeWorld(SessionNet.Node node, byte firstMessage, string? label, params (string Name, BodyPose Pose)[] objects)
    {
        Node = node;
        Replication = new ObjectReplication(node.Session, node.Logger, firstMessage, label);
        Action<double>? earlier = node.BeforeUpdate;
        node.BeforeUpdate = time =>
        {
            earlier?.Invoke(time);
            Frame(time);
        };
        foreach ((string name, BodyPose pose) in objects)
        {
            Add(name, pose);
        }

        // A world built with its things is loaded; an empty one may still load (call Ready).
        if (objects.Length > 0)
        {
            Replication.SetWorldReady();
        }
    }

    /// <summary>This world is loaded and has all its things: what the replication is told once the first scan is done.</summary>
    public void Ready() => Replication.SetWorldReady();

    public SessionNet.Node Node { get; }

    public ObjectReplication Replication { get; }

    public double Time { get; private set; }

    public IReadOnlyList<Thing> Things => things;

    public IEnumerable<string> Log => Node.Log.Where(e => e.Category == LogCategory.Authority).Select(e => e.Message);

    public Thing this[int index] => things[index];

    public Thing Add(string name, BodyPose pose)
    {
        var thing = new Thing(Replication.AddLocal(name, pose), pose);
        things.Add(thing);
        return thing;
    }

    /// <summary>A thing with its key from the save ("beercase0").</summary>
    public Thing AddKeyed(string name, BodyPose pose, string key)
    {
        var thing = new Thing(Replication.AddLocal(name, pose, Protocol.HostPlayerId, key), pose);
        things.Add(thing);
        return thing;
    }

    /// <summary>An object this machine made because player <paramref name="bornWith"/>'s game had just made it.</summary>
    public Thing AddBorn(string name, BodyPose pose, byte bornWith)
    {
        var thing = new Thing(Replication.AddLocal(name, pose, bornWith), pose);
        things.Add(thing);
        return thing;
    }

    public void Remove(Thing thing)
    {
        things.Remove(thing);
        Replication.RemoveLocal(thing.Shared);
    }

    /// <summary>
    /// The local player picks the object up, lifts it to <paramref name="height"/> and carries it around in a
    /// circle at walking pace, turning as it goes. It starts from wherever the object is.
    /// </summary>
    public void Grab(Thing thing, float height = 1.2f)
    {
        inHand = thing;
        thing.Path = Circling(thing.Pose, Time, height);
        thing.SettleAt = double.NaN;
        thing.Awake = true;
        Replication.SetHeld(thing.Shared);
    }

    /// <summary>Something knocks the object: it rolls around for a while, on this machine's physics, and stops.</summary>
    public void Push(Thing thing, double seconds)
    {
        thing.Path = Circling(thing.Pose, Time, thing.Pose.Y);
        thing.Awake = true;
        thing.SettleAt = Time + seconds;
    }

    /// <summary>Where every fake world has its one vehicle at a given time: driving off along x at 10 m/s.</summary>
    public static BodyPose VehicleAt(double time) => BodyPose.At((float)(100.0 + (10.0 * time)), 0f, 0f);

    private static Func<double, BodyPose> Circling(BodyPose start, double startTime, float height)
    {
        return time =>
        {
            double elapsed = Math.Max(0.0, time - startTime);
            double angle = elapsed * 2.0;
            double lift = Math.Min(1.0, elapsed / 0.3);
            float half = (float)(angle / 2.0);
            return new BodyPose(
                (float)(start.X + Math.Cos(angle) - 1.0),
                (float)(start.Y + ((height - start.Y) * lift)),
                (float)(start.Z + Math.Sin(angle)),
                0,
                (float)Math.Sin(half),
                0,
                (float)Math.Cos(half));
        };
    }

    /// <summary>The local player lets go. The object keeps moving for a while and then comes to rest.</summary>
    public void LetGo(double settleSeconds = 0.5)
    {
        if (inHand != null)
        {
            inHand.SettleAt = Time + settleSeconds;
            inHand = null;
        }

        Replication.SetHeld(null);
    }

    private void Frame(double time)
    {
        Time = time;
        Replication.Update(time);
        foreach (Thing thing in things)
        {
            switch (thing.Shared.Role)
            {
                case BodyRole.Simulate:
                    if (thing.Shared.TakeHandOff(out BodyPose pose, out BodyMotion motion))
                    {
                        thing.Pose = pose;
                        thing.HandOffs++;
                        thing.LastHandOffMotion = motion;

                        // In the local player's hand it goes on from there; otherwise whatever was handed over at
                        // speed keeps falling for a moment.
                        if (thing == inHand)
                        {
                            thing.Path = Circling(pose, time, 3f);
                        }
                        else
                        {
                            thing.Path = null;
                            thing.Awake = motion.VX != 0 || motion.VY != 0 || motion.VZ != 0;
                            thing.SettleAt = thing.Awake ? time + 0.3 : double.NaN;
                        }
                    }

                    if (thing.Path != null)
                    {
                        thing.Pose = thing.Path(time);
                    }

                    if (time >= thing.SettleAt)
                    {
                        thing.Path = null;
                        thing.Awake = false;
                        thing.SettleAt = double.NaN;
                    }

                    if (thing.CarriedBy != 0)
                    {
                        thing.Shared.ReportCarried(thing.Pose, thing.CarriedBy, thing.PlaceOnVehicle, thing.Motion, thing.Awake);
                    }
                    else if (thing.StepSeconds > 0 && thing.Path != null)
                    {
                        // Moved by physics steps only, as a vehicle is: where its last step left it, said with that step's time.
                        double step = Math.Floor(time / thing.StepSeconds) * thing.StepSeconds;
                        thing.Pose = thing.Path(step);
                        thing.Shared.Report(thing.Pose, thing.Motion, thing.Awake, step);
                    }
                    else
                    {
                        thing.Shared.Report(thing.Pose, thing.Motion, thing.Awake);
                    }

                    break;

                case BodyRole.Follow:
                    if (thing.Shared.HasTarget)
                    {
                        thing.LargestStep = Math.Max(thing.LargestStep, thing.Pose.DistanceTo(thing.Shared.Target));
                        thing.Pose = thing.Shared.Target;
                        if (thing.Pose.Parent != 0)
                        {
                            // What a game does with a pose on a vehicle: works out where that is in its world.
                            thing.Shared.NoteWorldPose(VehicleAt(Time).Moved(thing.Pose.X, thing.Pose.Y, thing.Pose.Z));
                        }
                    }

                    break;
            }
        }

        Replication.Sample(time);
    }

    internal sealed class Thing(SharedBody shared, BodyPose pose)
    {
        public SharedBody Shared { get; } = shared;

        public BodyPose Pose { get; set; } = pose;

        public BodyMotion Motion { get; set; }

        public bool Awake { get; set; }

        /// <summary>Where the object is at a given time while this machine simulates it and it moves.</summary>
        public Func<double, BodyPose>? Path { get; set; }

        public double SettleAt { get; set; } = double.NaN;

        public int HandOffs { get; set; }

        public BodyMotion LastHandOffMotion { get; set; }

        /// <summary>The furthest the network moved it in one frame while it was being followed.</summary>
        public double LargestStep { get; set; }

        /// <summary>The vehicle that carries it while this machine simulates it, or 0.</summary>
        public ushort CarriedBy { get; set; }

        /// <summary>Where it lies on that vehicle.</summary>
        public BodyPose PlaceOnVehicle { get; set; }

        /// <summary>For a thing that only moves in physics steps this long apart (a vehicle); 0 for one that moves every frame.</summary>
        public double StepSeconds { get; set; }
    }
}

public class ObjectReplicationTests
{
    private static readonly BodyPose CrateA = BodyPose.At(0, 0, 0);
    private static readonly BodyPose CrateB = BodyPose.At(5, 0, 0);
    private static readonly BodyPose Bottle = new(1, 0.9f, 1, 0, 0.7071068f, 0, 0.7071068f);

    private static (string, BodyPose)[] Yard() =>
        new[] { ("beer case(itemx)", CrateA), ("beer case(itemx)", CrateB), ("booze(itemx)", Bottle) };

    private static void AssertSamePlace(BodyPose expected, BodyPose actual, double metres = 0.002, double degrees = 0.2)
    {
        Assert.True(expected.DistanceTo(actual) <= metres, $"{expected.DistanceTo(actual):0.0000} m apart");
        Assert.True(expected.AngleTo(actual) <= degrees, $"{expected.AngleTo(actual):0.000} degrees apart");
    }

    [Fact]
    public void A_state_reported_with_the_time_of_its_physics_step_says_that_time_and_the_others_see_an_even_speed()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, ("van", BodyPose.At(0, 0, 0)));
        var client = new FakeWorld(net.AddClient("Julian"), ("van", BodyPose.At(0, 0, 0)));
        net.Run(1);

        // Driven off along x at 8 m/s, its physics stepping every fiftieth of a second whatever the frames do.
        FakeWorld.Thing van = host[0];
        double start = host.Time;
        van.StepSeconds = 0.02;
        van.Path = time => BodyPose.At((float)(8.0 * Math.Max(0.0, time - start)), 0f, 0f);
        van.Awake = true;
        var heard = new List<(double Time, float X)>();
        client.Replication.StateHeard += (body, time, pose) => heard.Add((time, pose.X));
        net.Run(2);

        var speeds = new List<double>();
        for (int i = 1; i < heard.Count; i++)
        {
            double interval = heard[i].Time - heard[i - 1].Time;
            if (interval > 0 && heard[i].X > 0.5f)
            {
                speeds.Add((heard[i].X - heard[i - 1].X) / interval);
            }
        }

        Assert.True(speeds.Count > 20, speeds.Count + " intervals heard");
        Assert.All(speeds, speed => Assert.InRange(speed, 7.95, 8.05));
    }

    [Fact]
    public void A_client_with_the_same_world_gets_every_object_of_the_host()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());

        // The same save, loaded on another machine: the same things, listed in another order and a hair off.
        var client = new FakeWorld(
            net.AddClient("Julian"),
            ("booze(itemx)", Bottle.Moved(0.01, 0, 0)),
            ("beer case(itemx)", CrateB.Moved(0, 0.02, 0)),
            ("beer case(itemx)", CrateA.Moved(0.03, 0, 0)));

        net.Run(2);

        Assert.Equal(3, host.Replication.Shared);
        Assert.Equal(3, client.Replication.Shared);
        Assert.Equal(0, client.Replication.MissingHere);
        Assert.Equal(0, client.Replication.OnlyHere);
        Assert.All(host.Things, thing => Assert.Equal(BodyRole.Simulate, thing.Shared.Role));
        Assert.All(client.Things, thing => Assert.Equal(BodyRole.Follow, thing.Shared.Role));

        // Each one was paired with the nearest of its name and moved to where the host has it.
        AssertSamePlace(Bottle, client[0].Pose);
        AssertSamePlace(CrateB, client[1].Pose);
        AssertSamePlace(CrateA, client[2].Pose);
        Assert.Equal(host[1].Shared.Id, client[1].Shared.Id);
        Assert.Equal(host[0].Shared.Id, client[2].Shared.Id);
        Assert.Contains("Objects: 3 shared with the host, 0 of the host's not found here, 0 only here.", client.Log);
    }

    [Fact]
    public void What_only_one_of_the_two_worlds_has_is_counted_and_left_alone()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, ("beer case(itemx)", CrateA), ("spark plug(Clone)", CrateB));
        var client = new FakeWorld(net.AddClient("Julian"), ("beer case(itemx)", CrateA), ("sugar(itemx)", Bottle));

        net.Run(2);

        Assert.Equal(1, client.Replication.Shared);
        Assert.Equal(1, client.Replication.MissingHere);
        Assert.Equal(1, client.Replication.OnlyHere);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);
        Assert.Equal(BodyRole.Unmapped, client[1].Shared.Role);
        Assert.False(client[1].Shared.HasId);
        Assert.Equal(2, host.Replication.Shared);
        Assert.Equal(0, host.Replication.OnlyHere);

        // The counts say that the worlds differ; the log says in what, once things have settled.
        net.Run(2);
        Assert.Equal(
            "The two worlds differ. Only the host has: 'spark plug(Clone)' at (5.0, 0.0, 0.0). Only this world has: 'sugar(itemx)' at (1.0, 0.9, 1.0).",
            Assert.Single(client.Log, m => m.StartsWith("The two worlds differ")));
        Assert.DoesNotContain(host.Log, m => m.StartsWith("The two worlds differ"));

        // It is said once, and again only when it changes.
        client.Add("spark plug(Clone)", CrateB);
        net.Run(3);
        Assert.Equal(2, client.Replication.Shared);
        Assert.Equal(
            "The two worlds differ. Only this world has: 'sugar(itemx)' at (1.0, 0.9, 1.0).",
            client.Log.Last(m => m.StartsWith("The two worlds differ")));
        client.Remove(client[1]);
        net.Run(3);
        Assert.Equal("Every object pairs up with one of the host's now.", client.Log.Last());
        Assert.Equal(2, client.Log.Count(m => m.StartsWith("The two worlds differ")));
    }

    [Fact]
    public void A_long_list_of_differences_is_cut_short_in_the_log()
    {
        var net = new SessionNet();
        var objects = new List<(string, BodyPose)>();
        for (int i = 0; i < 20; i++)
        {
            objects.Add(("bottle " + i, BodyPose.At(i, 0, 0)));
        }

        var host = new FakeWorld(net.Host, objects.ToArray());
        var client = new FakeWorld(net.AddClient("Julian"), ("beer case(itemx)", CrateA));

        net.Run(4);

        Assert.Equal(20, host.Replication.Shared);
        string line = Assert.Single(client.Log, m => m.StartsWith("The two worlds differ"));
        Assert.Contains("'bottle 11' at (11.0, 0.0, 0.0), and 8 more. Only this world has: 'beer case(itemx)' at (0.0, 0.0, 0.0).", line);
        Assert.DoesNotContain("bottle 12", line);
    }

    [Fact]
    public void Nothing_is_sent_while_nothing_moves()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Enumerable.Range(0, 200).Select(i => ("part " + i, BodyPose.At(i, 0, 0))).ToArray());
        var client = new FakeWorld(net.AddClient("Julian"), Enumerable.Range(0, 200).Select(i => ("part " + i, BodyPose.At(i, 0, 0))).ToArray());
        net.Run(3);
        Assert.Equal(200, client.Replication.Shared);

        long before = host.Replication.StatesSent;
        net.Run(10);

        Assert.Equal(before, host.Replication.StatesSent);
        Assert.Equal(0, client.Replication.StatesSent);
    }

    [Fact]
    public void A_client_whose_world_goes_hands_back_what_it_had()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50 });
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        client.Grab(client[2]);
        net.Run(1);
        Assert.True(host[2].Shared.Held);

        client.Replication.ClearWorld();
        net.Run(1);

        Assert.Equal(Protocol.HostPlayerId, host[2].Shared.Owner);
        Assert.False(host[2].Shared.Held);
        Assert.Equal(BodyRole.Simulate, host[2].Shared.Role);
    }

    [Fact]
    public void Grabbing_again_just_after_handing_back_keeps_it_in_the_hand()
    {
        // The host's word that it has the object again (the answer to the hand-back) is on its way when the client's
        // player takes it again; the answer to that claim comes after it.
        var net = new SessionNet(new NetworkConditions { LatencyMs = 100 });
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        client.Grab(client[2]);
        net.Run(1);
        client.LetGo(0.1);
        for (int frame = 0; frame < 600 && client[2].Shared.Role == BodyRole.Simulate; frame++)
        {
            net.Run(1.0 / 60.0);
        }

        Assert.Equal(BodyRole.Follow, client[2].Shared.Role);
        client.Grab(client[2]);
        net.Run(2);

        Assert.Equal(BodyRole.Simulate, client[2].Shared.Role);
        Assert.Equal(client.Node.Session.LocalPlayerId, host[2].Shared.Owner);
        Assert.True(host[2].Shared.Held);
        AssertSamePlace(client[2].Pose, host[2].Pose, 0.6, 360);
    }

    [Fact]
    public void Steps_that_are_awake_for_one_frame_all_reach_the_other_machine()
    {
        // A car on the floor jack: each pump moves it a few millimetres, and it is told as awake only in the frame it
        // passes the next couple of millimetres. The states go out every other frame.
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        FakeWorld mover = host;
        FakeWorld watcher = client;
        FakeWorld.Thing lifted = mover[0];
        for (int pump = 0; pump < 12; pump++)
        {
            BodyPose pose = lifted.Pose;
            pose.Y += 0.003f;
            lifted.Pose = pose;
            lifted.Path = null;
            lifted.Awake = true;
            lifted.SettleAt = mover.Time + (1.5 / 60.0);
            net.Run(0.25);
            Assert.Equal(BodyRole.Simulate, lifted.Shared.Role);
            AssertSamePlace(lifted.Pose, watcher[0].Pose, 0.0005);
        }
    }

    [Fact]
    public void An_object_the_host_moves_is_followed_smoothly_and_ends_exactly_where_it_came_to_rest()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 100, JitterMs = 20, LossPercent = 5 });
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        // Something knocks the bottle over: it rolls for three seconds and stops.
        host.Push(host[2], 3);
        net.Run(1.5);
        Assert.False(client[2].Shared.AtRest);
        Assert.InRange(client[2].Pose.DistanceTo(Bottle), 0.3, 2.5);
        net.Run(3);

        AssertSamePlace(host[2].Pose, client[2].Pose);
        Assert.True(client[2].Shared.AtRest);
        Assert.False(host[2].Awake);

        // At two metres a second it never moved further in a frame than that speed explains, give or take a correction.
        Assert.InRange(client[2].LargestStep, 0.02, 0.12);
        Assert.InRange(host.Replication.StatesSent, 80, 100);

        // The ones that did not move were not touched.
        AssertSamePlace(CrateA, client[0].Pose, 0.0001);
        Assert.Equal(0, client.Replication.Malformed + host.Replication.Malformed);
    }

    // The heart of the phase: whoever picks an object up simulates it, everyone else follows, and it goes back
    // to the host when it has come to rest.
    [Fact]
    public void A_client_picks_an_object_up_carries_it_and_puts_it_down()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 80, JitterMs = 15, LossPercent = 3 });
        var host = new FakeWorld(net.Host, Yard());
        var first = new FakeWorld(net.AddClient("Julian"), Yard());
        var second = new FakeWorld(net.AddClient("Friend"), Yard());
        net.Run(2);

        first.Grab(first[0]);

        // At once, before the host has heard of it: the crate has to follow the hand from the first frame.
        Assert.Equal(BodyRole.Simulate, first[0].Shared.Role);
        net.Run(3);

        Assert.Equal(1, host[0].Shared.Owner);
        Assert.True(host[0].Shared.Held);
        Assert.Equal(BodyRole.Follow, host[0].Shared.Role);
        Assert.Equal(1, second[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, second[0].Shared.Role);

        // Host and bystander both show the crate where the carrier had it a moment ago.
        Assert.InRange(host[0].Pose.DistanceTo(first[0].Pose), 0.05, 0.9);
        Assert.InRange(second[0].Pose.DistanceTo(first[0].Pose), 0.05, 1.2);
        Assert.InRange(host[0].Pose.Y, 1.19f, 1.21f);
        Assert.InRange(second[0].Pose.Y, 1.19f, 1.21f);

        second[0].LargestStep = 0;
        first.LetGo(settleSeconds: 0.6);
        net.Run(3);

        // Back with the host, at the very spot where the carrier's physics left it.
        Assert.All(new[] { host, first, second }, world => Assert.Equal(0, world[0].Shared.Owner));
        Assert.All(new[] { host, first, second }, world => Assert.False(world[0].Shared.Held));
        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(BodyRole.Follow, first[0].Shared.Role);
        Assert.Equal(1, host[0].HandOffs);
        AssertSamePlace(first[0].Pose, host[0].Pose);
        AssertSamePlace(first[0].Pose, second[0].Pose);
        Assert.True(second[0].Shared.AtRest);
        Assert.InRange(second[0].LargestStep, 0.02, 0.2);
        Assert.Equal(0, host.Replication.Malformed + first.Replication.Malformed + second.Replication.Malformed);
    }

    [Fact]
    public void An_object_thrown_is_handed_back_moving_if_it_takes_too_long_to_stop()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        client.Grab(client[2]);
        net.Run(1);

        // Rolling down a hill: it will not come to rest by itself.
        client[2].Motion = new BodyMotion { VX = 2, VY = -1, AZ = 3 };
        client.LetGo(settleSeconds: 60);
        net.Run(ObjectReplication.ReleaseAfterSeconds - 1);
        Assert.Equal(1, host[2].Shared.Owner);
        net.Run(2);

        Assert.Equal(0, host[2].Shared.Owner);
        Assert.Equal(BodyRole.Simulate, host[2].Shared.Role);
        Assert.Equal(2f, host[2].LastHandOffMotion.VX);
        Assert.Equal(-1f, host[2].LastHandOffMotion.VY);
        Assert.Equal(3f, host[2].LastHandOffMotion.AZ);
        Assert.Equal(BodyRole.Follow, client[2].Shared.Role);
    }

    [Fact]
    public void An_object_in_someone_s_hand_cannot_be_taken_from_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new FakeWorld(net.Host, Yard());
        var first = new FakeWorld(net.AddClient("Julian"), Yard());
        var second = new FakeWorld(net.AddClient("Friend"), Yard());
        net.Run(2);
        first.Grab(first[0]);
        net.Run(1);

        second.Grab(second[0], 2f);
        net.Run(1);

        // The second player's game believes it has the crate; the crate stays with the first.
        Assert.Equal(1, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, second[0].Shared.Role);
        Assert.Equal(1, second[0].Shared.Owner);
        Assert.InRange(second[0].Pose.Y, 1.19f, 1.21f);
        Assert.Contains(second.Log, m => m == "Object " + second[0].Shared.Id + " 'beer case(itemx)' is in player 1's hand; it stays there.");
        Assert.Single(host.Log, m => m.StartsWith("Player 2 reached for object"));

        // And the host cannot take it either.
        host.Grab(host[0], 3f);
        net.Run(1);
        Assert.Equal(1, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, host[0].Shared.Role);

        // Once it is put down, the next one to reach for it gets it.
        first.LetGo();
        second.LetGo();
        host.LetGo();
        net.Run(2);
        second.Grab(second[0], 2f);
        net.Run(1);
        Assert.Equal(2, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Simulate, second[0].Shared.Role);
        Assert.InRange(first[0].Pose.Y, 1.99f, 2.01f);
    }

    [Fact]
    public void An_object_still_settling_can_be_picked_up_by_someone_else()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var first = new FakeWorld(net.AddClient("Julian"), Yard());
        var second = new FakeWorld(net.AddClient("Friend"), Yard());
        net.Run(2);
        first.Grab(first[0]);
        net.Run(1);
        first.LetGo(settleSeconds: 5);
        net.Run(0.5);
        Assert.Equal(1, host[0].Shared.Owner);

        // Caught in the air by the other client, and then by the host.
        second.Grab(second[0], 2f);
        net.Run(1);
        Assert.Equal(2, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, first[0].Shared.Role);
        Assert.InRange(first[0].Pose.Y, 1.99f, 2.01f);

        second.LetGo(settleSeconds: 5);
        net.Run(0.5);
        host.Grab(host[0], 3f);
        net.Run(1);

        Assert.All(new[] { host, first, second }, world => Assert.Equal(0, world[0].Shared.Owner));
        Assert.True(host[0].Shared.Held);
        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(BodyRole.Follow, second[0].Shared.Role);
        Assert.InRange(second[0].Pose.Y, 2.99f, 3.01f);
    }

    [Fact]
    public void When_a_player_leaves_what_they_were_carrying_goes_back_to_the_host()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var first = new FakeWorld(net.AddClient("Julian"), Yard());
        var second = new FakeWorld(net.AddClient("Friend"), Yard());
        net.Run(2);
        first.Grab(first[1]);
        net.Run(2);
        BodyPose lastSeen = host[1].Pose;

        first.Node.Session.Close();
        net.Run(1);

        Assert.Equal(0, host[1].Shared.Owner);
        Assert.False(host[1].Shared.Held);
        Assert.Equal(BodyRole.Simulate, host[1].Shared.Role);
        Assert.Equal(1, host[1].HandOffs);
        AssertSamePlace(lastSeen, host[1].Pose, 0.2, 20);
        Assert.Equal(0, second[1].Shared.Owner);
        AssertSamePlace(host[1].Pose, second[1].Pose);
        Assert.Contains("Player 1 left while simulating 1 object(s); the host has them again.", host.Log);
    }

    [Fact]
    public void A_player_cannot_move_what_it_does_not_simulate()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        // A made-up state for the host's bottle, and an attempt to hand back something that was never taken.
        var writer = new PacketWriter(128);
        writer.WriteByte(ObjectWire.MessageStatesUp);
        writer.WriteUInt32(1000);
        writer.WriteByte(1);
        writer.WriteUInt16(host[2].Shared.Id);
        BodyPose.At(500, 500, 500).Write(writer);
        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        writer.Reset();
        writer.WriteByte(ObjectWire.MessageRelease);
        writer.WriteUInt16(host[2].Shared.Id);
        BodyPose.At(500, 500, 500).Write(writer);
        default(BodyMotion).Write(writer);
        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(1);

        AssertSamePlace(Bottle, host[2].Pose, 0.0001);
        Assert.Equal(0, host[2].Shared.Owner);
        Assert.Equal(BodyRole.Simulate, host[2].Shared.Role);
        Assert.Equal(0, host[2].HandOffs);
    }

    [Fact]
    public void Rubbish_and_messages_sent_the_wrong_way_are_counted_not_fatal()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        byte[] wrongWay = { ObjectWire.MessageOwner, 1, 0, 1, 1 };
        client.Node.Session.Send(Protocol.HostPlayerId, wrongWay, 0, wrongWay.Length, true);
        byte[] cutShort = { ObjectWire.MessageStatesUp, 1, 2, 3, 4, 2, 9 };
        client.Node.Session.Send(Protocol.HostPlayerId, cutShort, 0, cutShort.Length, true);
        var nan = new PacketWriter(64);
        nan.WriteByte(ObjectWire.MessageRelease);
        nan.WriteUInt16(host[0].Shared.Id);
        nan.WriteSingle(float.NaN);
        nan.WriteBytes(new byte[40], 0, 40);
        client.Node.Session.Send(Protocol.HostPlayerId, nan.Buffer, 0, nan.Length, true);
        net.Run(1);

        Assert.Equal(3, host.Replication.Malformed);
        Assert.Equal(3, host.Replication.Shared);
        Assert.Single(host.Node.Log, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Dropped an object message from player 1"));
        Assert.DoesNotContain(host.Node.Log, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void A_client_asks_for_the_objects_only_once_its_own_world_is_loaded()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"));
        net.Run(3);
        Assert.Equal(0, client.Replication.Shared);
        Assert.DoesNotContain(host.Log, m => m.StartsWith("Sent player 1 the list"));

        // The loading screen is over: the client's objects appear, a few at a time.
        client.Add("booze(itemx)", Bottle);
        net.Run(1);
        client.Add("beer case(itemx)", CrateA);
        client.Add("beer case(itemx)", CrateB);
        net.Run(1);

        Assert.Equal(3, client.Replication.Shared);
        Assert.Equal(0, client.Replication.MissingHere);
        Assert.Contains("Sent player 1 the list of 3 object(s).", host.Log);
        Assert.Contains("Asked the host for its objects; this world has 1.", client.Log);
    }

    [Fact]
    public void A_host_whose_world_loads_later_announces_its_objects_as_they_appear()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        Assert.Equal(0, client.Replication.Shared);
        Assert.Equal(0, client.Replication.OnlyHere);

        foreach ((string name, BodyPose pose) in Yard())
        {
            host.Add(name, pose);
        }

        net.Run(1);

        Assert.Equal(3, client.Replication.Shared);
        Assert.All(client.Things, thing => Assert.Equal(BodyRole.Follow, thing.Shared.Role));
    }

    [Fact]
    public void An_object_that_disappears_from_the_host_s_world_is_reported_gone()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        Assert.True(client[2].Shared.Present);

        // The host's player drank the bottle.
        host.Remove(host[2]);
        net.Run(1);

        Assert.False(client[2].Shared.Present);
        Assert.Equal(2, host.Replication.Shared);
        Assert.Equal(3, client.Replication.Shared);
    }

    [Fact]
    public void An_object_the_host_s_world_loses_and_gets_back_is_paired_with_the_same_one_here()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        FakeWorld.Thing mine = client[2];
        ushort before = mine.Shared.Id;

        // The host's game switches the bottle off for a while, and it stops being shared.
        host.Remove(host[2]);
        net.Run(1);
        Assert.False(mine.Shared.Present);

        // It is back, somewhere else, and the host gives it a new number. The client still has one bottle.
        BodyPose back = Bottle.Moved(2, 0, 0);
        FakeWorld.Thing again = host.Add("booze(itemx)", back);
        net.Run(1);

        Assert.NotEqual(before, again.Shared.Id);
        Assert.Equal(again.Shared.Id, mine.Shared.Id);
        Assert.True(mine.Shared.Present);
        Assert.Equal(BodyRole.Follow, mine.Shared.Role);
        AssertSamePlace(back, mine.Pose);
        Assert.Equal(3, client.Replication.Shared);
        Assert.Equal(0, client.Replication.MissingHere);
        Assert.Equal(0, client.Replication.OnlyHere);
        Assert.Null(client.Replication.Find(before));

        // And it goes on like any other: the client picks it up and the host sees that.
        client.Grab(mine);
        net.Run(1);
        Assert.Equal(1, again.Shared.Owner);
        Assert.True(again.Shared.Held);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);
    }

    [Fact]
    public void What_the_host_s_world_lost_leaves_nothing_behind_when_it_goes_from_this_world_too()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        host.Remove(host[2]);
        net.Run(1);
        client.Remove(client[2]);
        net.Run(1);

        // Nothing of the host's is missing here: the host does not have it either.
        Assert.Equal(0, client.Replication.MissingHere);
        Assert.Equal(2, client.Replication.Shared);
        Assert.Equal(0, client.Replication.OnlyHere);
    }

    [Fact]
    public void An_object_that_disappears_from_a_client_s_world_stays_on_the_host_s_list()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        FakeWorld.Thing bottle = client[2];
        client.Remove(bottle);
        net.Run(1);

        Assert.Equal(2, client.Replication.Shared);
        Assert.Equal(1, client.Replication.MissingHere);
        Assert.Equal(BodyRole.Unmapped, bottle.Shared.Role);
        Assert.Equal(3, host.Replication.Shared);

        // If one turns up again, it takes the place of the missing one.
        FakeWorld.Thing again = client.Add("booze(itemx)", Bottle.Moved(3, 0, 0));
        net.Run(0.5);
        Assert.Equal(3, client.Replication.Shared);
        Assert.Equal(0, client.Replication.MissingHere);
        Assert.Equal(BodyRole.Follow, again.Shared.Role);
        AssertSamePlace(Bottle, again.Pose);
    }

    [Fact]
    public void With_more_objects_moving_than_fit_in_a_flush_they_take_turns()
    {
        var net = new SessionNet();
        (string, BodyPose)[] pile = Enumerable.Range(0, 200).Select(i => ("part " + i, BodyPose.At(i, 0, 0))).ToArray();
        var host = new FakeWorld(net.Host, pile);
        var client = new FakeWorld(net.AddClient("Julian"), pile);
        net.Run(3);

        // An earthquake: every one of them slides sideways for two seconds.
        for (int i = 0; i < host.Things.Count; i++)
        {
            int index = i;
            double start = host.Time;
            host[i].Path = t => BodyPose.At(index, 0, (float)(t - start));
            host[i].Awake = true;
            host[i].SettleAt = host.Time + 2;
        }

        long before = host.Replication.StatesSent;
        net.Run(1);

        // 80 a flush, 30 flushes a second: every object is refreshed 12 times a second instead of 30.
        long perSecond = host.Replication.StatesSent - before;
        Assert.InRange(perSecond, 2300, 2500);
        Assert.All(client.Things, thing => Assert.InRange(thing.Pose.Z, 0.5f, 1.05f));

        net.Run(3);
        for (int i = 0; i < host.Things.Count; i++)
        {
            AssertSamePlace(host[i].Pose, client[i].Pose);
        }
    }

    [Fact]
    public void Leaving_the_world_gives_every_object_back_to_the_game()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        IReadOnlyList<SharedBody> bodies = client.Things.Select(t => t.Shared).ToList();

        client.Replication.Detach();

        Assert.All(bodies, body => Assert.Equal(BodyRole.Unmapped, body.Role));
        Assert.Equal(0, client.Replication.Shared);
        Assert.Contains(client.Log, m => m.StartsWith("Objects when the session ended: 3 shared, 0 of the host's missing here, 0 only here;"));
    }
}

public class BodyPoseTests
{
    [Fact]
    public void A_pose_survives_the_wire_within_its_precision()
    {
        var pose = new BodyPose(-1234.567f, 3.25f, 987.125f, 0.1825742f, 0.3651484f, 0.5477226f, 0.7302967f);
        var writer = new PacketWriter(64);

        pose.Write(writer);
        BodyPose read = BodyPose.Read(new PacketReader(writer.Buffer, 0, writer.Length));

        Assert.Equal(BodyPose.Size, writer.Length);
        Assert.Equal(pose.X, read.X);
        Assert.Equal(pose.Y, read.Y);
        Assert.Equal(pose.Z, read.Z);
        Assert.True(pose.AngleTo(read) < 0.02, pose.AngleTo(read) + " degrees");
    }

    [Fact]
    public void A_rotation_of_all_zeros_is_read_as_no_rotation()
    {
        var writer = new PacketWriter(64);
        new BodyPose(1, 2, 3, 0, 0, 0, 0).Write(writer);

        BodyPose read = BodyPose.Read(new PacketReader(writer.Buffer, 0, writer.Length));

        Assert.Equal(1f, read.QW);
        Assert.Equal(0f, read.QX);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(1e7f)]
    public void A_position_nothing_can_be_at_is_refused(float coordinate)
    {
        var writer = new PacketWriter(64);
        new BodyPose(0, coordinate, 0, 0, 0, 0, 1).Write(writer);

        Assert.Throws<PacketFormatException>(() => BodyPose.Read(new PacketReader(writer.Buffer, 0, writer.Length)));
    }

    [Fact]
    public void A_rotation_is_interpolated_the_short_way_round()
    {
        // The same half turn about the vertical, written with either sign.
        var a = new BodyPose(0, 0, 0, 0, 0, 0, 1);
        var b = new BodyPose(2, 0, 0, 0, -1, 0, 0);
        var sameAsB = new BodyPose(2, 0, 0, 0, 1, 0, 0);

        BodyPose middle = a.LerpTo(b, 0.5);
        BodyPose other = a.LerpTo(sameAsB, 0.5);

        Assert.Equal(1f, middle.X);
        Assert.Equal(90.0, a.AngleTo(middle), 3);
        Assert.Equal(90.0, a.AngleTo(other), 3);
        Assert.Equal(180.0, a.AngleTo(b), 3);
        Assert.Equal(0.0, b.AngleTo(sameAsB), 3);
        Assert.Equal(1.0, Math.Sqrt((middle.QX * middle.QX) + (middle.QY * middle.QY) + (middle.QZ * middle.QZ) + (middle.QW * middle.QW)), 5);
    }

    [Fact]
    public void A_motion_survives_the_wire_and_absurd_speeds_are_refused()
    {
        var motion = new BodyMotion { VX = 1.5f, VY = -9.81f, VZ = 0.25f, AX = 3, AY = -2, AZ = 1 };
        var writer = new PacketWriter(64);

        motion.Write(writer);
        BodyMotion read = BodyMotion.Read(new PacketReader(writer.Buffer, 0, writer.Length));

        Assert.Equal(BodyMotion.Size, writer.Length);
        Assert.Equal(motion.VY, read.VY);
        Assert.Equal(motion.AZ, read.AZ);

        writer.Reset();
        new BodyMotion { VX = float.NaN }.Write(writer);
        Assert.Throws<PacketFormatException>(() => BodyMotion.Read(new PacketReader(writer.Buffer, 0, writer.Length)));
    }
}
