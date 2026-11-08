using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class WorldClockTests
{
    private static WorldClock Noon(int day = 3, float elapsed = 0) =>
        new(day, 12, elapsed) { SunAngle = 330, HourHand = 0, MinuteHand = 0, Minutes = 0 };

    [Fact]
    public void A_clock_survives_the_wire_within_its_precision()
    {
        var clock = new WorldClock(7, 24, 599.99f) { SunAngle = 359.99f, HourHand = 58.5f, MinuteHand = 342.06f, Minutes = 59.999f };
        var writer = new PacketWriter(32);
        clock.Write(writer);
        Assert.Equal(WorldClock.Size, writer.Length);

        WorldClock read = WorldClock.Read(new PacketReader(writer.Buffer, 0, writer.Length));
        Assert.Equal(7, read.Day);
        Assert.Equal(24, read.Hour);
        Assert.Equal(599.99f, read.Elapsed, 2);
        Assert.True(Math.Abs(WorldClock.AngleBetween(359.99f, read.SunAngle)) < 0.01f);
        Assert.Equal(58.5f, read.HourHand, 2);
        Assert.Equal(342.06f, read.MinuteHand, 2);
        Assert.Equal(59.999f, read.Minutes, 2);
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(8, 12)]
    [InlineData(3, 0)]
    [InlineData(3, 13)]
    [InlineData(3, 26)]
    public void A_time_the_game_cannot_be_at_is_refused(int day, int hour)
    {
        var writer = new PacketWriter(32);
        new WorldClock(day, hour, 10).Write(writer);
        Assert.Throws<PacketFormatException>(() => WorldClock.Read(new PacketReader(writer.Buffer, 0, writer.Length)));
    }

    [Fact]
    public void The_block_that_starts_at_midnight_is_the_first_of_its_day()
    {
        Assert.Equal(0, new WorldClock(1, 24, 0).Block);
        Assert.Equal(1, new WorldClock(1, 2, 0).Block);
        Assert.Equal(11, new WorldClock(1, 22, 0).Block);
        Assert.Equal(0.0, new WorldClock(1, 24, 0).SecondsIntoWeek);
        Assert.Equal(600.0, new WorldClock(1, 2, 0).SecondsIntoWeek);
        Assert.Equal(7200.0, new WorldClock(2, 24, 0).SecondsIntoWeek);
    }

    [Fact]
    public void Time_passes_into_the_next_block_the_next_day_and_the_next_week()
    {
        WorldClock later = Noon(elapsed: 590).Later(20);
        Assert.Equal(3, later.Day);
        Assert.Equal(14, later.Hour);
        Assert.Equal(10f, later.Elapsed, 3);

        // The last block of the day runs into midnight, which the game calls 24 and counts as the next day.
        later = new WorldClock(3, 22, 595).Later(10);
        Assert.Equal(4, later.Day);
        Assert.Equal(24, later.Hour);
        Assert.Equal(5f, later.Elapsed, 3);

        later = new WorldClock(7, 22, 595).Later(10);
        Assert.Equal(1, later.Day);
        Assert.Equal(24, later.Hour);

        // No time at all changes nothing, not even by rounding.
        WorldClock same = Noon(elapsed: 123.456f).Later(0);
        Assert.Equal(123.456f, same.Elapsed);
    }

    [Fact]
    public void The_sun_and_the_hands_move_on_at_the_game_s_speeds()
    {
        // One block: the sun a twelfth of the way round, the hour hand two hours, the minute hand twice round.
        WorldClock later = Noon().Later(600);
        Assert.True(Math.Abs(WorldClock.AngleBetween(0f, later.SunAngle)) < 0.01f, "sun at " + later.SunAngle);
        Assert.True(Math.Abs(WorldClock.AngleBetween(300f, later.HourHand)) < 0.01f, "hour hand at " + later.HourHand);
        Assert.True(Math.Abs(WorldClock.AngleBetween(0f, later.MinuteHand)) < 0.01f, "minute hand at " + later.MinuteHand);
        Assert.Equal(0f, later.Minutes, 2);

        // A quarter of an hour of the game is 75 seconds.
        later = Noon().Later(75);
        Assert.True(Math.Abs(WorldClock.AngleBetween(270f, later.MinuteHand)) < 0.01f, "minute hand at " + later.MinuteHand);
        Assert.Equal(15f, later.Minutes, 2);
    }

    [Fact]
    public void How_far_ahead_one_clock_is_of_another_goes_the_short_way_round_the_week()
    {
        Assert.Equal(5.0, Noon(elapsed: 15).Ahead(Noon(elapsed: 10)), 6);
        Assert.Equal(-5.0, Noon(elapsed: 10).Ahead(Noon(elapsed: 15)), 6);

        // Across a block: two seconds before the turn against one second after it.
        Assert.Equal(3.0, new WorldClock(3, 14, 1).Ahead(Noon(elapsed: 598)), 6);

        // Across the week: Monday just after midnight is ahead of Sunday just before it.
        Assert.Equal(4.0, new WorldClock(1, 24, 2).Ahead(new WorldClock(7, 22, 598)), 6);
        Assert.Equal(-4.0, new WorldClock(7, 22, 598).Ahead(new WorldClock(1, 24, 2)), 6);
        Assert.True(Noon().SameBlock(Noon(elapsed: 500)));
        Assert.False(Noon().SameBlock(Noon(day: 4)));
    }

    [Fact]
    public void A_weather_survives_the_wire_and_one_the_game_does_not_have_is_refused()
    {
        var weather = new WeatherState { X = 1650.448f, Z = -3076.79f, Yaw = 46.32f, Kind = WeatherState.Thunder, Cloud = 4, ThunderForce = 321.5f };
        var writer = new PacketWriter(32);
        weather.Write(writer);
        Assert.Equal(WeatherState.Size, writer.Length);
        WeatherState read = WeatherState.Read(new PacketReader(writer.Buffer, 0, writer.Length));
        Assert.Equal(1650.448f, read.X);
        Assert.Equal(-3076.79f, read.Z);
        Assert.Equal(46.32f, read.Yaw, 2);
        Assert.Equal(WeatherState.Thunder, read.Kind);
        Assert.Equal(4, read.Cloud);
        Assert.Equal(321.5f, read.ThunderForce);
        Assert.True(read.SameFront(weather));

        // The front moving on is the same front; anything else about it changing is not.
        WeatherState moved = weather;
        moved.X -= 400;
        Assert.True(moved.SameFront(weather));
        moved.Kind = WeatherState.Dry;
        Assert.False(moved.SameFront(weather));

        foreach (WeatherState bad in new[]
        {
            new WeatherState { Kind = 3 },
            new WeatherState { Cloud = 7 },
            new WeatherState { X = float.NaN },
            new WeatherState { ThunderForce = -1 },
        })
        {
            var broken = new PacketWriter(32);
            bad.Write(broken);
            Assert.Throws<PacketFormatException>(() => WeatherState.Read(new PacketReader(broken.Buffer, 0, broken.Length)));
        }
    }
}

public class WorldReplicationTests
{
    // A machine with a world of its own in which time passes at the game's pace from wherever it was set.
    private sealed class World
    {
        private readonly SessionNet.Node node;
        private WorldClock start;
        private double startedAt;
        private WeatherState front;
        private double frontAt;

        public World(SessionNet.Node node, WorldClock start, WeatherState front)
        {
            this.node = node;
            this.start = start;
            this.front = front;
            Replication = new WorldReplication(node.Session, node.Logger);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Now = time;
                Replication.Update(time);
                if (InWorld && node.Session.Role == SessionRole.Host)
                {
                    Replication.SetClock(Clock);
                    Replication.SetWeather(Front, Speed);
                }
            };
        }

        public const float Speed = -4f;

        public WorldReplication Replication { get; }

        public double Now { get; private set; }

        public bool InWorld { get; set; } = true;

        public WorldClock Clock => start.Later(Now - startedAt);

        public WeatherState Front
        {
            get
            {
                WeatherState moved = front;
                moved.X += (float)(Speed * (Now - frontAt));
                return moved;
            }
        }

        /// <summary>The player sleeps, or the save is loaded: it is suddenly another time.</summary>
        public void SetClock(WorldClock clock)
        {
            start = clock;
            startedAt = Now;
        }

        public void SetFront(WeatherState weather)
        {
            front = weather;
            frontAt = Now;
        }
    }

    private static readonly WeatherState Rainy = new() { X = 1650, Z = 3076, Yaw = 46, Kind = WeatherState.Rain, Cloud = 2 };

    [Fact]
    public void A_client_knows_what_time_the_host_has_between_one_clock_and_the_next()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new World(net.Host, new WorldClock(3, 12, 100) { SunAngle = 335 }, Rainy);
        var client = new World(net.AddClient("Julian"), new WorldClock(1, 24, 0), default);
        Assert.False(client.Replication.TryGetClock(out _));

        net.Run(1);
        Assert.True(client.Replication.TryGetClock(out WorldClock heard));
        Assert.Equal(3, heard.Day);
        Assert.Equal(12, heard.Hour);

        // In step all along, not only right after a clock arrives: within the delay of the network.
        for (int i = 0; i < 40; i++)
        {
            net.Run(0.37);
            Assert.True(client.Replication.TryGetClock(out heard));
            Assert.True(Math.Abs(heard.Ahead(host.Clock)) < 0.15, $"{heard.Ahead(host.Clock):0.000} s apart after {net.Now:0.0} s");
            Assert.True(Math.Abs(WorldClock.AngleBetween(heard.SunAngle, host.Clock.SunAngle)) < 0.05f);
        }

        // That was a clock every five seconds or so, not one per frame.
        Assert.InRange(host.Replication.Clocks, 3, 6);
        Assert.Equal(host.Replication.Clocks, client.Replication.Clocks);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);
    }

    [Fact]
    public void A_radio_with_the_longest_clip_name_is_told()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new World(net.Host, new WorldClock(3, 12, 100), Rainy);
        var client = new World(net.AddClient("Julian"), new WorldClock(1, 24, 0), default);
        net.Run(1);

        // Seen on 2026-10-18: a clip name of 58 bytes did not fit in what one message had room for, and no radio went out.
        string clip = new string('x', RadioState.MaxClipBytes);
        host.Replication.SetRadio(1, new RadioState { Playing = true, Position = 12.5f, Clip = clip });
        net.Run(1);

        Assert.True(client.Replication.TryGetRadio(1, out RadioState heard));
        Assert.Equal(clip, heard.Clip);
        // Playing, so on since it was said.
        Assert.InRange(heard.Position, 12.5f, 14.5f);
    }

    [Fact]
    public void A_jump_in_time_is_told_at_once()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new World(net.Host, new WorldClock(3, 22, 100), Rainy);
        var client = new World(net.AddClient("Julian"), new WorldClock(1, 24, 0), default);
        net.Run(2);
        long before = host.Replication.Clocks;

        // The host's player sleeps until six in the morning of the next day.
        host.SetClock(new WorldClock(4, 6, 0));
        net.Run(0.3);

        Assert.Equal(before + 1, host.Replication.Clocks);
        Assert.True(client.Replication.TryGetClock(out WorldClock heard));
        Assert.Equal(4, heard.Day);
        Assert.Equal(6, heard.Hour);
        Assert.True(Math.Abs(heard.Ahead(host.Clock)) < 0.15);
    }

    [Fact]
    public void Whoever_joins_later_is_told_without_waiting_for_the_next_turn()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new World(net.Host, new WorldClock(5, 8, 300), Rainy);
        var first = new World(net.AddClient("Julian"), new WorldClock(1, 24, 0), default);
        net.Run(1.2);

        var second = new World(net.AddClient("Friend"), new WorldClock(1, 24, 0), default);
        net.Run(1.0);

        Assert.True(second.Replication.TryGetClock(out WorldClock heard));
        Assert.Equal(5, heard.Day);
        Assert.Equal(8, heard.Hour);
        Assert.True(second.Replication.TryGetWeather(out WeatherState weather, out double age));
        Assert.Equal(WeatherState.Rain, weather.Kind);
        Assert.True(age < 1.0);
        Assert.True(first.Replication.TryGetClock(out _));
    }

    [Fact]
    public void The_front_is_told_now_and_then_and_at_once_when_it_becomes_another()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new World(net.Host, new WorldClock(3, 12, 0), Rainy);
        var client = new World(net.AddClient("Julian"), new WorldClock(1, 24, 0), default);
        net.Run(25);

        // The front moves at its own pace: where the client takes it to be is where the host has it.
        Assert.True(client.Replication.TryGetWeather(out WeatherState heard, out double age));
        Assert.True(Math.Abs((heard.X + (World.Speed * age)) - host.Front.X) < 1.0, "the front is where the host has it");
        Assert.InRange(host.Replication.Weathers, 3, 4);
        int serial = client.Replication.WeatherSerial;

        // It reaches the edge of the map and the game makes a new one somewhere else.
        host.SetFront(new WeatherState { X = 18000, Z = -1200, Yaw = 200, Kind = WeatherState.Thunder, Cloud = 5, ThunderForce = 250 });
        net.Run(0.3);
        Assert.True(client.Replication.TryGetWeather(out heard, out age));
        Assert.Equal(serial + 1, client.Replication.WeatherSerial);
        Assert.Equal(WeatherState.Thunder, heard.Kind);
        Assert.Equal(5, heard.Cloud);
        Assert.Equal(-1200f, heard.Z);
    }

    [Fact]
    public void A_host_without_a_world_says_nothing_and_only_the_host_is_believed()
    {
        var net = new SessionNet();
        var host = new World(net.Host, new WorldClock(3, 12, 0), Rainy) { InWorld = false };
        SessionNet.Node node = net.AddClient("Julian");
        var client = new World(node, new WorldClock(1, 24, 0), default);
        net.Run(8);
        Assert.Equal(0, host.Replication.Clocks);
        Assert.False(client.Replication.TryGetClock(out _));

        // A client that says what time it is, is not listened to.
        var writer = new PacketWriter(32);
        writer.WriteByte(WorldWire.MessageClock);
        new WorldClock(6, 4, 50).Write(writer);
        node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(1);
        Assert.Equal(1, host.Replication.Malformed);
        Assert.False(host.Replication.TryGetClock(out _));

        // The host's world loads: from then on it is told.
        host.InWorld = true;
        net.Run(1);
        Assert.True(client.Replication.TryGetClock(out WorldClock heard));
        Assert.Equal(12, heard.Hour);
    }

    [Fact]
    public void World_messages_are_game_messages_of_their_own()
    {
        Assert.True(WorldWire.MessageClock >= Protocol.FirstUserMessage);
        Assert.Equal(56, WorldWire.MessageClock);
        Assert.Equal(57, WorldWire.MessageWeather);
    }
}
