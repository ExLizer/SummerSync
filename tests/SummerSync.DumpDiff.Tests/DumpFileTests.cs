using System.Text;
using SummerSync.Core.Dump;

namespace SummerSync.DumpDiff.Tests;

public class DumpFileTests
{
    private static DumpBuilder Sample() => new DumpBuilder()
        .At(120.5, 7200)
        .Global("float", "PlayerMoney", 1500.5)
        .Global("bool", "Raining", false)
        .Fsm("SATSUMA(557kg, 248)/Engine", "Fuel", "Burn", fsm => fsm
            .Id(-1234, "4/0", 1)
            .Flags(isEnabled: true, isActive: false, isInitialized: true)
            .Var("float", "FuelLevel", 12.5)
            .Var("vector3", "Offset", new double[] { 1, 2, 3 })
            .Var("gameObject", "Target", "SATSUMA(557kg, 248)/Tank")
            .Var("string", "Label", null)
            .State("Burn")
            .Action("FloatAdd", "Math", parameters: new() { ["floatVariable"] = new Dictionary<string, object?> { ["var"] = "FuelLevel", ["v"] = 12.5 }, ["add"] = -0.1, ["everyFrame"] = true })
            .State("Idle", loaded: false)
            .Action("Wait", "Time"))
        .Collection("Systems/Parts", "ArrayList", "bolts", new object[] { "a", 1, true })
        .Body("ITEMS/beer case(itemx)", position: [1, 2, 3], sleeping: true, id: 77)
        .Vehicle("SATSUMA(557kg, 248)", position: [10, 0, 5], drivetrain: new() { ["rpm"] = 850.0, ["gear"] = 1 }, colliders: 3)
        .Error("FSM 'x': NullReferenceException: boom");

    [Fact]
    public void Reads_everything_the_dumper_writes()
    {
        DumpFile dump = Sample().Build();

        Assert.Equal(DumpFormat.KindFull, dump.Kind);
        Assert.Equal("GAME", dump.Game.Scene);
        Assert.Equal(120.5, dump.Game.Time);
        Assert.Equal(7200, dump.Game.Frame);
        Assert.Equal(1500.5, dump.Globals["float"]["PlayerMoney"].Number);
        Assert.Equal(DumpValueKind.Bool, dump.Globals["bool"]["Raining"].Kind);

        FsmEntry fsm = Assert.Single(dump.Fsms);
        Assert.Equal("SATSUMA(557kg, 248)/Engine", fsm.Path);
        Assert.Equal("4/0", fsm.IndexPath);
        Assert.Equal("Fuel", fsm.Name);
        Assert.Equal(1, fsm.Ordinal);
        Assert.Equal(-1234, fsm.Id);
        Assert.True(fsm.Enabled);
        Assert.False(fsm.Active);
        Assert.True(fsm.Initialized);
        Assert.Equal("Burn", fsm.State);
        Assert.Equal(12.5, fsm.Variables["float"]["FuelLevel"].Number);
        Assert.Equal(new double[] { 1, 2, 3 }, fsm.Variables["vector3"]["Offset"].Numbers);
        Assert.Equal("SATSUMA(557kg, 248)/Tank", fsm.Variables["gameObject"]["Target"].Text);
        Assert.Equal(DumpValueKind.Null, fsm.Variables["string"]["Label"].Kind);

        Assert.Equal(2, fsm.States!.Count);
        Assert.True(fsm.States[0].Loaded);
        ActionEntry action = Assert.Single(fsm.States[0].Actions);
        Assert.Equal("FloatAdd", action.Type);
        Assert.Equal("Math", action.Category);
        Assert.Equal(DumpValueKind.Json, action.Parameters!["floatVariable"].Kind);
        Assert.Equal(-0.1, action.Parameters["add"].Number);
        Assert.False(fsm.States[1].Loaded);
        Assert.Null(fsm.States[1].Actions[0].Parameters);
        Assert.Equal("Next", fsm.States[0].Transitions[0].To);

        CollectionEntry collection = Assert.Single(dump.Collections);
        Assert.Equal("ArrayList", collection.Type);
        Assert.Equal("bolts", collection.Reference);
        Assert.Equal("[\"a\",1,true]", collection.Items.Text);

        BodyEntry body = Assert.Single(dump.Rigidbodies);
        Assert.Equal(new double[] { 1, 2, 3 }, body.Position);
        Assert.True(body.Sleeping);
        Assert.Equal(77, body.Id);

        VehicleEntry vehicle = Assert.Single(dump.Vehicles);
        Assert.Equal(4, vehicle.Wheels);
        Assert.Equal(850.0, vehicle.Drivetrain!["rpm"].Number);
        Assert.Equal(new double[] { 10, 0, 5 }, vehicle.Body!.Position);
        Assert.Equal(3, vehicle.Colliders!.Count);
        Assert.Contains("CarDynamics", vehicle.Components);

        Assert.Single(dump.Errors);
        Assert.Equal(12, dump.Stats[DumpFormat.StatMilliseconds]);
    }

    [Fact]
    public void Structure_can_be_skipped_to_save_memory()
    {
        DumpFile dump = Sample().Build(includeStructure: false);

        FsmEntry fsm = Assert.Single(dump.Fsms);
        Assert.Null(fsm.States);
        Assert.Null(fsm.Events);
        Assert.Null(dump.Vehicles[0].Colliders);
        Assert.Equal(12.5, fsm.Variables["float"]["FuelLevel"].Number);
    }

    [Fact]
    public void State_dumps_have_no_structure_at_all()
    {
        DumpFile dump = Sample().Kind(DumpFormat.KindState).Build();

        Assert.Equal(DumpFormat.KindState, dump.Kind);
        Assert.Null(dump.Fsms[0].States);
        Assert.Equal("Burn", dump.Fsms[0].State);
    }

    [Fact]
    public void A_different_format_version_is_rejected()
    {
        var builder = new DumpBuilder().FormatVersion(DumpFormat.Version + 1);

        Assert.Throws<InvalidDataException>(() => builder.Build());
    }

    [Fact]
    public void Truncated_files_are_reported_as_invalid_json()
    {
        string json = Sample().ToJson();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json[..(json.Length / 2)]));

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => DumpFile.Parse(stream, includeStructure: true));
    }
}

public class DumpValueTests
{
    [Fact]
    public void Numbers_compare_within_absolute_and_relative_tolerance()
    {
        Assert.True(DumpValue.FromNumber(1.0000).EqualsWithin(DumpValue.FromNumber(1.0005), 0.001, 0));
        Assert.False(DumpValue.FromNumber(1.0).EqualsWithin(DumpValue.FromNumber(1.01), 0.001, 0));
        Assert.True(DumpValue.FromNumber(1000.0).EqualsWithin(DumpValue.FromNumber(1000.5), 0, 0.001));
        Assert.False(DumpValue.FromNumber(1000.0).EqualsWithin(DumpValue.FromNumber(1002.0), 0, 0.001));
    }

    [Fact]
    public void Vectors_compare_component_wise()
    {
        DumpValue a = DumpValue.FromNumbers([1, 2, 3]);

        Assert.True(a.EqualsWithin(DumpValue.FromNumbers([1.0004, 2, 3]), 0.001, 0));
        Assert.False(a.EqualsWithin(DumpValue.FromNumbers([1.1, 2, 3]), 0.001, 0));
        Assert.False(a.EqualsWithin(DumpValue.FromNumbers([1, 2]), 0.001, 0));
        Assert.Equal(0.5, DumpValue.Distance(a, DumpValue.FromNumbers([1, 2.5, 3])), 6);
    }

    [Fact]
    public void Different_kinds_are_never_equal()
    {
        Assert.False(DumpValue.FromNumber(1).EqualsWithin(DumpValue.FromString("1"), 1, 1));
        Assert.False(DumpValue.Null.EqualsWithin(DumpValue.FromBool(false), 1, 1));
        Assert.True(DumpValue.Null.EqualsWithin(DumpValue.Null, 0, 0));
        Assert.True(DumpValue.FromString("a").EqualsWithin(DumpValue.FromString("a"), 0, 0));
        Assert.False(DumpValue.FromBool(true).EqualsWithin(DumpValue.FromBool(false), 1, 1));
    }

    [Fact]
    public void Nested_json_is_compared_structurally_with_tolerance()
    {
        DumpValue a = DumpValue.FromJson("{\"go\":\"A/B\",\"v\":[1.0,2.0],\"n\":{\"x\":5}}");

        Assert.True(a.EqualsWithin(DumpValue.FromJson("{\"n\":{\"x\":5.0004},\"go\":\"A/B\",\"v\":[1.0003,2.0]}"), 0.001, 0));
        Assert.False(a.EqualsWithin(DumpValue.FromJson("{\"go\":\"A/C\",\"v\":[1.0,2.0],\"n\":{\"x\":5}}"), 0.001, 0));
        Assert.False(a.EqualsWithin(DumpValue.FromJson("{\"go\":\"A/B\",\"v\":[1.0,2.0],\"n\":{\"x\":5},\"extra\":1}"), 0.001, 0));
        Assert.False(a.EqualsWithin(DumpValue.FromJson("{\"go\":\"A/B\",\"v\":[1.0],\"n\":{\"x\":5}}"), 0.001, 0));
    }

    [Fact]
    public void Text_form_is_short_and_culture_independent()
    {
        Assert.Equal("12.5", DumpValue.FromNumber(12.5).ToString());
        Assert.Equal("[1, 2.25, 3]", DumpValue.FromNumbers([1, 2.25, 3]).ToString());
        Assert.Equal("\"Idle\"", DumpValue.FromString("Idle").ToString());
        Assert.Equal("true", DumpValue.FromBool(true).ToString());
        Assert.Equal("null", DumpValue.Null.ToString());
        Assert.True(DumpValue.FromJson("[" + new string('x', 400) + "]").ToString().Length <= 160);
    }
}
