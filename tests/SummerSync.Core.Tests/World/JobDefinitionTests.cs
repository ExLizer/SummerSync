using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class JobDefinitionTests
{
    [Fact]
    public void A_file_says_which_machines_are_the_worlds_and_what_every_game_has_the_same()
    {
        JobDefinition well = JobDefinition.Parse("""
            {
              "format": 1,
              "name": "shit1",
              "root": "JOBS",
              "path": "HouseShit1",
              "pause": [{ "path": "WasteWell_2000litre/Shit/Level1/ShitLevelTrigger", "fsm": "Level" }],
              "values": [
                { "path": "WasteWell_2000litre/Shit/Level1/ShitLevelTrigger", "fsm": "Level", "variable": "ShitLevel", "kind": "amount", "scale": 1000 },
                { "path": "LOD/ShitNPC/ShitMan1", "fsm": "Anims", "variable": "Money", "kind": "said" }
              ],
              "positions": ["WasteWell_2000litre/Shit/Level1"]
            }
            """);

        Assert.Equal("shit1", well.Name);
        Assert.Equal("Level", Assert.Single(well.Paused).Machine);
        Assert.Equal(JobValueKind.Amount, well.Values[0].Kind);
        Assert.Equal(1000, well.Values[0].Scale);
        Assert.Equal("ShitLevel", well.Values[0].Variable);
        Assert.Equal(JobValueKind.Said, well.Values[1].Kind);
        Assert.Equal("Anims", well.Values[1].Machine);
        JobPlace level = Assert.Single(well.Positions);
        Assert.Equal("WasteWell_2000litre/Shit/Level1", level.Path);
        Assert.False(level.Once);
        Assert.Empty(well.Loose);
        Assert.Equal("job/shit1/1", well.ValueKey(1));
        Assert.Equal("job/shit1/p0", well.PositionKey(0));
    }

    [Fact]
    public void A_job_can_relay_states_both_ways_and_mirror_what_is_switched_on()
    {
        JobDefinition ads = JobDefinition.Parse("""
            {
              "format": 1, "name": "teimoads", "root": "JOBS", "path": "TeimoADs",
              "relay": [{ "fsm": "Data", "states": ["Activate pay", "Reset 2"] }],
              "actives": ["/STORE/LOD/ActivateStore/PayMoneyAdvert"]
            }
            """);

        JobRelay relay = Assert.Single(ads.Relays);
        Assert.Equal(string.Empty, relay.Path);
        Assert.Equal("Data", relay.Machine);
        Assert.Equal(new[] { "Activate pay", "Reset 2" }, relay.States);
        Assert.Empty(relay.Objects);
        Assert.Null(relay.Pays);
        Assert.Equal(JobRelayFrom.Any, relay.From);
        Assert.False(relay.IsDeal);
        Assert.Empty(relay.Guards);
        JobActive pay = Assert.Single(ads.Actives);
        Assert.Equal("/STORE/LOD/ActivateStore/PayMoneyAdvert", pay.Path);
        Assert.False(pay.Said);
        Assert.Null(pay.Machine);
        Assert.Equal("job/teimoads/s0", ads.RelayKey(0));
        Assert.Equal("job/teimoads/a0", ads.ActiveKey(0));
        Assert.Throws<FormatException>(() => JobDefinition.Parse("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "Data", "states": [] }] }"""));
    }

    [Fact]
    public void A_relay_can_carry_things_pay_in_one_game_and_come_from_one_side()
    {
        JobDefinition field = JobDefinition.Parse("""
            {
              "format": 1, "name": "strawberry", "root": "JOBS", "path": "StrawberryField",
              "relay": [
                { "path": "LOD/Functions/DropABox", "fsm": "Pick", "states": ["State 5"], "objects": ["Box"] },
                { "path": "LOD/Functions/Money", "fsm": "Pick", "states": ["State 1"], "pays": "Money", "quiet": true },
                { "path": "Note", "fsm": "Use", "states": ["Wait button"], "from": "clients" }
              ],
              "actives": [{ "path": "LOD/Functions/Money", "said": true }, { "path": "LOD/Functions/PickABox", "fsm": "Pick", "said": true }],
              "positions": [{ "path": "SuskiPivot", "once": true }],
              "loose": ["JOBS/StrawberryField/LOD/Functions"]
            }
            """);

        Assert.Equal(new[] { "Box" }, field.Relays[0].Objects);
        Assert.Equal("Money", field.Relays[1].Pays);
        Assert.True(field.Relays[1].Quiet);
        Assert.Equal(JobRelayFrom.Clients, field.Relays[2].From);
        Assert.True(field.Actives[0].Said);
        Assert.Null(field.Actives[0].Machine);
        Assert.Equal("Pick", field.Actives[1].Machine);
        Assert.True(Assert.Single(field.Positions).Once);
        Assert.Equal("JOBS/StrawberryField/LOD/Functions", Assert.Single(field.Loose));
    }

    [Fact]
    public void A_dealt_relay_names_where_a_deal_ends_and_what_it_guards()
    {
        JobDefinition kilju = JobDefinition.Parse("""
            {
              "format": 1, "name": "kilju", "root": "JOBS", "path": "HouseDrunk/BeerCampOld/BeerCamp/KiljuBuyer",
              "relay": [{
                "path": "CanTrigger", "fsm": "Logic", "states": ["Freeze Can", "Drink", "Wait trigger 2"], "objects": ["Object"],
                "deal": { "ends": ["Wait trigger 2"] },
                "guards": [{ "state": "Check Kilju", "object": "Object" }]
              }]
            }
            """);

        JobRelay buyer = Assert.Single(kilju.Relays);
        Assert.True(buyer.IsDeal);
        Assert.Equal(new[] { "Wait trigger 2" }, buyer.DealEnds);
        JobGuard guard = Assert.Single(buyer.Guards);
        Assert.Equal("Check Kilju", guard.State);
        Assert.Equal("Object", guard.Variable);
    }

    [Fact]
    public void A_value_can_be_a_global_of_the_game_and_a_relay_can_spare_a_vehicle()
    {
        JobDefinition uncle = JobDefinition.Parse("""
            {
              "format": 1, "name": "uncle", "root": "YARD", "path": "UNCLE",
              "pause": [{ "path": "", "fsm": "States" }],
              "values": [
                { "global": "PlayerKeyHayosiko", "kind": "said" },
                { "path": "", "fsm": "States", "variable": "UncleStage", "kind": "said" }
              ],
              "relay": [{
                "fsm": "States", "from": "host", "states": ["Away", "At uncle"],
                "spare": [{ "state": "At uncle", "vehicle": "Van", "also": ["VanFuelTank"] }, { "state": "Away", "vehicle": "Van" }]
              }]
            }
            """);

        Assert.True(uncle.Values[0].Global);
        Assert.Equal("PlayerKeyHayosiko", uncle.Values[0].Variable);
        Assert.Null(uncle.Values[0].Machine);
        Assert.Equal(string.Empty, uncle.Values[0].Path);
        Assert.False(uncle.Values[1].Global);
        Assert.Equal("States", uncle.Values[1].Machine);
        Assert.Equal(string.Empty, Assert.Single(uncle.Paused).Path);
        JobRelay story = Assert.Single(uncle.Relays);
        Assert.Equal(JobRelayFrom.Host, story.From);
        Assert.Equal(2, story.Spares.Length);
        Assert.Equal("At uncle", story.Spares[0].State);
        Assert.Equal("Van", story.Spares[0].Vehicle);
        Assert.Equal(new[] { "VanFuelTank" }, story.Spares[0].Also);
        Assert.Empty(story.Spares[1].Also);
        Assert.Empty(JobDefinition.Parse("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"] }] }""").Relays[0].Spares);
    }

    [Theory]
    [InlineData("""{ "format": 2, "name": "x", "root": "JOBS", "path": "A", "positions": ["a"] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "values": [{ "global": "PlayerKeyHome", "fsm": "F", "kind": "said" }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "values": [{ "global": "", "kind": "said" }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "spare": [{ "state": "S" }] }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "spare": [{ "state": "S", "vehicle": "" }] }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "spare": [{ "state": "S", "object": "/CAR" }] }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "spare": [{ "state": "S", "object": "CAR", "actions": [0] }] }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "spare": [{ "state": "S", "vehicle": "V", "object": "/CAR", "actions": [0] }] }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "spare": [{ "state": "S", "object": "/CAR", "actions": [-1] }] }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "values": [{ "path": "a", "fsm": "F", "variable": "V", "kind": "said", "scale": 0.0001 }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A" }""")]
    [InlineData("""{ "format": 1, "name": "x/y", "root": "JOBS", "path": "A", "positions": ["a"] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "values": [{ "path": "a", "fsm": "F", "variable": "V", "kind": "sometimes" }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "values": [{ "path": "a", "fsm": "F", "variable": "V", "kind": "amount", "scale": 0 }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "pause": [{ "fsm": "Level" }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "from": "everyone" }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "objects": ["a", "b", "c", "d", "e", "f"] }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "deal": { "ends": ["T"] } }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "deal": { } }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S", "T"], "from": "host", "deal": { "ends": ["S"] } }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "relay": [{ "fsm": "F", "states": ["S"], "quiet": "yes" }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "loose": ["/JOBS/A"] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "loose": ["JOBS"] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "positions": ["a"], "holds": [{ "path": "", "fsm": "F", "states": [], "within": 10 }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "positions": ["a"], "holds": [{ "path": "", "fsm": "F", "states": ["S"], "within": 0 }] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "JOBS", "path": "A", "positions": ["a"], "holds": [{ "path": "", "fsm": "", "states": ["S"], "within": 10 }] }""")]
    public void A_file_that_cannot_be_right_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => JobDefinition.Parse(json));
    }

    [Fact]
    public void Every_job_file_in_the_repository_can_be_read()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "jobs");
        string[] files = Directory.GetFiles(folder, "*.json");

        Assert.NotEmpty(files);
        List<JobDefinition> jobs = files.Select(file => JobDefinition.Parse(File.ReadAllText(file))).ToList();
        List<string> names = jobs.Select(job => job.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("shit1", names);

        // The progress that is everyone's: the keys, the uncle's story and the cards.
        JobDefinition keys = jobs.Single(job => job.Name == "keys");
        Assert.Equal(6, keys.Values.Length);
        Assert.All(keys.Values, value => Assert.True(value.Global && value.Kind == JobValueKind.Said && value.Variable.StartsWith("PlayerKey")));
        JobDefinition uncle = jobs.Single(job => job.Name == "uncle");
        Assert.Equal("States", Assert.Single(uncle.Paused).Machine);
        Assert.Contains(Assert.Single(uncle.Relays).Spares, spare => spare.State == "At uncle" && spare.Vehicle == "Van");
        Assert.Contains("ventti", names);

        // The kilju buyer is found after Jokke's move too; the drunks' camp waits while another player is near it.
        Assert.Contains("HouseDrunkNew/BeerCampNew/BeerCamp/KiljuBuyer", jobs.Single(job => job.Name == "kilju").Moved);
        JobDefinition drunks = jobs.Single(job => job.Name == "drunks");
        JobHold hold = Assert.Single(drunks.Holds);
        Assert.Equal("OpeningHours", hold.Machine);
        Assert.Equal(new[] { "Wait", "Wait 2" }, hold.States);
        Assert.Equal(300f, hold.Within);
        Assert.Equal(5, drunks.Actives.Length);
        // The pub's fighter stays out while another player is near, as the game keeps him for its own (the states it rests in).
        Assert.Equal(new[] { "Bar open?", "Player away?" }, Assert.Single(jobs.Single(job => job.Name == "pubfighter").Holds).States);
        // Fleetari's order is the host's: anyone orders, it counts down while nobody is near, and what it does to the cars is
        // done only where each is simulated.
        JobDefinition shop = jobs.Single(job => job.Name == "repairshop");
        Assert.Equal(39, shop.Values.Length);
        Assert.Equal(0.01, shop.Values.Single(v => v.Variable == "_OrderTime").Scale);
        JobRelay order = Assert.Single(shop.Relays);
        Assert.Equal(JobRelayFrom.Host, order.From);
        JobSpare work = order.Spares.Single(s => s.State == "Work");
        Assert.Equal("/SATSUMA(557kg, 248)", work.Object);
        Assert.Null(work.Vehicle);
        Assert.Equal(new[] { 0 }, work.Actions);
        Assert.True(work.EvenNear);
        Assert.Equal(new[] { 1, 2 }, order.Spares.Single(s => s.State == "Move Satsuma" && s.Object == "/SATSUMA(557kg, 248)").Actions);
        Assert.False(order.Spares.Single(s => s.Object == "/FERNDALE(1630kg)").EvenNear);
        Assert.Equal(new[] { "Installed", "Remove" }, Assert.Single(jobs.Single(job => job.Name == "rollcage").Relays).States);
        JobDefinition tires = jobs.Single(job => job.Name == "tires");
        Assert.Equal(41, tires.Values.Length);
        Assert.Equal(41, tires.Relays.Length);

        // By name wherever the game has them: a wheel on the car hangs from its hub, not from its box.
        Assert.All(tires.Values, value => Assert.StartsWith("*wheel_", value.Path));
        Assert.All(tires.Relays, relay => Assert.StartsWith("*wheel_", relay.Path));
        JobDefinition watch = jobs.Single(job => job.Name == "watch");
        Assert.Equal("Owned", Assert.Single(watch.Values).Variable);
        Assert.True(Assert.Single(watch.Actives).Said);

        // The battery radio: its cover, switch, channel and volume travel as the states they go to and their values; its
        // batteries and its charge as values and the batteries shown in it, never by its "Add battery", which throws away
        // the battery in the hand of whoever plays that game.
        JobDefinition radio = jobs.Single(job => job.Name == "radio");
        Assert.Equal("ITEMS", radio.Root);
        Assert.Equal("radio(itemx)", radio.Path);
        Assert.Contains(radio.Values, value => value.Path == "ChangeBatteries/Insert" && value.Variable == "Batteries" && value.Kind == JobValueKind.Said);
        Assert.Contains(radio.Values, value => value.Variable == "Charge" && value.Scale == 100);
        Assert.Equal(new[] { "Check channel", "Off" }, radio.Relays.Single(relay => relay.Path == "Power").States);
        Assert.DoesNotContain(radio.Relays, relay => relay.States.Contains("Add battery"));
        Assert.Equal(11, radio.Actives.Length);
        Assert.All(radio.Actives, active => Assert.True(active.Said));

        // The car inspection: the inspector and his lift go through their states in every game; only the game that
        // simulates the Satsuma checks it, and the others take its result (the list of what failed, then the verdict).
        JobDefinition inspection = jobs.Single(job => job.Name == "inspection");
        Assert.Equal(33, inspection.Values.Length);
        Assert.All(inspection.Values, value => Assert.True(value.Machine == "Results" && value.Kind == JobValueKind.Said));
        JobRelay inspector = inspection.Relays.Single(relay => relay.Machine == "Animations");
        Assert.Equal(JobRelayFrom.Any, inspector.From);
        Assert.Contains("Lift up", inspector.States);
        JobSpare results = Assert.Single(inspector.Spares);
        Assert.Equal("Get results", results.State);
        Assert.Equal("/SATSUMA(557kg, 248)", results.Object);
        Assert.Equal(new[] { 0 }, results.Actions);
        Assert.True(results.EvenNear);
        Assert.Equal(new[] { "State 2" }, inspection.Relays.Single(relay => relay.Machine == "Results").States);
        Assert.Equal(2, inspection.Actives.Length);

        // What used to be listed in the code: the hay bales, the furniture and the wells' things are loose.
        string[] loose = jobs.SelectMany(job => job.Loose).ToArray();
        Assert.Contains("JOBS/HayBales", loose);
        Assert.Contains("JOBS/HouseDrunk/Moving/Stuff/DestroyThese", loose);
        Assert.Contains("JOBS/HouseShit5/WasteWell_2000litre", loose);
    }
}
