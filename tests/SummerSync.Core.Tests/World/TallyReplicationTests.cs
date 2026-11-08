using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class TallyReplicationTests
{
    // One machine's shop: for each product, how many are on the counter and how many were taken off the shelf.
    private sealed class Shop
    {
        public Shop(SessionNet.Node node, int stock = 12)
        {
            Node = node;
            Stock = stock;
            Tallies = new TallyReplication(node.Session, node.Logger);
            Tallies.StateWanted += (name, values) =>
            {
                Counts[name] = values;
                Heard.Add((name, Tallies.CatchingUp));
            };
            Tallies.CaughtUp += () =>
            {
                CaughtUp++;
                CatchingUpWhenCaughtUp |= Tallies.CatchingUp;
            };
            Tallies.ChangeWanted += (name, change, player) =>
            {
                int[] before = Get(name);
                int[] after =
                {
                    Math.Max(0, before[0] + change[0]),
                    Math.Max(0, Math.Min(Stock, before[1] + change[1])),
                };
                Counts[name] = after;
                Tallies.Tell(name, before, after);
            };
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Tallies.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public TallyReplication Tallies { get; }

        public int Stock { get; }

        public Dictionary<string, int[]> Counts { get; } = new();

        /// <summary>Every tally heard, in order, and whether it came as part of the host's answer to a request.</summary>
        public List<(string Name, bool CatchingUp)> Heard { get; } = new();

        public int CaughtUp { get; private set; }

        public bool CatchingUpWhenCaughtUp { get; private set; }

        public int[] Get(string name) => Counts.TryGetValue(name, out int[]? values) ? values : new[] { 0, 0 };

        // The player takes one off the shelf and puts it on the counter, the way the game does.
        public void Take(string name)
        {
            int[] before = Get(name);
            int[] after = { before[0] + 1, before[1] + 1 };
            Counts[name] = after;
            Tallies.Tell(name, before, after);
        }

        // The player pays: the counter empties, what was taken stays taken.
        public void Pay()
        {
            foreach (string name in Counts.Keys.ToList())
            {
                int[] before = Counts[name];
                int[] after = { 0, before[1] };
                Counts[name] = after;
                Tallies.Tell(name, before, after);
            }
        }
    }

    [Fact]
    public void What_anyone_takes_off_the_shelf_is_on_everyone_s_counter()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, LossPercent = 5 });
        var host = new Shop(net.Host);
        var guest = new Shop(net.AddClient("Julian"));
        var other = new Shop(net.AddClient("Second"));
        net.Run(1);

        guest.Take("store/Sausages");
        host.Take("store/Beer");
        net.Run(1);

        foreach (Shop shop in new[] { host, guest, other })
        {
            Assert.Equal(new[] { 1, 1 }, shop.Get("store/Sausages"));
            Assert.Equal(new[] { 1, 1 }, shop.Get("store/Beer"));
        }
    }

    [Fact]
    public void Two_players_taking_the_same_thing_at_once_both_take_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 120 });
        var host = new Shop(net.Host);
        var first = new Shop(net.AddClient("Julian"));
        var second = new Shop(net.AddClient("Second"));
        net.Run(1);

        first.Take("store/Milk");
        second.Take("store/Milk");
        host.Take("store/Milk");
        net.Run(1.5);

        Assert.Equal(new[] { 3, 3 }, host.Get("store/Milk"));
        Assert.Equal(new[] { 3, 3 }, first.Get("store/Milk"));
        Assert.Equal(new[] { 3, 3 }, second.Get("store/Milk"));
    }

    [Fact]
    public void Paying_empties_everyone_s_counter_and_what_was_taken_stays_taken()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new Shop(net.Host);
        var guest = new Shop(net.AddClient("Julian"));
        net.Run(1);
        host.Take("store/Chips");
        guest.Take("store/Chips");
        guest.Take("store/Pizza");
        net.Run(1);

        guest.Pay();
        net.Run(1);

        Assert.Equal(new[] { 0, 2 }, host.Get("store/Chips"));
        Assert.Equal(new[] { 0, 1 }, host.Get("store/Pizza"));
        Assert.Equal(new[] { 0, 2 }, guest.Get("store/Chips"));
        Assert.Equal(new[] { 0, 1 }, guest.Get("store/Pizza"));
    }

    [Fact]
    public void The_shelf_runs_out_on_the_host_s_word()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new Shop(net.Host, stock: 2);
        var guest = new Shop(net.AddClient("Julian"), stock: 2);
        net.Run(1);

        guest.Take("store/Coffee");
        guest.Take("store/Coffee");
        guest.Take("store/Coffee");
        net.Run(1);

        Assert.Equal(2, host.Get("store/Coffee")[1]);
        Assert.Equal(2, guest.Get("store/Coffee")[1]);
    }

    [Fact]
    public void A_client_whose_world_loads_later_gets_what_changed_before()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Shop(net.Host);
        var early = new Shop(net.AddClient("Julian"));
        net.Run(1);
        early.Take("store/Sugar");
        host.Take("store/Yeast");
        net.Run(1);

        var late = new Shop(net.AddClient("Second"));
        net.Run(1);
        Assert.Empty(late.Counts);
        late.Tallies.RequestAll();
        net.Run(0.5);

        Assert.Equal(new[] { 1, 1 }, late.Get("store/Sugar"));
        Assert.Equal(new[] { 1, 1 }, late.Get("store/Yeast"));
    }

    [Fact]
    public void A_client_already_in_its_world_who_asks_while_connecting_is_told_once_connected()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Shop(net.Host);
        host.Take("store/Sugar");
        host.Tallies.Set("sleep/0", new[] { 1 });

        // Joined from the game world: its world is ready before the session is.
        var guest = new Shop(net.AddClient("Julian"));
        Assert.NotEqual(SessionState.Connected, guest.Node.Session.State);
        guest.Tallies.RequestAll();

        // Its own number is not known yet: what it would say about itself now would be about the host.
        guest.Tallies.Set("sleep/" + guest.Node.Session.LocalPlayerId, new[] { 0 });
        net.Run(1);

        Assert.Equal(new[] { 1, 1 }, guest.Get("store/Sugar"));
        Assert.Equal(new[] { 1 }, guest.Get("sleep/0"));
    }

    [Fact]
    public void What_a_player_sets_is_what_everyone_has_whatever_it_was_before()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new Shop(net.Host);
        var first = new Shop(net.AddClient("Julian"));
        var second = new Shop(net.AddClient("Second"));
        net.Run(1);

        // Someone with this number chose before and left; the one who has it now chooses.
        host.Tallies.Set("look/1", new[] { 8 });
        net.Run(0.5);
        first.Tallies.Set("look/1", new[] { 3 });
        second.Tallies.Set("look/2", new[] { 5 });
        net.Run(1);

        foreach (Shop shop in new[] { host, first, second })
        {
            Assert.Equal(new[] { 3 }, shop.Get("look/1"));
            Assert.Equal(new[] { 5 }, shop.Get("look/2"));
        }

        // And a client whose world loads later hears what was set.
        var late = new Shop(net.AddClient("Third"));
        net.Run(1);
        late.Tallies.RequestAll();
        net.Run(0.5);
        Assert.Equal(new[] { 3 }, late.Get("look/1"));
        Assert.Equal(74, TallyWire.MessageSet);
    }

    [Fact]
    public void Only_the_host_says_what_a_tally_is()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Shop(net.Host);
        var guest = new Shop(net.AddClient("Julian"));
        net.Run(1);
        var writer = new PacketWriter(100);

        writer.WriteByte(TallyWire.MessageState);
        writer.WriteString("store/Beer");
        writer.WriteByte(2);
        writer.WriteVarUInt32(SpawnWire.ZigZag(99));
        writer.WriteVarUInt32(SpawnWire.ZigZag(99));
        guest.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        writer.Reset();
        writer.WriteByte(TallyWire.MessageChange);
        writer.WriteString("store/Beer");
        writer.WriteByte(TallyWire.MaxValues + 1);
        guest.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(0.5);

        Assert.Empty(host.Counts);
        Assert.Equal(2, host.Tallies.Malformed);
        Assert.Equal(69, TallyWire.MessageState);
        Assert.Equal(71, TallyWire.MessageRequest);
    }

    [Fact]
    public void The_host_s_answer_comes_between_two_marks_and_is_heard_as_a_catch_up()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50, LossPercent = 10 });
        var host = new Shop(net.Host);
        var early = new Shop(net.AddClient("Julian"));
        net.Run(1);
        early.Take("store/Sugar");
        net.Run(1);
        host.Take("store/Yeast");
        host.Tallies.Set("log/YARD/MachineHall/Logging", new[] { 3, 3, 0, 9000, 0 });
        net.Run(1);

        var late = new Shop(net.AddClient("Second"));
        net.Run(1);
        late.Tallies.RequestAll();
        net.Run(1.5);

        Assert.Equal(new[] { ("store/Sugar", true), ("store/Yeast", true), ("log/YARD/MachineHall/Logging", true) }, late.Heard);
        Assert.Equal(new[] { 3, 3, 0, 9000, 0 }, late.Get("log/YARD/MachineHall/Logging"));
        Assert.False(late.Tallies.CatchingUp);
        Assert.Equal(1, late.CaughtUp);
        Assert.False(late.CatchingUpWhenCaughtUp);
        Assert.Equal(1, late.Tallies.Answers);
        Assert.Equal(1, host.Tallies.Answers);

        // On the wire: the start, the tallies, the end, in that order even with loss.
        byte[] answer = late.Node.Messages.Select(m => m.Data[0])
            .Where(t => t == TallyWire.MessageAllBegin || t == TallyWire.MessageAllEnd || t == TallyWire.MessageState).ToArray();
        Assert.Equal(new byte[] { 97, TallyWire.MessageState, TallyWire.MessageState, TallyWire.MessageState, 98 }, answer);
        Assert.Equal(97, TallyWire.MessageAllBegin);
        Assert.Equal(98, TallyWire.MessageAllEnd);
        Assert.True(AssemblyWire.MessageOwner < TallyWire.MessageAllBegin);

        // The one already in its world heard all of it as it happened: news, never a catch-up.
        Assert.NotEmpty(early.Heard);
        Assert.All(early.Heard, heard => Assert.False(heard.CatchingUp));
        Assert.Equal(0, early.CaughtUp);
        Assert.False(host.Tallies.CatchingUp);
    }

    [Fact]
    public void What_the_host_says_after_its_answer_is_news()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Shop(net.Host);
        host.Take("store/Beer");
        var guest = new Shop(net.AddClient("Julian"));
        net.Run(1);
        guest.Tallies.RequestAll();
        net.Run(1);
        Assert.Equal(new[] { ("store/Beer", true) }, guest.Heard);

        host.Take("store/Beer");
        host.Tallies.Set("pub/order/0", new[] { 1, 1, 0 });
        net.Run(1);

        Assert.Equal(new[] { ("store/Beer", true), ("store/Beer", false), ("pub/order/0", false) }, guest.Heard);
        Assert.Equal(new[] { 2, 2 }, guest.Get("store/Beer"));
        Assert.False(guest.Tallies.CatchingUp);

        // What a client sets reaches the host's own handlers as news too.
        guest.Tallies.Set("look/1", new[] { 4 });
        net.Run(1);
        Assert.Contains(("look/1", false), host.Heard);
        Assert.Contains(("look/1", false), guest.Heard);
    }

    [Fact]
    public void A_request_made_while_connecting_is_answered_once_as_a_catch_up()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Shop(net.Host);
        host.Take("store/Sugar");
        host.Tallies.Set("phone/ring", new[] { 1, 4, 1 });

        // Joined from the game world: several things ask before the session is connected.
        var guest = new Shop(net.AddClient("Julian"));
        Assert.NotEqual(SessionState.Connected, guest.Node.Session.State);
        guest.Tallies.RequestAll();
        guest.Tallies.RequestAll();
        net.Run(1);

        Assert.Equal(new[] { ("store/Sugar", true), ("phone/ring", true) }, guest.Heard);
        Assert.Equal(1, guest.CaughtUp);
        Assert.Equal(1, host.Tallies.Answers);
        Assert.False(guest.Tallies.CatchingUp);
    }

    [Fact]
    public void Requests_made_before_the_answer_starts_are_answered_once_and_a_later_one_again()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new Shop(net.Host);
        host.Take("store/Milk");
        var guest = new Shop(net.AddClient("Julian"));
        net.Run(1);

        // The shop, the fuel and the mounts of a world that has just loaded, in the same frame or the next.
        guest.Tallies.RequestAll();
        guest.Tallies.RequestAll();
        net.Run(0.03);
        guest.Tallies.RequestAll();
        net.Run(1);

        Assert.Equal(1, guest.CaughtUp);
        Assert.Equal(1, host.Tallies.Answers);
        Assert.Single(guest.Heard);

        // The world loaded again (or something found late asks): answered again, whole.
        guest.Tallies.RequestAll();
        net.Run(1);
        Assert.Equal(2, guest.CaughtUp);
        Assert.Equal(2, host.Tallies.Answers);
        Assert.Equal(new[] { ("store/Milk", true), ("store/Milk", true) }, guest.Heard);
    }

    [Fact]
    public void An_answer_with_nothing_in_it_still_ends()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Shop(net.Host);
        var guest = new Shop(net.AddClient("Julian"));
        net.Run(1);

        guest.Tallies.RequestAll();
        net.Run(1);

        Assert.Empty(guest.Heard);
        Assert.Equal(1, guest.CaughtUp);
        Assert.False(guest.Tallies.CatchingUp);

        // And the host asking does nothing.
        host.Tallies.RequestAll();
        net.Run(1);
        Assert.Equal(1, host.Tallies.Answers);
        Assert.Equal(0, host.CaughtUp);
    }

    [Fact]
    public void Only_the_host_marks_an_answer()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Shop(net.Host);
        var guest = new Shop(net.AddClient("Julian"));
        net.Run(1);

        guest.Node.Session.Send(Protocol.HostPlayerId, new[] { TallyWire.MessageAllBegin }, 0, 1, true);
        guest.Node.Session.Send(Protocol.HostPlayerId, new[] { TallyWire.MessageAllEnd }, 0, 1, true);
        net.Run(0.5);

        Assert.Equal(2, host.Tallies.Malformed);
        Assert.False(host.Tallies.CatchingUp);
        Assert.Equal(0, host.CaughtUp);
    }
}
