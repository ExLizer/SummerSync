using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>Messages of <see cref="AssemblyReplication"/>. Every one carries the catalogue's shape after its type.</summary>
    public static class AssemblyWire
    {
        /// <summary>Client to host, reliable: steps this player's game made, numbered by this client.</summary>
        public const byte MessageSteps = NpcWire.MessageState + 1;

        /// <summary>
        /// Host to every client that asked for the car, reliable: steps in the host's order, each with the player whose
        /// game made it and that player's number for it; and, in the same order, the steps of a client the host refused.
        /// </summary>
        public const byte MessageSequenced = MessageSteps + 1;

        /// <summary>Client to host, reliable: this world is ready; send the whole car and every step from now on.</summary>
        public const byte MessageRequest = MessageSteps + 2;

        /// <summary>Host to one client, reliable: the whole car as of a step of the host's order, in pieces.</summary>
        public const byte MessageSnapshot = MessageSteps + 3;

        /// <summary>Host to every client that asked for the car, reliable: the car's digest as of a step of the host's order.</summary>
        public const byte MessageDigest = MessageSteps + 4;

        /// <summary>
        /// The machine that runs the car to the host, and the host to the others, reliable: the numbers of the car that
        /// changed, and how many of the others' changes they hold.
        /// </summary>
        public const byte MessageValues = MessageSteps + 5;

        /// <summary>Client to host, reliable: by how much this player changed numbers of the car (oil poured), numbered by this client.</summary>
        public const byte MessagePoured = MessageSteps + 6;

        /// <summary>Host to the client that runs the car, reliable: the others' changes, numbered by the host, to add.</summary>
        public const byte MessageAbsorb = MessageSteps + 7;

        /// <summary>Host to every client that asked for the car, reliable: which machine runs the car's numbers now.</summary>
        public const byte MessageOwner = MessageSteps + 8;

        /// <summary>Who made a step the replication made up to correct this game, and an owner that is nobody.</summary>
        public const byte NoPlayer = 255;

        /// <summary>An <see cref="AssemblyStepKind.Outcome"/> that leaves the entry damaged.</summary>
        public const int OutcomeDamaged = 1;

        /// <summary>An <see cref="AssemblyStepKind.Outcome"/> that leaves the entry repaired.</summary>
        public const int OutcomeRepaired = 2;

        /// <summary>The game side's outcome codes start here; below it, the codes are the replication's.</summary>
        public const int FirstGameOutcome = 16;

        /// <summary>Steps of one message at most.</summary>
        public const int MaxStepsPerMessage = 200;

        // A message stops growing here, well inside one packet.
        internal const int MessageBytes = 960;

        // The most one step takes in a message of the host's order.
        internal const int MaxRecordBytes = 32;

        // The most one entry takes in the whole car.
        internal const int MaxEntryBytes = 24 + AssemblyCatalog.MaxBolts;

        // The head of a step the host refused, in its order.
        internal const byte RefusedBit = 0x80;

        // The "piece" of the whole car that says the client's catalogue is not the host's.
        internal const byte Mismatch = 255;
    }

    /// <summary>
    /// Putting a car together between several players with nothing out of step: installing and removing parts,
    /// turning bolts, wiring, painting, and what the machine that runs the car decides by chance, as steps on the
    /// entries of an <see cref="AssemblyCatalog"/> that both sides list the same way.
    ///
    /// A step is made by the game where the player acted: that game has already made it, and tells the host
    /// (<see cref="Report"/>). The host's car is the car. It checks the step against its own (installing into a full
    /// slot, a bolt past its last stage), gives what it takes the next number of its order, makes its own game take
    /// it, and tells every client that asked for the car, the one that made it too. The steps of a client it refuses
    /// are told in the same order. Every machine keeps the car as the host's order leaves it (<see cref="Model"/>).
    ///
    /// A client keeps its own steps the host has not answered yet, and what its game shows (<see cref="Shown"/>):
    /// the host's car with those steps on top, as far as the host is going to take them. When a step of someone else
    /// fits with what this game shows, the game is handed it as it is; when it does not (two players put a part into
    /// the same slot at once, and the other was first), the game is handed the steps that take it to the host's car
    /// and its own steps that still stand: remove the part, put the other's in. A refused step is undone the same way.
    /// So the game is only ever handed steps (<see cref="StepWanted"/>), and never a result to write.
    ///
    /// A client whose world is ready asks for the whole car (<see cref="RequestAll"/>): it comes in pieces as of a
    /// number of the host's order, every step after that follows, and the game is handed the steps that take what it
    /// shows (<see cref="Load"/>) to the host's car, in an order a game can make them. Every few seconds the host says
    /// its car's digest as of a number; a client whose car has another digest at that number says so in the log and
    /// asks for the whole car again.
    ///
    /// Besides the steps, a book of the car's numbers that change by themselves (fluids, wear, the battery): they are
    /// the game's of the machine that runs the car, which the host names (<see cref="SetValuesOwner"/>). That machine
    /// says the numbers that changed a few times a second; a player of another machine who changes one (pours oil)
    /// sees it change at once, and the machine that runs the car adds it and says it again, saying too which of the
    /// others' changes it holds, so nothing counts twice. A machine that starts running the car starts from the last
    /// numbers said and every change since.
    ///
    /// This class only carries the news. Reading and moving the game's parts is the game side's.
    /// </summary>
    public sealed partial class AssemblyReplication
    {
        /// <summary>Seconds between two digests of the host.</summary>
        public const double DigestInterval = 5.0;

        /// <summary>Seconds between two messages of the car's numbers from the machine that runs it: four a second.</summary>
        public const double ValueInterval = 0.25;

        /// <summary>Seconds after which a number that changed by less than its step is said anyway.</summary>
        public const double ValueRefresh = 2.0;

        /// <summary>Steps of this client the host has not answered, at most; past that, this client asks for the whole car again.</summary>
        public const int MaxPending = 4096;

        private const double MalformedLogInterval = 5.0;
        private const int MaxBatchCount = 1024;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly PacketWriter writer = new PacketWriter(1024);
        private readonly PacketWriter batch = new PacketWriter(1024);
        private readonly PacketReader reader = new PacketReader();
        private readonly List<Pending> pending = new List<Pending>();
        private readonly List<Record> records = new List<Record>();
        private readonly List<AssemblyStep> corrections = new List<AssemblyStep>();
        private readonly List<byte> subscribers = new List<byte>();
        private readonly List<WaitingRequest> waitingRequests = new List<WaitingRequest>();

        private AssemblyCatalog catalog;
        private uint shape;
        private AssemblyModel model;
        private AssemblyModel shown;
        private AssemblyModel target;
        private AssemblyModel naive;
        private AssemblyModel work;
        private AssemblyModel incoming;

        private uint sequence;
        private uint nextLocalId = 1;
        private bool inWorld;
        private bool synced;
        private bool requested;
        private bool requestWaiting;
        private bool raising;

        // The host's order being written: the number of its first step and how many records there are.
        private int batchCount;
        private uint batchFirst;

        // A client: the pieces of the whole car coming in.
        private bool receiving;
        private int expectedPart;
        private uint snapshotSequence;

        private double clock;
        private double nextDigest;
        private double nextMalformedLog = double.NegativeInfinity;
        private double nextShapeLog = double.NegativeInfinity;
        private bool detached;

        public AssemblyReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
            session.BeforeFlush += OnBeforeFlush;
            session.PlayerLeft += OnPlayerLeft;
        }

        /// <summary>
        /// A step for this game to make: another player's, or one that corrects this game (<see cref="AssemblyStep.Player"/>
        /// is then <see cref="AssemblyWire.NoPlayer"/>). Steps come in the order they have to be made. A handler must
        /// not call back into the replication.
        /// </summary>
        public event Action<AssemblyStep> StepWanted;

        /// <summary>On a machine that does not run the car: show this number (the value's place, what it is now).</summary>
        public event Action<int, float> ValueWanted;

        /// <summary>On the machine that runs the car: another player changed this number by this much; add it.</summary>
        public event Action<int, float> ValueDeltaWanted;

        /// <summary>The machine that runs the car's numbers changed (its player number).</summary>
        public event Action<byte> ValuesOwnerChanged;

        /// <summary>The catalogue in use, or null before <see cref="Define"/>.</summary>
        public AssemblyCatalog Catalog
        {
            get { return catalog; }
        }

        public bool Defined
        {
            get { return catalog != null; }
        }

        /// <summary>
        /// Whether this machine's car is the session's: on the host once its world is loaded, on a client once the
        /// whole car has come.
        /// </summary>
        public bool Synced
        {
            get { return synced; }
        }

        /// <summary>Set on a client whose catalogue is not the host's. Nothing is shared then.</summary>
        public bool Mismatch { get; private set; }

        /// <summary>The car as the host's order leaves it, up to <see cref="Sequence"/>.</summary>
        public AssemblyModel Model
        {
            get { return model; }
        }

        /// <summary>What this game shows, or is being handed the steps to show: the host's car and this client's steps not answered yet.</summary>
        public AssemblyModel Shown
        {
            get { return shown; }
        }

        /// <summary>The number of the last step of the host's order in <see cref="Model"/>.</summary>
        public uint Sequence
        {
            get { return sequence; }
        }

        /// <summary>Steps of this client the host has not answered yet.</summary>
        public int PendingCount
        {
            get { return pending.Count; }
        }

        /// <summary>Clients that asked for the car (on the host).</summary>
        public int Subscribers
        {
            get { return subscribers.Count; }
        }

        /// <summary>Steps this machine's game made and told.</summary>
        public long StepsTold { get; private set; }

        /// <summary>Steps of the others heard (on a client, from the host's order; on the host, from clients).</summary>
        public long StepsHeard { get; private set; }

        /// <summary>Steps refused: on a client its own, on the host everyone's.</summary>
        public long Refused { get; private set; }

        /// <summary>Steps handed to this game to correct it: undoing, matching the host's car.</summary>
        public long Corrections { get; private set; }

        /// <summary>Whole cars sent (host) or received (client).</summary>
        public long Snapshots { get; private set; }

        public long DigestsAgreed { get; private set; }

        public long DigestsDiffered { get; private set; }

        /// <summary>The host's last digest heard, and the number of its order it was made at.</summary>
        public ulong HostDigest { get; private set; }

        public uint HostDigestSequence { get; private set; }

        public long Malformed { get; private set; }

        /// <summary>Messages of another catalogue's shape, or for a car this machine has not listed yet, not read.</summary>
        public long OtherShape { get; private set; }

        /// <summary>Who runs the car's numbers: a player number.</summary>
        public byte ValuesOwner
        {
            get { return valuesOwner; }
        }

        /// <summary>Whether this machine runs the car's numbers.</summary>
        public bool OwnsValues
        {
            get { return catalog != null && valuesOwner == session.LocalPlayerId && (IsHost || synced); }
        }

        /// <summary>Changes of the car's numbers by this machine's player, told.</summary>
        public long Poured { get; private set; }

        /// <summary>Changes by the others this machine added while running the car.</summary>
        public long Absorbed { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>
        /// The game side's list of the car. Both sides list it the same way; defining another list starts over (a
        /// client then has to ask for the whole car again).
        /// </summary>
        public void Define(AssemblyCatalog newCatalog)
        {
            if (newCatalog == null)
            {
                throw new ArgumentNullException("newCatalog");
            }

            newCatalog.Freeze();
            if (newCatalog == catalog)
            {
                return;
            }

            catalog = newCatalog;
            shape = newCatalog.Shape;
            model = new AssemblyModel(newCatalog);
            shown = new AssemblyModel(newCatalog);
            target = new AssemblyModel(newCatalog);
            naive = new AssemblyModel(newCatalog);
            work = new AssemblyModel(newCatalog);
            incoming = new AssemblyModel(newCatalog);
            int values = newCatalog.ValueCount;
            current = new float[values];
            hasCurrent = new bool[values];
            lastSent = new float[values];
            sentOnce = new bool[values];
            word = new float[values];
            hasWord = new bool[values];
            shownValues = new float[values];
            everShown = new bool[values];
            sums = new float[values];
            sequence = 0;
            inWorld = false;
            synced = false;
            requested = false;
            receiving = false;
            Mismatch = false;
            pending.Clear();
            ownDeltas.Clear();
            uncounted.Clear();
            batchCount = 0;
            fullWordDue = true;
        }

        /// <summary>
        /// This machine's world is ready: <paramref name="scanned"/> is the car as its game has it (null for a car with
        /// nothing on it). On the host it becomes the session's car. On a client it is what the game shows; once the whole
        /// car comes, the game is handed the steps to match it.
        /// </summary>
        public void Load(AssemblyModel scanned)
        {
            RequireDefined();
            if (scanned != null && scanned.Catalog != catalog)
            {
                throw new ArgumentException("The car read from the game is listed by another catalogue.", "scanned");
            }

            inWorld = true;
            if (scanned == null)
            {
                shown.Clear();
            }
            else
            {
                shown.CopyFrom(scanned);
            }

            // A world that has just loaded has the numbers of its save: the game is handed every number said again.
            Array.Clear(everShown, 0, everShown.Length);
            ShowValues();
            if (IsHost)
            {
                model.CopyFrom(shown);
                synced = true;
                if (session.State == SessionState.Connected)
                {
                    // Clients that asked before this world was ready, and those that had another car of this host.
                    SendBatch();
                    for (int i = 0; i < subscribers.Count; i++)
                    {
                        SendSnapshot(subscribers[i]);
                    }

                    var waiting = new List<WaitingRequest>(waitingRequests);
                    waitingRequests.Clear();
                    for (int i = 0; i < waiting.Count; i++)
                    {
                        Answer(waiting[i].Player, SameList(waiting[i]));
                    }
                }

                log.Info(LogCategory.Vehicle, "Car: " + catalog.Count + " entr(ies) of this world are the session's (digest " + DigestText(model.Digest()) + ").");
                return;
            }

            if (synced)
            {
                // The world came back while the car was already known: the game is handed what it takes to match it.
                Rebase(null, AssemblyWire.NoPlayer, false);
            }
        }

        /// <summary>This machine left its game world: nothing is handed to the game until <see cref="Load"/>.</summary>
        public void ClearWorld()
        {
            inWorld = false;
            pending.Clear();
            if (IsHost)
            {
                synced = false;
            }
        }

        /// <summary>
        /// On a client whose world is ready: asks the host for the whole car and every step from then on. A client
        /// already in its world when it joins asks before it is connected: the question goes once it is. Asking again
        /// brings the whole car again.
        /// </summary>
        public void RequestAll()
        {
            if (catalog == null || IsHost)
            {
                return;
            }

            if (session.Role == SessionRole.Client && session.State == SessionState.Connecting)
            {
                requestWaiting = true;
                return;
            }

            if (session.State == SessionState.Connected)
            {
                SendRequest();
            }
        }

        /// <summary>
        /// This machine's game made a step: its player installed, removed, turned a bolt, wired, painted; or, on the
        /// machine that runs the car, an outcome. Returns false when it is not told (no car here, or not a step of it).
        /// </summary>
        public bool Report(AssemblyStep step)
        {
            if (catalog == null || !inWorld || Mismatch)
            {
                return false;
            }

            if (!model.IsWellFormed(step))
            {
                log.Warning(LogCategory.Vehicle, "Car: this game made a step that is not one of the car (" + step.Describe(catalog) + "); not told.");
                return false;
            }

            step.Player = session.LocalPlayerId;
            StepsTold++;
            if (IsHost)
            {
                if (model.Allows(step))
                {
                    Accept(step, Protocol.HostPlayerId, 0);
                    shown.Apply(step);
                    return true;
                }

                // The host's own game did what its car says cannot happen: it is undone, so that the car stays one.
                Refused++;
                shown.Apply(step);
                log.Info(LogCategory.Vehicle, "Car: " + step.Describe(catalog) + " does not fit the session's car; undone here.");
                Rebase(null, AssemblyWire.NoPlayer, false);
                return true;
            }

            shown.Apply(step);
            if (pending.Count >= MaxPending)
            {
                log.Warning(LogCategory.Vehicle, "Car: " + pending.Count + " step(s) of this game have no answer from the host; asking for the whole car again.");
                pending.Clear();
                if (requested)
                {
                    SendRequest();
                }

                return true;
            }

            pending.Add(new Pending { Id = nextLocalId++, Step = step });
            return true;
        }

        /// <summary>
        /// Compares the car as this game has it now with what it should show, and hands the game the steps that make
        /// up the difference (logged). For a game side that reads its car now and then to catch a step it failed to
        /// make. Returns how many steps it was handed.
        /// </summary>
        public int Correct(AssemblyModel scanned)
        {
            if (catalog == null || !inWorld || !synced || scanned == null)
            {
                return 0;
            }

            if (scanned.Catalog != catalog)
            {
                throw new ArgumentException("The car read from the game is listed by another catalogue.", "scanned");
            }

            List<AssemblyStep> steps = raising ? new List<AssemblyStep>() : corrections;
            steps.Clear();
            AssemblyModel.Reconcile(scanned, shown, steps, work);
            if (steps.Count == 0)
            {
                return 0;
            }

            log.Warning(LogCategory.Vehicle, "Car: this game differs from what it should show in " + scanned.DescribeDifferences(shown, 5) + "; " + steps.Count + " step(s) to correct it.");
            RaiseAll(steps);
            return steps.Count;
        }

        /// <summary>The digest of the car as the host's order leaves it here (<see cref="AssemblyModel.Digest"/>).</summary>
        public ulong Digest()
        {
            return model == null ? 0UL : model.Digest();
        }

        /// <summary>Call once per frame with the session's clock.</summary>
        public void Update(double now)
        {
            clock = now;
            if (requestWaiting && !IsHost && session.State == SessionState.Connected)
            {
                requestWaiting = false;
                SendRequest();
            }
        }

        /// <summary>One line for the overlay, or null before there is a car.</summary>
        public string Describe()
        {
            if (catalog == null || (!synced && StepsTold == 0 && Malformed == 0))
            {
                return null;
            }

            var text = new System.Text.StringBuilder();
            text.Append("Car: ").Append(StepsTold).Append(" step(s) here, ").Append(StepsHeard).Append(" heard");
            if (Refused > 0)
            {
                text.Append(", ").Append(Refused).Append(" refused");
            }

            if (Corrections > 0)
            {
                text.Append(", ").Append(Corrections).Append(" correction(s)");
            }

            text.Append("; step ").Append(sequence).Append(", digest ").Append(DigestText(model.Digest()));
            if (!IsHost)
            {
                text.Append(synced ? string.Empty : " (not the host's yet)").Append(", agreed ").Append(DigestsAgreed).Append(" time(s)");
                if (DigestsDiffered > 0)
                {
                    text.Append(", differed ").Append(DigestsDiffered);
                }

                if (pending.Count > 0)
                {
                    text.Append(", ").Append(pending.Count).Append(" waiting");
                }
            }
            else
            {
                text.Append(", ").Append(subscribers.Count).Append(" client(s)");
            }

            if (current.Length > 0)
            {
                text.Append("; numbers run by player ").Append(valuesOwner);
            }

            if (Malformed > 0)
            {
                text.Append(", ").Append(Malformed).Append(" malformed");
            }

            return text.ToString();
        }

        /// <summary>The digest as 16 hexadecimal digits.</summary>
        public static string DigestText(ulong digest)
        {
            return digest.ToString("x16", CultureInfo.InvariantCulture);
        }

        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.BeforeFlush -= OnBeforeFlush;
            session.PlayerLeft -= OnPlayerLeft;
            pending.Clear();
            subscribers.Clear();
            waitingRequests.Clear();
            ownDeltas.Clear();
            uncounted.Clear();
            inWorld = false;
            synced = false;
        }

        // ---- Steps ----

        private void RequireDefined()
        {
            if (catalog == null)
            {
                throw new InvalidOperationException("The car has no catalogue yet: call Define first.");
            }
        }

        // The host takes a step into its order: its car, the number, and the message to every client that asked.
        private void Accept(AssemblyStep step, byte origin, uint originId)
        {
            AppendRecord(step, origin, originId, false);
            model.Apply(step);
            sequence++;
        }

        private void AppendRecord(AssemblyStep step, byte origin, uint originId, bool refused)
        {
            if (subscribers.Count == 0 || session.State != SessionState.Connected)
            {
                return;
            }

            if (batchCount > 0 && (batch.Length + AssemblyWire.MaxRecordBytes > AssemblyWire.MessageBytes || batchCount >= MaxBatchCount))
            {
                SendBatch();
            }

            if (batchCount == 0)
            {
                batch.Reset();
                batch.WriteByte(AssemblyWire.MessageSequenced);
                batch.WriteUInt32(shape);
                batchFirst = sequence + 1;
                batch.WriteUInt32(batchFirst);
                batch.WriteUInt16(0);
            }

            batch.WriteByte((byte)((byte)step.Kind | (refused ? AssemblyWire.RefusedBit : 0)));
            batch.WriteByte(origin);
            batch.WriteVarUInt32(originId);
            WriteStepBody(batch, step);
            batchCount++;
        }

        // The host: whatever of its order is waiting goes out now, before anything else is said.
        private void SendBatch()
        {
            if (batchCount == 0)
            {
                return;
            }

            // The count sits after the type, the shape and the first number.
            batch.Buffer[9] = (byte)batchCount;
            batch.Buffer[10] = (byte)(batchCount >> 8);
            for (int i = 0; i < subscribers.Count; i++)
            {
                session.Send(subscribers[i], batch.Buffer, 0, batch.Length, true);
            }

            batchCount = 0;
        }

        private void SendToSubscribers()
        {
            for (int i = 0; i < subscribers.Count; i++)
            {
                session.Send(subscribers[i], writer.Buffer, 0, writer.Length, true);
            }
        }

        private static void WriteStepBody(PacketWriter output, AssemblyStep step)
        {
            output.WriteVarUInt32((uint)step.Entry);
            switch (step.Kind)
            {
                case AssemblyStepKind.Install:
                    output.WriteVarUInt32((uint)step.Variant);
                    output.WriteVarUInt32(SpawnWire.ZigZag(step.PartRef));
                    break;
                case AssemblyStepKind.Bolt:
                    output.WriteVarUInt32((uint)step.Bolt);
                    output.WriteByte(step.Delta > 0 ? (byte)1 : (byte)0);
                    break;
                case AssemblyStepKind.Wire:
                    output.WriteByte(step.Connected ? (byte)1 : (byte)0);
                    break;
                case AssemblyStepKind.Paint:
                    output.WriteVarUInt32((uint)step.PaintType);
                    output.WriteInt32(step.Color);
                    break;
                case AssemblyStepKind.Outcome:
                    output.WriteVarUInt32((uint)step.Code);
                    break;
            }
        }

        private AssemblyStep ReadStep(byte kind)
        {
            var step = new AssemblyStep { Kind = (AssemblyStepKind)kind, Entry = ReadIndex() };
            switch (step.Kind)
            {
                case AssemblyStepKind.Install:
                    step.Argument = ReadIndex();
                    step.Value = SpawnWire.UnZigZag(reader.ReadVarUInt32());
                    break;
                case AssemblyStepKind.Remove:
                    break;
                case AssemblyStepKind.Bolt:
                    step.Argument = ReadIndex();
                    byte direction = reader.ReadByte();
                    if (direction > 1)
                    {
                        throw new PacketFormatException("A bolt turned " + direction + " ways.");
                    }

                    step.Value = direction == 1 ? 1 : -1;
                    break;
                case AssemblyStepKind.Wire:
                    byte connected = reader.ReadByte();
                    if (connected > 1)
                    {
                        throw new PacketFormatException("A wire " + connected + " times connected.");
                    }

                    step.Argument = connected;
                    break;
                case AssemblyStepKind.Paint:
                    step.Argument = ReadIndex();
                    step.Value = reader.ReadInt32();
                    break;
                case AssemblyStepKind.Outcome:
                    step.Argument = ReadIndex();
                    break;
                default:
                    throw new PacketFormatException("A step of kind " + kind + ".");
            }

            if (!model.IsWellFormed(step))
            {
                throw new PacketFormatException("A step that is not one of this car: " + step.Describe(catalog) + ".");
            }

            return step;
        }

        private int ReadIndex()
        {
            uint value = reader.ReadVarUInt32();
            if (value > AssemblyModel.MaxArgument)
            {
                throw new PacketFormatException("A number of a step out of range: " + value + ".");
            }

            return (int)value;
        }

        private void SendRequest()
        {
            requested = true;
            writer.Reset();
            writer.WriteByte(AssemblyWire.MessageRequest);
            writer.WriteUInt32(shape);
            writer.WriteUInt16((ushort)catalog.Count);
            writer.WriteUInt16((ushort)catalog.ValueCount);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);

            // Steps this game made before the whole car came go out once it has (OnBeforeFlush), so that the host takes
            // them after the car it sent, whenever its own world is ready to send it.
        }

        // Host: a client asked for the car. The answer is the whole car, or that its list is not this one.
        private void Answer(byte client, bool sameList)
        {
            SendBatch();
            if (!sameList)
            {
                subscribers.Remove(client);
                writer.Reset();
                writer.WriteByte(AssemblyWire.MessageSnapshot);
                writer.WriteUInt32(shape);
                writer.WriteUInt32(0);
                writer.WriteByte(AssemblyWire.Mismatch);
                session.Send(client, writer.Buffer, 0, writer.Length, true);
                log.Warning(LogCategory.Vehicle, "Car: player " + client + " lists the car differently; it is not shared with them.");
                return;
            }

            SendSnapshot(client);
            if (!subscribers.Contains(client))
            {
                subscribers.Add(client);
            }
        }

        // Host: the whole car as of the last step of its order, in pieces, and the book of numbers in the last one.
        private void SendSnapshot(byte client)
        {
            int part = 0;
            int countAt = StartPiece(part);
            int count = 0;
            for (int entry = 0; entry < catalog.Count; entry++)
            {
                if (IsBare(model, entry))
                {
                    continue;
                }

                if (writer.Length + AssemblyWire.MaxEntryBytes > AssemblyWire.MessageBytes)
                {
                    EndPiece(countAt, count, false);
                    session.Send(client, writer.Buffer, 0, writer.Length, true);
                    countAt = StartPiece(++part);
                    count = 0;
                }

                WriteEntry(entry);
                count++;
            }

            EndPiece(countAt, count, true);
            int known = 0;
            for (int i = 0; i < hasWord.Length; i++)
            {
                known += hasWord[i] ? 1 : 0;
            }

            // An owner, a count, and for each number its place (two bytes at most) and four bytes.
            if (writer.Length + 7 + (known * 6) > AssemblyWire.MessageBytes)
            {
                // The numbers would not fit behind the entries: they go in a piece of their own.
                writer.Buffer[countAt - 1] = 0;
                session.Send(client, writer.Buffer, 0, writer.Length, true);
                countAt = StartPiece(++part);
                EndPiece(countAt, 0, true);
            }

            writer.WriteByte(valuesOwner);
            writer.WriteUInt32(wordAbsorbed);
            writer.WriteUInt16((ushort)known);
            for (int i = 0; i < hasWord.Length; i++)
            {
                if (hasWord[i])
                {
                    writer.WriteVarUInt32((uint)i);
                    writer.WriteSingle(word[i]);
                }
            }

            session.Send(client, writer.Buffer, 0, writer.Length, true);
            Snapshots++;
            log.Info(LogCategory.Vehicle, "Car: sent the whole car to player " + client + " as of step " + sequence + " (" + (part + 1) + " piece(s), digest " + DigestText(model.Digest()) + ").");
        }

        private int StartPiece(int part)
        {
            writer.Reset();
            writer.WriteByte(AssemblyWire.MessageSnapshot);
            writer.WriteUInt32(shape);
            writer.WriteUInt32(sequence);
            writer.WriteByte((byte)part);
            writer.WriteByte(0);
            int countAt = writer.Length;
            writer.WriteUInt16(0);
            return countAt;
        }

        private void EndPiece(int countAt, int count, bool last)
        {
            writer.Buffer[countAt - 1] = last ? (byte)1 : (byte)0;
            writer.Buffer[countAt] = (byte)count;
            writer.Buffer[countAt + 1] = (byte)(count >> 8);
        }

        private static bool IsBare(AssemblyModel car, int entry)
        {
            return !car.IsInstalled(entry) && !car.IsWired(entry) && !car.IsPainted(entry) && !car.IsDamaged(entry) && car.Tightness(entry) == 0;
        }

        private void WriteEntry(int entry)
        {
            bool bolts = model.Tightness(entry) > 0;
            int flags = (model.IsInstalled(entry) ? 1 : 0) | (model.IsWired(entry) ? 2 : 0) | (model.IsPainted(entry) ? 4 : 0) | (model.IsDamaged(entry) ? 8 : 0) | (bolts ? 16 : 0);
            writer.WriteVarUInt32((uint)entry);
            writer.WriteByte((byte)flags);
            if (model.IsInstalled(entry))
            {
                writer.WriteVarUInt32((uint)model.Variant(entry));
                writer.WriteVarUInt32(SpawnWire.ZigZag(model.PartRef(entry)));
            }

            if (model.IsPainted(entry))
            {
                writer.WriteVarUInt32((uint)model.PaintType(entry));
                writer.WriteInt32(model.Color(entry));
            }

            if (bolts)
            {
                for (int bolt = 0; bolt < catalog.Bolts(entry); bolt++)
                {
                    writer.WriteByte((byte)model.Stage(entry, bolt));
                }
            }
        }

        private void ReadEntry()
        {
            uint entry = reader.ReadVarUInt32();
            if (entry >= (uint)catalog.Count)
            {
                throw new PacketFormatException("Entry " + entry + " of a car of " + catalog.Count + ".");
            }

            int index = (int)entry;
            int flags = reader.ReadByte();
            if (flags > 31)
            {
                throw new PacketFormatException("An entry with flags " + flags + ".");
            }

            if ((flags & 1) != 0)
            {
                int variant = ReadIndex();
                incoming.SetInstalled(index, true, variant, SpawnWire.UnZigZag(reader.ReadVarUInt32()));
            }
            else
            {
                incoming.SetInstalled(index, false);
            }

            incoming.SetWired(index, (flags & 2) != 0);
            if ((flags & 4) != 0)
            {
                int paintType = ReadIndex();
                incoming.SetPaint(index, paintType, reader.ReadInt32());
            }
            else
            {
                incoming.ClearPaint(index);
            }

            incoming.SetDamaged(index, (flags & 8) != 0);
            for (int bolt = 0; bolt < catalog.Bolts(index); bolt++)
            {
                int stage = (flags & 16) != 0 ? reader.ReadByte() : 0;
                if (stage > catalog.MaxStage(index, bolt))
                {
                    throw new PacketFormatException("Bolt " + bolt + " of '" + catalog.Name(index) + "' at stage " + stage + ".");
                }

                incoming.SetStage(index, bolt, stage);
            }
        }

        // ---- What a client does with the host's order ----

        // The car target: the host's, with this client's steps not answered yet that still fit on it, in their order.
        private void BuildTarget()
        {
            target.CopyFrom(model);
            for (int i = 0; i < pending.Count; i++)
            {
                if (target.Allows(pending[i].Step))
                {
                    target.Apply(pending[i].Step);
                }
            }
        }

        /// <summary>
        /// After the host's car or this client's steps changed: <paramref name="step"/> (if any) is someone else's step
        /// just taken into the car. If adding it to what the game shows gives the car it should show, the game is handed
        /// it as it is; otherwise the game is handed the steps that take what it shows to what it should.
        /// </summary>
        private void Rebase(AssemblyStep? step, byte origin, bool fast)
        {
            if (!inWorld)
            {
                return;
            }

            if (fast && step.HasValue)
            {
                // Nothing of this client's is waiting: what the game shows is the host's car before the step.
                shown.Apply(step.Value);
                Raise(step.Value, origin);
                return;
            }

            BuildTarget();
            if (step.HasValue)
            {
                naive.CopyFrom(shown);
                if (naive.Allows(step.Value))
                {
                    naive.Apply(step.Value);
                    if (naive.SameAs(target))
                    {
                        shown.CopyFrom(naive);
                        Raise(step.Value, origin);
                        return;
                    }
                }
            }
            else if (shown.SameAs(target))
            {
                return;
            }

            List<AssemblyStep> steps = raising ? new List<AssemblyStep>() : corrections;
            steps.Clear();
            AssemblyModel.Reconcile(shown, target, steps, work);
            shown.CopyFrom(target);
            if (steps.Count > 0)
            {
                log.Info(LogCategory.Vehicle, "Car: " + steps.Count + " step(s) to bring this game to the host's car" + (step.HasValue ? " after " + step.Value.Describe(catalog) + " of player " + origin : string.Empty) + ".");
            }

            RaiseAll(steps);
        }

        private void RaiseAll(List<AssemblyStep> steps)
        {
            Corrections += steps.Count;
            Action<AssemblyStep> handler = StepWanted;
            if (handler == null)
            {
                return;
            }

            bool was = raising;
            raising = true;
            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    AssemblyStep each = steps[i];
                    each.Player = AssemblyWire.NoPlayer;
                    handler(each);
                }
            }
            finally
            {
                raising = was;
            }
        }

        private void Raise(AssemblyStep step, byte player)
        {
            Action<AssemblyStep> handler = StepWanted;
            if (handler == null)
            {
                return;
            }

            step.Player = player;
            bool was = raising;
            raising = true;
            try
            {
                handler(step);
            }
            finally
            {
                raising = was;
            }
        }

        // Client: a message of the host's order. Read whole first, so that a broken one changes nothing.
        private void OnSequenced()
        {
            uint first = reader.ReadUInt32();
            int count = reader.ReadUInt16();
            records.Clear();
            for (int i = 0; i < count; i++)
            {
                byte head = reader.ReadByte();
                var record = new Record
                {
                    Refused = (head & AssemblyWire.RefusedBit) != 0,
                    Origin = reader.ReadByte(),
                    OriginId = reader.ReadVarUInt32(),
                };
                record.Step = ReadStep((byte)(head & ~AssemblyWire.RefusedBit));
                records.Add(record);
            }

            if (!synced)
            {
                // The whole car has not come: it will hold all this.
                return;
            }

            uint number = first;
            byte me = session.LocalPlayerId;
            for (int i = 0; i < records.Count; i++)
            {
                Record record = records[i];
                if (record.Refused)
                {
                    if (record.Origin == me)
                    {
                        OnRefused(record);
                    }

                    continue;
                }

                if (number <= sequence)
                {
                    // Already in the car: it came with the whole car.
                    number++;
                    continue;
                }

                if (number != sequence + 1)
                {
                    log.Warning(LogCategory.Vehicle, "Car: step " + number + " of the host's order came after step " + sequence + "; asking for the whole car again.");
                    SendRequest();
                    return;
                }

                sequence = number++;
                model.Apply(record.Step);
                bool own = record.Origin == me && pending.Count > 0 && pending[0].Id == record.OriginId;
                if (own)
                {
                    pending.RemoveAt(0);
                    Rebase(null, me, false);
                    continue;
                }

                StepsHeard++;
                Rebase(record.Step, record.Origin, pending.Count == 0);
            }
        }

        private void OnRefused(Record record)
        {
            Refused++;
            if (pending.Count > 0 && pending[0].Id == record.OriginId)
            {
                pending.RemoveAt(0);
            }

            log.Info(LogCategory.Vehicle, "Car: the host refused " + record.Step.Describe(catalog) + " of this game (the car had changed); undoing it.");
            Rebase(null, AssemblyWire.NoPlayer, false);
        }

        // Client: a piece of the whole car.
        private void OnSnapshot()
        {
            uint at = reader.ReadUInt32();
            byte part = reader.ReadByte();
            bool last = reader.ReadByte() != 0;
            int count = reader.ReadUInt16();
            if (part == 0)
            {
                incoming.Clear();
                receiving = true;
                expectedPart = 0;
                snapshotSequence = at;
            }

            if (!receiving || part != expectedPart || at != snapshotSequence)
            {
                receiving = false;
                throw new PacketFormatException("Piece " + part + " of the host's car out of order.");
            }

            expectedPart++;
            for (int i = 0; i < count; i++)
            {
                ReadEntry();
            }

            if (!last)
            {
                return;
            }

            byte owner = reader.ReadByte();
            uint heldAt = reader.ReadUInt32();
            int known = reader.ReadUInt16();
            if (known > word.Length)
            {
                throw new PacketFormatException(known + " numbers of a car of " + word.Length + ".");
            }

            for (int i = 0; i < hasWord.Length; i++)
            {
                hasWord[i] = false;
            }

            for (int i = 0; i < known; i++)
            {
                int index = ReadValueIndex();
                word[index] = ReadNumber();
                hasWord[index] = true;
            }

            receiving = false;
            model.CopyFrom(incoming);
            sequence = snapshotSequence;
            bool first = !synced;
            synced = true;
            Mismatch = false;
            Snapshots++;
            wordAbsorbed = heldAt;
            if (owner != valuesOwner)
            {
                valuesOwner = owner;
                RaiseOwner(owner);
            }

            ShowValues();
            log.Info(LogCategory.Vehicle, "Car: " + (first ? "the host's car" : "the host's car again") + " as of step " + sequence + " (digest " + DigestText(model.Digest()) + ").");
            Rebase(null, AssemblyWire.NoPlayer, false);
        }

        private void OnDigest()
        {
            uint at = reader.ReadUInt32();
            ulong digest = reader.ReadUInt64();
            if (!synced || at != sequence)
            {
                return;
            }

            HostDigest = digest;
            HostDigestSequence = at;
            ulong mine = model.Digest();
            if (mine == digest)
            {
                DigestsAgreed++;
                return;
            }

            DigestsDiffered++;
            log.Warning(LogCategory.Vehicle, "Car: as of step " + at + " the host's car has digest " + DigestText(digest) + " and this one " + DigestText(mine) + "; asking for the whole car again.");
            SendRequest();
        }

        // ---- What the host does with the clients ----

        private void OnSteps(byte sender)
        {
            uint first = reader.ReadUInt32();
            int count = reader.ReadUInt16();
            if (count > AssemblyWire.MaxStepsPerMessage)
            {
                throw new PacketFormatException(count + " steps in one message.");
            }

            records.Clear();
            for (int i = 0; i < count; i++)
            {
                records.Add(new Record { Step = ReadStep(reader.ReadByte()), Origin = sender, OriginId = first + (uint)i });
            }

            if (!subscribers.Contains(sender))
            {
                // Not one of those who have the car (another list): its steps are not taken.
                return;
            }

            for (int i = 0; i < records.Count; i++)
            {
                Record record = records[i];
                StepsHeard++;
                if (synced && model.Allows(record.Step))
                {
                    Accept(record.Step, sender, record.OriginId);
                    shown.Apply(record.Step);
                    Raise(record.Step, sender);
                }
                else
                {
                    // Refused, also by a host out of its world: what is not answered would stay in that game forever.
                    Refused++;
                    AppendRecord(record.Step, sender, record.OriginId, true);
                    log.Info(LogCategory.Vehicle, "Car: refused " + record.Step.Describe(catalog) + " of player " + sender + (synced ? " (the car had changed)." : " (this world is not loaded)."));
                }
            }
        }

        private void OnRequest(byte sender, uint theirShape)
        {
            var request = new WaitingRequest { Player = sender, Shape = theirShape, Entries = reader.ReadUInt16(), Values = reader.ReadUInt16() };
            RemoveWaiting(sender);
            if (!synced)
            {
                // This world is not ready: the answer goes once it is.
                waitingRequests.Add(request);
                return;
            }

            Answer(sender, SameList(request));
        }

        private bool SameList(WaitingRequest request)
        {
            return catalog != null && request.Shape == shape && request.Entries == catalog.Count && request.Values == catalog.ValueCount;
        }

        private void RemoveWaiting(byte player)
        {
            for (int i = waitingRequests.Count - 1; i >= 0; i--)
            {
                if (waitingRequests[i].Player == player)
                {
                    waitingRequests.RemoveAt(i);
                }
            }
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            if (!IsHost)
            {
                return;
            }

            subscribers.Remove(player.Id);
            RemoveWaiting(player.Id);
            countedFor.Remove(player.Id);
            for (int i = 0; i < uncounted.Count; i++)
            {
                if (uncounted[i].Player == player.Id)
                {
                    // Still added by whoever runs the car, but no longer anyone's to count.
                    HostDelta delta = uncounted[i];
                    delta.Player = AssemblyWire.NoPlayer;
                    uncounted[i] = delta;
                }
            }

            if (valuesOwner == player.Id && catalog != null)
            {
                SetValuesOwner(session.LocalPlayerId);
            }
        }

        private void OnBeforeFlush()
        {
            if (catalog == null || session.State != SessionState.Connected)
            {
                return;
            }

            if (IsHost)
            {
                SendBatch();
                Forward();
                if (OwnsValues)
                {
                    SendWord(false, clock);
                }

                if (clock >= nextDigest && synced && subscribers.Count > 0)
                {
                    nextDigest = clock + DigestInterval;
                    writer.Reset();
                    writer.WriteByte(AssemblyWire.MessageDigest);
                    writer.WriteUInt32(shape);
                    writer.WriteUInt32(sequence);
                    writer.WriteUInt64(model.Digest());
                    SendToSubscribers();
                }

                return;
            }

            if (!synced || Mismatch)
            {
                // Steps and changes of this game wait for the whole car: the host takes them after it, against it.
                // Sent while the host's world is still loading, they would reach a host that cannot answer them yet.
                return;
            }

            SendSteps();
            SendPoured();
            if (OwnsValues)
            {
                SendWord(false, clock);
            }
        }

        // Client: its steps not sent yet, in messages of their order.
        private void SendSteps()
        {
            int start = 0;
            while (start < pending.Count && pending[start].Sent)
            {
                start++;
            }

            while (start < pending.Count)
            {
                writer.Reset();
                writer.WriteByte(AssemblyWire.MessageSteps);
                writer.WriteUInt32(shape);
                writer.WriteUInt32(pending[start].Id);
                int countAt = writer.Length;
                writer.WriteUInt16(0);
                int count = 0;
                while (start < pending.Count && count < AssemblyWire.MaxStepsPerMessage && writer.Length + AssemblyWire.MaxRecordBytes <= AssemblyWire.MessageBytes)
                {
                    Pending each = pending[start];
                    writer.WriteByte((byte)each.Step.Kind);
                    WriteStepBody(writer, each.Step);
                    each.Sent = true;
                    pending[start] = each;
                    start++;
                    count++;
                }

                writer.Buffer[countAt] = (byte)count;
                writer.Buffer[countAt + 1] = (byte)(count >> 8);
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            }
        }

        // ---- Receiving ----

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type < AssemblyWire.MessageSteps || type > AssemblyWire.MessageOwner)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                uint theirShape = reader.ReadUInt32();
                bool toHost = type == AssemblyWire.MessageSteps || type == AssemblyWire.MessageRequest || type == AssemblyWire.MessagePoured;
                if (toHost != IsHost && type != AssemblyWire.MessageValues)
                {
                    throw new PacketFormatException("Message " + type + " about the car, sent to " + (IsHost ? "the host." : "a client."));
                }

                if (!IsHost && sender != Protocol.HostPlayerId)
                {
                    throw new PacketFormatException("Message " + type + " about the car from someone who is not the host.");
                }

                if (type == AssemblyWire.MessageRequest)
                {
                    OnRequest(sender, theirShape);
                    return;
                }

                if (type == AssemblyWire.MessageSnapshot && catalog != null && data.Length >= 10 && data[9] == AssemblyWire.Mismatch)
                {
                    Mismatch = true;
                    synced = false;
                    log.Warning(LogCategory.Vehicle, "Car: the host lists the car differently; it is not shared.");
                    return;
                }

                if (catalog == null || theirShape != shape)
                {
                    OtherShape++;
                    if (clock >= nextShapeLog)
                    {
                        nextShapeLog = clock + MalformedLogInterval;
                        log.Warning(LogCategory.Vehicle, "Car: ignored a message from player " + sender + " about a car " + (catalog == null ? "this game has not listed yet." : "listed differently.") + " (" + OtherShape + " so far)");
                    }

                    return;
                }

                switch (type)
                {
                    case AssemblyWire.MessageSteps:
                        OnSteps(sender);
                        break;
                    case AssemblyWire.MessageSequenced:
                        OnSequenced();
                        break;
                    case AssemblyWire.MessageSnapshot:
                        OnSnapshot();
                        break;
                    case AssemblyWire.MessageDigest:
                        OnDigest();
                        break;
                    case AssemblyWire.MessageValues:
                        OnValues(sender, data);
                        break;
                    case AssemblyWire.MessagePoured:
                        OnPoured(sender);
                        break;
                    case AssemblyWire.MessageAbsorb:
                        OnAbsorb();
                        break;
                    case AssemblyWire.MessageOwner:
                        OnOwner();
                        break;
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Vehicle, "Dropped a message about the car from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        private static ulong Mix(ulong hash, byte value)
        {
            unchecked
            {
                return (hash ^ value) * 1099511628211UL;
            }
        }

        private struct WaitingRequest
        {
            public byte Player;
            public uint Shape;
            public int Entries;
            public int Values;
        }

        private struct Pending
        {
            public uint Id;
            public AssemblyStep Step;
            public bool Sent;
        }

        private struct Record
        {
            public AssemblyStep Step;
            public byte Origin;
            public uint OriginId;
            public bool Refused;
        }
    }
}
