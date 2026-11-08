using System.Text;
using SummerSync.Core.Dump;
using SummerSync.Core.Json;

namespace SummerSync.DumpDiff.Tests;

/// <summary>
/// Builds dump files for tests with the same JsonWriter and DumpFormat names the in-game dumper uses, so the
/// tests also check that what the game writes is what the tools read.
/// </summary>
internal sealed class DumpBuilder
{
    private readonly List<(string Type, string Name, object? Value)> globals = new();
    private readonly List<FsmBuilder> fsms = new();
    private readonly List<Action<JsonWriter>> collections = new();
    private readonly List<Action<JsonWriter>> bodies = new();
    private readonly List<Action<JsonWriter>> vehicles = new();
    private readonly List<string> errors = new();
    private string kind = DumpFormat.KindFull;
    private string scene = "GAME";
    private double time;
    private long frame;
    private int format = DumpFormat.Version;

    public DumpBuilder Kind(string value)
    {
        kind = value;
        return this;
    }

    public DumpBuilder At(double seconds, long frameNumber, string sceneName = "GAME")
    {
        time = seconds;
        frame = frameNumber;
        scene = sceneName;
        return this;
    }

    public DumpBuilder FormatVersion(int value)
    {
        format = value;
        return this;
    }

    public DumpBuilder Global(string type, string name, object? value)
    {
        globals.Add((type, name, value));
        return this;
    }

    public DumpBuilder Fsm(string path, string name, string state = "Idle", Action<FsmBuilder>? configure = null)
    {
        var builder = new FsmBuilder(path, name, state);
        configure?.Invoke(builder);
        fsms.Add(builder);
        return this;
    }

    public DumpBuilder Collection(string path, string type, string reference, object? items, int id = 0)
    {
        collections.Add(w =>
        {
            w.BeginObject();
            Identity(w, path, "0", id, true);
            w.Property(DumpFormat.CollectionType, type).Property(DumpFormat.Reference, reference).Name(DumpFormat.Items);
            Value(w, items);
            w.EndObject();
        });
        return this;
    }

    public DumpBuilder Body(
        string path,
        double[]? position = null,
        double[]? rotation = null,
        double[]? velocity = null,
        bool sleeping = false,
        bool kinematic = false,
        int id = 0,
        string indexPath = "0",
        bool active = true)
    {
        bodies.Add(w =>
        {
            w.BeginObject();
            Identity(w, path, indexPath, id, active);
            BodyState(w, position, rotation, velocity, sleeping, kinematic);
            w.Property(DumpFormat.Layer, 0);
            w.EndObject();
        });
        return this;
    }

    public DumpBuilder Vehicle(string path, double[]? position = null, Dictionary<string, object?>? drivetrain = null, int wheels = 4, int colliders = 0)
    {
        vehicles.Add(w =>
        {
            w.BeginObject();
            Identity(w, path, "0", 0, true);
            w.Name(DumpFormat.Components).BeginArray().Value("Transform").Value("Rigidbody").Value("CarDynamics").Value("Drivetrain").EndArray();
            w.Name(DumpFormat.Body).BeginObject();
            BodyState(w, position, null, null, false, false);
            w.EndObject();
            w.Name(DumpFormat.Drivetrain);
            if (drivetrain == null)
            {
                w.Null();
            }
            else
            {
                w.BeginObject();
                foreach (KeyValuePair<string, object?> field in drivetrain)
                {
                    w.Name(field.Key);
                    Value(w, field.Value);
                }

                w.EndObject();
            }

            w.Property(DumpFormat.Wheels, wheels);
            if (kind == DumpFormat.KindFull)
            {
                w.Name(DumpFormat.Colliders).BeginArray();
                for (int i = 0; i < colliders; i++)
                {
                    w.BeginObject().Property(DumpFormat.Path, "body/col" + i).Property(DumpFormat.ColliderType, "BoxCollider")
                        .Property(DumpFormat.IsTrigger, i == 0).EndObject();
                }

                w.EndArray();
            }

            w.EndObject();
        });
        return this;
    }

    public DumpBuilder Error(string text)
    {
        errors.Add(text);
        return this;
    }

    public string ToJson()
    {
        using var text = new StringWriter();
        var w = new JsonWriter(text);
        w.BeginObject();
        w.Property(DumpFormat.Format, format).Property(DumpFormat.Kind, kind).Property(DumpFormat.Tool, "test")
            .Property(DumpFormat.Created, "2026-10-12T18:00:00").Property(DumpFormat.Trigger, "key");
        w.Name(DumpFormat.Game).BeginObject().Property(DumpFormat.Unity, "5.0.0f4").Property(DumpFormat.Scene, scene)
            .Property(DumpFormat.Time, time).Property(DumpFormat.Realtime, time).Property(DumpFormat.Frame, frame).EndObject();

        w.Break().Name(DumpFormat.Globals);
        Variables(w, globals);

        w.Break().Name(DumpFormat.Fsms).BeginArray();
        foreach (FsmBuilder fsm in fsms)
        {
            w.Break();
            fsm.Write(w, kind == DumpFormat.KindFull);
        }

        w.EndArray();
        Section(w, DumpFormat.Collections, collections);
        Section(w, DumpFormat.Rigidbodies, bodies);
        Section(w, DumpFormat.Vehicles, vehicles);

        w.Break().Name(DumpFormat.Stats).BeginObject().Property(DumpFormat.StatFsms, fsms.Count).Property(DumpFormat.StatMilliseconds, 12L).EndObject();
        w.Break().Name(DumpFormat.Errors).BeginArray();
        errors.ForEach(error => w.Value(error));
        w.EndArray().EndObject();
        return text.ToString();
    }

    public DumpFile Build(bool includeStructure = true)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ToJson()));
        DumpFile dump = DumpFile.Parse(stream, includeStructure);
        dump.Source = "test.json";
        return dump;
    }

    internal static void Identity(JsonWriter w, string path, string indexPath, int id, bool active)
    {
        w.Property(DumpFormat.Path, path).Property(DumpFormat.IndexPath, indexPath).Property(DumpFormat.InstanceId, id)
            .Property(DumpFormat.ActiveInHierarchy, active);
    }

    internal static void Variables(JsonWriter w, IEnumerable<(string Type, string Name, object? Value)> variables)
    {
        w.BeginObject();
        foreach (var section in variables.GroupBy(variable => variable.Type))
        {
            w.Name(section.Key).BeginObject();
            foreach (var variable in section)
            {
                w.Name(variable.Name);
                Value(w, variable.Value);
            }

            w.EndObject();
        }

        w.EndObject();
    }

    internal static void Value(JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.Null(); break;
            case string text: w.Value(text); break;
            case bool flag: w.Value(flag); break;
            case int number: w.Value(number); break;
            case float number: w.Value(number); break;
            case double number: w.Value(number); break;
            case double[] numbers:
                w.BeginArray();
                foreach (double number in numbers)
                {
                    w.Value(number);
                }

                w.EndArray();
                break;
            case object[] items:
                w.BeginArray();
                foreach (object? item in items)
                {
                    Value(w, item);
                }

                w.EndArray();
                break;
            case Dictionary<string, object?> map:
                w.BeginObject();
                foreach (KeyValuePair<string, object?> pair in map)
                {
                    w.Name(pair.Key);
                    Value(w, pair.Value);
                }

                w.EndObject();
                break;
            default: throw new NotSupportedException(value.GetType().Name);
        }
    }

    private static void Section(JsonWriter w, string name, List<Action<JsonWriter>> items)
    {
        w.Break().Name(name).BeginArray();
        foreach (Action<JsonWriter> item in items)
        {
            w.Break();
            item(w);
        }

        w.EndArray();
    }

    private static void BodyState(JsonWriter w, double[]? position, double[]? rotation, double[]? velocity, bool sleeping, bool kinematic)
    {
        w.Name(DumpFormat.Position);
        Value(w, position ?? [0, 0, 0]);
        w.Name(DumpFormat.Rotation);
        Value(w, rotation ?? [0, 0, 0, 1]);
        w.Name(DumpFormat.Velocity);
        Value(w, velocity ?? [0, 0, 0]);
        w.Name(DumpFormat.AngularVelocity);
        Value(w, new double[] { 0, 0, 0 });
        w.Property(DumpFormat.Mass, 1.5).Property(DumpFormat.Kinematic, kinematic).Property(DumpFormat.UseGravity, true)
            .Property(DumpFormat.Sleeping, sleeping);
    }
}

internal sealed class FsmBuilder
{
    private readonly string path;
    private readonly string name;
    private readonly string state;
    private readonly List<(string Type, string Name, object? Value)> variables = new();
    private readonly List<(string Name, bool Loaded, List<(string Type, string Category, bool Enabled, Dictionary<string, object?>? Parameters)> Actions)> states = new();
    private int id;
    private string indexPath = "0";
    private int ordinal;
    private bool enabled = true;
    private bool active = true;
    private bool initialized = true;

    public FsmBuilder(string path, string name, string state)
    {
        this.path = path;
        this.name = name;
        this.state = state;
    }

    public FsmBuilder Id(int value, string index = "0", int k = 0)
    {
        id = value;
        indexPath = index;
        ordinal = k;
        return this;
    }

    public FsmBuilder Flags(bool isEnabled = true, bool isActive = true, bool isInitialized = true)
    {
        enabled = isEnabled;
        active = isActive;
        initialized = isInitialized;
        return this;
    }

    public FsmBuilder Var(string type, string variableName, object? value)
    {
        variables.Add((type, variableName, value));
        return this;
    }

    public FsmBuilder State(string stateName, bool loaded = true)
    {
        states.Add((stateName, loaded, new()));
        return this;
    }

    /// <summary>Adds an action to the last state.</summary>
    public FsmBuilder Action(string type, string category, bool isEnabled = true, Dictionary<string, object?>? parameters = null)
    {
        if (states.Count == 0)
        {
            State("State 1");
        }

        states[^1].Actions.Add((type, category, isEnabled, parameters));
        return this;
    }

    public void Write(JsonWriter w, bool full)
    {
        w.BeginObject();
        DumpBuilder.Identity(w, path, indexPath, id, active);
        w.Property(DumpFormat.Name, name).Property(DumpFormat.Ordinal, ordinal).Property(DumpFormat.Enabled, enabled)
            .Property(DumpFormat.Initialized, initialized).Property(DumpFormat.ActiveState, state);
        w.Name(DumpFormat.Variables);
        DumpBuilder.Variables(w, variables);

        if (full)
        {
            w.Property(DumpFormat.StartState, states.Count > 0 ? states[0].Name : "");
            w.Name(DumpFormat.Events).BeginArray().Value("FINISHED").EndArray();
            w.Name(DumpFormat.GlobalTransitions).BeginArray().EndArray();
            w.Name(DumpFormat.States).BeginArray();
            foreach (var entry in states)
            {
                w.BeginObject().Property(DumpFormat.Name, entry.Name).Property(DumpFormat.ActionsLoaded, entry.Loaded);
                w.Name(DumpFormat.Transitions).BeginArray()
                    .BeginObject().Property(DumpFormat.Event, "FINISHED").Property(DumpFormat.ToState, "Next").EndObject()
                    .EndArray();
                w.Name(DumpFormat.Actions).BeginArray();
                foreach (var action in entry.Actions)
                {
                    w.BeginObject().Property(DumpFormat.ActionType, action.Type).Property(DumpFormat.ActionCategory, action.Category)
                        .Property(DumpFormat.ActionEnabled, action.Enabled);
                    if (action.Parameters != null)
                    {
                        w.Name(DumpFormat.ActionParameters);
                        DumpBuilder.Value(w, action.Parameters);
                    }

                    w.EndObject();
                }

                w.EndArray().EndObject();
            }

            w.EndArray();
        }

        w.EndObject();
    }
}
