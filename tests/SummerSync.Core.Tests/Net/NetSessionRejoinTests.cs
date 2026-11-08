using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

/// <summary>
/// A transport can hand a new session what was sent to an earlier one. Steam does: its queue of received
/// packets belongs to the game, not to our transport object, so whatever was not read before a session ended
/// is still there when the next one starts, and it comes from the same Steam ID. Found on 2026-10-12, when a
/// host closed a session and could not get the same friend back in until both restarted the game.
/// </summary>
public class NetSessionRejoinTests
{
    [Fact]
    public void After_the_host_closes_the_same_players_can_start_over()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);

        net.Host.Session.Close();
        net.Run(0.5);
        Assert.Equal(new[] { "connected", "joined:0:Host", "disconnected:HostClosed" }, client.Events);

        // A new "Host on Steam" and a new invite, a few seconds later, on the very same endpoints.
        client.Events.Clear();
        net.Host.Session.StartHost("Host");
        net.Run(3);
        client.Session.Connect(SessionNet.HostPeer, "Julian");
        net.Run(1);

        Assert.Equal(new[] { "connected", "joined:0:Host" }, client.Events);
        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);
    }

    [Fact]
    public void After_the_host_closes_twice_in_a_row_it_still_works()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        for (int round = 0; round < 3; round++)
        {
            net.Run(1);
            Assert.Equal(SessionState.Connected, client.Session.State);
            net.Host.Session.Close();
            net.Run(0.5);
            Assert.Equal(SessionRole.None, client.Session.Role);
            net.Host.Session.StartHost("Host");
            client.Session.Connect(SessionNet.HostPeer, "Julian");
        }

        net.Run(1);

        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);
    }

    // The client leaves and comes straight back while its old packets and goodbyes are still on their way.
    // Latency and jitter decide in which order the host sees the old and the new connection.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void A_client_that_leaves_and_comes_straight_back_is_not_kicked_by_its_own_old_goodbye(int seed)
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 120, JitterMs = 100 }, seed);
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(2);
        Assert.Equal(SessionState.Connected, client.Session.State);

        client.Session.Close();
        client.Events.Clear();
        net.Host.Events.Clear();
        client.Session.Connect(SessionNet.HostPeer, "Julian");
        net.Run(5);

        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);
        Assert.DoesNotContain(client.Events, e => e.StartsWith("disconnected"));
        Assert.Equal(0, net.Host.Session.Traffic.ReliableErrors + client.Session.Traffic.ReliableErrors);

        // The host saw one player leave and one arrive: nobody was removed twice.
        Assert.Single(net.Host.Events, e => e.StartsWith("left:"));
        Assert.Single(net.Host.Events, e => e.StartsWith("joined:"));
    }

    // Packets of the old connection must not be read as packets of the new one: their sequence numbers and
    // acknowledgements mean something else.
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Packets_of_an_earlier_connection_do_not_corrupt_the_new_one(int seed)
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 150, JitterMs = 140, LossPercent = 5 }, seed);
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(4);

        // Twice over, with traffic in flight in both directions each time.
        for (int round = 0; round < 2; round++)
        {
            client.Session.Close();
            client.Session.Connect(SessionNet.HostPeer, "Julian");
            net.Run(4);
        }

        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);
        Assert.Equal(0, net.Host.Session.Traffic.ReliableErrors + client.Session.Traffic.ReliableErrors);
        Assert.True(net.Host.Session.StaleDatagrams + client.Session.StaleDatagrams > 0, "nothing stale arrived: the test does not exercise what it claims");
        Assert.Equal(0, net.Host.Session.MalformedDatagrams + client.Session.MalformedDatagrams);
    }

    [Fact]
    public void A_client_talking_to_a_host_that_started_over_is_told_so()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);

        // The host comes back as a new session that does not know the client, and its goodbye never arrived.
        net.Conditions.LossPercent = 100;
        net.Host.Session.Close();
        net.Conditions.LossPercent = 0;
        net.Host.Session.StartHost("Host");
        net.Run(1);

        Assert.Contains("disconnected:HostClosed", client.Events);
        Assert.Equal("0=Host", net.Host.PlayerNames);
    }
}
