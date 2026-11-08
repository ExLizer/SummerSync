using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class SatsumaCatalogTests
{
    // A small catalogue in the shape of data/satsuma/parts.json: a door (B2), the subframe (A), a wishbone that needs
    // the subframe, and a wire.
    private const string Small = """
        {
          "format": 1,
          "car": "SATSUMA(557kg, 248)",
          "entries": [
            {
              "name": "DatabaseBody/Wishbone_FL", "group": "Body", "family": "A", "data": ["Installed", "Bolted", "Damaged"],
              "part": "wishbone fl(Clone)", "partVariable": "SpawnThis", "copies": ["Chassis/FL/Wishbone/Arm/IK_wishbone_fl"],
              "spawn": "wishbone fl(Clone)",
              "triggers": [
                { "path": "Chassis/FL/trigger_wishbone_fl", "part": "wishbone fl(Clone)", "state": "Assemble", "event": false,
                  "sound": true, "family": "A",
                  "requires": [{ "entry": "DatabaseBody/Subframe", "variable": "Bolted", "value": true },
                               { "entry": "DatabaseBody/Subframe", "variable": "Installed", "value": true }] }
              ],
              "removals": ["Chassis/FL/Wishbone/Arm/IK_wishbone_fl/wishbone fl(xxxxx)"],
              "blockedBy": [{ "entry": "DatabaseBody/Subframe", "variable": "Installed" }],
              "boltChecks": [
                { "path": "Chassis/FL/Wishbone/Arm/IK_wishbone_fl/wishbone fl(xxxxx)", "yes": 2, "no": 0, "bolts": [
                  { "index": 1, "path": "Chassis/FL/Wishbone/Arm/IK_wishbone_fl/BoltPM", "size": 1.0, "stages": 8 },
                  { "index": 0, "path": "Chassis/FL/Wishbone/Arm/IK_wishbone_fl/BoltPM", "size": 1.0, "stages": 8 }] }
              ]
            },
            {
              "name": "DatabaseBody/Door_Left", "group": "Body", "family": "B2", "data": ["Installed", "Bolted", "Damaged"],
              "part": "door left(Clone)", "partVariable": "ThisPart",
              "triggers": [
                { "path": "Body/trigger_door_left", "part": "door left(Clone)", "state": "Wait", "event": true, "setup": "Setup",
                  "sound": true, "family": "B2" }
              ],
              "removals": ["DatabaseBody/Door_Left:"],
              "boltChecks": [
                { "path": "DatabaseBody/Door_Left:", "yes": 28, "no": 0, "bolts": [
                  { "index": 0, "path": "DatabaseBody/Door_Left:Bolts/BoltPM", "size": 1.0, "stages": 8 },
                  { "index": 1, "path": "DatabaseBody/Door_Left:Bolts/BoltPM", "size": 1.0, "stages": 8 }] }
              ]
            },
            {
              "name": "DatabaseBody/Subframe", "group": "Body", "family": "A", "data": ["Installed", "Bolted", "Damaged"],
              "part": "sub frame(Clone)", "partVariable": "SpawnThis", "copies": ["Chassis/sub frame(xxxxx)"], "spawn": "sub frame(Clone)",
              "triggers": [{ "path": "Chassis/trigger_subframe", "part": "sub frame(Clone)", "state": "Assemble", "event": false, "sound": true, "family": "A" }],
              "removals": ["Chassis/sub frame(xxxxx)"],
              "blockedBy": [{ "entry": "DatabaseBody/Wishbone_FL", "variable": "Installed" }]
            },
            {
              "name": "DatabaseWiring/WiringGaugeExtra", "group": "Wiring", "family": "wiring", "data": ["Installed", "Bolted"],
              "wiring": { "ends": ["Wiring/Triggers/GaugeExtra/GaugeHarness", "Wiring/Triggers/GaugeExtra/HarnessGauge"],
                          "state": "Finish assembly", "activates": ["Wiring/Parts/wiring_gauge_extra(xxxxx)"] }
            },
            {
              "name": "Wheels/FL", "group": "Wheels", "family": "wheel", "part": "wheel_regula",
              "boltChecks": [{ "path": "Wheels/FL:", "fsm": "Use", "yes": 1, "no": 0, "bolts": [
                { "index": 0, "path": "Wheels/FL:Bolts/BoltPM", "size": 1.3, "stages": 8 }] }]
            },
            {
              "name": "DatabaseBody/Hubcap1", "group": "Body", "family": "B1", "data": ["Installed"], "part": "hubcap(Clone)",
              "triggers": [{ "path": "wheel:trigger_hubcap", "part": "hubcap(Clone)", "state": "Wait 2", "event": true, "sound": true,
                             "family": "B1", "shared": true }],
              "removals": ["/CARPARTS/PartsCar/hubcap(Clone)"]
            }
          ]
        }
        """;

    [Fact]
    public void Entries_are_sorted_by_name_and_numbered_in_that_order()
    {
        SatsumaCatalog catalog = SatsumaCatalog.Parse(Small);

        Assert.Equal("SATSUMA(557kg, 248)", catalog.Car);
        Assert.Equal(new[] { "DatabaseBody/Door_Left", "DatabaseBody/Hubcap1", "DatabaseBody/Subframe", "DatabaseBody/Wishbone_FL",
            "DatabaseWiring/WiringGaugeExtra", "Wheels/FL" }, catalog.Entries.Select(e => e.Name));
        for (int i = 0; i < catalog.Entries.Length; i++)
        {
            Assert.Equal(i, catalog.Entries[i].Index);
            Assert.Same(catalog.Entries[i], catalog.At(i));
            Assert.Same(catalog.Entries[i], catalog.Find(catalog.Entries[i].Name));
        }

        Assert.Null(catalog.Find("DatabaseBody/Door_Right"));
        Assert.Null(catalog.At(-1));
        Assert.Null(catalog.At(catalog.Entries.Length));
        Assert.Equal(5, catalog.BoltCount);
    }

    [Fact]
    public void An_entry_says_how_its_part_goes_on_and_comes_off()
    {
        SatsumaCatalog catalog = SatsumaCatalog.Parse(Small);
        PartEntry door = catalog.Find("DatabaseBody/Door_Left")!;

        Assert.Equal(PartFamily.Jointed, door.Family);
        Assert.Equal("Body", door.Group);
        Assert.True(door.Has("Bolted"));
        Assert.False(door.Has("Purchased"));
        Assert.Equal("door left(Clone)", door.Part);
        Assert.Equal("ThisPart", door.PartVariable);
        PartTrigger trigger = Assert.Single(door.Triggers);
        Assert.Equal(PartPlaceKind.Car, trigger.Place.Kind);
        Assert.Equal("Body/trigger_door_left", trigger.Place.Path);
        Assert.Equal("Wait", trigger.State);
        Assert.True(trigger.AssembleEvent);
        Assert.Equal("Setup", trigger.Setup);
        Assert.True(trigger.Sound);
        Assert.Equal("Part", PartTrigger.PartVariable);
        Assert.Empty(trigger.Requires);

        PartPlace removal = Assert.Single(door.Removals);
        Assert.Equal(PartPlaceKind.Part, removal.Kind);
        Assert.Equal("DatabaseBody/Door_Left", removal.Entry);
        Assert.Equal("", removal.Path);

        PartBoltCheck check = Assert.Single(door.BoltChecks);
        Assert.Equal("BoltCheck", check.Machine);
        Assert.True(check.HasThresholds);
        Assert.Equal(28f, check.Yes);
        Assert.Equal(new[] { 0, 1 }, door.Bolts.Select(b => b.Index));
        Assert.Equal(new[] { 0, 1 }, door.Bolts.Select(b => b.Slot));
        Assert.Equal("Bolts/BoltPM", door.Bolts[0].Place.Path);
        Assert.Same(check, door.Bolts[1].Check);
        Assert.Equal(8, door.Bolts[0].Stages);

        PartEntry subframe = catalog.Find("DatabaseBody/Subframe")!;
        Assert.Equal(PartFamily.Clone, subframe.Family);
        Assert.Equal("sub frame(Clone)", subframe.Spawn);
        Assert.Equal("Chassis/sub frame(xxxxx)", Assert.Single(subframe.Copies).Path);
        Assert.False(Assert.Single(subframe.Triggers).AssembleEvent);
        Assert.Null(subframe.Triggers[0].Setup);
        Assert.Equal("DatabaseBody/Wishbone_FL", Assert.Single(subframe.BlockedBy).Entry);

        PartEntry wishbone = catalog.Find("DatabaseBody/Wishbone_FL")!;
        Assert.Equal(new[] { "Bolted", "Installed" }, wishbone.Triggers[0].Requires.Select(r => r.Variable));
        Assert.All(wishbone.Triggers[0].Requires, r => Assert.True(r.Value));
        // Bolts are in index order whatever order the file has them in.
        Assert.Equal(new[] { 0, 1 }, wishbone.Bolts.Select(b => b.Index));
    }

    [Fact]
    public void A_wire_a_wheel_slot_and_a_hubcap_have_their_own_places()
    {
        SatsumaCatalog catalog = SatsumaCatalog.Parse(Small);

        PartEntry wire = catalog.Find("DatabaseWiring/WiringGaugeExtra")!;
        Assert.Equal(PartFamily.Wiring, wire.Family);
        Assert.Equal("Finish assembly", wire.Wiring!.State);
        Assert.Equal("Assemble", PartWiring.Machine);
        Assert.Equal(2, wire.Wiring.Ends.Length);
        Assert.Equal("Wiring/Parts/wiring_gauge_extra(xxxxx)", Assert.Single(wire.Wiring.Activates).Path);
        Assert.Empty(wire.Triggers);
        Assert.Empty(wire.Bolts);

        PartEntry wheel = catalog.Find("Wheels/FL")!;
        Assert.Equal(PartFamily.Wheel, wheel.Family);
        Assert.Empty(wheel.Data);
        Assert.Equal("Use", wheel.BoltChecks[0].Machine);
        Assert.Equal("Wheels/FL", wheel.Bolts[0].Place.Entry);

        PartEntry hubcap = catalog.Find("DatabaseBody/Hubcap1")!;
        PartTrigger trigger = Assert.Single(hubcap.Triggers);
        Assert.Equal(PartPlaceKind.Wheel, trigger.Place.Kind);
        Assert.Null(trigger.Place.Entry);
        Assert.Equal("trigger_hubcap", trigger.Place.Path);
        Assert.True(trigger.Shared);
        Assert.Equal(PartPlaceKind.Scene, hubcap.Removals[0].Kind);
        Assert.Equal("CARPARTS/PartsCar/hubcap(Clone)", hubcap.Removals[0].Path);
    }

    [Theory]
    [InlineData("\"format\": 1", "\"format\": 2", "format 2")]
    [InlineData("\"family\": \"B2\", \"data\"", "\"family\": \"B3\", \"data\"", "unknown family 'B3'")]
    [InlineData("\"name\": \"DatabaseBody/Hubcap1\"", "\"name\": \"DatabaseBody/Door_Left\"", "two entries called 'DatabaseBody/Door_Left'")]
    [InlineData("{ \"entry\": \"DatabaseBody/Subframe\", \"variable\": \"Bolted\"", "{ \"entry\": \"DatabaseBody/Sub\", \"variable\": \"Bolted\"", "depends on 'DatabaseBody/Sub'")]
    [InlineData("\"blockedBy\": [{ \"entry\": \"DatabaseBody/Wishbone_FL\"", "\"blockedBy\": [{ \"entry\": \"DatabaseBody/Wishbone\"", "depends on 'DatabaseBody/Wishbone'")]
    [InlineData("{ \"index\": 1, \"path\": \"DatabaseBody/Door_Left:Bolts/BoltPM\"", "{ \"index\": 0, \"path\": \"DatabaseBody/Door_Left:Bolts/BoltPM\"", "two bolts with the index 0")]
    [InlineData("{ \"index\": 1, \"path\": \"DatabaseBody/Door_Left:Bolts/BoltPM\"", "{ \"index\": 10, \"path\": \"DatabaseBody/Door_Left:Bolts/BoltPM\"", "index outside 0 to 9")]
    [InlineData("\"size\": 1.3, \"stages\": 8", "\"size\": 1.3, \"stages\": 9", "stages outside 1 to 8")]
    [InlineData("\"removals\": [\"DatabaseBody/Door_Left:\"]", "\"removals\": [\"DatabaseBody/Door_Right:\"]", "below the part of an entry the catalogue does not have")]
    [InlineData("\"removals\": [\"DatabaseBody/Door_Left:\"]", "\"removals\": [\"/\"]", "names nothing")]
    [InlineData("\"state\": \"Wait\"", "\"state\": \"\"", "needs the state to enter")]
    [InlineData("\"data\": [\"Installed\", \"Bolted\"]", "\"data\": [\"Installed\", \"Fitted\"]", "unknown flag 'Fitted'")]
    [InlineData("\"state\": \"Finish assembly\"", "\"state\": \"\"", "two ends and the state")]
    [InlineData("\"event\": true, \"setup\"", "\"event\": 1, \"setup\"", "\"event\" that is not true or false")]
    [InlineData("\"variable\": \"Bolted\", \"value\": true", "\"variable\": \"Bolted\"", "without a true or false \"value\"")]
    public void A_catalogue_that_is_wrong_is_refused_with_the_reason(string from, string to, string expected)
    {
        Assert.Contains(from, Small);
        var error = Assert.Throws<FormatException>(() => SatsumaCatalog.Parse(ReplaceFirst(Small, from, to)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Triggers_are_in_place_order_whatever_order_the_file_has_them_in()
    {
        const string twoWays = """
            { "format": 1, "car": "C", "entries": [{ "name": "Wheels/FL", "group": "Wheels", "family": "wheel", "triggers": [
              { "path": "FL/AckerFL/wheelFL/TriggerWheelFL_New", "part": "wheel_offset", "state": "Wait 2", "event": true, "family": "wheel" },
              { "path": "FL/AckerFL/wheelFL/TriggerWheelFL_New", "part": "wheel_regula", "state": "Wait", "event": true, "family": "wheel" }] }] }
            """;
        PartEntry wheel = SatsumaCatalog.Parse(twoWays).Entries[0];

        Assert.Equal(new[] { "Wait", "Wait 2" }, wheel.Triggers.Select(t => t.State));
        Assert.Equal(new[] { "wheel_regula", "wheel_offset" }, wheel.Triggers.Select(t => t.Part));
    }

    [Fact]
    public void A_catalogue_with_no_entries_or_no_car_is_refused()
    {
        Assert.Throws<FormatException>(() => SatsumaCatalog.Parse("""{ "format": 1, "car": "SATSUMA(557kg, 248)", "entries": [] }"""));
        Assert.Throws<FormatException>(() => SatsumaCatalog.Parse("""{ "format": 1, "car": "", "entries": [{ "name": "A", "group": "G", "family": "other" }] }"""));
        Assert.Throws<FormatException>(() => SatsumaCatalog.Parse("""{ "format": 1, "car": "C", "entries": [{ "name": "A:B", "group": "G", "family": "other" }] }"""));
    }

    [Fact]
    public void The_shape_does_not_depend_on_the_order_of_the_file_and_changes_with_what_the_session_names()
    {
        SatsumaCatalog one = SatsumaCatalog.Parse(Small);
        SatsumaCatalog again = SatsumaCatalog.Parse(Small);
        Assert.Equal(one.Shape, again.Shape);

        // The same entries in another order: same indices, same shape.
        string reordered = SwapFirstTwoEntries(Small);
        Assert.NotEqual(Small, reordered);
        SatsumaCatalog other = SatsumaCatalog.Parse(reordered);
        Assert.Equal(one.Shape, other.Shape);
        Assert.Equal(one.Entries.Select(e => e.Name), other.Entries.Select(e => e.Name));

        // What the session refers to changes the shape: a trigger's state, a bolt's index, an entry's name.
        Assert.NotEqual(one.Shape, SatsumaCatalog.Parse(Small.Replace("\"state\": \"Wait\"", "\"state\": \"Freeze car\"")).Shape);
        Assert.NotEqual(one.Shape, SatsumaCatalog.Parse(Small.Replace("{ \"index\": 0, \"path\": \"Wheels/FL:", "{ \"index\": 1, \"path\": \"Wheels/FL:")).Shape);
        Assert.NotEqual(one.Shape, SatsumaCatalog.Parse(Small.Replace("DatabaseWiring/WiringGaugeExtra", "DatabaseWiring/WiringGaugeAFR")).Shape);

        // What it does not refer to does not: whether a trigger plays a sound.
        Assert.Equal(one.Shape, SatsumaCatalog.Parse(Small.Replace("\"sound\": true, \"family\": \"B2\"", "\"sound\": false, \"family\": \"B2\"")).Shape);
    }

    [Fact]
    public void The_catalogue_in_the_repository_can_be_read_and_matches_what_the_game_did_in_the_bridge_tests()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "data", "satsuma", "parts.json");
        string text = File.ReadAllText(file);
        SatsumaCatalog catalog = SatsumaCatalog.Parse(text);

        Assert.Equal("SATSUMA(557kg, 248)", catalog.Car);
        Assert.InRange(catalog.Entries.Length, 200, 300);
        Assert.Equal(catalog.Shape, SatsumaCatalog.Parse(text).Shape);
        Assert.Equal(4, catalog.Entries.Count(e => e.Family == PartFamily.Wheel));
        Assert.Contains(catalog.Entries, e => e.Family == PartFamily.Wiring && e.Wiring != null);
        Assert.InRange(catalog.BoltCount, 300, 600);

        // Door: a part on a hinge, its trigger takes ASSEMBLE in its resting state; four bolts, bolted at 28 (bridge, 2026-10-15).
        PartEntry door = catalog.Find("DatabaseBody/Door_Left")!;
        Assert.Equal(PartFamily.Jointed, door.Family);
        PartTrigger doorTrigger = Assert.Single(door.Triggers);
        Assert.Equal("Body/trigger_door_left", doorTrigger.Place.Text);
        Assert.True(doorTrigger.AssembleEvent);
        Assert.Equal(28f, Assert.Single(door.BoltChecks).Yes);
        Assert.Equal(new[] { 0, 1, 2, 3 }, door.Bolts.Select(b => b.Index));
        Assert.All(door.Bolts, b => Assert.Equal(8, b.Stages));

        // Subframe: family A, no ASSEMBLE in the resting state, so the way in is the state 'Assemble'.
        PartEntry subframe = catalog.Find("DatabaseBody/Subframe")!;
        Assert.Equal(PartFamily.Clone, subframe.Family);
        Assert.False(Assert.Single(subframe.Triggers).AssembleEvent);
        Assert.Equal("Assemble", subframe.Triggers[0].State);
        Assert.Equal("sub frame(Clone)", subframe.Spawn);

        // A wire is made by taking one end to 'Finish assembly'.
        PartEntry gauge = catalog.Find("DatabaseWiring/WiringGaugeExtra")!;
        Assert.Equal("Finish assembly", gauge.Wiring!.State);
        Assert.Contains(gauge.Wiring.Ends, e => e.Text == "Wiring/Triggers/GaugeExtra/HarnessGauge");

        // Every entry and every bolt key is unique; every reference resolves (Parse checks it), and indices follow names.
        Assert.Equal(catalog.Entries.Length, catalog.Entries.Select(e => e.Name).Distinct().Count());
        for (int i = 1; i < catalog.Entries.Length; i++)
        {
            Assert.True(string.CompareOrdinal(catalog.Entries[i - 1].Name, catalog.Entries[i].Name) < 0);
        }
    }

    private static string ReplaceFirst(string text, string from, string to)
    {
        int at = text.IndexOf(from, StringComparison.Ordinal);
        return text.Substring(0, at) + to + text.Substring(at + from.Length);
    }

    // Moves the second entry of the small catalogue in front of the first one.
    private static string SwapFirstTwoEntries(string text)
    {
        int first = text.LastIndexOf('{', text.IndexOf("\"name\": \"DatabaseBody/Wishbone_FL\"", StringComparison.Ordinal));
        int second = text.LastIndexOf('{', text.IndexOf("\"name\": \"DatabaseBody/Door_Left\"", StringComparison.Ordinal));
        int third = text.LastIndexOf('{', text.IndexOf("\"name\": \"DatabaseBody/Subframe\"", StringComparison.Ordinal));
        Assert.True(first >= 0 && second > first && third > second, "the entries of the small catalogue moved");
        string a = text.Substring(first, second - first);
        string b = text.Substring(second, third - second);
        return text.Substring(0, first) + b + a + text.Substring(third);
    }
}
