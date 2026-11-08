using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Objects;

public class ItemReplicationTests
{
    // A machine that hears what happens to objects and what others throw away.
    private sealed class Listener
    {
        public Listener(SessionNet.Node node)
        {
            Node = node;
            Items = new ItemReplication(node.Session, node.Logger);
            Items.StateWanted += (id, name, values) => States.Add(id + " " + name + ": " + string.Join(", ", values.Select(Describe)));
            Items.ThrownWanted += thrown => Thrown.Add(thrown);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Items.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public ItemReplication Items { get; }

        public List<string> States { get; } = new();

        public List<ThrownItem> Thrown { get; } = new();

        private static string Describe(ItemValue value) =>
            value.Name + "=" + (value.Kind == ItemWire.KindFloat ? value.Number.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : value.Whole.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_can_drunk_empty_on_one_machine_is_empty_on_all()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, LossPercent = 5 });
        var host = new Listener(net.Host);
        var drinker = new Listener(net.AddClient("Julian"));
        var other = new Listener(net.AddClient("Second"));
        net.Run(1);

        Assert.True(drinker.Items.TellState(12, "empty plastic can(itemx)", new[] { ItemValue.Bool("ContainsJuice", false), ItemValue.Bool("ContainsKilju", false), ItemValue.Float("KiljuAlc", 0.25f) }));
        net.Run(1);

        string expected = "12 empty plastic can(itemx): ContainsJuice=0, ContainsKilju=0, KiljuAlc=0.25";
        Assert.Equal(new[] { expected }, host.States);
        Assert.Equal(new[] { expected }, other.States);

        // The drinker hears it back, and that is harmless: it is what it has.
        Assert.Equal(new[] { expected }, drinker.States);
    }

    [Fact]
    public void Two_games_changing_the_same_thing_at_once_end_the_same()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 120 });
        var host = new Listener(net.Host);
        var first = new Listener(net.AddClient("Julian"));
        var second = new Listener(net.AddClient("Second"));
        net.Run(1);

        first.Items.TellState(4, "beer case(itemx)", new[] { ItemValue.Int("DestroyedBottles", 1) });
        second.Items.TellState(4, "beer case(itemx)", new[] { ItemValue.Int("DestroyedBottles", 2) });
        net.Run(1.5);

        // What each heard last is what the host heard last.
        string last = host.States.Last();
        Assert.Equal(last, first.States.Last());
        Assert.Equal(last, second.States.Last());
    }

    [Fact]
    public void What_the_host_changes_every_client_hears()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Listener(net.Host);
        var first = new Listener(net.AddClient("Julian"));
        var second = new Listener(net.AddClient("Second"));
        net.Run(1);

        host.Items.TellState(3, "beer case(itemx)", new[] { ItemValue.Int("DestroyedBottles", 7) });
        net.Run(0.5);

        Assert.Equal(new[] { "3 beer case(itemx): DestroyedBottles=7" }, first.States);
        Assert.Equal(new[] { "3 beer case(itemx): DestroyedBottles=7" }, second.States);
        Assert.Empty(host.States);
    }

    [Fact]
    public void An_empty_bottle_thrown_by_a_client_is_thrown_everywhere_with_who_threw_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new Listener(net.Host);
        var drinker = new Listener(net.AddClient("Julian"));
        var other = new Listener(net.AddClient("Second"));
        net.Run(1);

        drinker.Items.TellThrown(new ThrownItem
        {
            Prefab = "MilkFly",
            Name = "empty pack(Clone)",
            Pose = BodyPose.At(10f, 1.5f, -4f),
            Motion = new BodyMotion { VX = 2f, VY = 3f, VZ = 0.5f, AY = 6f },
        });
        net.Run(1);

        ThrownItem heard = Assert.Single(host.Thrown);
        Assert.Equal("MilkFly", heard.Prefab);
        Assert.Equal("empty pack(Clone)", heard.Name);
        Assert.Equal(drinker.Node.Session.LocalPlayerId, heard.Player);
        Assert.Equal(3f, heard.Motion.VY, 3);
        Assert.Equal(10f, heard.Pose.X, 3);
        Assert.Equal(heard, Assert.Single(other.Thrown));
        Assert.Empty(drinker.Thrown);
    }

    [Fact]
    public void Too_much_to_say_about_one_object_is_not_said()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Listener(net.Host);
        var client = new Listener(net.AddClient("Julian"));
        net.Run(1);

        ItemValue[] many = Enumerable.Range(0, ItemWire.MaxValues + 1).Select(i => ItemValue.Int("Value" + i, i)).ToArray();
        Assert.False(client.Items.TellState(1, "thing", many));
        net.Run(0.5);

        Assert.Empty(host.States);
    }

    [Fact]
    public void Only_the_host_passes_news_of_objects_on_and_a_value_must_be_a_number()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Listener(net.Host);
        var client = new Listener(net.AddClient("Julian"));
        net.Run(1);
        var writer = new PacketWriter(200);

        writer.WriteByte(ItemWire.MessageState);
        writer.WriteUInt16(5);
        writer.WriteString("milk(itemx)");
        writer.WriteByte(1);
        writer.WriteString("Condition");
        writer.WriteByte(ItemWire.KindFloat);
        writer.WriteSingle(float.NaN);
        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(0.5);

        Assert.Empty(host.States);
        Assert.Equal(1, host.Items.Malformed);
        Assert.Equal(72, ItemWire.MessageState);
        Assert.Equal(73, ItemWire.MessageThrown);
    }
}
