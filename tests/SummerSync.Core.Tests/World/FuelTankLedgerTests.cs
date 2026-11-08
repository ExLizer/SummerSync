using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class FuelTankLedgerTests
{
    // Two machines: the one that runs the van and one that does not. Messages are passed by hand.
    private sealed class Pair
    {
        public FuelTankLedger Runner { get; } = new();

        public FuelTankLedger Watcher { get; } = new();

        public int Tank { get; set; } = 2400;

        public void RunnerSays()
        {
            Watcher.Heard(Runner.Word(Tank, 0));
        }

        public void HostCounts()
        {
            int[] added = Watcher.Added();
            Runner.HeardAdded(added[0], added[1]);
        }

        public void RunnerTakesIn()
        {
            Runner.Absorb(out int fuel, out _);
            Tank += fuel;
        }
    }

    [Fact]
    public void What_a_player_pours_into_a_tank_they_do_not_run_lands_in_it_once()
    {
        var pair = new Pair();
        int level = pair.Tank;
        int oil = 0;
        pair.Runner.TakeOver(ref level, ref oil);
        pair.RunnerSays();

        pair.Watcher.Pour(1500, 0);
        Assert.True(pair.Watcher.Shown(out int shown, out _));
        Assert.Equal(3900, shown);

        pair.HostCounts();
        pair.RunnerTakesIn();
        pair.RunnerTakesIn();
        pair.RunnerSays();

        Assert.Equal(3900, pair.Tank);
        Assert.True(pair.Watcher.Shown(out shown, out _));
        Assert.Equal(3900, shown);
    }

    [Fact]
    public void A_word_that_does_not_have_the_pouring_yet_does_not_take_it_away_from_whoever_poured()
    {
        var pair = new Pair();
        int level = pair.Tank;
        int oil = 0;
        pair.Runner.TakeOver(ref level, ref oil);
        pair.Watcher.Pour(1000, 0);

        // The runner speaks before it hears of the pouring: the van burnt a little, nothing poured in.
        pair.Tank -= 5;
        pair.RunnerSays();
        Assert.True(pair.Watcher.Shown(out int shown, out _));
        Assert.Equal(3395, shown);
    }

    [Fact]
    public void Whoever_takes_the_vehicle_over_starts_from_the_last_word_and_what_was_poured_since()
    {
        var pair = new Pair();
        int level = pair.Tank;
        int oil = 0;
        pair.Runner.TakeOver(ref level, ref oil);
        pair.Tank -= 400;
        pair.RunnerSays();

        // Poured while nobody took it in yet, then the watcher takes the wheel.
        pair.Watcher.Pour(700, 0);
        int mine = 1234;
        int mineOil = 0;
        pair.Watcher.TakeOver(ref mine, ref mineOil);

        Assert.Equal(2700, mine);
        pair.Watcher.Absorb(out int more, out _);
        Assert.Equal(0, more);
    }

    [Fact]
    public void Nobody_said_anything_yet_and_the_tank_is_as_this_game_has_it()
    {
        var ledger = new FuelTankLedger();
        int level = 1800;
        int oil = 300;
        ledger.TakeOver(ref level, ref oil);

        Assert.Equal(1800, level);
        Assert.Equal(300, oil);
        Assert.False(ledger.Shown(out _, out _));
    }

    [Fact]
    public void An_older_count_of_the_pouring_is_not_news()
    {
        var ledger = new FuelTankLedger();
        ledger.Pour(500, 100);
        ledger.HeardAdded(300, 0);

        Assert.Equal(500, ledger.AddedFuel);
        Assert.Equal(100, ledger.AddedOil);
        ledger.HeardAdded(900, 150);
        Assert.Equal(900, ledger.AddedFuel);
        Assert.Equal(150, ledger.AddedOil);
    }

    [Fact]
    public void Fuel_oil_is_kept_apart_from_fuel()
    {
        var ledger = new FuelTankLedger();
        int level = 1000;
        int oil = 0;
        ledger.TakeOver(ref level, ref oil);
        ledger.HeardAdded(0, 250);
        ledger.Absorb(out int fuel, out int fuelOil);

        Assert.Equal(0, fuel);
        Assert.Equal(250, fuelOil);
        Assert.Equal(new[] { 1000, 250, 0, 250 }, ledger.Word(1000, 250));
    }

    [Fact]
    public void A_bad_word_is_left_out()
    {
        var ledger = new FuelTankLedger();
        ledger.Heard(new[] { 1, 2 });
        ledger.Heard(null!);

        Assert.False(ledger.HasWord);
        Assert.Equal(new[] { 0, 0 }, ledger.Pour(-5, -5));
        Assert.Equal(0, ledger.AddedFuel);
    }
}
