using System.Text.Json;
using System.Text.Json.Serialization;
using SummerSync.Core.Dump;

namespace SummerSync.DumpDiff;

/// <summary>How an FSM is treated in multiplayer. See docs/ARCHITECTURE.md.</summary>
internal enum FsmClass
{
    /// <summary>Runs only on the host; clients mirror its state.</summary>
    WorldLogic,

    /// <summary>Sound, UI, animation: runs locally on every machine.</summary>
    Presentation,

    /// <summary>Does both, or depends on local input: needs a targeted patch.</summary>
    Mixed,

    /// <summary>Does nothing relevant.</summary>
    Ignore,
}

internal sealed class FsmClassification
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("k")]
    public int Ordinal { get; set; }

    [JsonPropertyName("class")]
    public FsmClass Class { get; set; }

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "";

    [JsonPropertyName("why")]
    public string Why { get; set; } = "";

    [JsonPropertyName("world")]
    public int World { get; set; }

    [JsonPropertyName("logic")]
    public int Logic { get; set; }

    [JsonPropertyName("presentation")]
    public int Presentation { get; set; }

    [JsonPropertyName("input")]
    public int Input { get; set; }

    /// <summary>Global variables referenced by the FSM's action parameters.</summary>
    [JsonPropertyName("globals")]
    public List<string> Globals { get; set; } = new();
}

/// <summary>
/// Heuristic first draft of the FSM classification, from the category PlayMaker assigns to each action.
/// It is a starting point for a human review, not a decision: the output says why and how sure it is.
/// </summary>
internal static class Classifier
{
    // The category names are the ones found in a full dump of the game (see docs/GAME_NOTES.md), which adds its
    // own to PlayMaker's: "Easy Save 2", "cInput", "steamworks.NET", "ArrayMaker/...", "Simple Waypoint System".

    // Actions that only affect what one player sees or hears. Steam achievements are per player too.
    private static readonly HashSet<string> PresentationCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Audio", "Animation", "AnimateVariables", "GUI", "GUILayout", "GUIElement", "Effects", "Camera", "Lights",
        "Material", "Renderer", "RenderSettings", "Movie", "Color", "Screen", "steamworks.NET",
    };

    // Actions that change the simulated world or its saved state. "UnityObject" is almost entirely SetProperty,
    // which writes arbitrary component properties.
    private static readonly HashSet<string> WorldCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Physics", "GameObject", "Transform", "Movement", "Character", "CharacterController", "NavMesh", "NavMeshAgent",
        "Level", "Application", "World", "GameLogic", "ScriptControl", "iTween", "UnityObject", "Easy Save 2",
        "Simple Waypoint System",
    };

    // Actions that read the local player's devices.
    private static readonly HashSet<string> InputCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Input", "InputDevice", "Device", "Controller", "cInput",
    };

    // Plumbing that says nothing by itself about what the FSM is for. "Time" is almost entirely Wait.
    private static readonly HashSet<string> FlowCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "StateMachine", "Events", "Debug", "Time", "Web", "Web Player",
    };

    public static List<FsmClassification> Classify(DumpFile dump)
    {
        var results = new List<FsmClassification>(dump.Fsms.Count);
        foreach (FsmEntry fsm in dump.Fsms)
        {
            results.Add(Classify(fsm, dump.Globals));
        }

        return results;
    }

    public static FsmClassification Classify(FsmEntry fsm, VariableSet globals)
    {
        var result = new FsmClassification { Path = fsm.Path, Name = fsm.Name, Ordinal = fsm.Ordinal };
        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var referencedGlobals = new SortedSet<string>(StringComparer.Ordinal);
        int actions = 0;

        foreach (ActionEntry action in (fsm.States ?? []).SelectMany(state => state.Actions))
        {
            if (!action.Enabled)
            {
                continue;
            }

            actions++;
            string category = action.Category.Length == 0 ? "(uncategorised)" : action.Category;
            counts[category] = counts.GetValueOrDefault(category) + 1;

            if (PresentationCategories.Contains(category))
            {
                result.Presentation++;
            }
            else if (WorldCategories.Contains(category))
            {
                result.World++;
            }
            else if (InputCategories.Contains(category))
            {
                result.Input++;
            }
            else if (!FlowCategories.Contains(category))
            {
                result.Logic++;
            }

            CollectGlobals(action, fsm, globals, referencedGlobals);
        }

        result.Globals = referencedGlobals.ToList();
        Decide(result, actions);
        string breakdown = string.Join(", ", counts.Select(pair => pair.Key + " " + pair.Value));
        result.Why += actions == 0 ? "" : " Actions by category: " + breakdown + ".";
        return result;
    }

    public static string ToJson(DumpFile dump, IReadOnlyList<FsmClassification> classifications)
    {
        var byName = classifications
            .GroupBy(c => c.Name)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                name = group.Key,
                count = group.Count(),
                classes = group.GroupBy(c => c.Class).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            });

        var document = new
        {
            format = 1,
            status = "DRAFT - generated by heuristics from action categories. Review before using it.",
            generatedFrom = dump.Source,
            scene = dump.Game.Scene,
            totals = classifications.GroupBy(c => c.Class).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            byName,
            fsms = classifications.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Ordinal),
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(document, options);
    }

    /// <summary>
    /// One row per FSM name, most common first. FSMs that share a name almost always share their logic, so this
    /// is the unit a person can actually review: hundreds of rows instead of thousands of instances.
    /// </summary>
    public static string ToReviewTable(DumpFile dump, IReadOnlyList<FsmClassification> classifications)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("# FSM classification draft, by FSM name");
        text.AppendLine();
        text.AppendLine($"Generated by `dumpdiff classify` from `{dump.Source}` (scene {dump.Game.Scene}): {classifications.Count} FSMs.");
        text.AppendLine("**Draft from heuristics over action categories. It decides nothing; it is here to be reviewed.**");
        text.AppendLine();
        text.AppendLine("Totals: " + string.Join(", ", classifications.GroupBy(c => c.Class).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())) + ".");
        text.AppendLine();
        text.AppendLine("- *Suggested*: the class most instances of that name got. *Split*: how the instances are distributed when they disagree.");
        text.AppendLine("- *Input*: how many instances read the local player's keyboard or mouse.");
        text.AppendLine("- *Actions*: world / logic / presentation actions summed over all instances.");
        text.AppendLine();
        text.AppendLine("| FSMs | Name | Suggested | Split | Input | Actions (W/L/P) | Globals | Example object |");
        text.AppendLine("|---:|---|---|---|---:|---|---|---|");

        foreach (var group in classifications.GroupBy(c => c.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var classes = group.GroupBy(c => c.Class).OrderByDescending(g => g.Count()).ToList();
            string split = classes.Count == 1 ? "" : string.Join(", ", classes.Select(g => g.Key + " " + g.Count()));
            List<string> globals = group.SelectMany(c => c.Globals).GroupBy(name => name).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            string globalsText = string.Join(", ", globals.Take(4)) + (globals.Count > 4 ? ", +" + (globals.Count - 4) : "");

            text.AppendLine(string.Join(" | ", new[]
            {
                "| " + group.Count(),
                Cell(group.Key.Length == 0 ? "(unnamed)" : group.Key),
                classes[0].Key.ToString(),
                split,
                group.Count(c => c.Input > 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                group.Sum(c => c.World) + " / " + group.Sum(c => c.Logic) + " / " + group.Sum(c => c.Presentation),
                Cell(globalsText),
                Cell(group.OrderBy(c => c.Path, StringComparer.Ordinal).First().Path) + " |",
            }));
        }

        return text.ToString();
    }

    // A pipe inside a cell would break the table.
    private static string Cell(string value) => value.Replace("|", "\\|");

    private static void Decide(FsmClassification result, int actions)
    {
        if (actions == 0)
        {
            result.Class = FsmClass.Ignore;
            result.Confidence = "high";
            result.Why = "No enabled actions.";
        }
        else if (result.Presentation == 0 && result.World == 0 && result.Logic == 0 && result.Input == 0)
        {
            result.Class = FsmClass.Ignore;
            result.Confidence = "low";
            result.Why = "Only flow control actions.";
        }
        else if (result.Input > 0 && (result.World > 0 || result.Logic > 0))
        {
            result.Class = FsmClass.Mixed;
            result.Confidence = "medium";
            result.Why = "Reads local input and also runs logic: the input has to reach the host.";
        }
        else if (result.Presentation > 0 && result.World > 0)
        {
            result.Class = FsmClass.Mixed;
            result.Confidence = "medium";
            result.Why = "Changes the world and also plays presentation.";
        }
        else if (result.Presentation > 0 && result.Logic > result.Presentation)
        {
            result.Class = FsmClass.Mixed;
            result.Confidence = "low";
            result.Why = "Mostly calculations with some presentation: may hold state other FSMs read.";
        }
        else if (result.Presentation > 0)
        {
            result.Class = FsmClass.Presentation;
            result.Confidence = result.Logic == 0 ? "high" : "low";
            result.Why = "Presentation actions and nothing that changes the world.";
        }
        else if (result.Input > 0)
        {
            result.Class = FsmClass.Mixed;
            result.Confidence = "low";
            result.Why = "Only reads local input.";
        }
        else
        {
            result.Class = FsmClass.WorldLogic;
            result.Confidence = result.World > 0 ? "high" : "medium";
            result.Why = result.World > 0 ? "Changes the world and has no presentation." : "Calculations only, no presentation.";
        }
    }

    /// <summary>
    /// A parameter bound to a variable is dumped as {"var": name, ...}. If the FSM has no variable of that name
    /// but the globals do, the action touches a global.
    /// </summary>
    private static void CollectGlobals(ActionEntry action, FsmEntry fsm, VariableSet globals, SortedSet<string> found)
    {
        if (action.Parameters == null)
        {
            return;
        }

        foreach (DumpValue parameter in action.Parameters.Values)
        {
            if (parameter.Kind != DumpValueKind.Json || !parameter.Text!.Contains("\"" + DumpFormat.VariableName + "\"", StringComparison.Ordinal))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(parameter.Text);
            CollectGlobals(document.RootElement, fsm, globals, found);
        }
    }

    private static void CollectGlobals(JsonElement element, FsmEntry fsm, VariableSet globals, SortedSet<string> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectGlobals(item, fsm, globals, found);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(DumpFormat.VariableName, out JsonElement name) && name.ValueKind == JsonValueKind.String)
            {
                string variable = name.GetString()!;
                bool local = fsm.Variables.Values.Any(section => section.ContainsKey(variable));
                if (!local && globals.Values.Any(section => section.ContainsKey(variable)))
                {
                    found.Add(variable);
                }
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                CollectGlobals(property.Value, fsm, globals, found);
            }
        }
    }
}
