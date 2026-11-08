using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

/// <summary>The cards of the main menu's saved worlds, and the three copies kept of each.</summary>
public class SavedWorldsTests
{
    [Fact]
    public void The_games_day_is_the_day_of_the_week()
    {
        Assert.Equal("Lunes", SavedWorldText.Weekday(1, true));
        Assert.Equal("Sábado", SavedWorldText.Weekday(6, true));
        Assert.Equal("Sunday", SavedWorldText.Weekday(7, false));
        Assert.Equal(string.Empty, SavedWorldText.Weekday(0, false));
        Assert.Equal(string.Empty, SavedWorldText.Weekday(8, true));
    }

    [Fact]
    public void When_is_the_day_and_the_hour_its_block_starts()
    {
        Assert.Equal("Monday 12:00", SavedWorldText.When(1, 12, false));
        Assert.Equal("Lunes 12:00", SavedWorldText.When(1, 12, true));
        Assert.Equal("Viernes 06:00", SavedWorldText.When(5, 6, true));
        Assert.Equal("22:00", SavedWorldText.When(0, 22, false));
        Assert.Equal("Sunday 00:00", SavedWorldText.When(7, 24, false));
        Assert.Equal("Martes", SavedWorldText.When(2, -1, true));
    }

    [Fact]
    public void Money_shows_as_the_game_shows_it()
    {
        Assert.Equal("3.000 mk", SavedWorldText.Money(3000f));
        Assert.Equal("0 mk", SavedWorldText.Money(0.2));
        Assert.Equal("1.234.568 mk", SavedWorldText.Money(1234567.6));
        Assert.Equal("999 mk", SavedWorldText.Money(999));
        Assert.Equal("-1.500 mk", SavedWorldText.Money(-1500));
    }

    [Fact]
    public void A_world_is_named_after_its_player()
    {
        Assert.Equal("Matti Virtanen", SavedWorldText.Title(" Matti Virtanen ", "x", "y"));
        Assert.Equal("Kukein", SavedWorldText.Title(" Kukein", string.Empty, "Kukein"));
        Assert.Equal("Matti Virtanen", SavedWorldText.Title(null, "Matti", "Virtanen"));
        Assert.Equal("Virtanen", SavedWorldText.Title(string.Empty, null, " Virtanen"));
        Assert.Equal(string.Empty, SavedWorldText.Title(null, null, null));
    }

    [Fact]
    public void A_worlds_hash_needs_its_main_file_and_follows_every_file()
    {
        byte[] main = { 1, 2, 3 };
        byte[] items = { 4, 5 };
        Assert.Equal(0UL, WorldCopies.Hash(new byte[]?[] { null, items }!));
        Assert.Equal(0UL, WorldCopies.Hash(new byte[0][]));

        ulong one = WorldCopies.Hash(new[] { main, items });
        Assert.NotEqual(0UL, one);
        Assert.Equal(one, WorldCopies.Hash(new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5 } }));
        Assert.NotEqual(one, WorldCopies.Hash(new[] { main, new byte[] { 4, 6 } }));
        Assert.NotEqual(one, WorldCopies.Hash(new byte[]?[] { main, null, items }!));
        Assert.NotEqual(one, WorldCopies.Hash(new[] { main }));
    }

    [Fact]
    public void A_copy_is_made_only_of_what_no_copy_has()
    {
        var kept = new List<WorldCopyInfo> { new("20261016-120000", 7UL), new("20261016-130000", 9UL) };
        Assert.False(WorldCopies.Worth(kept, 7UL));
        Assert.False(WorldCopies.Worth(kept, 9UL));
        Assert.True(WorldCopies.Worth(kept, 8UL));
        Assert.True(WorldCopies.Worth(null!, 8UL));
        Assert.False(WorldCopies.Worth(kept, 0UL));
    }

    [Fact]
    public void A_copy_is_named_when_its_world_was_saved()
    {
        var kept = new List<WorldCopyInfo> { new("20261016-183000", 1UL), new("20261016-183000-2", 2UL) };
        Assert.Equal("20261016-190512", WorldCopies.NameFor(new DateTime(2026, 10, 16, 19, 5, 12), kept));
        Assert.Equal("20261016-183000-3", WorldCopies.NameFor(new DateTime(2026, 10, 16, 18, 30, 0), kept));
    }

    [Fact]
    public void Only_the_newest_three_stay()
    {
        var kept = new List<WorldCopyInfo>
        {
            new("20261016-183000", 1UL),
            new("20261015-090000", 2UL),
            new("20261016-183000-2", 3UL),
            new("20261017-000000", 4UL),
            new("20261014-235959", 5UL),
        };

        Assert.Equal(new[] { "20261014-235959", "20261015-090000" }, WorldCopies.ToDrop(kept, WorldCopies.Kept));
        Assert.Empty(WorldCopies.ToDrop(kept.GetRange(0, 3), WorldCopies.Kept));
        Assert.Equal(5, WorldCopies.ToDrop(kept, 0).Count);
        Assert.True(WorldCopies.Older("20261016-183000", "20261016-183000-2") < 0);
        Assert.True(WorldCopies.Older("20261016-183000-10", "20261016-183000-2") > 0);
    }
}
