using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Vehicles;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The Satsuma's book of numbers and its chance machines, the game's side of the car's numbers in
    /// <see cref="AssemblyReplication"/>.
    ///
    /// What is assumed about the game, from the dump of 2026-10-14 22:29 and the level file (data/satsuma/values.json
    /// lists each number with where it is; the evidence is in the research notes of Phase 7): the car keeps its fluids,
    /// its battery's charge and most of its settings in the "Data" machines of the part database
    /// ("Database/DatabaseMotor/Oilpan" holds "Oil", "OilContamination" and "OilGrade"), its wear in
    /// "CarSimulation/MechanicalWear", and a tyre's health in the "Use" machine of the wheel that the hub's
    /// "Condition" machine holds in "ThisTire". Fluids are poured through the cap triggers ("CapTrigger_…", machine
    /// "Trigger"), which add to the number every frame while in "Pouring" ("Jerry can" or "Nozzle" for the fuel); the
    /// distributor and the alternator are turned by hand through a "HandRotate" machine that also turns a pivot; the
    /// engine's own simulation, which runs on every machine, burns and wears the rest.
    ///
    /// The numbers are the game's of the machine that runs the car (<see cref="AssemblyReplication.OwnsValues"/>):
    /// there, every number is read every frame and handed to the replication, and what another player poured is added
    /// to the game's variable, held in the range the game holds it in. On every other machine, the game is shown the
    /// numbers said, written every frame they differ, so that its own simulation (which burns oil and wears pistons
    /// here too) never gets anywhere; except while the local player is changing a number (a cap trigger pours, the
    /// distributor turns) and until what they did has been told: what changed then is theirs, and goes to the machine that
    /// runs the car as an amount to add, a few times a second.
    ///
    /// The state machines under the car that decide by chance (a bolt that works loose, a puncture, a part that breaks
    /// off, a worn part that fails) decide only on the machine that runs the car. On the others, each is kept from it
    /// the way the book says: switched off whole (<see cref="PausedMachine"/>), its random choice made to never pick
    /// the events that decide, sent away from the state that starts the chain (the mechanical wear, which must still
    /// hear SAVEGAME), or held by a variable (a tyre's wear rate). What they decide on the machine that runs the car
    /// travels as an outcome on the entry: a damage flag of the part database turning on or off, a tyre going flat, a
    /// part breaking off; another game is made to do the same its own way (IS_DAMAGED, REPAIR, PUNCTURE). A repair is
    /// told from whichever machine's game made it (the repair shop, the parts salesman are where the player is).
    ///
    /// On the host, the machine that simulates the car's body runs its numbers (<see cref="VehiclePresenter"/>).
    /// Offline and in the loopback test nothing here touches the game: it does only once bound.
    /// </summary>
    internal sealed class CarValueSync
    {
        private const string Resource = "SummerSync.satsuma.values.json";
        private const string DatabaseRoot = "Database";
        private const string DataMachine = "Data";
        private const string DamagedVariable = "Damaged";
        private const string DamageEvent = "IS_DAMAGED";
        private const string RepairEvent = "REPAIR";
        private const string BoltCheckMachine = "BoltCheck";
        private const string BreakOffState = "Break off";
        private const string HoldEvent = "SUMMERSYNC_HOLD";
        private const double SearchInterval = 2.0;
        private const double SettleSeconds = 5.0;
        private const double TellInterval = 0.25;
        private const double OwnerInterval = 1.0;
        private const double OwnerIntervalMost = 30.0;
        private const float Tiny = 1e-6f;

        // The game's machines that carry a hook of this class, by instance: a hook is put in once and calls whichever
        // instance is current.
        private static readonly HashSet<int> HookedBreakOffs = new HashSet<int>();
        private static readonly HashSet<int> HookedHolds = new HashSet<int>();
        private static SatsumaValues book;
        private static CarValueSync current;

        private readonly Logger log;
        private readonly VehiclePresenter vehicles;
        private readonly List<Bound> values = new List<Bound>();
        private readonly List<Chance> chances = new List<Chance>();
        private readonly List<BoltCheckWatch> boltChecks = new List<BoltCheckWatch>();
        private readonly List<DamageFlag> flags = new List<DamageFlag>();
        private readonly List<PunctureWatch> punctures = new List<PunctureWatch>();
        private readonly Dictionary<int, int> breakOffs = new Dictionary<int, int>();
        private readonly HashSet<int> held = new HashSet<int>();
        private readonly HashSet<string> warned = new HashSet<string>();
        private readonly List<AssemblyStep> deferred = new List<AssemblyStep>();

        private Transform car;
        private Transform database;
        private SatsumaCatalog parts;
        private AssemblyReplication replication;
        private bool bound;
        private bool runsCar;
        private bool blocking;
        private bool applying;
        private double clock = double.NaN;
        private double settleUntil = double.NaN;
        private double nextSearch;
        private double nextOwner;
        private double ownerWait = OwnerInterval;
        private byte ownerWanted = byte.MaxValue;
        private int blockedCount;

        public CarValueSync(Logger log)
            : this(log, null)
        {
        }

        /// <summary>With <paramref name="vehicles"/>, the host gives the car's numbers to the machine that simulates its body.</summary>
        public CarValueSync(Logger log, VehiclePresenter vehicles)
        {
            this.log = log;
            this.vehicles = vehicles;
        }

        /// <summary>Changes of numbers this machine's player made and told.</summary>
        public long Told { get; private set; }

        /// <summary>Amounts the others poured that this machine, running the car, added to its game.</summary>
        public long Added { get; private set; }

        /// <summary>Outcomes this machine's game decided and told.</summary>
        public long Outcomes { get; private set; }

        /// <summary>Outcomes of another machine this game was made to do.</summary>
        public long Applied { get; private set; }

        /// <summary>The book of numbers the mod carries; read once.</summary>
        /// <exception cref="FormatException">The file in the mod is not a valid book (a build that should not have passed its tests).</exception>
        public static SatsumaValues Book
        {
            get
            {
                if (book == null)
                {
                    using (Stream stream = typeof(CarValueSync).Assembly.GetManifestResourceStream(Resource))
                    {
                        if (stream == null)
                        {
                            throw new FormatException("The mod carries no '" + Resource + "'.");
                        }

                        using (var reader = new StreamReader(stream))
                        {
                            book = SatsumaValues.Parse(reader.ReadToEnd());
                        }
                    }
                }

                return book;
            }
        }

        /// <summary>Lists the car's numbers in the session's catalogue, in the book's order. Call it before the catalogue is in use.</summary>
        public static void AddValues(AssemblyCatalog catalog)
        {
            Book.AddTo(catalog);
        }

        /// <summary>
        /// The world is ready: finds every number, chance machine and damage flag of the car below
        /// <paramref name="carRoot"/>, and starts following <paramref name="replication"/>. What is not found now is
        /// looked for again every two seconds.
        /// </summary>
        public void Bind(Transform carRoot, SatsumaCatalog parts, AssemblyReplication replication)
        {
            Clear();
            if (carRoot == null || parts == null || replication == null)
            {
                return;
            }

            SatsumaValues numbers;
            try
            {
                numbers = Book;
                numbers.Check(parts);
            }
            catch (FormatException exception)
            {
                log.Error(LogCategory.Vehicle, "Car numbers: the book of numbers cannot be used with this part catalogue; the car's numbers are each game's own. " + exception.Message);
                return;
            }

            car = carRoot;
            this.parts = parts;
            this.replication = replication;
            // From the top of the scene: other objects are called "Database" too.
            GameObject root = GameObject.Find("/" + DatabaseRoot);
            database = root == null ? null : root.transform;
            current = this;
            bound = true;
            runsCar = true;
            blocking = false;
            clock = double.NaN;
            settleUntil = double.NaN;
            nextSearch = 0.0;
            nextOwner = 0.0;
            ownerWait = OwnerInterval;
            ownerWanted = byte.MaxValue;
            replication.ValueWanted += OnValueWanted;
            replication.ValueDeltaWanted += OnValueDeltaWanted;

            AssemblyCatalog catalog = replication.Catalog;
            var missing = new List<string>();
            int found = 0;
            for (int i = 0; i < numbers.Values.Length; i++)
            {
                CarValue spec = numbers.Values[i];
                int index = catalog == null ? -1 : catalog.FindValue(spec.Name);
                if (index < 0)
                {
                    missing.Add(spec.Name + " (not in the session's list)");
                    continue;
                }

                var value = new Bound { Spec = spec, Index = index };
                values.Add(value);
                if (Find(value))
                {
                    found++;
                }
                else
                {
                    missing.Add(spec.Name);
                }
            }

            FindBoltChecks();
            var missingChances = new List<string>();
            int breakable = 0;
            int keepable = 0;
            for (int i = 0; i < numbers.Chances.Length; i++)
            {
                CarChance spec = numbers.Chances[i];
                if (spec.EachBoltCheck)
                {
                    breakable += AddBoltChecks(spec);
                    continue;
                }

                var chance = new Chance { Spec = spec };
                chances.Add(chance);
                chance.Machine = FindMachine(spec.Place, spec.Machine);
                if (spec.Block == ChanceBlock.None)
                {
                    continue;
                }

                if (chance.Machine == null)
                {
                    missingChances.Add(spec.Name);
                }
                else
                {
                    keepable++;
                }
            }

            int watched = HookBreakOffs();
            FindFlags();
            for (int i = 0; i < numbers.Outcomes.Length; i++)
            {
                CarOutcomeWatch spec = numbers.Outcomes[i];
                PartEntry entry = parts.Find(spec.Entry);
                var watch = new PunctureWatch { Spec = spec, Entry = entry == null ? -1 : entry.Index, Machine = FindMachine(spec.Place, spec.Machine) };
                watch.Last = watch.Machine == null ? string.Empty : watch.Machine.ActiveStateName;
                punctures.Add(watch);
            }

            log.Info(
                LogCategory.Vehicle,
                "Car numbers: " + found + " of " + numbers.Values.Length + " found" + Listed(missing)
                + "; " + keepable + " chance machine(s) found" + Listed(missingChances)
                + " and " + breakable + " bolt check(s) that break off by chance; " + watched + " of " + boltChecks.Count
                + " bolt check(s) watched for breaking off; " + flags.Count
                + " damage flag(s); " + punctures.Count + " tyre(s) watched for punctures.");
        }

        /// <summary>
        /// Once per frame. <paramref name="runsCar"/>: whether this machine runs the car (simulates its body); the
        /// chance machines decide only where it does.
        /// </summary>
        public void Update(double now, bool runsCar)
        {
            if (!bound)
            {
                return;
            }

            clock = now;
            if (double.IsNaN(settleUntil))
            {
                settleUntil = now + SettleSeconds;
            }

            // Unity's null check is also true for a destroyed object.
            if (car == null)
            {
                Clear();
                return;
            }

            if (now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            this.runsCar = runsCar;
            Block(!runsCar);
            PickOwner(now);
            if (!replication.Mismatch)
            {
                bool owns = replication.OwnsValues;
                for (int i = 0; i < values.Count; i++)
                {
                    Step(values[i], owns, now);
                }
            }

            WatchFlags(now);
            WatchPunctures(now);
            if (deferred.Count > 0 && now >= settleUntil)
            {
                var waiting = new List<AssemblyStep>(deferred);
                deferred.Clear();
                for (int i = 0; i < waiting.Count; i++)
                {
                    Apply(waiting[i]);
                }
            }
        }

        /// <summary>
        /// An outcome another machine's game decided (or one that corrects this game) for this game to do its own
        /// way. Steps of other kinds are not this class's.
        /// </summary>
        public void Apply(AssemblyStep step)
        {
            if (!bound || step.Kind != AssemblyStepKind.Outcome)
            {
                return;
            }

            PartEntry entry = parts.At(step.Entry);
            if (entry == null)
            {
                return;
            }

            if (double.IsNaN(clock) || clock < settleUntil)
            {
                // A "Data" machine still loading the save would be taken out of its way to assembling the part:
                // its damage waits until the world has settled.
                deferred.Add(step);
                return;
            }

            applying = true;
            try
            {
                switch (step.Code)
                {
                    case AssemblyWire.OutcomeDamaged:
                        SetDamaged(entry, true);
                        break;
                    case AssemblyWire.OutcomeRepaired:
                        SetDamaged(entry, false);
                        break;
                    case SatsumaValues.OutcomePuncture:
                        Puncture(entry);
                        break;
                    case SatsumaValues.OutcomeBrokeOff:
                        // What breaking off does (the bolts to nothing, the parts that fall) comes as steps of its own.
                        log.Info(LogCategory.Vehicle, "Car numbers: '" + entry.Name + "' broke off in the game of player " + step.Player + ".");
                        break;
                    default:
                        WarnOnce("outcome " + step.Code, "Car numbers: an outcome " + step.Code + " on '" + entry.Name + "' that this build does not know; not done here.");
                        return;
                }

                Applied++;
            }
            finally
            {
                applying = false;
            }
        }

        /// <summary>Lets go of the game: every chance machine back as it was, nothing followed.</summary>
        public void Clear()
        {
            Release();
            if (replication != null)
            {
                replication.ValueWanted -= OnValueWanted;
                replication.ValueDeltaWanted -= OnValueDeltaWanted;
            }

            if (current == this)
            {
                current = null;
            }

            values.Clear();
            chances.Clear();
            boltChecks.Clear();
            deferred.Clear();
            flags.Clear();
            punctures.Clear();
            breakOffs.Clear();
            held.Clear();
            replication = null;
            parts = null;
            car = null;
            database = null;
            bound = false;
        }

        /// <summary>One line for the overlay, or null before there is a car.</summary>
        public string Describe()
        {
            if (!bound || replication == null)
            {
                return null;
            }

            int found = 0;
            for (int i = 0; i < values.Count; i++)
            {
                found += Available(values[i]) ? 1 : 0;
            }

            var text = new StringBuilder();
            text.Append("Car numbers: ").Append(found).Append('/').Append(values.Count).Append(replication.OwnsValues ? " run here" : " shown")
                .Append(" (P").Append(replication.ValuesOwner).Append(')');
            if (blocking)
            {
                text.Append(", ").Append(blockedCount).Append(" chance machine(s) held");
            }

            text.Append(", ").Append(Told).Append(" told, ").Append(Added).Append(" added, ").Append(Outcomes).Append('/').Append(Applied).Append(" outcome(s)");
            return text.ToString();
        }

        // ---- The numbers ----

        private void Step(Bound value, bool owns, double now)
        {
            if (!Follow(value, owns) || !Available(value))
            {
                return;
            }

            float game = Read(value);
            int index = value.Index;
            if (owns)
            {
                replication.SetValue(index, game);
                value.Known = game;
                value.HasKnown = true;
                value.Pending = 0f;
                value.AwaitWord = false;
                return;
            }

            if (!replication.HasValue(index) || !value.HasKnown)
            {
                // Nothing said yet, or just found: what the game has is where its player's changes are counted from.
                value.Known = game;
                value.HasKnown = true;
                return;
            }

            CarValue spec = value.Spec;
            bool gated = spec.While.Length > 0;
            bool open = gated && GateOpen(value);
            float change = game - value.Known;
            if (Math.Abs(change) > Tiny && (!gated || open) && spec.CountsAsLocal(change))
            {
                if (value.Pending == 0f)
                {
                    value.TellAt = now + TellInterval;
                }

                value.Pending += change;
                value.Known = game;
            }

            if (value.Pending != 0f && (now >= value.TellAt || (gated && !open)))
            {
                if (replication.AddToValue(index, value.Pending))
                {
                    Told++;
                }

                value.Pending = 0f;
            }

            if (open || value.Pending != 0f || value.AwaitWord)
            {
                // The player is at it, or what they did is not in the numbers said yet.
                return;
            }

            float wanted = replication.ShownValue(index);
            if (game != wanted)
            {
                Write(value, wanted);
                value.Known = wanted;
            }
        }

        private void OnValueWanted(int index, float number)
        {
            Bound value = ByIndex(index);
            if (value == null)
            {
                return;
            }

            value.AwaitWord = false;
            if (replication != null && replication.OwnsValues && Available(value))
            {
                // This machine has just started running the car: it starts from what it showed.
                Write(value, number);
                value.Known = number;
                value.HasKnown = true;
                value.Pending = 0f;
            }
        }

        private void OnValueDeltaWanted(int index, float amount)
        {
            Bound value = ByIndex(index);
            if (value == null || !Available(value))
            {
                WarnOnce(
                    "lost " + index,
                    "Car numbers: another player changed '" + (value == null ? index.ToString() : value.Spec.Name) + "' by " + amount
                    + ", but this game, which runs the car, does not have that number now; the change is lost.");
                return;
            }

            float number = value.Spec.Clamp(Read(value) + amount);
            Write(value, number);
            value.Known = number;
            Added++;
        }

        private Bound ByIndex(int index)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i].Index == index)
                {
                    return values[i];
                }
            }

            return null;
        }

        // A number found through a game object variable (the tyre on a hub): whether there is one to follow now. A wheel
        // that is not on the car does not count: the game may keep pointing at the one that came off.
        private bool Follow(Bound value, bool owns)
        {
            if (value.Through == null)
            {
                return true;
            }

            GameObject target = value.Through.Value;
            if (target != null && !target.transform.IsChildOf(car))
            {
                target = null;
            }

            if (target == value.Followed)
            {
                return target != null;
            }

            value.Followed = target;
            value.Float = null;
            value.Int = null;
            value.HasKnown = false;
            value.Pending = 0f;

            // Another tyre: the number said until the machine that runs the car says this one's is the last tyre's.
            value.AwaitWord = !owns;
            if (target == null)
            {
                return false;
            }

            PlayMakerFSM machine = DoorSync.FindMachine(target.transform, value.Spec.FollowMachine);
            Bind(value, machine);
            return value.Float != null || value.Int != null;
        }

        private static bool Available(Bound value)
        {
            return value.Float != null || value.Int != null;
        }

        // Whether everything a number needs has been found (a followed number: its hub's variable).
        private static bool Complete(Bound value)
        {
            if (value.Spec.FollowVariable != null ? value.Through == null : !Available(value))
            {
                return false;
            }

            if (value.Spec.Mirror != null && value.Mirror == null)
            {
                return false;
            }

            for (int i = 0; i < value.Gates.Length; i++)
            {
                if (value.Gates[i].Machine == null)
                {
                    return false;
                }
            }

            return true;
        }

        private static float Read(Bound value)
        {
            return value.Float != null ? value.Float.Value : value.Int.Value;
        }

        private static void Write(Bound value, float number)
        {
            if (value.Float != null)
            {
                value.Float.Value = number;
            }
            else
            {
                value.Int.Value = (int)Math.Round(number);
            }

            // Unity's null check is also true for a destroyed object.
            if (value.Mirror != null)
            {
                Vector3 angles = value.Mirror.localEulerAngles;
                angles[value.Spec.MirrorAxis] = number;
                value.Mirror.localEulerAngles = angles;
            }
        }

        private static bool GateOpen(Bound value)
        {
            for (int i = 0; i < value.Gates.Length; i++)
            {
                PlayMakerFSM machine = value.Gates[i].Machine;

                // Unity's null check is also true for a destroyed object.
                if (machine != null && machine.enabled && machine.gameObject.activeInHierarchy && value.Gates[i].Spec.Holds(machine.ActiveStateName))
                {
                    return true;
                }
            }

            return false;
        }

        // Finds what a number needs; true when the number itself was found.
        private bool Find(Bound value)
        {
            CarValue spec = value.Spec;
            if (value.Gates == null)
            {
                value.Gates = new Gate[spec.While.Length];
                for (int i = 0; i < spec.While.Length; i++)
                {
                    value.Gates[i] = new Gate { Spec = spec.While[i] };
                }
            }

            for (int i = 0; i < value.Gates.Length; i++)
            {
                if (value.Gates[i].Machine == null)
                {
                    value.Gates[i].Machine = FindMachine(value.Gates[i].Spec.Place, value.Gates[i].Spec.Machine);
                }
            }

            if (spec.Mirror != null && value.Mirror == null)
            {
                value.Mirror = Resolve(spec.Mirror);
            }

            if (spec.FollowVariable != null)
            {
                if (value.Through == null)
                {
                    PlayMakerFSM hub = spec.Entry != null ? MachineAt(Entry(spec.Entry), spec.Machine) : FindMachine(spec.Place, spec.Machine);
                    value.Through = hub == null ? null : hub.FsmVariables.FindFsmGameObject(spec.FollowVariable);
                }

                return value.Through != null;
            }

            if (!Available(value))
            {
                PlayMakerFSM machine = spec.Entry != null ? MachineAt(Entry(spec.Entry), spec.Machine) : FindMachine(spec.Place, spec.Machine);
                Bind(value, machine);
            }

            return Available(value);
        }

        private static void Bind(Bound value, PlayMakerFSM machine)
        {
            if (machine == null)
            {
                return;
            }

            if (value.Spec.Kind == CarValueKind.Int)
            {
                value.Int = machine.FsmVariables.FindFsmInt(value.Spec.Variable);
            }
            else
            {
                value.Float = machine.FsmVariables.FindFsmFloat(value.Spec.Variable);
            }
        }

        // Looks again for what was missing: parts whose machines appear later, chance machines that have never run.
        private void Search()
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (!Complete(values[i]))
                {
                    Find(values[i]);
                }
            }

            for (int i = 0; i < chances.Count; i++)
            {
                Chance chance = chances[i];

                // Unity's null check is also true for a destroyed object.
                if (chance.Machine == null && chance.Spec.Place != null)
                {
                    chance.Machine = FindMachine(chance.Spec.Place, chance.Spec.Machine);
                }
            }

            HookBreakOffs();
            for (int i = 0; i < flags.Count; i++)
            {
                if (flags[i].Damaged == null)
                {
                    FindFlag(flags[i]);
                }
            }

            for (int i = 0; i < punctures.Count; i++)
            {
                if (punctures[i].Machine == null)
                {
                    punctures[i].Machine = FindMachine(punctures[i].Spec.Place, punctures[i].Spec.Machine);
                }
            }
        }

        // ---- Who runs the numbers ----

        // On the host: the machine that simulates the car's body runs its numbers. The replication refuses a player who
        // has not asked for the car yet; it is asked again a second later, then less and less often. On a client it
        // refuses, quietly.
        private void PickOwner(double now)
        {
            if (vehicles == null || now < nextOwner)
            {
                return;
            }

            byte player;
            if (!vehicles.TryGetSimulator(car, out player))
            {
                player = Protocol.HostPlayerId;
            }

            if (player != ownerWanted)
            {
                ownerWanted = player;
                ownerWait = OwnerInterval;
            }

            nextOwner = now + ownerWait;
            if (player != replication.ValuesOwner && !replication.SetValuesOwner(player))
            {
                ownerWait = Math.Min(ownerWait * 2.0, OwnerIntervalMost);
            }
        }

        // ---- The chance machines ----

        // The bolt checks of the catalogue (machine "BoltCheck"; a wheel's bolts are counted by its "Use"). Their objects
        // are the parts and the copies in the car, which are all in the scene from the start, switched on or not.
        private void FindBoltChecks()
        {
            for (int e = 0; e < parts.Entries.Length; e++)
            {
                PartEntry entry = parts.Entries[e];

                // A tool's (the hoist's hook) goes through its "Break off" whenever its bolts come out: that is letting go
                // of the block, not a part breaking off by chance.
                if (SatsumaAssembly.IsTool(entry))
                {
                    continue;
                }

                for (int c = 0; c < entry.BoltChecks.Length; c++)
                {
                    if (entry.BoltChecks[c].Machine == BoltCheckMachine)
                    {
                        boltChecks.Add(new BoltCheckWatch { Entry = entry.Index, Machine = FindMachine(entry.BoltChecks[c].Place, BoltCheckMachine) });
                    }
                }
            }
        }

        private int AddBoltChecks(CarChance spec)
        {
            int found = 0;
            for (int i = 0; i < boltChecks.Count; i++)
            {
                PlayMakerFSM machine = boltChecks[i].Machine;
                if (machine != null && machine.FsmName == spec.Machine && DoorSync.HasState(machine, spec.State))
                {
                    chances.Add(new Chance { Spec = spec, Machine = machine });
                    found++;
                }
            }

            return found;
        }

        // Every bolt check of the catalogue that can break its part off tells, on the machine that runs the car, that it
        // did. Hooked once per machine; the hook calls whichever instance of this class is current. Returns how many
        // are watched.
        private int HookBreakOffs()
        {
            for (int i = 0; i < boltChecks.Count; i++)
            {
                BoltCheckWatch watch = boltChecks[i];
                PlayMakerFSM machine = watch.Machine;

                // Unity's null check is also true for a destroyed object.
                if (watch.Hooked || machine == null || !Prepare(machine))
                {
                    continue;
                }

                watch.Hooked = true;
                if (!DoorSync.HasState(machine, BreakOffState))
                {
                    continue;
                }

                int id = machine.GetInstanceID();
                breakOffs[id] = watch.Entry;
                if (!HookedBreakOffs.Contains(id))
                {
                    Action hook = () => OnBreakOff(id);
                    if (machine.FsmInject(BreakOffState, hook, false, 0, false))
                    {
                        HookedBreakOffs.Add(id);
                    }
                }
            }

            return breakOffs.Count;
        }

        // A machine's actions are loaded when it first runs; a part switched off since the world loaded has not run.
        // MSCLoader loads them on demand (InitializeFSM), as it does for every hook and transition it adds. False when
        // the game's machine will not give them.
        private bool Prepare(PlayMakerFSM machine)
        {
            if (machine.Fsm != null && machine.Fsm.Initialized)
            {
                return true;
            }

            try
            {
                machine.InitializeFSM();
                return machine.Fsm != null && machine.Fsm.Initialized;
            }
            catch (Exception exception)
            {
                WarnOnce("init " + machine.GetInstanceID(), "Car numbers: the machine '" + machine.FsmName + "' on '" + machine.name + "' does not give its actions yet (" + exception.Message + "); tried again later.");
                return false;
            }
        }

        private static void OnBreakOff(int machine)
        {
            CarValueSync sync = current;
            if (sync != null)
            {
                sync.BrokeOff(machine);
            }
        }

        private void BrokeOff(int machine)
        {
            int entry;
            if (!bound || applying || !runsCar || !breakOffs.TryGetValue(machine, out entry))
            {
                return;
            }

            if (replication.Report(AssemblyStep.Outcome(entry, SatsumaValues.OutcomeBrokeOff)))
            {
                Outcomes++;
            }

            log.Info(LogCategory.Vehicle, "Car numbers: '" + parts.Entries[entry].Name + "' broke off here; told.");
        }

        private static void OnHoldPoint(int id, PlayMakerFSM machine)
        {
            CarValueSync sync = current;
            if (sync != null && sync.held.Contains(id))
            {
                // Inside the machine's own action: it goes to the hold's state once this action is done, and the rest of
                // the state is not run.
                machine.SendEvent(HoldEvent);
            }
        }

        // Keeps every chance machine from deciding (on a machine that does not run the car), or lets every one go.
        private void Block(bool block)
        {
            if (block != blocking)
            {
                blocking = block;
                if (!block)
                {
                    Release();
                    log.Info(LogCategory.Vehicle, "Car numbers: this machine runs the car; its chance machines decide here.");
                    return;
                }

                log.Info(LogCategory.Vehicle, "Car numbers: another machine runs the car; " + chances.Count + " chance machine(s) are kept from deciding here.");
            }

            if (!block)
            {
                return;
            }

            for (int i = 0; i < chances.Count; i++)
            {
                Chance chance = chances[i];
                if (!chance.Applied)
                {
                    if (clock >= chance.RetryAt)
                    {
                        KeepFromDeciding(chance);
                    }
                }
                else if (chance.Paused != null && chance.Machine != null && chance.Machine.enabled)
                {
                    // The game switched it on again (EnableFSM). It no longer restarts when switched on (the pause
                    // took that away until it lets go): off again, where it is.
                    chance.Machine.enabled = false;
                }
                else if (chance.Forced != null && chance.Forced.Value != chance.Spec.Value)
                {
                    // The game set it (another tyre on the hub): that is what to give back, and it is held again.
                    chance.ForcedWas = chance.Forced.Value;
                    chance.Forced.Value = chance.Spec.Value;
                }
            }
        }

        private void KeepFromDeciding(Chance chance)
        {
            PlayMakerFSM machine = chance.Machine;

            // Unity's null check is also true for a destroyed object.
            if (machine == null || chance.Spec.Block == ChanceBlock.None)
            {
                return;
            }

            switch (chance.Spec.Block)
            {
                case ChanceBlock.Pause:
                    if (machine.enabled)
                    {
                        chance.Paused = new PausedMachine(machine);
                    }

                    chance.Applied = true;
                    break;
                case ChanceBlock.Weights:
                    if (!Prepare(machine))
                    {
                        chance.RetryAt = clock + SearchInterval;
                        return;
                    }

                    ZeroWeights(chance);
                    chance.Applied = true;
                    break;
                case ChanceBlock.Hold:
                    if (!Prepare(machine))
                    {
                        chance.RetryAt = clock + SearchInterval;
                        return;
                    }

                    if (!HookHold(chance))
                    {
                        WarnOnce("hold " + chance.Spec.Name, "Car numbers: '" + chance.Spec.Name + "' has no state '" + chance.Spec.State + "' or '" + chance.Spec.To + "' in this game; it decides here too.");
                        chance.Applied = true;
                        return;
                    }

                    held.Add(machine.GetInstanceID());
                    chance.Applied = true;
                    break;
                case ChanceBlock.Force:
                    FsmFloat variable = machine.FsmVariables.FindFsmFloat(chance.Spec.Variable);
                    if (variable == null)
                    {
                        WarnOnce("force " + chance.Spec.Name, "Car numbers: '" + chance.Spec.Name + "' has no float '" + chance.Spec.Variable + "' in this game; it decides here too.");
                        chance.Applied = true;
                        return;
                    }

                    chance.Forced = variable;
                    chance.ForcedWas = variable.Value;
                    variable.Value = chance.Spec.Value;
                    chance.Applied = true;
                    break;
            }

            blockedCount++;
        }

        private void ZeroWeights(Chance chance)
        {
            FsmState[] states = chance.Machine.Fsm.States;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                if (states[s].Name != chance.Spec.State)
                {
                    continue;
                }

                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    var random = actions[a] as SendRandomEvent;
                    if (random == null || random.events == null || random.weights == null)
                    {
                        continue;
                    }

                    for (int e = 0; e < random.events.Length && e < random.weights.Length; e++)
                    {
                        if (random.events[e] != null && Array.IndexOf(chance.Spec.Events, random.events[e].Name) >= 0)
                        {
                            if (chance.Zeroed == null)
                            {
                                chance.Zeroed = new List<Zeroed>();
                            }

                            chance.Zeroed.Add(new Zeroed { Action = random, Slot = e, Was = random.weights[e] });
                            random.weights[e] = new FsmFloat { Value = 0f };
                        }
                    }
                }
            }

            if (chance.Zeroed == null)
            {
                WarnOnce(
                    "weights " + chance.Spec.Name + " " + chance.Machine.GetInstanceID(),
                    "Car numbers: '" + chance.Spec.Name + "' has no random choice of " + string.Join(", ", chance.Spec.Events) + " in its state '" + chance.Spec.State + "' in this game; it decides here too.");
            }
        }

        // The mod's event to the hold's state, and the hook first in the state the chain starts at. Once per machine.
        private static bool HookHold(Chance chance)
        {
            PlayMakerFSM machine = chance.Machine;
            if (!DoorSync.HasState(machine, chance.Spec.State) || !DoorSync.HasState(machine, chance.Spec.To))
            {
                return false;
            }

            int id = machine.GetInstanceID();
            if (HookedHolds.Contains(id))
            {
                return true;
            }

            if (machine.GetGlobalTransition(HoldEvent) == null)
            {
                machine.AddGlobalTransition(HoldEvent, chance.Spec.To);
            }

            Action hook = () => OnHoldPoint(id, machine);
            if (!machine.FsmInject(chance.Spec.State, hook, false, 0, false))
            {
                return false;
            }

            HookedHolds.Add(id);
            return true;
        }

        // Every chance machine back as it was: switched on where it is, its weights its own, its variable its own.
        private void Release()
        {
            for (int i = 0; i < chances.Count; i++)
            {
                Chance chance = chances[i];
                if (chance.Paused != null)
                {
                    chance.Paused.Resume();
                    chance.Paused = null;
                }

                if (chance.Zeroed != null)
                {
                    for (int z = 0; z < chance.Zeroed.Count; z++)
                    {
                        Zeroed zeroed = chance.Zeroed[z];
                        zeroed.Action.weights[zeroed.Slot] = zeroed.Was;
                    }

                    chance.Zeroed = null;
                }

                if (chance.Forced != null)
                {
                    chance.Forced.Value = chance.ForcedWas;
                    chance.Forced = null;
                }

                chance.Applied = false;
            }

            held.Clear();
            blockedCount = 0;
            blocking = false;
        }

        // ---- The outcomes ----

        private void FindFlags()
        {
            for (int e = 0; e < parts.Entries.Length; e++)
            {
                PartEntry entry = parts.Entries[e];
                if (!entry.Has(DamagedVariable))
                {
                    continue;
                }

                var flag = new DamageFlag { Entry = entry };
                flags.Add(flag);
                FindFlag(flag);
            }
        }

        private void FindFlag(DamageFlag flag)
        {
            PlayMakerFSM machine = MachineAt(Entry(flag.Entry.Name), DataMachine);
            if (machine == null)
            {
                return;
            }

            flag.Machine = machine;
            flag.Damaged = machine.FsmVariables.FindFsmBool(DamagedVariable);
            flag.Was = flag.Damaged != null && flag.Damaged.Value;
            flag.DamageEvent = machine.GetGlobalTransition(DamageEvent) != null;
            flag.RepairEvent = machine.GetGlobalTransition(RepairEvent) != null;
        }

        // A damage flag of the part database that turned: told from the machine that runs the car, and a repair from
        // any machine (the shop and the salesman are wherever the player is). What the save writes at loading is not news.
        private void WatchFlags(double now)
        {
            bool settled = now >= settleUntil;
            for (int i = 0; i < flags.Count; i++)
            {
                DamageFlag flag = flags[i];
                if (flag.Damaged == null || flag.Damaged.Value == flag.Was)
                {
                    continue;
                }

                bool damaged = flag.Damaged.Value;
                flag.Was = damaged;
                if (!settled)
                {
                    continue;
                }

                if (!runsCar && damaged)
                {
                    WarnOnce(
                        "damaged " + flag.Entry.Name,
                        "Car numbers: this game damaged '" + flag.Entry.Name + "' on a machine that does not run the car; not told (the machine that runs it decides).");
                    continue;
                }

                if (replication.Report(AssemblyStep.Outcome(flag.Entry.Index, damaged ? AssemblyWire.OutcomeDamaged : AssemblyWire.OutcomeRepaired)))
                {
                    Outcomes++;
                    log.Info(LogCategory.Vehicle, "Car numbers: '" + flag.Entry.Name + "' " + (damaged ? "damaged" : "repaired") + " here; told.");
                }
            }
        }

        // A hub whose machine left its resting state for a flat tyre: told from the machine that runs the car.
        private void WatchPunctures(double now)
        {
            bool settled = now >= settleUntil;
            for (int i = 0; i < punctures.Count; i++)
            {
                PunctureWatch watch = punctures[i];

                // Unity's null check is also true for a destroyed object.
                if (watch.Machine == null)
                {
                    continue;
                }

                string state = watch.Machine.ActiveStateName;
                if (state == watch.Last)
                {
                    continue;
                }

                bool flat = watch.Last == watch.Spec.From && Array.IndexOf(watch.Spec.Into, state) >= 0;
                watch.Last = state;
                if (flat && settled && runsCar && !applying && watch.Entry >= 0 && replication.Report(AssemblyStep.Outcome(watch.Entry, watch.Spec.Code)))
                {
                    Outcomes++;
                    log.Info(LogCategory.Vehicle, "Car numbers: the tyre of '" + watch.Spec.Entry + "' went flat here; told.");
                }
            }
        }

        private void SetDamaged(PartEntry entry, bool damaged)
        {
            DamageFlag flag = null;
            for (int i = 0; i < flags.Count; i++)
            {
                if (flags[i].Entry == entry)
                {
                    flag = flags[i];
                    break;
                }
            }

            if (flag == null || flag.Damaged == null)
            {
                return;
            }

            if (damaged)
            {
                // The game's way where it has one: the entry's own "Damaged" state swaps the part for its broken look.
                if (flag.DamageEvent && flag.Machine.enabled && flag.Machine.gameObject.activeInHierarchy)
                {
                    flag.Machine.SendEvent(DamageEvent);
                }

                flag.Damaged.Value = true;
            }
            else
            {
                // As the repair shop and the parts salesman do it: the flag off, then REPAIR.
                flag.Damaged.Value = false;
                if (flag.RepairEvent && flag.Machine.enabled && flag.Machine.gameObject.activeInHierarchy)
                {
                    flag.Machine.SendEvent(RepairEvent);
                }
            }

            flag.Was = flag.Damaged.Value;
        }

        private void Puncture(PartEntry entry)
        {
            for (int i = 0; i < punctures.Count; i++)
            {
                PunctureWatch watch = punctures[i];
                PlayMakerFSM machine = watch.Machine;

                // Unity's null check is also true for a destroyed object.
                if (watch.Entry != entry.Index || machine == null)
                {
                    continue;
                }

                if (machine.enabled && machine.gameObject.activeInHierarchy && machine.ActiveStateName == watch.Spec.From)
                {
                    machine.SendEvent(watch.Spec.Event);
                    watch.Last = machine.ActiveStateName;
                }

                // The game set the tyre's health to nothing: the health said is the old tyre's until the next word.
                AwaitWordOn(watch.Spec.Place);
            }
        }

        private void AwaitWordOn(PartPlace hub)
        {
            for (int i = 0; i < values.Count; i++)
            {
                CarValue spec = values[i].Spec;
                if (spec.FollowVariable != null && spec.Place != null && spec.Place.Text == hub.Text)
                {
                    values[i].AwaitWord = true;
                }
            }
        }

        // ---- Finding the game's ----

        private Transform Entry(string name)
        {
            // Unity's null check is also true for a destroyed object.
            return database == null ? null : database.Find(name);
        }

        private static PlayMakerFSM MachineAt(Transform at, string machine)
        {
            return at == null ? null : DoorSync.FindMachine(at, machine);
        }

        private PlayMakerFSM FindMachine(PartPlace place, string machine)
        {
            return MachineAt(Resolve(place), machine);
        }

        // Where a place of the book is: below the car, from the top of the scene, or below the part object of an entry
        // (the object its "Data" machine holds in its part variable, which moves with the part).
        private Transform Resolve(PartPlace place)
        {
            if (place == null)
            {
                return null;
            }

            switch (place.Kind)
            {
                case PartPlaceKind.Car:
                    return car.Find(place.Path);
                case PartPlaceKind.Scene:
                    int slash = place.Path.IndexOf('/');
                    GameObject top = GameObject.Find("/" + (slash < 0 ? place.Path : place.Path.Substring(0, slash)));
                    if (top == null)
                    {
                        return null;
                    }

                    return slash < 0 ? top.transform : top.transform.Find(place.Path.Substring(slash + 1));
                case PartPlaceKind.Part:
                    PartEntry entry = parts.Find(place.Entry);
                    PlayMakerFSM data = entry == null || entry.PartVariable == null ? null : MachineAt(Entry(entry.Name), DataMachine);
                    FsmGameObject part = data == null ? null : data.FsmVariables.FindFsmGameObject(entry.PartVariable);
                    if (part == null || part.Value == null)
                    {
                        return null;
                    }

                    return place.Path.Length == 0 ? part.Value.transform : part.Value.transform.Find(place.Path);
                default:
                    return null;
            }
        }

        private void WarnOnce(string key, string message)
        {
            if (warned.Add(key))
            {
                log.Warning(LogCategory.Vehicle, message);
            }
        }

        private static string Listed(List<string> missing)
        {
            return missing.Count == 0 ? string.Empty : " (missing: " + string.Join(", ", missing.ToArray()) + ")";
        }

        private sealed class Bound
        {
            public CarValue Spec;
            public int Index;
            public FsmFloat Float;
            public FsmInt Int;
            public FsmGameObject Through;
            public GameObject Followed;
            public Gate[] Gates;
            public Transform Mirror;

            // What the game had when this machine last wrote it or counted its player's change.
            public float Known;
            public bool HasKnown;

            // The player's change not told yet, and when it is.
            public float Pending;
            public double TellAt;

            // The number said is not this one's yet (another tyre on the hub, a tyre gone flat here).
            public bool AwaitWord;
        }

        private sealed class Gate
        {
            public FsmGate Spec;
            public PlayMakerFSM Machine;
        }

        private sealed class BoltCheckWatch
        {
            public int Entry;
            public PlayMakerFSM Machine;

            // Looked at once its actions are loaded: hooked, or found without a state that breaks the part off.
            public bool Hooked;
        }

        private sealed class Chance
        {
            public CarChance Spec;
            public PlayMakerFSM Machine;
            public bool Applied;
            public double RetryAt;
            public PausedMachine Paused;
            public List<Zeroed> Zeroed;
            public FsmFloat Forced;
            public float ForcedWas;
        }

        private struct Zeroed
        {
            public SendRandomEvent Action;
            public int Slot;
            public FsmFloat Was;
        }

        private sealed class DamageFlag
        {
            public PartEntry Entry;
            public PlayMakerFSM Machine;
            public FsmBool Damaged;
            public bool Was;
            public bool DamageEvent;
            public bool RepairEvent;
        }

        private sealed class PunctureWatch
        {
            public CarOutcomeWatch Spec;
            public int Entry;
            public PlayMakerFSM Machine;
            public string Last;
        }
    }
}
