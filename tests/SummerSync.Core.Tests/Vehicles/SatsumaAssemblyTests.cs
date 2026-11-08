using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using Xunit;

namespace SummerSync.Core.Tests.Vehicles;

public class SatsumaAssemblyTests
{
    private static SatsumaCatalog Parts()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "data", "satsuma", "parts.json");
        return SatsumaCatalog.Parse(File.ReadAllText(file));
    }

    [Fact]
    public void Build_lists_every_entry_in_the_catalogue_order_with_its_bolts()
    {
        SatsumaCatalog parts = Parts();
        AssemblyCatalog catalog = SatsumaAssembly.Build(parts, null, null);

        Assert.Equal(parts.Entries.Length, catalog.Count);
        Assert.Equal(parts.BoltCount, catalog.TotalBolts);
        for (int i = 0; i < parts.Entries.Length; i++)
        {
            Assert.Equal(parts.Entries[i].Name, catalog.Name(i));
            Assert.Equal(parts.Entries[i].Bolts.Length, catalog.Bolts(i));
        }

        Assert.True(catalog.Frozen);
    }

    [Fact]
    public void Two_builds_from_the_same_file_have_the_same_shape_and_numbers_change_it()
    {
        uint first = SatsumaAssembly.Build(Parts(), null, null).Shape;
        uint second = SatsumaAssembly.Build(Parts(), null, null).Shape;
        uint withValues = SatsumaAssembly.Build(Parts(), new[] { "DatabaseMotor/Oilpan.Oil" }, new[] { 0.05f }).Shape;

        Assert.Equal(first, second);
        Assert.NotEqual(first, withValues);
    }

    [Fact]
    public void Build_appends_the_numbers_after_the_entries()
    {
        AssemblyCatalog catalog = SatsumaAssembly.Build(Parts(), new[] { "a", "b" }, new[] { 0.1f, 1f });

        Assert.Equal(2, catalog.ValueCount);
        Assert.Equal("b", catalog.ValueName(1));
        Assert.Equal(1f, catalog.ValueStep(1));
        Assert.Throws<ArgumentException>(() => SatsumaAssembly.Build(Parts(), new[] { "a" }, new float[0]));
    }

    [Fact]
    public void Whatever_a_trigger_needs_installed_goes_in_first()
    {
        SatsumaCatalog parts = Parts();
        int[] ranks = SatsumaAssembly.Ranks(parts);
        int checkedNeeds = 0;
        foreach (PartEntry entry in parts.Entries)
        {
            foreach (PartTrigger trigger in entry.Triggers)
            {
                foreach (PartRequirement need in trigger.Requires)
                {
                    PartEntry other = parts.Find(need.Entry)!;
                    if (need.Value && other.Index != entry.Index)
                    {
                        Assert.True(ranks[entry.Index] > ranks[other.Index], entry.Name + " should go in after " + other.Name);
                        checkedNeeds++;
                    }
                }

                if (trigger.Place.Kind == PartPlaceKind.Part && trigger.Place.Entry != entry.Name)
                {
                    PartEntry holder = parts.Find(trigger.Place.Entry)!;
                    Assert.True(ranks[entry.Index] > ranks[holder.Index], entry.Name + " sits on " + holder.Name);
                    checkedNeeds++;
                }
            }
        }

        Assert.True(checkedNeeds > 50);
        Assert.True(ranks[parts.Find("DatabaseBody/Wishbone_FL")!.Index] > ranks[parts.Find("DatabaseBody/Subframe")!.Index]);
        Assert.True(ranks[parts.Find("DatabaseMechanics/Radio")!.Index] > ranks[parts.Find("DatabaseMechanics/Dashboard")!.Index]);
    }

    [Fact]
    public void The_hoist_hook_is_a_tool_whose_two_bolts_turn_on_their_own()
    {
        PartEntry hook = Parts().Find("Tools/MotorHoistHook")!;

        Assert.True(SatsumaAssembly.IsTool(hook));
        Assert.False(SatsumaAssembly.InstalledBySteps(hook));
        Assert.Equal(2, hook.Bolts.Length);
        Assert.Equal(AssemblyEntryFlags.BoltsWhileLoose, SatsumaAssembly.FlagsOf(hook));
        Assert.Equal(PartPlaceKind.Scene, hook.Bolts[0].Place.Kind);
        Assert.False(SatsumaAssembly.IsTool(Parts().Find("DatabaseMotor/Block")!));
    }

    [Fact]
    public void What_a_trigger_needs_off_goes_in_after()
    {
        SatsumaCatalog parts = Parts();
        int[] ranks = SatsumaAssembly.Ranks(parts);
        int Rank(string name) => ranks[parts.Find(name)!.Index];

        Assert.True(Rank("DatabaseMotor/Cylinderhead") > Rank("DatabaseMotor/Headgasket"));
        Assert.True(Rank("DatabaseMotor/Cylinderhead") > Rank("DatabaseMotor/Piston1"));
        Assert.True(Rank("DatabaseMotor/CrankBearing1") > Rank("DatabaseMotor/Crankshaft"));
        Assert.True(Rank("DatabaseMotor/Oilpan") > Rank("DatabaseMotor/CrankBearing3"));
        Assert.True(Rank("DatabaseMotor/Timingcover") > Rank("DatabaseMotor/Timingchain"));

        // And never against what a trigger needs installed.
        int kept = 0;
        foreach (PartEntry entry in parts.Entries)
        {
            foreach (PartTrigger trigger in entry.Triggers)
            {
                foreach (PartRequirement need in trigger.Requires)
                {
                    PartEntry other = parts.Find(need.Entry)!;
                    if (!need.Value && other.Index != entry.Index && ranks[other.Index] > ranks[entry.Index])
                    {
                        kept++;
                    }
                }
            }
        }

        Assert.True(kept >= 40, kept + " of the parts asked not to be installed yet go in after");
    }

    [Theory]
    [InlineData("DatabaseMotor/Block", AssemblyEntryFlags.BoltsWhileLoose)]
    [InlineData("DatabaseBody/Door_Left", AssemblyEntryFlags.ClearsBoltsOnRemove)]
    [InlineData("DatabaseMechanics/Dashboard", AssemblyEntryFlags.ClearsBoltsOnRemove)]
    [InlineData("DatabaseBody/Grille", AssemblyEntryFlags.None)]
    [InlineData("DatabaseBody/Subframe", AssemblyEntryFlags.None)]
    [InlineData("DatabaseWiring/WiringStarter", AssemblyEntryFlags.BoltsWhileLoose)]
    [InlineData("DatabaseMotor/FuelLine", AssemblyEntryFlags.BoltsWhileLoose)]
    [InlineData("Wheels/FL", AssemblyEntryFlags.None)]
    public void Flags_follow_what_the_game_does_with_the_bolts(string name, AssemblyEntryFlags expected)
    {
        Assert.Equal(expected, SatsumaAssembly.FlagsOf(Parts().Find(name)!));
    }

    [Fact]
    public void Wheel_slots_know_their_corner()
    {
        SatsumaCatalog parts = Parts();

        Assert.Equal(0, SatsumaAssembly.CornerOf(parts.Find("Wheels/FL")));
        Assert.Equal(3, SatsumaAssembly.CornerOf(parts.Find("Wheels/RR")));
        Assert.Equal(-1, SatsumaAssembly.CornerOf(parts.Find("DatabaseBody/Door_Left")));
        Assert.True(SatsumaAssembly.InstalledBySteps(parts.Find("Wheels/RL")!));
        Assert.False(SatsumaAssembly.InstalledBySteps(parts.Find("DatabaseMotor/Block")!));
    }
}
