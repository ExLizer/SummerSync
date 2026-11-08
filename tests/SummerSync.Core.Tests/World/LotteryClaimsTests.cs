using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class LotteryClaimsTests
{
    [Fact]
    public void The_first_player_to_ask_cashes_the_ticket_and_the_others_are_told_who()
    {
        var claims = new LotteryClaims();

        Assert.Equal(2, claims.Grant(5, 2, _ => true));
        Assert.Equal(2, claims.Grant(5, 0, _ => true));
        Assert.Equal(2, claims.Grant(5, 1, _ => true));

        // Asking again is still a yes, and another week's ticket is another ticket.
        Assert.Equal(2, claims.Grant(5, 2, _ => true));
        Assert.Equal(0, claims.Grant(6, 0, _ => true));
        Assert.True(claims.TryGetHolder(5, out byte holder));
        Assert.Equal(2, holder);
        Assert.False(claims.TryGetHolder(7, out _));
    }

    [Fact]
    public void A_player_who_left_no_longer_holds_the_ticket()
    {
        var claims = new LotteryClaims();
        Assert.Equal(1, claims.Grant(3, 1, _ => true));

        // Gone from the session: the next to ask cashes it.
        Assert.Equal(0, claims.Grant(3, 0, p => p != 1));

        Assert.Equal(2, claims.Grant(4, 2, _ => true));
        claims.Forget(2);
        Assert.False(claims.TryGetHolder(4, out _));
        Assert.Equal(1, claims.Grant(4, 1, _ => true));
    }
}
