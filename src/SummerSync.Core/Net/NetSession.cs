using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Logging;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.Net
{
    public sealed class SessionConfig
    {
        public SessionConfig()
        {
            MaxPlayers = 4;
            TickRate = 30;
            SendRate = 30;
            ConnectRetryInterval = 0.5;
            ConnectTimeout = 10;
            PeerTimeout = 10;
            TimeSyncInterval = 1.0;
            ModVersion = string.Empty;
            GameVersion = string.Empty;
            Connection = new ConnectionConfig();
        }

        public int MaxPlayers { get; set; }

        /// <summary>Simulation ticks per second of the shared session clock.</summary>
        public int TickRate { get; set; }

        /// <summary>How many times per second queued messages are packed and sent.</summary>
        public double SendRate { get; set; }

        public double ConnectRetryInterval { get; set; }

        public double ConnectTimeout { get; set; }

        /// <summary>Seconds without hearing from a peer before it is dropped.</summary>
        public double PeerTimeout { get; set; }

        public double TimeSyncInterval { get; set; }

        /// <summary>Must be identical on both ends.</summary>
        public string ModVersion { get; set; }

        /// <summary>Must be identical on both ends.</summary>
        public string GameVersion { get; set; }

        public ConnectionConfig Connection { get; set; }
    }

    public sealed class PlayerInfo
    {
        public PlayerInfo(byte id, string name, bool isLocal)
        {
            Id = id;
            Name = name;
            IsLocal = isLocal;
        }

        public byte Id { get; }

        public string Name { get; }

        public bool IsLocal { get; }

        public bool IsHost
        {
            get { return Id == Protocol.HostPlayerId; }
        }
    }

    /// <summary>Link quality towards one peer, for the debug overlay.</summary>
    public sealed class PeerStats
    {
        public byte PlayerId { get; set; }

        public string Name { get; set; }

        public bool HasRoundTripTime { get; set; }

        public double RoundTripMs { get; set; }

        public double LossPercent { get; set; }

        public double PacketsInPerSecond { get; set; }

        public double PacketsOutPerSecond { get; set; }

        public double BytesInPerSecond { get; set; }

        public double BytesOutPerSecond { get; set; }

        public int ReliableQueue { get; set; }

        public long ReliableResends { get; set; }
    }

    /// <summary>Counters of the built-in test traffic, which proves ordering and delivery on a live link.</summary>
    public sealed class TrafficTestStats
    {
        public long ReliableSent { get; set; }

        public long ReliableReceived { get; set; }

        /// <summary>Reliable test messages that arrived out of order or duplicated. Anything but 0 is a bug.</summary>
        public long ReliableErrors { get; set; }

        public long UnreliableSent { get; set; }

        public long UnreliableReceived { get; set; }
    }

    /// <summary>
    /// A multiplayer session in a star topology: one host, and clients that only talk to the host.
    ///
    /// It handles the handshake (protocol, mod and game versions must match), the player list, timeouts, the
    /// shared clock and tick, and carries game messages. It owns no thread and no clock: call
    /// <see cref="Update"/> once per frame with the current time.
    /// </summary>
    public sealed class NetSession
    {
        private readonly ITransport transport;
        private readonly SessionConfig config;
        private readonly Logger log;
        private readonly byte[] receiveBuffer;
        private readonly PacketReader reader = new PacketReader();
        private readonly PacketWriter control;
        private readonly PacketWriter message;
        private readonly List<ReceivedMessage> received = new List<ReceivedMessage>();
        private readonly List<PlayerInfo> players = new List<PlayerInfo>();
        private readonly Dictionary<ulong, Remote> remotes = new Dictionary<ulong, Remote>();
        private readonly List<Remote> remoteList = new List<Remote>();
        private readonly Random random = new Random();
        private readonly ClockSync clock = new ClockSync();
        private readonly TrafficTestStats traffic = new TrafficTestStats();

        // A gap this long between two updates means this side was frozen, not that the others went away.
        private const double OwnFreezeSeconds = 2.0;
        private const double TestTrafficSilence = 2.0;

        private double now;
        private bool updated;
        private double frozenUntil = double.NegativeInfinity;
        private double nextFlush = double.NegativeInfinity;
        private string localName = string.Empty;

        // Client only.
        private ulong hostPeer;
        private ulong salt;
        private double connectStarted;
        private double lastRequest;
        private double nextTimeSync;

        public NetSession(ITransport transport, SessionConfig config, Logger log)
        {
            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }

            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            this.transport = transport;
            this.config = config;
            this.log = log ?? new Logger();
            config.Connection.MaxDatagramSize = Math.Min(config.Connection.MaxDatagramSize, transport.MaxDatagramSize);
            receiveBuffer = new byte[transport.MaxDatagramSize];
            control = new PacketWriter(transport.MaxDatagramSize);
            message = new PacketWriter(config.Connection.MaxDatagramSize);
        }

        public event Action Connected;

        public event Action<DisconnectReason, string> Disconnected;

        public event Action<PlayerInfo> PlayerJoined;

        public event Action<PlayerInfo, DisconnectReason> PlayerLeft;

        /// <summary>A game message arrived: sender's player id, payload (first byte is its type), reliable or not.</summary>
        public event Action<byte, byte[], bool> MessageReceived;

        /// <summary>
        /// Raised right before the queued messages go out, <see cref="SessionConfig.SendRate"/> times a second.
        /// State that is sent continuously is queued from here, so that it leaves as fresh as it can be.
        /// </summary>
        public event Action BeforeFlush;

        public SessionRole Role { get; private set; }

        public SessionState State { get; private set; }

        public byte LocalPlayerId { get; private set; }

        public IList<PlayerInfo> Players
        {
            get { return players.AsReadOnly(); }
        }

        /// <summary>When on, both ends exchange numbered test messages continuously. See <see cref="Traffic"/>.</summary>
        public bool TestTraffic { get; set; }

        public TrafficTestStats Traffic
        {
            get { return traffic; }
        }

        /// <summary>Whether the session clock is known: always on the host, after the first sync on a client.</summary>
        public bool IsClockSynced
        {
            get { return Role == SessionRole.Host || (State == SessionState.Connected && clock.HasEstimate); }
        }

        /// <summary>The host's clock, in seconds. Meaningful only when <see cref="IsClockSynced"/>.</summary>
        public double SessionTime
        {
            get { return Role == SessionRole.Client ? clock.ToRemote(now) : now; }
        }

        /// <summary>Current tick of the shared session clock.</summary>
        public long Tick
        {
            get { return (long)Math.Floor(SessionTime * config.TickRate); }
        }

        public double ClockOffset
        {
            get { return clock.Offset; }
        }

        /// <summary>Datagrams dropped because they could not be parsed.</summary>
        public long MalformedDatagrams { get; private set; }

        /// <summary>
        /// Datagrams dropped because they belong to an earlier connection with the same peer. A transport that
        /// queues what it receives (Steam does) hands them to whichever session reads next.
        /// </summary>
        public long StaleDatagrams { get; private set; }

        public void StartHost(string playerName)
        {
            RequireIdle();
            Role = SessionRole.Host;
            State = SessionState.Connected;
            LocalPlayerId = Protocol.HostPlayerId;
            localName = Protocol.LimitText(playerName, Protocol.MaxNameBytes);
            players.Add(new PlayerInfo(Protocol.HostPlayerId, localName, true));
            log.Info(LogCategory.Net, "Hosting a session as '" + localName + "' (max " + config.MaxPlayers + " players, protocol " + Protocol.Version + ").");
        }

        public void Connect(ulong host, string playerName)
        {
            RequireIdle();
            Role = SessionRole.Client;
            State = SessionState.Connecting;
            localName = Protocol.LimitText(playerName, Protocol.MaxNameBytes);
            hostPeer = host;
            var saltBytes = new byte[8];
            random.NextBytes(saltBytes);
            salt = BitConverter.ToUInt64(saltBytes, 0);
            connectStarted = double.NaN;
            lastRequest = double.NegativeInfinity;
            log.Info(LogCategory.Net, "Connecting to host " + host + " as '" + localName + "'.");
        }

        /// <summary>Leaves or shuts down the session, telling the other side.</summary>
        public void Close()
        {
            if (Role == SessionRole.None)
            {
                return;
            }

            if (Role == SessionRole.Host)
            {
                foreach (Remote remote in remoteList)
                {
                    SendDisconnect(remote.Peer, DisconnectReason.HostClosed, remote.ConnectionId);
                }
            }
            else
            {
                SendDisconnect(hostPeer, DisconnectReason.ClosedByUser, ConnectionIdOf(salt));
            }

            log.Info(LogCategory.Net, "Session closed.");
            Reset();
        }

        public void Update(double time)
        {
            double gap = time - now;
            now = time;
            if (Role == SessionRole.None)
            {
                return;
            }

            if (updated && gap > OwnFreezeSeconds)
            {
                // This side did not run for a while: a loading screen, most likely. What the others sent in the
                // meantime may be waiting, may have been dropped by a full queue, or both. Their silence was
                // ours, so it does not count against them.
                frozenUntil = now;
                log.Info(LogCategory.Net, "This side did not run for " + gap.ToString("0.0", CultureInfo.InvariantCulture) + " s. The other players get a fresh timeout.");
            }

            updated = true;
            ReceiveAll();
            if (Role == SessionRole.Host)
            {
                UpdateHost();
            }
            else if (Role == SessionRole.Client)
            {
                UpdateClient();
            }

            if (Role != SessionRole.None && now >= nextFlush)
            {
                // The next send time advances by whole intervals from the previous one, not from "now". Measuring
                // from now would round every interval up to the next frame and send at 20 Hz on a 60 fps game.
                double interval = 1.0 / config.SendRate;
                nextFlush = now - nextFlush > interval ? now + interval : nextFlush + interval;
                Raise(BeforeFlush);
                if (Role != SessionRole.None)
                {
                    Flush();
                }
            }
        }

        /// <summary>
        /// How long <paramref name="playerId"/> may stay silent before being given up on, when that is longer than
        /// <see cref="SessionConfig.PeerTimeout"/>; zero for the normal timeout. A player on a loading screen
        /// sends nothing for as long as it lasts. On a client only the host can be named.
        /// </summary>
        public void SetPeerPatience(byte playerId, double seconds)
        {
            for (int i = 0; i < remoteList.Count; i++)
            {
                Remote remote = remoteList[i];
                if (remote.PlayerId == playerId && remote.Patience != seconds)
                {
                    remote.Patience = seconds;
                    log.Debug(LogCategory.Net, "Player " + playerId + " may now stay silent for " + TimeoutFor(remote).ToString("0", CultureInfo.InvariantCulture) + " s.");
                }
            }
        }

        /// <summary>
        /// Sends a game message. <paramref name="target"/> is a player id, or <see cref="Protocol.Broadcast"/> on
        /// the host for every client. A client can only send to the host.
        /// </summary>
        public void Send(byte target, byte[] data, int offset, int count, bool reliable)
        {
            if (count < 1 || data[offset] < Protocol.FirstUserMessage)
            {
                throw new ArgumentException("A game message must start with a type byte of " + Protocol.FirstUserMessage + " or above.");
            }

            if (State != SessionState.Connected)
            {
                return;
            }

            for (int i = 0; i < remoteList.Count; i++)
            {
                Remote remote = remoteList[i];
                if (Role == SessionRole.Client || target == Protocol.Broadcast || target == remote.PlayerId)
                {
                    Queue(remote, data, offset, count, reliable);
                }
            }
        }

        /// <summary>
        /// Unreliable messages to that player (the host, on a client) that the last flush could not fit. Something
        /// bulky waits for this to be 0, or it would crowd out what is said every flush. -1 for a player there is no
        /// connection to.
        /// </summary>
        public int UnreliableWaiting(byte playerId)
        {
            for (int i = 0; i < remoteList.Count; i++)
            {
                Remote remote = remoteList[i];
                if (Role == SessionRole.Client || remote.PlayerId == playerId)
                {
                    return remote.Connection.UnreliableLeftOver;
                }
            }

            return -1;
        }

        public IList<PeerStats> GetPeerStats()
        {
            var stats = new List<PeerStats>(remoteList.Count);
            for (int i = 0; i < remoteList.Count; i++)
            {
                Remote remote = remoteList[i];
                Connection connection = remote.Connection;
                stats.Add(new PeerStats
                {
                    PlayerId = remote.PlayerId,
                    Name = remote.Name,
                    HasRoundTripTime = connection.HasRoundTripTime,
                    RoundTripMs = connection.RoundTripTime * 1000.0,
                    LossPercent = connection.PacketLoss * 100.0,
                    PacketsInPerSecond = remote.PacketsIn.PerSecond,
                    PacketsOutPerSecond = remote.PacketsOut.PerSecond,
                    BytesInPerSecond = remote.BytesIn.PerSecond,
                    BytesOutPerSecond = remote.BytesOut.PerSecond,
                    ReliableQueue = connection.ReliableQueueLength,
                    ReliableResends = connection.ReliableResends,
                });
            }

            return stats;
        }

        private void RequireIdle()
        {
            if (Role != SessionRole.None)
            {
                throw new InvalidOperationException("The session is already running as " + Role + ". Close it first.");
            }
        }

        private void Reset()
        {
            Role = SessionRole.None;
            State = SessionState.Idle;
            LocalPlayerId = 0;
            players.Clear();
            remotes.Clear();
            remoteList.Clear();
            nextFlush = double.NegativeInfinity;
            updated = false;
            frozenUntil = double.NegativeInfinity;
        }

        private void ReceiveAll()
        {
            int count;
            ulong peer;
            while (Role != SessionRole.None && transport.Receive(receiveBuffer, out count, out peer))
            {
                try
                {
                    HandleDatagram(peer, count);
                }
                catch (PacketFormatException exception)
                {
                    // Anything can arrive on a socket; a datagram that does not parse is dropped, never fatal.
                    MalformedDatagrams++;
                    log.Debug(LogCategory.Net, "Dropped a malformed datagram of " + count + " bytes from " + peer + ": " + exception.Message);
                }
            }
        }

        private void HandleDatagram(ulong peer, int count)
        {
            reader.SetBuffer(receiveBuffer, 0, count);
            byte kind = reader.ReadByte();
            if (Role == SessionRole.Host)
            {
                switch (kind)
                {
                    case Protocol.ConnectRequest:
                        HandleConnectRequest(peer);
                        break;
                    case Protocol.Data:
                        Remote remote;
                        if (remotes.TryGetValue(peer, out remote))
                        {
                            HandleData(remote, count);
                        }
                        else
                        {
                            // Someone still talking to a session that no longer knows them, e.g. after a host restart.
                            // The answer names the connection it is about, so that it can only end that one.
                            SendDisconnect(peer, DisconnectReason.HostClosed, reader.ReadUInt32(), 1);
                        }

                        break;
                    case Protocol.Disconnect:
                        uint leaving = reader.ReadUInt32();
                        if (remotes.TryGetValue(peer, out remote) && remote.ConnectionId == leaving)
                        {
                            RemoveRemote(remote, DisconnectReason.ClosedByUser, false);
                        }
                        else
                        {
                            // The goodbye of a connection that is already gone. The same peer may be back by now.
                            StaleDatagrams++;
                        }

                        break;
                }
            }
            else if (peer == hostPeer)
            {
                switch (kind)
                {
                    case Protocol.ConnectAccept:
                        HandleConnectAccept();
                        break;
                    case Protocol.ConnectReject:
                        HandleConnectReject();
                        break;
                    case Protocol.Data:
                        if (State == SessionState.Connected)
                        {
                            HandleData(remoteList[0], count);
                        }

                        break;
                    case Protocol.Disconnect:
                        if (State != SessionState.Idle)
                        {
                            if (reader.ReadUInt32() != ConnectionIdOf(salt))
                            {
                                // About an earlier connection of ours, read late.
                                StaleDatagrams++;
                                break;
                            }

                            byte reason = reader.ReadByte();
                            EndClient((DisconnectReason)reason, "The host closed the connection.");
                        }

                        break;
                }
            }
        }

        private void HandleConnectRequest(ulong peer)
        {
            ushort protocol = reader.ReadUInt16();
            ulong clientSalt = reader.ReadUInt64();
            string modVersion = reader.ReadString(Protocol.MaxVersionBytes);
            string gameVersion = reader.ReadString(Protocol.MaxVersionBytes);
            string name = reader.ReadString(Protocol.MaxNameBytes);

            Remote existing;
            if (remotes.TryGetValue(peer, out existing))
            {
                if (existing.Salt == clientSalt)
                {
                    // Our accept got lost and the client asked again.
                    SendAccept(existing);
                    return;
                }

                // Same peer, new attempt: it restarted without saying goodbye.
                RemoveRemote(existing, DisconnectReason.Timeout, false);
            }

            if (protocol != Protocol.Version)
            {
                Reject(peer, clientSalt, name, DisconnectReason.ProtocolMismatch, "Host speaks protocol " + Protocol.Version + ", you speak " + protocol + ".");
            }
            else if (modVersion != config.ModVersion)
            {
                Reject(peer, clientSalt, name, DisconnectReason.ModVersionMismatch, "Host runs SummerSync " + config.ModVersion + ", you run " + modVersion + ". Every player needs the same build of the mod.");
            }
            else if (gameVersion != config.GameVersion)
            {
                Reject(peer, clientSalt, name, DisconnectReason.GameVersionMismatch, "Host runs game version " + config.GameVersion + ", you run " + gameVersion + ".");
            }
            else if (players.Count >= config.MaxPlayers)
            {
                Reject(peer, clientSalt, name, DisconnectReason.ServerFull, "The session is full (" + config.MaxPlayers + " players).");
            }
            else
            {
                var remote = new Remote(peer, FreePlayerId(), name, clientSalt, new Connection(config.Connection, ConnectionIdOf(clientSalt)), transport, now);
                remotes[peer] = remote;
                remoteList.Add(remote);
                var player = new PlayerInfo(remote.PlayerId, name, false);
                players.Add(player);
                SendAccept(remote);
                log.Info(LogCategory.Net, "Player " + remote.PlayerId + " '" + name + "' joined (peer " + peer + ").");
                Raise(PlayerJoined, player);
                BroadcastPlayerList();
            }
        }

        private byte FreePlayerId()
        {
            for (byte id = 1; id < byte.MaxValue; id++)
            {
                bool taken = false;
                for (int i = 0; i < players.Count; i++)
                {
                    taken |= players[i].Id == id;
                }

                if (!taken)
                {
                    return id;
                }
            }

            throw new InvalidOperationException("No free player id.");
        }

        private void Reject(ulong peer, ulong clientSalt, string name, DisconnectReason reason, string text)
        {
            log.Warning(LogCategory.Net, "Rejected '" + name + "' (peer " + peer + "): " + reason + ". " + text);
            control.Reset();
            control.WriteByte(Protocol.ConnectReject);
            control.WriteUInt64(clientSalt);
            control.WriteByte((byte)reason);
            control.WriteString(text);
            transport.Send(peer, control.Buffer, control.Length);
        }

        private void SendAccept(Remote remote)
        {
            control.Reset();
            control.WriteByte(Protocol.ConnectAccept);
            control.WriteUInt64(remote.Salt);
            control.WriteByte(remote.PlayerId);
            control.WriteByte((byte)config.MaxPlayers);
            control.WriteUInt16((ushort)config.TickRate);
            transport.Send(remote.Peer, control.Buffer, control.Length);
        }

        // Every connection gets a number of its own from the salt the client picked. Data packets and goodbyes
        // carry it, which is what tells them apart from those of an earlier connection between the same two.
        private static uint ConnectionIdOf(ulong salt)
        {
            return (uint)(salt ^ (salt >> 32));
        }

        private void SendDisconnect(ulong peer, DisconnectReason reason, uint connectionId)
        {
            SendDisconnect(peer, reason, connectionId, Protocol.DisconnectRepeats);
        }

        private void SendDisconnect(ulong peer, DisconnectReason reason, uint connectionId, int repeats)
        {
            control.Reset();
            control.WriteByte(Protocol.Disconnect);
            control.WriteUInt32(connectionId);
            control.WriteByte((byte)reason);
            for (int i = 0; i < repeats; i++)
            {
                transport.Send(peer, control.Buffer, control.Length);
            }
        }

        private void HandleConnectAccept()
        {
            ulong echoedSalt = reader.ReadUInt64();
            byte playerId = reader.ReadByte();
            reader.ReadByte();
            ushort tickRate = reader.ReadUInt16();
            if (State != SessionState.Connecting || echoedSalt != salt)
            {
                return;
            }

            State = SessionState.Connected;
            LocalPlayerId = playerId;
            config.TickRate = tickRate;
            var remote = new Remote(hostPeer, Protocol.HostPlayerId, "host", salt, new Connection(config.Connection, ConnectionIdOf(salt)), transport, now);
            remotes[hostPeer] = remote;
            remoteList.Add(remote);
            players.Add(new PlayerInfo(playerId, localName, true));
            nextTimeSync = now;
            log.Info(LogCategory.Net, "Connected to the host as player " + playerId + " (tick rate " + tickRate + ").");
            Raise(Connected);
        }

        private void HandleConnectReject()
        {
            ulong echoedSalt = reader.ReadUInt64();
            byte reason = reader.ReadByte();
            string text = reader.ReadString(Protocol.MaxReasonBytes);
            if (State == SessionState.Connecting && echoedSalt == salt)
            {
                EndClient((DisconnectReason)reason, text);
            }
        }

        private void EndClient(DisconnectReason reason, string text)
        {
            log.Warning(LogCategory.Net, "Disconnected from the host: " + reason + ". " + text);
            Reset();
            Raise(Disconnected, reason, text);
        }

        private void HandleData(Remote remote, int datagramSize)
        {
            received.Clear();
            if (!remote.Connection.Receive(now, reader, datagramSize, received))
            {
                StaleDatagrams++;
                return;
            }

            for (int i = 0; i < received.Count && Role != SessionRole.None; i++)
            {
                HandleMessage(remote, received[i]);
            }
        }

        private void HandleMessage(Remote remote, ReceivedMessage incoming)
        {
            byte[] data = incoming.Data;
            if (data.Length == 0)
            {
                throw new PacketFormatException("Empty message.");
            }

            if (data[0] >= Protocol.FirstUserMessage)
            {
                Action<byte, byte[], bool> handler = MessageReceived;
                if (handler != null)
                {
                    try
                    {
                        handler(remote.PlayerId, data, incoming.Reliable);
                    }
                    catch (Exception exception)
                    {
                        ReportHandlerFailure(exception);
                    }
                }

                return;
            }

            var body = new PacketReader(data, 1, data.Length - 1);
            switch (data[0])
            {
                case Protocol.MessagePlayerList:
                    if (Role == SessionRole.Client)
                    {
                        ApplyPlayerList(body);
                    }

                    break;

                case Protocol.MessageTimeRequest:
                    if (Role == SessionRole.Host)
                    {
                        message.Reset();
                        message.WriteByte(Protocol.MessageTimeResponse);
                        message.WriteDouble(body.ReadDouble());
                        message.WriteDouble(now);
                        remote.Connection.QueueUnreliable(message.Buffer, 0, message.Length);
                    }

                    break;

                case Protocol.MessageTimeResponse:
                    if (Role == SessionRole.Client)
                    {
                        double sentAt = body.ReadDouble();
                        clock.AddSample(sentAt, body.ReadDouble(), now);
                    }

                    break;

                case Protocol.MessageTestReliable:
                    uint counter = body.ReadUInt32();
                    traffic.ReliableReceived++;
                    if (counter != remote.NextTestReliable)
                    {
                        traffic.ReliableErrors++;
                        log.Error(LogCategory.Net, "Reliable test message " + counter + " from player " + remote.PlayerId + " arrived when " + remote.NextTestReliable + " was expected.");
                    }

                    remote.NextTestReliable = counter + 1;
                    break;

                case Protocol.MessageTestUnreliable:
                    traffic.UnreliableReceived++;
                    break;
            }
        }

        private void ApplyPlayerList(PacketReader body)
        {
            int count = body.ReadByte();
            var incoming = new List<PlayerInfo>(count);
            for (int i = 0; i < count; i++)
            {
                byte id = body.ReadByte();
                string name = body.ReadString(Protocol.MaxNameBytes);
                incoming.Add(new PlayerInfo(id, name, id == LocalPlayerId));
            }

            var previous = new List<PlayerInfo>(players);
            players.Clear();
            players.AddRange(incoming);
            remoteList[0].Name = NameOf(incoming, Protocol.HostPlayerId) ?? remoteList[0].Name;

            foreach (PlayerInfo player in incoming)
            {
                if (!player.IsLocal && NameOf(previous, player.Id) == null)
                {
                    Raise(PlayerJoined, player);
                }
            }

            foreach (PlayerInfo player in previous)
            {
                if (!player.IsLocal && NameOf(incoming, player.Id) == null)
                {
                    Raise(PlayerLeft, player, DisconnectReason.None);
                }
            }
        }

        private static string NameOf(List<PlayerInfo> list, byte id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Id == id)
                {
                    return list[i].Name;
                }
            }

            return null;
        }

        private void BroadcastPlayerList()
        {
            message.Reset();
            message.WriteByte(Protocol.MessagePlayerList);
            message.WriteByte((byte)players.Count);
            for (int i = 0; i < players.Count; i++)
            {
                message.WriteByte(players[i].Id);
                message.WriteString(players[i].Name);
            }

            for (int i = 0; i < remoteList.Count; i++)
            {
                remoteList[i].Connection.QueueReliable(message.Buffer, 0, message.Length);
            }
        }

        private void RemoveRemote(Remote remote, DisconnectReason reason, bool notify)
        {
            if (notify)
            {
                SendDisconnect(remote.Peer, reason, remote.ConnectionId);
            }

            remotes.Remove(remote.Peer);
            remoteList.Remove(remote);
            PlayerInfo player = null;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Id == remote.PlayerId)
                {
                    player = players[i];
                    players.RemoveAt(i);
                    break;
                }
            }

            log.Info(LogCategory.Net, "Player " + remote.PlayerId + " '" + remote.Name + "' left: " + reason + ".");
            if (player != null)
            {
                Raise(PlayerLeft, player, reason);
            }

            BroadcastPlayerList();
        }

        private double LastHeard(Remote remote)
        {
            return Math.Max(Math.Max(remote.ConnectedAt, remote.Connection.LastReceiveTime), frozenUntil);
        }

        private double TimeoutFor(Remote remote)
        {
            return Math.Max(config.PeerTimeout, remote.Patience);
        }

        private void UpdateHost()
        {
            for (int i = remoteList.Count - 1; i >= 0; i--)
            {
                Remote remote = remoteList[i];
                if (now - LastHeard(remote) > TimeoutFor(remote))
                {
                    RemoveRemote(remote, DisconnectReason.Timeout, true);
                }
                else if (remote.Connection.IsOverloaded)
                {
                    RemoveRemote(remote, DisconnectReason.ReliableOverflow, true);
                }
            }
        }

        private void UpdateClient()
        {
            if (State == SessionState.Connecting)
            {
                if (double.IsNaN(connectStarted))
                {
                    connectStarted = now;
                }

                if (now - connectStarted > config.ConnectTimeout)
                {
                    EndClient(DisconnectReason.ConnectFailed, "The host did not answer.");
                }
                else if (now - lastRequest >= config.ConnectRetryInterval)
                {
                    lastRequest = now;
                    control.Reset();
                    control.WriteByte(Protocol.ConnectRequest);
                    control.WriteUInt16(Protocol.Version);
                    control.WriteUInt64(salt);
                    control.WriteString(config.ModVersion);
                    control.WriteString(config.GameVersion);
                    control.WriteString(localName);
                    transport.Send(hostPeer, control.Buffer, control.Length);
                }

                return;
            }

            Remote host = remoteList[0];
            if (now - LastHeard(host) > TimeoutFor(host))
            {
                EndClient(DisconnectReason.Timeout, "Nothing received from the host for " + TimeoutFor(host).ToString("0", CultureInfo.InvariantCulture) + " seconds.");
                return;
            }

            if (host.Connection.IsOverloaded)
            {
                EndClient(DisconnectReason.ReliableOverflow, "The host stopped acknowledging messages.");
                return;
            }

            if (now >= nextTimeSync)
            {
                // A quick burst at the start gives a usable clock right away; after that a slow trickle tracks drift.
                nextTimeSync = now + (clock.Samples < 5 ? 0.2 : config.TimeSyncInterval);
                message.Reset();
                message.WriteByte(Protocol.MessageTimeRequest);
                message.WriteDouble(now);
                host.Connection.QueueUnreliable(message.Buffer, 0, message.Length);
            }
        }

        private void Flush()
        {
            for (int i = 0; i < remoteList.Count; i++)
            {
                Remote remote = remoteList[i];

                // Not to a player who has gone quiet: on a loading screen nothing is acknowledged, and a minute of
                // numbered messages would pile up in the reliable queue for no reason.
                if (TestTraffic && now - LastHeard(remote) < TestTrafficSilence)
                {
                    message.Reset();
                    message.WriteByte(Protocol.MessageTestReliable);
                    message.WriteUInt32(remote.TestReliableSent++);
                    remote.Connection.QueueReliable(message.Buffer, 0, message.Length);
                    traffic.ReliableSent++;

                    message.Reset();
                    message.WriteByte(Protocol.MessageTestUnreliable);
                    remote.Connection.QueueUnreliable(message.Buffer, 0, message.Length);
                    traffic.UnreliableSent++;
                }

                Connection connection = remote.Connection;
                connection.Flush(now, remote.Sender);
                remote.PacketsIn.Update(now, connection.PacketsReceived);
                remote.PacketsOut.Update(now, connection.PacketsSent);
                remote.BytesIn.Update(now, connection.BytesReceived);
                remote.BytesOut.Update(now, connection.BytesSent);
            }
        }

        private void Queue(Remote remote, byte[] data, int offset, int count, bool reliable)
        {
            if (reliable)
            {
                remote.Connection.QueueReliable(data, offset, count);
            }
            else
            {
                remote.Connection.QueueUnreliable(data, offset, count);
            }
        }

        // Subscribers are game code running in the middle of packet processing. If one throws, the session must
        // still finish the frame in a consistent state, so the failure is logged here instead of propagating.
        private void Raise(Action handler)
        {
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception exception)
                {
                    ReportHandlerFailure(exception);
                }
            }
        }

        private void Raise<T>(Action<T> handler, T argument)
        {
            if (handler != null)
            {
                try
                {
                    handler(argument);
                }
                catch (Exception exception)
                {
                    ReportHandlerFailure(exception);
                }
            }
        }

        private void Raise<T1, T2>(Action<T1, T2> handler, T1 first, T2 second)
        {
            if (handler != null)
            {
                try
                {
                    handler(first, second);
                }
                catch (Exception exception)
                {
                    ReportHandlerFailure(exception);
                }
            }
        }

        private void ReportHandlerFailure(Exception exception)
        {
            log.Error(LogCategory.Net, "A session event handler failed: " + exception);
        }

        private sealed class Remote
        {
            private readonly ITransport transport;

            public Remote(ulong peer, byte playerId, string name, ulong salt, Connection connection, ITransport transport, double connectedAt)
            {
                Peer = peer;
                PlayerId = playerId;
                Name = name;
                Salt = salt;
                Connection = connection;
                ConnectedAt = connectedAt;
                this.transport = transport;
                Sender = SendDatagram;
                PacketsIn = new RateSampler();
                PacketsOut = new RateSampler();
                BytesIn = new RateSampler();
                BytesOut = new RateSampler();
            }

            public ulong Peer { get; }

            public byte PlayerId { get; }

            public string Name { get; set; }

            public ulong Salt { get; }

            public uint ConnectionId
            {
                get { return ConnectionIdOf(Salt); }
            }

            public Connection Connection { get; }

            /// <summary>Seconds of silence granted beyond the normal timeout; zero for none.</summary>
            public double Patience { get; set; }

            public double ConnectedAt { get; }

            /// <summary>Kept as a field so flushing does not allocate a delegate every time.</summary>
            public Action<byte[], int> Sender { get; }

            public RateSampler PacketsIn { get; }

            public RateSampler PacketsOut { get; }

            public RateSampler BytesIn { get; }

            public RateSampler BytesOut { get; }

            public uint TestReliableSent { get; set; }

            public uint NextTestReliable { get; set; }

            private void SendDatagram(byte[] data, int count)
            {
                transport.Send(Peer, data, count);
            }
        }
    }
}
