using System.Globalization;
using System.Text.RegularExpressions;
using SummerSync.DumpDiff;

const string Usage = """
    dumpdiff - reads the dumps written by SummerSync Dumper

    dumpdiff summary <dump> [--top <n>]
        What a dump contains.

    dumpdiff diff <before> <after> [options]
        What changed between two dumps, grouped by GameObject and FSM.
          --tolerance <n>     absolute tolerance for numbers (default 0.001)
          --relative <n>      relative tolerance for numbers (default 0.001)
          --position <m>      rigidbodies that moved less are unchanged (default 0.01)
          --rotation <deg>    rigidbodies that turned less are unchanged (default 1)
          --velocity <m/s>    velocity differences below this are ignored (default 0.05)
          --only <list>       comma separated: globals,fsms,collections,rigidbodies,vehicles
          --path <regex>      only objects whose path matches
          --ignore <regex>    hide changes whose "path :: fsm :: what" matches (repeatable)
          --noise-file <f>    file with one such regex per line (see 'dumpdiff noise')
          --no-default-noise  do not hide timer-like variable names
          --show-noise        list hidden changes too
          --max <n>           lines per FSM or object (default 40)
          -o <file>           write the report to a file

    dumpdiff noise <dump> <dump> [<dump>...] [--every-pair] [-o <file>]
        From dumps taken while the player does nothing, lists everything that changed, as a noise
        file for 'diff --noise-file'. With --every-pair, only what changed between every
        consecutive pair (things that never stop changing).

    dumpdiff classify <full dump> [-o <file>] [--review <file>]
        Heuristic DRAFT of the FSM classification (WorldLogic, Presentation, Mixed, Ignore), one
        entry per FSM as JSON. --review also writes a Markdown table with one row per FSM name,
        which is the readable form to review.
    """;

if (args.Length < 2)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

try
{
    var arguments = new Arguments(args.Skip(1));
    switch (args[0])
    {
        case "summary" when arguments.Positional.Count == 1:
            return Emit(SummaryReport.Render(DumpFile.Load(arguments.Positional[0], includeStructure: true), arguments.Int("--top", 15)), arguments);

        case "diff" when arguments.Positional.Count == 2:
            {
                DiffOptions options = ReadOptions(arguments);
                DumpFile before = DumpFile.Load(arguments.Positional[0], includeStructure: false);
                DumpFile after = DumpFile.Load(arguments.Positional[1], includeStructure: false);
                List<Change> changes = new Differ(options).Compare(before, after);
                return Emit(DiffReport.Render(before, after, changes, arguments.Flag("--show-noise"), arguments.Int("--max", 40)), arguments);
            }

        case "noise" when arguments.Positional.Count >= 2:
            {
                DiffOptions options = ReadOptions(arguments);
                options.UseDefaultNoise = false;
                List<DumpFile> dumps = arguments.Positional.Select(path => DumpFile.Load(path, includeStructure: false)).ToList();
                List<string> keys = NoiseFinder.Find(dumps, options, arguments.Flag("--every-pair"));
                Console.Error.WriteLine($"{keys.Count} thing(s) changed on their own across {dumps.Count} dumps.");
                return Emit(NoiseFinder.ToFile(keys, dumps.Select(dump => dump.Source)), arguments);
            }

        case "classify" when arguments.Positional.Count == 1:
            {
                DumpFile dump = DumpFile.Load(arguments.Positional[0], includeStructure: true);
                if (dump.Kind != SummerSync.Core.Dump.DumpFormat.KindFull)
                {
                    Console.Error.WriteLine("error: classification needs a full dump (Ctrl+F10 or 'ssdump full'); this one has no actions.");
                    return 1;
                }

                List<FsmClassification> classifications = Classifier.Classify(dump);
                foreach (var group in classifications.GroupBy(c => c.Class).OrderBy(g => g.Key))
                {
                    Console.Error.WriteLine($"{group.Key,-13} {group.Count(),6}");
                }

                string? review = arguments.Value("--review");
                if (review != null)
                {
                    File.WriteAllText(review, Classifier.ToReviewTable(dump, classifications));
                    Console.Error.WriteLine("Review table written to " + review);
                }

                return Emit(Classifier.ToJson(dump, classifications), arguments);
            }

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException)
{
    Console.Error.WriteLine("error: " + exception.Message);
    return 1;
}

static int Emit(string text, Arguments arguments)
{
    string? output = arguments.Value("-o");
    if (output == null)
    {
        Console.Out.Write(text);
    }
    else
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(output, text);
        Console.Error.WriteLine("Written to " + output);
    }

    return 0;
}

static DiffOptions ReadOptions(Arguments arguments)
{
    var options = new DiffOptions
    {
        Absolute = arguments.Double("--tolerance", 0.001),
        Relative = arguments.Double("--relative", 0.001),
        Position = arguments.Double("--position", 0.01),
        RotationDegrees = arguments.Double("--rotation", 1.0),
        Velocity = arguments.Double("--velocity", 0.05),
        UseDefaultNoise = !arguments.Flag("--no-default-noise"),
    };

    string? path = arguments.Value("--path");
    if (path != null)
    {
        options.PathFilter = new Regex(path, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    foreach (string pattern in arguments.Values("--ignore"))
    {
        options.Noise.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    string? noiseFile = arguments.Value("--noise-file");
    if (noiseFile != null)
    {
        options.Noise.AddRange(NoiseFinder.Load(noiseFile));
    }

    string? only = arguments.Value("--only");
    if (only != null)
    {
        options.Sections.Clear();
        foreach (string name in only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options.Sections.Add(Enum.Parse<Section>(name, ignoreCase: true));
        }
    }

    return options;
}

/// <summary>Minimal command line parser: positional arguments, flags and options that take one value.</summary>
internal sealed class Arguments
{
    private static readonly HashSet<string> Flags = new() { "--no-default-noise", "--show-noise", "--every-pair" };

    private readonly List<KeyValuePair<string, string>> options = new();
    private readonly HashSet<string> flags = new();

    public Arguments(IEnumerable<string> args)
    {
        string? pending = null;
        foreach (string arg in args)
        {
            if (pending != null)
            {
                options.Add(new KeyValuePair<string, string>(pending, arg));
                pending = null;
            }
            else if (Flags.Contains(arg))
            {
                flags.Add(arg);
            }
            else if (arg.StartsWith('-') && arg.Length > 1 && !char.IsDigit(arg[1]))
            {
                pending = arg;
            }
            else
            {
                Positional.Add(arg);
            }
        }

        if (pending != null)
        {
            throw new ArgumentException($"Option {pending} needs a value.");
        }
    }

    public List<string> Positional { get; } = new();

    public bool Flag(string name) => flags.Contains(name);

    public string? Value(string name) => options.Where(option => option.Key == name).Select(option => option.Value).LastOrDefault();

    public IEnumerable<string> Values(string name) => options.Where(option => option.Key == name).Select(option => option.Value);

    public int Int(string name, int fallback) => Value(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

    public double Double(string name, double fallback) => Value(name) is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;
}
