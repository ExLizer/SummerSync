using SummerSync.Core.Net;
using SummerSync.Core.Players;
using SummerSync.Core.Reports;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Reports;

public class ReportReplicationTests
{
    private sealed class Player
    {
        private readonly List<ReportNotice> notices = new();

        public Player(SessionNet.Node node)
        {
            Node = node;
            Reports = new ReportReplication(node.Session, node.Logger);
            node.BeforeUpdate = time => Reports.Update(time);
        }

        public SessionNet.Node Node { get; }

        public ReportReplication Reports { get; }

        /// <summary>Every notice so far, as "Name: code".</summary>
        public List<string> Heard
        {
            get
            {
                Reports.Take(notices);
                return notices.Select(n => n.Name + ": " + n.Code).ToList();
            }
        }
    }

    [Fact]
    public void A_report_is_told_to_every_other_player_once_with_who_sent_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, JitterMs = 20, LossPercent = 10 });
        var host = new Player(net.Host);
        var ana = new Player(net.AddClient("Ana"));
        var beto = new Player(net.AddClient("Beto"));
        net.Run(2);

        Assert.True(ana.Reports.Announce("R-4F2K7Q"));
        net.Run(1);
        Assert.True(host.Reports.Announce("r-9xyzab"));
        net.Run(2);

        Assert.Equal(new[] { "Ana: R-4F2K7Q" }, host.Heard);
        Assert.Equal(new[] { "Host: R-9XYZAB" }, ana.Heard);
        Assert.Equal(new[] { "Ana: R-4F2K7Q", "Host: R-9XYZAB" }, beto.Heard);
        Assert.Equal(0, host.Reports.Malformed + ana.Reports.Malformed + beto.Reports.Malformed);
    }

    [Fact]
    public void Nothing_is_told_without_a_session_or_without_a_code()
    {
        var net = new SessionNet();
        var host = new Player(net.Host);
        var ana = new Player(net.AddClient("Ana"));
        Assert.False(ana.Reports.Announce("R-4F2K7Q"));
        net.Run(2);
        Assert.False(ana.Reports.Announce("not a code"));
        net.Run(1);
        Assert.Empty(host.Heard);
    }

    [Fact]
    public void A_notice_with_something_that_is_not_a_code_is_refused()
    {
        var net = new SessionNet();
        var host = new Player(net.Host);
        var ana = new Player(net.AddClient("Ana"));
        net.Run(2);

        byte[] wrong = { ReportWire.MessageSent, 3, (byte)'b', (byte)'a', (byte)'d' };
        ana.Node.Session.Send(Protocol.HostPlayerId, wrong, 0, wrong.Length, true);
        net.Run(1);
        Assert.Equal(1, host.Reports.Malformed);
        Assert.Empty(host.Heard);
    }

    [Fact]
    public void The_report_messages_follow_the_chats()
    {
        Assert.Equal(ChatWire.MessageLine + 1, ReportWire.MessageSent);
        Assert.Equal(ChatWire.MessageLine + 2, ReportWire.MessageNotice);
    }
}
