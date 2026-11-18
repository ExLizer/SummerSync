using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class FlatbedUnloadTests
{
    // One game's flatbed as the game runs it, a frame at a time: "Wait log" until "Unload"; then "Wait", entered once a
    // frame here, takes 100 off and, below 10, "Pay" sets the customer's pay (twice what there was, nothing for an empty
    // pile) and empties the pile; "Unload" false in "Wait" goes back to "Wait log". The mod's check runs first in "Wait".
    private sealed class Game
    {
        public readonly FlatbedUnload Rule = new FlatbedUnload();
        public bool Unload;
        public int Logs;
        public bool Unloading;
        public bool Counting;
        public int Paid = -1;
        public int Pays;
        public int Said = -1;
        public int TurnedBack;
        private int price;

        public Game(int logs)
        {
            Logs = logs;
        }

        public bool Waiting
        {
            get { return !Unloading && !Counting; }
        }

        public void Frame(double now)
        {
            if (Unloading && !Unload)
            {
                Unloading = false;
            }
            else if ((Unloading || (Waiting && Unload)) && !Counting)
            {
                Enter(now);
            }

            int said = Rule.Observe(Waiting, Unload, Logs, now);
            if (said >= 0)
            {
                Said = said;
            }

            bool value;
            if (Rule.Want(Waiting, Unload, Logs, now, out value))
            {
                Unload = value;
            }
        }

        private void Enter(double now)
        {
            Unloading = true;
            int what = Rule.Entering(Logs, now);
            if (what == FlatbedUnload.TurnBack)
            {
                Unload = false;
                Unloading = false;
                TurnedBack++;
                return;
            }

            if (what == FlatbedUnload.GoOnAndSay)
            {
                Said = 1;
            }

            price = Logs > price ? Logs : price;
            Logs -= 100;
            if (Logs < FlatbedUnload.Loaded)
            {
                Paid = price < FlatbedUnload.Loaded ? 0 : price * 2;
                Pays++;
                price = 0;
                Logs = 0;
                Unload = false;
                Unloading = false;
            }
        }
    }

    [Fact]
    public void A_game_that_unloads_says_so_and_the_other_games_unload_their_own_pile_too()
    {
        var host = new Game(60);
        var guest = new Game(60);
        host.Frame(0);
        guest.Frame(0);

        // The guest's box tips with its hatch open: its game unloads and pays within the frame. The host's box did not tip.
        guest.Unload = true;
        guest.Frame(0.1);
        host.Frame(0.1);
        Assert.Equal(1, guest.Said);
        Assert.Equal(0, guest.Logs);
        Assert.Equal(120, guest.Paid);

        host.Rule.Hear(1, false, false, 0.15);
        host.Frame(0.2);
        Assert.True(host.Unload);
        Assert.True(host.Rule.Relayed);
        host.Frame(0.3);

        // The host's pile is down too, and its customer paid as the guest's did: what its save keeps is an empty flatbed.
        Assert.Equal(0, host.Logs);
        Assert.Equal(120, host.Paid);
        Assert.False(host.Unload);
        Assert.False(host.Rule.Relayed);
        Assert.Equal(-1, host.Said);

        // Nothing more is said by either, and nobody pays again.
        guest.Said = -1;
        for (double t = 0.4; t < 2; t += 0.1)
        {
            host.Frame(t);
            guest.Frame(t);
        }

        Assert.Equal(-1, host.Said);
        Assert.Equal(-1, guest.Said);
        Assert.Equal(1, host.Pays);
        Assert.Equal(1, guest.Pays);
    }

    [Fact]
    public void A_box_that_tips_late_over_a_pile_emptied_a_moment_ago_does_not_pay_nothing_over_the_pay()
    {
        var game = new Game(60);
        game.Frame(0);
        game.Rule.Hear(1, false, false, 0.1);
        game.Frame(0.1);
        game.Frame(0.2);
        Assert.Equal(120, game.Paid);
        Assert.Equal(1, game.Pays);

        // Its own box comes to tip now: the game would enter "Wait" with nothing and set the customer's pay to 0.
        game.Unload = true;
        game.Frame(1.0);
        Assert.Equal(1, game.TurnedBack);
        Assert.Equal(1, game.Pays);
        Assert.Equal(120, game.Paid);
        Assert.False(game.Unload);
        Assert.Equal(-1, game.Said);

        // Long after, an empty unload is the game's own business again, as without the mod; it is not said.
        game.Unload = true;
        game.Frame(0.2 + FlatbedUnload.EmptyAgainSeconds + 1);
        Assert.Equal(1, game.TurnedBack);
        Assert.Equal(2, game.Pays);
        Assert.Equal(0, game.Paid);
        Assert.Equal(-1, game.Said);
    }

    [Fact]
    public void Two_games_that_unload_by_themselves_unload_once_each()
    {
        var host = new Game(200);
        var guest = new Game(200);
        host.Frame(0);
        guest.Frame(0);
        host.Unload = true;
        guest.Unload = true;
        host.Frame(0.1);
        guest.Frame(0.1);
        Assert.Equal(1, host.Said);
        Assert.Equal(1, guest.Said);

        // Each hears the other while unloading already: nothing more is written.
        host.Rule.Hear(1, false, false, 0.15);
        guest.Rule.Hear(1, false, false, 0.15);
        for (double t = 0.2; t < 1; t += 0.1)
        {
            host.Frame(t);
            guest.Frame(t);
        }

        Assert.Equal(1, host.Pays);
        Assert.Equal(1, guest.Pays);
        Assert.Equal(400, host.Paid);
        Assert.Equal(400, guest.Paid);
        Assert.False(host.Rule.Relayed);
        Assert.False(guest.Rule.Relayed);
        Assert.False(host.Unload);
        Assert.False(guest.Unload);
    }

    [Fact]
    public void A_box_lowered_halfway_is_said_and_the_games_that_followed_stop_too()
    {
        var host = new Game(1000);
        var guest = new Game(1000);
        host.Frame(0);
        guest.Frame(0);
        guest.Unload = true;
        guest.Frame(0.1);
        Assert.Equal(1, guest.Said);
        host.Rule.Hear(1, false, false, 0.1);
        host.Frame(0.1);
        host.Frame(0.2);
        guest.Frame(0.2);
        Assert.True(host.Unload);
        Assert.True(host.Unloading);

        // The guest's box comes down with logs still on it.
        guest.Unload = false;
        guest.Frame(0.3);
        Assert.Equal(0, guest.Said);
        Assert.True(guest.Logs >= FlatbedUnload.Loaded);

        host.Rule.Hear(0, false, false, 0.3);
        host.Frame(0.35);
        host.Frame(0.4);
        Assert.False(host.Unload);
        Assert.False(host.Unloading);
        Assert.False(host.Rule.Relayed);
        Assert.True(host.Logs >= FlatbedUnload.Loaded);
        Assert.Equal(0, host.Pays);
        Assert.Equal(-1, host.Said);
    }

    [Fact]
    public void A_game_unloading_for_another_keeps_on_when_its_own_box_says_stop()
    {
        var game = new Game(1000);
        game.Frame(0);
        game.Rule.Hear(1, false, false, 0);
        game.Frame(0.1);
        Assert.True(game.Unload);
        game.Frame(0.2);

        // This game's own hatch machine writes "Unload" false as its box (which did not tip here) is seen as down.
        game.Unload = false;
        game.Frame(0.3);
        game.Frame(0.4);
        Assert.True(game.Unload);
        Assert.Equal(-1, game.Said);
        for (double t = 0.5; t < 3; t += 0.1)
        {
            game.Frame(t);
        }

        Assert.Equal(0, game.Logs);
        Assert.Equal(1, game.Pays);
        Assert.Equal(2000, game.Paid);
        Assert.False(game.Rule.Relayed);
    }

    [Fact]
    public void Another_games_unloading_waits_for_the_pile_to_rest_and_not_for_ever()
    {
        var game = new Game(60) { Counting = true };
        game.Frame(0);
        game.Rule.Hear(1, false, false, 0);
        game.Frame(1);
        Assert.False(game.Unload);
        Assert.True(game.Rule.Pending);
        game.Counting = false;
        game.Frame(2);
        game.Frame(2.1);
        Assert.Equal(120, game.Paid);

        var late = new Game(60) { Counting = true };
        late.Frame(0);
        late.Rule.Hear(1, false, false, 0);
        late.Frame(FlatbedUnload.StartWaitSeconds + 0.5);
        late.Counting = false;
        late.Frame(FlatbedUnload.StartWaitSeconds + 0.6);
        Assert.False(late.Unload);
        Assert.False(late.Rule.Pending);
        Assert.Equal(60, late.Logs);
    }

    [Fact]
    public void What_comes_back_or_comes_with_the_catch_up_or_finds_nothing_to_unload_does_nothing()
    {
        var game = new Game(60);
        game.Frame(0);
        game.Rule.Hear(1, true, false, 0);
        game.Frame(0.1);
        game.Rule.Hear(1, false, true, 0.1);
        game.Frame(0.2);
        Assert.False(game.Unload);
        Assert.Equal(60, game.Logs);

        var empty = new Game(0);
        empty.Frame(0);
        empty.Rule.Hear(1, false, false, 0);
        empty.Frame(0.1);
        Assert.False(empty.Unload);
        Assert.Equal(0, empty.Pays);

        // A game whose own box tips over an empty pile, with no unload before it, says nothing.
        empty.Unload = true;
        empty.Frame(0.2);
        Assert.Equal(-1, empty.Said);
    }
}
