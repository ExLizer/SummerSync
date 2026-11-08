using SummerSync.Core.Logging;
using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

/// <summary>A host and any number of clients on one in-memory network, driven by a manual clock.</summary>
internal sealed class SessionNet
{
    public const ulong HostPeer = 100;

    private readonly LoopbackNetwork network = new();
    private readonly List<Node> nodes = new();
    private readonly int seed;

    public SessionNet(NetworkConditions? conditions = null, int seed = 42)
    {
        Conditions = conditions ?? new NetworkConditions();
        this.seed = seed;
        Host = Add(HostPeer, 0, null);
        Host.Session.StartHost("Host");
    }

    public NetworkConditions Conditions { get; }

    public Node Host { get; }

    public double Now { get; private set; }

    public LoopbackNetwork Network => network;

    public Node AddClient(string name, double clockSkew = 0, Action<SessionConfig>? configure = null)
    {
        Node node = Add((ulong)(200 + nodes.Count), clockSkew, configure);
        node.Session.Connect(HostPeer, name);
        return node;
    }

    public ITransport AddRawEndpoint(ulong id) => network.CreateEndpoint(id);

    /// <summary>Stops running a node's session, as if its process had died. Its transport keeps working.</summary>
    public void Kill(Node node) => node.Dead = true;

    /// <summary>Kills a node and takes it off the network: nothing goes in or out any more.</summary>
    public void Vanish(Node node)
    {
        Kill(node);
        network.RemoveEndpoint(node.Peer);
    }

    public void Run(double seconds)
    {
        const double step = 1.0 / 60.0;
        double end = Now + seconds;
        while (Now < end)
        {
            Now += step;
            foreach (Node node in nodes)
            {
                node.Transport.Update(Now);
            }

            foreach (Node node in nodes)
            {
                // Each machine reads its own clock; only the differences are shared.
                if (!node.Dead)
                {
                    node.BeforeUpdate?.Invoke(Now + node.ClockSkew);
                    node.Session.Update(Now + node.ClockSkew);
                }
            }
        }
    }

    private Node Add(ulong peer, double clockSkew, Action<SessionConfig>? configure)
    {
        var config = new SessionConfig { ModVersion = "0.1.0", GameVersion = "5.0.0f4/1" };
        configure?.Invoke(config);
        var transport = new SimulatedTransport(network.CreateEndpoint(peer), Conditions, seed + nodes.Count);
        var log = new Logger { MinimumLevel = LogLevel.Trace };
        var node = new Node(peer, transport, new NetSession(transport, config, log), clockSkew, log);
        nodes.Add(node);
        return node;
    }

    internal sealed class Node
    {
        public Node(ulong peer, SimulatedTransport transport, NetSession session, double clockSkew, Logger log)
        {
            Peer = peer;
            Transport = transport;
            Session = session;
            ClockSkew = clockSkew;
            Logger = log;
            log.AddSink(new Collector(Log));
            session.Connected += () => Events.Add("connected");
            session.Disconnected += (reason, text) =>
            {
                Events.Add("disconnected:" + reason);
                LastDisconnectText = text;
            };
            session.PlayerJoined += player => Events.Add("joined:" + player.Id + ":" + player.Name);
            session.PlayerLeft += (player, reason) => Events.Add("left:" + player.Id + ":" + reason);
            session.MessageReceived += (from, data, reliable) => Messages.Add((from, data, reliable));
        }

        public ulong Peer { get; }

        public SimulatedTransport Transport { get; }

        public NetSession Session { get; }

        public double ClockSkew { get; }

        public bool Dead { get; set; }

        public Logger Logger { get; }

        /// <summary>Runs with the node's clock right before its session is updated, like the game's own frame code.</summary>
        public Action<double>? BeforeUpdate { get; set; }

        public List<string> Events { get; } = new();

        public List<(byte From, byte[] Data, bool Reliable)> Messages { get; } = new();

        public List<LogEntry> Log { get; } = new();

        public string? LastDisconnectText { get; private set; }

        public string PlayerNames => string.Join(",", Session.Players.OrderBy(p => p.Id).Select(p => p.Id + "=" + p.Name));

        private sealed class Collector(List<LogEntry> entries) : ILogSink
        {
            public void Write(LogEntry entry) => entries.Add(entry);
        }
    }
}

public class NetSessionTests
{
    private static readonly byte[] Hello = [Protocol.FirstUserMessage, 1, 2, 3];

    [Fact]
    public void A_client_connects_and_both_sides_agree_on_the_players()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");

        net.Run(1);

        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal(1, client.Session.LocalPlayerId);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);
        Assert.Equal("0=Host,1=Julian", client.PlayerNames);
        Assert.Equal(new[] { "joined:1:Julian" }, net.Host.Events);
        Assert.Equal(new[] { "connected", "joined:0:Host" }, client.Events);
        Assert.True(client.Session.Players.Single(p => p.Id == 1).IsLocal);
        Assert.True(net.Host.Session.Players.Single(p => p.Id == 0).IsHost);
    }

    [Fact]
    public void Several_clients_get_distinct_ids_and_the_same_list()
    {
        var net = new SessionNet();
        SessionNet.Node first = net.AddClient("A");
        SessionNet.Node second = net.AddClient("B");
        SessionNet.Node third = net.AddClient("C");

        net.Run(1);

        Assert.Equal("0=Host,1=A,2=B,3=C", net.Host.PlayerNames);
        Assert.All(new[] { first, second, third }, node => Assert.Equal("0=Host,1=A,2=B,3=C", node.PlayerNames));
        Assert.Equal(new byte[] { 1, 2, 3 }, new[] { first, second, third }.Select(n => n.Session.LocalPlayerId));
        Assert.Contains("joined:2:B", first.Events);
    }

    [Theory]
    [InlineData("0.2.0", "5.0.0f4/1", DisconnectReason.ModVersionMismatch)]
    [InlineData("0.1.0", "5.0.0f4/2", DisconnectReason.GameVersionMismatch)]
    public void A_different_mod_or_game_version_is_rejected_with_an_explanation(string modVersion, string gameVersion, DisconnectReason expected)
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian", configure: config =>
        {
            config.ModVersion = modVersion;
            config.GameVersion = gameVersion;
        });

        net.Run(1);

        Assert.Equal(SessionRole.None, client.Session.Role);
        Assert.Equal(new[] { "disconnected:" + expected }, client.Events);
        Assert.Contains("Host runs", client.LastDisconnectText);
        Assert.Contains(expected == DisconnectReason.ModVersionMismatch ? "0.2.0" : "5.0.0f4/2", client.LastDisconnectText);
        Assert.Equal("0=Host", net.Host.PlayerNames);
        Assert.Contains(net.Host.Log, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Rejected 'Julian'"));
    }

    [Fact]
    public void A_full_session_turns_new_players_away()
    {
        var net = new SessionNet();
        for (int i = 0; i < 3; i++)
        {
            net.AddClient("P" + i);
        }

        net.Run(1);
        SessionNet.Node extra = net.AddClient("Extra");
        net.Run(1);

        Assert.Equal(4, net.Host.Session.Players.Count);
        Assert.Equal(new[] { "disconnected:ServerFull" }, extra.Events);
    }

    [Fact]
    public void Connecting_to_nobody_fails_after_the_timeout()
    {
        var network = new LoopbackNetwork();
        var session = new NetSession(network.CreateEndpoint(1), new SessionConfig { ConnectTimeout = 3 }, new Logger());
        DisconnectReason? reason = null;
        session.Disconnected += (r, _) => reason = r;
        session.Connect(999, "Lonely");

        for (double now = 0; now < 2.9; now += 0.05)
        {
            session.Update(now);
        }

        Assert.Equal(SessionState.Connecting, session.State);

        for (double now = 2.9; now < 3.5; now += 0.05)
        {
            session.Update(now);
        }

        Assert.Equal(DisconnectReason.ConnectFailed, reason);
        Assert.Equal(SessionRole.None, session.Role);
    }

    [Fact]
    public void When_a_client_leaves_everyone_else_is_told()
    {
        var net = new SessionNet();
        SessionNet.Node leaver = net.AddClient("Leaver");
        SessionNet.Node stayer = net.AddClient("Stayer");
        net.Run(1);

        leaver.Session.Close();
        net.Run(1);

        Assert.Equal("0=Host,2=Stayer", net.Host.PlayerNames);
        Assert.Equal("0=Host,2=Stayer", stayer.PlayerNames);
        Assert.Contains("left:1:ClosedByUser", net.Host.Events);
        Assert.Contains("left:1:None", stayer.Events);
        Assert.Equal(SessionRole.None, leaver.Session.Role);
    }

    [Fact]
    public void When_the_host_closes_the_clients_are_disconnected()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);

        net.Host.Session.Close();
        net.Run(0.5);

        Assert.Equal("disconnected:HostClosed", client.Events.Last());
        Assert.Equal(SessionRole.None, client.Session.Role);
        Assert.Empty(client.Session.Players);
    }

    [Fact]
    public void A_client_that_vanishes_is_dropped_after_the_timeout()
    {
        var net = new SessionNet();
        SessionNet.Node ghost = net.AddClient("Ghost");
        SessionNet.Node other = net.AddClient("Other");
        net.Run(1);

        net.Vanish(ghost);
        net.Run(9);
        Assert.Equal(3, net.Host.Session.Players.Count);

        net.Run(2);
        Assert.Equal("0=Host,2=Other", net.Host.PlayerNames);
        Assert.Contains("left:1:Timeout", net.Host.Events);
        Assert.Equal("0=Host,2=Other", other.PlayerNames);
    }

    [Fact]
    public void A_host_that_vanishes_times_the_client_out()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);

        net.Vanish(net.Host);
        net.Run(11);

        Assert.Equal("disconnected:Timeout", client.Events.Last());
    }

    [Fact]
    public void A_client_can_come_back_after_leaving()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);
        client.Session.Close();
        net.Run(1);

        client.Session.Connect(SessionNet.HostPeer, "Julian again");
        net.Run(1);

        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal("0=Host,1=Julian again", net.Host.PlayerNames);
    }

    [Fact]
    public void A_client_that_restarts_without_saying_goodbye_replaces_its_old_self()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Before");
        net.Run(1);

        // Same peer id, new session object: the process crashed and came back.
        net.Kill(client);
        var config = new SessionConfig { ModVersion = "0.1.0", GameVersion = "5.0.0f4/1" };
        var reborn = new NetSession(client.Transport, config, new Logger());
        reborn.Connect(SessionNet.HostPeer, "After");
        for (int i = 0; i < 60; i++)
        {
            net.Run(1.0 / 60.0);
            reborn.Update(net.Now);
        }

        Assert.Equal(SessionState.Connected, reborn.State);
        Assert.Equal("After", Assert.Single(net.Host.Session.Players, p => !p.IsLocal).Name);
    }

    [Fact]
    public void Game_messages_reach_the_right_players()
    {
        var net = new SessionNet();
        SessionNet.Node first = net.AddClient("A");
        SessionNet.Node second = net.AddClient("B");
        net.Run(1);

        first.Session.Send(Protocol.HostPlayerId, Hello, 0, Hello.Length, reliable: true);
        net.Host.Session.Send(Protocol.Broadcast, [Protocol.FirstUserMessage + 1], 0, 1, reliable: true);
        net.Host.Session.Send(2, [Protocol.FirstUserMessage + 2], 0, 1, reliable: false);
        net.Run(0.5);

        var fromFirst = Assert.Single(net.Host.Messages);
        Assert.Equal((1, true), (fromFirst.From, fromFirst.Reliable));
        Assert.Equal(Hello, fromFirst.Data);
        Assert.Equal(new[] { Protocol.FirstUserMessage + 1 }, first.Messages.Select(m => (int)m.Data[0]));
        Assert.Equal(new[] { Protocol.FirstUserMessage + 1, Protocol.FirstUserMessage + 2 }, second.Messages.Select(m => (int)m.Data[0]));
        Assert.All(second.Messages, m => Assert.Equal(0, m.From));
        Assert.False(second.Messages[1].Reliable);
    }

    [Fact]
    public void Game_messages_cannot_use_the_reserved_type_range()
    {
        var net = new SessionNet();

        Assert.Throws<ArgumentException>(() => net.Host.Session.Send(Protocol.Broadcast, [Protocol.MessagePlayerList], 0, 1, true));
        Assert.Throws<ArgumentException>(() => net.Host.Session.Send(Protocol.Broadcast, [], 0, 0, true));
    }

    [Fact]
    public void The_client_learns_the_hosts_clock_despite_its_own_being_different()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 100, JitterMs = 20 });
        SessionNet.Node client = net.AddClient("Julian", clockSkew: 5000.5);

        // At 100 ms each way not even the handshake has completed yet.
        net.Run(0.15);
        Assert.False(client.Session.IsClockSynced);

        net.Run(10);

        Assert.True(client.Session.IsClockSynced);
        Assert.True(net.Host.Session.IsClockSynced);
        Assert.InRange(client.Session.SessionTime - net.Host.Session.SessionTime, -0.025, 0.025);
        Assert.InRange(client.Session.Tick - net.Host.Session.Tick, -1, 1);
        Assert.InRange(client.Session.ClockOffset, -5000.53, -5000.47);
    }

    [Fact]
    public void Survives_200_ms_of_latency_and_5_percent_loss()
    {
        // The phase 2 criterion: 200 ms each way with jitter and 5% loss, for a minute, with traffic flowing.
        var net = new SessionNet(new NetworkConditions { LatencyMs = 200, JitterMs = 30, LossPercent = 5 });
        SessionNet.Node client = net.AddClient("Julian");
        net.Host.Session.TestTraffic = true;
        client.Session.TestTraffic = true;

        net.Run(60);
        net.Host.Session.TestTraffic = false;
        client.Session.TestTraffic = false;
        net.Run(10);

        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal("0=Host,1=Julian", client.PlayerNames);
        foreach (SessionNet.Node node in new[] { net.Host, client })
        {
            TrafficTestStats traffic = node.Session.Traffic;
            Assert.True(traffic.ReliableSent > 1500, "traffic must have flowed");
            Assert.Equal(0, traffic.ReliableErrors);
            Assert.InRange(traffic.UnreliableReceived, traffic.UnreliableSent * 0.85, traffic.UnreliableSent - 1);
        }

        Assert.Equal(net.Host.Session.Traffic.ReliableSent, client.Session.Traffic.ReliableReceived);
        Assert.Equal(client.Session.Traffic.ReliableSent, net.Host.Session.Traffic.ReliableReceived);

        PeerStats link = Assert.Single(client.Session.GetPeerStats());
        Assert.InRange(link.RoundTripMs, 380, 470);
        Assert.Equal(0, link.ReliableQueue);
        Assert.True(link.ReliableResends > 0);
        Assert.DoesNotContain(net.Host.Log.Concat(client.Log), entry => entry.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_handshake_gets_through_a_terrible_link(int seed)
    {
        var net = new SessionNet(new NetworkConditions { LossPercent = 60, LatencyMs = 150, JitterMs = 100 }, seed);
        SessionNet.Node client = net.AddClient("Julian");

        net.Run(9);

        Assert.Equal(SessionState.Connected, client.Session.State);
        net.Run(20);
        Assert.Equal("0=Host,1=Julian", client.PlayerNames);
    }

    [Fact]
    public void Link_statistics_are_reported_per_peer()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50 });
        SessionNet.Node client = net.AddClient("Julian");
        client.Session.TestTraffic = true;

        net.Run(5);

        PeerStats fromHost = Assert.Single(net.Host.Session.GetPeerStats());
        Assert.Equal((1, "Julian"), (fromHost.PlayerId, fromHost.Name));
        Assert.True(fromHost.HasRoundTripTime);
        Assert.InRange(fromHost.RoundTripMs, 90, 140);
        Assert.InRange(fromHost.PacketsInPerSecond, 25, 35);
        Assert.True(fromHost.BytesInPerSecond > fromHost.PacketsInPerSecond * 13);
        Assert.InRange(fromHost.LossPercent, 0, 0.1);

        PeerStats fromClient = Assert.Single(client.Session.GetPeerStats());
        Assert.Equal((0, "Host"), (fromClient.PlayerId, fromClient.Name));
    }

    [Fact]
    public void Garbage_from_the_network_is_counted_and_ignored()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);
        ITransport stranger = net.AddRawEndpoint(777);
        var random = new Random(5);

        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[random.Next(0, 80)];
            random.NextBytes(junk);
            if (junk.Length > 0 && i % 2 == 0)
            {
                junk[0] = (byte)(1 + (i % 5));
            }

            stranger.Send(SessionNet.HostPeer, junk, junk.Length);
            stranger.Send(client.Peer, junk, junk.Length);
        }

        net.Run(1);

        Assert.True(net.Host.Session.MalformedDatagrams > 0);
        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);
        Assert.Equal("0=Host,1=Julian", client.PlayerNames);
    }

    [Fact]
    public void A_failing_event_handler_does_not_break_the_session()
    {
        var net = new SessionNet();
        net.Host.Session.PlayerJoined += _ => throw new InvalidOperationException("handler bug");
        net.Host.Session.MessageReceived += (_, _, _) => throw new InvalidOperationException("handler bug");
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);

        client.Session.Send(0, Hello, 0, Hello.Length, true);
        net.Run(0.5);

        Assert.Equal("0=Host,1=Julian", client.PlayerNames);
        Assert.Single(net.Host.Messages);
        Assert.Equal(2, net.Host.Log.Count(entry => entry.Level == LogLevel.Error && entry.Message.Contains("handler bug")));
    }

    [Fact]
    public void A_session_cannot_be_started_twice()
    {
        var net = new SessionNet();

        Assert.Throws<InvalidOperationException>(() => net.Host.Session.StartHost("Again"));
        Assert.Throws<InvalidOperationException>(() => net.Host.Session.Connect(1, "x"));
    }
}

public class ClockSyncTests
{
    [Fact]
    public void A_symmetric_trip_gives_the_exact_offset()
    {
        var clock = new ClockSync();

        // Local 10.0 -> remote reads 110.05 halfway -> back at local 10.1: the remote is 100 s ahead.
        clock.AddSample(10.0, 110.05, 10.1);

        Assert.True(clock.HasEstimate);
        Assert.Equal(100.0, clock.Offset, 9);
        Assert.Equal(0.1, clock.BestRoundTrip, 9);
        Assert.Equal(120.0, clock.ToRemote(20.0), 9);
    }

    [Fact]
    public void The_fastest_sample_wins_because_it_is_the_most_symmetric()
    {
        var clock = new ClockSync();

        // Slow and lopsided: 400 ms out, 0 ms back. Halving it is 200 ms wrong.
        clock.AddSample(0.0, 100.4, 0.4);
        Assert.Equal(100.2, clock.Offset, 9);

        // Fast one: almost no room to be wrong.
        clock.AddSample(1.0, 101.01, 1.02);
        Assert.Equal(100.0, clock.Offset, 9);

        // A later slow sample does not displace it.
        clock.AddSample(2.0, 102.5, 2.5);
        Assert.Equal(100.0, clock.Offset, 9);
    }

    [Fact]
    public void Old_samples_age_out_of_the_window()
    {
        var clock = new ClockSync(window: 2);
        clock.AddSample(0.0, 100.01, 0.02);

        clock.AddSample(1.0, 201.1, 1.2);
        clock.AddSample(2.0, 202.1, 2.2);

        Assert.Equal(200.0, clock.Offset, 9);
        Assert.Equal(3, clock.Samples);
    }

    [Fact]
    public void Impossible_samples_are_ignored()
    {
        var clock = new ClockSync();

        clock.AddSample(5.0, 100.0, 4.0);
        clock.AddSample(double.NaN, 100.0, 4.0);

        Assert.False(clock.HasEstimate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClockSync(0));
    }

    [Fact]
    public void Rate_sampler_reports_per_second_values()
    {
        var sampler = new RateSampler();

        sampler.Update(10.0, 1000);
        sampler.Update(10.5, 1300);
        Assert.Equal(0, sampler.PerSecond);

        sampler.Update(11.0, 1600);
        Assert.Equal(600, sampler.PerSecond, 6);

        sampler.Update(13.0, 1700);
        Assert.Equal(50, sampler.PerSecond, 6);
    }
}
