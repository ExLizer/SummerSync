using System.Text.RegularExpressions;

namespace SummerSync.DumpDiff;

internal enum Section
{
    Globals,
    Fsms,
    Collections,
    Rigidbodies,
    Vehicles,
}

/// <summary>One difference between two dumps.</summary>
/// <param name="Section">Part of the dump it belongs to.</param>
/// <param name="Object">GameObject path (empty for global variables).</param>
/// <param name="Scope">FSM name within the object, when there is one.</param>
/// <param name="What">What changed, e.g. "state" or "float FuelLevel".</param>
/// <param name="Before">Old value, or null when the thing did not exist.</param>
/// <param name="After">New value, or null when the thing no longer exists.</param>
/// <param name="Noise">True when a noise filter matched; such changes are counted but not listed by default.</param>
internal sealed record Change(Section Section, string Object, string Scope, string What, string? Before, string? After, bool Noise);

internal sealed class DiffOptions
{
    /// <summary>Variable names that change on their own all the time. Matched against the variable name only.</summary>
    public static readonly Regex DefaultNoise = new(
        "(timer|^time$|wait|delay|elapsed|countdown|random|rnd|deltatime|^tick)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Absolute tolerance for numbers.</summary>
    public double Absolute { get; set; } = 0.001;

    /// <summary>Relative tolerance for numbers, as a fraction of their magnitude.</summary>
    public double Relative { get; set; } = 0.001;

    /// <summary>A rigidbody that moved less than this many metres did not move.</summary>
    public double Position { get; set; } = 0.01;

    /// <summary>A rigidbody that turned less than this many degrees did not turn.</summary>
    public double RotationDegrees { get; set; } = 1.0;

    /// <summary>Velocity differences below this many metres per second are ignored.</summary>
    public double Velocity { get; set; } = 0.05;

    public bool UseDefaultNoise { get; set; } = true;

    /// <summary>Extra noise patterns, matched against "path :: fsm :: what".</summary>
    public List<Regex> Noise { get; } = new();

    /// <summary>When set, only objects whose path matches are compared.</summary>
    public Regex? PathFilter { get; set; }

    public HashSet<Section> Sections { get; } = new(Enum.GetValues<Section>());

    public static string NoiseKey(string path, string scope, string what) => path + " :: " + scope + " :: " + what;
}

/// <summary>Compares two dumps and lists what changed.</summary>
internal sealed class Differ
{
    private readonly DiffOptions options;
    private readonly List<Change> changes = new();

    public Differ(DiffOptions options)
    {
        this.options = options;
    }

    public List<Change> Compare(DumpFile before, DumpFile after)
    {
        changes.Clear();

        if (options.Sections.Contains(Section.Globals) && options.PathFilter == null)
        {
            CompareVariables(Section.Globals, "", "", before.Globals, after.Globals);
        }

        if (options.Sections.Contains(Section.Fsms))
        {
            foreach (EntryPair<FsmEntry> pair in EntryMatcher.Pair(Filter(before.Fsms), Filter(after.Fsms), f => f.Path + "\n" + f.Name, f => f.IndexPath + "#" + f.Ordinal))
            {
                CompareFsm(pair);
            }
        }

        if (options.Sections.Contains(Section.Collections))
        {
            foreach (EntryPair<CollectionEntry> pair in EntryMatcher.Pair(Filter(before.Collections), Filter(after.Collections), c => c.Path + "\n" + c.Reference + "\n" + c.Type, c => c.IndexPath))
            {
                CompareCollection(pair);
            }
        }

        if (options.Sections.Contains(Section.Rigidbodies))
        {
            foreach (EntryPair<BodyEntry> pair in EntryMatcher.Pair(Filter(before.Rigidbodies), Filter(after.Rigidbodies), b => b.Path, b => b.IndexPath))
            {
                CompareBody(pair);
            }
        }

        if (options.Sections.Contains(Section.Vehicles))
        {
            foreach (EntryPair<VehicleEntry> pair in EntryMatcher.Pair(Filter(before.Vehicles), Filter(after.Vehicles), v => v.Path, v => v.IndexPath))
            {
                CompareVehicle(pair);
            }
        }

        return changes.ToList();
    }

    /// <summary>Angle in degrees between two rotations given as [x, y, z, w] quaternions.</summary>
    public static double AngleBetween(double[] a, double[] b)
    {
        if (a.Length != 4 || b.Length != 4)
        {
            return 0;
        }

        // q and -q are the same rotation, hence the absolute value.
        double dot = Math.Abs((a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]) + (a[3] * b[3]));
        return 2 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI;
    }

    public static double Distance(double[] a, double[] b)
    {
        if (a.Length != b.Length)
        {
            return double.PositiveInfinity;
        }

        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += (a[i] - b[i]) * (a[i] - b[i]);
        }

        return Math.Sqrt(sum);
    }

    private IEnumerable<T> Filter<T>(IEnumerable<T> entries)
        where T : DumpEntry =>
        options.PathFilter == null ? entries : entries.Where(entry => options.PathFilter.IsMatch(entry.Path));

    private void Add(Section section, string path, string scope, string what, string? before, string? after, string? noiseName = null)
    {
        bool noise = (noiseName != null && options.UseDefaultNoise && DiffOptions.DefaultNoise.IsMatch(noiseName))
            || (options.Noise.Count > 0 && options.Noise.Any(pattern => pattern.IsMatch(DiffOptions.NoiseKey(path, scope, what))));
        changes.Add(new Change(section, path, scope, what, before, after, noise));
    }

    private void CompareFsm(EntryPair<FsmEntry> pair)
    {
        FsmEntry? a = pair.Before;
        FsmEntry? b = pair.After;
        if (a == null || b == null)
        {
            FsmEntry present = (a ?? b)!;
            Add(Section.Fsms, present.Path, present.Name, a == null ? "FSM added" : "FSM removed", a?.State, b?.State);
            return;
        }

        CompareFlag(Section.Fsms, a.Path, a.Name, "state", a.State, b.State);
        CompareFlag(Section.Fsms, a.Path, a.Name, "enabled", a.Enabled, b.Enabled);
        CompareFlag(Section.Fsms, a.Path, a.Name, "object active", a.Active, b.Active);
        CompareFlag(Section.Fsms, a.Path, a.Name, "initialized", a.Initialized, b.Initialized);
        CompareVariables(Section.Fsms, a.Path, a.Name, a.Variables, b.Variables);
    }

    private void CompareFlag<T>(Section section, string path, string scope, string what, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            Add(section, path, scope, what, Text(before), Text(after));
        }
    }

    private static string Text<T>(T value) => value switch
    {
        bool flag => flag ? "true" : "false",
        string text => text.Length == 0 ? "(none)" : text,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    private void CompareVariables(Section section, string path, string scope, VariableSet before, VariableSet after)
    {
        foreach (string type in before.Keys.Union(after.Keys).OrderBy(t => t, StringComparer.Ordinal))
        {
            Dictionary<string, DumpValue> first = before.GetValueOrDefault(type) ?? new();
            Dictionary<string, DumpValue> second = after.GetValueOrDefault(type) ?? new();
            foreach (string name in first.Keys.Union(second.Keys).OrderBy(n => n, StringComparer.Ordinal))
            {
                bool inFirst = first.TryGetValue(name, out DumpValue x);
                bool inSecond = second.TryGetValue(name, out DumpValue y);
                if (inFirst && inSecond && x.EqualsWithin(y, options.Absolute, options.Relative))
                {
                    continue;
                }

                Add(section, path, scope, type + " " + name, inFirst ? x.ToString() : null, inSecond ? y.ToString() : null, name);
            }
        }
    }

    private void CompareCollection(EntryPair<CollectionEntry> pair)
    {
        CollectionEntry? a = pair.Before;
        CollectionEntry? b = pair.After;
        string scope = (a ?? b)!.Type + (string.IsNullOrEmpty((a ?? b)!.Reference) ? "" : " '" + (a ?? b)!.Reference + "'");
        if (a == null || b == null)
        {
            Add(Section.Collections, (a ?? b)!.Path, scope, a == null ? "collection added" : "collection removed", a?.Items.ToString(), b?.Items.ToString());
            return;
        }

        CompareFlag(Section.Collections, a.Path, scope, "object active", a.Active, b.Active);
        if (!a.Items.EqualsWithin(b.Items, options.Absolute, options.Relative))
        {
            Add(Section.Collections, a.Path, scope, "items", a.Items.ToString(), b.Items.ToString());
        }
    }

    private void CompareBody(EntryPair<BodyEntry> pair)
    {
        BodyEntry? a = pair.Before;
        BodyEntry? b = pair.After;
        if (a == null || b == null)
        {
            Add(Section.Rigidbodies, (a ?? b)!.Path, "", a == null ? "rigidbody added" : "rigidbody removed", null, null);
            return;
        }

        CompareFlag(Section.Rigidbodies, a.Path, "", "object active", a.Active, b.Active);
        CompareBodyState(Section.Rigidbodies, a.Path, a.ToState(), b.ToState());
    }

    private void CompareBodyState(Section section, string path, BodyState a, BodyState b)
    {
        // Shown to the millimetre and to the tenth of a degree: more digits are measurement noise.
        double moved = Distance(a.Position, b.Position);
        if (moved > options.Position)
        {
            Add(section, path, "", "moved " + Rounded(moved, "0.###") + " m", Vector(a.Position), Vector(b.Position));
        }

        double turned = AngleBetween(a.Rotation, b.Rotation);
        if (turned > options.RotationDegrees)
        {
            Add(section, path, "", "turned " + Rounded(turned, "0.#") + " deg", Vector(a.Rotation), Vector(b.Rotation));
        }

        if (Distance(a.Velocity, b.Velocity) > options.Velocity)
        {
            Add(section, path, "", "velocity", Vector(a.Velocity), Vector(b.Velocity));
        }

        if (Distance(a.AngularVelocity, b.AngularVelocity) > options.Velocity)
        {
            Add(section, path, "", "angular velocity", Vector(a.AngularVelocity), Vector(b.AngularVelocity));
        }

        CompareFlag(section, path, "", "sleeping", a.Sleeping, b.Sleeping);
        CompareFlag(section, path, "", "kinematic", a.Kinematic, b.Kinematic);
        CompareFlag(section, path, "", "gravity", a.UseGravity, b.UseGravity);
        if (Math.Abs(a.Mass - b.Mass) > options.Absolute + (options.Relative * Math.Max(Math.Abs(a.Mass), Math.Abs(b.Mass))))
        {
            Add(section, path, "", "mass", DumpValue.Format(a.Mass), DumpValue.Format(b.Mass));
        }
    }

    private static string Rounded(double value, string format) => value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    private static string Vector(double[] values) => "(" + string.Join(", ", values.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) + ")";

    private void CompareVehicle(EntryPair<VehicleEntry> pair)
    {
        VehicleEntry? a = pair.Before;
        VehicleEntry? b = pair.After;
        if (a == null || b == null)
        {
            Add(Section.Vehicles, (a ?? b)!.Path, "", a == null ? "vehicle added" : "vehicle removed", null, null);
            return;
        }

        CompareFlag(Section.Vehicles, a.Path, "", "object active", a.Active, b.Active);
        CompareFlag(Section.Vehicles, a.Path, "", "wheels", a.Wheels, b.Wheels);
        CompareFlag(Section.Vehicles, a.Path, "", "components", string.Join(", ", a.Components), string.Join(", ", b.Components));

        if (a.Body != null && b.Body != null)
        {
            CompareBodyState(Section.Vehicles, a.Path, a.Body, b.Body);
        }
        else if ((a.Body == null) != (b.Body == null))
        {
            Add(Section.Vehicles, a.Path, "", "rigidbody", a.Body == null ? "none" : "present", b.Body == null ? "none" : "present");
        }

        Dictionary<string, DumpValue> first = a.Drivetrain ?? new();
        Dictionary<string, DumpValue> second = b.Drivetrain ?? new();
        foreach (string name in first.Keys.Union(second.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            bool inFirst = first.TryGetValue(name, out DumpValue x);
            bool inSecond = second.TryGetValue(name, out DumpValue y);
            if (!(inFirst && inSecond && x.EqualsWithin(y, options.Absolute, options.Relative)))
            {
                Add(Section.Vehicles, a.Path, "Drivetrain", name, inFirst ? x.ToString() : null, inSecond ? y.ToString() : null, name);
            }
        }
    }
}
