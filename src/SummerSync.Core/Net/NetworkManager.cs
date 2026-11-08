using System;
using SummerSync.Core.Logging;
using SummerSync.Core.Objects;
using SummerSync.Core.Vehicles;
using SummerSync.Core.Players;
using SummerSync.Core.Diagnostics;
using SummerSync.Core.World;

namespace SummerSync.Core.Net
{
    public enum NetworkMode
    {
        Offline,

        /// <summary>A host and a client inside this process, talking over an in-memory network.</summary>
        LoopbackTest,
        Host,
        Client,
    }

    /// <summary>
    /// Runs at most one multiplayer session at a time and owns everything that belongs to it: the transport,
    /// the network simulator wrapped around it, and the session itself. Whoever starts a session only picks the
    /// transport; from there on it is driven by <see cref="Update"/> and ended by <see cref="Stop"/> or by the
    /// other side.
    /// </summary>
    public sealed class NetworkManager
    {
        public const string LoopbackTransportName = "loopback";

        private const ulong LoopbackHostPeer = 1;
        private const ulong LoopbackClientPeer = 2;

        private readonly Logger log;
        private readonly NetworkConditions conditions = new NetworkConditions();
        private int seed;
        private Link local;
        private Link loopbackHost;
        private bool endPending;
        private DisconnectReason endReason;
        private string endText;

        public NetworkManager(Logger log)
            : this(log, Environment.TickCount)
        {
        }

        /// <param name="seed">Seed of the network simulator's random numbers, for repeatable tests.</param>
        public NetworkManager(Logger log, int seed)
        {
            this.log = log ?? new Logger();
            this.seed = seed;
            ModVersion = string.Empty;
            GameVersion = string.Empty;
            TransportName = string.Empty;
            MaxPlayers = 4;
            TestTraffic = true;
            ConnectTimeout = 20;
        }

        /// <summary>
        /// A running session ended, by <see cref="Stop"/> or because the other side closed or dropped it: the
        /// mode it was in, the reason, and a text for the player.
        /// </summary>
        public event Action<NetworkMode, DisconnectReason, string> Stopped;

        /// <summary>Must be set before starting a session, and be identical on both ends.</summary>
        public string ModVersion { get; set; }

        /// <summary>Must be set before starting a session, and be identical on both ends.</summary>
        public string GameVersion { get; set; }

        public int MaxPlayers { get; set; }

        /// <summary>
        /// Seconds a client keeps asking before giving up. Generous, because a Steam P2P link that has to fall
        /// back to a relay takes a while to open.
        /// </summary>
        public double ConnectTimeout { get; set; }

        /// <summary>
        /// Whether new sessions exchange numbered test messages continuously. On while there is no game traffic
        /// yet: it is what proves ordering and delivery on a live link.
        /// </summary>
        public bool TestTraffic { get; set; }

        /// <summary>Bad network conditions imposed on everything this process sends. Can be changed at any time.</summary>
        public NetworkConditions Conditions
        {
            get { return conditions; }
        }

        public NetworkMode Mode { get; private set; }

        public bool IsActive
        {
            get { return Mode != NetworkMode.Offline; }
        }

        /// <summary>Short name of the transport in use: "loopback", "udp", "steam". Empty when offline.</summary>
        public string TransportName { get; private set; }

        /// <summary>The local player's session, or null when offline. In the loopback test, the client end.</summary>
        public NetSession Session
        {
            get { return local == null ? null : local.Session; }
        }

        /// <summary>The host end of the loopback test, or null in every other mode.</summary>
        public NetSession LoopbackHostSession
        {
            get { return loopbackHost == null ? null : loopbackHost.Session; }
        }

        /// <summary>
        /// The other players as the local player sees them, or null when offline. In the loopback test the
        /// only other player is the local one, echoed back through the simulated network.
        /// </summary>
        public PlayerReplication Players
        {
            get { return local == null ? null : local.Players; }
        }

        /// <summary>The replication that sends the local player's state: the host end in the loopback test.</summary>
        public PlayerReplication LocalPlayers
        {
            get { return loopbackHost != null ? loopbackHost.Players : Players; }
        }

        /// <summary>
        /// The loose objects shared with the other players, or null when offline and in the loopback test: there
        /// both ends live in the same game world, and an object cannot be its own copy.
        /// </summary>
        public ObjectReplication Objects
        {
            get { return local == null || loopbackHost != null ? null : local.Objects; }
        }

        /// <summary>
        /// The vehicles shared with the other players, each a body whose "hand" is the driver's seat. Null like
        /// <see cref="Objects"/>.
        /// </summary>
        public ObjectReplication Vehicles
        {
            get { return local == null || loopbackHost != null ? null : local.Vehicles; }
        }

        /// <summary>The doors and other two-state things shared with the other players. Null like <see cref="Objects"/>.</summary>
        public SwitchReplication Switches
        {
            get { return local == null || loopbackHost != null ? null : local.Switches; }
        }

        /// <summary>The time and the weather of the world, as the host has them. Null like <see cref="Objects"/>.</summary>
        public WorldReplication World
        {
            get { return local == null || loopbackHost != null ? null : local.World; }
        }

        /// <summary>The host's own songs, on their way to the others. Null like <see cref="Objects"/>.</summary>
        public FileTransfer Songs
        {
            get { return local == null || loopbackHost != null ? null : local.Songs; }
        }

        /// <summary>The host's saved world, on its way to the others. Null like <see cref="Objects"/>.</summary>
        public WorldShare SavedWorld
        {
            get { return local == null || loopbackHost != null ? null : local.SavedWorld; }
        }

        /// <summary>The money, which is one for every player. Null like <see cref="Objects"/>.</summary>
        public WalletReplication Wallet
        {
            get { return local == null || loopbackHost != null ? null : local.Wallet; }
        }

        /// <summary>What the games make while they run, made everywhere. Null like <see cref="Objects"/>.</summary>
        public SpawnReplication Spawns
        {
            get { return local == null || loopbackHost != null ? null : local.Spawns; }
        }

        /// <summary>The shop's counters and shelves, one for every player. Null like <see cref="Objects"/>.</summary>
        public TallyReplication Tallies
        {
            get { return local == null || loopbackHost != null ? null : local.Tallies; }
        }

        /// <summary>What happens to loose objects besides moving, and what players throw away. Null like <see cref="Objects"/>.</summary>
        public ItemReplication Items
        {
            get { return local == null || loopbackHost != null ? null : local.Items; }
        }

        /// <summary>Machines one player works and everyone watches (the video poker). Null like <see cref="Objects"/>.</summary>
        public PanelReplication Panels
        {
            get { return local == null || loopbackHost != null ? null : local.Panels; }
        }

        /// <summary>The traffic of the host's game. Null like <see cref="Objects"/>.</summary>
        public TrafficReplication Traffic
        {
            get { return local == null || loopbackHost != null ? null : local.Traffic; }
        }

        /// <summary>The people who walk the roads in the host's game. Null like <see cref="Objects"/>.</summary>
        public TrafficReplication People
        {
            get { return local == null || loopbackHost != null ? null : local.People; }
        }

        /// <summary>The rest of what moves by itself in the host's game (train, boats, moose…). Null like <see cref="Objects"/>.</summary>
        public TrafficReplication Npcs
        {
            get { return local == null || loopbackHost != null ? null : local.Npcs; }
        }

        /// <summary>The car put together between the players: its steps in the host's order, and its numbers. Null like <see cref="Objects"/>.</summary>
        public AssemblyReplication Assembly
        {
            get { return local == null || loopbackHost != null ? null : local.Assembly; }
        }

        /// <summary>The host's CD tracks, on their way to the others. Null like <see cref="Objects"/>.</summary>
        public FileTransfer Cds
        {
            get { return local == null || loopbackHost != null ? null : local.Cds; }
        }

        /// <summary>The host's car picture (the Satsuma's "Art" paint), on its way to the others. Null like <see cref="Objects"/>.</summary>
        public FileTransfer Art
        {
            get { return local == null || loopbackHost != null ? null : local.Art; }
        }

        /// <summary>The guests' own state, kept by the host with its save (Fase 8). Null like <see cref="Objects"/>.</summary>
        public GuestReplication Guests
        {
            get { return local == null || loopbackHost != null ? null : local.Guests; }
        }

        /// <summary>The dents of the vehicles and the sound of their crashes. Null like <see cref="Objects"/>.</summary>
        public DentReplication Dents
        {
            get { return local == null || loopbackHost != null ? null : local.Dents; }
        }

        /// <summary>Checksums by section of what the games show, and their partial resync (Fase 8). Null like <see cref="Objects"/>.</summary>
        public ChecksumReplication Checks
        {
            get { return local == null || loopbackHost != null ? null : local.Checks; }
        }

        /// <summary>
        /// The text chat, or null when offline. In the loopback test, the client end: what it writes comes back from the
        /// host end like another player's would not, since a line is shown as soon as it is written.
        /// </summary>
        public ChatReplication Chat
        {
            get { return local == null ? null : local.Chat; }
        }

        /// <summary>Datagrams the simulator dropped on purpose during the current session.</summary>
        public long SimulatedDrops
        {
            get { return (local == null ? 0 : local.Simulator.Dropped) + (loopbackHost == null ? 0 : loopbackHost.Simulator.Dropped); }
        }

        /// <summary>Datagrams the simulator is holding back right now.</summary>
        public int SimulatedInFlight
        {
            get { return (local == null ? 0 : local.Simulator.InFlight) + (loopbackHost == null ? 0 : loopbackHost.Simulator.InFlight); }
        }

        /// <summary>
        /// Starts a host and a client in this process and connects them. It exercises the whole protocol, with
        /// the simulator in both directions, without needing a second machine.
        /// </summary>
        public void StartLoopbackTest(string playerName)
        {
            RequireOffline();
            var network = new LoopbackNetwork();
            loopbackHost = CreateLink(network.CreateEndpoint(LoopbackHostPeer), false);
            local = CreateLink(network.CreateEndpoint(LoopbackClientPeer), false);
            Begin(NetworkMode.LoopbackTest, LoopbackTransportName);
            loopbackHost.Session.StartHost(playerName + " (host)");
            local.Session.Connect(LoopbackHostPeer, playerName + " (client)");
        }

        /// <summary>Hosts a session on <paramref name="transport"/>, which is disposed when the session ends.</summary>
        public void StartHost(ITransport transport, string transportName, string playerName)
        {
            RequireOffline(transport);
            local = CreateLink(transport, true);
            Begin(NetworkMode.Host, transportName);
            local.Session.StartHost(playerName);
        }

        /// <summary>
        /// Joins the session hosted by <paramref name="hostPeer"/> over <paramref name="transport"/>, which is
        /// disposed when the session ends.
        /// </summary>
        public void StartClient(ITransport transport, string transportName, ulong hostPeer, string playerName)
        {
            RequireOffline(transport);
            local = CreateLink(transport, true);
            Begin(NetworkMode.Client, transportName);
            local.Session.Connect(hostPeer, playerName);
        }

        /// <summary>Ends the running session, if any, telling the other side.</summary>
        public void Stop()
        {
            if (!IsActive)
            {
                return;
            }

            try
            {
                local.Session.Close();
                if (loopbackHost != null)
                {
                    loopbackHost.Session.Close();
                }
            }
            catch (Exception exception)
            {
                // Saying goodbye is a courtesy. Whatever the transport does, the session must end here.
                log.Warning(LogCategory.Net, "Could not tell the other side that the session is closing: " + exception.Message);
            }

            End(DisconnectReason.ClosedByUser, "Closed on this side.");
        }

        /// <summary>
        /// The local player's current state: whether they have a body in the game world and, if so, where.
        /// Call it every frame before <see cref="Update"/>. Ignored when offline.
        /// </summary>
        public void SetLocalPlayer(bool inWorld, PlayerPose pose)
        {
            if (!IsActive)
            {
                return;
            }

            if (loopbackHost != null)
            {
                // One body, two ends: the host end carries it and the client end watches it come back.
                loopbackHost.Players.SetLocal(inWorld, pose);
                local.Players.SetLocal(false, default(PlayerPose));
            }
            else
            {
                local.Players.SetLocal(inWorld, pose);
            }
        }

        /// <summary>Call once per frame with a clock that never goes backwards, in seconds.</summary>
        public void Update(double now)
        {
            if (!IsActive)
            {
                return;
            }

            if (loopbackHost != null)
            {
                loopbackHost.Players.Update(now);
                loopbackHost.Chat.Update(now);
            }

            local.Players.Update(now);
            local.Chat.Update(now);
            local.Objects.Update(now);
            local.Vehicles.Update(now);
            local.Switches.Update(now);
            local.World.Update(now);
            local.Songs.Update(now);
            local.Wallet.Update(now);
            local.Spawns.Update(now);
            local.Tallies.Update(now);
            local.Items.Update(now);
            local.SavedWorld.Update(now);
            local.Panels.Update(now);
            local.Traffic.Update(now);
            local.People.Update(now);
            local.Npcs.Update(now);
            local.Assembly.Update(now);
            local.Checks.Update(now);
            local.Guests.Update(now);
            local.Dents.Update(now);
            local.Cds.Update(now);
            local.Art.Update(now);

            // Simulators first: they release the delayed datagrams that the sessions read next.
            if (loopbackHost != null)
            {
                loopbackHost.Simulator.Update(now);
            }

            local.Simulator.Update(now);
            if (loopbackHost != null)
            {
                loopbackHost.Session.Update(now);
            }

            // A session event handler may have called Stop in the middle of this update.
            if (local != null)
            {
                local.Session.Update(now);
            }

            if (endPending)
            {
                End(endReason, endText);
            }
        }

        private void RequireOffline()
        {
            if (IsActive)
            {
                throw new InvalidOperationException("A session is already running (" + Mode + " over " + TransportName + "). Stop it first.");
            }
        }

        private void RequireOffline(ITransport transport)
        {
            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }

            if (IsActive)
            {
                // The caller hands the transport over, so it must not leak when the handover is refused.
                Release(transport);
                RequireOffline();
            }
        }

        // "relaysLog": whether this end sends the lines of its log that matter to the host. Not for the two ends
        // of a session inside this process, which write to the same log.
        private Link CreateLink(ITransport transport, bool relaysLog)
        {
            var simulator = new SimulatedTransport(transport, conditions, seed++);
            var config = new SessionConfig
            {
                MaxPlayers = MaxPlayers,
                ConnectTimeout = ConnectTimeout,
                ModVersion = ModVersion,
                GameVersion = GameVersion,
            };
            var session = new NetSession(simulator, config, log) { TestTraffic = TestTraffic };
            return new Link(transport, simulator, session, new PlayerReplication(session, log), new ObjectReplication(session, log), new ObjectReplication(session, log, VehicleWire.FirstBodyMessage, VehicleWire.Label), new SwitchReplication(session, log), new WorldReplication(session, log), new LogRelay(session, log, relaysLog), new FileTransfer(session, log), new WalletReplication(session, log), new SpawnReplication(session, log), new TallyReplication(session, log), new ItemReplication(session, log), new WorldShare(session, log), new PanelReplication(session, log), new TrafficReplication(session, log), new TrafficReplication(session, log, PeopleWire.MessageState), new TrafficReplication(session, log, NpcWire.MessageState), new AssemblyReplication(session, log))
            {
                Checks = new ChecksumReplication(session, log),
                Guests = new GuestReplication(session, log),
                Dents = new DentReplication(session, log),
                Cds = new FileTransfer(session, log, FileChannel.Cds),
                Art = new FileTransfer(session, log, FileChannel.Art),
                Chat = new ChatReplication(session, log),
            };
        }

        private void Begin(NetworkMode mode, string transportName)
        {
            Mode = mode;
            TransportName = transportName;
            endPending = false;

            // Only the local end can be told to go away by someone else. It is torn down after the update that
            // reported it, not from inside the session's own packet processing.
            local.Session.Disconnected += OnDisconnected;
            if (!conditions.IsPerfect)
            {
                log.Warning(LogCategory.Net, "The network simulator is on: " + NetworkStatus.DescribeConditions(conditions) + ".");
            }
        }

        private void OnDisconnected(DisconnectReason reason, string text)
        {
            endPending = true;
            endReason = reason;
            endText = text;
        }

        private void End(DisconnectReason reason, string text)
        {
            NetworkMode endedMode = Mode;
            Link[] links = { local, loopbackHost };
            local = null;
            loopbackHost = null;
            Mode = NetworkMode.Offline;
            TransportName = string.Empty;
            endPending = false;

            foreach (Link link in links)
            {
                if (link != null)
                {
                    link.Session.Disconnected -= OnDisconnected;
                    link.Players.Detach();
                    link.Objects.Detach();
                    link.Vehicles.Detach();
                    link.Switches.Detach();
                    link.World.Detach();
                    link.Relay.Detach();
                    link.Songs.Detach();
                    link.Wallet.Detach();
                    link.Spawns.Detach();
                    link.Tallies.Detach();
                    link.Items.Detach();
                    link.SavedWorld.Detach();
                    link.Panels.Detach();
                    link.Traffic.Detach();
                    link.People.Detach();
                    link.Npcs.Detach();
                    link.Assembly.Detach();
                    link.Checks.Detach();
                    link.Guests.Detach();
                    link.Dents.Detach();
                    link.Cds.Detach();
                    link.Art.Detach();
                    link.Chat.Detach();
                    if (link.Session.StaleDatagrams > 0 || link.Session.MalformedDatagrams > 0)
                    {
                        log.Info(
                            LogCategory.Net,
                            "Ignored " + link.Session.StaleDatagrams + " datagram(s) of earlier connections and " + link.Session.MalformedDatagrams + " malformed one(s).");
                    }
                    try
                    {
                        // A goodbye still sitting in the delay queue would otherwise never be sent.
                        link.Simulator.ReleaseAll();
                    }
                    catch (Exception exception)
                    {
                        log.Warning(LogCategory.Net, "Could not send what was still queued: " + exception.Message);
                    }

                    Release(link.Transport);
                }
            }

            log.Info(LogCategory.Net, "Network is offline again (was " + endedMode + "): " + reason + ". " + text);
            Action<NetworkMode, DisconnectReason, string> handler = Stopped;
            if (handler != null)
            {
                try
                {
                    handler(endedMode, reason, text);
                }
                catch (Exception exception)
                {
                    log.Error(LogCategory.Net, "A network event handler failed: " + exception);
                }
            }
        }

        private void Release(ITransport transport)
        {
            var disposable = transport as IDisposable;
            if (disposable == null)
            {
                return;
            }

            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                log.Warning(LogCategory.Net, "Closing the transport failed: " + exception.Message);
            }
        }

        private sealed class Link
        {
            public Link(ITransport transport, SimulatedTransport simulator, NetSession session, PlayerReplication players, ObjectReplication objects, ObjectReplication vehicles, SwitchReplication switches, WorldReplication world, LogRelay relay, FileTransfer songs, WalletReplication wallet, SpawnReplication spawns, TallyReplication tallies, ItemReplication items, WorldShare savedWorld, PanelReplication panels, TrafficReplication traffic, TrafficReplication people, TrafficReplication npcs, AssemblyReplication assembly)
            {
                Npcs = npcs;
                Assembly = assembly;
                SavedWorld = savedWorld;
                Panels = panels;
                Traffic = traffic;
                People = people;
                Tallies = tallies;
                Items = items;
                World = world;
                Relay = relay;
                Songs = songs;
                Wallet = wallet;
                Spawns = spawns;
                Transport = transport;
                Simulator = simulator;
                Session = session;
                Players = players;
                Objects = objects;
                Vehicles = vehicles;
                Switches = switches;
            }

            public ITransport Transport { get; }

            public SimulatedTransport Simulator { get; }

            public NetSession Session { get; }

            public PlayerReplication Players { get; }

            public ObjectReplication Objects { get; }

            public ObjectReplication Vehicles { get; }

            public SwitchReplication Switches { get; }

            public WorldReplication World { get; }

            public LogRelay Relay { get; }

            public FileTransfer Songs { get; }

            public WalletReplication Wallet { get; }

            public SpawnReplication Spawns { get; }

            public TallyReplication Tallies { get; }

            public ItemReplication Items { get; }

            public WorldShare SavedWorld { get; }

            public PanelReplication Panels { get; }

            public TrafficReplication Traffic { get; }

            public TrafficReplication People { get; }

            public TrafficReplication Npcs { get; }

            public AssemblyReplication Assembly { get; }

            public ChecksumReplication Checks { get; set; }

            public GuestReplication Guests { get; set; }

            public DentReplication Dents { get; set; }

            public FileTransfer Cds { get; set; }

            public FileTransfer Art { get; set; }

            public ChatReplication Chat { get; set; }
        }
    }
}
