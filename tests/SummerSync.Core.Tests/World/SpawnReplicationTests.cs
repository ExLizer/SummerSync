using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class SpawnReplicationTests
{
    // A machine whose game makes things, and makes what the others made.
    private sealed class Game
    {
        public Game(SessionNet.Node node)
        {
            Node = node;
            Spawns = new SpawnReplication(node.Session, node.Logger);
            Spawns.Wanted += made => Made.Add(made);
            Spawns.ContentsWanted += (name, lists) => Contents[name] = lists.Select(list => list.Name + ":" + string.Join(",", list.Values)).ToArray();
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Spawns.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public SpawnReplication Spawns { get; }

        public List<SpawnEvent> Made { get; } = new();

        public Dictionary<string, string[]> Contents { get; } = new();
    }

    private static SpawnEvent Beer(int number)
    {
        return new SpawnEvent { Spawner = "Spawner/CreateItems :: Beer", Number = number, Pose = BodyPose.At(10f, 2f, -3f) };
    }

    [Fact]
    public void What_a_client_makes_is_made_by_the_host_and_the_other_clients_but_not_again_by_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40, LossPercent = 5 });
        var host = new Game(net.Host);
        var buyer = new Game(net.AddClient("Julian"));
        var other = new Game(net.AddClient("Second"));
        net.Run(1);

        buyer.Spawns.Report(new SpawnEvent { Spawner = "Spawner/CreateItems :: Sausages", Number = 4, Pose = BodyPose.At(1f, 2f, 3f), Near = "shoppingbagx4" });
        net.Run(1);

        SpawnEvent made = Assert.Single(host.Made);
        Assert.Equal("Spawner/CreateItems :: Sausages", made.Spawner);
        Assert.Equal(4, made.Number);
        Assert.Equal("shoppingbagx4", made.Near);
        Assert.Equal(buyer.Node.Session.LocalPlayerId, made.Player);
        Assert.Equal(1f, made.Pose.X, 3);
        Assert.Equal(made, Assert.Single(other.Made));
        Assert.Empty(buyer.Made);
    }

    [Fact]
    public void Food_keeps_the_freshness_its_game_gave_it_and_the_rest_has_none()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host);
        var buyer = new Game(net.AddClient("Julian"));
        var other = new Game(net.AddClient("Second"));
        net.Run(1);

        buyer.Spawns.Report(new SpawnEvent { Spawner = "Spawner/CreateItems :: Sausages", Number = 5, Pose = BodyPose.At(1f, 2f, 3f), HasCondition = true, Condition = 97.25f });
        buyer.Spawns.Report(Beer(6));
        net.Run(1);

        SpawnEvent sausages = host.Made[0];
        Assert.True(sausages.HasCondition);
        Assert.Equal(97.25f, sausages.Condition);
        Assert.False(host.Made[1].HasCondition);
        Assert.Equal(new[] { true, false }, other.Made.Select(made => made.HasCondition));
        Assert.Equal(97.25f, other.Made[0].Condition);
    }

    [Fact]
    public void A_freshness_that_is_not_a_number_is_not_sent()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host);
        var client = new Game(net.AddClient("Julian"));
        net.Run(1);

        host.Spawns.Report(new SpawnEvent { Spawner = "Spawner/CreateItems :: Milk", Number = 2, Pose = BodyPose.At(1f, 2f, 3f), HasCondition = true, Condition = float.NaN });
        net.Run(0.5);

        SpawnEvent milk = Assert.Single(client.Made);
        Assert.False(milk.HasCondition);
        Assert.Equal(0L, client.Spawns.Malformed);
    }

    [Fact]
    public void What_the_host_makes_every_client_makes()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Game(net.Host);
        var first = new Game(net.AddClient("Julian"));
        var second = new Game(net.AddClient("Second"));
        net.Run(1);

        host.Spawns.Report(Beer(3));
        host.Spawns.Report(Beer(4));
        net.Run(0.5);

        Assert.Equal(new[] { 3, 4 }, first.Made.Select(made => made.Number));
        Assert.Equal(new[] { 3, 4 }, second.Made.Select(made => made.Number));
        Assert.All(first.Made, made => Assert.Equal(Protocol.HostPlayerId, made.Player));
        Assert.Empty(host.Made);
    }

    [Fact]
    public void A_client_that_says_it_made_too_much_is_not_passed_on()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Game(net.Host);
        var flood = new Game(net.AddClient("Julian"));
        var other = new Game(net.AddClient("Second"));
        net.Run(1);

        for (int i = 1; i <= 200; i++)
        {
            flood.Spawns.Report(Beer(i));
        }

        net.Run(0.5);

        Assert.InRange(host.Made.Count, (int)SpawnReplication.MadeBurst, (int)SpawnReplication.MadeBurst + 20);
        Assert.Equal(host.Made.Count, other.Made.Count);
        Assert.Equal(200 - host.Made.Count, host.Spawns.Dropped);
    }

    [Fact]
    public void What_a_container_holds_ends_the_same_everywhere_whoever_changed_it_last()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 100 });
        var host = new Game(net.Host);
        var buyer = new Game(net.AddClient("Julian"));
        var other = new Game(net.AddClient("Second"));
        net.Run(1);

        // The host and a client change the same bag at the same moment.
        host.Spawns.ReportContents("shoppingbagx4", new[] { new ContainerList("Items", new[] { 0, 2, 0, 1 }) });
        buyer.Spawns.ReportContents("shoppingbagx4", new[] { new ContainerList("Items", new[] { 0, 1, 0, 1 }), new ContainerList("Spraycans", new[] { 0, -1, 3 }) });
        net.Run(1);

        string[] expected = { "Items:0,1,0,1", "Spraycans:0,-1,3" };
        Assert.Equal(expected, host.Contents["shoppingbagx4"]);
        Assert.Equal(expected, buyer.Contents["shoppingbagx4"]);
        Assert.Equal(expected, other.Contents["shoppingbagx4"]);
    }

    [Fact]
    public void A_container_too_big_for_one_message_is_not_told()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Game(net.Host);
        var client = new Game(net.AddClient("Julian"));
        net.Run(1);

        var huge = new[] { new ContainerList("Items", Enumerable.Repeat(int.MaxValue, 256).ToArray()) };
        Assert.True(SpawnWire.ContentsSize("bag", huge) > SpawnWire.MaxContentsBytes);
        client.Spawns.ReportContents("bag", huge);
        net.Run(0.5);

        Assert.Empty(host.Contents);
        Assert.Equal(0, client.Spawns.ContentsTold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Numbers_of_either_sign_survive_the_wire(int value)
    {
        Assert.Equal(value, SpawnWire.UnZigZag(SpawnWire.ZigZag(value)));
    }

    [Fact]
    public void Only_the_host_says_what_to_make_and_a_thing_needs_a_maker_and_a_number()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Game(net.Host);
        var client = new Game(net.AddClient("Julian"));
        var other = new Game(net.AddClient("Second"));
        net.Run(1);
        var writer = new PacketWriter(200);

        // A client pretending to be the host.
        writer.WriteByte(SpawnWire.MessageMake);
        writer.WriteByte(0);
        writer.WriteString("Spawner/CreateItems :: Beer");
        writer.WriteInt32(5);
        BodyPose.At(0f, 0f, 0f).Write(writer);
        writer.WriteString(string.Empty);
        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);

        // Something with no number.
        writer.Reset();
        writer.WriteByte(SpawnWire.MessageMade);
        writer.WriteString("Spawner/CreateItems :: Beer");
        writer.WriteInt32(0);
        BodyPose.At(0f, 0f, 0f).Write(writer);
        writer.WriteString(string.Empty);
        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(0.5);

        Assert.Empty(host.Made);
        Assert.Empty(other.Made);
        Assert.Equal(2, host.Spawns.Malformed);
        Assert.Equal(65, SpawnWire.MessageMade);
        Assert.Equal(67, SpawnWire.MessageContents);
    }
}
