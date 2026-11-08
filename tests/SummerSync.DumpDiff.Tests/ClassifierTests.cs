using System.Text.Json;

namespace SummerSync.DumpDiff.Tests;

public class ClassifierTests
{
    private static FsmClassification Classify(Action<FsmBuilder> configure, Action<DumpBuilder>? dump = null)
    {
        var builder = new DumpBuilder();
        dump?.Invoke(builder);
        builder.Fsm("Car/Part", "Logic", configure: configure);
        DumpFile file = builder.Build();
        return Classifier.Classify(file.Fsms[0], file.Globals);
    }

    [Fact]
    public void An_fsm_without_actions_is_ignored_with_high_confidence()
    {
        FsmClassification result = Classify(f => f.State("Empty"));

        Assert.Equal((FsmClass.Ignore, "high"), (result.Class, result.Confidence));
    }

    [Fact]
    public void Only_flow_control_is_ignored_with_low_confidence()
    {
        FsmClassification result = Classify(f => f.Action("SendEvent", "StateMachine").Action("DebugLog", "Debug"));

        Assert.Equal((FsmClass.Ignore, "low"), (result.Class, result.Confidence));
    }

    [Fact]
    public void Only_presentation_actions_make_a_presentation_fsm()
    {
        FsmClassification result = Classify(f => f.Action("AudioPlay", "Audio").Action("SetGUIText", "GUI").Action("SendEvent", "StateMachine"));

        Assert.Equal((FsmClass.Presentation, "high"), (result.Class, result.Confidence));
        Assert.Equal(2, result.Presentation);
    }

    [Fact]
    public void World_actions_without_presentation_are_world_logic()
    {
        FsmClassification result = Classify(f => f.Action("AddForce", "Physics").Action("ActivateGameObject", "GameObject").Action("FloatAdd", "Math"));

        Assert.Equal((FsmClass.WorldLogic, "high"), (result.Class, result.Confidence));
        Assert.Equal((2, 1), (result.World, result.Logic));
    }

    [Fact]
    public void Calculations_only_are_world_logic_with_medium_confidence()
    {
        FsmClassification result = Classify(f => f.Action("FloatAdd", "Math").Action("FloatCompare", "Logic"));

        Assert.Equal((FsmClass.WorldLogic, "medium"), (result.Class, result.Confidence));
    }

    [Fact]
    public void World_plus_presentation_is_mixed()
    {
        FsmClassification result = Classify(f => f.Action("AddForce", "Physics").Action("AudioPlay", "Audio"));

        Assert.Equal(FsmClass.Mixed, result.Class);
    }

    [Fact]
    public void Local_input_driving_logic_is_mixed()
    {
        FsmClassification result = Classify(f => f.Action("GetKeyDown", "Input").Action("SetBoolValue", "Math"));

        Assert.Equal(FsmClass.Mixed, result.Class);
        Assert.Contains("input", result.Why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_sound_with_a_little_logic_is_presentation_but_not_sure()
    {
        FsmClassification result = Classify(f => f.Action("AudioPlay", "Audio").Action("SetAudioVolume", "Audio").Action("BoolTest", "Logic"));

        Assert.Equal((FsmClass.Presentation, "low"), (result.Class, result.Confidence));
    }

    [Fact]
    public void Mostly_calculations_with_some_presentation_is_mixed()
    {
        FsmClassification result = Classify(f => f.Action("AudioPlay", "Audio").Action("FloatAdd", "Math").Action("FloatMultiply", "Math").Action("FloatClamp", "Math"));

        Assert.Equal((FsmClass.Mixed, "low"), (result.Class, result.Confidence));
    }

    [Fact]
    public void Categories_added_by_the_game_are_recognised()
    {
        FsmClassification save = Classify(f => f.Action("SaveFloat", "Easy Save 2").Action("Wait", "Time"));
        Assert.Equal((FsmClass.WorldLogic, 1, 0), (save.Class, save.World, save.Logic));

        FsmClassification rebind = Classify(f => f.Action("CInputGetButton", "cInput").Action("ArrayListSet", "ArrayMaker/ArrayList"));
        Assert.Equal((FsmClass.Mixed, 1, 1), (rebind.Class, rebind.Input, rebind.Logic));

        FsmClassification achievement = Classify(f => f.Action("SteamAchievementUnlock", "steamworks.NET"));
        Assert.Equal(FsmClass.Presentation, achievement.Class);

        FsmClassification property = Classify(f => f.Action("SetProperty", "UnityObject"));
        Assert.Equal(FsmClass.WorldLogic, property.Class);
    }

    [Fact]
    public void Disabled_actions_do_not_count()
    {
        FsmClassification result = Classify(f => f.Action("AddForce", "Physics", isEnabled: false).Action("AudioPlay", "Audio"));

        Assert.Equal(FsmClass.Presentation, result.Class);
        Assert.Equal(0, result.World);
    }

    [Fact]
    public void Categories_are_listed_in_the_explanation()
    {
        FsmClassification result = Classify(f => f.Action("AddForce", "Physics").Action("FloatAdd", "Math").Action("Custom", ""));

        Assert.Contains("Math 1", result.Why);
        Assert.Contains("Physics 1", result.Why);
        Assert.Contains("(uncategorised) 1", result.Why);
    }

    [Fact]
    public void Finds_the_global_variables_an_fsm_touches()
    {
        FsmClassification result = Classify(
            f => f
                .Var("float", "LocalFuel", 1.0)
                .Action("FloatAdd", "Math", parameters: new()
                {
                    ["floatVariable"] = new Dictionary<string, object?> { ["var"] = "PlayerMoney", ["v"] = 10.0 },
                    ["add"] = new Dictionary<string, object?> { ["var"] = "LocalFuel", ["v"] = 1.0 },
                    ["other"] = new Dictionary<string, object?> { ["var"] = "NotDeclaredAnywhere", ["v"] = 0.0 },
                    ["list"] = new object[] { new Dictionary<string, object?> { ["var"] = "GlobalDay", ["v"] = 3 } },
                }),
            dump => dump.Global("float", "PlayerMoney", 10.0).Global("int", "GlobalDay", 3).Global("float", "Unused", 0.0));

        Assert.Equal(new[] { "GlobalDay", "PlayerMoney" }, result.Globals);
    }

    [Fact]
    public void Review_table_has_one_row_per_fsm_name_with_the_split()
    {
        DumpFile dump = new DumpBuilder()
            .Global("float", "PlayerMoney", 1.0)
            .Fsm("Shop/Till", "Use", configure: f => f.Action("GetKeyDown", "Input").Action("FloatAdd", "Math", parameters: new()
            {
                ["floatVariable"] = new Dictionary<string, object?> { ["var"] = "PlayerMoney", ["v"] = 1.0 },
            }))
            .Fsm("Car/Door", "Use", configure: f => f.Action("AddForce", "Physics"))
            .Fsm("Car/Radio", "Sound|FX", configure: f => f.Action("AudioPlay", "Audio"))
            .Build();

        string table = Classifier.ToReviewTable(dump, Classifier.Classify(dump));

        Assert.Contains("Totals: WorldLogic 1, Presentation 1, Mixed 1.", table);
        Assert.Contains("| 2 | Use | Mixed | Mixed 1, WorldLogic 1 | 1 | 1 / 1 / 0 | PlayerMoney | Car/Door |", table);
        Assert.Contains("| 1 | Sound\\|FX | Presentation |  | 0 | 0 / 0 / 1 |  | Car/Radio |", table);
        Assert.True(table.IndexOf("| 2 | Use", StringComparison.Ordinal) < table.IndexOf("| 1 | Sound", StringComparison.Ordinal));
    }

    [Fact]
    public void Draft_file_is_valid_json_with_totals_and_a_summary_by_name()
    {
        DumpFile dump = new DumpBuilder()
            .Fsm("A", "Use", configure: f => f.Action("AddForce", "Physics"))
            .Fsm("B", "Use", configure: f => f.Action("AddForce", "Physics"))
            .Fsm("C", "Sound", configure: f => f.Action("AudioPlay", "Audio"))
            .Build();

        string json = Classifier.ToJson(dump, Classifier.Classify(dump));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.StartsWith("DRAFT", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("totals").GetProperty("WorldLogic").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("Presentation").GetInt32());
        Assert.Equal("Use", root.GetProperty("byName")[0].GetProperty("name").GetString());
        Assert.Equal(2, root.GetProperty("byName")[0].GetProperty("count").GetInt32());
        Assert.Equal(3, root.GetProperty("fsms").GetArrayLength());
        Assert.Equal("WorldLogic", root.GetProperty("fsms")[0].GetProperty("class").GetString());
    }
}
