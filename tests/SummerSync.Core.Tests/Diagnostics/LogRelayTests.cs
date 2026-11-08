using SummerSync.Core.Diagnostics;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Diagnostics;

public class LogRelayTests
{
    private static List<string> FromPlayers(SessionNet.Node node) =>
        node.Log.Where(entry => entry.Message.StartsWith(LogRelay.Prefix, StringComparison.Ordinal)).Select(entry => entry.Level + " " + entry.Category + " " + entry.Message).ToList();

    [Fact]
    public void What_went_wrong_on_a_client_is_in_the_hosts_log()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new LogRelay(net.Host.Session, net.Host.Logger, true);
        SessionNet.Node guest = net.AddClient("Julian");
        var client = new LogRelay(guest.Session, guest.Logger, true);
        net.Run(1);
        int before = FromPlayers(net.Host).Count;

        guest.Logger.Info(LogCategory.Vehicle, "The part 'doorl' of 'HAYOSIKO' was off its hinge: 7 mm to the vehicle's right.");
        guest.Logger.Warning(LogCategory.Net, "Something about the network that went wrong.");
        guest.Logger.Info(LogCategory.Fsm, "The key of 'HAYOSIKO' is out.");
        net.Run(1);

        List<string> lines = FromPlayers(net.Host).Skip(before).ToList();
        Assert.Equal(
            new[]
            {
                "Info Vehicle [from player 1] The part 'doorl' of 'HAYOSIKO' was off its hinge: 7 mm to the vehicle's right.",
                "Warning Net [from player 1] Something about the network that went wrong.",
                "Info Fsm [from player 1] The key of 'HAYOSIKO' is out.",
            },
            lines);
        Assert.Equal(0, host.Malformed + client.Malformed);
    }

    [Fact]
    public void Chatter_about_the_network_and_the_players_stays_on_the_client()
    {
        var net = new SessionNet(new NetworkConditions());
        _ = new LogRelay(net.Host.Session, net.Host.Logger, true);
        SessionNet.Node guest = net.AddClient("Julian");
        _ = new LogRelay(guest.Session, guest.Logger, true);
        net.Run(1);
        int before = FromPlayers(net.Host).Count;

        guest.Logger.Info(LogCategory.Net, "Connected.");
        guest.Logger.Info(LogCategory.Player, "Someone moved.");
        guest.Logger.Info(LogCategory.General, "Log file: somewhere on this machine.");
        guest.Logger.Debug(LogCategory.Vehicle, "Too much detail.");
        net.Run(1);

        Assert.Equal(before, FromPlayers(net.Host).Count);
    }

    [Fact]
    public void The_host_does_not_send_its_log_to_anyone_and_a_loopback_end_sends_nothing()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new LogRelay(net.Host.Session, net.Host.Logger, true);
        SessionNet.Node guest = net.AddClient("Julian");
        var silent = new LogRelay(guest.Session, guest.Logger, false);
        net.Run(1);

        net.Host.Logger.Warning(LogCategory.Vehicle, "On the host.");
        guest.Logger.Warning(LogCategory.Vehicle, "On an end that shares its log with the host.");
        net.Run(1);

        Assert.Equal(0, host.Lines);
        Assert.Equal(0, silent.Lines);
        Assert.Empty(FromPlayers(guest));
        Assert.Empty(FromPlayers(net.Host));
    }

    [Fact]
    public void A_flood_is_cut_short_and_the_host_is_told_how_much_is_missing()
    {
        var net = new SessionNet(new NetworkConditions());
        _ = new LogRelay(net.Host.Session, net.Host.Logger, true);
        SessionNet.Node guest = net.AddClient("Julian");
        _ = new LogRelay(guest.Session, guest.Logger, true);
        net.Run(1);
        int before = FromPlayers(net.Host).Count;

        for (int i = 0; i < 500; i++)
        {
            guest.Logger.Info(LogCategory.Vehicle, "Line " + i + " " + new string('x', 600));
        }

        net.Run(10);

        List<string> lines = FromPlayers(net.Host).Skip(before).ToList();
        Assert.InRange(lines.Count, 100, 210);
        Assert.Contains(lines, line => line.Contains("line(s) of this log were not sent to the host"));

        // A long line arrives cut, and as one line.
        Assert.All(lines, line => Assert.True(line.Length < 500, line.Length + " characters"));
        Assert.StartsWith("Info Vehicle [from player 1] Line 0 xxx", lines[0]);
    }

    [Fact]
    public void After_the_session_nothing_is_kept_or_sent()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new LogRelay(net.Host.Session, net.Host.Logger, true);
        SessionNet.Node guest = net.AddClient("Julian");
        var client = new LogRelay(guest.Session, guest.Logger, true);
        net.Run(1);
        long sent = client.Lines;

        client.Detach();
        host.Detach();
        guest.Logger.Warning(LogCategory.Vehicle, "After the session.");
        net.Run(1);

        Assert.Equal(sent, client.Lines);
        Assert.DoesNotContain(FromPlayers(net.Host), line => line.Contains("After the session"));
    }
}
