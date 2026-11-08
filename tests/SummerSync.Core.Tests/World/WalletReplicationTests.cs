using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class WalletReplicationTests
{
    // A machine whose game has some money and now and then earns or spends.
    private sealed class Game
    {
        public Game(SessionNet.Node node, float money)
        {
            Node = node;
            Money = money;
            Wallet = new WalletReplication(node.Session, node.Logger);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Wallet.Update(time);
                if (InWorld)
                {
                    Money = Wallet.Sync(Money);
                    Lowest = Math.Min(Lowest, Money);
                    Highest = Math.Max(Highest, Money);
                }
            };
        }

        public SessionNet.Node Node { get; }

        public WalletReplication Wallet { get; }

        public float Money { get; set; }

        public bool InWorld { get; set; } = true;

        public float Lowest { get; set; } = float.MaxValue;

        public float Highest { get; set; } = float.MinValue;

        public void Watch()
        {
            Lowest = Money;
            Highest = Money;
        }
    }

    [Fact]
    public void The_hosts_money_is_everyones_money()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host, 3000f);
        var client = new Game(net.AddClient("Julian"), 120f);
        Assert.False(client.Wallet.Shared);

        net.Run(1);

        Assert.True(client.Wallet.Shared);
        Assert.Equal(3000f, client.Money);
        Assert.Equal(3000f, host.Money);
        Assert.Equal(0, host.Wallet.Changes + client.Wallet.Changes);
    }

    [Fact]
    public void What_a_client_spends_is_gone_at_once_there_and_soon_everywhere()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 80 });
        var host = new Game(net.Host, 3000f);
        var buyer = new Game(net.AddClient("Julian"), 0f);
        var other = new Game(net.AddClient("Second"), 0f);
        net.Run(1);

        buyer.Money -= 12.95f;
        buyer.Watch();
        net.Run(0.02);
        Assert.Equal(2987.05f, buyer.Money, 2);
        Assert.Equal(3000f, host.Money);

        net.Run(1);
        Assert.Equal(2987.05f, host.Money, 2);
        Assert.Equal(2987.05f, other.Money, 2);
        Assert.Equal(2987.05f, buyer.Money, 2);

        // The buyer's own number never went back up while the host's word was on its way.
        Assert.Equal(buyer.Lowest, buyer.Highest, 2);
        Assert.Equal(1, host.Wallet.Changes);
    }

    [Fact]
    public void What_the_host_earns_everyone_has()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host, 100f);
        var client = new Game(net.AddClient("Julian"), 100f);
        net.Run(1);

        host.Money += 3200f;
        net.Run(0.5);

        Assert.Equal(3300f, client.Money);
        Assert.Equal(0, client.Wallet.Changes);
    }

    [Fact]
    public void Two_players_paying_at_the_same_moment_both_pay()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 120 });
        var host = new Game(net.Host, 1000f);
        var first = new Game(net.AddClient("Julian"), 0f);
        var second = new Game(net.AddClient("Second"), 0f);
        net.Run(1);

        first.Money -= 100f;
        second.Money -= 250f;
        host.Money -= 50f;
        net.Run(1.5);

        Assert.Equal(600f, host.Money);
        Assert.Equal(600f, first.Money);
        Assert.Equal(600f, second.Money);
    }

    [Fact]
    public void Paying_again_before_the_host_has_answered_does_not_bring_the_money_back()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 150 });
        var host = new Game(net.Host, 500f);
        var client = new Game(net.AddClient("Julian"), 0f);
        net.Run(1);

        client.Money -= 10f;
        net.Run(0.1);
        client.Money -= 20f;
        client.Watch();
        for (int i = 0; i < 40; i++)
        {
            net.Run(0.05);
            Assert.Equal(470f, client.Money);
        }

        Assert.Equal(470f, host.Money);
        Assert.Equal(470f, client.Lowest);
        Assert.Equal(470f, client.Highest);
    }

    [Fact]
    public void A_client_whose_world_loads_again_takes_the_hosts_money_and_tells_nothing()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host, 800f);
        var client = new Game(net.AddClient("Julian"), 0f);
        net.Run(1);
        host.Money -= 300f;
        net.Run(0.5);
        Assert.Equal(500f, client.Money);

        // Back to the menu and into the game again: the save says something else.
        client.InWorld = false;
        client.Wallet.ClearWorld();
        net.Run(0.5);
        client.Money = 3000f;
        client.InWorld = true;
        net.Run(0.5);

        Assert.Equal(500f, client.Money);
        Assert.Equal(500f, host.Money);
        Assert.Equal(0, client.Wallet.Changes);
    }

    [Fact]
    public void A_host_whose_world_loads_again_says_what_its_save_has()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host, 800f);
        var client = new Game(net.AddClient("Julian"), 0f);
        net.Run(1);

        host.InWorld = false;
        host.Wallet.ClearWorld();
        net.Run(0.5);
        host.Money = 1234f;
        host.InWorld = true;
        net.Run(0.5);

        Assert.Equal(1234f, client.Money);
    }

    [Fact]
    public void What_a_client_spends_while_the_host_is_loading_is_counted_when_the_host_is_back()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host, 800f);
        var client = new Game(net.AddClient("Julian"), 0f);
        net.Run(1);

        host.InWorld = false;
        host.Wallet.ClearWorld();
        client.Money -= 75f;
        net.Run(1);
        host.InWorld = true;
        net.Run(0.5);

        Assert.Equal(725f, host.Money);
        Assert.Equal(725f, client.Money);
    }

    [Fact]
    public void A_float_being_a_float_is_not_money()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Game(net.Host, 100f);
        var client = new Game(net.AddClient("Julian"), 0f);
        net.Run(1);

        client.Money += 0.001f;
        host.Money -= 0.002f;
        net.Run(0.5);

        Assert.Equal(0, host.Wallet.Changes + client.Wallet.Changes);
    }

    [Fact]
    public void Only_the_host_says_how_much_there_is_and_only_money_is_a_change()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Game(net.Host, 100f);
        var client = new Game(net.AddClient("Julian"), 0f);
        net.Run(1);
        var writer = new PacketWriter(16);

        writer.WriteByte(WalletWire.MessageBalance);
        writer.WriteSingle(999999f);
        writer.WriteUInt32(0);
        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        writer.Reset();
        writer.WriteByte(WalletWire.MessageChange);
        writer.WriteUInt32(7);
        writer.WriteSingle(float.NaN);
        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(0.5);

        Assert.Equal(100f, host.Money);
        Assert.Equal(100f, client.Money);
        Assert.Equal(2, host.Wallet.Malformed);
        Assert.Equal(63, WalletWire.MessageBalance);
    }
}
