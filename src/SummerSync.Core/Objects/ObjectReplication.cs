using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Players;
using SummerSync.Core.Replication;
using SummerSync.Core.World;

namespace SummerSync.Core.Objects
{
    /// <summary>What this machine does with a loose object.</summary>
    public enum BodyRole
    {
        /// <summary>Not shared: the game is left to do with it what it always does.</summary>
        Unmapped,

        /// <summary>This machine runs its physics and tells the others where it is.</summary>
        Simulate,

        /// <summary>Another machine runs its physics; here it is moved to where that machine says it is.</summary>
        Follow,
    }

    /// <summary>Message types and limits of object replication.</summary>
    public static class ObjectWire
    {
        /// <summary>Client to host, reliable: "my world is loaded, send me your objects".</summary>
        public const byte MessageRequest = PlayerWire.MessageViewReport + 1;

        /// <summary>Host to client, reliable: the host's clock, a count, then id, owner, flags, name and pose of each object.</summary>
        public const byte MessageList = PlayerWire.MessageViewReport + 2;

        /// <summary>Client to host, unreliable: the sender's clock, a count, then id and pose of objects it simulates.</summary>
        public const byte MessageStatesUp = PlayerWire.MessageViewReport + 3;

        /// <summary>Host to client, unreliable: whose objects, their clock, a count, then id and pose of each.</summary>
        public const byte MessageStatesDown = PlayerWire.MessageViewReport + 4;

        /// <summary>
        /// Client to host, reliable: id and one of the Claim values. Asks for the object, in hand or just because
        /// it is near, or lets go of it.
        /// </summary>
        public const byte MessageClaim = PlayerWire.MessageViewReport + 5;

        /// <summary>Host to client, reliable: id, who simulates it now, and whether it is in their hand.</summary>
        public const byte MessageOwner = PlayerWire.MessageViewReport + 6;

        /// <summary>
        /// Client to host, reliable: id, pose and motion of an object handed back to the host. With a pose on a
        /// vehicle, the motion is relative to the vehicle too, and in its axes.
        /// </summary>
        public const byte MessageRelease = PlayerWire.MessageViewReport + 7;

        /// <summary>Host to client, reliable: the host's clock, id and the pose in which an object came to rest.</summary>
        public const byte MessageRest = PlayerWire.MessageViewReport + 8;

        /// <summary>Host to client, reliable: id and whether the object still exists in the host's world.</summary>
        public const byte MessagePresent = PlayerWire.MessageViewReport + 9;

        /// <summary>
        /// Either way, reliable: id and the player whose game ate the object or threw it away. The host passes it
        /// on. Only for the loose objects, outside the nine types above.
        /// </summary>
        public const byte MessageGone = SpawnWire.MessageContents + 1;

        /// <summary>The sender no longer has the object in hand.</summary>
        public const byte ClaimLetGo = 0;

        /// <summary>The sender has picked the object up. Nobody can take it from them.</summary>
        public const byte ClaimInHand = 1;

        /// <summary>
        /// The object is close to the sender or to what they drive, and they want to run its physics: an object
        /// someone else simulates stands like a wall. Granted only for what the host has, and anyone may still
        /// pick it up.
        /// </summary>
        public const byte ClaimNear = 2;

        public const byte FlagHeld = 1;
        public const byte FlagPresent = 2;

        /// <summary>In a list message's header: the last part of the whole list a client asked for.</summary>
        public const byte ListWhole = 1;

        public const int MaxNameBytes = 64;

        /// <summary>A thing's key: what its game calls it in the save ("beercase0"), the same in every world loaded from it.</summary>
        public const int MaxKeyBytes = 48;

        /// <summary>Objects per list message: each takes up to 90 bytes.</summary>
        public const int ListEntries = 10;

        /// <summary>Objects per state message: each takes 22 bytes.</summary>
        public const int StateEntries = 40;

        /// <summary>State messages per flush. Beyond that the moving objects take turns.</summary>
        public const int StateMessagesPerFlush = 2;
    }

    /// <summary>A thing of the host's world that has no counterpart in this one.</summary>
    public struct MissingThing
    {
        public ushort Id;
        public string Name;
        public string Key;
        public BodyPose Pose;
    }

    /// <summary>One loose object of the game world, as the replication sees it on this machine.</summary>
    public sealed class SharedBody
    {
        /// <summary>Eaten or thrown away, here or on another machine, and told: it is not told again.</summary>
        public bool Gone { get; internal set; }

        internal SharedBody(string name, bool local)
        {
            Name = name;
            IsLocal = local;
            Present = true;
            DroppedAt = double.NaN;
            Key = string.Empty;
        }

        public string Name { get; private set; }

        /// <summary>
        /// What the game calls the thing in its save ("beercase0", "sausagesx0"), empty if it has no such name. Two worlds
        /// loaded from the same save give the same thing the same key, wherever it was moved and however it was renamed;
        /// a thing made later gets its maker's prefix and number.
        /// </summary>
        public string Key { get; internal set; }

        /// <summary>False for an object of the host's world that has no counterpart in this one.</summary>
        public bool IsLocal { get; private set; }

        /// <summary>Whether the session knows this object: always on the host, after matching on a client.</summary>
        public bool HasId { get; internal set; }

        public ushort Id { get; internal set; }

        /// <summary>The player whose machine simulates it.</summary>
        public byte Owner { get; internal set; }

        /// <summary>Whether its owner has it in hand, or hanging from something in their game.</summary>
        public bool Held { get; internal set; }

        /// <summary>
        /// Whether this machine's game hangs it from something (the engine block from the hoist). See
        /// <see cref="ObjectReplication.SetHung"/>.
        /// </summary>
        public bool Hung { get; internal set; }

        /// <summary>Whether it still exists in the host's world.</summary>
        public bool Present { get; internal set; }

        public BodyRole Role { get; internal set; }

        /// <summary>While following: where to put it. Valid when <see cref="HasTarget"/>.</summary>
        public BodyPose Target { get; internal set; }

        public bool HasTarget { get; internal set; }

        /// <summary>While following: whether it is shown at rest, as opposed to on its way somewhere.</summary>
        public bool AtRest
        {
            get { return Buffer == null || !Buffer.HasData || Buffer.AtRest; }
        }

        internal SnapshotBuffer<BodyPose> Buffer { get; set; }

        // Where it is in the world, as this machine last knew.
        internal BodyPose LocalPose { get; set; }

        // What goes into the stream of states: the same, or the pose relative to the vehicle that carries it.
        internal BodyPose StreamPose { get; set; }

        internal double NearClaimAt { get; set; }

        // Handed back to the host as soon as it has left the hand, moving or not.
        internal bool HandBackSoon { get; set; }

        internal BodyMotion LocalMotion { get; set; }

        internal bool LocalAwake { get; set; }

        // Awake in some frame since the last states went out: a step that lasts one frame (a car on the jack, told every
        // couple of millimetres) is sent even if the frame the states go out in says it is still.
        internal bool AwakeSinceSent { get; set; }

        internal bool WasAwake { get; set; }

        internal bool Reported { get; set; }

        internal bool HasHandOff { get; set; }

        internal BodyPose HandOffPose { get; set; }

        internal BodyMotion HandOffMotion { get; set; }

        internal bool PendingClaim { get; set; }

        internal double DroppedAt { get; set; }

        /// <summary>The session's clock when the reported pose was the object's, or NaN for "when it is sent".</summary>
        internal double SampledAt { get; set; } = double.NaN;

        /// <summary>
        /// While simulating: tells where the object is this frame, how it moves, and whether its physics is
        /// awake. Call it every frame for every object with the role <see cref="BodyRole.Simulate"/>.
        /// </summary>
        public void Report(BodyPose pose, BodyMotion motion, bool awake)
        {
            Report(pose, motion, awake, double.NaN);
        }

        /// <summary>
        /// The same, for a pose that was the object's at <paramref name="sampledAt"/> on the session's clock: the physics
        /// step that put it there, which is not the frame's time. A vehicle moves only in its physics steps; stamped with
        /// the frame's time, the states said it went one step in some intervals and two in others of the same length, and
        /// the others saw it lurch (measured on 2026-10-16: at 8 m/s, 15.5 or 31 cm every 33 ms).
        /// </summary>
        public void Report(BodyPose pose, BodyMotion motion, bool awake, double sampledAt)
        {
            pose.Parent = 0;
            LocalPose = pose;
            StreamPose = pose;
            LocalMotion = motion;
            LocalAwake = awake;
            AwakeSinceSent |= awake;
            Reported = true;
            SampledAt = sampledAt;
        }

        /// <summary>
        /// The same for an object that a vehicle carries. <paramref name="world"/> is where it is in the world;
        /// <paramref name="onVehicle"/>, where it is relative to the vehicle numbered <paramref name="vehicle"/>
        /// by the vehicles' replication. The others are sent the second, and show it on their copy of the vehicle.
        /// <paramref name="motion"/> is relative to the vehicle as well, and in its axes: it is what the object is
        /// handed over with, and the vehicle does not move the same on two machines at the same moment.
        /// </summary>
        public void ReportCarried(BodyPose world, ushort vehicle, BodyPose onVehicle, BodyMotion motion, bool awake)
        {
            world.Parent = 0;
            onVehicle.Parent = vehicle;
            LocalPose = world;
            StreamPose = onVehicle;
            LocalMotion = motion;
            LocalAwake = awake;
            AwakeSinceSent |= awake;
            Reported = true;
        }

        /// <summary>
        /// While following an object whose <see cref="Target"/> is relative to a vehicle: tells where that put
        /// it in the world. It is what this machine starts from if it has to take the object over.
        /// </summary>
        public void NoteWorldPose(BodyPose world)
        {
            if (Role == BodyRole.Follow)
            {
                world.Parent = 0;
                LocalPose = world;
            }
        }

        /// <summary>
        /// When this machine has just taken over the simulation: the state to start it from. True once per
        /// take-over; the caller then places the object and gives it that motion.
        /// </summary>
        public bool TakeHandOff(out BodyPose pose, out BodyMotion motion)
        {
            pose = HandOffPose;
            motion = HandOffMotion;
            bool had = HasHandOff;
            HasHandOff = false;
            return had;
        }
    }

    /// <summary>
    /// Shares the loose objects of the game world (crates, bottles, car parts lying around) between the
    /// players of a session.
    ///
    /// Each object is simulated by exactly one machine, its owner, and shown everywhere else where the owner
    /// says it is. The host owns everything by default. Whoever picks an object up becomes its owner at once,
    /// keeps it while it is in hand and until it has come to rest after being let go, and then hands it back
    /// to the host together with its final state. Only objects that are moving are sent; one that comes to
    /// rest is announced once, reliably, with the exact pose it rested in.
    ///
    /// The host's list of objects is the list. A client matches each entry to an object of the same name in
    /// its own world, the nearest one first; what it cannot match is counted, not invented.
    /// </summary>
    public sealed class ObjectReplication
    {
        /// <summary>Seconds after letting go of an object that a client hands it back even if it never came to rest.</summary>
        public const double ReleaseAfterSeconds = 10.0;

        // Right after leaving the hand an object can look at rest for a frame or two.
        private const double ReleaseNotBeforeSeconds = 0.3;
        private const double MalformedLogInterval = 5.0;
        private const double NearClaimInterval = 1.0;
        private const double UnpairedReportDelay = 1.5;

        // What only this world has may change every second while the car is built here (its loose parts go in one by one).
        private const double UnpairedReportEvery = 30.0;

        /// <summary>
        /// Seconds a client waits for an object the host says is its own to turn up in its world before it hands
        /// the object back: a thing it made that was gone again before it was found, or one it ate.
        /// </summary>
        public const double OrphanSeconds = 6.0;
        private const int UnpairedNamesLogged = 12;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly List<SharedBody> locals = new List<SharedBody>();
        private readonly IList<SharedBody> localsView;
        private readonly Dictionary<ushort, SharedBody> byId = new Dictionary<ushort, SharedBody>();
        private readonly List<PendingEntry> pending = new List<PendingEntry>();
        private readonly List<byte> subscribers = new List<byte>();
        private readonly List<SharedBody> announce = new List<SharedBody>();
        private readonly List<SharedBody> scratch = new List<SharedBody>();
        private readonly PacketWriter writer = new PacketWriter(1000);
        private readonly PacketReader reader = new PacketReader();
        private readonly SnapshotBufferConfig bufferConfig = new SnapshotBufferConfig { MaxExtrapolation = 0.1 };

        private double clock;
        private double epoch = double.NaN;
        private ushort nextId = 1;
        private bool requested;
        private bool wantsList;
        private bool listReceived;

        // A client: the keys of the things the host's world has, by the host's number, and whether its whole list is here.
        private readonly Dictionary<ushort, string> hostKeys = new Dictionary<ushort, string>();
        private readonly Dictionary<string, int> hostKeyCount = new Dictionary<string, int>();
        private int cursor;
        private SharedBody held;

        // On a client: what the local player holds that is not paired yet (something just made). It is taken as in
        // hand the moment it pairs, instead of being shown where the host has it for a frame.
        private SharedBody heldUnpaired;
        private double nextMalformedLog = double.NegativeInfinity;
        private double describeUnpairedAt = double.PositiveInfinity;
        private string describedUnpaired = string.Empty;
        private string describedHostOnly = string.Empty;
        private double describedUnpairedAt = double.NegativeInfinity;
        private bool detached;

        // Message types on the wire are those of ObjectWire moved by this much.
        private readonly int shift;
        private readonly string prefix;

        public ObjectReplication(NetSession session, Logger log)
            : this(session, log, ObjectWire.MessageRequest, null)
        {
        }

        /// <summary>
        /// A replication of its own for another kind of thing, next to that of the loose objects.
        /// </summary>
        /// <param name="firstMessage">The first of its nine message types, in the order of <see cref="ObjectWire"/>.</param>
        /// <param name="label">What its lines in the log start with, or null for none.</param>
        public ObjectReplication(NetSession session, Logger log, byte firstMessage, string label)
        {
            shift = firstMessage - ObjectWire.MessageRequest;
            prefix = string.IsNullOrEmpty(label) ? string.Empty : label + ": ";
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            localsView = locals.AsReadOnly();
            session.MessageReceived += OnMessage;
            session.BeforeFlush += OnBeforeFlush;
            session.PlayerLeft += OnPlayerLeft;
        }

        /// <summary>The objects of this machine's world that were handed to the replication.</summary>
        public IList<SharedBody> Locals
        {
            get { return localsView; }
        }

        /// <summary>Objects known to the session and present in this world.</summary>
        public int Shared { get; private set; }

        /// <summary>Objects of the host's world that have no counterpart here.</summary>
        public int MissingHere
        {
            get { return pending.Count; }
        }

        /// <summary>
        /// Name and place of each object of the host's world that has no counterpart here. A program that has no
        /// game world of its own can make one out of these.
        /// </summary>
        public IList<KeyValuePair<string, BodyPose>> Missing
        {
            get
            {
                var missing = new List<KeyValuePair<string, BodyPose>>(pending.Count);
                for (int i = 0; i < pending.Count; i++)
                {
                    missing.Add(new KeyValuePair<string, BodyPose>(pending[i].Name, pending[i].Pose));
                }

                return missing;
            }
        }

        /// <summary>Objects of this world that the host's world does not have. Zero until the host's list arrived.</summary>
        public int OnlyHere
        {
            get { return IsHost || !listReceived ? 0 : locals.Count - Shared; }
        }

        /// <summary>Shared objects that are on the move right now, here or on someone else's machine.</summary>
        public int Moving
        {
            get
            {
                int moving = 0;
                for (int i = 0; i < locals.Count; i++)
                {
                    SharedBody body = locals[i];
                    if (body.HasId && (body.Role == BodyRole.Simulate ? body.LocalAwake || body.Held : !body.AtRest))
                    {
                        moving++;
                    }
                }

                return moving;
            }
        }

        /// <summary>Shared objects this machine is simulating for everyone.</summary>
        public int Simulated
        {
            get
            {
                int simulated = 0;
                for (int i = 0; i < locals.Count; i++)
                {
                    if (locals[i].HasId && locals[i].Role == BodyRole.Simulate)
                    {
                        simulated++;
                    }
                }

                return simulated;
            }
        }

        /// <summary>The shared object in the local player's hand, or null.</summary>
        public SharedBody InHand
        {
            get { return held != null && held.HasId && held.Role == BodyRole.Simulate ? held : null; }
        }

        /// <summary>Object messages that could not be parsed or made no sense.</summary>
        public long Malformed { get; private set; }

        /// <summary>Object states sent, counting each object in each message.</summary>
        public long StatesSent { get; private set; }

        public long StatesReceived { get; private set; }

        /// <summary>Objects this machine's game ate or threw away, told to the others.</summary>
        public long GoneTold { get; private set; }

        /// <summary>Objects another machine's game ate or threw away, to be gone here too.</summary>
        public long GoneHeard { get; private set; }

        /// <summary>Another machine's game ate this object or threw it away: so must this one. The player whose game did.</summary>
        public event Action<SharedBody, byte> GoneWanted;

        /// <summary>On the host: a client's world is ready and it was just sent the whole list. The client's number.</summary>
        public event Action<byte> Subscribed;

        /// <summary>
        /// For measuring only: every state of an object another machine moves, as it arrives, with the sender's clock
        /// (seconds) when it was captured. The test tools listen; the game does not.
        /// </summary>
        public event Action<SharedBody, double, BodyPose> StateHeard;

        /// <summary>On a client: the whole list the host had when this world asked for it is here.</summary>
        public bool ListComplete { get; private set; }

        /// <summary>Whether this machine's world is loaded and its things were handed over (<see cref="SetWorldReady"/>).</summary>
        public bool WorldReady { get; private set; }

        /// <summary>
        /// This machine's world is loaded and the things it has were handed over. On the host, a list sent before that is
        /// not the whole of its world: the clients that asked get the whole list now. Until then a client must not take
        /// what the host's list lacks as gone from the host's world.
        /// </summary>
        public void SetWorldReady()
        {
            if (WorldReady)
            {
                return;
            }

            WorldReady = true;
            if (!IsHost || subscribers.Count == 0)
            {
                return;
            }

            scratch.Clear();
            for (int i = 0; i < locals.Count; i++)
            {
                if (locals[i].HasId)
                {
                    scratch.Add(locals[i]);
                }
            }

            for (int i = 0; i < subscribers.Count; i++)
            {
                SendList(subscribers[i], scratch, true);
            }

            log.Info(LogCategory.Authority, prefix + "This world is ready: " + subscribers.Count + " client(s) got the whole list of " + scratch.Count + " object(s).");
            scratch.Clear();
        }

        /// <summary>
        /// On a client, once <see cref="ListComplete"/>: whether a thing with this key is in the host's world now. A thing
        /// of this world whose key the host does not have was eaten, thrown away or used up there since the save.
        /// </summary>
        public bool HostHasKey(string key)
        {
            return !string.IsNullOrEmpty(key) && hostKeyCount.ContainsKey(key);
        }

        /// <summary>
        /// The host: tells one client again where an object of this machine rests (a checksum found it elsewhere there).
        /// Only what the host simulates and nobody holds.
        /// </summary>
        public void ResendRest(byte player, ushort id)
        {
            SharedBody body;
            if (!IsHost || !byId.TryGetValue(id, out body) || body.Owner != Protocol.HostPlayerId || body.Held)
            {
                return;
            }

            writer.Reset();
            writer.WriteByte((byte)(ObjectWire.MessageRest + shift));
            writer.WriteUInt32(TimeMs());
            writer.WriteUInt16(body.Id);
            body.LocalPose.Write(writer);
            session.Send(player, writer.Buffer, 0, writer.Length, true);
        }

        /// <summary>The host's things that have no counterpart here and have a key: what the host's game made since the save.</summary>
        public IList<MissingThing> MissingKeyed
        {
            get
            {
                var missing = new List<MissingThing>();
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!string.IsNullOrEmpty(pending[i].Key))
                    {
                        missing.Add(new MissingThing { Id = pending[i].Id, Name = pending[i].Name, Key = pending[i].Key, Pose = pending[i].Pose });
                    }
                }

                return missing;
            }
        }

        /// <summary>
        /// The host's things that have no counterpart here and no key: what its game made since the save with no number of
        /// its own (firewood, say), or what this world has elsewhere; <see cref="CountOnlyHere"/> tells them apart by name.
        /// </summary>
        public IList<MissingThing> MissingUnkeyed
        {
            get
            {
                var missing = new List<MissingThing>();
                for (int i = 0; i < pending.Count; i++)
                {
                    if (string.IsNullOrEmpty(pending[i].Key))
                    {
                        missing.Add(new MissingThing { Id = pending[i].Id, Name = pending[i].Name, Key = string.Empty, Pose = pending[i].Pose });
                    }
                }

                return missing;
            }
        }

        /// <summary>How many objects of this world with that name have no counterpart in the host's; none until its list came.</summary>
        public int CountOnlyHere(string name)
        {
            if (IsHost || !listReceived)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < locals.Count; i++)
            {
                if (!locals[i].HasId && locals[i].Name == name)
                {
                    count++;
                }
            }

            return count;
        }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>
        /// Hands an object of this machine's world to the replication. On the host it is shared from then on;
        /// on a client it waits to be matched with an entry of the host's list.
        /// </summary>
        public SharedBody AddLocal(string name, BodyPose pose)
        {
            return AddLocal(name, pose, Protocol.HostPlayerId);
        }

        /// <summary>
        /// The same, for an object that the game of player <paramref name="bornWith"/> has just made and this
        /// machine made after it: on the host, that player simulates it from the start, so that it does not jump
        /// to where it was made here while it is already in their hand. Elsewhere the argument does nothing.
        /// </summary>
        public SharedBody AddLocal(string name, BodyPose pose, byte bornWith)
        {
            return AddLocal(name, pose, bornWith, null);
        }

        /// <summary>The same, with the thing's key (<see cref="SharedBody.Key"/>), or null if it has none.</summary>
        public SharedBody AddLocal(string name, BodyPose pose, byte bornWith, string key)
        {
            var body = new SharedBody(Protocol.LimitText(name, ObjectWire.MaxNameBytes), true)
            {
                LocalPose = pose,
                Key = string.IsNullOrEmpty(key) ? string.Empty : Protocol.LimitText(key, ObjectWire.MaxKeyBytes),
            };
            locals.Add(body);
            if (IsHost)
            {
                Register(body);
                if (bornWith != Protocol.HostPlayerId && bornWith != session.LocalPlayerId && IsConnected(bornWith))
                {
                    body.Owner = bornWith;
                    body.Held = false;
                    body.Role = BodyRole.Follow;
                    body.Buffer = null;
                    body.Target = pose;
                    body.HasTarget = true;
                    body.WasAwake = false;
                    log.Info(LogCategory.Authority, prefix + "Object " + body.Id + " '" + body.Name + "' was made by player " + bornWith + "'s game; it simulates it.");
                }
            }
            else
            {
                Match();
                DescribeUnpairedSoon();
            }

            return body;
        }

        /// <summary>
        /// On a client: asks the host for its objects even though this world has none of its own yet. Normally
        /// the first object handed over does that.
        /// </summary>
        public void RequestList()
        {
            wantsList = true;
        }

        /// <summary>
        /// This machine's game ate the object or threw it away. The others are told, so that it goes in their
        /// worlds too; then hand it back with <see cref="RemoveLocal"/> as usual. Only for the loose objects.
        /// </summary>
        public void Consume(SharedBody body)
        {
            if (shift != 0 || body == null || !body.HasId || body.Gone || session.State != SessionState.Connected)
            {
                return;
            }

            body.Gone = true;
            GoneTold++;
            writer.Reset();
            writer.WriteByte(ObjectWire.MessageGone);
            writer.WriteUInt16(body.Id);
            writer.WriteByte(session.LocalPlayerId);
            session.Send(IsHost ? Protocol.Broadcast : Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            log.Info(LogCategory.Authority, prefix + "Object " + body.Id + " '" + body.Name + "' was eaten or thrown away here; the others are told.");
        }

        /// <summary>The object is gone from this machine's world: destroyed, switched off, built into something.</summary>
        public void RemoveLocal(SharedBody body)
        {
            if (body == null || !locals.Remove(body))
            {
                return;
            }

            if (held == body)
            {
                held = null;
            }

            if (heldUnpaired == body)
            {
                heldUnpaired = null;
            }

            body.Hung = false;
            DescribeUnpairedSoon();
            if (!body.HasId)
            {
                return;
            }

            Shared--;
            if (IsHost)
            {
                byId.Remove(body.Id);
                announce.Remove(body);
                BroadcastPresent(body.Id, false);
            }
            else
            {
                if (body.Role == BodyRole.Simulate && body.Owner == session.LocalPlayerId && body.Present && session.State == SessionState.Connected)
                {
                    // What this machine moved goes back to the host as it was: otherwise it stays still in every other
                    // game, held by nobody (seen in a review on 2026-10-15: a part of the mod that failed gave everything
                    // back to the game here and told nobody).
                    Release(body);
                    body.Held = false;
                }

                byId.Remove(body.Id);
                if (body.Present)
                {
                    // The host still has it. It goes back to being an entry without a counterpart here.
                    pending.Add(new PendingEntry { Id = body.Id, Name = body.Name, Key = body.Key, Pose = WorldPose(body), Owner = body.Owner, Held = body.Held, Since = clock });
                }
            }

            body.HasId = false;
            body.Role = BodyRole.Unmapped;
        }

        /// <summary>This machine left the game world: every object goes back to the game.</summary>
        public void ClearWorld()
        {
            for (int i = locals.Count - 1; i >= 0; i--)
            {
                RemoveLocal(locals[i]);
            }

            requested = false;
            ListComplete = false;
            WorldReady = false;
            hostKeys.Clear();
            hostKeyCount.Clear();

            // With no world here, nothing pairs up, and that is not worth a warning.
            describeUnpairedAt = double.PositiveInfinity;
            describedUnpaired = string.Empty;
            describedHostOnly = string.Empty;
        }

        /// <summary>
        /// The object the local player has in hand, or null. Picking one up makes this machine its owner at
        /// once; the host is told, and says so if someone else was holding it.
        /// </summary>
        public void SetHeld(SharedBody body)
        {
            heldUnpaired = body != null && !body.HasId ? body : null;
            if (body != null && !body.HasId)
            {
                body = null;
            }

            if (body == held)
            {
                return;
            }

            SharedBody before = held;
            held = body;
            if (before != null && before.HasId)
            {
                LetGo(before);
            }

            if (body != null)
            {
                Grab(body);
            }
        }

        /// <summary>
        /// Whether the object hangs from something in this machine's game (the engine block from the hoist), rather than
        /// lying loose. Call it as often as the answer may have changed. No other game has it hanging there: while it
        /// hangs, this machine holds it as a hand would, so that nobody takes it over by being near it or reaching for it
        /// and a client does not hand it back. If someone else simulates it, it is taken over, unless they hold it; then
        /// it is asked for again now and then. Once it no longer hangs it is let go of, as the hand lets go.
        /// </summary>
        public void SetHung(SharedBody body, bool hung)
        {
            if (body == null || !body.HasId)
            {
                return;
            }

            if (!hung)
            {
                if (body.Hung)
                {
                    body.Hung = false;
                    if (body != held && body.Role == BodyRole.Simulate && body.Held)
                    {
                        LetGo(body);
                    }
                }

                return;
            }

            bool first = !body.Hung;
            body.Hung = true;
            if (body.Role == BodyRole.Simulate && body.Held)
            {
                return;
            }

            if (IsHost)
            {
                if (body.Owner != Protocol.HostPlayerId)
                {
                    if (body.Held)
                    {
                        // Someone else holds it: theirs while they do.
                        return;
                    }

                    TakeOver(body, WorldPose(body), default(BodyMotion));
                }

                body.Held = true;
                BroadcastOwner(body);
                log.Info(LogCategory.Authority, prefix + "Object " + body.Id + " '" + body.Name + "' hangs from something in this game: held here while it does.");
                return;
            }

            if (body.Role == BodyRole.Follow && (body.Held || clock < body.NearClaimAt))
            {
                return;
            }

            body.NearClaimAt = clock + NearClaimInterval;
            if (body.Owner != session.LocalPlayerId)
            {
                body.PendingClaim = true;
                body.Role = BodyRole.Simulate;
                body.Buffer = null;
            }

            body.Held = true;
            body.DroppedAt = double.NaN;
            body.HandBackSoon = false;
            SendClaim(body.Id, ObjectWire.ClaimInHand);
            if (first)
            {
                log.Info(LogCategory.Authority, prefix + "Object " + body.Id + " '" + body.Name + "' hangs from something in this game: held here while it does.");
            }
        }

        /// <summary>Call once per frame, before the session's own update, with the same clock.</summary>
        public void Update(double now)
        {
            clock = now;
            if (double.IsNaN(epoch))
            {
                epoch = now;
            }

            if (now >= describeUnpairedAt)
            {
                describeUnpairedAt = double.PositiveInfinity;
                DescribeUnpaired();
            }

            if (!IsHost && session.State == SessionState.Connected)
            {
                HandBackOrphans();
            }
        }

        // Client: what the host says this machine simulates, but this world does not have, goes back to the host
        // after a while. Nobody would move it otherwise.
        private void HandBackOrphans()
        {
            for (int i = 0; i < pending.Count; i++)
            {
                PendingEntry entry = pending[i];
                if (entry.Owner != session.LocalPlayerId || clock - entry.Since < OrphanSeconds)
                {
                    continue;
                }

                writer.Reset();
                writer.WriteByte((byte)(ObjectWire.MessageRelease + shift));
                writer.WriteUInt16(entry.Id);
                entry.Pose.Write(writer);
                default(BodyMotion).Write(writer);
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
                log.Info(LogCategory.Authority, prefix + "Object " + entry.Id + " '" + entry.Name + "' is this machine's to simulate, but this world does not have it; handed back to the host.");
                entry.Owner = Protocol.HostPlayerId;
                entry.Held = false;
                pending[i] = entry;
            }
        }

        private bool IsConnected(byte player)
        {
            IList<PlayerInfo> players = session.Players;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Id == player)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Works out where every followed object goes at local time <paramref name="now"/>. Call it once per
        /// rendered frame, then read <see cref="SharedBody.Target"/>.
        /// </summary>
        public void Sample(double now)
        {
            for (int i = 0; i < locals.Count; i++)
            {
                SharedBody body = locals[i];
                if (body.Role == BodyRole.Follow && body.Buffer != null)
                {
                    BodyPose pose;
                    if (body.Buffer.Sample(now, out pose))
                    {
                        body.Target = pose;
                        body.HasTarget = true;
                    }
                }
            }
        }

        public SharedBody Find(ushort id)
        {
            SharedBody body;
            return byId.TryGetValue(id, out body) ? body : null;
        }

        /// <summary>Stops listening to the session. Every object goes back to the game.</summary>
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
            if (Shared > 0 || pending.Count > 0)
            {
                log.Info(
                    LogCategory.Authority,
                    prefix + "Objects when the session ended: " + Shared + " shared, " + pending.Count + " of the host's missing here, " + OnlyHere
                    + " only here; " + StatesSent + " state(s) sent, " + StatesReceived + " received.");
            }

            for (int i = 0; i < locals.Count; i++)
            {
                locals[i].HasId = false;
                locals[i].Role = BodyRole.Unmapped;
            }

            locals.Clear();
            byId.Clear();
            pending.Clear();
            subscribers.Clear();
            announce.Clear();
            held = null;
            Shared = 0;
            describeUnpairedAt = double.PositiveInfinity;
            describedUnpaired = string.Empty;
            describedHostOnly = string.Empty;
        }

        // The list comes in several messages and the world in several scans: wait for both to settle.
        private void DescribeUnpairedSoon()
        {
            if (!IsHost && listReceived)
            {
                describeUnpairedAt = clock + UnpairedReportDelay;
            }
        }

        // Client: names what the counts in the overlay only count. Two players on different saves, or an object
        // one of them made or used up, look the same from the outside; the names tell them apart.
        private void DescribeUnpaired()
        {
            if (pending.Count == 0 && OnlyHere == 0)
            {
                if (describedUnpaired.Length > 0)
                {
                    describedUnpaired = string.Empty;
                    describedHostOnly = string.Empty;
                    log.Info(LogCategory.Authority, prefix + "Every object pairs up with one of the host's now.");
                }

                return;
            }

            var text = new StringBuilder("The two worlds differ.");
            if (pending.Count > 0)
            {
                text.Append(" Only the host has: ");
                for (int i = 0; i < pending.Count && i < UnpairedNamesLogged; i++)
                {
                    AppendPlaced(text, i, pending[i].Name, pending[i].Pose);
                }

                AppendRest(text, pending.Count);
            }

            string hostOnly = text.ToString();
            if (OnlyHere > 0)
            {
                text.Append(" Only this world has: ");
                int listed = 0;
                for (int i = 0; i < locals.Count && listed < UnpairedNamesLogged; i++)
                {
                    if (!locals[i].HasId)
                    {
                        AppendPlaced(text, listed++, locals[i].Name, locals[i].LocalPose);
                    }
                }

                AppendRest(text, OnlyHere);
            }

            string described = text.ToString();
            if (described == describedUnpaired)
            {
                return;
            }

            if (hostOnly == describedHostOnly && clock < describedUnpairedAt + UnpairedReportEvery)
            {
                // Only what this world alone has changed: said again in a while, as it is then.
                describeUnpairedAt = Math.Min(describeUnpairedAt, describedUnpairedAt + UnpairedReportEvery);
                return;
            }

            describedUnpaired = described;
            describedHostOnly = hostOnly;
            describedUnpairedAt = clock;
            log.Warning(LogCategory.Authority, prefix + described);
        }

        private static void AppendPlaced(StringBuilder text, int index, string name, BodyPose pose)
        {
            if (index > 0)
            {
                text.Append(", ");
            }

            text.Append('\'').Append(name).Append("' at (")
                .Append(pose.X.ToString("0.0", CultureInfo.InvariantCulture)).Append(", ")
                .Append(pose.Y.ToString("0.0", CultureInfo.InvariantCulture)).Append(", ")
                .Append(pose.Z.ToString("0.0", CultureInfo.InvariantCulture)).Append(')');
        }

        private static void AppendRest(StringBuilder text, int total)
        {
            if (total > UnpairedNamesLogged)
            {
                text.Append(", and ").Append(total - UnpairedNamesLogged).Append(" more");
            }

            text.Append('.');
        }

        private uint TimeMs()
        {
            return (uint)Math.Round((clock - epoch) * 1000.0);
        }

        // When the states of a message were the objects', for those reported with the time of their physics step: a moment
        // of the last half second, never later than now. The states of one message were reported in the same frame.
        private uint SampledTimeMs(SharedBody first, uint now)
        {
            double at = first.SampledAt;
            if (double.IsNaN(at) || at > clock || clock - at > 0.5 || at < epoch)
            {
                return now;
            }

            return (uint)Math.Round((at - epoch) * 1000.0);
        }

        // Host: the object becomes part of the session.
        private void Register(SharedBody body)
        {
            body.Id = nextId++;
            body.HasId = true;
            body.Owner = Protocol.HostPlayerId;
            body.Role = BodyRole.Simulate;
            byId[body.Id] = body;
            Shared++;
            if (subscribers.Count > 0)
            {
                announce.Add(body);
            }
        }

        // Client: pairs the host's entries with the objects of this world, by name and then by distance. One
        // whose counterpart the host's world lost is free to be paired again: what turns up there later (a tool
        // put away and taken out, a part taken off a car) comes with a new number.
        private void Match()
        {
            for (int p = 0; p < pending.Count; p++)
            {
                PendingEntry entry = pending[p];
                SharedBody best = null;
                double bestDistance = double.MaxValue;
                bool byKey = false;
                for (int i = 0; i < locals.Count; i++)
                {
                    SharedBody candidate = locals[i];
                    if (candidate.HasId && candidate.Present)
                    {
                        continue;
                    }

                    // The same key is the same thing of the save, however the host's game renamed it; two different keys
                    // are two things, whatever their names.
                    bool sameKey = entry.Key.Length > 0 && candidate.Key == entry.Key;
                    if (!sameKey && (byKey || candidate.Name != entry.Name || (entry.Key.Length > 0 && candidate.Key.Length > 0)))
                    {
                        continue;
                    }

                    if (sameKey && !byKey)
                    {
                        byKey = true;
                        best = null;
                        bestDistance = double.MaxValue;
                    }

                    double distance = WorldPose(candidate).DistanceTo(entry.Pose);
                    if (distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }

                if (best == null)
                {
                    continue;
                }

                if (best.HasId)
                {
                    byId.Remove(best.Id);
                }
                else
                {
                    Shared++;
                }

                best.Id = entry.Id;
                best.HasId = true;
                best.Owner = entry.Owner;
                best.Held = entry.Held;
                best.Present = true;
                best.Role = entry.Owner == session.LocalPlayerId ? BodyRole.Simulate : BodyRole.Follow;
                best.DroppedAt = best.Role == BodyRole.Simulate && !entry.Held ? clock : double.NaN;
                best.Target = entry.Pose;
                best.HasTarget = true;
                best.Buffer = null;
                best.PendingClaim = false;
                best.HandBackSoon = false;
                best.NearClaimAt = 0.0;
                byId[best.Id] = best;
                pending.RemoveAt(p);
                p--;
                if (best == heldUnpaired)
                {
                    heldUnpaired = null;
                    held = best;
                    if (!(entry.Held && entry.Owner != session.LocalPlayerId))
                    {
                        Grab(best);
                    }
                }
            }
        }

        /// <summary>
        /// On a client: whether the object is close to the local player or to what they drive. One that is, and
        /// that the host has, is asked for and simulated here from this moment, so that it gives way when it is
        /// walked or driven into. It stays here for as long as it is near, and then goes back to the host like
        /// an object that was put down. Call it as often as the answer may have changed.
        /// </summary>
        public void SetNear(SharedBody body, bool near)
        {
            if (IsHost || body == null || !body.HasId || body == held)
            {
                return;
            }

            if (!near)
            {
                if (body.Role == BodyRole.Simulate && !body.Held && double.IsNaN(body.DroppedAt))
                {
                    body.DroppedAt = clock;
                }

                return;
            }

            if (body.Role == BodyRole.Simulate)
            {
                if (!body.Held)
                {
                    // Not handed back while it is near, even at rest.
                    body.DroppedAt = double.NaN;
                    body.HandBackSoon = false;
                }

                return;
            }

            // Asked for again now and then: the host may have said no because someone else had it.
            if (body.Role == BodyRole.Follow && body.Owner == Protocol.HostPlayerId && !body.Held && body.Present && clock >= body.NearClaimAt)
            {
                body.NearClaimAt = clock + NearClaimInterval;
                body.PendingClaim = true;
                body.Role = BodyRole.Simulate;
                body.Buffer = null;
                body.Held = false;
                body.DroppedAt = double.NaN;
                SendClaim(body.Id, ObjectWire.ClaimNear);
            }
        }

        /// <summary>
        /// On a client: the object is not for this machine to simulate, and goes back to the host without
        /// waiting for it to lie still. For what was put down or thrown inside a vehicle that someone else
        /// moves: only their machine has the vehicle's real floor under it. It is handed over where it is on the
        /// vehicle, if it was reported as carried.
        /// </summary>
        public void GiveBack(SharedBody body)
        {
            if (IsHost || body == null || !body.HasId || body == held || body.Role != BodyRole.Simulate || body.Held)
            {
                return;
            }

            body.HandBackSoon = true;
            if (double.IsNaN(body.DroppedAt))
            {
                body.DroppedAt = clock;
            }
        }

        /// <summary>
        /// The local game put the object somewhere by itself, at <paramref name="pose"/> (the repair shop's till puts a part
        /// that was paid for on its bench): this machine simulates it from there, as if its player had picked it up and put
        /// it down there at once. The others show it where it is put; on a client it goes back to the host like anything
        /// put down. Nothing changes if someone holds it.
        /// </summary>
        public void Place(SharedBody body, BodyPose pose)
        {
            if (body == null || !body.HasId || body == held || pose.Parent != 0)
            {
                return;
            }

            if (IsHost)
            {
                if (body.Owner != Protocol.HostPlayerId)
                {
                    if (body.Held)
                    {
                        return;
                    }

                    TakeOver(body, pose, default(BodyMotion));
                    BroadcastOwner(body);
                    log.Info(LogCategory.Authority, prefix + "Object " + body.Id + " '" + body.Name + "' was put somewhere by this game: simulated here from there.");
                }

                return;
            }

            if (body.Role == BodyRole.Follow && body.Held)
            {
                // In someone's hand: theirs.
                return;
            }

            if (body.Owner != session.LocalPlayerId)
            {
                body.PendingClaim = true;
                body.Role = BodyRole.Simulate;
                body.Buffer = null;
                log.Info(LogCategory.Authority, prefix + "Object " + body.Id + " '" + body.Name + "' was put somewhere by this game: asked for, and simulated here from there.");
            }

            // Picked up and put down at once: the host gives it, or answers whose it is.
            body.Held = false;
            body.HandBackSoon = false;
            body.DroppedAt = clock;
            body.LocalPose = pose;
            SendClaim(body.Id, ObjectWire.ClaimInHand);
            SendClaim(body.Id, ObjectWire.ClaimLetGo);
        }

        // Where the object is in the world, for everything that is not the stream of states.
        private static BodyPose WorldPose(SharedBody body)
        {
            return body.Role == BodyRole.Follow && body.HasTarget && body.Target.Parent == 0 ? body.Target : body.LocalPose;
        }

        // Where a machine that simulated the object until now goes on showing it, until its new owner's states
        // arrive: on the vehicle that carries it, if one does. Left at its place in the world, it would stay
        // behind while the vehicle drives on.
        private static BodyPose LastReported(SharedBody body)
        {
            return body.StreamPose.Parent != 0 ? body.StreamPose : body.LocalPose;
        }

        private void Grab(SharedBody body)
        {
            if (IsHost)
            {
                if (body.Owner != Protocol.HostPlayerId)
                {
                    if (body.Held)
                    {
                        // In someone else's hand. The local game thinks it picked the object up; it stays where
                        // its holder has it.
                        log.Info(LogCategory.Authority, prefix + "Object " + body.Id + " '" + body.Name + "' is in player " + body.Owner + "'s hand; it stays there.");
                        return;
                    }

                    TakeOver(body, WorldPose(body), default(BodyMotion));
                }

                body.Held = true;
                BroadcastOwner(body);
                return;
            }

            if (body.Owner != session.LocalPlayerId)
            {
                // At once, without waiting for the host: the object has to follow the hand from the first frame.
                body.PendingClaim = true;
                body.Role = BodyRole.Simulate;
                body.Buffer = null;
            }

            body.Held = true;
            body.DroppedAt = double.NaN;
            body.HandBackSoon = false;
            SendClaim(body.Id, ObjectWire.ClaimInHand);
        }

        private void LetGo(SharedBody body)
        {
            if (body.Hung)
            {
                // Let go of by the hand, it still hangs here.
                return;
            }

            if (IsHost)
            {
                if (body.Owner == Protocol.HostPlayerId)
                {
                    body.Held = false;
                    BroadcastOwner(body);
                }

                return;
            }

            if (body.Role == BodyRole.Simulate)
            {
                body.Held = false;
                body.DroppedAt = clock;
                SendClaim(body.Id, ObjectWire.ClaimLetGo);
            }
        }

        // Host: starts simulating an object that someone else was simulating.
        private void TakeOver(SharedBody body, BodyPose pose, BodyMotion motion)
        {
            body.Owner = Protocol.HostPlayerId;
            body.Held = false;
            body.Role = BodyRole.Simulate;
            body.Buffer = null;
            body.HasHandOff = true;
            body.HandOffPose = pose;
            body.HandOffMotion = motion;
            if (pose.Parent == 0)
            {
                body.LocalPose = pose;
            }

            body.WasAwake = true;
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            if (!IsHost)
            {
                return;
            }

            subscribers.Remove(player.Id);
            int reclaimed = 0;
            for (int i = 0; i < locals.Count; i++)
            {
                SharedBody body = locals[i];
                if (body.HasId && body.Owner == player.Id)
                {
                    TakeOver(body, WorldPose(body), default(BodyMotion));
                    BroadcastOwner(body);
                    reclaimed++;
                }
            }

            if (reclaimed > 0)
            {
                log.Info(LogCategory.Authority, prefix + "Player " + player.Id + " left while simulating " + reclaimed + " object(s); the host has them again.");
            }
        }

        private void OnBeforeFlush()
        {
            if (session.State != SessionState.Connected || double.IsNaN(epoch))
            {
                return;
            }

            if (IsHost)
            {
                FlushHost();
            }
            else
            {
                FlushClient();
            }
        }

        private void FlushHost()
        {
            if (subscribers.Count == 0)
            {
                // Nobody to tell. Keep the wake/sleep tracking in step so that the first subscriber is not sent
                // a rest notice for every object.
                for (int i = 0; i < locals.Count; i++)
                {
                    locals[i].WasAwake = locals[i].LocalAwake;
                    locals[i].AwakeSinceSent = false;
                    locals[i].Reported = false;
                }

                return;
            }

            if (announce.Count > 0)
            {
                for (int i = 0; i < subscribers.Count; i++)
                {
                    SendList(subscribers[i], announce, false);
                }

                announce.Clear();
            }

            uint timeMs = TimeMs();
            scratch.Clear();
            for (int i = 0; i < locals.Count; i++)
            {
                SharedBody body = locals[i];
                if (!body.HasId || body.Role != BodyRole.Simulate || !body.Reported)
                {
                    continue;
                }

                body.Reported = false;
                bool moving = body.LocalAwake || body.AwakeSinceSent || body.Held;
                body.AwakeSinceSent = false;
                if (moving)
                {
                    scratch.Add(body);
                }
                else if (body.WasAwake)
                {
                    // Came to rest: once, reliably, with the exact pose.
                    writer.Reset();
                    writer.WriteByte((byte)(ObjectWire.MessageRest + shift));
                    writer.WriteUInt32(timeMs);
                    writer.WriteUInt16(body.Id);
                    body.LocalPose.Write(writer);
                    SendToSubscribers(true);
                }

                body.WasAwake = moving;
            }

            SendStates(scratch, timeMs, true);
        }

        private void FlushClient()
        {
            if (!requested && (locals.Count > 0 || wantsList))
            {
                requested = true;
                writer.Reset();
                writer.WriteByte((byte)(ObjectWire.MessageRequest + shift));
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
                log.Info(LogCategory.Authority, prefix + "Asked the host for its objects; this world has " + locals.Count + ".");
            }

            uint timeMs = TimeMs();
            scratch.Clear();
            for (int i = 0; i < locals.Count; i++)
            {
                SharedBody body = locals[i];
                if (!body.HasId || body.Role != BodyRole.Simulate || !body.Reported)
                {
                    continue;
                }

                body.Reported = false;
                bool awakeSinceSent = body.AwakeSinceSent;
                body.AwakeSinceSent = false;
                double sinceDropped = clock - body.DroppedAt;
                if (!body.Held && sinceDropped > ReleaseNotBeforeSeconds && (!body.LocalAwake || body.HandBackSoon || sinceDropped > ReleaseAfterSeconds))
                {
                    Release(body);
                    continue;
                }

                if (body.LocalAwake || awakeSinceSent || body.Held)
                {
                    scratch.Add(body);
                }
            }

            SendStates(scratch, timeMs, false);
        }

        // Client: hands a let-go object back to the host, with the state to go on from.
        private void Release(SharedBody body)
        {
            writer.Reset();
            writer.WriteByte((byte)(ObjectWire.MessageRelease + shift));
            writer.WriteUInt16(body.Id);

            // Where it is on the vehicle that carries it, if one does: the host's copy of the vehicle is not
            // where this machine's is, and a place in the world would put the object beside it.
            body.StreamPose.Write(writer);
            body.LocalMotion.Write(writer);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);

            body.Owner = Protocol.HostPlayerId;
            body.PendingClaim = false;
            body.DroppedAt = double.NaN;
            body.HandBackSoon = false;
            body.Role = BodyRole.Follow;
            body.Buffer = null;
            body.Target = body.StreamPose;
            body.HasTarget = true;
        }

        // The moving objects this machine simulates, a message full at a time. With more of them than fit in
        // one flush they take turns, so that none is starved.
        private void SendStates(List<SharedBody> moving, uint timeMs, bool host)
        {
            if (moving.Count == 0)
            {
                return;
            }

            int budget = Math.Min(moving.Count, ObjectWire.StateEntries * ObjectWire.StateMessagesPerFlush);
            if (cursor >= moving.Count)
            {
                cursor = 0;
            }

            int sent = 0;
            while (sent < budget)
            {
                int entries = Math.Min(ObjectWire.StateEntries, budget - sent);
                writer.Reset();
                if (host)
                {
                    writer.WriteByte((byte)(ObjectWire.MessageStatesDown + shift));
                    writer.WriteByte(Protocol.HostPlayerId);
                }
                else
                {
                    writer.WriteByte((byte)(ObjectWire.MessageStatesUp + shift));
                }

                writer.WriteUInt32(SampledTimeMs(moving[(cursor + sent) % moving.Count], timeMs));
                writer.WriteByte((byte)entries);
                for (int i = 0; i < entries; i++)
                {
                    SharedBody body = moving[(cursor + sent + i) % moving.Count];
                    writer.WriteUInt16(body.Id);
                    body.StreamPose.Write(writer);
                }

                if (host)
                {
                    SendToSubscribers(false);
                }
                else
                {
                    session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, false);
                }

                sent += entries;
                StatesSent += entries;
            }

            cursor = (cursor + sent) % moving.Count;
        }

        // Host: what is in the writer goes to every client that asked for objects.
        private void SendToSubscribers(bool reliable)
        {
            for (int i = 0; i < subscribers.Count; i++)
            {
                session.Send(subscribers[i], writer.Buffer, 0, writer.Length, reliable);
            }
        }

        private void SendList(byte target, List<SharedBody> bodies, bool whole)
        {
            uint timeMs = TimeMs();
            for (int start = 0; start < bodies.Count || (whole && start == 0); start += ObjectWire.ListEntries)
            {
                int entries = Math.Min(ObjectWire.ListEntries, bodies.Count - start);
                bool last = start + entries >= bodies.Count;
                writer.Reset();
                writer.WriteByte((byte)(ObjectWire.MessageList + shift));
                writer.WriteUInt32(timeMs);
                writer.WriteByte(whole && last ? ObjectWire.ListWhole : (byte)0);
                writer.WriteByte((byte)entries);
                for (int i = 0; i < entries; i++)
                {
                    SharedBody body = bodies[start + i];
                    writer.WriteUInt16(body.Id);
                    writer.WriteByte(body.Owner);
                    writer.WriteByte((byte)((body.Held ? ObjectWire.FlagHeld : 0) | (body.Present ? ObjectWire.FlagPresent : 0)));
                    writer.WriteString(body.Name);
                    writer.WriteString(body.Key);
                    WorldPose(body).Write(writer);
                }

                session.Send(target, writer.Buffer, 0, writer.Length, true);
            }
        }

        private void SendClaim(ushort id, byte kind)
        {
            writer.Reset();
            writer.WriteByte((byte)(ObjectWire.MessageClaim + shift));
            writer.WriteUInt16(id);
            writer.WriteByte(kind);
            session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        }

        private void BroadcastOwner(SharedBody body)
        {
            writer.Reset();
            writer.WriteByte((byte)(ObjectWire.MessageOwner + shift));
            writer.WriteUInt16(body.Id);
            writer.WriteByte(body.Owner);
            writer.WriteByte(body.Held ? (byte)1 : (byte)0);
            SendToSubscribers(true);
        }

        private void BroadcastPresent(ushort id, bool present)
        {
            writer.Reset();
            writer.WriteByte((byte)(ObjectWire.MessagePresent + shift));
            writer.WriteUInt16(id);
            writer.WriteByte(present ? (byte)1 : (byte)0);
            SendToSubscribers(true);
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            if (shift == 0 && data[0] == ObjectWire.MessageGone)
            {
                try
                {
                    reader.SetBuffer(data, 1, data.Length - 1);
                    HandleGone(sender);
                }
                catch (PacketFormatException exception)
                {
                    Malformed++;
                    log.Warning(LogCategory.Authority, prefix + "Dropped an object message from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }

                return;
            }

            int moved = data[0] - shift;
            if (moved < ObjectWire.MessageRequest || moved > ObjectWire.MessagePresent)
            {
                return;
            }

            byte type = (byte)moved;

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                bool fromClient = type == ObjectWire.MessageRequest || type == ObjectWire.MessageStatesUp || type == ObjectWire.MessageClaim
                    || type == ObjectWire.MessageRelease;
                if (fromClient != IsHost)
                {
                    throw new PacketFormatException("Object message " + type + " sent the wrong way.");
                }

                switch (type)
                {
                    case ObjectWire.MessageRequest:
                        HandleRequest(sender);
                        break;
                    case ObjectWire.MessageList:
                        HandleList();
                        break;
                    case ObjectWire.MessageStatesUp:
                        HandleStatesUp(sender);
                        break;
                    case ObjectWire.MessageStatesDown:
                        HandleStatesDown();
                        break;
                    case ObjectWire.MessageClaim:
                        HandleClaim(sender);
                        break;
                    case ObjectWire.MessageOwner:
                        HandleOwner();
                        break;
                    case ObjectWire.MessageRelease:
                        HandleRelease(sender);
                        break;
                    case ObjectWire.MessageRest:
                        HandleRest();
                        break;
                    default:
                        HandlePresent();
                        break;
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Authority, prefix + "Dropped an object message from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        // Someone's game ate an object or threw it away. The host passes it on to the other clients.
        private void HandleGone(byte sender)
        {
            ushort id = reader.ReadUInt16();
            byte player = reader.ReadByte();
            if (IsHost)
            {
                player = sender;
                writer.Reset();
                writer.WriteByte(ObjectWire.MessageGone);
                writer.WriteUInt16(id);
                writer.WriteByte(player);
                IList<PlayerInfo> players = session.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    byte other = players[i].Id;
                    if (other != session.LocalPlayerId && other != sender)
                    {
                        session.Send(other, writer.Buffer, 0, writer.Length, true);
                    }
                }
            }
            else if (sender != Protocol.HostPlayerId)
            {
                throw new PacketFormatException("An object gone, said by someone who is not the host.");
            }

            SharedBody body = Find(id);
            if (body == null || body.Gone)
            {
                return;
            }

            body.Gone = true;
            GoneHeard++;
            log.Info(LogCategory.Authority, prefix + "Object " + id + " '" + body.Name + "' was eaten or thrown away in player " + player + "'s game; it goes here too.");
            Action<SharedBody, byte> handler = GoneWanted;
            if (handler != null)
            {
                handler(body, player);
            }
        }

        // Host: a client's world is loaded. It gets the whole list now and every change from here on.
        private void HandleRequest(byte sender)
        {
            if (!subscribers.Contains(sender))
            {
                subscribers.Add(sender);
            }

            scratch.Clear();
            for (int i = 0; i < locals.Count; i++)
            {
                if (locals[i].HasId)
                {
                    scratch.Add(locals[i]);
                }
            }

            SendList(sender, scratch, WorldReady);
            log.Info(LogCategory.Authority, prefix + "Sent player " + sender + " the list of " + scratch.Count + " object(s)" + (WorldReady ? "." : "; this world is not ready yet, more come when it is."));
            scratch.Clear();
            Action<byte> handler = Subscribed;
            if (handler != null)
            {
                handler(sender);
            }
        }

        private void HandleList()
        {
            reader.ReadUInt32();
            bool whole = (reader.ReadByte() & ObjectWire.ListWhole) != 0;
            int entries = reader.ReadByte();
            for (int i = 0; i < entries; i++)
            {
                var entry = new PendingEntry { Id = reader.ReadUInt16(), Owner = reader.ReadByte(), Since = clock };
                byte flags = reader.ReadByte();
                entry.Held = (flags & ObjectWire.FlagHeld) != 0;
                entry.Name = reader.ReadString(ObjectWire.MaxNameBytes);
                entry.Key = reader.ReadString(ObjectWire.MaxKeyBytes);
                entry.Pose = BodyPose.Read(reader);
                NoteHostKey(entry.Id, entry.Key);
                if (!byId.ContainsKey(entry.Id) && !IsPending(entry.Id))
                {
                    pending.Add(entry);
                }
            }

            if (whole && !ListComplete)
            {
                ListComplete = true;
                log.Info(LogCategory.Authority, prefix + "The host's whole list is here: " + hostKeys.Count + " object(s) with a key from the save.");
            }

            bool first = !listReceived;
            listReceived = true;
            int before = Shared;
            Match();
            DescribeUnpairedSoon();
            if (first || Shared != before)
            {
                log.Info(
                    LogCategory.Authority,
                    prefix + "Objects: " + Shared + " shared with the host, " + pending.Count + " of the host's not found here, " + OnlyHere + " only here.");
            }
        }

        private bool IsPending(ushort id)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].Id == id)
                {
                    return true;
                }
            }

            return false;
        }

        // Host: a client says where the objects it simulates are. Whatever it does not own is ignored; the rest
        // is followed here and passed on to the other clients with the sender's clock untouched.
        private void HandleStatesUp(byte sender)
        {
            uint timeMs = reader.ReadUInt32();
            int entries = reader.ReadByte();
            int accepted = 0;
            writer.Reset();
            writer.WriteByte((byte)(ObjectWire.MessageStatesDown + shift));
            writer.WriteByte(sender);
            writer.WriteUInt32(timeMs);
            writer.WriteByte(0);
            for (int i = 0; i < entries; i++)
            {
                ushort id = reader.ReadUInt16();
                BodyPose pose = BodyPose.Read(reader);
                SharedBody body = Find(id);
                if (body == null || body.Owner != sender)
                {
                    continue;
                }

                Follow(body, timeMs, pose, false);
                writer.WriteUInt16(id);
                pose.Write(writer);
                accepted++;
            }

            StatesReceived += accepted;
            if (accepted > 0 && subscribers.Count > 1)
            {
                writer.Buffer[6] = (byte)accepted;
                for (int i = 0; i < subscribers.Count; i++)
                {
                    if (subscribers[i] != sender)
                    {
                        session.Send(subscribers[i], writer.Buffer, 0, writer.Length, false);
                    }
                }
            }
        }

        private void HandleStatesDown()
        {
            byte owner = reader.ReadByte();
            uint timeMs = reader.ReadUInt32();
            int entries = reader.ReadByte();
            for (int i = 0; i < entries; i++)
            {
                ushort id = reader.ReadUInt16();
                BodyPose pose = BodyPose.Read(reader);
                SharedBody body = Find(id);

                // Only from whoever the host last said simulates it. A state that overtakes the news of a change of
                // owner, or trails behind it, belongs to another clock.
                if (body != null && body.Owner == owner && body.Role == BodyRole.Follow)
                {
                    Follow(body, timeMs, pose, false);
                    StatesReceived++;
                }
            }
        }

        private void Follow(SharedBody body, uint timeMs, BodyPose pose, bool final)
        {
            if (body.Buffer == null)
            {
                body.Buffer = new SnapshotBuffer<BodyPose>(bufferConfig);
            }

            if (final)
            {
                body.Buffer.AddFinal(timeMs / 1000.0, pose, clock);
            }
            else
            {
                body.Buffer.Add(timeMs / 1000.0, pose, clock);
            }

            Action<SharedBody, double, BodyPose> heard = StateHeard;
            if (heard != null)
            {
                heard(body, timeMs / 1000.0, pose);
            }
        }

        // Host: a client picked an object up, or let go of one it has.
        private void HandleClaim(byte sender)
        {
            ushort id = reader.ReadUInt16();
            byte kind = reader.ReadByte();
            if (kind > ObjectWire.ClaimNear)
            {
                throw new PacketFormatException("Unknown kind of claim " + kind + ".");
            }

            bool inHand = kind == ObjectWire.ClaimInHand;
            SharedBody body = Find(id);
            if (body == null)
            {
                return;
            }

            if (body.Owner == sender)
            {
                body.Held = inHand;
            }
            else if (kind == ObjectWire.ClaimLetGo)
            {
                // Letting go of something it does not have: late news, already settled.
                return;
            }
            else if (kind == ObjectWire.ClaimNear && (body.Held || body.Owner != Protocol.HostPlayerId))
            {
                // Being near takes nothing from another player. The answer says who has it, and the claimant
                // goes back to showing it where they say.
                writer.Reset();
                writer.WriteByte((byte)(ObjectWire.MessageOwner + shift));
                writer.WriteUInt16(body.Id);
                writer.WriteByte(body.Owner);
                writer.WriteByte(body.Held ? (byte)1 : (byte)0);
                session.Send(sender, writer.Buffer, 0, writer.Length, true);
                return;
            }
            else if (body.Held)
            {
                // In someone else's hand. The answer says whose, and the claimant stops simulating it.
                writer.Reset();
                writer.WriteByte((byte)(ObjectWire.MessageOwner + shift));
                writer.WriteUInt16(body.Id);
                writer.WriteByte(body.Owner);
                writer.WriteByte(1);
                session.Send(sender, writer.Buffer, 0, writer.Length, true);
                log.Info(LogCategory.Authority, prefix + "Player " + sender + " reached for object " + id + " '" + body.Name + "', which player " + body.Owner + " is holding.");
                return;
            }
            else
            {
                // Until the claimant's own states arrive it stays where it was last seen here.
                if (body.Role == BodyRole.Simulate)
                {
                    body.Target = LastReported(body);
                }

                body.Owner = sender;
                body.Held = inHand;
                body.Role = BodyRole.Follow;
                body.Buffer = null;
                body.HasTarget = true;
                body.WasAwake = false;
            }

            BroadcastOwner(body);
        }

        private void HandleOwner()
        {
            ushort id = reader.ReadUInt16();
            byte owner = reader.ReadByte();
            bool inHand = reader.ReadByte() != 0;
            SharedBody body = Find(id);
            if (body == null)
            {
                UpdatePending(id, owner, inHand);
                return;
            }

            bool mine = owner == session.LocalPlayerId;
            if (mine)
            {
                // Either the answer to our own claim, or old news about an object already handed back.
                if (body.Role == BodyRole.Simulate)
                {
                    body.PendingClaim = false;
                    body.Owner = owner;
                }

                return;
            }

            if (body.Role == BodyRole.Simulate && body.PendingClaim && body.Held && !inHand)
            {
                // The answer to a claim with the hand is "yours" or "in someone's hand": nobody holding it is older news,
                // sent before the claim got there (the answer to handing it back, when the player took it again at once;
                // seen in a review on 2026-10-15: the hand kept a copy that nobody moved).
                return;
            }

            if (body.Role == BodyRole.Simulate && body.PendingClaim && inHand)
            {
                log.Info(LogCategory.Authority, prefix + "Object " + id + " '" + body.Name + "' is in player " + owner + "'s hand; it stays there.");
            }

            bool changed = body.Owner != owner || body.Role != BodyRole.Follow;
            body.Owner = owner;
            body.Held = inHand;
            body.PendingClaim = false;
            body.DroppedAt = double.NaN;
            if (changed)
            {
                // A new owner counts time on another clock: what the old one sent cannot be mixed with it.
                body.Target = body.Role == BodyRole.Simulate ? LastReported(body) : body.Target;
                body.HasTarget = true;
                body.Role = BodyRole.Follow;
                body.Buffer = null;
            }
        }

        private void UpdatePending(ushort id, byte owner, bool inHand)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].Id == id)
                {
                    PendingEntry entry = pending[i];
                    if (entry.Owner != owner)
                    {
                        entry.Since = clock;
                    }

                    entry.Owner = owner;
                    entry.Held = inHand;
                    pending[i] = entry;
                }
            }
        }

        // Host: a client hands an object back, at rest or not, with the state to go on from.
        private void HandleRelease(byte sender)
        {
            ushort id = reader.ReadUInt16();
            BodyPose pose = BodyPose.Read(reader);
            BodyMotion motion = BodyMotion.Read(reader);
            SharedBody body = Find(id);
            if (body == null || body.Owner != sender)
            {
                return;
            }

            TakeOver(body, pose, motion);
            BroadcastOwner(body);
        }

        private void HandleRest()
        {
            uint timeMs = reader.ReadUInt32();
            ushort id = reader.ReadUInt16();
            BodyPose pose = BodyPose.Read(reader);
            SharedBody body = Find(id);
            if (body == null)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (pending[i].Id == id)
                    {
                        PendingEntry entry = pending[i];
                        entry.Pose = pose;
                        pending[i] = entry;
                    }
                }

                return;
            }

            if (body.Owner == Protocol.HostPlayerId && body.Role == BodyRole.Follow)
            {
                Follow(body, timeMs, pose, true);
            }
        }

        private void HandlePresent()
        {
            ushort id = reader.ReadUInt16();
            bool present = reader.ReadByte() != 0;
            if (!present)
            {
                ForgetHostKey(id);
            }

            SharedBody body = Find(id);
            if (body != null)
            {
                body.Present = present;
                return;
            }

            if (!present)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].Id == id)
                    {
                        pending.RemoveAt(i);
                    }
                }
            }
        }

        // A client: the host has a thing with this number and key.
        private void NoteHostKey(ushort id, string key)
        {
            if (string.IsNullOrEmpty(key) || hostKeys.ContainsKey(id))
            {
                return;
            }

            hostKeys[id] = key;
            int count;
            hostKeyCount.TryGetValue(key, out count);
            hostKeyCount[key] = count + 1;
        }

        private void ForgetHostKey(ushort id)
        {
            string key;
            if (!hostKeys.TryGetValue(id, out key))
            {
                return;
            }

            hostKeys.Remove(id);
            int count;
            if (hostKeyCount.TryGetValue(key, out count) && count > 1)
            {
                hostKeyCount[key] = count - 1;
            }
            else
            {
                hostKeyCount.Remove(key);
            }
        }

        private struct PendingEntry
        {
            public ushort Id;
            public string Name;
            public string Key;
            public BodyPose Pose;
            public byte Owner;
            public bool Held;

            // When it became this way: added, or given another owner.
            public double Since;
        }
    }
}
