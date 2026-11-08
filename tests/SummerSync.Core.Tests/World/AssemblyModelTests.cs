using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class AssemblyModelTests
{
    // A few entries the way the Satsuma lists them: the subframe goes in before the wishbones, the block bolts on
    // while it hangs from the hoist, the doors lose their bolts when they come off.
    internal static AssemblyCatalog Satsuma()
    {
        var catalog = new AssemblyCatalog();
        catalog.Add("Subframe", 4, rank: 0);
        catalog.Add("Wishbone_FL", 2, rank: 1);
        catalog.Add("Wishbone_FR", 2, rank: 1);
        catalog.Add("Door_Left", 4, rank: 0, entryFlags: AssemblyEntryFlags.ClearsBoltsOnRemove);
        catalog.Add("Door_Right", 4, rank: 0, entryFlags: AssemblyEntryFlags.ClearsBoltsOnRemove);
        catalog.Add("Block", 3, rank: 2, entryFlags: AssemblyEntryFlags.BoltsWhileLoose);
        catalog.Add("WiringStarter", 0, rank: 3);
        catalog.Add("Hood", 4, rank: 0);
        catalog.Add("Spindle_FL", new byte[] { 8, 6 }, 2, AssemblyEntryFlags.None);
        catalog.AddValue("Oilpan.Oil", 0.01f);
        catalog.AddValue("Radiator.Water", 0.01f);
        catalog.AddValue("Battery.Charge", 0.5f);
        return catalog;
    }

    internal const int Subframe = 0;
    internal const int WishboneFL = 1;
    internal const int WishboneFR = 2;
    internal const int DoorLeft = 3;
    internal const int DoorRight = 4;
    internal const int Block = 5;
    internal const int Wiring = 6;
    internal const int Hood = 7;
    internal const int Spindle = 8;

    [Fact]
    public void Each_entry_says_what_can_happen_to_it()
    {
        var car = new AssemblyModel(Satsuma());

        Assert.True(car.Allows(AssemblyStep.Install(Subframe, 0, 12)));
        Assert.False(car.Allows(AssemblyStep.Remove(Subframe)));
        Assert.False(car.Allows(AssemblyStep.Tighten(Subframe, 0)));
        Assert.True(car.Allows(AssemblyStep.Tighten(Block, 2)));
        Assert.False(car.Allows(AssemblyStep.Loosen(Block, 2)));
        Assert.True(car.Allows(AssemblyStep.Wire(Wiring, true)));
        Assert.False(car.Allows(AssemblyStep.Wire(Wiring, false)));
        Assert.True(car.Allows(AssemblyStep.Paint(Hood, 2, 0x112233ff)));
        Assert.True(car.Allows(AssemblyStep.Outcome(Hood, AssemblyWire.OutcomeDamaged)));

        car.Apply(AssemblyStep.Install(Subframe, 0, 12));
        Assert.False(car.Allows(AssemblyStep.Install(Subframe, 0, 13)));
        Assert.True(car.Allows(AssemblyStep.Remove(Subframe)));
        for (int i = 0; i < 8; i++)
        {
            Assert.True(car.Allows(AssemblyStep.Tighten(Subframe, 3)));
            car.Apply(AssemblyStep.Tighten(Subframe, 3));
        }

        Assert.False(car.Allows(AssemblyStep.Tighten(Subframe, 3)));
        Assert.Equal(8, car.Stage(Subframe, 3));
        Assert.Equal(8, car.Tightness(Subframe));

        // A bolt with fewer stages of its own.
        car.Apply(AssemblyStep.Install(Spindle, 0, 0));
        for (int i = 0; i < 6; i++)
        {
            car.Apply(AssemblyStep.Tighten(Spindle, 1));
        }

        Assert.False(car.Allows(AssemblyStep.Tighten(Spindle, 1)));
        Assert.True(car.Allows(AssemblyStep.Tighten(Spindle, 0)));
    }

    [Fact]
    public void A_step_that_is_not_one_of_the_car_is_not_well_formed()
    {
        var car = new AssemblyModel(Satsuma());

        Assert.False(car.IsWellFormed(AssemblyStep.Install(99, 0, 0)));
        Assert.False(car.IsWellFormed(AssemblyStep.Install(-1, 0, 0)));
        Assert.False(car.IsWellFormed(AssemblyStep.Tighten(Subframe, 4)));
        Assert.False(car.IsWellFormed(AssemblyStep.Turn(Subframe, 0, 2)));
        Assert.False(car.IsWellFormed(AssemblyStep.Turn(Subframe, 0, 0)));
        Assert.False(car.IsWellFormed(AssemblyStep.Tighten(Wiring, 0)));
        Assert.False(car.IsWellFormed(AssemblyStep.Outcome(Hood, 0)));
        Assert.False(car.IsWellFormed(new AssemblyStep { Kind = (AssemblyStepKind)42, Entry = Hood }));
        Assert.False(car.Allows(AssemblyStep.Tighten(Subframe, 4)));
        Assert.Throws<ArgumentException>(() => car.Apply(AssemblyStep.Tighten(Subframe, 4)));
    }

    [Fact]
    public void Applying_what_is_not_allowed_does_the_nearest_thing()
    {
        var car = new AssemblyModel(Satsuma());
        car.Apply(AssemblyStep.Install(Hood, 1, 5));
        car.Apply(AssemblyStep.Install(Hood, 2, 6));
        Assert.Equal(2, car.Variant(Hood));
        Assert.Equal(6, car.PartRef(Hood));

        car.Apply(AssemblyStep.Loosen(Hood, 0));
        Assert.Equal(0, car.Stage(Hood, 0));

        car.Apply(AssemblyStep.Outcome(Hood, AssemblyWire.OutcomeDamaged));
        car.Apply(AssemblyStep.Outcome(Hood, AssemblyWire.FirstGameOutcome + 3));
        Assert.True(car.IsDamaged(Hood));
        car.Apply(AssemblyStep.Outcome(Hood, AssemblyWire.OutcomeRepaired));
        Assert.False(car.IsDamaged(Hood));
    }

    [Fact]
    public void What_comes_off_keeps_its_bolts_unless_the_game_clears_them()
    {
        var car = new AssemblyModel(Satsuma());
        foreach (int entry in new[] { Hood, DoorLeft })
        {
            car.Apply(AssemblyStep.Install(entry, 0, 0));
            car.Apply(AssemblyStep.Tighten(entry, 1));
            car.Apply(AssemblyStep.Tighten(entry, 1));
            car.Apply(AssemblyStep.Remove(entry));
        }

        Assert.Equal(2, car.Stage(Hood, 1));
        Assert.Equal(0, car.Stage(DoorLeft, 1));
    }

    [Fact]
    public void The_digest_is_the_same_for_the_same_car_and_changes_with_everything_but_the_parts()
    {
        var a = new AssemblyModel(Satsuma());
        var b = new AssemblyModel(Satsuma());
        ulong empty = a.Digest();
        Assert.Equal(empty, b.Digest());

        a.Apply(AssemblyStep.Install(Hood, 0, 5));
        b.Apply(AssemblyStep.Install(Hood, 0, 0));
        Assert.Equal(a.Digest(), b.Digest());
        Assert.NotEqual(empty, a.Digest());
        Assert.False(a.SameAs(a.Clone().Also(car => car.Apply(AssemblyStep.Install(Hood, 0, 9)))));

        var digests = new HashSet<ulong> { empty, a.Digest() };
        AssemblyStep[] changes =
        {
            AssemblyStep.Install(Hood, 1, 5),
            AssemblyStep.Tighten(Hood, 3),
            AssemblyStep.Tighten(Block, 0),
            AssemblyStep.Wire(Wiring, true),
            AssemblyStep.Paint(Hood, 1, 0x00ff00ff),
            AssemblyStep.Paint(Hood, 2, 0x00ff00ff),
            AssemblyStep.Paint(Hood, 2, 0x0000ffff),
            AssemblyStep.Outcome(Hood, AssemblyWire.OutcomeDamaged),
        };
        foreach (AssemblyStep change in changes)
        {
            a.Apply(change);
            Assert.True(digests.Add(a.Digest()), "No change of the digest after " + change);
        }
    }

    [Fact]
    public void The_catalogue_s_shape_changes_with_anything_listed()
    {
        uint shape = Satsuma().Shape;
        Assert.Equal(shape, Satsuma().Shape);

        var shapes = new HashSet<uint> { shape };
        var named = Satsuma();
        named.Add("Trunk", 4);
        Assert.True(shapes.Add(named.Shape));
        var bolts = new AssemblyCatalog();
        bolts.Add("Subframe", 5);
        Assert.True(shapes.Add(bolts.Shape));
        var stages = new AssemblyCatalog();
        stages.Add("Subframe", 4, maxStage: 6);
        Assert.True(shapes.Add(stages.Shape));
        var ranked = new AssemblyCatalog();
        ranked.Add("Subframe", 4, rank: 1);
        Assert.True(shapes.Add(ranked.Shape));
        var flagged = new AssemblyCatalog();
        flagged.Add("Subframe", 4, entryFlags: AssemblyEntryFlags.BoltsWhileLoose);
        Assert.True(shapes.Add(flagged.Shape));
        var valued = new AssemblyCatalog();
        valued.Add("Subframe", 4);
        valued.AddValue("Oil", 1f);
        Assert.True(shapes.Add(valued.Shape));
    }

    [Fact]
    public void A_catalogue_in_use_cannot_change()
    {
        AssemblyCatalog catalog = Satsuma();
        Assert.Equal(DoorLeft, catalog.Find("Door_Left"));
        Assert.Equal(-1, catalog.Find("Door_Middle"));
        Assert.Equal(1, catalog.FindValue("Radiator.Water"));
        Assert.Throws<ArgumentException>(() => catalog.Add("Hood", 2));
        _ = new AssemblyModel(catalog);

        Assert.True(catalog.Frozen);
        Assert.Throws<InvalidOperationException>(() => catalog.Add("Trunk", 4));
        Assert.Throws<InvalidOperationException>(() => catalog.AddValue("Fuel", 1f));
    }

    [Fact]
    public void Reconcile_takes_one_car_to_another_in_an_order_a_game_can_make()
    {
        AssemblyCatalog catalog = Satsuma();
        var here = new AssemblyModel(catalog);
        here.SetInstalled(Hood, true, 0, 0);
        here.SetStage(Hood, 0, 3);
        here.SetInstalled(Subframe, true);
        here.SetStage(Subframe, 1, 8);
        here.SetWired(Wiring, true);
        here.SetInstalled(DoorLeft, true, 0, 41);

        var there = new AssemblyModel(catalog);
        there.SetInstalled(Subframe, true);
        there.SetStage(Subframe, 1, 6);
        there.SetStage(Subframe, 2, 2);
        there.SetInstalled(WishboneFL, true, 0, 7);
        there.SetStage(WishboneFL, 0, 1);
        there.SetInstalled(DoorLeft, true, 0, 42);
        there.SetStage(Block, 0, 1);
        there.SetPaint(Hood, 3, 0x10203040);
        there.SetDamaged(Subframe, true);

        List<AssemblyStep> steps = AssemblyModel.Reconcile(here, there);

        AssemblyStep[] expected =
        {
            // Bolts down first, the deepest parts first: the hood's to nothing, as it comes off.
            AssemblyStep.Loosen(Hood, 0),
            AssemblyStep.Loosen(Hood, 0),
            AssemblyStep.Loosen(Hood, 0),
            AssemblyStep.Loosen(Subframe, 1),
            AssemblyStep.Loosen(Subframe, 1),
            AssemblyStep.Wire(Wiring, false),

            // Off from the top down: another door is the one in that slot.
            AssemblyStep.Remove(Hood),
            AssemblyStep.Remove(DoorLeft),

            // On from the bottom up.
            AssemblyStep.Install(DoorLeft, 0, 42),
            AssemblyStep.Install(WishboneFL, 0, 7),

            AssemblyStep.Tighten(Subframe, 2),
            AssemblyStep.Tighten(Subframe, 2),
            AssemblyStep.Tighten(WishboneFL, 0),
            AssemblyStep.Tighten(Block, 0),

            // Paint goes on a part that is off too; damage last.
            AssemblyStep.Paint(Hood, 3, 0x10203040),
            AssemblyStep.Outcome(Subframe, AssemblyWire.OutcomeDamaged),
        };
        Assert.Equal(expected, steps);
        Assert.All(steps, step => Assert.Equal(AssemblyWire.NoPlayer, step.Player));

        // Made in a game showing "here", they leave it as "there".
        AssemblyModel game = here.Clone();
        foreach (AssemblyStep step in steps)
        {
            game.Apply(step);
        }

        Assert.Equal(there.Digest(), game.Digest());
        Assert.Empty(AssemblyModel.Reconcile(there, there));
    }

    [Fact]
    public void Reconcile_does_not_swap_a_part_whose_reference_one_side_does_not_know()
    {
        AssemblyCatalog catalog = Satsuma();
        var read = new AssemblyModel(catalog);
        read.SetInstalled(Hood, true, 0, 0);
        var told = new AssemblyModel(catalog);
        told.SetInstalled(Hood, true, 0, 77);

        Assert.Empty(AssemblyModel.Reconcile(read, told));
        Assert.Empty(AssemblyModel.Reconcile(told, read));

        told.SetInstalled(Hood, true, 1, 77);
        Assert.Equal(new[] { AssemblyStep.Remove(Hood), AssemblyStep.Install(Hood, 1, 77) }, AssemblyModel.Reconcile(read, told));
    }

    [Fact]
    public void Reconcile_leaves_out_what_a_game_cannot_do()
    {
        AssemblyCatalog catalog = Satsuma();
        var here = new AssemblyModel(catalog);
        here.SetPaint(Hood, 1, 5);
        var there = new AssemblyModel(catalog);
        there.SetStage(Hood, 2, 4);

        // Paint does not come off, and the bolts of a part that stays off do not turn.
        Assert.Empty(AssemblyModel.Reconcile(here, there));
        Assert.Contains("Hood (paint)", here.DescribeDifferences(there, 3));
    }

    [Fact]
    public void A_part_that_comes_off_keeps_the_stages_of_the_host_s_car()
    {
        AssemblyCatalog catalog = Satsuma();
        var here = new AssemblyModel(catalog);
        here.SetInstalled(Hood, true);
        here.SetStage(Hood, 0, 1);
        var there = new AssemblyModel(catalog);
        there.SetStage(Hood, 0, 3);

        // The host took it off with three stages on that bolt: the game turns it there before it comes off.
        Assert.Equal(
            new[] { AssemblyStep.Tighten(Hood, 0), AssemblyStep.Tighten(Hood, 0), AssemblyStep.Remove(Hood) },
            AssemblyModel.Reconcile(here, there));
    }
}

internal static class AssemblyModelTestExtensions
{
    public static AssemblyModel Also(this AssemblyModel car, Action<AssemblyModel> change)
    {
        change(car);
        return car;
    }
}
