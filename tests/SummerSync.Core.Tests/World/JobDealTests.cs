using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class JobDealTests
{
    [Fact]
    public void The_game_that_enters_first_runs_the_machine_and_the_others_follow_it_paused_until_it_ends()
    {
        var host = new JobDeal();
        var guest = new JobDeal();

        // The guest's player brings the bottle: the guest's game deals.
        Assert.True(guest.Entered(1, ends: false));
        Assert.Equal(1, guest.Dealer);
        Assert.Equal(JobDealStep.FollowPaused, host.Heard(0, 1, ends: false));
        Assert.Equal(1, host.Dealer);

        // Its next states: the host follows, paused.
        Assert.True(guest.Entered(1, ends: false));
        Assert.Equal(JobDealStep.FollowPaused, host.Heard(0, 1, ends: false));

        // Over: the host's machine goes on by itself from there.
        Assert.True(guest.Entered(1, ends: true));
        Assert.Equal(JobDeal.Nobody, guest.Dealer);
        Assert.Equal(JobDealStep.FollowAndResume, host.Heard(0, 1, ends: true));
        Assert.Equal(JobDeal.Nobody, host.Dealer);

        // An end said while nobody deals is followed as any relayed state.
        Assert.Equal(JobDealStep.Follow, host.Heard(0, 1, ends: true));
    }

    [Fact]
    public void Two_games_that_start_at_once_leave_the_deal_to_the_smaller_number()
    {
        var host = new JobDeal();
        var guest = new JobDeal();

        Assert.True(host.Entered(0, ends: false));
        Assert.True(guest.Entered(2, ends: false));

        // Each hears the other's: the guest gives way, the host keeps it.
        Assert.Equal(JobDealStep.Ignore, host.Heard(0, 2, ends: false));
        Assert.Equal(0, host.Dealer);
        Assert.Equal(JobDealStep.FollowPaused, guest.Heard(2, 0, ends: false));
        Assert.Equal(0, guest.Dealer);

        // A state the guest's game entered before it heard: not told, and the host does not follow it.
        Assert.False(guest.Entered(2, ends: false));
        Assert.Equal(JobDealStep.Ignore, host.Heard(0, 2, ends: false));

        // A smaller number that starts while a larger one deals takes the deal.
        var third = new JobDeal();
        Assert.Equal(JobDealStep.FollowPaused, third.Heard(3, 2, ends: false));
        Assert.Equal(JobDealStep.FollowPaused, third.Heard(3, 0, ends: false));
        Assert.Equal(0, third.Dealer);
    }

    [Fact]
    public void A_dealer_who_leaves_ends_the_deal_and_news_of_oneself_is_nothing()
    {
        var host = new JobDeal();
        Assert.Equal(JobDealStep.FollowPaused, host.Heard(0, 3, ends: false));
        Assert.Equal(JobDealStep.Ignore, host.Heard(0, 0, ends: false));
        Assert.False(host.Left(2));
        Assert.True(host.Left(3));
        Assert.Equal(JobDeal.Nobody, host.Dealer);
        host.Heard(0, 3, ends: false);
        host.Reset();
        Assert.Equal(JobDeal.Nobody, host.Dealer);
    }

    [Fact]
    public void A_state_travels_with_its_number_its_player_and_its_things()
    {
        var news = new JobRelayNews { State = 4, Serial = 17, Player = 2, Things = new ushort[] { 301, 0 } };
        int[] values = news.Encode();
        Assert.Equal(new[] { 4, 17, 2, 301, 0 }, values);

        Assert.True(JobRelayNews.TryDecode(values, out JobRelayNews heard));
        Assert.Equal(4, heard.State);
        Assert.Equal(17, heard.Serial);
        Assert.Equal(2, heard.Player);
        Assert.Equal(new ushort[] { 301, 0 }, heard.Things);

        // An older build's: the state and its number only.
        Assert.True(JobRelayNews.TryDecode(new[] { 1, 3 }, out JobRelayNews old));
        Assert.Equal(-1, old.Player);
        Assert.Empty(old.Things);
        Assert.False(JobRelayNews.TryDecode(new[] { 1 }, out _));
        Assert.True(JobRelayNews.TryDecode(new[] { 0, 1, 0, 70000 }, out JobRelayNews odd));
        Assert.Equal((ushort)0, odd.Things[0]);
        Assert.True(news.Encode().Length <= TallyWire.MaxValues);
    }
}
