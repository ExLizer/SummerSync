using SummerSync.Core.Vehicles;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.Vehicles;

public class SatsumaValuesTests
{
    // A small book in the shape of data/satsuma/values.json: a fluid poured by hand, a wear, a setting with a pivot to
    // turn, a tyre found through its hub, and one chance machine of each kind.
    private const string Small = """
        {
          "format": 1,
          "values": [
            { "name": "oil", "entry": "DatabaseMotor/Oilpan", "variable": "Oil", "step": 0.01, "min": 0.01, "max": 3, "local": "any",
              "while": [{ "path": "DatabaseMotor/Cylinderhead:Bolts/CapTrigger_MotorOil", "fsm": "Trigger", "states": ["Pouring"] }] },
            { "name": "wear crankshaft", "path": "CarSimulation/MechanicalWear", "fsm": "Data", "variable": "WearCrankshaft", "step": 0.5,
              "max": 99, "local": "up" },
            { "name": "gearbox damage type", "path": "CarSimulation/MechanicalWear", "fsm": "Data", "variable": "GearboxDamageType",
              "type": "int", "step": 1 },
            { "name": "spark timing", "entry": "DatabaseMotor/Distributor", "variable": "SparkAngle", "step": 0.05, "min": 0, "max": 20,
              "local": "any", "mirror": { "path": "DatabaseMotor/Distributor:Pivot", "axis": "z" } },
            { "name": "tyre FL", "path": "FL/AckerFL/wheelFL", "fsm": "Condition", "follow": { "variable": "ThisTire", "fsm": "Use" },
              "variable": "TireHealth", "step": 0.5, "min": 0, "local": "up" },
            { "name": "piss", "path": "/PLAYER/Piss", "fsm": "Pouring", "variable": "Urine", "step": 1, "note": "from the top of the scene" }
          ],
          "chance": [
            { "name": "random bolts", "path": "CarSimulation/RandomBolt", "fsm": "Logic", "block": "pause" },
            { "name": "mechanical wear", "path": "CarSimulation/MechanicalWear", "fsm": "Data", "block": "hold", "state": "Wear 1", "to": "Wait" },
            { "name": "loose parts", "each": "boltChecks", "fsm": "BoltCheck", "block": "weights", "state": "Chance", "events": ["BREAK"] },
            { "name": "tyre wear FL", "path": "FL/AckerFL/wheelFL", "fsm": "Condition", "block": "force", "variable": "WearRatio", "value": 1000000000 },
            { "name": "misfires", "path": "CarSimulation/Engine/Combustion", "fsm": "Cylinders", "block": "none" }
          ],
          "outcomes": [
            { "code": "puncture", "entry": "Wheels/FL", "path": "FL/AckerFL/wheelFL", "fsm": "Condition", "from": "State 1",
              "into": ["Check rim", "Flat friction"], "event": "PUNCTURE" }
          ]
        }
        """;

    [Fact]
    public void Numbers_keep_the_order_of_the_file_and_what_each_says()
    {
        SatsumaValues book = SatsumaValues.Parse(Small);

        Assert.Equal(new[] { "oil", "wear crankshaft", "gearbox damage type", "spark timing", "tyre FL", "piss" }, book.Values.Select(v => v.Name));
        Assert.Equal(Enumerable.Range(0, 6), book.Values.Select(v => v.Index));

        CarValue oil = book.Find("oil")!;
        Assert.Equal("DatabaseMotor/Oilpan", oil.Entry);
        Assert.Null(oil.Place);
        Assert.Equal("Data", oil.Machine);
        Assert.Equal("Oil", oil.Variable);
        Assert.Equal(CarValueKind.Float, oil.Kind);
        Assert.Equal(0.01f, oil.Step);
        Assert.True(oil.HasMin && oil.HasMax);
        Assert.Equal(CarValueLocal.Any, oil.Local);
        FsmGate gate = Assert.Single(oil.While);
        Assert.Equal(PartPlaceKind.Part, gate.Place.Kind);
        Assert.Equal("DatabaseMotor/Cylinderhead", gate.Place.Entry);
        Assert.Equal("Bolts/CapTrigger_MotorOil", gate.Place.Path);
        Assert.Equal("Trigger", gate.Machine);
        Assert.True(gate.Holds("Pouring"));
        Assert.False(gate.Holds("State 1"));

        CarValue wear = book.Find("wear crankshaft")!;
        Assert.Null(wear.Entry);
        Assert.Equal(PartPlaceKind.Car, wear.Place.Kind);
        Assert.Equal("CarSimulation/MechanicalWear", wear.Place.Path);
        Assert.False(wear.HasMin);
        Assert.Empty(wear.While);

        Assert.Equal(CarValueKind.Int, book.Find("gearbox damage type")!.Kind);
        Assert.Equal(CarValueLocal.None, book.Find("gearbox damage type")!.Local);

        CarValue timing = book.Find("spark timing")!;
        Assert.Equal("DatabaseMotor/Distributor:Pivot", timing.Mirror.Text);
        Assert.Equal(2, timing.MirrorAxis);

        CarValue tyre = book.Find("tyre FL")!;
        Assert.Equal("ThisTire", tyre.FollowVariable);
        Assert.Equal("Use", tyre.FollowMachine);
        Assert.Null(timing.FollowVariable);

        Assert.Equal(PartPlaceKind.Scene, book.Find("piss")!.Place.Kind);
        Assert.Equal("PLAYER/Piss", book.Find("piss")!.Place.Path);
        Assert.Null(book.Find("nothing"));
    }

    [Fact]
    public void A_number_is_held_in_its_range_and_whole_when_it_is_an_int()
    {
        SatsumaValues book = SatsumaValues.Parse(Small);

        CarValue oil = book.Find("oil")!;
        Assert.Equal(3f, oil.Clamp(3.4f));
        Assert.Equal(0.01f, oil.Clamp(-1f));
        Assert.Equal(1.5f, oil.Clamp(1.5f));

        CarValue wear = book.Find("wear crankshaft")!;
        Assert.Equal(99f, wear.Clamp(140f));
        Assert.Equal(-5f, wear.Clamp(-5f));

        Assert.Equal(3f, book.Find("gearbox damage type")!.Clamp(2.6f));
    }

    [Fact]
    public void Which_changes_are_the_local_players_follows_the_book()
    {
        SatsumaValues book = SatsumaValues.Parse(Small);

        // A wear only goes down by itself: up is a repair.
        CarValue wear = book.Find("wear crankshaft")!;
        Assert.True(wear.CountsAsLocal(10f));
        Assert.False(wear.CountsAsLocal(-0.2f));

        // A setting turned by hand: either way.
        CarValue timing = book.Find("spark timing")!;
        Assert.True(timing.CountsAsLocal(0.2f));
        Assert.True(timing.CountsAsLocal(-0.2f));
        Assert.False(timing.CountsAsLocal(0f));

        // Only the game of the machine that runs the car changes it.
        Assert.False(book.Find("gearbox damage type")!.CountsAsLocal(1f));
    }

    [Fact]
    public void Chance_machines_and_outcomes_are_read_with_how_each_is_kept_from_deciding()
    {
        SatsumaValues book = SatsumaValues.Parse(Small);

        Assert.Equal(new[] { ChanceBlock.Pause, ChanceBlock.Hold, ChanceBlock.Weights, ChanceBlock.Force, ChanceBlock.None },
            book.Chances.Select(c => c.Block));
        CarChance hold = book.Chances[1];
        Assert.Equal("Wear 1", hold.State);
        Assert.Equal("Wait", hold.To);
        CarChance loose = book.Chances[2];
        Assert.True(loose.EachBoltCheck);
        Assert.Null(loose.Place);
        Assert.Equal(new[] { "BREAK" }, loose.Events);
        CarChance force = book.Chances[3];
        Assert.Equal("WearRatio", force.Variable);
        Assert.Equal(1e9f, force.Value);

        CarOutcomeWatch puncture = Assert.Single(book.Outcomes);
        Assert.Equal(SatsumaValues.OutcomePuncture, puncture.Code);
        Assert.Equal("Wheels/FL", puncture.Entry);
        Assert.Equal("State 1", puncture.From);
        Assert.Equal(new[] { "Check rim", "Flat friction" }, puncture.Into);
        Assert.Equal("PUNCTURE", puncture.Event);
    }

    [Fact]
    public void The_game_side_outcome_codes_start_where_the_replications_end()
    {
        Assert.True(SatsumaValues.OutcomePuncture >= AssemblyWire.FirstGameOutcome);
        Assert.True(SatsumaValues.OutcomeBrokeOff >= AssemblyWire.FirstGameOutcome);
        Assert.NotEqual(SatsumaValues.OutcomePuncture, SatsumaValues.OutcomeBrokeOff);
        Assert.Equal("puncture", SatsumaValues.DescribeOutcome(SatsumaValues.OutcomePuncture));
        Assert.Equal("broke off", SatsumaValues.DescribeOutcome(SatsumaValues.OutcomeBrokeOff));
        Assert.Equal("damaged", SatsumaValues.DescribeOutcome(AssemblyWire.OutcomeDamaged));
        Assert.Equal("repaired", SatsumaValues.DescribeOutcome(AssemblyWire.OutcomeRepaired));
        Assert.Equal("outcome 40", SatsumaValues.DescribeOutcome(40));
    }

    [Fact]
    public void Numbers_go_into_the_session_catalogue_in_the_file_order_with_their_steps()
    {
        SatsumaValues book = SatsumaValues.Parse(Small);
        var catalog = new AssemblyCatalog();
        catalog.Add("DatabaseMotor/Oilpan", 9);

        book.AddTo(catalog);

        Assert.Equal(book.Values.Length, catalog.ValueCount);
        for (int i = 0; i < book.Values.Length; i++)
        {
            Assert.Equal(book.Values[i].Name, catalog.ValueName(i));
            Assert.Equal(book.Values[i].Step, catalog.ValueStep(i));
            Assert.Equal(i, catalog.FindValue(book.Values[i].Name));
        }

        // A book that names a number otherwise is another shape: the two ends would not read each other's numbers.
        var other = new AssemblyCatalog();
        other.Add("DatabaseMotor/Oilpan", 9);
        SatsumaValues swapped = SatsumaValues.Parse(Small.Replace("\"name\": \"oil\"", "\"name\": \"oil2\""));
        swapped.AddTo(other);
        Assert.NotEqual(catalog.Shape, other.Shape);
    }

    [Theory]
    [InlineData("\"format\": 1", "\"format\": 2", "format")]
    [InlineData("{ \"name\": \"gearbox damage type\"", "{ \"name\": \"oil\"", "two numbers called 'oil'")]
    [InlineData("\"entry\": \"DatabaseMotor/Oilpan\",", "\"entry\": \"DatabaseMotor/Oilpan\", \"path\": \"CarSimulation\",", "not both")]
    [InlineData("\"type\": \"int\"", "\"type\": \"double\"", "type 'double'")]
    [InlineData("\"variable\": \"WearCrankshaft\", \"step\": 0.5", "\"variable\": \"WearCrankshaft\", \"step\": 0", "step")]
    [InlineData("\"min\": 0, \"max\": 20", "\"min\": 21, \"max\": 20", "\"min\" above")]
    [InlineData("\"local\": \"up\" },", "\"local\": \"down\" },", "\"local\" 'down'")]
    [InlineData("\"axis\": \"z\"", "\"axis\": \"w\"", "about 'w'")]
    [InlineData("\"variable\": \"Urine\"", "\"variable\": \"Urine\", \"steps\": 2", "unknown field \"steps\"")]
    [InlineData("\"block\": \"pause\"", "\"block\": \"sleep\"", "\"block\" 'sleep'")]
    [InlineData("\"to\": \"Wait\"", "\"note\": \"no target\"", "sent \"to\"")]
    [InlineData("\"events\": [\"BREAK\"]", "\"events\": []", "\"events\"")]
    [InlineData("\"each\": \"boltChecks\"", "\"each\": \"screws\"", "each 'screws'")]
    [InlineData("\"code\": \"puncture\"", "\"code\": \"explosion\"", "code 'explosion'")]
    [InlineData("\"path\": \"FL/AckerFL/wheelFL\", \"fsm\": \"Condition\", \"follow\"", "\"path\": \"wheel:Use\", \"fsm\": \"Condition\", \"follow\"", "below any wheel")]
    public void A_broken_book_is_refused_with_what_is_wrong(string from, string to, string expected)
    {
        Assert.Contains(from, Small);
        FormatException error = Assert.Throws<FormatException>(() => SatsumaValues.Parse(Small.Replace(from, to)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Gates_need_a_number_whose_local_changes_they_let_through()
    {
        string noLocal = Small.Replace("\"max\": 3, \"local\": \"any\",", "\"max\": 3,");
        Assert.NotEqual(Small, noLocal);
        FormatException error = Assert.Throws<FormatException>(() => SatsumaValues.Parse(noLocal));
        Assert.Contains("gates", error.Message);
    }

    [Fact]
    public void The_book_in_the_repository_reads_and_names_only_what_the_part_catalogue_has()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "satsuma");
        SatsumaValues book = SatsumaValues.Parse(File.ReadAllText(Path.Combine(folder, "values.json")));
        SatsumaCatalog parts = SatsumaCatalog.Parse(File.ReadAllText(Path.Combine(folder, "parts.json")));

        book.Check(parts);
        Assert.InRange(book.Values.Length, 30, AssemblyCatalog.MaxValues);

        // Every number fits a session catalogue next to the parts, as the mod will list them.
        var catalog = new AssemblyCatalog();
        for (int i = 0; i < parts.Entries.Length; i++)
        {
            catalog.Add(parts.Entries[i].Name, parts.Entries[i].Bolts.Length);
        }

        book.AddTo(catalog);
        Assert.Equal(book.Values.Length, catalog.ValueCount);
        catalog.Freeze();

        // The fluids are poured by hand, within the range the game holds them in (level file, 2026-10-15).
        CarValue oil = book.Find("oil")!;
        Assert.Equal("DatabaseMotor/Oilpan", oil.Entry);
        Assert.Equal(3f, oil.Max);
        Assert.Equal("Pouring", Assert.Single(Assert.Single(oil.While).States));
        CarValue fuel = book.Find("fuel")!;
        Assert.Equal("DatabaseMechanics/FuelTank", fuel.Entry);
        Assert.Equal(36f, fuel.Max);
        Assert.Contains("Nozzle", Assert.Single(fuel.While).States);
        Assert.Equal(2, book.Find("coolant")!.While.Length);
        Assert.Equal(1f, book.Find("brake fluid front")!.Max);
        Assert.Equal(0.5f, book.Find("clutch fluid")!.Max);

        // The wear of the stock engine, up to 99, and only raised by hand (a repair).
        string[] wears = { "crankshaft", "head gasket", "rocker shaft", "piston 1", "piston 2", "piston 3", "piston 4", "water pump",
            "alternator", "starter", "gearbox", "fuel pump", "clutch" };
        foreach (string part in wears)
        {
            CarValue wear = book.Find("wear " + part)!;
            Assert.Equal("CarSimulation/MechanicalWear", wear.Place.Text);
            Assert.Equal(99f, wear.Max);
            Assert.Equal(CarValueLocal.Up, wear.Local);
        }

        // Settings turned by hand are numbers; those a "Screw" turns (valves, idle screws, toe, the rally dampers) too, since
        // the part catalogue has no step for those screws (Fase 8): any player's turn is theirs.
        Assert.NotNull(book.Find("spark timing")!.Mirror);
        Assert.Equal(8, book.Values.Count(v => v.Entry == "DatabaseMotor/RockerShaft" && v.Variable.StartsWith("cyl", StringComparison.Ordinal)));
        Assert.Equal(CarValueLocal.Any, book.Find("carburettor idle")!.Local);
        // The four racing carburettor screws, and the average the game works out of them when one turns: what their trigger reads
        // when they are installed ("Assembly", state "End").
        Assert.Equal(5, book.Values.Count(v => v.Entry == "DatabaseOrders/Racing Carburators"));
        Assert.Equal(CarValueLocal.Any, book.Find("racing carburettor average")!.Local);
        Assert.Equal(2, book.Values.Count(v => v.Variable == "Alignment" && v.Local == CarValueLocal.Any));
        Assert.Equal(8, book.Values.Count(v => v.Entry != null && v.Entry.StartsWith("DatabaseOrders/Shock Rally", StringComparison.Ordinal) &&v.Variable.StartsWith("Setting", StringComparison.Ordinal)));

        // The nitrous: what is in the bottle is the simulator's (it burns it; Fleetari fills it there), its setting anyone's.
        Assert.Equal(CarValueLocal.Any, book.Find("n2o setting")!.Local);
        Assert.Equal("DatabaseOrders/N2O Bottle", book.Find("n2o bottle")!.Entry);

        // One tyre number for each hub, and one puncture watch for each wheel entry.
        Assert.Equal(4, book.Values.Count(v => v.FollowVariable == "ThisTire"));
        Assert.Equal(new[] { "Wheels/FL", "Wheels/FR", "Wheels/RL", "Wheels/RR" }, book.Outcomes.Select(o => o.Entry));

        // The chance machines named in the research are all there.
        string[] machines = { "Logic", "RedLining", "FL", "FR", "RL", "RR", "Damage", "Gear Linkage", "Starter", "Oil", "FuelLine",
            "Stuck Throttle", "Brake", "Condition" };
        foreach (string machine in machines)
        {
            Assert.Contains(book.Chances, c => c.Machine == machine && c.Block != ChanceBlock.None);
        }

        CarChance loose = Assert.Single(book.Chances, c => c.EachBoltCheck);
        Assert.Equal("Chance", loose.State);
        Assert.Equal(ChanceBlock.Hold, Assert.Single(book.Chances, c => c.Name == "mechanical wear").Block);
    }

    [Fact]
    public void A_book_naming_an_entry_the_part_catalogue_lacks_is_refused()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "satsuma");
        SatsumaCatalog parts = SatsumaCatalog.Parse(File.ReadAllText(Path.Combine(folder, "parts.json")));
        SatsumaValues book = SatsumaValues.Parse(Small.Replace("DatabaseMotor/Oilpan", "DatabaseMotor/Sump"));

        FormatException error = Assert.Throws<FormatException>(() => book.Check(parts));
        Assert.Contains("'DatabaseMotor/Sump'", error.Message);
    }
}
