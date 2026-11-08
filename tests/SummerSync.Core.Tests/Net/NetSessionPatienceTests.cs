using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

/// <summary>What the session offers to state that is sent continuously, and to players who freeze for a while.</summary>
public class NetSessionPatienceTests
{
    [Fact]
    public void Before_flush_is_raised_as_often_as_the_session_sends()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        int hostFlushes = 0;
        int clientFlushes = 0;
        net.Host.Session.BeforeFlush += () => hostFlushes++;
        client.Session.BeforeFlush += () => clientFlushes++;

        net.Run(10);

        Assert.InRange(hostFlushes, 298, 302);
        Assert.InRange(clientFlushes, 298, 302);
    }

    [Fact]
    public void A_message_sent_from_before_flush_leaves_in_that_flush()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);
        int sent = 0;
        client.Session.BeforeFlush += () =>
        {
            byte[] state = { Protocol.FirstUserMessage, (byte)sent++ };
            client.Session.Send(Protocol.HostPlayerId, state, 0, state.Length, false);
        };

        net.Run(2);

        // One per flush, none held back for the next one: the newest sent is the newest received, give or
        // take the one in flight.
        int received = net.Host.Messages.Count;
        Assert.InRange(received, sent - 1, sent);
        Assert.InRange(sent, 59, 61);
    }

    [Fact]
    public void A_handler_that_closes_the_session_from_before_flush_does_no_harm()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);
        client.Session.BeforeFlush += () => client.Session.Close();

        net.Run(1);

        Assert.Equal(SessionRole.None, client.Session.Role);
        Assert.Equal("0=Host", net.Host.PlayerNames);
    }

    [Fact]
    public void A_player_granted_patience_may_stay_silent_that_long_and_no_longer()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);

        net.Host.Session.SetPeerPatience(1, 60);
        net.Vanish(client);
        net.Run(55);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);
        net.Run(10);

        Assert.Equal("0=Host", net.Host.PlayerNames);
        Assert.Contains("left:1:Timeout", net.Host.Events);
    }

    [Fact]
    public void Patience_shorter_than_the_normal_timeout_changes_nothing_and_it_can_be_taken_back()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(1);

        net.Host.Session.SetPeerPatience(1, 3);
        net.Vanish(client);
        net.Run(8);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);

        net.Host.Session.SetPeerPatience(1, 600);
        net.Host.Session.SetPeerPatience(1, 0);
        net.Run(4);
        Assert.Equal("0=Host", net.Host.PlayerNames);
    }

    [Fact]
    public void A_client_grants_patience_to_the_host_only()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.AddClient("Other");
        net.Run(1);

        client.Session.SetPeerPatience(2, 600);
        client.Session.SetPeerPatience(Protocol.HostPlayerId, 30);
        net.Kill(net.Host);
        net.Run(25);
        Assert.Equal(SessionState.Connected, client.Session.State);
        net.Run(10);

        Assert.Contains("disconnected:Timeout", client.Events);
        Assert.Equal("Nothing received from the host for 30 seconds.", client.LastDisconnectText);
    }

    [Fact]
    public void Numbered_test_messages_do_not_pile_up_for_a_player_who_went_quiet()
    {
        var net = new SessionNet();
        net.Host.Session.TestTraffic = true;
        SessionNet.Node client = net.AddClient("Julian");
        client.Session.TestTraffic = true;
        net.Run(2);
        net.Host.Session.SetPeerPatience(1, 300);

        net.Kill(client);
        net.Run(120);
        int queued = net.Host.Session.GetPeerStats().Single().ReliableQueue;
        client.Dead = false;
        net.Run(3);

        // Two seconds' worth at most, not two minutes' worth; and the count picks up where it left off.
        Assert.InRange(queued, 30, 70);
        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal(0, net.Host.Session.Traffic.ReliableErrors + client.Session.Traffic.ReliableErrors);
        Assert.True(net.Host.Session.GetPeerStats().Single().ReliableQueue < 5);
    }

    // A debugger pause, a laptop lid, two games loading at once: nobody ran, so nobody is to blame.
    [Fact]
    public void When_everyone_was_frozen_nobody_is_dropped_for_it()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(2);

        net.Kill(net.Host);
        net.Kill(client);
        net.Run(60);
        net.Host.Dead = false;
        client.Dead = false;
        net.Run(5);

        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.Equal("0=Host,1=Julian", net.Host.PlayerNames);
        Assert.Contains(net.Host.Log, e => e.Message.StartsWith("This side did not run for 60."));
        Assert.Contains(client.Log, e => e.Message.StartsWith("This side did not run for 60."));
    }

    [Fact]
    public void A_short_hitch_is_not_reported_as_a_freeze()
    {
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient("Julian");
        net.Run(2);

        net.Kill(client);
        net.Run(1.5);
        client.Dead = false;
        net.Run(1);

        Assert.DoesNotContain(client.Log, e => e.Message.StartsWith("This side did not run"));
    }

    [Fact]
    public void A_session_started_long_after_the_clock_did_is_not_a_freeze()
    {
        var net = new SessionNet();
        net.Run(500);
        SessionNet.Node client = net.AddClient("Julian");

        net.Run(2);

        Assert.Equal(SessionState.Connected, client.Session.State);
        Assert.DoesNotContain(client.Log, e => e.Message.StartsWith("This side did not run"));
        Assert.DoesNotContain(net.Host.Log, e => e.Message.StartsWith("This side did not run"));
    }
}
