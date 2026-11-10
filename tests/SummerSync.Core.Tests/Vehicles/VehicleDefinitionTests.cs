using SummerSync.Core.Json;
using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class JsonReaderTests
{
    [Fact]
    public void Reads_objects_arrays_texts_numbers_and_the_three_words()
    {
        var root = JsonReader.Object(JsonReader.Parse("""
            { "name": "van \"one\"\n", "seats": 2, "ratio": -1.5e1, "open": true, "shut": false, "nothing": null,
              "box": [0.5, [1, 2], {}], "empty": [] }
            """), "The test");

        Assert.Equal("van \"one\"\n", root["name"]);
        Assert.Equal(2.0, root["seats"]);
        Assert.Equal(-15.0, root["ratio"]);
        Assert.Equal(true, root["open"]);
        Assert.Equal(false, root["shut"]);
        Assert.Null(root["nothing"]);
        var box = JsonReader.Array(root["box"], "box");
        Assert.Equal(0.5, box[0]);
        Assert.Equal(new object[] { 1.0, 2.0 }, JsonReader.Array(box[1], "inner"));
        Assert.Empty(JsonReader.Object(box[2], "inner"));
        Assert.Empty(JsonReader.Array(root["empty"], "empty"));
        Assert.Equal("é/", JsonReader.Parse("\"\\u00e9\\/\""));
    }

    [Theory]
    [InlineData("", "ends where a value should be (line 1, column 1)")]
    [InlineData("{ \"a\": 1,\n  \"b\" 2 }", "':' should follow the property name (line 2, column 7)")]
    [InlineData("[1, 2", "A ',' or a ']' should be here (line 1, column 6)")]
    [InlineData("{ \"a\": tru }", "Unexpected 't' (line 1, column 8)")]
    [InlineData("\"open", "never closed (line 1, column 1)")]
    [InlineData("1 2", "Something follows the end of the value (line 1, column 3)")]
    [InlineData("{ a: 1 }", "A property name should start here (line 1, column 3)")]
    [InlineData("1.2.3", "Not a number (line 1, column 1)")]
    public void Says_where_a_text_stops_being_json(string text, string expected)
    {
        var error = Assert.Throws<FormatException>(() => JsonReader.Parse(text));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Nesting_without_end_is_refused_instead_of_overflowing_the_stack()
    {
        Assert.Throws<FormatException>(() => JsonReader.Parse(new string('[', 100000)));
    }
}

public class VehicleDefinitionTests
{
    private const string Van = """
        {
          "format": 2,
          "nick": "van",
          "rootName": "HAYOSIKO(1500kg, 250)",
          "driveTriggerPath": "LOD/PlayerTrigger/DriveTrigger",
          "driveMachineName": "PlayerTrigger",
          "keyVariable": "PlayerKeyHayosiko",
          "interior": { "centre": [0.0, 0.95, -0.15], "halfSize": [0.62, 0.62, 1.95] },
          "exitMargin": 0.25,
          "lights": { "switchPath": "LOD/Dashboard/Knobs/Lights", "switchMachine": "Use", "powerPath": "LOD/Electricity/PowerON" },
          "engine": { "starterPath": "Starter", "starterMachine": "Starter", "ignitionPath": "LOD/Dashboard/Knobs/Ignition", "ignitionMachine": "Use", "detailPath": "LOD" },
          "knobs": [
            { "name": "wipers", "path": "LOD/Dashboard/Knobs/ButtonWipers", "machine": "Use", "variable": "Selection", "states": ["Off", "Slow", "Fast"] },
            { "name": "radio volume", "path": "RadioPivot/Radio/ButtonsRadio/RadioVolume", "machine": "Knob", "variable": "Volume", "step": 0.1, "steps": 10, "states": ["Off", "Check tune"] },
            { "name": "radio tuner", "path": "RadioPivot/Radio/TunerPivot/RadioTuner", "machine": "Knob", "variable": "Tune", "from": 0.05, "step": 0.001, "steps": 60, "states": ["Wait1"] },
            { "name": "parking brake", "path": "LOD/Dashboard/ParkingBrake", "machine": "Use", "variable": "KnobPos", "step": 0.01, "steps": 10 }
          ]
        }
        """;

    [Fact]
    public void A_vehicle_file_says_what_the_vehicle_is_called_and_where_its_inside_is()
    {
        VehicleDefinition van = VehicleDefinition.Parse(Van);

        Assert.Equal("HAYOSIKO(1500kg, 250)", van.RootName);
        Assert.Equal("LOD/PlayerTrigger/DriveTrigger", van.DriveTriggerPath);
        Assert.Equal("PlayerTrigger", van.DriveMachineName);
        Assert.Equal("van", van.Nick);
        Assert.Equal("PlayerKeyHayosiko", van.KeyVariable);
        Assert.True(van.HasInterior);
        Assert.Equal("LOD/Dashboard/Knobs/Lights", van.LightSwitchPath);
        Assert.Equal("Use", van.LightSwitchMachine);
        Assert.Equal("LOD/Electricity/PowerON", van.PowerPath);
        Assert.Equal("Starter", van.StarterPath);
        Assert.Equal("Starter", van.StarterMachine);
        Assert.Equal("LOD/Dashboard/Knobs/Ignition", van.IgnitionPath);
        Assert.Equal("Use", van.IgnitionMachine);
        Assert.Equal("LOD", van.DetailPath);
        Assert.Equal(0.25f, van.ExitMargin);

        // On the floor in the middle of the load space, and at the driver's seat.
        Assert.True(van.Interior.Contains(0f, 0.62f, -1f, 0f));
        Assert.True(van.Interior.Contains(-0.33f, 0.9f, 0.8f, 0f));

        // Outside by the side door: not in, and still not in with the margin that getting off takes.
        Assert.False(van.Interior.Contains(-1.0f, 0.62f, -0.1f, 0f));
        Assert.False(van.Interior.Contains(-1.0f, 0.62f, -0.1f, van.ExitMargin));

        // In the doorway: out, but not yet far enough to count as off.
        Assert.False(van.Interior.Contains(-0.7f, 0.62f, -0.1f, 0f));
        Assert.True(van.Interior.Contains(-0.7f, 0.62f, -0.1f, van.ExitMargin));

        // On the roof, and behind the rear door.
        Assert.False(van.Interior.Contains(0f, 2.0f, 0f, van.ExitMargin));
        Assert.False(van.Interior.Contains(0f, 0.62f, -2.5f, van.ExitMargin));

        // A thing against a front door is on board: grown to the sides, but not through the floor or the roof.
        Assert.True(van.Interior.Contains(0.8f, 0.5f, 1.2f, 0.25f, 0f));
        Assert.True(van.Interior.Contains(0f, 0.5f, 2.0f, 0.25f, 0f));
        Assert.False(van.Interior.Contains(0.8f, 0.2f, 1.2f, 0.25f, 0f));
        Assert.False(van.Interior.Contains(0f, 1.7f, 0f, 0.25f, 0f));
        Assert.True(van.Interior.Contains(0f, 1.7f, 0f, 0.25f, 0.25f));
    }

    [Theory]
    [InlineData("\"format\": 2", "\"format\": 1", "format 1 cannot be read")]
    [InlineData("\"nick\": \"van\"", "\"nick\": \"The Van\"", "a nick in lower case without spaces")]
    [InlineData("\"switchMachine\": \"Use\", ", "", "needs a text \"switchMachine\"")]
    [InlineData("\"keyVariable\": \"PlayerKeyHayosiko\"", "\"keyVariable\": 7", "\"keyVariable\" that is not a text")]
    [InlineData("\"rootName\": \"HAYOSIKO(1500kg, 250)\",", "", "needs a text \"rootName\"")]
    [InlineData("[0.62, 0.62, 1.95]", "[0.62, 0.62]", "\"halfSize\" as an array of 3 numbers")]
    [InlineData("[0.62, 0.62, 1.95]", "[0.62, 0, 1.95]", "interior has no size")]
    [InlineData("\"exitMargin\": 0.25", "\"exitMargin\": -1", "negative exit margin")]
    [InlineData("\"interior\": {", "\"interior\": 3, \"was\": {", "\"interior\" must be an object")]
    [InlineData("\"starterMachine\": \"Starter\", ", "", "needs a text \"starterMachine\"")]
    [InlineData("\"ignitionMachine\": \"Use\", ", "", "an ignition lock without its state machine")]
    public void A_vehicle_file_that_is_wrong_is_refused_with_the_reason(string from, string to, string expected)
    {
        Assert.Contains(from, Van);
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(Van.Replace(from, to)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_vehicle_may_have_no_key_no_inside_and_no_lights()
    {
        VehicleDefinition moped = VehicleDefinition.Parse("""
            { "format": 2, "nick": "moped", "rootName": "JONNEZ ES(Clone)",
              "driveTriggerPath": "LOD/PlayerTrigger/DriveTrigger", "driveMachineName": "PlayerTrigger", "keyVariable": "" }
            """);

        Assert.Null(moped.KeyVariable);
        Assert.False(moped.HasInterior);
        Assert.Null(moped.LightSwitchPath);
        Assert.Null(moped.PowerPath);
        Assert.Null(moped.StarterPath);
        Assert.Null(moped.IgnitionPath);
        Assert.Empty(moped.Knobs);
        Assert.Equal(0.25f, moped.ExitMargin);
    }

    [Fact]
    public void A_knob_is_a_position_from_zero_up_and_the_states_that_show_each()
    {
        VehicleDefinition van = VehicleDefinition.Parse(Van);

        Assert.Equal(4, van.Knobs.Length);
        VehicleKnob wipers = van.Knobs[0];
        Assert.Equal("wipers", wipers.Name);
        Assert.Equal("LOD/Dashboard/Knobs/ButtonWipers", wipers.Path);
        Assert.Equal("Use", wipers.Machine);
        Assert.Equal("Selection", wipers.Variable);
        Assert.Equal(2, wipers.Highest);
        Assert.Equal(2, wipers.Bits);
        Assert.Equal(0f, wipers.Step);
        Assert.Equal("Off", wipers.StateFor(0));
        Assert.Equal("Fast", wipers.StateFor(2));
    }

    [Fact]
    public void A_knob_that_turns_through_a_range_has_steps()
    {
        VehicleDefinition van = VehicleDefinition.Parse(Van);
        VehicleKnob volume = van.Knobs[1];
        VehicleKnob tuner = van.Knobs[2];

        Assert.Equal(10, volume.Highest);
        Assert.Equal(4, volume.Bits);
        Assert.Equal("Off", volume.StateFor(0));
        Assert.Equal("Check tune", volume.StateFor(1));
        Assert.Equal("Check tune", volume.StateFor(10));

        // Ten steps of a tenth, added up by the game as floats.
        Assert.Equal(7, volume.PositionOf(0.70000005f));
        Assert.Equal(0, volume.PositionOf(-0.2f));
        Assert.Equal(10, volume.PositionOf(1.3f));
        Assert.Equal(0.3f, volume.ValueAt(3), 5);

        Assert.Equal(60, tuner.Highest);
        Assert.Equal(6, tuner.Bits);
        Assert.Equal("Wait1", tuner.StateFor(41));
        Assert.Equal(41, tuner.PositionOf(0.091f));
        Assert.Equal(0.091f, tuner.ValueAt(41), 5);
        Assert.Equal(21, tuner.PositionOf(0.0709999f));
    }

    [Fact]
    public void A_knob_that_the_game_shows_from_its_variable_needs_no_states()
    {
        VehicleKnob brake = VehicleDefinition.Parse(Van).Knobs[3];

        Assert.Equal("parking brake", brake.Name);
        Assert.Empty(brake.States);
        Assert.Null(brake.StateFor(0));
        Assert.Null(brake.StateFor(10));
        Assert.Equal(10, brake.Highest);
        Assert.Equal(4, brake.Bits);

        // The lever goes from 0 to 0.1 at half a unit per second: any value on the way is the nearest step.
        Assert.Equal(0, brake.PositionOf(0.004f));
        Assert.Equal(1, brake.PositionOf(0.0051f));
        Assert.Equal(10, brake.PositionOf(0.1f));
        Assert.Equal(0.1f, brake.ValueAt(10), 5);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(255, 8)]
    public void A_position_takes_as_many_switches_as_its_highest_value_has_bits(int highest, int bits)
    {
        VehicleDefinition van = VehicleDefinition.Parse(Van.Replace("\"step\": 0.1, \"steps\": 10", "\"step\": 0.1, \"steps\": " + highest));
        Assert.Equal(bits, van.Knobs[1].Bits);
    }

    [Theory]
    [InlineData("\"states\": [\"Off\", \"Slow\", \"Fast\"]", "\"states\": [\"Off\"]", "at least two positions")]
    [InlineData("\"states\": [\"Off\", \"Slow\", \"Fast\"]", "\"states\": [\"Off\", 3]", "a state that is not a name")]
    [InlineData("\"name\": \"radio tuner\"", "\"name\": \"wipers\"", "two knobs called 'wipers'")]
    [InlineData("\"step\": 0.1, \"steps\": 10", "\"step\": 0.1, \"steps\": 0", "from 1 to 255 steps")]
    [InlineData("\"variable\": \"Selection\", ", "", "either a variable or the states it is in at each position")]
    [InlineData("\"variable\": \"Selection\", \"states\": [\"Off\", \"Slow\", \"Fast\"]", "\"variable\": \"Selection\"", "needs \"states\"")]
    public void A_knob_that_is_wrong_is_refused_with_the_reason(string from, string to, string expected)
    {
        Assert.Contains(from, Van);
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(Van.Replace(from, to)));
        Assert.Contains(expected, error.Message);
    }

    private const string Signals = """
        { "format": 2, "nick": "van", "rootName": "HAYOSIKO(1500kg, 250)",
          "driveTriggerPath": "LOD/PlayerTrigger/DriveTrigger", "driveMachineName": "PlayerTrigger",
          "knobs": [
            { "name": "turn signals", "path": "LOD/Dashboard/TurnSignals", "machine": "Usage", "by": "simulator", "states": ["State 3", "Activate dash", "Activate dash 2"],
              "during": [["Check player", "State 2", "Check steering", "State 3"], ["Activate dash", "On", "Off"], ["Activate dash 2", "On 2", "Off 2"]] },
            { "name": "diff lock", "path": "LOD/Dashboard/DiffLock", "machine": "Use", "variable": "DiffLock", "toggles": true, "states": ["Flip", "Flip"] },
            { "name": "hand throttle", "path": "LOD/Dashboard/Throttle", "machine": "Use", "variable": "LeverPos", "step": 1, "steps": 70,
              "copies": [ { "machine": "Throttle", "variable": "Throttle", "times": 0.0066667 }, { "path": "Other", "machine": "M", "variable": "V", "times": 14.8, "min": 0.13, "max": 1 } ],
              "moves": { "path": "LOD/Dashboard/Throttle/Pivot", "what": "rotation", "axis": "x" } }
          ],
          "horn": { "path": "LOD/Dashboard/ButtonHorn/CarHorn", "buttons": [
            { "path": "LOD/Dashboard/ButtonHorn", "machine": "Use", "pressed": "State 1" },
            { "under": "gt steering wheel(Clone)", "machine": "Horn", "pressed": "State 1" } ] } }
        """;

    [Fact]
    public void A_knob_may_be_read_from_its_machines_state_and_be_told_only_by_the_simulating_machine()
    {
        VehicleKnob signals = VehicleDefinition.Parse(Signals).Knobs[0];

        Assert.Null(signals.Variable);
        Assert.True(signals.BySimulator);
        Assert.Equal(2, signals.Highest);
        Assert.Equal(2, signals.Bits);
        Assert.Equal("Activate dash", signals.StateFor(1));
        Assert.Equal(0, signals.PositionDuring("Check player"));
        Assert.Equal(1, signals.PositionDuring("Off"));
        Assert.Equal(2, signals.PositionDuring("On 2"));
        Assert.Equal(-1, signals.PositionDuring("Something else"));
        Assert.Equal(-1, signals.PositionDuring(null));

        // A knob with a variable is told by whoever turns it, and has nothing to read from states.
        VehicleKnob wipers = VehicleDefinition.Parse(Van).Knobs[0];
        Assert.False(wipers.BySimulator);
        Assert.Null(wipers.During);
        Assert.Equal(-1, wipers.PositionDuring("Off"));
        Assert.False(wipers.Toggles);
        Assert.Empty(wipers.Copies);
        Assert.Null(wipers.Moves);
    }

    [Fact]
    public void A_knob_may_flip_its_variable_on_the_way_and_keep_its_float_in_copies_and_an_object()
    {
        VehicleDefinition van = VehicleDefinition.Parse(Signals);
        VehicleKnob diff = van.Knobs[1];
        VehicleKnob throttle = van.Knobs[2];

        Assert.True(diff.Toggles);
        Assert.Equal(1, diff.Highest);

        Assert.Equal(70, throttle.Highest);
        Assert.Equal(2, throttle.Copies.Length);
        Assert.Null(throttle.Copies[0].Path);
        Assert.Equal("Throttle", throttle.Copies[0].Machine);
        Assert.Equal(70f / 150f, throttle.Copies[0].Of(70f), 3);
        Assert.Equal("Other", throttle.Copies[1].Path);
        Assert.Equal(0.13f, throttle.Copies[1].Of(0.008f));
        Assert.Equal(0.444f, throttle.Copies[1].Of(0.03f), 3);
        Assert.Equal(1f, throttle.Copies[1].Of(1f));
        Assert.Equal("LOD/Dashboard/Throttle/Pivot", throttle.Moves.Path);
        Assert.False(throttle.Moves.Slides);
        Assert.Equal(0, throttle.Moves.Axis);
        Assert.Equal(35f, throttle.Moves.Of(35f));
    }

    [Fact]
    public void A_horn_is_what_sounds_and_the_buttons_held_to_sound_it()
    {
        VehicleHorn horn = VehicleDefinition.Parse(Signals).Horn;

        Assert.Equal("LOD/Dashboard/ButtonHorn/CarHorn", horn.Path);
        Assert.Equal(2, horn.Buttons.Length);
        Assert.Null(horn.Buttons[0].Under);
        Assert.Equal("LOD/Dashboard/ButtonHorn", horn.Buttons[0].Path);
        Assert.Equal("Use", horn.Buttons[0].Machine);
        Assert.Equal("State 1", horn.Buttons[0].Pressed);
        Assert.Equal("gt steering wheel(Clone)", horn.Buttons[1].Under);
        Assert.Null(horn.Buttons[1].Path);

        Assert.Null(VehicleDefinition.Parse(Van).Horn);
    }

    [Theory]
    [InlineData("\"by\": \"simulator\"", "\"by\": \"driver\"", "it can be anyone or simulator")]
    [InlineData("[\"Activate dash 2\", \"On 2\", \"Off 2\"]]", "[\"Activate dash 2\", \"On 2\", \"Off\"]]", "is in two positions")]
    [InlineData(", [\"Activate dash 2\", \"On 2\", \"Off 2\"]]", "]", "says what it is during 2 positions")]
    [InlineData("\"machine\": \"Usage\",", "\"machine\": \"Usage\", \"variable\": \"Blink\",", "either a variable or the states")]
    [InlineData("\"toggles\": true, \"states\": [\"Flip\", \"Flip\"]", "\"toggles\": true, \"states\": [\"A\", \"B\", \"C\"]", "a variable of two positions")]
    [InlineData("\"toggles\": true", "\"toggles\": 1", "not true or false")]
    [InlineData("\"what\": \"rotation\"", "\"what\": \"scale\"", "it can be rotation or position")]
    [InlineData("\"axis\": \"x\" }", "\"axis\": \"q\" }", "it can be x, y or z")]
    [InlineData("\"min\": 0.13, \"max\": 1", "\"min\": 2, \"max\": 1", "least value no greater than its most")]
    [InlineData("\"variable\": \"DiffLock\", \"toggles\": true, \"states\": [\"Flip\", \"Flip\"]", "\"variable\": \"DiffLock\", \"states\": [\"Flip\", \"Flip\"], \"moves\": { \"path\": \"P\", \"what\": \"rotation\", \"axis\": \"x\" }", "it needs a \"step\"")]
    [InlineData("\"pressed\": \"State 1\" },", "\"pressed\": \"\" },", "the state it is in while held")]
    [InlineData("{ \"under\": \"gt steering wheel(Clone)\", ", "{ ", "a path (or a part it is on)")]
    [InlineData("\"buttons\": [", "\"was\": [", "at least one button")]
    public void A_state_read_knob_a_copy_or_a_horn_that_is_wrong_is_refused_with_the_reason(string from, string to, string expected)
    {
        string text = Signals.Replace("\r\n", "\n");
        Assert.Contains(from, text);
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(text.Replace(from, to)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Something_only_towed_has_no_drive_trigger()
    {
        VehicleDefinition flatbed = VehicleDefinition.Parse("""
            { "format": 2, "nick": "flatbed", "rootName": "FLATBED" }
            """);

        Assert.False(flatbed.CanBeDriven);
        Assert.Null(flatbed.DriveTriggerPath);
        Assert.Null(flatbed.DriveMachineName);
        Assert.True(VehicleDefinition.Parse(Van).CanBeDriven);

        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse("""
            { "format": 2, "nick": "flatbed", "rootName": "FLATBED", "driveTriggerPath": "LOD/PlayerTrigger/DriveTrigger" }
            """));
        Assert.Contains("a drive trigger without its state machine", error.Message);
    }

    [Fact]
    public void An_engine_may_be_started_without_an_ignition_lock()
    {
        VehicleDefinition moped = VehicleDefinition.Parse("""
            { "format": 2, "nick": "moped", "rootName": "JONNEZ ES(Clone)",
              "driveTriggerPath": "LOD/PlayerTrigger/DriveTrigger", "driveMachineName": "PlayerTrigger",
              "engine": { "starterPath": "Starter", "starterMachine": "Starter" } }
            """);

        Assert.Equal("Starter", moped.StarterPath);
        Assert.Equal("Starter", moped.StarterMachine);
        Assert.Null(moped.IgnitionPath);
        Assert.Null(moped.IgnitionMachine);
        Assert.Null(moped.DetailPath);
    }

    [Fact]
    public void A_knob_or_a_light_switch_can_sit_on_a_part_and_a_starter_can_run_in_a_state_of_its_own()
    {
        VehicleDefinition car = VehicleDefinition.Parse("""
            { "format": 2, "nick": "car", "rootName": "SATSUMA(557kg, 248)",
              "driveTriggerPath": "PlayerTrigger/DriveTrigger", "driveMachineName": "PlayerTrigger",
              "lights": { "switchPath": "Knobs/ButtonsDash/LightModes", "switchUnder": "dashboard meters(Clone)", "switchMachine": "Use" },
              "engine": { "starterPath": "CarSimulation/Car/Starter", "starterMachine": "Starter", "runningState": "Running" },
              "knobs": [
                { "name": "wipers", "under": "dashboard meters(Clone)", "path": "Knobs/ButtonsDash/ButtonWipers", "machine": "Use", "variable": "Selection", "states": ["Off", "Slow", "Fast"] },
                { "name": "interior light", "path": "Interior/InteriorLight", "machine": "Use", "variable": "LightON", "states": ["State 3", "State 2"] }
              ] }
            """);

        Assert.Equal("dashboard meters(Clone)", car.LightSwitchUnder);
        Assert.Equal("Knobs/ButtonsDash/LightModes", car.LightSwitchPath);
        Assert.Equal("Running", car.RunningState);
        Assert.Equal("dashboard meters(Clone)", car.Knobs[0].Under);
        Assert.Null(car.Knobs[1].Under);

        VehicleDefinition van = VehicleDefinition.Parse(Van);
        Assert.Equal(VehicleDefinition.DefaultRunningState, van.RunningState);
        Assert.Equal("Motor running", van.RunningState);
        Assert.Null(van.LightSwitchUnder);
        Assert.All(van.Knobs, k => Assert.Null(k.Under));
    }

    private const string Driver = """
        { "format": 2, "nick": "van", "rootName": "HAYOSIKO(1500kg, 250)",
          "driveTriggerPath": "LOD/PlayerTrigger/DriveTrigger", "driveMachineName": "PlayerTrigger",
          "driver": {
            "pedals": [
              { "input": "clutch", "path": "LOD/Dashboard/Pedals 1/clutch", "axis": "x", "degrees": 9 },
              { "input": "brake", "path": "LOD/Dashboard/Pedals 1/brake", "axis": "x", "degrees": 9 },
              { "input": "throttle", "path": "LOD/Dashboard/Pedals 1/throttle", "axis": "y", "degrees": -8 }
            ],
            "levers": ["LOD/Dashboard/GearShifter/lever"],
            "seat": [-0.33, 0.4, 0.8, 0.9]
          } }
        """;

    [Fact]
    public void A_driver_has_pedals_that_turn_by_the_input_a_gear_lever_and_a_seat()
    {
        VehicleDefinition van = VehicleDefinition.Parse(Driver);

        Assert.Equal(3, van.Pedals.Length);
        Assert.Equal(DriverInput.Clutch, van.Pedals[0].Input);
        Assert.Equal("LOD/Dashboard/Pedals 1/clutch", van.Pedals[0].Path);
        Assert.Equal(0, van.Pedals[0].Axis);
        Assert.Equal(9f, van.Pedals[0].Degrees);
        Assert.Equal(DriverInput.Brake, van.Pedals[1].Input);
        Assert.Equal(DriverInput.Throttle, van.Pedals[2].Input);
        Assert.Equal(1, van.Pedals[2].Axis);
        Assert.Equal(-8f, van.Pedals[2].Degrees);
        Assert.Equal(new[] { "LOD/Dashboard/GearShifter/lever" }, van.Levers);
        Assert.Null(van.Grips);
        Assert.Equal(new[] { -0.33f, 0.4f, 0.8f, 0.9f }, van.Seat);

        // Without the section: nothing to press, no lever, no seat measured.
        VehicleDefinition plain = VehicleDefinition.Parse(Van);
        Assert.Empty(plain.Pedals);
        Assert.Empty(plain.Levers);
        Assert.Null(plain.Grips);
        Assert.Null(plain.Seat);
    }

    [Fact]
    public void A_handlebar_is_held_by_two_grips_the_left_one_first()
    {
        VehicleDefinition moped = VehicleDefinition.Parse("""
            { "format": 2, "nick": "moped", "rootName": "JONNEZ ES(Clone)",
              "driveTriggerPath": "LOD/PlayerTrigger/DriveTrigger", "driveMachineName": "PlayerTrigger",
              "driver": { "grips": ["Bar/Clutch", "Bar/Throttle"],
                "pedals": [ { "input": "throttle", "path": "Bar/Throttle", "axis": "x", "degrees": -100 } ] } }
            """);

        Assert.Equal(new[] { "Bar/Clutch", "Bar/Throttle" }, moped.Grips);
        Assert.Empty(moped.Levers);
        Assert.Single(moped.Pedals);
    }

    [Theory]
    [InlineData("\"input\": \"clutch\"", "\"input\": \"horn\"", "it can be clutch, brake or throttle")]
    [InlineData("\"axis\": \"y\"", "\"axis\": \"w\"", "it can be x, y or z")]
    [InlineData("\"input\": \"brake\"", "\"input\": \"clutch\"", "two pedals for the clutch")]
    [InlineData("Pedals 1/clutch\", \"axis\": \"x\", \"degrees\": 9", "Pedals 1/clutch\", \"axis\": \"x\", \"degrees\": 0", "from 1 to 180 degrees")]
    [InlineData("\"degrees\": -8", "\"degrees\": 200", "from 1 to 180 degrees")]
    [InlineData("\"levers\": [\"LOD/Dashboard/GearShifter/lever\"]", "\"levers\": [\"\"]", "something that is not a path")]
    [InlineData("\"levers\": [\"LOD/Dashboard/GearShifter/lever\"]", "\"grips\": [\"Bar\"]", "needs two grips")]
    [InlineData("[-0.33, 0.4, 0.8, 0.9]", "[-0.33, 0.4, 0.8, 0]", "eyes above the feet")]
    [InlineData("[-0.33, 0.4, 0.8, 0.9]", "[-0.33, 0.4, 0.8]", "as an array of 4 numbers")]
    public void A_driver_that_is_wrong_is_refused_with_the_reason(string from, string to, string expected)
    {
        string text = Driver.Replace("\r\n", "\n");
        Assert.Contains(from, text);
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(text.Replace(from, to)));
        Assert.Contains(expected, error.Message);
    }

    private const string Seats = """
        { "format": 2, "nick": "car", "rootName": "SATSUMA(557kg, 248)",
          "driveTriggerPath": "PlayerTrigger/DriveTrigger", "driveMachineName": "PlayerTrigger",
          "seats": [
            { "name": "front", "player": [0.28, -0.02, -0.13], "needs": ["Interior/pivot_seat_passenger"], "belt": "handle", "beltPath": "Interior/Seatbelts" },
            { "name": "rear left", "player": [-0.3, 0.02, -0.72], "reach": 0.6 }
          ] }
        """;

    [Fact]
    public void A_seat_is_where_a_passenger_sits_what_it_needs_and_the_belt_it_copies()
    {
        VehicleDefinition car = VehicleDefinition.Parse(Seats);

        Assert.Equal(2, car.Seats.Length);
        Assert.Equal("front", car.Seats[0].Name);
        Assert.Equal(new[] { 0.28f, -0.02f, -0.13f }, car.Seats[0].Player);
        Assert.Equal(VehicleDefinition.DefaultSeatReach, car.Seats[0].Reach);
        Assert.Equal(new[] { "Interior/pivot_seat_passenger" }, car.Seats[0].Needs);
        Assert.Equal(SeatBelt.Handle, car.Seats[0].Belt);
        Assert.Equal("Interior/Seatbelts", car.Seats[0].BeltPath);
        Assert.Equal(0.6f, car.Seats[1].Reach);
        Assert.Empty(car.Seats[1].Needs);
        Assert.Equal(SeatBelt.None, car.Seats[1].Belt);
        Assert.Null(car.Seats[1].BeltPath);

        // Without the section, no seat.
        Assert.Empty(VehicleDefinition.Parse(Van).Seats);
    }

    [Theory]
    [InlineData("\"name\": \"rear left\"", "\"name\": \"front\"", "two seats called 'front'")]
    [InlineData("\"name\": \"rear left\"", "\"name\": \"\"", "needs a name")]
    [InlineData("\"reach\": 0.6", "\"reach\": 0", "a reach from above 0")]
    [InlineData("\"belt\": \"handle\"", "\"belt\": \"rope\"", "it can be handle, buckle or none")]
    [InlineData(", \"beltPath\": \"Interior/Seatbelts\"", "", "names a belt without the driver's")]
    [InlineData("[-0.3, 0.02, -0.72]", "[-0.3, 0.02]", "as an array of 3 numbers")]
    public void A_seat_that_is_wrong_is_refused_with_the_reason(string from, string to, string expected)
    {
        string text = Seats.Replace("\r\n", "\n");
        Assert.Contains(from, text);
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(text.Replace(from, to)));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_head_holds_what_the_games_machine_gives_a_drivers()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "vehicles");
        var vehicles = Directory.GetFiles(folder, "*.json").Select(f => VehicleDefinition.Parse(File.ReadAllText(f))).ToDictionary(v => v.Nick);

        // "HeadForce": the base, plus the belt, times 1.5 with a helmet (dump of 2026-10-15; the Ferndale's lap belt measured).
        Assert.Equal(300f, vehicles["van"].HeadHolds(false, false));
        Assert.Equal(500f, vehicles["van"].HeadHolds(true, false));
        Assert.Equal(750f, vehicles["van"].HeadHolds(true, true));
        Assert.Equal(500f, vehicles["gifu"].HeadHolds(false, false));
        Assert.Equal(700f, vehicles["ferndale"].HeadHolds(true, false));
        Assert.Equal(450f, vehicles["satsuma"].HeadHolds(false, true));

        // "HelmetSim": one force without a helmet and another with one; a belt changes nothing.
        Assert.Equal(300f, vehicles["ruscko"].HeadHolds(true, false));
        Assert.Equal(500f, vehicles["ruscko"].HeadHolds(false, true));
        Assert.Equal(800f, vehicles["kekmet"].HeadHolds(false, false));
        Assert.Equal(1100f, vehicles["jonnez"].HeadHolds(true, true));

        Assert.All(vehicles.Values.Where(v => v.CanBeDriven), v => Assert.True(v.CrashHead > 0f, v.Nick));
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(Van.Replace("\"interior\"", "\"crash\": { \"head\": 300, \"belt\": 200, \"helmet\": 500 }, \"interior\"")));
        Assert.Contains("a helmet's newtons (without a belt)", error.Message);
    }

    [Fact]
    public void Every_vehicle_with_a_passenger_seat_has_it_beside_or_behind_the_driver_and_inside_it()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "vehicles");
        var withSeats = new List<string>();
        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            VehicleDefinition vehicle = VehicleDefinition.Parse(File.ReadAllText(file));
            foreach (VehicleSeat seat in vehicle.Seats)
            {
                withSeats.Add(vehicle.Nick);

                // Not where the driver sits: a metre of the measured feet would put two people on one seat.
                float across = seat.Player[0] - vehicle.Seat![0];
                float along = seat.Player[2] - vehicle.Seat[2];
                if (seat.Name == "roof")
                {
                    // On top of it, over the driver if need be: above the inside, and offered from the ground beside it.
                    Assert.False(vehicle.Interior.Contains(seat.Player[0], seat.Player[1], seat.Player[2], 0f), file + " " + seat.Name);
                    Assert.True(seat.Below > seat.Player[1], file + " " + seat.Name);
                }
                else
                {
                    Assert.True((across * across) + (along * along) > 0.25f * 0.25f, file + " " + seat.Name);
                }

                if (seat.Name != "roof" && vehicle.HasInterior)
                {
                    Assert.Equal(VehicleDefinition.DefaultSeatBelow, seat.Below);
                    Assert.True(vehicle.Interior.Contains(seat.Player[0], seat.Player[1], seat.Player[2], 0.1f), file + " " + seat.Name);
                }
            }
        }

        // The tractor has no seat inside but the driver's: whoever rides it stands, or sits on its roof.
        Assert.Equal(1, withSeats.Count(n => n == "kekmet"));
        Assert.Contains("van", withSeats);
        Assert.Contains("jonnez", withSeats);
        Assert.Equal(3, withSeats.Count(n => n == "satsuma"));
        Assert.Equal(3, withSeats.Count(n => n == "ferndale"));
    }

    [Fact]
    public void The_satsuma_file_names_what_was_verified_in_the_dump_and_the_level_file()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "data", "vehicles", "satsuma.json");
        VehicleDefinition satsuma = VehicleDefinition.Parse(File.ReadAllText(file));

        Assert.Equal("SATSUMA(557kg, 248)", satsuma.RootName);
        Assert.Equal("satsuma", satsuma.Nick);
        Assert.Equal("PlayerTrigger/DriveTrigger", satsuma.DriveTriggerPath);
        Assert.Equal("PlayerKeySatsuma", satsuma.KeyVariable);
        Assert.Equal("Running", satsuma.RunningState);
        Assert.Equal("UseNew", satsuma.IgnitionMachine);
        Assert.Null(satsuma.DetailPath);

        // Both front seats and the back seat are inside; the driver's door handle and the roof are not.
        Assert.True(satsuma.Interior.Contains(-0.30f, 0.05f, 0.05f, 0f));
        Assert.True(satsuma.Interior.Contains(0.30f, 0.05f, 0.0f, 0f));
        Assert.True(satsuma.Interior.Contains(0f, 0.10f, -0.66f, 0f));
        Assert.False(satsuma.Interior.Contains(-0.95f, 0.05f, 0.05f, satsuma.ExitMargin));
        Assert.False(satsuma.Interior.Contains(0f, 1.2f, 0f, satsuma.ExitMargin));

        Assert.Contains(satsuma.Knobs, k => k.Name == "wipers" && k.Under == "dashboard meters(Clone)");
        Assert.Contains(satsuma.Knobs, k => k.Name == "parking brake" && k.Under == null && k.Highest == 10);
    }

    [Fact]
    public void Every_vehicle_file_says_where_its_driver_sits_and_what_they_work()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "vehicles");
        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            VehicleDefinition vehicle = VehicleDefinition.Parse(File.ReadAllText(file));
            if (!vehicle.CanBeDriven)
            {
                // The flatbed is only towed.
                Assert.Equal("flatbed", vehicle.Nick);
                continue;
            }

            Assert.True(vehicle.Seat != null, file);
            Assert.True(vehicle.Seat![3] > 0.5f && vehicle.Seat[3] < 1.2f, file);
            if (vehicle.Nick == "boat")
            {
                // Steered with the outboard's tiller: nothing to press with the feet.
                Assert.Empty(vehicle.Pedals);
                continue;
            }

            Assert.Contains(vehicle.Pedals, p => p.Input == DriverInput.Throttle);
            Assert.Contains(vehicle.Pedals, p => p.Input == DriverInput.Brake);

            // The moped is steered with a handlebar, and only it; the automatic Ferndale and the moped have no gear lever.
            bool moped = vehicle.Nick == "jonnez";
            Assert.Equal(moped, vehicle.Grips != null);
            Assert.Equal(moped || vehicle.Nick == "ferndale", vehicle.Levers.Length == 0);
            Assert.Equal(vehicle.Nick != "ferndale", vehicle.Pedals.Any(p => p.Input == DriverInput.Clutch));
        }
    }

    [Fact]
    public void The_mopeds_kickstand_is_a_knob_of_two_positions()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "data", "vehicles", "jonnez.json");
        VehicleDefinition moped = VehicleDefinition.Parse(File.ReadAllText(file));

        // Down or up as the last player left it: each game's physics leans the moped on its own stand (2026-10-18).
        VehicleKnob stand = Assert.Single(moped.Knobs, k => k.Name == "kickstand");
        Assert.Equal("Kickstand", stand.Path);
        Assert.Equal("Use", stand.Machine);
        Assert.Equal("StandOn", stand.Variable);
        Assert.Equal(new[] { "Stand off", "Stand on" }, stand.States);
        Assert.Equal(1, stand.Highest);
    }

    [Fact]
    public void The_boat_is_a_vehicle_whose_floating_is_paused_where_another_machine_simulates_it()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "data", "vehicles", "boat.json");
        VehicleDefinition boat = VehicleDefinition.Parse(File.ReadAllText(file));

        Assert.Equal("BOAT", boat.RootName);
        Assert.Equal("GFX/Triggers/DriveTrigger", boat.DriveTriggerPath);
        Assert.Equal(new[] { "::Y Velocity", "::Rise Front" }, boat.FollowOff);
        Assert.Equal(new[] { "GFX" }, boat.FollowKinematic);

        // Its engine running or not, said by the machine that simulates it, read from the state of its sound's machine.
        VehicleKnob engine = Assert.Single(boat.Knobs);
        Assert.True(engine.BySimulator);
        Assert.Equal("Sound Engine", engine.Machine);
        Assert.True(boat.HasInterior);
        Assert.Equal("middle", Assert.Single(boat.Seats).Name);

        // Most vehicles pause nothing; a machine is named with its path below the root.
        string van = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "vehicles", "hayosiko.json"));
        Assert.Empty(VehicleDefinition.Parse(van).FollowOff);
        string text = File.ReadAllText(file);
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(text.Replace("\"::Y Velocity\"", "\"Y Velocity\"")));
        Assert.Contains("path::machine", error.Message);
    }

    [Fact]
    public void The_cabins_open_by_their_doors_and_not_by_the_hood_or_the_boot()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "vehicles");
        var vehicles = Directory.GetFiles(folder, "*.json").Select(f => VehicleDefinition.Parse(File.ReadAllText(f))).ToDictionary(v => v.Nick);

        Assert.Equal(new[] { "DriverDoors/doorl", "DriverDoors/doorr" }, vehicles["kekmet"].CabinVents);
        Assert.Equal(new[] { "Body/pivot_door_left/door left(Clone)", "Body/pivot_door_right/door right(Clone)" }, vehicles["satsuma"].CabinVents);
        // The van and the Ruscko are one space from the front seats to the back door.
        Assert.Contains("RearDoor/doorear", vehicles["van"].CabinVents);
        Assert.Contains("SideDoor/door/Collider", vehicles["van"].CabinVents);
        Assert.Contains("RearDoor/doorear/door/Handle", vehicles["ruscko"].CabinVents);
        Assert.DoesNotContain(vehicles.Values.SelectMany(v => v.CabinVents), vent => vent.Contains("hood") || vent.Contains("ootlid") || vent.Contains("glov"));
        Assert.Empty(vehicles["jonnez"].CabinVents);

        // Doors to a space the file does not have are a mistake.
        string moped = File.ReadAllText(Path.Combine(folder, "jonnez.json"));
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(moped.Replace("\"rootName\"", "\"cabinVents\": [\"door\"], \"rootName\"")));
        Assert.Contains("cabinVents", error.Message);
    }

    [Fact]
    public void Every_vehicle_file_in_the_repository_can_be_read()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "vehicles");
        string[] files = Directory.GetFiles(folder, "*.json");

        Assert.NotEmpty(files);
        var nicks = new List<string>();
        var roots = new List<string>();
        foreach (string file in files)
        {
            VehicleDefinition vehicle = VehicleDefinition.Parse(File.ReadAllText(file));
            Assert.False(string.IsNullOrWhiteSpace(vehicle.RootName), file);
            nicks.Add(vehicle.Nick);
            roots.Add(vehicle.RootName);
        }

        // People type the nick and the scene is searched by the name: neither can be shared by two vehicles.
        Assert.Equal(files.Length, nicks.Distinct().Count());
        Assert.Equal(files.Length, roots.Distinct().Count());
        Assert.Contains("van", nicks);
    }

    [Fact]
    public void The_shipped_files_have_the_horns_turn_signals_hand_throttles_and_the_tractors_hitch()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "data", "vehicles");
        var vehicles = Directory.GetFiles(folder, "*.json").Select(f => VehicleDefinition.Parse(File.ReadAllText(f))).ToDictionary(v => v.Nick);

        // A horn button on the dashboard of four of them, two steering wheels on the Satsuma; none on the tractor or the moped.
        foreach (string nick in new[] { "van", "gifu", "ruscko", "ferndale" })
        {
            Assert.EndsWith("ButtonHorn/CarHorn", vehicles[nick].Horn!.Path);
            Assert.Equal("State 1", Assert.Single(vehicles[nick].Horn!.Buttons).Pressed);
        }

        Assert.Equal(2, vehicles["satsuma"].Horn!.Buttons.Length);
        Assert.Null(vehicles["kekmet"].Horn);
        Assert.Null(vehicles["jonnez"].Horn);

        // The turn signals, told by the simulating machine: the van, the Gifu, the Ferndale and the Satsuma.
        string[] signalled = vehicles.Values.Where(v => v.Knobs.Any(k => k.Name == "turn signals")).Select(v => v.Nick).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "ferndale", "gifu", "satsuma", "van" }, signalled);
        Assert.All(vehicles.Values.SelectMany(v => v.Knobs).Where(k => k.Name == "turn signals"), k => Assert.True(k.BySimulator && k.Highest == 2));

        // The hand throttles keep the engine's idle in another machine of the same object, worked out as the game does.
        VehicleKnob tractor = vehicles["kekmet"].Knobs.Single(k => k.Name == "hand throttle");
        Assert.Equal(0.4667f, tractor.Copies[0].Of(tractor.ValueAt(tractor.Highest)), 3);
        VehicleKnob truck = vehicles["gifu"].Knobs.Single(k => k.Name == "hand throttle");
        Assert.Equal(0.03f, truck.ValueAt(truck.Highest), 4);
        Assert.Equal(0.13f, truck.Copies[0].Of(truck.ValueAt(0)), 4);
        Assert.True(truck.Moves!.Slides);

        // The front loader's two levers, read from their machines' states; nothing else has hydraulics shared this way.
        VehicleHydraulic[] levers = vehicles["kekmet"].Hydraulics;
        Assert.Equal(new[] { "loader", "arm" }, levers.Select(l => l.Name).ToArray());
        Assert.Equal(1, levers[0].LeverDuring("INCREASE"));
        Assert.Equal(2, levers[1].LeverDuring("DECREASE 2"));
        Assert.Equal(0, levers[1].LeverDuring("Wait button 2"));
        Assert.Equal(-1, levers[0].LeverDuring("State 1"));
        Assert.Equal(1, levers[0].Moves.Axis);
        Assert.True(levers[1].Min < levers[1].Max);
        Assert.All(vehicles.Values.Where(v => v.Nick != "kekmet"), v => Assert.Empty(v.Hydraulics));

        // The tractor tows the flatbed, which is a vehicle of its own that nobody drives.
        VehicleHitch hitch = Assert.Single(vehicles["kekmet"].Hitches);
        Assert.Equal("FLATBED", hitch.Towed);
        Assert.Equal("TrailerAttached", hitch.Variable);
        Assert.Equal("Close door", hitch.Release);
        Assert.Equal("HookTarget", hitch.TowedPoint);
        Assert.False(vehicles["flatbed"].CanBeDriven);
        Assert.Equal("FLATBED", vehicles["flatbed"].RootName);
        Assert.All(vehicles.Values.Where(v => v.Nick != "kekmet"), v => Assert.Empty(v.Hitches));
    }

    [Theory]
    [InlineData("\"towed\": \"FLATBED\", ", "", "needs a text \"towed\"")]
    [InlineData("\"attach\": \"Sound\"", "\"attach\": \"\"", "needs what it tows")]
    [InlineData(", \"release\": { \"path\": \"Trailer/Remove\", \"machine\": \"Use\", \"state\": \"Close door\" }", "", "\"release\" must be an object")]
    public void A_hitch_that_is_wrong_is_refused_with_the_reason(string from, string to, string expected)
    {
        const string Tractor = """
            { "format": 2, "nick": "kekmet", "rootName": "KEKMET(350-400psi)",
              "driveTriggerPath": "LOD/PlayerTrigger/DriveTrigger", "driveMachineName": "PlayerTrigger",
              "hitches": [ { "towed": "FLATBED", "towedPoint": "HookTarget", "path": "Trailer/Hook", "machine": "Distance", "variable": "TrailerAttached", "attach": "Sound", "release": { "path": "Trailer/Remove", "machine": "Use", "state": "Close door" } } ] }
            """;
        Assert.Contains(from, Tractor);
        var error = Assert.Throws<FormatException>(() => VehicleDefinition.Parse(Tractor.Replace(from, to)));
        Assert.Contains(expected, error.Message);
    }
}
