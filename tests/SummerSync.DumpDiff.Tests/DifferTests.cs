using System.Text.RegularExpressions;
using SummerSync.Core.Dump;

namespace SummerSync.DumpDiff.Tests;

public class DifferTests
{
    private static List<Change> Diff(DumpBuilder before, DumpBuilder after, Action<DiffOptions>? configure = null)
    {
        var options = new DiffOptions();
        configure?.Invoke(options);
        return new Differ(options).Compare(before.Kind(DumpFormat.KindState).Build(), after.Kind(DumpFormat.KindState).Build());
    }

    [Fact]
    public void Identical_dumps_have_no_changes()
    {
        DumpBuilder Make() => new DumpBuilder()
            .Global("float", "Money", 10.0)
            .Fsm("Car/Engine", "Fuel", "Idle", f => f.Var("float", "Level", 5.0))
            .Body("Box", position: [1, 2, 3])
            .Vehicle("Car", drivetrain: new() { ["rpm"] = 800.0 })
            .Collection("Parts", "ArrayList", "bolts", new object[] { 1, 2 });

        Assert.Empty(Diff(Make(), Make()));
    }

    [Fact]
    public void Reports_state_and_variable_changes_of_an_fsm()
    {
        List<Change> changes = Diff(
            new DumpBuilder().Fsm("Car/Engine", "Fuel", "Idle", f => f.Var("float", "Level", 5.0).Var("bool", "Running", false)),
            new DumpBuilder().Fsm("Car/Engine", "Fuel", "Burn", f => f.Var("float", "Level", 4.2).Var("bool", "Running", true)));

        Assert.Equal(3, changes.Count);
        Assert.All(changes, change => Assert.Equal(("Car/Engine", "Fuel", Section.Fsms), (change.Object, change.Scope, change.Section)));
        Assert.Contains(changes, c => c is { What: "state", Before: "Idle", After: "Burn" });
        Assert.Contains(changes, c => c is { What: "float Level", Before: "5", After: "4.2" });
        Assert.Contains(changes, c => c is { What: "bool Running", Before: "false", After: "true" });
    }

    [Fact]
    public void Small_float_differences_are_within_tolerance()
    {
        DumpBuilder before = new DumpBuilder().Fsm("A", "F", configure: f => f.Var("float", "x", 100.0).Var("vector3", "p", new double[] { 1, 2, 3 }));
        DumpBuilder after = new DumpBuilder().Fsm("A", "F", configure: f => f.Var("float", "x", 100.05).Var("vector3", "p", new double[] { 1.0005, 2, 3 }));

        Assert.Empty(Diff(before, after));
        Assert.Equal(2, Diff(before, after, o => { o.Absolute = 0.0001; o.Relative = 0; }).Count);
    }

    [Fact]
    public void Variables_that_appear_or_disappear_are_reported()
    {
        List<Change> changes = Diff(
            new DumpBuilder().Fsm("A", "F", configure: f => f.Var("int", "old", 1)),
            new DumpBuilder().Fsm("A", "F", configure: f => f.Var("int", "fresh", 2)));

        Assert.Contains(changes, c => c is { What: "int old", Before: "1", After: null });
        Assert.Contains(changes, c => c is { What: "int fresh", Before: null, After: "2" });
    }

    [Fact]
    public void Added_and_removed_fsms_are_reported_once()
    {
        List<Change> changes = Diff(
            new DumpBuilder().Fsm("A", "Gone", "S1", f => f.Var("float", "x", 1.0)),
            new DumpBuilder().Fsm("B", "New", "S2", f => f.Var("float", "y", 2.0)));

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c is { Object: "A", Scope: "Gone", What: "FSM removed" });
        Assert.Contains(changes, c => c is { Object: "B", Scope: "New", What: "FSM added" });
    }

    [Fact]
    public void Flags_of_an_fsm_are_compared()
    {
        List<Change> changes = Diff(
            new DumpBuilder().Fsm("A", "F", configure: f => f.Flags(isEnabled: true, isActive: true, isInitialized: false)),
            new DumpBuilder().Fsm("A", "F", configure: f => f.Flags(isEnabled: false, isActive: false, isInitialized: true)));

        Assert.Equal(new[] { "enabled", "initialized", "object active" }, changes.Select(c => c.What).Order());
    }

    [Fact]
    public void Timer_like_variables_are_marked_as_noise_by_default()
    {
        DumpBuilder before = new DumpBuilder().Global("float", "GlobalTimer", 1.0).Fsm("A", "F", configure: f => f.Var("float", "WaitTime", 1.0).Var("float", "Fuel", 5.0));
        DumpBuilder after = new DumpBuilder().Global("float", "GlobalTimer", 2.0).Fsm("A", "F", configure: f => f.Var("float", "WaitTime", 2.0).Var("float", "Fuel", 4.0));

        List<Change> changes = Diff(before, after);
        Assert.Equal(new[] { "float GlobalTimer", "float WaitTime" }, changes.Where(c => c.Noise).Select(c => c.What).Order());
        Assert.Equal("float Fuel", Assert.Single(changes, c => !c.Noise).What);

        Assert.DoesNotContain(Diff(before, after, o => o.UseDefaultNoise = false), c => c.Noise);
    }

    [Fact]
    public void User_noise_patterns_match_the_full_key()
    {
        DumpBuilder before = new DumpBuilder().Fsm("Car/Radio", "Tuner", "A").Fsm("Car/Engine", "Fuel", "A");
        DumpBuilder after = new DumpBuilder().Fsm("Car/Radio", "Tuner", "B").Fsm("Car/Engine", "Fuel", "B");

        List<Change> changes = Diff(before, after, o => o.Noise.Add(new Regex("^Car/Radio :: Tuner :: state$")));

        Assert.True(Assert.Single(changes, c => c.Object == "Car/Radio").Noise);
        Assert.False(Assert.Single(changes, c => c.Object == "Car/Engine").Noise);
    }

    [Fact]
    public void Path_filter_and_section_filter_narrow_the_comparison()
    {
        DumpBuilder before = new DumpBuilder().Global("int", "g", 1).Fsm("Car/Engine", "F", "A").Fsm("House/Door", "F", "A").Body("Car/Wheel", position: [0, 0, 0]);
        DumpBuilder after = new DumpBuilder().Global("int", "g", 2).Fsm("Car/Engine", "F", "B").Fsm("House/Door", "F", "B").Body("Car/Wheel", position: [5, 0, 0]);

        List<Change> byPath = Diff(before, after, o => o.PathFilter = new Regex("^Car/"));
        Assert.Equal(new[] { "Car/Engine", "Car/Wheel" }, byPath.Select(c => c.Object).Order());

        List<Change> bySection = Diff(before, after, o => { o.Sections.Clear(); o.Sections.Add(Section.Globals); });
        Assert.Equal("int g", Assert.Single(bySection).What);
    }

    [Fact]
    public void Rigidbody_movement_is_measured_against_thresholds()
    {
        List<Change> still = Diff(
            new DumpBuilder().Body("Box", position: [1, 1, 1], velocity: [0, 0, 0]),
            new DumpBuilder().Body("Box", position: [1.005, 1, 1], velocity: [0.01, 0, 0]));
        Assert.Empty(still);

        List<Change> moved = Diff(
            new DumpBuilder().Body("Box", position: [0, 0, 0], sleeping: true),
            new DumpBuilder().Body("Box", position: [3, 4, 0], velocity: [1, 0, 0], sleeping: false));

        Assert.Contains(moved, c => c.What == "moved 5 m");
        Assert.Contains(moved, c => c is { What: "sleeping", Before: "true", After: "false" });
        Assert.Contains(moved, c => c.What == "velocity");
    }

    [Fact]
    public void A_quaternion_and_its_negation_are_the_same_rotation()
    {
        Assert.Empty(Diff(
            new DumpBuilder().Body("Box", rotation: [0, 0.7071068, 0, 0.7071068]),
            new DumpBuilder().Body("Box", rotation: [0, -0.7071068, 0, -0.7071068])));

        Change turned = Assert.Single(Diff(
            new DumpBuilder().Body("Box", rotation: [0, 0, 0, 1]),
            new DumpBuilder().Body("Box", rotation: [0, 0.7071068, 0, 0.7071068])));
        Assert.Equal("turned 90 deg", turned.What);
    }

    [Fact]
    public void Vehicle_drivetrain_and_body_are_compared()
    {
        List<Change> changes = Diff(
            new DumpBuilder().Vehicle("SATSUMA", position: [0, 0, 0], drivetrain: new() { ["rpm"] = 800.0, ["gear"] = 1 }),
            new DumpBuilder().Vehicle("SATSUMA", position: [0, 0, 12], drivetrain: new() { ["rpm"] = 3200.0, ["gear"] = 3 }));

        Assert.All(changes, c => Assert.Equal(Section.Vehicles, c.Section));
        Assert.Contains(changes, c => c is { Scope: "Drivetrain", What: "rpm", Before: "800", After: "3200" });
        Assert.Contains(changes, c => c is { Scope: "Drivetrain", What: "gear", Before: "1", After: "3" });
        Assert.Contains(changes, c => c.What == "moved 12 m");
    }

    [Fact]
    public void Collection_items_are_compared()
    {
        Change change = Assert.Single(Diff(
            new DumpBuilder().Collection("Parts", "ArrayList", "bolts", new object[] { "a", "b" }),
            new DumpBuilder().Collection("Parts", "ArrayList", "bolts", new object[] { "a", "c" })));

        Assert.Equal((Section.Collections, "Parts", "ArrayList 'bolts'", "items"), (change.Section, change.Object, change.Scope, change.What));
    }

    [Fact]
    public void Report_groups_by_object_and_fsm_and_hides_noise()
    {
        DumpFile before = new DumpBuilder().At(10, 600).Kind(DumpFormat.KindState)
            .Fsm("Car/Engine", "Fuel", "Idle", f => f.Var("float", "Level", 5.0).Var("float", "Timer", 1.0)).Build();
        DumpFile after = new DumpBuilder().At(25.5, 1530).Kind(DumpFormat.KindState)
            .Fsm("Car/Engine", "Fuel", "Burn", f => f.Var("float", "Level", 4.0).Var("float", "Timer", 9.0)).Build();
        List<Change> changes = new Differ(new DiffOptions()).Compare(before, after);

        string report = DiffReport.Render(before, after, changes, showNoise: false, maxPerObject: 40);

        Assert.Contains("15.5 s of game time, 930 frames", report);
        Assert.Contains("== FSMs ==", report);
        Assert.Contains("Car/Engine" + Environment.NewLine + "  [Fuel]" + Environment.NewLine + "    state: Idle -> Burn", report);
        Assert.Contains("    float Level: 5 -> 4", report);
        Assert.DoesNotContain("Timer", report);
        Assert.Contains("Hidden as noise: 1 change(s)", report);

        Assert.Contains("float Timer: 1 -> 9  (noise)", DiffReport.Render(before, after, changes, showNoise: true, maxPerObject: 40));
    }

    [Fact]
    public void Report_caps_the_lines_per_fsm()
    {
        DumpBuilder Make(double value) => new DumpBuilder().Kind(DumpFormat.KindState)
            .Fsm("A", "F", configure: f => { for (int i = 0; i < 10; i++) { f.Var("float", "v" + i, value); } });
        DumpFile before = Make(1).Build();
        DumpFile after = Make(2).Build();

        string report = DiffReport.Render(before, after, new Differ(new DiffOptions()).Compare(before, after), showNoise: false, maxPerObject: 3);

        Assert.Contains("... 7 more", report);
    }
}

public class EntryMatcherTests
{
    private static BodyEntry Body(string path, int id, string indexPath) => new() { Path = path, Id = id, IndexPath = indexPath };

    private static List<EntryPair<BodyEntry>> Pair(BodyEntry[] before, BodyEntry[] after) =>
        EntryMatcher.Pair(before, after, b => b.Path, b => b.IndexPath);

    [Fact]
    public void Unique_paths_match_even_if_id_and_position_changed()
    {
        EntryPair<BodyEntry> pair = Assert.Single(Pair([Body("A", 1, "0")], [Body("A", 99, "7")]));

        Assert.Equal(1, pair.Before!.Id);
        Assert.Equal(99, pair.After!.Id);
    }

    [Fact]
    public void Duplicates_are_matched_by_instance_id_first()
    {
        // Same session: the first sibling was destroyed, so every position shifted but ids are stable.
        List<EntryPair<BodyEntry>> pairs = Pair(
            [Body("Bolt", 10, "0/0"), Body("Bolt", 11, "0/1"), Body("Bolt", 12, "0/2")],
            [Body("Bolt", 11, "0/0"), Body("Bolt", 12, "0/1")]);

        Assert.Equal(3, pairs.Count);
        Assert.Contains(pairs, p => p.Before?.Id == 11 && p.After?.Id == 11);
        Assert.Contains(pairs, p => p.Before?.Id == 12 && p.After?.Id == 12);
        Assert.Contains(pairs, p => p.Before?.Id == 10 && p.After == null);
    }

    [Fact]
    public void Duplicates_fall_back_to_hierarchy_position_across_sessions()
    {
        List<EntryPair<BodyEntry>> pairs = Pair(
            [Body("Bolt", 10, "0/0"), Body("Bolt", 11, "0/1")],
            [Body("Bolt", 501, "0/1"), Body("Bolt", 500, "0/0"), Body("Bolt", 502, "0/2")]);

        Assert.Equal(3, pairs.Count);
        Assert.Contains(pairs, p => p.Before?.Id == 10 && p.After?.Id == 500);
        Assert.Contains(pairs, p => p.Before?.Id == 11 && p.After?.Id == 501);
        Assert.Contains(pairs, p => p.Before == null && p.After?.Id == 502);
    }

    [Fact]
    public void Entries_only_on_one_side_are_kept_as_half_pairs()
    {
        List<EntryPair<BodyEntry>> pairs = Pair([Body("OnlyBefore", 1, "0")], [Body("OnlyAfter", 2, "0")]);

        Assert.Contains(pairs, p => p.Before?.Path == "OnlyBefore" && p.After == null);
        Assert.Contains(pairs, p => p.Before == null && p.After?.Path == "OnlyAfter");
    }
}

public class NoiseFinderTests
{
    private static DumpFile Idle(double clock, double fuel, string blinker, double thirst = 0) => new DumpBuilder().Kind(DumpFormat.KindState)
        .Global("float", "Clock", clock)
        .Global("float", "Thirst", thirst)
        .Fsm("Car", "Fuel", configure: f => f.Var("float", "Level", fuel))
        .Fsm("Car", "Blinker", blinker)
        .Build();

    [Fact]
    public void Anything_that_changes_while_idle_is_noise()
    {
        // The clock and the blinker change in both intervals; the fuel only in the first.
        List<string> noise = NoiseFinder.Find(
            [Idle(1, 5.0, "On"), Idle(2, 4.0, "Off"), Idle(3, 4.0, "On")],
            new DiffOptions { UseDefaultNoise = false },
            everyPair: false);

        Assert.Equal(new[] { " ::  :: float Clock", "Car :: Blinker :: state", "Car :: Fuel :: float Level" }, noise);
    }

    [Fact]
    public void Every_pair_mode_keeps_only_what_never_stops_changing()
    {
        List<string> noise = NoiseFinder.Find(
            [Idle(1, 5.0, "On"), Idle(2, 4.0, "Off"), Idle(3, 4.0, "On")],
            new DiffOptions { UseDefaultNoise = false },
            everyPair: true);

        Assert.Equal(new[] { " ::  :: float Clock", "Car :: Blinker :: state" }, noise);
    }

    [Fact]
    public void Slow_drift_below_the_tolerance_between_dumps_is_still_found()
    {
        // Each step is within the tolerance; the total is not.
        var options = new DiffOptions { UseDefaultNoise = false, Absolute = 0.01, Relative = 0 };
        DumpFile[] dumps = [Idle(1, 5, "On", thirst: 10.000), Idle(1, 5, "On", thirst: 10.008), Idle(1, 5, "On", thirst: 10.016)];

        Assert.Empty(NoiseFinder.Find(dumps, options, everyPair: true));
        Assert.Equal(new[] { " ::  :: float Thirst" }, NoiseFinder.Find(dumps, options, everyPair: false));
    }

    [Fact]
    public void Generated_file_round_trips_into_patterns_that_match_exactly()
    {
        string path = Path.Combine(Path.GetTempPath(), "SummerSync.Tests", Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, NoiseFinder.ToFile(["SATSUMA(557kg, 248)/Engine :: Fuel :: float Level"], ["a.json", "b.json"]));

            Regex pattern = Assert.Single(NoiseFinder.Load(path));
            Assert.Matches(pattern, "SATSUMA(557kg, 248)/Engine :: Fuel :: float Level");
            Assert.DoesNotMatch(pattern, "SATSUMA(557kg, 248)/Engine :: Fuel :: float Level2");
            Assert.DoesNotMatch(pattern, "XSATSUMA(557kg, 248)/Engine :: Fuel :: float Level");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
