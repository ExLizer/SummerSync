using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class NpcDefinitionTests
{
    private const string Moose = """
        {
          "format": 1,
          "name": "moose",
          "root": "AnimalsMoose",
          "find": "Moose",
          "count": 2,
          "moves": "transform",
          "off": ["Move", "Raycast"],
          "gone": { "path": "Offset/Mesh/Collider", "fsm": "CarHit", "state": "State 2" }
        }
        """;

    [Fact]
    public void A_file_says_where_an_npc_is_how_it_moves_and_what_of_it_is_shown()
    {
        NpcDefinition boat = NpcDefinition.Parse("""
            {
              "format": 1,
              "name": "boat2",
              "root": "TRAFFIC",
              "path": "Lake/AIboat2",
              "moves": "body",
              "off": ["Simulation", "Steer"],
              "offElsewhere": [{ "path": "TRAFFIC/Lake", "fsm": "Activate" }],
              "shown": ["LOD"],
              "rpm": { "path": "LOD/Audio", "fsm": "Sound Engine", "variable": "RPM" },
              "animation": "LOD/Driver/skeleton"
            }
            """);

        Assert.Equal("boat2", boat.Name);
        Assert.Equal("TRAFFIC", boat.Root);
        Assert.Equal("Lake/AIboat2", boat.Path);
        Assert.Null(boat.Find);
        Assert.Equal(1, boat.Count);
        Assert.Equal(NpcMotion.Body, boat.Motion);
        Assert.Equal(new[] { "Simulation", "Steer" }, boat.Off);
        Assert.Equal("TRAFFIC/Lake", Assert.Single(boat.OffElsewhere).Path);
        Assert.Equal("Activate", boat.OffElsewhere[0].Machine);
        Assert.Equal(new[] { "LOD" }, boat.Shown);
        Assert.Equal("RPM", boat.Rpm!.Name);
        Assert.Equal("LOD/Audio", boat.Rpm.Path);
        Assert.Equal("LOD/Driver/skeleton", boat.Animation);
        Assert.Null(boat.Gone);
        Assert.Equal(new[] { "boat2" }, boat.Keys);
    }

    [Fact]
    public void Several_of_one_name_each_travel_by_their_place()
    {
        NpcDefinition moose = NpcDefinition.Parse(Moose);

        Assert.Equal(NpcMotion.Transform, moose.Motion);
        Assert.Equal(new[] { "moose#0", "moose#1" }, moose.Keys);
        Assert.Equal("State 2", moose.Gone!.Name);
        Assert.Equal("CarHit", moose.Gone.Machine);
        Assert.Empty(moose.Shown);
        Assert.Empty(moose.OffElsewhere);
    }

    [Fact]
    public void A_car_can_follow_the_hosts_states_and_be_looked_for_under_a_second_root()
    {
        NpcDefinition kuski = NpcDefinition.Parse("""
            {
              "format": 1, "name": "kuski", "root": "NPC_CARS", "find": "KUSKI", "alsoRoot": "DRAGRACE", "moves": "car",
              "follows": [{ "path": "CrashEvent", "fsm": "Crash", "state": "Ragdoll" }]
            }
            """);

        Assert.Equal("DRAGRACE", kuski.AlsoRoot);
        NpcMachine crash = Assert.Single(kuski.Follows);
        Assert.Equal("CrashEvent", crash.Path);
        Assert.Equal("Crash", crash.Machine);
        Assert.Equal("Ragdoll", crash.Name);
        Assert.Throws<FormatException>(() => NpcDefinition.Parse("""{ "format": 1, "name": "x", "root": "R", "path": "a", "alsoRoot": "B", "moves": "car" }"""));
        Assert.Throws<FormatException>(() => NpcDefinition.Parse("""{ "format": 1, "name": "x", "root": "R", "path": "a", "moves": "car", "follows": [{ "fsm": "Crash" }] }"""));
    }

    [Fact]
    public void A_car_keeps_one_bit_for_its_brake_lights()
    {
        NpcDefinition bus = NpcDefinition.Parse("""{ "format": 1, "name": "bus", "root": "NPC_CARS", "find": "BUS", "moves": "car", "shown": ["1","2","3","4","5","6","7"] }""");

        Assert.Equal(NpcMotion.Car, bus.Motion);
        Assert.Equal(7, bus.Shown.Length);
        Assert.Equal(1 << 7, NpcDefinition.BrakeBit);
        Assert.Throws<FormatException>(() => NpcDefinition.Parse("""{ "format": 1, "name": "bus", "root": "NPC_CARS", "find": "BUS", "moves": "car", "shown": ["1","2","3","4","5","6","7","8"] }"""));
    }

    [Theory]
    [InlineData("""{ "format": 2, "name": "x", "root": "R", "path": "a", "moves": "body" }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "moves": "body" }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "path": "a", "find": "b", "moves": "body" }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "path": "a", "count": 2, "moves": "body" }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "find": "a", "count": 9, "moves": "body" }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "path": "a", "moves": "flying" }""")]
    [InlineData("""{ "format": 1, "name": "x#1", "root": "R", "path": "a", "moves": "body" }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "path": "a", "moves": "body", "shown": ["1","2","3","4","5","6","7","8","9"] }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "path": "a", "moves": "body", "gone": { "fsm": "CarHit" } }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "path": "a", "moves": "body", "mirror": { "fsm": "Move", "states": [] } }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "path": "a", "moves": "body", "mirror": { "fsm": "Move", "states": ["A", "A"] } }""")]
    [InlineData("""{ "format": 1, "name": "x", "root": "R", "path": "a", "moves": "body", "mirror": { "states": ["A"] } }""")]
    public void A_file_that_cannot_be_right_is_refused(string json)
    {
        Assert.Throws<FormatException>(() => NpcDefinition.Parse(json));
    }

    [Fact]
    public void Both_sides_list_the_npcs_the_same_way_whatever_order_the_files_come_in()
    {
        NpcDefinition moose = NpcDefinition.Parse(Moose);
        NpcDefinition train = NpcDefinition.Parse("""{ "format": 1, "name": "train", "root": "TRAIN", "find": "TRAIN", "moves": "transform" }""");

        string[] one = NpcDefinition.List(new[] { moose, train }, out uint shapeOne);
        string[] other = NpcDefinition.List(new[] { train, moose }, out uint shapeOther);

        Assert.Equal(new[] { "moose#0", "moose#1", "train" }, one);
        Assert.Equal(one, other);
        Assert.Equal(shapeOne, shapeOther);
        NpcDefinition.List(new[] { train }, out uint shapeTrain);
        Assert.NotEqual(shapeOne, shapeTrain);
    }

    [Fact]
    public void A_machine_can_be_the_hosts_with_a_way_into_each_state()
    {
        NpcDefinition bike = NpcDefinition.Parse("""
            { "format": 1, "name": "teimobike", "root": "STORE", "path": "TeimoInBike", "moves": "transform", "off": ["::Move"],
              "mirror": { "fsm": "Move", "states": ["At home", "New path", { "state": "At store", "enter": "State 2" }] } }
            """);

        Assert.NotNull(bike.Mirror);
        Assert.Equal(string.Empty, bike.Mirror!.Path);
        Assert.Equal("Move", bike.Mirror.Machine);
        Assert.Equal(new[] { "At home", "New path", "At store" }, bike.Mirror.States);
        Assert.Equal(new[] { "At home", "New path", "State 2" }, bike.Mirror.Enter);
        Assert.Equal(new[] { "::Move" }, bike.Off);
        Assert.Null(NpcDefinition.Parse(Moose).Mirror);
    }

    [Fact]
    public void Every_npc_file_in_the_repository_can_be_read()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "npcs");
        string[] files = Directory.GetFiles(folder, "*.json");

        Assert.NotEmpty(files);
        var definitions = files.Select(file => NpcDefinition.Parse(File.ReadAllText(file))).ToList();
        string[] keys = NpcDefinition.List(definitions, out _);

        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.InRange(keys.Length, 1, TrafficWire.MaxCars);
        Assert.Contains("train", keys);
        Assert.Contains("teimobike", keys);
    }
}
