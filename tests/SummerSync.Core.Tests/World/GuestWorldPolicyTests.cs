using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class GuestWorldPolicyTests
{
    [Fact]
    public void A_guest_whose_hosts_world_is_here_goes_into_it_from_the_menu()
    {
        GuestWorldDecision decision = GuestWorldPolicy.Decide(true, true, 42, SaveFolder.Own, 0, false);

        Assert.Equal(SaveFolder.Host, decision.Folder);
        Assert.Equal(42UL, decision.World);
        Assert.True(decision.Prepare);
        Assert.Equal(MenuOffer.Host, decision.Menu);
    }

    [Fact]
    public void Until_the_hosts_world_is_here_a_guest_is_offered_nothing()
    {
        GuestWorldDecision decision = GuestWorldPolicy.Decide(true, true, 0, SaveFolder.Own, 0, false);

        Assert.Equal(SaveFolder.Own, decision.Folder);
        Assert.False(decision.Prepare);
        Assert.Equal(MenuOffer.Wait, decision.Menu);
    }

    [Fact]
    public void In_the_world_nothing_changes_even_when_the_session_ends()
    {
        GuestWorldDecision lostHost = GuestWorldPolicy.Decide(false, false, 0, SaveFolder.Host, 42, true);
        GuestWorldDecision joinedLate = GuestWorldPolicy.Decide(false, true, 42, SaveFolder.Own, 0, true);

        Assert.Equal(SaveFolder.Host, lostHost.Folder);
        Assert.Equal(42UL, lostHost.World);
        Assert.False(lostHost.Prepare);
        Assert.Equal(SaveFolder.Own, joinedLate.Folder);
        Assert.False(joinedLate.Prepare);
    }

    [Fact]
    public void Back_in_the_menu_without_a_session_the_own_world_is_back()
    {
        GuestWorldDecision decision = GuestWorldPolicy.Decide(true, false, 42, SaveFolder.Host, 42, true);

        Assert.Equal(SaveFolder.Own, decision.Folder);
        Assert.Equal(0UL, decision.World);
        Assert.Equal(MenuOffer.Game, decision.Menu);
    }

    [Fact]
    public void Another_of_the_hosts_worlds_being_fetched_takes_away_the_one_here()
    {
        // The host chose another world in the main menu's "HOST": the copy here is not a save of it.
        GuestWorldDecision decision = GuestWorldPolicy.Decide(true, true, 0, SaveFolder.Host, 42, false, false);

        Assert.Equal(SaveFolder.Own, decision.Folder);
        Assert.Equal(0UL, decision.World);
        Assert.Equal(MenuOffer.Wait, decision.Menu);

        // Once it is here, it is the one used, copied fresh.
        GuestWorldDecision ready = GuestWorldPolicy.Decide(true, true, 77, SaveFolder.Own, 0, false, false);
        Assert.Equal(SaveFolder.Host, ready.Folder);
        Assert.Equal(77UL, ready.World);
        Assert.True(ready.Prepare);
        Assert.Equal(MenuOffer.Host, ready.Menu);
    }

    [Fact]
    public void A_world_being_fetched_does_not_take_away_the_one_that_is_ready()
    {
        // The host saved again: the new one is on its way, the old one is still here and in use.
        GuestWorldDecision decision = GuestWorldPolicy.Decide(true, true, 0, SaveFolder.Host, 42, false);

        Assert.Equal(SaveFolder.Host, decision.Folder);
        Assert.Equal(42UL, decision.World);
        Assert.False(decision.Prepare);
        Assert.Equal(MenuOffer.Host, decision.Menu);
    }

    [Fact]
    public void A_copy_that_was_played_in_is_not_offered_while_a_newer_world_arrives()
    {
        GuestWorldDecision decision = GuestWorldPolicy.Decide(true, true, 0, SaveFolder.Host, 42, true);

        Assert.Equal(SaveFolder.Own, decision.Folder);
        Assert.Equal(MenuOffer.Wait, decision.Menu);
    }

    [Fact]
    public void A_new_world_or_a_copy_that_was_played_in_is_copied_again()
    {
        Assert.True(GuestWorldPolicy.Decide(true, true, 43, SaveFolder.Host, 42, false).Prepare);
        Assert.True(GuestWorldPolicy.Decide(true, true, 42, SaveFolder.Host, 42, true).Prepare);
        Assert.False(GuestWorldPolicy.Decide(true, true, 42, SaveFolder.Host, 42, false).Prepare);
    }

    [Fact]
    public void The_host_and_a_player_alone_keep_their_own_world_and_menu()
    {
        GuestWorldDecision decision = GuestWorldPolicy.Decide(true, false, 0, SaveFolder.Own, 0, false);

        Assert.Equal(SaveFolder.Own, decision.Folder);
        Assert.False(decision.Prepare);
        Assert.Equal(MenuOffer.Game, decision.Menu);
    }
}
