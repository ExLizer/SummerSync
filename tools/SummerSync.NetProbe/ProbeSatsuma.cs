using System.Globalization;
using System.Text;
using SummerSync.Core.Net;
using SummerSync.Core.World;

/// <summary>
/// A made-up car put together by every probe of the session at once, a few steps a second, the way players would:
/// thirty entries of five bolts each in a simple tree (a part goes on once the one it hangs from is on, comes off
/// once nothing hangs from it and it is loose), five wires, paint, outcomes from whoever runs the car, and its eight
/// numbers, which the machine that runs the car drains and the others pour into. The host hands the numbers to the
/// first client a third of the way through, and takes them back at two thirds.
///
/// Its "game" is a model that the steps handed to it change, as a game would. Once its steps are done and answered,
/// each probe says its car's digest and its numbers' digest as a tally ("probe/satsuma/&lt;player&gt;"); it passes if
/// every other probe ended with the same, its game shows what the replication says it should, and nothing was
/// malformed.
/// </summary>
internal sealed class ProbeSatsuma
{
    private const int Parts = 25;
    private const int Wires = 5;
    private const int Bolts = 5;
    private const double FirstStep = 3.0;
    private const double StepsPerSecond = 5.0;
    private const double WaitForOthers = 10.0;
    private const double ReportEvery = 1.0;
    private const string ReportPrefix = "probe/satsuma/";

    private static readonly string[] ValueNames = { "Oilpan.Oil", "Radiator.Water", "BrakeMasterCylinder.BrakeFluidF", "BrakeMasterCylinder.BrakeFluidR", "ClutchMasterCylinder.ClutchFluid", "FuelTank.FuelLevel", "Battery.Charge", "MechanicalWear.WearClutch" };
    private static readonly float[] StartValues = { 4f, 6f, 1f, 1f, 0.5f, 20f, 12f, 80f };

    private readonly int count;
    private readonly int seed;
    private readonly AssemblyCatalog catalog = Catalog();
    private readonly AssemblyModel game;
    private readonly float[] values = new float[ValueNames.Length];
    private readonly Dictionary<byte, int[]> reports = new Dictionary<byte, int[]>();
    private Random? random;
    private AssemblyReplication? car;
    private TallyReplication? tallies;
    private bool loaded;
    private double next = FirstStep;
    private double nextReport;
    private int partNumber;
    private int ownerPhase;
    private bool everShared;
    private byte localId;

    public ProbeSatsuma(int count, int seed)
    {
        this.count = count;
        this.seed = seed;
        game = new AssemblyModel(catalog);
    }

    /// <summary>Steps this probe's game made and told.</summary>
    public int Told { get; private set; }

    public int Pours { get; private set; }

    public int Handed { get; private set; }

    private bool Done => Told >= count;

    // Where this probe's car was at the last moment its session was alive: the verdict is about then, not about a
    // replication the session's end has detached.
    private int[]? last;
    private int lastPending;
    private byte lastOwner;

    public bool Passed
    {
        get
        {
            if (car == null || last == null || !everShared || !Done || lastPending > 0 || car.Malformed > 0 || car.OtherShape > 0 || game.Digest() != car.Model.Digest() || reports.Count == 0)
            {
                return false;
            }

            foreach (int[] theirs in reports.Values)
            {
                if (!theirs.SequenceEqual(last))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public void Step(NetworkManager manager, double now)
    {
        NetSession? session = manager.Session;
        if (session == null || session.State != SessionState.Connected || manager.Assembly == null || manager.Tallies == null)
        {
            return;
        }

        localId = session.LocalPlayerId;
        if (manager.Tallies != tallies)
        {
            tallies = manager.Tallies;
            tallies.StateWanted += OnTally;
            tallies.RequestAll();
        }

        if (manager.Assembly != car)
        {
            Hook(manager.Assembly, session);
        }

        AssemblyReplication shared = car!;
        if (!loaded)
        {
            // Its world is ready: the car of its "save" (nothing on it), and the host is asked for its own.
            loaded = true;
            shared.Load(game);
            shared.RequestAll();
            random = new Random(seed * 31 + session.LocalPlayerId);
            Console.WriteLine("Satsuma: " + catalog.Count + " entries, " + catalog.TotalBolts + " bolts and " + catalog.ValueCount + " numbers (shape "
                + catalog.Shape.ToString("x8", CultureInfo.InvariantCulture) + "); " + count + " step(s) to make here, seed " + seed + ".");
        }

        everShared |= shared.Synced;
        if (shared.OwnsValues)
        {
            if (!Done)
            {
                // The engine runs: oil and charge go down by themselves while the probe works.
                values[0] = Math.Max(0f, values[0] - 0.0004f);
                values[6] = Math.Max(0f, values[6] - 0.0007f);
            }

            for (int i = 0; i < values.Length; i++)
            {
                shared.SetValue(i, values[i]);
            }
        }

        if (session.Role == SessionRole.Host)
        {
            HandOver(shared, session);
        }

        bool othersHere = session.Role != SessionRole.Host || shared.Subscribers > 0 || now >= WaitForOthers;
        if (!Done && shared.Synced && othersHere && now >= next)
        {
            next = Math.Max(next + (1.0 / StepsPerSecond), now - 0.5);
            Act(shared, session);
        }

        last = Report();
        lastPending = shared.PendingCount;
        lastOwner = shared.ValuesOwner;
        if (Done && shared.PendingCount == 0 && now >= nextReport)
        {
            nextReport = now + ReportEvery;
            tallies.Set(ReportPrefix + session.LocalPlayerId.ToString(CultureInfo.InvariantCulture), last);
        }
    }

    public string Describe()
    {
        if (car == null || last == null)
        {
            return "Satsuma: never had a car (there is none in loopback, where both ends share one game)\n";
        }

        var text = new StringBuilder();
        text.Append("Satsuma: ").Append(Told).Append(" of ").Append(count).Append(" step(s) made here, ").Append(Handed).Append(" handed to this game (")
            .Append(car.Corrections).Append(" correction(s)), ").Append(car.Refused).Append(" refused, ").Append(Pours).Append(" pour(s), ")
            .Append(car.Absorbed).Append(" absorbed, ").Append(car.Malformed).Append(" malformed, ").Append(car.OtherShape).Append(" of another shape\n");
        int[] mine = last;
        text.Append("Satsuma here: ").Append(Describe(mine)).Append(", game ").Append(AssemblyReplication.DigestText(game.Digest()))
            .Append(", numbers run by player ").Append(lastOwner).Append(lastPending > 0 ? ", " + lastPending + " step(s) not answered" : string.Empty).Append('\n');
        foreach (KeyValuePair<byte, int[]> theirs in reports.OrderBy(pair => pair.Key))
        {
            text.Append("Satsuma of player ").Append(theirs.Key).Append(": ").Append(Describe(theirs.Value)).Append(theirs.Value.SequenceEqual(mine) ? " (the same)" : "  <-- differs").Append('\n');
        }

        if (reports.Count == 0)
        {
            text.Append("Satsuma: no other probe said where its car ended\n");
        }

        return text.ToString();
    }

    private static AssemblyCatalog Catalog()
    {
        var made = new AssemblyCatalog();
        for (int i = 0; i < Parts; i++)
        {
            // Part 3 is the block, bolted on while it hangs; every sixth part is a door, whose bolts come off with it.
            AssemblyEntryFlags flags = i == 3 ? AssemblyEntryFlags.BoltsWhileLoose : i % 6 == 5 ? AssemblyEntryFlags.ClearsBoltsOnRemove : AssemblyEntryFlags.None;
            made.Add("Part" + i.ToString(CultureInfo.InvariantCulture), Bolts, AssemblyCatalog.DefaultMaxStage, Depth(i), flags);
        }

        for (int i = 0; i < Wires; i++)
        {
            made.Add("Wire" + i.ToString(CultureInfo.InvariantCulture), 0, AssemblyCatalog.DefaultMaxStage, 10, AssemblyEntryFlags.None);
        }

        foreach (string name in ValueNames)
        {
            made.AddValue(name, 0.01f);
        }

        return made;
    }

    // A part hangs from the one before half its place: a tree four or five deep.
    private static int Parent(int part)
    {
        return part == 0 ? -1 : (part - 1) / 2;
    }

    private static int Depth(int part)
    {
        int depth = 0;
        for (int at = Parent(part); at >= 0; at = Parent(at))
        {
            depth++;
        }

        return depth;
    }

    private void Hook(AssemblyReplication replication, NetSession session)
    {
        car = replication;
        loaded = false;
        replication.Define(catalog);
        if (session.Role == SessionRole.Host)
        {
            Array.Copy(StartValues, values, values.Length);
        }

        replication.StepWanted += step =>
        {
            Handed++;
            game.Apply(step);
        };
        replication.ValueWanted += (index, value) => values[index] = value;
        replication.ValueDeltaWanted += (index, amount) => values[index] += amount;
        replication.ValuesOwnerChanged += owner => Console.WriteLine("Satsuma: player " + owner + " runs the car's numbers now.");
    }

    // The host hands the numbers to a client a third of the way through, and takes them back at two thirds.
    private void HandOver(AssemblyReplication shared, NetSession session)
    {
        if (ownerPhase == 0 && Told >= count / 3)
        {
            foreach (PlayerInfo player in session.Players)
            {
                if (!player.IsLocal && shared.SetValuesOwner(player.Id))
                {
                    ownerPhase = 1;
                    break;
                }
            }
        }
        else if (ownerPhase == 1 && Told >= 2 * count / 3)
        {
            shared.SetValuesOwner(session.LocalPlayerId);
            ownerPhase = 2;
        }
    }

    // One thing a player could do to this car now, picked at random among what its game allows.
    private void Act(AssemblyReplication shared, NetSession session)
    {
        Random dice = random!;
        if (!shared.OwnsValues && dice.Next(100) < 15)
        {
            int index = dice.Next(values.Length);
            float amount = (dice.Next(1, 6) * 0.1f) * (index == 7 ? -1f : 1f);
            values[index] += amount;
            if (shared.AddToValue(index, amount))
            {
                Pours++;
            }
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            AssemblyStep? step = Pick(dice, shared.OwnsValues, session.LocalPlayerId);
            if (step != null)
            {
                game.Apply(step.Value);
                if (shared.Report(step.Value))
                {
                    Told++;
                }

                return;
            }
        }
    }

    private AssemblyStep? Pick(Random dice, bool runsTheCar, byte me)
    {
        int roll = dice.Next(100);
        if (roll < 25)
        {
            int part = dice.Next(Parts);
            int parent = Parent(part);
            return !game.IsInstalled(part) && (parent < 0 || game.IsInstalled(parent))
                ? AssemblyStep.Install(part, dice.Next(2), ((me + 1) * 100000) + ++partNumber)
                : null;
        }

        if (roll < 35)
        {
            int part = dice.Next(Parts);
            if (!game.IsInstalled(part) || game.Tightness(part) > 2 * Bolts)
            {
                return null;
            }

            for (int child = (2 * part) + 1; child <= (2 * part) + 2 && child < Parts; child++)
            {
                if (game.IsInstalled(child))
                {
                    return null;
                }
            }

            return AssemblyStep.Remove(part);
        }

        if (roll < 80)
        {
            int part = dice.Next(Parts);
            if (!game.BoltsActive(part))
            {
                return null;
            }

            int bolt = dice.Next(Bolts);
            int stage = game.Stage(part, bolt);
            bool tighter = stage == 0 || (stage < AssemblyCatalog.DefaultMaxStage && dice.Next(100) < 70);
            return AssemblyStep.Turn(part, bolt, tighter ? 1 : -1);
        }

        if (roll < 88)
        {
            int wire = Parts + dice.Next(Wires);
            return AssemblyStep.Wire(wire, !game.IsWired(wire));
        }

        if (roll < 95)
        {
            int part = dice.Next(Parts);
            return game.IsInstalled(part) ? AssemblyStep.Paint(part, dice.Next(4), dice.Next()) : null;
        }

        if (!runsTheCar)
        {
            return null;
        }

        // What the machine that runs the car decides by chance: a part worn out or mended, or one of the game's own.
        int entry = dice.Next(Parts);
        int code = dice.Next(3) switch
        {
            0 => AssemblyWire.OutcomeDamaged,
            1 => AssemblyWire.OutcomeRepaired,
            _ => AssemblyWire.FirstGameOutcome + dice.Next(4),
        };
        return AssemblyStep.Outcome(entry, code);
    }

    // Where this probe's car ended: the step of the host's order, the car's digest and the numbers' digest.
    private int[] Report()
    {
        ulong digest = car!.Model.Digest();
        ulong numbers = car.ValuesDigest();
        return new[] { (int)car.Sequence, (int)digest, (int)(digest >> 32), (int)numbers, (int)(numbers >> 32) };
    }

    private static string Describe(int[] report)
    {
        ulong digest = (uint)report[1] | ((ulong)(uint)report[2] << 32);
        ulong numbers = (uint)report[3] | ((ulong)(uint)report[4] << 32);
        return "step " + report[0] + ", digest " + AssemblyReplication.DigestText(digest) + ", numbers " + AssemblyReplication.DigestText(numbers);
    }

    private void OnTally(string name, int[] values)
    {
        if (!name.StartsWith(ReportPrefix, StringComparison.Ordinal) || values.Length != 5
            || !byte.TryParse(name.Substring(ReportPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte player))
        {
            return;
        }

        if (car != null && player != localId)
        {
            reports[player] = values;
        }
    }
}
