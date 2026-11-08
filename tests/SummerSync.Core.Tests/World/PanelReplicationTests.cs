using SummerSync.Core.Net;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class PanelReplicationTests
{
    private const int Poker = 0;
    private const int Slots = 300;
    private const uint Shape = 0xC0FFEE;

    // One machine's video poker, as the game side drives it: presses made, parts shown, who works it.
    private sealed class Machine
    {
        public Machine(SessionNet.Node node, uint shape = Shape)
        {
            Node = node;
            Panels = new PanelReplication(node.Session, node.Logger);
            Panels.Define(Poker, Slots, shape);
            Panels.PressWanted += (panel, button) => Made.Add(button);
            Panels.ShowWanted += (panel, slot, value) => Screen[slot] = value;
            Panels.OperatorChanged += (panel, player) => Workers.Add(player);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Panels.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public PanelReplication Panels { get; }

        public List<int> Made { get; } = new();

        public Dictionary<int, string> Screen { get; } = new();

        public List<byte> Workers { get; } = new();

        public byte Id => Node.Session.LocalPlayerId;

        // The player presses a button: their own game makes it if it works the machine, the host decides otherwise.
        public void Press(int button)
        {
            if (Panels.IsMine(Poker))
            {
                Made.Add(button);
            }
            else
            {
                Panels.Press(Poker, button);
            }
        }
    }

    [Fact]
    public void Whoever_presses_an_idle_machine_works_it_and_their_press_is_made_in_their_game()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50, LossPercent = 5 });
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"));
        net.Run(1);

        guest.Press(7);
        net.Run(1);

        Assert.Equal(guest.Id, host.Panels.Operator(Poker));
        Assert.True(guest.Panels.IsMine(Poker));
        Assert.Equal(new[] { 7 }, guest.Made);
        Assert.Empty(host.Made);
    }

    [Fact]
    public void Presses_of_the_others_are_made_in_the_game_of_whoever_works_the_machine()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50, LossPercent = 5 });
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"));
        var other = new Machine(net.AddClient("Second"));
        net.Run(1);

        host.Press(1);
        net.Run(1);
        guest.Press(2);
        other.Press(3);
        host.Press(4);
        net.Run(1);

        Assert.Equal(new[] { 1, 2, 3, 4 }.OrderBy(b => b), host.Made.OrderBy(b => b));
        Assert.Empty(guest.Made);
        Assert.Empty(other.Made);
        Assert.Equal(host.Id, other.Panels.Operator(Poker));
    }

    [Fact]
    public void Two_players_pressing_an_idle_machine_at_once_do_not_both_start_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 80 });
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"));
        var other = new Machine(net.AddClient("Second"));
        net.Run(1);

        guest.Press(10);
        other.Press(11);
        net.Run(1);

        byte worker = host.Panels.Operator(Poker);
        Machine works = worker == guest.Id ? guest : other;
        Machine watches = worker == guest.Id ? other : guest;
        Assert.Equal(new[] { 10, 11 }.OrderBy(b => b), works.Made.OrderBy(b => b));
        Assert.Empty(watches.Made);
        Assert.Equal(worker, watches.Panels.Operator(Poker));
    }

    [Fact]
    public void What_the_machine_shows_in_the_workers_game_is_shown_in_the_others_but_not_sent_back()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50, LossPercent = 5 });
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"));
        var other = new Machine(net.AddClient("Second"));
        net.Run(1);

        guest.Press(0);
        net.Run(1);
        for (int slot = 0; slot < Slots; slot++)
        {
            guest.Panels.Show(Poker, slot, "1|Pokeri-Card-Material-" + slot + "|Pokeri-Card-A-Black");
        }

        net.Run(2);
        guest.Panels.Show(Poker, 5, "0|changed");
        net.Run(1);

        Assert.Equal(Slots, host.Screen.Count);
        Assert.Equal(Slots, other.Screen.Count);
        Assert.Empty(guest.Screen);
        Assert.Equal("0|changed", other.Screen[5]);
        Assert.Equal("1|Pokeri-Card-Material-299|Pokeri-Card-A-Black", host.Screen[299]);
    }

    [Fact]
    public void Letting_go_makes_the_machine_idle_for_everyone()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"));
        net.Run(1);

        guest.Press(0);
        net.Run(1);
        guest.Panels.Release(Poker);
        net.Run(1);

        Assert.Equal(PanelWire.NoOperator, host.Panels.Operator(Poker));
        Assert.Equal(PanelWire.NoOperator, guest.Panels.Operator(Poker));

        host.Press(3);
        net.Run(1);
        Assert.Equal(new[] { 3 }, host.Made);
    }

    [Fact]
    public void A_worker_who_leaves_lets_go_of_the_machine()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"));
        var other = new Machine(net.AddClient("Second"));
        net.Run(1);

        guest.Press(0);
        net.Run(1);
        guest.Node.Session.Close();
        net.Run(1);

        Assert.Equal(PanelWire.NoOperator, host.Panels.Operator(Poker));
        Assert.Equal(PanelWire.NoOperator, other.Panels.Operator(Poker));
    }

    [Fact]
    public void A_player_who_comes_later_is_told_who_works_the_machine_and_all_it_shows()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50 });
        var host = new Machine(net.Host);
        net.Run(1);
        host.Press(0);
        for (int slot = 0; slot < Slots; slot += 3)
        {
            host.Panels.Show(Poker, slot, "shown " + slot);
        }

        net.Run(1);
        var late = new Machine(net.AddClient("Late"));
        net.Run(1);
        late.Panels.RequestAll();
        net.Run(1);

        Assert.Equal(host.Id, late.Panels.Operator(Poker));
        Assert.Equal(Slots / 3, late.Screen.Count);
        Assert.Equal("shown 297", late.Screen[297]);
    }

    [Fact]
    public void A_player_already_in_their_world_who_asks_while_connecting_is_told_once_connected()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50 });
        var host = new Machine(net.Host);
        net.Run(1);
        host.Press(0);
        host.Panels.Show(Poker, 12, "shown 12");
        net.Run(1);

        var late = new Machine(net.AddClient("Late"));
        Assert.NotEqual(SessionState.Connected, late.Node.Session.State);
        late.Panels.RequestAll();
        net.Run(1);

        Assert.Equal(host.Id, late.Panels.Operator(Poker));
        Assert.Equal("shown 12", late.Screen[12]);
    }

    [Fact]
    public void A_machine_listed_differently_in_another_build_is_not_shown()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"), Shape + 1);
        net.Run(1);

        host.Press(0);
        host.Panels.Show(Poker, 1, "something");
        net.Run(1);

        Assert.Empty(guest.Screen);
        Assert.True(guest.Panels.Malformed > 0);
    }

    [Fact]
    public void The_panels_messages_follow_the_last_ones_in_use()
    {
        Assert.Equal(79, PanelWire.MessagePress);
        Assert.Equal(84, PanelWire.MessageRequest);
    }

    [Fact]
    public void The_host_keeps_a_game_it_was_playing_and_a_client_cannot_claim_one()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"));
        net.Run(1);

        guest.Panels.Claim(Poker);
        net.Run(1);
        Assert.Equal(PanelWire.NoOperator, host.Panels.Operator(Poker));

        host.Panels.Claim(Poker);
        net.Run(1);
        Assert.Equal(host.Id, guest.Panels.Operator(Poker));
        guest.Press(5);
        net.Run(1);
        Assert.Equal(new[] { 5 }, host.Made);
    }

    [Fact]
    public void Only_whoever_works_the_machine_can_say_what_it_shows()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        var guest = new Machine(net.AddClient("Julian"));
        net.Run(1);

        // Nobody works it: what a game would show is not sent.
        guest.Panels.Show(Poker, 1, "mine");
        host.Panels.Show(Poker, 2, "mine too");
        net.Run(1);

        Assert.Empty(host.Screen);
        Assert.Empty(guest.Screen);
        Assert.Null(host.Panels.Shown(Poker, 1));
    }
}
