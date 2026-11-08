using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.Players
{
    /// <summary>Another player of the session, as this side sees them.</summary>
    public sealed class RemotePlayer
    {
        private readonly Dictionary<byte, ViewQuality> views = new Dictionary<byte, ViewQuality>();

        internal RemotePlayer(byte id, string name)
        {
            Id = id;
            Name = name;
            Buffer = new PoseBuffer();
        }

        public byte Id { get; private set; }

        public string Name { get; internal set; }

        /// <summary>Whether a state has arrived from this player at all.</summary>
        public bool HasState { get; internal set; }

        /// <summary>
        /// Whether the player has a body in the game world, according to their newest state. False in the main
        /// menu and on loading screens.
        /// </summary>
        public bool InWorld { get; internal set; }

        /// <summary>Local time, in seconds, when their newest state arrived.</summary>
        public double LastHeard { get; internal set; }

        public long StatesReceived { get; internal set; }

        public PoseBuffer Buffer { get; private set; }

        /// <summary>Set by <see cref="PlayerReplication.Sample"/>: whether there is a pose to draw.</summary>
        public bool Visible { get; internal set; }

        /// <summary>Set by <see cref="PlayerReplication.Sample"/>: the pose to draw when <see cref="Visible"/>.</summary>
        public PlayerPose Shown { get; internal set; }

        /// <summary>
        /// On the host: how this player says the others look on their screen, by player id. Empty elsewhere.
        /// </summary>
        public IDictionary<byte, ViewQuality> Views
        {
            get { return views; }
        }

        internal uint NewestTimeMs { get; set; }

        internal ushort NewestSequence { get; set; }

        internal PlayerPose NewestPose { get; set; }
    }

    /// <summary>
    /// Keeps every player's position flowing through a session. Each side sends its own state with every
    /// flush of the session, unreliably; the host passes each client's newest state on to the other clients
    /// with the sender's clock untouched. What arrives goes into a <see cref="PoseBuffer"/> per player.
    ///
    /// A player who is not in the game world (main menu, loading screen) says so twice a second instead. That
    /// doubles as the notice that they may go silent: loading a save freezes the game, so the session is told
    /// to wait much longer for them than for a player who is in the world.
    /// </summary>
    public sealed class PlayerReplication
    {
        /// <summary>Seconds a player who is not in the game world may stay silent before being dropped.</summary>
        public const double MenuPatienceSeconds = 180.0;

        // A player outside the world reports every this many flushes: twice a second at 30 flushes a second.
        private const int IdleFlushes = 15;

        // A client tells the host how it sees the others every this many flushes: every five seconds.
        private const int ReportFlushes = 150;
        private const double MalformedLogInterval = 5.0;

        private readonly NetSession session;
        private readonly Logger log;
        private readonly List<RemotePlayer> remotes = new List<RemotePlayer>();
        private readonly IList<RemotePlayer> remotesView;
        private readonly Dictionary<int, uint> relayed = new Dictionary<int, uint>();
        private readonly PacketWriter writer = new PacketWriter(2 + (16 * (1 + PlayerWire.MaxStateBytes)));
        private readonly PacketReader reader = new PacketReader();

        private double clock;
        private double epoch = double.NaN;
        private int flushes;
        private ushort sequence;
        private bool localSent;
        private bool lastSentInWorld;
        private double nextMalformedLog = double.NegativeInfinity;
        private bool detached;

        public PlayerReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            remotesView = remotes.AsReadOnly();
            session.MessageReceived += OnMessage;
            session.BeforeFlush += OnBeforeFlush;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
        }

        public bool LocalInWorld { get; private set; }

        public PlayerPose LocalPose { get; private set; }

        /// <summary>States sent about the local player.</summary>
        public long StatesSent { get; private set; }

        /// <summary>Player messages that could not be parsed.</summary>
        public long Malformed { get; private set; }

        /// <summary>The other players. The same live, read-only list on every call: it is read every frame.</summary>
        public IList<RemotePlayer> Remotes
        {
            get { return remotesView; }
        }

        /// <summary>
        /// The local player's current state. Call it every frame; the newest one is what goes out.
        /// </summary>
        public void SetLocal(bool inWorld, PlayerPose pose)
        {
            if (inWorld != LocalInWorld)
            {
                log.Info(LogCategory.Player, inWorld ? "The local player is in the game world." : "The local player left the game world.");
            }

            LocalInWorld = inWorld;
            LocalPose = pose;
        }

        /// <summary>Call once per frame, before the session's own update, with the same clock.</summary>
        public void Update(double now)
        {
            clock = now;
            if (double.IsNaN(epoch))
            {
                epoch = now;
            }
        }

        /// <summary>
        /// Works out what to draw for every remote player at local time <paramref name="now"/>. Call it once
        /// per rendered frame, then read <see cref="RemotePlayer.Visible"/> and <see cref="RemotePlayer.Shown"/>.
        /// </summary>
        public void Sample(double now)
        {
            for (int i = 0; i < remotes.Count; i++)
            {
                RemotePlayer remote = remotes[i];
                PlayerPose pose = default(PlayerPose);
                remote.Visible = remote.InWorld && remote.Buffer.Sample(now, out pose);
                if (remote.Visible)
                {
                    remote.Shown = pose;
                }
            }
        }

        public RemotePlayer Find(byte id)
        {
            for (int i = 0; i < remotes.Count; i++)
            {
                if (remotes[i].Id == id)
                {
                    return remotes[i];
                }
            }

            return null;
        }

        /// <summary>Stops listening to the session and logs how each remote player was seen.</summary>
        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.BeforeFlush -= OnBeforeFlush;
            session.PlayerJoined -= OnPlayerJoined;
            session.PlayerLeft -= OnPlayerLeft;
            for (int i = 0; i < remotes.Count; i++)
            {
                LogSummary(remotes[i]);
            }

            remotes.Clear();
            relayed.Clear();
        }

        /// <summary>One line on how a remote player's movement came through, for the log and the overlay.</summary>
        public static string DescribeQuality(RemotePlayer remote)
        {
            return ViewQuality.Of(remote.Buffer).DescribeSmoothness();
        }

        /// <summary>What went missing on the way and whose game stood still, for the log.</summary>
        public static string DescribeDelivery(RemotePlayer remote)
        {
            return ViewQuality.Of(remote.Buffer).DescribeDelivery();
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (!player.IsLocal)
            {
                Ensure(player.Id).Name = player.Name;
            }
        }

        private void OnPlayerLeft(PlayerInfo player, DisconnectReason reason)
        {
            RemotePlayer remote = Find(player.Id);
            if (remote == null)
            {
                return;
            }

            LogSummary(remote);
            remotes.Remove(remote);

            // Player ids are reused, and a new player must not inherit what was relayed for the old one.
            var stale = new List<int>();
            foreach (int key in relayed.Keys)
            {
                if ((key >> 8) == player.Id || (key & 0xFF) == player.Id)
                {
                    stale.Add(key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                relayed.Remove(stale[i]);
            }
        }

        private RemotePlayer Ensure(byte id)
        {
            RemotePlayer remote = Find(id);
            if (remote == null)
            {
                // A state can overtake the player list that names its sender.
                string name = "player " + id;
                IList<PlayerInfo> players = session.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i].Id == id)
                    {
                        name = players[i].Name;
                    }
                }

                remote = new RemotePlayer(id, name);
                remotes.Add(remote);
            }

            return remote;
        }

        private void OnBeforeFlush()
        {
            if (session.State != SessionState.Connected || double.IsNaN(epoch))
            {
                return;
            }

            flushes++;
            uint timeMs = (uint)Math.Round((clock - epoch) * 1000.0);

            // In the world: every flush. Outside it: now and then, and at once when that has just changed.
            bool own = LocalInWorld || !localSent || lastSentInWorld != LocalInWorld || flushes % IdleFlushes == 0;
            if (own)
            {
                localSent = true;
                lastSentInWorld = LocalInWorld;
                sequence++;
            }

            if (session.Role == SessionRole.Client)
            {
                if (flushes % ReportFlushes == 0)
                {
                    SendViewReport();
                }

                if (own)
                {
                    writer.Reset();
                    writer.WriteByte(PlayerWire.MessageState);
                    PlayerWire.WriteState(writer, timeMs, sequence, LocalInWorld, LocalPose);
                    session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, false);
                    StatesSent++;
                }

                return;
            }

            for (int i = 0; i < remotes.Count; i++)
            {
                RemotePlayer target = remotes[i];
                writer.Reset();
                writer.WriteByte(PlayerWire.MessageStates);
                writer.WriteByte(0);
                int entries = 0;
                if (own)
                {
                    writer.WriteByte(Protocol.HostPlayerId);
                    PlayerWire.WriteState(writer, timeMs, sequence, LocalInWorld, LocalPose);
                    entries++;
                }

                for (int k = 0; k < remotes.Count; k++)
                {
                    RemotePlayer other = remotes[k];
                    if (k == i || !other.HasState)
                    {
                        continue;
                    }

                    // Only what this client has not been sent yet. The sender's clock goes along unchanged, so
                    // the receiver interpolates the original movement, not the host's resampling of it.
                    int key = (target.Id << 8) | other.Id;
                    uint sent;
                    if (relayed.TryGetValue(key, out sent) && sent == other.NewestTimeMs)
                    {
                        continue;
                    }

                    relayed[key] = other.NewestTimeMs;
                    writer.WriteByte(other.Id);
                    PlayerWire.WriteState(writer, other.NewestTimeMs, other.NewestSequence, other.InWorld, other.NewestPose);
                    entries++;
                }

                if (entries > 0)
                {
                    writer.Buffer[1] = (byte)entries;
                    session.Send(target.Id, writer.Buffer, 0, writer.Length, false);
                }
            }

            if (own)
            {
                StatesSent++;
            }
        }

        // How every player in the world looks from here, for the host's log.
        private void SendViewReport()
        {
            writer.Reset();
            writer.WriteByte(PlayerWire.MessageViewReport);
            writer.WriteByte(0);
            int entries = 0;
            for (int i = 0; i < remotes.Count; i++)
            {
                if (remotes[i].Buffer.PlayedSeconds > 0)
                {
                    writer.WriteByte(remotes[i].Id);
                    ViewQuality.Of(remotes[i].Buffer).Write(writer);
                    entries++;
                }
            }

            if (entries > 0)
            {
                writer.Buffer[1] = (byte)entries;
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            }
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != PlayerWire.MessageState && type != PlayerWire.MessageStates && type != PlayerWire.MessageViewReport)
            {
                return;
            }

            try
            {
                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == PlayerWire.MessageState)
                {
                    // Only the host is told a player's own state, and the session says who sent it: a client
                    // cannot speak for anyone else.
                    if (session.Role != SessionRole.Host)
                    {
                        throw new PacketFormatException("A player state sent to a client.");
                    }

                    ReadAndApply(sender);
                }
                else if (type == PlayerWire.MessageViewReport)
                {
                    if (session.Role != SessionRole.Host)
                    {
                        throw new PacketFormatException("A view report sent to a client.");
                    }

                    RemotePlayer reporter = Ensure(sender);
                    int entries = reader.ReadByte();
                    for (int i = 0; i < entries; i++)
                    {
                        byte subject = reader.ReadByte();
                        reporter.Views[subject] = ViewQuality.Read(reader);
                    }
                }
                else
                {
                    if (session.Role != SessionRole.Client)
                    {
                        throw new PacketFormatException("A list of player states sent to the host.");
                    }

                    int entries = reader.ReadByte();
                    for (int i = 0; i < entries; i++)
                    {
                        ReadAndApply(reader.ReadByte());
                    }
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Player, "Dropped a player message from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }

        private void ReadAndApply(byte playerId)
        {
            uint timeMs;
            ushort number;
            bool inWorld;
            PlayerPose pose;
            PlayerWire.ReadState(reader, out timeMs, out number, out inWorld, out pose);
            if (playerId == session.LocalPlayerId)
            {
                return;
            }

            RemotePlayer remote = Ensure(playerId);
            remote.StatesReceived++;

            // The channel reorders. Whether the player is in the world, and what gets relayed, follow the newest
            // state by the sender's clock; an older one is still a point on their path worth interpolating through.
            if (!remote.HasState || (int)(timeMs - remote.NewestTimeMs) > 0)
            {
                bool first = !remote.HasState;
                remote.HasState = true;
                remote.NewestTimeMs = timeMs;
                remote.NewestSequence = number;
                remote.NewestPose = pose;
                remote.LastHeard = clock;
                if (first || remote.InWorld != inWorld)
                {
                    remote.InWorld = inWorld;
                    if (!inWorld)
                    {
                        remote.Buffer.Clear();
                        remote.Visible = false;
                    }

                    log.Info(LogCategory.Player, "Player " + remote.Id + " '" + remote.Name + "' is " + (inWorld ? "in the game world." : "not in the game world (main menu or loading)."));

                    // Our link is with the host if we are a client and with each client if we are the host; a
                    // client has no say over how long the host waits for another client.
                    if (session.Role == SessionRole.Host || remote.Id == Protocol.HostPlayerId)
                    {
                        session.SetPeerPatience(remote.Id, inWorld ? 0.0 : MenuPatienceSeconds);
                    }
                }
            }

            if (inWorld && remote.InWorld)
            {
                remote.Buffer.Add(timeMs / 1000.0, pose, clock, number);
            }
        }

        private void LogSummary(RemotePlayer remote)
        {
            LogViews(remote);
            if (remote.StatesReceived == 0)
            {
                return;
            }

            PoseBuffer buffer = remote.Buffer;
            log.Info(
                LogCategory.Player,
                "Player " + remote.Id + " '" + remote.Name + "' as seen from here: " + remote.StatesReceived + " state(s), shown for "
                + buffer.PlayedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + DescribeQuality(remote)
                + ", " + DescribeDelivery(remote) + ", " + buffer.Late + " late, " + buffer.Duplicates + " duplicate(s), " + buffer.Resyncs + " clock restart(s).");
        }

        // What the player last reported about their own screen. Up to five seconds old when they leave.
        private void LogViews(RemotePlayer remote)
        {
            foreach (KeyValuePair<byte, ViewQuality> view in remote.Views)
            {
                ViewQuality quality = view.Value;
                log.Info(
                    LogCategory.Player,
                    "Player " + remote.Id + " '" + remote.Name + "' saw player " + view.Key + " like this: shown for " + quality.ShownSeconds + " s, "
                    + quality.DescribeSmoothness() + ", " + quality.DescribeDelivery() + ".");
            }
        }
    }
}
