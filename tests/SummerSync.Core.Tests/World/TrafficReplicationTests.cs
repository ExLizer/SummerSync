using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class TrafficReplicationTests
{
    private const int Cars = 12;
    private const uint Shape = 0x7AFF1C;

    private sealed class Game
    {
        public Game(SessionNet.Node node, uint shape = Shape)
        {
            Node = node;
            Traffic = new TrafficReplication(node.Session, node.Logger);
            Traffic.Define(Cars, shape);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Traffic.Update(time);
                Now = time;
                Drive?.Invoke(time);
            };
        }

        public SessionNet.Node Node { get; }

        public TrafficReplication Traffic { get; }

        public double Now { get; private set; }

        public Action<double>? Drive { get; set; }
    }

    // The host's traffic: car 3 drives north at 20 m/s on the highway, car 7 stands with its brake lights on.
    private static TrafficCar[] Highway(double time)
    {
        var cars = new TrafficCar[Cars];
        cars[3] = new TrafficCar { Active = true, Pose = BodyPose.At(100f, 2f, (float)(time * 20.0)), Speed = 20f, Rpm = 3100f };
        cars[7] = new TrafficCar { Active = true, Pose = BodyPose.At(-50f, 1f, 5f), Speed = 0f, Rpm = 700f, Flags = TrafficWire.LightBrake };
        return cars;
    }

    [Fact]
    public void A_client_shows_the_hosts_cars_where_the_host_had_them_a_moment_ago()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40, JitterMs = 10, LossPercent = 5 });
        var host = new Game(net.Host);
        var guest = new Game(net.AddClient("Julian"));
        host.Drive = time => host.Traffic.Report(time, TrafficWire.GroupHighway, Highway(time));
        net.Run(3);

        Assert.True(guest.Traffic.HasState);
        Assert.Equal(TrafficWire.GroupHighway, guest.Traffic.Groups);
        Assert.True(guest.Traffic.IsActive(3));
        Assert.True(guest.Traffic.IsActive(7));
        Assert.False(guest.Traffic.IsActive(0));
        Assert.True(guest.Traffic.Sample(3, guest.Now, out BodyPose shown));

        // About a tenth of a second and a link's delay behind: between one and nine metres back along its road.
        double behind = (host.Now * 20.0) - shown.Z;
        Assert.InRange(behind, 1.0, 9.0);
        Assert.Equal(100f, shown.X, 2);
        Assert.Equal(20f, guest.Traffic.Latest(3).Speed, 2);
        Assert.Equal(3100f, guest.Traffic.Latest(3).Rpm);
        Assert.Equal(TrafficWire.LightBrake, guest.Traffic.Latest(7).Flags);
    }

    [Fact]
    public void The_host_says_it_about_fifteen_times_a_second()
    {
        var net = new SessionNet();
        var host = new Game(net.Host);
        var guest = new Game(net.AddClient("Julian"));
        host.Drive = time => host.Traffic.Report(time, TrafficWire.GroupHighway, Highway(time));
        net.Run(1);
        long before = host.Traffic.Sent;
        net.Run(2);

        Assert.InRange(host.Traffic.Sent - before, 28, 32);
        Assert.True(guest.Traffic.Received > 20);
    }

    [Fact]
    public void A_car_the_host_switches_off_is_off_for_the_client()
    {
        var net = new SessionNet();
        var host = new Game(net.Host);
        var guest = new Game(net.AddClient("Julian"));
        bool off = false;
        host.Drive = time =>
        {
            TrafficCar[] cars = Highway(time);
            cars[3].Active = !off;
            host.Traffic.Report(time, off ? (byte)0 : TrafficWire.GroupHighway, cars);
        };
        net.Run(2);
        Assert.True(guest.Traffic.IsActive(3));

        off = true;
        net.Run(1);

        Assert.False(guest.Traffic.IsActive(3));
        Assert.False(guest.Traffic.Sample(3, guest.Now, out _));
        Assert.Equal(0, guest.Traffic.Groups);
    }

    [Fact]
    public void A_client_with_its_traffic_listed_differently_shows_nothing()
    {
        var net = new SessionNet();
        var host = new Game(net.Host);
        var guest = new Game(net.AddClient("Julian"), Shape + 1);
        host.Drive = time => host.Traffic.Report(time, TrafficWire.GroupHighway, Highway(time));
        net.Run(2);

        Assert.False(guest.Traffic.HasState);
        Assert.True(guest.Traffic.Malformed > 0);
    }

    [Fact]
    public void Only_the_host_says_where_the_traffic_is()
    {
        var net = new SessionNet();
        var host = new Game(net.Host);
        var guest = new Game(net.AddClient("Julian"));
        guest.Drive = time => Assert.False(guest.Traffic.Report(time, TrafficWire.GroupHighway, Highway(time)));
        net.Run(1);

        Assert.False(host.Traffic.HasState);
        Assert.Equal(0, guest.Traffic.Sent);
    }

    [Fact]
    public void The_traffics_message_follows_the_last_ones_in_use()
    {
        Assert.Equal(85, TrafficWire.MessageState);
        Assert.Equal(86, PeopleWire.MessageState);
        Assert.Equal(87, NpcWire.MessageState);
    }

    [Fact]
    public void What_an_npc_shows_and_plays_reaches_the_guest_and_not_the_traffic()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host);
        var guest = new Game(net.AddClient("Julian"));
        var hostNpcs = new TrafficReplication(net.Host.Session, net.Host.Logger, NpcWire.MessageState);
        var guestNpcs = new TrafficReplication(guest.Node.Session, guest.Node.Logger, NpcWire.MessageState);
        hostNpcs.Define(5, 0x7A1A);
        guestNpcs.Define(5, 0x7A1A);
        var npcs = new TrafficCar[5];
        npcs[0] = new TrafficCar { Active = true, Pose = BodyPose.At(700f, 0f, -940f), Speed = 30f, Flags = 1, Clip = 0 };
        npcs[4] = new TrafficCar { Active = true, Pose = BodyPose.At(638f, 1f, -657f), Rpm = 4000f, Flags = 0, Clip = 3 };
        host.Drive = time =>
        {
            host.Traffic.Report(time, TrafficWire.GroupHighway, Highway(time));
            npcs[0].Pose = BodyPose.At(700f, 0f, -940f + (float)time * 30f);
            hostNpcs.Report(time, 0, npcs);
        };
        guest.Drive = time => guestNpcs.Update(time);
        net.Run(2);

        Assert.True(guestNpcs.IsActive(0));
        Assert.Equal(1, guestNpcs.Latest(0).Flags);
        Assert.Equal(3, guestNpcs.Latest(4).Clip);
        Assert.Equal(4000f, guestNpcs.Latest(4).Rpm);
        Assert.False(guestNpcs.IsActive(2));
        Assert.True(guestNpcs.Sample(0, guest.Now, out BodyPose shown));
        Assert.InRange(shown.Z, -940f + 30f, -940f + (float)host.Now * 30f);
        Assert.Equal(0, guestNpcs.Malformed);
        Assert.Equal(0, guest.Traffic.Malformed);
        Assert.True(guest.Traffic.IsActive(3));
    }

    private static readonly string[] Walkers = { "Alpo", "Julli", "Kale", "Kristian", "Rauno", "Unto" };

    /// <summary>The host's walkers by name and a guest's, each with its own list, the host's all walking 10 m apart.</summary>
    private sealed class Walk
    {
        public Walk(string[] hostNames, string[]? guestNames, uint guestShape = 0)
        {
            Net = new SessionNet(new NetworkConditions { LatencyMs = 40, JitterMs = 10, LossPercent = 5 });
            Host = new TrafficReplication(Net.Host.Session, Net.Host.Logger, PeopleWire.MessageState);
            Guest = Net.AddClient("Julian");
            GuestPeople = new TrafficReplication(Guest.Session, Guest.Logger, PeopleWire.MessageState);
            Host.Define(hostNames);
            if (guestNames != null)
            {
                GuestPeople.Define(guestNames);
            }
            else
            {
                GuestPeople.Define(hostNames.Length, guestShape);
            }

            Walking = new TrafficCar[hostNames.Length];
            for (int i = 0; i < Walking.Length; i++)
            {
                Walking[i] = new TrafficCar { Active = true, Pose = BodyPose.At(10f * i, 0f, 0f), Speed = 1.2f, Flags = i % 2 == 0 ? PeopleWire.Walking : PeopleWire.Standing };
            }

            Net.Host.BeforeUpdate = time =>
            {
                Host.Update(time);
                Host.Report(time, 0, Walking);
            };
            Guest.BeforeUpdate = time =>
            {
                GuestPeople.Update(time);
                GuestNow = time;
            };
        }

        public SessionNet Net { get; }

        public TrafficReplication Host { get; }

        public SessionNet.Node Guest { get; }

        public TrafficReplication GuestPeople { get; }

        public TrafficCar[] Walking { get; }

        public double GuestNow { get; private set; }
    }

    [Fact]
    public void Walkers_both_games_have_are_shared_by_name_when_one_is_gone_from_the_hosts()
    {
        string[] alive = Walkers.Where(name => name != "Kale").ToArray();
        var walk = new Walk(alive, Walkers);
        walk.Net.Run(2);

        TrafficReplication guest = walk.GuestPeople;
        Assert.True(guest.HasState);
        Assert.True(guest.ByName);
        Assert.Equal(0, guest.Unmatched);
        Assert.Equal(0, guest.Malformed);
        Assert.Equal(0, guest.Groups);
        Assert.False(guest.IsActive(Array.IndexOf(Walkers, "Kale")));
        for (int i = 0; i < alive.Length; i++)
        {
            int here = Array.IndexOf(Walkers, alive[i]);
            Assert.True(guest.IsActive(here));
            Assert.Equal(walk.Walking[i].Flags, guest.Latest(here).Flags);
            Assert.True(guest.Sample(here, walk.GuestNow, out BodyPose shown));
            Assert.Equal(10f * i, shown.X, 2);
        }
    }

    [Fact]
    public void A_walker_only_the_host_has_is_counted_and_left_out()
    {
        string[] fewer = Walkers.Where(name => name != "Julli").ToArray();
        var walk = new Walk(Walkers, fewer);
        walk.Net.Run(2);

        TrafficReplication guest = walk.GuestPeople;
        Assert.True(guest.ByName);
        Assert.Equal(1, guest.Unmatched);
        Assert.Equal(0, guest.Malformed);
        for (int i = 0; i < fewer.Length; i++)
        {
            Assert.True(guest.IsActive(i));
            Assert.Equal(10f * Array.IndexOf(Walkers, fewer[i]), guest.Latest(i).Pose.X, 2);
        }
    }

    [Fact]
    public void The_same_walkers_go_by_their_place_and_one_switched_off_is_off()
    {
        var walk = new Walk(Walkers, Walkers);
        walk.Net.Run(1);
        Assert.False(walk.GuestPeople.ByName);
        Assert.True(walk.GuestPeople.IsActive(2));

        walk.Walking[2].Active = false;
        walk.Net.Run(1);

        Assert.False(walk.GuestPeople.IsActive(2));
        Assert.True(walk.GuestPeople.IsActive(3));
        Assert.Equal(0, walk.GuestPeople.Malformed);
    }

    [Fact]
    public void A_list_given_by_its_shape_hears_a_host_whose_list_has_the_same_names()
    {
        // As netprobe does, with the shape the game's log says.
        var same = new Walk(Walkers, null, TrafficReplication.ShapeOf(Walkers));
        same.Net.Run(1);
        Assert.True(same.GuestPeople.IsActive(5));
        Assert.Equal(0, same.GuestPeople.Malformed);

        var other = new Walk(Walkers, null, TrafficReplication.ShapeOf(Walkers) + 1);
        other.Net.Run(1);
        Assert.False(other.GuestPeople.HasState);
        Assert.True(other.GuestPeople.Malformed > 0);
    }

    [Fact]
    public void The_shape_of_a_list_of_names_is_the_one_the_log_has_always_shown()
    {
        uint shape = 2166136261;
        foreach (string name in Walkers)
        {
            foreach (char c in name)
            {
                shape = (shape ^ c) * 16777619;
            }

            shape = (shape ^ '\n') * 16777619;
        }

        Assert.Equal(shape, TrafficReplication.ShapeOf(Walkers));
        Assert.NotEqual(TrafficReplication.KeyOf("Kale"), TrafficReplication.KeyOf("Alpo"));
    }

    [Fact]
    public void The_people_and_the_traffic_do_not_hear_each_other()
    {
        var net = new SessionNet();
        var host = new Game(net.Host);
        var guest = new Game(net.AddClient("Julian"));
        var hostPeople = new TrafficReplication(net.Host.Session, net.Host.Logger, PeopleWire.MessageState);
        var guestPeople = new TrafficReplication(guest.Node.Session, guest.Node.Logger, PeopleWire.MessageState);
        hostPeople.Define(6, 0x9E0B1E);
        guestPeople.Define(6, 0x9E0B1E);
        var walkers = new TrafficCar[6];
        walkers[2] = new TrafficCar { Active = true, Pose = BodyPose.At(10f, 0f, 10f), Speed = 1.2f, Flags = PeopleWire.Walking };
        host.Drive = time =>
        {
            host.Traffic.Report(time, TrafficWire.GroupHighway, Highway(time));
            hostPeople.Report(time, 0, walkers);
        };
        guest.Drive = time => guestPeople.Update(time);
        net.Run(2);

        Assert.True(guestPeople.IsActive(2));
        Assert.Equal(PeopleWire.Walking, guestPeople.Latest(2).Flags);
        Assert.False(guestPeople.IsActive(3));
        Assert.True(guest.Traffic.IsActive(3));
        Assert.Equal(0, guestPeople.Malformed);
        Assert.Equal(0, guest.Traffic.Malformed);
    }
}
