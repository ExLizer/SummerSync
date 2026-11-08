using System.Globalization;
using System.Text;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;

/// <summary>
/// The real Satsuma against a game: this probe lists the car as the game does (data/satsuma/parts.json through
/// <see cref="SatsumaAssembly"/>) and plays a player who works on it. As host with "full", its car has every part that a
/// player installs on, every bolt at its last stage and every wire made, so a game that joins is handed the steps to
/// build the whole car; otherwise its car has nothing on it and it follows the host's. A script of steps
/// ("3,install,DatabaseBody/Door_Left;4,tightall,DatabaseBody/Door_Left") is told at its seconds, as steps its player
/// made. Its "game" is a model that the steps handed to it change.
///
/// It passes if its game ended as the session's car, the whole car came (as a client), its script was told, and
/// nothing was malformed or of another shape.
/// </summary>
internal sealed class ProbeCar
{
    private const double ReportEvery = 5.0;

    private readonly SatsumaCatalog parts;
    private readonly AssemblyCatalog catalog;
    private readonly AssemblyModel game;
    private readonly bool full;
    private readonly string? loadFile;
    private readonly string? saveFile;
    private readonly List<Scripted> script = new List<Scripted>();
    private AssemblyReplication? car;
    private bool loaded;
    private bool host;
    private int scriptAt;
    private double nextReport = ReportEvery;
    private string lastLine = "Car: never had a session";
    private bool lastPassed;

    public ProbeCar(string partsFile, bool full, string? steps, string? loadFile, string? saveFile)
    {
        parts = SatsumaCatalog.Parse(File.ReadAllText(partsFile));
        string valuesFile = Path.Combine(Path.GetDirectoryName(partsFile)!, "values.json");
        SatsumaValues book = SatsumaValues.Parse(File.ReadAllText(valuesFile));
        catalog = SatsumaAssembly.Build(parts, book.Values.Select(value => value.Name).ToArray(), book.Values.Select(value => value.Step).ToArray());
        game = new AssemblyModel(catalog);
        this.full = full;
        this.loadFile = loadFile;
        this.saveFile = saveFile;
        if (!string.IsNullOrEmpty(steps))
        {
            foreach (string item in steps.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                script.Add(Scripted.Parse(item.Trim(), parts));
            }
        }

        script.Sort((a, b) => a.At.CompareTo(b.At));
    }

    /// <summary>Where the catalogue is by default: data/satsuma/parts.json of the repository the probe was built in.</summary>
    public static string DefaultParts
    {
        get { return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "satsuma", "parts.json")); }
    }

    public int Told { get; private set; }

    public int Handed { get; private set; }

    /// <summary>Numbers of the car said by the machine that runs it and shown in this probe's game.</summary>
    public int ValuesHeard { get; private set; }

    public bool Passed => lastPassed;

    public void Step(NetworkManager manager, double now)
    {
        NetSession? session = manager.Session;
        if (session == null || session.State != SessionState.Connected || manager.Assembly == null)
        {
            return;
        }

        if (manager.Assembly != car)
        {
            car = manager.Assembly;
            car.Define(catalog);
            car.StepWanted += OnStepWanted;
            car.ValueWanted += (index, value) => ValuesHeard++;
            loaded = false;
        }

        AssemblyReplication shared = car!;
        if (!loaded)
        {
            loaded = true;
            host = session.Role == SessionRole.Host;
            game.Clear();
            if (full && host)
            {
                Fill(game);
            }

            if (loadFile != null && host)
            {
                Read(game, loadFile);
            }

            shared.Load(game);
            shared.RequestAll();
            Console.WriteLine(
                "Car: the real catalogue, " + catalog.Count + " entries, " + catalog.TotalBolts + " bolts and " + catalog.ValueCount + " numbers (shape " + catalog.Shape.ToString("x8", CultureInfo.InvariantCulture)
                + "); this game's car digest " + AssemblyReplication.DigestText(game.Digest()) + (full && host ? " (the whole car)" : string.Empty) + "; " + script.Count + " scripted step(s).");
        }

        while (scriptAt < script.Count && script[scriptAt].At <= now && (host || shared.Synced))
        {
            Scripted item = script[scriptAt++];
            foreach (AssemblyStep step in item.Steps(game))
            {
                if (game.IsWellFormed(step))
                {
                    game.Apply(step);
                }

                if (shared.Report(step))
                {
                    Told++;
                }
            }

            Console.WriteLine("Car: at " + now.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + item.Text + " told.");
        }

        lastLine = Line(shared);
        lastPassed = game.Digest() == shared.Shown.Digest() && shared.Malformed == 0 && shared.OtherShape == 0 && scriptAt == script.Count && shared.PendingCount == 0
            && (host || shared.Synced);
        if (now >= nextReport)
        {
            nextReport = now + ReportEvery;
            Console.WriteLine(lastLine);
        }
    }

    public string Describe()
    {
        if (saveFile != null && car != null)
        {
            Write(car.Model, saveFile);
            return lastLine + "\nCar: the session's car written to " + saveFile + "\n";
        }

        return lastLine + "\n";
    }

    // A car as text: one line per entry that has anything, "name|installed|variant|wired|stage,stage,…".
    private void Write(AssemblyModel model, string file)
    {
        var lines = new List<string>();
        foreach (PartEntry entry in parts.Entries)
        {
            int i = entry.Index;
            var stages = new int[entry.Bolts.Length];
            bool any = model.IsInstalled(i) || model.IsWired(i);
            for (int b = 0; b < stages.Length; b++)
            {
                stages[b] = model.Stage(i, b);
                any |= stages[b] != 0;
            }

            if (any)
            {
                lines.Add(entry.Name + "|" + (model.IsInstalled(i) ? 1 : 0) + "|" + model.Variant(i) + "|" + (model.IsWired(i) ? 1 : 0) + "|" + string.Join(",", stages));
            }
        }

        File.WriteAllLines(file, lines);
    }

    private void Read(AssemblyModel model, string file)
    {
        foreach (string line in File.ReadAllLines(file))
        {
            string[] fields = line.Split('|');
            PartEntry? entry = fields.Length == 5 ? parts.Find(fields[0]) : null;
            if (entry == null)
            {
                continue;
            }

            if (fields[1] == "1")
            {
                model.SetInstalled(entry.Index, true, int.Parse(fields[2], CultureInfo.InvariantCulture), 0);
            }

            model.SetWired(entry.Index, fields[3] == "1");
            string[] stages = fields[4].Length == 0 ? new string[0] : fields[4].Split(',');
            for (int b = 0; b < stages.Length && b < entry.Bolts.Length; b++)
            {
                model.SetStage(entry.Index, b, int.Parse(stages[b], CultureInfo.InvariantCulture));
            }
        }
    }

    private string Line(AssemblyReplication shared)
    {
        int installed = 0;
        int wired = 0;
        int stages = 0;
        AssemblyModel model = shared.Model;
        for (int i = 0; i < catalog.Count; i++)
        {
            installed += model.IsInstalled(i) ? 1 : 0;
            wired += model.IsWired(i) ? 1 : 0;
            stages += model.Tightness(i);
        }

        var text = new StringBuilder();
        text.Append("Car: step ").Append(shared.Sequence).Append(", session's car ").Append(AssemblyReplication.DigestText(shared.Digest()))
            .Append(" (").Append(installed).Append(" installed, ").Append(wired).Append(" wired, ").Append(stages).Append(" bolt stages), this game ")
            .Append(AssemblyReplication.DigestText(game.Digest())).Append(game.Digest() == shared.Shown.Digest() ? " (as it should be)" : " <-- not what it should show")
            .Append("; ").Append(Told).Append(" told, ").Append(Handed).Append(" handed, ").Append(shared.Refused).Append(" refused, ")
            .Append(shared.Malformed).Append(" malformed, ").Append(shared.OtherShape).Append(" of another shape, ").Append(ValuesHeard).Append(" number(s) heard (run by player ")
            .Append(shared.ValuesOwner).Append(')')
            .Append(host ? ", " + shared.Subscribers + " client(s)" : shared.Synced ? ", synced" : ", NOT synced");
        return text.ToString();
    }

    private void OnStepWanted(AssemblyStep step)
    {
        Handed++;
        if (game.IsWellFormed(step))
        {
            game.Apply(step);
        }
    }

    // The whole car: every part a player installs, every bolt at its last stage, every wire made.
    private void Fill(AssemblyModel model)
    {
        foreach (PartEntry entry in parts.Entries)
        {
            // A tool's bolts (the hoist's hook) are not part of the car: tight, they would hang the block.
            if (SatsumaAssembly.IsTool(entry))
            {
                continue;
            }

            if (SatsumaAssembly.InstalledBySteps(entry))
            {
                model.SetInstalled(entry.Index, true, 0, 0);
            }

            if (entry.Wiring != null)
            {
                model.SetWired(entry.Index, true);
            }

            for (int b = 0; b < entry.Bolts.Length; b++)
            {
                model.SetStage(entry.Index, b, entry.Bolts[b].Stages);
            }
        }
    }

    /// <summary>One step of the script: when, and what.</summary>
    private sealed class Scripted
    {
        public double At;
        public string Text = string.Empty;
        private string verb = string.Empty;
        private int entry;
        private int number;
        private int times = 1;
        private int bolts;
        private int[] maxima = new int[0];

        public static Scripted Parse(string text, SatsumaCatalog parts)
        {
            string[] fields = text.Split(',');
            if (fields.Length < 3)
            {
                throw new FormatException("A scripted step is \"<seconds>,<verb>,<entry>[,<number>[,<times>]]\", not \"" + text + "\".");
            }

            PartEntry found = parts.Find(fields[2]) ?? throw new FormatException("No entry '" + fields[2] + "' in the catalogue.");
            var item = new Scripted
            {
                At = double.Parse(fields[0], CultureInfo.InvariantCulture),
                Text = text,
                verb = fields[1],
                entry = found.Index,
                number = fields.Length > 3 ? int.Parse(fields[3], CultureInfo.InvariantCulture) : 0,
                times = fields.Length > 4 ? int.Parse(fields[4], CultureInfo.InvariantCulture) : 1,
                bolts = found.Bolts.Length,
                maxima = found.Bolts.Select(bolt => bolt.Stages).ToArray(),
            };
            if (item.verb != "install" && item.verb != "remove" && item.verb != "tighten" && item.verb != "loosen" && item.verb != "tightall" && item.verb != "wire")
            {
                throw new FormatException("A scripted step does install, remove, tighten, loosen, tightall or wire, not '" + item.verb + "'.");
            }

            return item;
        }

        public IEnumerable<AssemblyStep> Steps(AssemblyModel game)
        {
            switch (verb)
            {
                case "install":
                    yield return AssemblyStep.Install(entry, number, 0);
                    break;
                case "remove":
                    yield return AssemblyStep.Remove(entry);
                    break;
                case "wire":
                    yield return AssemblyStep.Wire(entry, true);
                    break;
                case "tighten":
                case "loosen":
                    for (int i = 0; i < times; i++)
                    {
                        yield return AssemblyStep.Turn(entry, number, verb == "tighten" ? 1 : -1);
                    }

                    break;
                case "tightall":
                    for (int b = 0; b < bolts; b++)
                    {
                        for (int stage = game.Stage(entry, b); stage < maxima[b]; stage++)
                        {
                            yield return AssemblyStep.Turn(entry, b, 1);
                        }
                    }

                    break;
            }
        }
    }
}
