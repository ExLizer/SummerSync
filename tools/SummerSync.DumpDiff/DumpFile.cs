using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SummerSync.Core.Dump;

namespace SummerSync.DumpDiff;

/// <summary>Variables grouped by type name ("float", "bool"...) and then by variable name.</summary>
internal sealed class VariableSet : Dictionary<string, Dictionary<string, DumpValue>>
{
}

internal sealed class DumpFile
{
    [JsonPropertyName(DumpFormat.Format)]
    public int Format { get; set; }

    [JsonPropertyName(DumpFormat.Kind)]
    public string Kind { get; set; } = "";

    [JsonPropertyName(DumpFormat.Tool)]
    public string Tool { get; set; } = "";

    [JsonPropertyName(DumpFormat.Created)]
    public string Created { get; set; } = "";

    [JsonPropertyName(DumpFormat.Trigger)]
    public string Trigger { get; set; } = "";

    [JsonPropertyName(DumpFormat.Game)]
    public GameInfo Game { get; set; } = new();

    [JsonPropertyName(DumpFormat.Globals)]
    public VariableSet Globals { get; set; } = new();

    [JsonPropertyName(DumpFormat.Fsms)]
    public List<FsmEntry> Fsms { get; set; } = new();

    [JsonPropertyName(DumpFormat.Collections)]
    public List<CollectionEntry> Collections { get; set; } = new();

    [JsonPropertyName(DumpFormat.Rigidbodies)]
    public List<BodyEntry> Rigidbodies { get; set; } = new();

    [JsonPropertyName(DumpFormat.Vehicles)]
    public List<VehicleEntry> Vehicles { get; set; } = new();

    [JsonPropertyName(DumpFormat.Stats)]
    public Dictionary<string, double> Stats { get; set; } = new();

    [JsonPropertyName(DumpFormat.Errors)]
    public List<string> Errors { get; set; } = new();

    /// <summary>File name the dump was loaded from, for reports.</summary>
    [JsonIgnore]
    public string Source { get; set; } = "";

    /// <summary>
    /// Loads a dump. Without <paramref name="includeStructure"/>, states, transitions and actions are skipped
    /// while reading, which keeps a diff of two full dumps small in memory.
    /// </summary>
    public static DumpFile Load(string path, bool includeStructure)
    {
        using FileStream stream = File.OpenRead(path);
        DumpFile dump = Parse(stream, includeStructure);
        dump.Source = Path.GetFileName(path);
        return dump;
    }

    public static DumpFile Parse(Stream stream, bool includeStructure)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        if (!includeStructure)
        {
            resolver.Modifiers.Add(typeInfo =>
            {
                if (typeInfo.Type == typeof(FsmEntry))
                {
                    foreach (JsonPropertyInfo property in typeInfo.Properties.Where(p => p.Name is DumpFormat.States or DumpFormat.GlobalTransitions or DumpFormat.Events).ToList())
                    {
                        typeInfo.Properties.Remove(property);
                    }
                }
                else if (typeInfo.Type == typeof(VehicleEntry))
                {
                    foreach (JsonPropertyInfo property in typeInfo.Properties.Where(p => p.Name == DumpFormat.Colliders).ToList())
                    {
                        typeInfo.Properties.Remove(property);
                    }
                }
            });
        }

        var options = new JsonSerializerOptions { TypeInfoResolver = resolver, MaxDepth = 64 };
        DumpFile dump = JsonSerializer.Deserialize<DumpFile>(stream, options) ?? throw new InvalidDataException("The dump is empty.");
        if (dump.Format != DumpFormat.Version)
        {
            throw new InvalidDataException($"Unsupported dump format {dump.Format}; this tool reads format {DumpFormat.Version}.");
        }

        return dump;
    }
}

internal sealed class GameInfo
{
    [JsonPropertyName(DumpFormat.Unity)]
    public string Unity { get; set; } = "";

    [JsonPropertyName(DumpFormat.Scene)]
    public string Scene { get; set; } = "";

    [JsonPropertyName(DumpFormat.Time)]
    public double Time { get; set; }

    [JsonPropertyName(DumpFormat.Realtime)]
    public double Realtime { get; set; }

    [JsonPropertyName(DumpFormat.Frame)]
    public long Frame { get; set; }
}

/// <summary>Identity shared by everything a dump lists.</summary>
internal abstract class DumpEntry
{
    [JsonPropertyName(DumpFormat.Path)]
    public string Path { get; set; } = "";

    [JsonPropertyName(DumpFormat.IndexPath)]
    public string IndexPath { get; set; } = "";

    [JsonPropertyName(DumpFormat.InstanceId)]
    public int Id { get; set; }

    [JsonPropertyName(DumpFormat.ActiveInHierarchy)]
    public bool Active { get; set; }
}

internal sealed class FsmEntry : DumpEntry
{
    [JsonPropertyName(DumpFormat.Name)]
    public string Name { get; set; } = "";

    [JsonPropertyName(DumpFormat.Ordinal)]
    public int Ordinal { get; set; }

    [JsonPropertyName(DumpFormat.Enabled)]
    public bool Enabled { get; set; }

    [JsonPropertyName(DumpFormat.Initialized)]
    public bool Initialized { get; set; }

    [JsonPropertyName(DumpFormat.ActiveState)]
    public string State { get; set; } = "";

    [JsonPropertyName(DumpFormat.Variables)]
    public VariableSet Variables { get; set; } = new();

    [JsonPropertyName(DumpFormat.StartState)]
    public string? StartState { get; set; }

    [JsonPropertyName(DumpFormat.Events)]
    public List<string>? Events { get; set; }

    [JsonPropertyName(DumpFormat.GlobalTransitions)]
    public List<TransitionEntry>? GlobalTransitions { get; set; }

    [JsonPropertyName(DumpFormat.States)]
    public List<StateEntry>? States { get; set; }
}

internal sealed class StateEntry
{
    [JsonPropertyName(DumpFormat.Name)]
    public string Name { get; set; } = "";

    [JsonPropertyName(DumpFormat.ActionsLoaded)]
    public bool Loaded { get; set; }

    [JsonPropertyName(DumpFormat.Transitions)]
    public List<TransitionEntry> Transitions { get; set; } = new();

    [JsonPropertyName(DumpFormat.Actions)]
    public List<ActionEntry> Actions { get; set; } = new();
}

internal sealed class TransitionEntry
{
    [JsonPropertyName(DumpFormat.Event)]
    public string Event { get; set; } = "";

    [JsonPropertyName(DumpFormat.ToState)]
    public string To { get; set; } = "";
}

internal sealed class ActionEntry
{
    [JsonPropertyName(DumpFormat.ActionType)]
    public string Type { get; set; } = "";

    [JsonPropertyName(DumpFormat.ActionCategory)]
    public string Category { get; set; } = "";

    [JsonPropertyName(DumpFormat.ActionEnabled)]
    public bool Enabled { get; set; } = true;

    /// <summary>Null when the state's actions were not loaded in the game and only their names are known.</summary>
    [JsonPropertyName(DumpFormat.ActionParameters)]
    public Dictionary<string, DumpValue>? Parameters { get; set; }
}

internal sealed class CollectionEntry : DumpEntry
{
    [JsonPropertyName(DumpFormat.CollectionType)]
    public string Type { get; set; } = "";

    [JsonPropertyName(DumpFormat.Reference)]
    public string Reference { get; set; } = "";

    [JsonPropertyName(DumpFormat.Items)]
    public DumpValue Items { get; set; }
}

internal class BodyState
{
    [JsonPropertyName(DumpFormat.Position)]
    public double[] Position { get; set; } = [];

    [JsonPropertyName(DumpFormat.Rotation)]
    public double[] Rotation { get; set; } = [];

    [JsonPropertyName(DumpFormat.Velocity)]
    public double[] Velocity { get; set; } = [];

    [JsonPropertyName(DumpFormat.AngularVelocity)]
    public double[] AngularVelocity { get; set; } = [];

    [JsonPropertyName(DumpFormat.Mass)]
    public double Mass { get; set; }

    [JsonPropertyName(DumpFormat.Kinematic)]
    public bool Kinematic { get; set; }

    [JsonPropertyName(DumpFormat.UseGravity)]
    public bool UseGravity { get; set; }

    [JsonPropertyName(DumpFormat.Sleeping)]
    public bool Sleeping { get; set; }
}

internal sealed class BodyEntry : DumpEntry
{
    [JsonPropertyName(DumpFormat.Position)]
    public double[] Position { get; set; } = [];

    [JsonPropertyName(DumpFormat.Rotation)]
    public double[] Rotation { get; set; } = [];

    [JsonPropertyName(DumpFormat.Velocity)]
    public double[] Velocity { get; set; } = [];

    [JsonPropertyName(DumpFormat.AngularVelocity)]
    public double[] AngularVelocity { get; set; } = [];

    [JsonPropertyName(DumpFormat.Mass)]
    public double Mass { get; set; }

    [JsonPropertyName(DumpFormat.Kinematic)]
    public bool Kinematic { get; set; }

    [JsonPropertyName(DumpFormat.UseGravity)]
    public bool UseGravity { get; set; }

    [JsonPropertyName(DumpFormat.Sleeping)]
    public bool Sleeping { get; set; }

    [JsonPropertyName(DumpFormat.Layer)]
    public int Layer { get; set; }

    public BodyState ToState() => new()
    {
        Position = Position,
        Rotation = Rotation,
        Velocity = Velocity,
        AngularVelocity = AngularVelocity,
        Mass = Mass,
        Kinematic = Kinematic,
        UseGravity = UseGravity,
        Sleeping = Sleeping,
    };
}

internal sealed class VehicleEntry : DumpEntry
{
    [JsonPropertyName(DumpFormat.Components)]
    public List<string> Components { get; set; } = new();

    [JsonPropertyName(DumpFormat.Body)]
    public BodyState? Body { get; set; }

    [JsonPropertyName(DumpFormat.Drivetrain)]
    public Dictionary<string, DumpValue>? Drivetrain { get; set; }

    [JsonPropertyName(DumpFormat.Wheels)]
    public int Wheels { get; set; }

    [JsonPropertyName(DumpFormat.Colliders)]
    public List<ColliderEntry>? Colliders { get; set; }
}

internal sealed class ColliderEntry
{
    [JsonPropertyName(DumpFormat.Path)]
    public string Path { get; set; } = "";

    [JsonPropertyName(DumpFormat.ColliderType)]
    public string Type { get; set; } = "";

    [JsonPropertyName(DumpFormat.IsTrigger)]
    public bool IsTrigger { get; set; }
}
