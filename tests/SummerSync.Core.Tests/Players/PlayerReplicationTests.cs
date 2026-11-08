using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Players;

public class PlayerReplicationTests
{
    /// <summary>A player on a session node: where they are as a function of their own clock, and what they see.</summary>
    private sealed class Player
    {
        private double lastSample = double.NaN;

        public Player(SessionNet.Node node, Func<double, PlayerPose>? path)
        {
            Node = node;
            Path = path;
            Replication = new PlayerReplication(node.Session, node.Logger);
            node.BeforeUpdate = time =>
            {
                Time = time;
                Replication.SetLocal(Path != null, Path != null ? Path(time) : default);
                Replication.Update(time);
                Replication.Sample(time);
                lastSample = time;
            };
        }

        public SessionNet.Node Node { get; }

        public PlayerReplication Replication { get; }

        /// <summary>Null while the player is in the main menu.</summary>
        public Func<double, PlayerPose>? Path { get; set; }

        public double Time { get; private set; }

        public RemotePlayer See(byte id) => Replication.Find(id) ?? throw new InvalidOperationException("Player " + id + " is not known here.");

        public IEnumerable<string> PlayerLog => Node.Log.Where(e => e.Category == LogCategory.Player).Select(e => e.Message);
    }

    // Each walks a straight line of their own, so that who is who can be told from the position.
    private static Func<double, PlayerPose> Walking(float lane) => t => new PlayerPose((float)(1.5 * t), 0, lane, 90, 0, 1.4f);

    [Fact]
    public void Two_players_see_each_other_move()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian", clockSkew: 300), Walking(10));

        net.Run(3);

        RemotePlayer hostSeen = client.See(0);
        RemotePlayer clientSeen = host.See(1);
        Assert.True(hostSeen.Visible && clientSeen.Visible);
        Assert.Equal("Host", hostSeen.Name);
        Assert.Equal("Julian", clientSeen.Name);
        Assert.Equal(0f, hostSeen.Shown.Z);
        Assert.Equal(10f, clientSeen.Shown.Z);

        // Each is shown where they were a little over a tenth of a second ago.
        Assert.InRange((1.5 * host.Time) - hostSeen.Shown.X, 0.15, 0.3);
        Assert.InRange((1.5 * client.Time) - clientSeen.Shown.X, 0.15, 0.3);
        Assert.Equal(0, hostSeen.Buffer.Snaps + clientSeen.Buffer.Snaps);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);
    }

    [Fact]
    public void A_state_goes_out_with_every_flush_and_costs_little()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian", configure: c => { }), Walking(10));
        client.Node.Session.TestTraffic = false;
        host.Node.Session.TestTraffic = false;
        net.Run(1);
        long sentBefore = client.Replication.StatesSent;
        long receivedBefore = host.See(1).StatesReceived;

        net.Run(10);

        Assert.InRange(client.Replication.StatesSent - sentBefore, 295, 305);
        Assert.InRange(host.See(1).StatesReceived - receivedBefore, 295, 305);
        PeerStats link = host.Node.Session.GetPeerStats().Single();
        Assert.InRange(link.BytesInPerSecond, 600, 1400);
    }

    [Fact]
    public void A_third_player_is_seen_through_the_host()
    {
        var conditions = new NetworkConditions { LatencyMs = 60, JitterMs = 15, LossPercent = 3 };
        var net = new SessionNet(conditions);
        var host = new Player(net.Host, Walking(0));
        var first = new Player(net.AddClient("A", clockSkew: 50), Walking(10));
        var second = new Player(net.AddClient("B", clockSkew: -700), Walking(20));

        net.Run(6);

        // Everyone sees the other two, each in their own lane.
        Assert.Equal(new byte[] { 1, 2 }, host.Replication.Remotes.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(new byte[] { 0, 2 }, first.Replication.Remotes.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(new byte[] { 0, 1 }, second.Replication.Remotes.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(20f, first.See(2).Shown.Z);
        Assert.Equal(10f, second.See(1).Shown.Z);
        Assert.Equal("B", first.See(2).Name);
        Assert.True(first.See(2).Visible && second.See(1).Visible);

        // The relayed player is two links away: about twice the latency behind, plus the buffer.
        double behind = ((1.5 * second.Time) - first.See(2).Shown.X) / 1.5;
        Assert.InRange(behind, 0.2, 0.45);
        Assert.Equal(0, first.See(2).Buffer.Snaps);
        Assert.True(first.See(2).Buffer.StarvedSeconds < 0.05 * first.See(2).Buffer.PlayedSeconds);
    }

    [Fact]
    public void A_player_in_the_menu_is_known_but_not_shown()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), null);

        net.Run(2);

        RemotePlayer seen = host.See(1);
        Assert.True(seen.HasState);
        Assert.False(seen.InWorld);
        Assert.False(seen.Visible);
        Assert.True(client.See(0).Visible);

        // Twice a second is enough to say "still in the menu".
        Assert.InRange(seen.StatesReceived, 3, 6);
        Assert.Contains("Player 1 'Julian' is not in the game world (main menu or loading).", host.PlayerLog);
    }

    [Fact]
    public void A_player_who_enters_and_leaves_the_world_appears_and_disappears_at_once()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), null);
        net.Run(2);

        client.Path = Walking(10);
        net.Run(0.3);
        bool appeared = host.See(1).Visible;

        client.Path = null;
        net.Run(0.1);

        Assert.True(appeared);
        Assert.False(host.See(1).Visible);
        Assert.False(host.See(1).InWorld);

        client.Path = Walking(30);
        net.Run(0.5);
        Assert.True(host.See(1).Visible);
        Assert.Equal(30f, host.See(1).Shown.Z);
    }

    // The decision behind "connect in the menu, then load": a loading screen freezes the game for longer than
    // the normal timeout, and must not cost the session.
    [Fact]
    public void A_client_that_freezes_on_a_loading_screen_is_waited_for()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), null);
        net.Run(2);

        net.Kill(client.Node);
        net.Run(75);
        Assert.Equal("0=Host,1=Julian", host.Node.PlayerNames);

        client.Node.Dead = false;
        client.Path = Walking(10);
        net.Run(2);

        Assert.Equal(SessionState.Connected, client.Node.Session.State);
        Assert.Equal("0=Host,1=Julian", host.Node.PlayerNames);
        Assert.True(host.See(1).Visible);
        Assert.True(client.See(0).Visible);
        Assert.DoesNotContain(client.Node.Events, e => e.StartsWith("disconnected"));
        Assert.Contains(client.Node.Log, e => e.Message.StartsWith("This side did not run for 75."));
        Assert.Equal(0, host.Node.Session.Traffic.ReliableErrors + client.Node.Session.Traffic.ReliableErrors);

        // After the freeze the host is shown where it is now, not where the backlog of old states says.
        Assert.InRange((1.5 * host.Time) - client.See(0).Shown.X, 0.15, 0.4);
    }

    [Fact]
    public void A_host_that_freezes_on_a_loading_screen_is_waited_for()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, null);
        var client = new Player(net.AddClient("Julian"), Walking(10));
        net.Run(2);

        net.Kill(host.Node);
        net.Run(75);
        Assert.Equal(SessionState.Connected, client.Node.Session.State);

        host.Node.Dead = false;
        host.Path = Walking(0);
        net.Run(2);

        Assert.Equal(SessionState.Connected, client.Node.Session.State);
        Assert.Equal("0=Host,1=Julian", host.Node.PlayerNames);
        Assert.True(client.See(0).Visible);
        Assert.True(host.See(1).Visible);
    }

    [Fact]
    public void A_player_in_the_world_who_goes_silent_is_dropped_after_the_normal_timeout()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), Walking(10));
        net.Run(2);

        net.Kill(client.Node);
        net.Run(9);
        Assert.Equal("0=Host,1=Julian", host.Node.PlayerNames);
        net.Run(2);

        Assert.Equal("0=Host", host.Node.PlayerNames);
        Assert.Contains("left:1:Timeout", host.Node.Events);
        Assert.Empty(host.Replication.Remotes);
    }

    [Fact]
    public void Nobody_is_waited_for_longer_than_three_minutes()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), null);
        net.Run(2);

        net.Vanish(client.Node);
        net.Run(PlayerReplication.MenuPatienceSeconds - 5);
        Assert.Equal("0=Host,1=Julian", host.Node.PlayerNames);
        net.Run(10);

        Assert.Equal("0=Host", host.Node.PlayerNames);
    }

    [Fact]
    public void A_player_who_returns_to_the_world_is_on_the_normal_timeout_again()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), null);
        net.Run(2);
        client.Path = Walking(10);
        net.Run(1);

        net.Kill(client.Node);
        net.Run(11);

        Assert.Equal("0=Host", host.Node.PlayerNames);
    }

    [Fact]
    public void When_a_player_leaves_how_they_came_through_is_logged()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 100, JitterMs = 20, LossPercent = 5 });
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), Walking(10));
        net.Run(10);

        client.Node.Session.Close();
        net.Run(1);

        string summary = host.PlayerLog.Single(m => m.StartsWith("Player 1 'Julian' as seen from here: "));
        Assert.Matches(@"^Player 1 'Julian' as seen from here: \d+ state\(s\), shown for \d+\.\d s, delay \d+ ms, no fresh data \d+\.\d %, \d+ jump\(s\) smoothed \(largest \d\.\d\d m\), 0 teleport\(s\), \d+ state\(s\) lost, their game paused 0 time\(s\) \(longest 0\.00 s\), this game paused 0 time\(s\) \(longest 0\.00 s\), \d+ late, \d+ duplicate\(s\), 0 clock restart\(s\)\.$", summary);
        Assert.Empty(host.Replication.Remotes);
    }

    [Fact]
    public void A_client_cannot_speak_for_another_player_and_rubbish_is_counted_not_fatal()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), Walking(10));
        net.Run(1);

        // A list of states is something only the host may send; and a state with a NaN position is refused.
        byte[] list = { PlayerWire.MessageStates, 1, 0, 1, 0, 0, 0, 0, 0, 0 };
        client.Node.Session.Send(Protocol.HostPlayerId, list, 0, list.Length, true);
        var nan = new List<byte> { PlayerWire.MessageState, 1, 0, 0, 0, 9, 0, PlayerWire.FlagInWorld };
        nan.AddRange(BitConverter.GetBytes(float.NaN));
        nan.AddRange(new byte[13]);
        client.Node.Session.Send(Protocol.HostPlayerId, nan.ToArray(), 0, nan.Count, true);
        byte[] cut = { PlayerWire.MessageState, 1, 2 };
        client.Node.Session.Send(Protocol.HostPlayerId, cut, 0, cut.Length, true);
        net.Run(1);

        Assert.Equal(3, host.Replication.Malformed);
        Assert.True(host.See(1).Visible);
        Assert.Equal(10f, host.See(1).Shown.Z);
        Assert.Single(host.Node.Log, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Dropped a player message from player 1"));
        Assert.DoesNotContain(host.Node.Log, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Other_game_messages_are_left_alone()
    {
        var net = new SessionNet();
        var host = new Player(net.Host, Walking(0));
        var client = new Player(net.AddClient("Julian"), Walking(10));
        net.Run(1);

        byte[] other = { (byte)(PlayerWire.MessageViewReport + 1), 9, 9 };
        client.Node.Session.Send(Protocol.HostPlayerId, other, 0, other.Length, true);
        net.Run(1);

        Assert.Equal(0, host.Replication.Malformed);
        Assert.Contains(host.Node.Messages, m => m.Data[0] == PlayerWire.MessageViewReport + 1);
    }

    [Fact]
    public void The_loopback_test_shows_the_local_player_echoed_back()
    {
        var manager = new NetworkManager(new Logger(), seed: 3) { ModVersion = "0.1.0", GameVersion = "g" };
        manager.Conditions.LatencyMs = 200;
        manager.Conditions.JitterMs = 30;
        manager.Conditions.LossPercent = 5;
        manager.StartLoopbackTest("Julian");
        Func<double, PlayerPose> path = t => BotPath.Evaluate(t, new PlayerPose(0, 0, 0, 0, 0, 1.4f));

        double now = 0;
        double largestStep = 0;
        PlayerPose? previous = null;
        while (now < 40)
        {
            now += 1.0 / 60.0;
            manager.SetLocalPlayer(true, path(now));
            manager.Update(now);
            manager.Players!.Sample(now);
            RemotePlayer echo = manager.Players.Remotes.SingleOrDefault()!;
            if (echo != null && echo.Visible)
            {
                if (previous != null)
                {
                    largestStep = Math.Max(largestStep, PlayerPose.Distance(echo.Shown, previous.Value));
                }

                previous = echo.Shown;
            }
        }

        RemotePlayer seen = manager.Players!.Remotes.Single();
        Assert.True(seen.Visible);
        Assert.Equal(0, seen.Buffer.Snaps);
        Assert.InRange(largestStep, 0.05, (BotPath.SprintSpeed / 60 * 1.06) + 0.06);
        Assert.True(seen.Buffer.StarvedSeconds < 0.02 * seen.Buffer.PlayedSeconds);
        Assert.True(manager.LocalPlayers!.LocalInWorld);
        Assert.False(manager.Players.LocalInWorld);

        var status = new StringBuilder();
        NetworkStatus.Describe(manager, status);
        Assert.Matches(@"\n You: at \(-?\d+\.\d, 0\.0, -?\d+\.\d\), looking \d+/-?\d+, eyes \d\.\d\d m\n Echo of you: \d+\.\d m away, delay \d+ ms, no fresh data \d+\.\d %, \d+ jump\(s\) smoothed \(largest \d\.\d\d m\), 0 teleport\(s\)$", status.ToString());

        manager.Stop();
        Assert.Null(manager.Players);
    }

    [Fact]
    public void The_status_says_who_is_not_in_the_world()
    {
        var host = new NetworkManager(new Logger(), seed: 1) { ModVersion = "0.1.0", GameVersion = "g" };
        var client = new NetworkManager(new Logger(), seed: 2) { ModVersion = "0.1.0", GameVersion = "g" };
        var network = new LoopbackNetwork();
        host.StartHost(network.CreateEndpoint(1), "test", "Host");
        client.StartClient(network.CreateEndpoint(2), "test", 1, "Julian");

        for (double now = 0; now < 2; now += 1.0 / 60.0)
        {
            host.SetLocalPlayer(true, new PlayerPose(1, 2, 3, 45, -10, 1.4f));
            client.SetLocalPlayer(false, default);
            host.Update(now);
            client.Update(now);
            host.Players!.Sample(now);
            client.Players!.Sample(now);
        }

        var hostStatus = new StringBuilder();
        var clientStatus = new StringBuilder();
        NetworkStatus.Describe(host, hostStatus);
        NetworkStatus.Describe(client, clientStatus);
        Assert.Contains("\n You: at (1.0, 2.0, 3.0), looking 45/-10, eyes 1.40 m\n #1 Julian: not in the game world", hostStatus.ToString());
        Assert.Contains("\n You: not in the game world\n #0 Host: delay 100 ms, no fresh data 0.0 %, 0 jump(s) smoothed (largest 0.00 m), 0 teleport(s)", clientStatus.ToString());
    }

    // The other player's own log is hard to get hold of, and has arrived empty. What their screen showed
    // reaches the host's log this way instead.
    [Fact]
    public void A_client_reports_how_it_sees_the_others_and_the_host_logs_it_when_the_client_leaves()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 100, JitterMs = 20, LossPercent = 5 });
        var host = new Player(net.Host, Walking(0));
        var first = new Player(net.AddClient("Julian"), Walking(10));
        var second = new Player(net.AddClient("Other"), Walking(20));
        net.Run(12);

        // A report every five seconds: the last one was sent at ten.
        ViewQuality ofHost = host.See(1).Views[0];
        Assert.InRange(ofHost.ShownSeconds, 8, 11);
        Assert.InRange(ofHost.DelayMs, 95, 220);
        Assert.Equal(0, ofHost.Snaps);
        Assert.InRange(ofHost.Lost, 5, 40);
        Assert.Equal(0, ofHost.SenderPauses);
        Assert.Equal(new byte[] { 0, 2 }, host.See(1).Views.Keys.OrderBy(id => id));
        Assert.Equal(new byte[] { 0, 1 }, host.See(2).Views.Keys.OrderBy(id => id));
        Assert.Empty(first.See(0).Views);

        first.Node.Session.Close();
        net.Run(1);

        string[] seen = host.PlayerLog.Where(m => m.StartsWith("Player 1 'Julian' saw player ")).ToArray();
        Assert.Equal(2, seen.Length);
        Assert.Matches(
            @"^Player 1 'Julian' saw player 0 like this: shown for \d+ s, delay \d+ ms, no fresh data \d+\.\d %, \d+ jump\(s\) smoothed \(largest \d\.\d\d m\), 0 teleport\(s\), \d+ state\(s\) lost, their game paused 0 time\(s\) \(longest 0\.00 s\), this game paused 0 time\(s\) \(longest 0\.00 s\)\.$",
            seen[0]);
        Assert.Equal(0, host.Replication.Malformed + first.Replication.Malformed + second.Replication.Malformed);
    }

    [Fact]
    public void The_numbers_of_a_report_survive_the_wire_and_do_not_overflow()
    {
        var buffer = new PoseBuffer();
        var writer = new SummerSync.Core.Net.Wire.PacketWriter(64);

        ViewQuality.Of(buffer).Write(writer);
        var reader = new SummerSync.Core.Net.Wire.PacketReader(writer.Buffer, 0, writer.Length);
        ViewQuality empty = ViewQuality.Read(reader);

        Assert.Equal(ViewQuality.Size, writer.Length);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal("delay 0 ms, no fresh data 0.0 %, 0 jump(s) smoothed (largest 0.00 m), 0 teleport(s)", empty.DescribeSmoothness());

        var large = new ViewQuality { ShownSeconds = ushort.MaxValue, StarvedPermille = 51, LargestCorrectionCm = 110, DelayMs = 160, Corrections = 3, Lost = 85, SenderPauses = 31, LongestSenderPauseCs = 42 };
        Assert.Equal("delay 160 ms, no fresh data 5.1 %, 3 jump(s) smoothed (largest 1.10 m), 0 teleport(s)", large.DescribeSmoothness());
        Assert.Equal("85 state(s) lost, their game paused 31 time(s) (longest 0.42 s), this game paused 0 time(s) (longest 0.00 s)", large.DescribeDelivery());
    }
}
