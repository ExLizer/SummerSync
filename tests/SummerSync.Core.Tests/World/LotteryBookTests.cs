using System.Collections.Generic;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class LotteryBookTests
{
    private static LotteryTicket Bought()
    {
        var ticket = new LotteryTicket { Round = 2, Paid = true };
        int[] line = { 3, 8, 15, 22, 27, 33, 39 };
        line.CopyTo(ticket.Lines[0], 0);
        return ticket;
    }

    private static LotteryDraw Drawn()
    {
        var draw = new LotteryDraw { Round = 2, Pot = 30909443, Jackpots = 1, Done = true };
        new[] { 5, 7, 12, 15, 19, 26, 34 }.CopyTo(draw.Numbers, 0);
        new[] { 1, 21, 28 }.CopyTo(draw.Extra, 0);
        new[] { 1, 6, 72, 4676, 91178 }.CopyTo(draw.Winners, 0);
        new[] { 5100568, 83540, 7517, 177, 38 }.CopyTo(draw.Prizes, 0);
        return draw;
    }

    private static int HearAll(LotteryBook book, List<KeyValuePair<string, int[]>> tallies)
    {
        int last = 0;
        foreach (KeyValuePair<string, int[]> tally in tallies)
        {
            last = book.Hear(tally.Key, tally.Value);
        }

        return last;
    }

    [Fact]
    public void A_ticket_told_in_its_order_arrives_whole_and_equal()
    {
        var book = new LotteryBook();
        LotteryTicket said = Bought();

        Assert.Equal(1, HearAll(book, LotteryBook.Tallies(said)));
        Assert.Equal(said, book.Ticket);
        Assert.Null(book.Draw);
    }

    [Fact]
    public void A_draw_told_in_its_order_arrives_whole_and_equal()
    {
        var book = new LotteryBook();
        LotteryDraw said = Drawn();

        Assert.Equal(2, HearAll(book, LotteryBook.Tallies(said)));
        Assert.Equal(said, book.Draw);
        Assert.Null(book.Ticket);
    }

    [Fact]
    public void Nothing_counts_until_every_part_has_been_heard()
    {
        var book = new LotteryBook();
        List<KeyValuePair<string, int[]>> tallies = LotteryBook.Tallies(Bought());

        // The ticket's own numbers before its lines, as a host's list may give them to a client that joins late.
        Assert.Equal(0, book.Hear(tallies[3].Key, tallies[3].Value));
        Assert.Equal(0, book.Hear(tallies[0].Key, tallies[0].Value));
        Assert.Equal(0, book.Hear(tallies[1].Key, tallies[1].Value));
        Assert.Null(book.Ticket);
        Assert.Equal(1, book.Hear(tallies[2].Key, tallies[2].Value));
        Assert.NotNull(book.Ticket);
    }

    [Fact]
    public void Once_whole_every_new_part_counts_again()
    {
        var book = new LotteryBook();
        HearAll(book, LotteryBook.Tallies(Bought()));

        // The host checked it after the draw: only the ticket's own numbers change.
        Assert.Equal(1, book.Hear(LotteryBook.TicketKey, new[] { 2, 1, 1, 177 }));
        Assert.True(book.Ticket!.Checked);
        Assert.Equal(177, book.Ticket.Winnings);
    }

    [Theory]
    [InlineData("lotto/line1", new[] { 3, 8, 15, 22, 27, 33, 40 })]
    [InlineData("lotto/line2", new[] { 3, 8, 15 })]
    [InlineData("lotto/line4", new[] { 1, 2, 3, 4, 5, 6, 7 })]
    [InlineData("lotto/lineX", new[] { 1, 2, 3, 4, 5, 6, 7 })]
    [InlineData("lotto/numbers", new[] { 1, 2, 3, 4, 5, 6, -1 })]
    [InlineData("lotto/extra", new[] { 1, 2 })]
    [InlineData("lotto/ticket", new[] { 2, 1, 0 })]
    [InlineData("lotto/ticket", new[] { 2, 1, 0, -5 })]
    [InlineData("lotto/draw", new[] { 2, -1, 0, 1 })]
    [InlineData("lotto/unknown", new[] { 1 })]
    public void Numbers_that_cannot_be_the_games_are_refused(string key, int[] values)
    {
        var book = new LotteryBook();
        HearAll(book, LotteryBook.Tallies(Bought()));
        HearAll(book, LotteryBook.Tallies(Drawn()));
        LotteryTicket ticketBefore = book.Ticket!.Copy();
        LotteryDraw drawBefore = book.Draw!.Copy();

        Assert.Equal(0, book.Hear(key, values));
        Assert.Equal(1, book.Refused);
        Assert.Equal(ticketBefore, book.Ticket);
        Assert.Equal(drawBefore, book.Draw);
    }

    [Fact]
    public void Other_tallies_are_not_the_lotterys_and_not_refused()
    {
        var book = new LotteryBook();

        Assert.Equal(0, book.Hear("sauna/heat/house", new[] { 1, 2, 3, 4 }));
        Assert.Equal(0, book.Refused);
    }

    [Fact]
    public void A_copy_is_equal_and_does_not_share_its_lines()
    {
        LotteryTicket ticket = Bought();
        LotteryTicket copy = ticket.Copy();

        Assert.Equal(ticket, copy);
        copy.Lines[0][0] = 4;
        Assert.NotEqual(ticket, copy);
    }

    [Fact]
    public void Every_tally_fits_in_one()
    {
        foreach (KeyValuePair<string, int[]> tally in LotteryBook.Tallies(Bought()))
        {
            Assert.True(tally.Value.Length <= TallyWire.MaxValues, tally.Key);
        }

        foreach (KeyValuePair<string, int[]> tally in LotteryBook.Tallies(Drawn()))
        {
            Assert.True(tally.Value.Length <= TallyWire.MaxValues, tally.Key);
        }
    }
}
