using System.Diagnostics;
using System.Globalization;
using System.Net;
using SummerSync.Core;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;

namespace SummerSync.NetProbe;

/// <summary>
/// "netprobe load": a host that is not a player and many clients that walk, all in this process, each on its own UDP
/// socket of this machine. A measurement of what the session costs with that many players: what the host sends and
/// receives, and how long its update takes.
/// </summary>
internal static class ProbeLoad
{
    private const double FrameSeconds = 1.0 / 60.0;

    public static int Run(string[] args, ILogSink sink)
    {
        int clients = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 63;
        double seconds = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 20;
        float spread = args.Length > 3 ? float.Parse(args[3], CultureInfo.InvariantCulture) : 10f;
        int port = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 27116;

        var log = new Logger { MinimumLevel = LogLevel.Warning };
        log.AddSink(sink);
        string version = UpdateCheck.TagOf(BuildInfo.InformationalVersion);
        var host = new NetworkManager(log) { ModVersion = version, GameVersion = "load/0", MaxPlayers = clients + 1 };
        var hostSocket = new Counting(new UdpTransport(IPAddress.Loopback, port));
        host.StartHost(hostSocket, "udp", "server");

        var managers = new List<NetworkManager>();
        var sockets = new List<Counting>();
        var centres = new List<PlayerPose>();
        int side = (int)Math.Ceiling(Math.Sqrt(clients));
        var clock = Stopwatch.StartNew();
        var hostTime = new Stopwatch();
        var clientTime = new Stopwatch();
        double nextJoin = 0.2;
        double nextFrame = 0;
        double measureFrom = -1;
        double nextReport = 2;
        long frames = 0;
        double worstUpdate = 0;
        Console.WriteLine("A host and " + clients + " walking client(s), " + spread.ToString("0.#", CultureInfo.InvariantCulture) + " m apart; measuring for "
            + seconds.ToString("0", CultureInfo.InvariantCulture) + " s once all are in.");

        while (true)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (managers.Count < clients && now >= nextJoin)
            {
                nextJoin = now + 0.1;
                int i = managers.Count;
                var manager = new NetworkManager(log, 1000 + i) { ModVersion = version, GameVersion = "load/0", MaxPlayers = clients + 1 };
                var socket = new Counting(new UdpTransport(IPAddress.Loopback, 0));
                manager.StartClient(socket, "udp", UdpTransport.PeerId(IPAddress.Loopback, port), "bot" + i);
                managers.Add(manager);
                sockets.Add(socket);
                centres.Add(new PlayerPose((i % side) * spread, 0f, (i / side) * spread, 0f, 0f, BotPath.StandingEyes));
            }

            if (now >= nextFrame)
            {
                nextFrame = now - nextFrame > 1 ? now + FrameSeconds : nextFrame + FrameSeconds;
                frames += measureFrom >= 0 ? 1 : 0;
                clientTime.Start();
                for (int i = 0; i < managers.Count; i++)
                {
                    if (!managers[i].IsActive)
                    {
                        continue;
                    }

                    managers[i].SetLocalPlayer(true, BotPath.Evaluate(now + i * 1.7, centres[i]));
                    managers[i].Update(now);
                    if (managers[i].IsActive && managers[i].Players != null)
                    {
                        managers[i].Players.Sample(now);
                    }
                }

                clientTime.Stop();
                host.SetLocalPlayer(false, default);
                long before = hostTime.ElapsedTicks;
                hostTime.Start();
                host.Update(now);
                hostTime.Stop();
                if (measureFrom >= 0)
                {
                    worstUpdate = Math.Max(worstUpdate, (hostTime.ElapsedTicks - before) * 1000.0 / Stopwatch.Frequency);
                }
            }
            else
            {
                Thread.Sleep(1);
            }

            if (!host.IsActive)
            {
                Console.Error.WriteLine("The host stopped.");
                return 1;
            }

            int inSession = host.Players == null ? 0 : host.Players.Remotes.Count;
            if (measureFrom < 0 && managers.Count == clients && inSession == clients && Seen(managers) >= clients - 1)
            {
                // All in and all seen by all: three more seconds, then count from zero.
                measureFrom = now + 3;
                Console.WriteLine("All " + clients + " are in at " + now.ToString("0.0", CultureInfo.InvariantCulture) + " s.");
            }

            if (measureFrom >= 0 && now >= measureFrom && !hostSocket.Counts)
            {
                hostSocket.Reset();
                foreach (Counting socket in sockets)
                {
                    socket.Reset();
                }

                hostTime.Reset();
                clientTime.Reset();
                frames = 0;
                worstUpdate = 0;
            }

            if (measureFrom < 0 && now >= nextReport)
            {
                nextReport = now + 2;
                Console.WriteLine("  " + now.ToString("0", CultureInfo.InvariantCulture) + " s: " + inSession + " in the host's session, each client sees at least " + Seen(managers) + " other(s).");
                if (now > 60 + clients * 0.2)
                {
                    Console.Error.WriteLine("They did not all get in.");
                    return 1;
                }
            }

            if (measureFrom >= 0 && hostSocket.Counts && now >= measureFrom + seconds)
            {
                double window = now - measureFrom;
                double sentRate = hostSocket.BytesSent / window;
                double receivedRate = hostSocket.BytesReceived / window;
                long clientSent = 0;
                long clientReceived = 0;
                foreach (Counting socket in sockets)
                {
                    clientSent += socket.BytesSent;
                    clientReceived += socket.BytesReceived;
                }

                // 28 bytes of IPv4 and UDP headers a datagram, which the socket does not count.
                double sentWire = (hostSocket.BytesSent + 28.0 * hostSocket.DatagramsSent) / window;
                double receivedWire = (hostSocket.BytesReceived + 28.0 * hostSocket.DatagramsReceived) / window;
                Console.WriteLine("Measured " + window.ToString("0.0", CultureInfo.InvariantCulture) + " s with " + clients + " client(s), each seeing at least " + Seen(managers) + " other(s):");
                Console.WriteLine("  host sends     " + Rate(sentRate) + " of payload, " + Rate(sentWire) + " on the wire, " + (hostSocket.DatagramsSent / window).ToString("0", CultureInfo.InvariantCulture)
                    + " datagrams/s, " + (hostSocket.BytesSent / (double)Math.Max(1, hostSocket.DatagramsSent)).ToString("0", CultureInfo.InvariantCulture) + " bytes each");
                Console.WriteLine("  host receives  " + Rate(receivedRate) + " of payload, " + Rate(receivedWire) + " on the wire, " + (hostSocket.DatagramsReceived / window).ToString("0", CultureInfo.InvariantCulture) + " datagrams/s");
                Console.WriteLine("  per client     down " + Rate(sentWire / clients) + ", up " + Rate(receivedWire / clients) + " (on the wire)");
                Console.WriteLine("  per player seen by a client: " + (sentRate / clients / Math.Max(1, clients - 1)).ToString("0", CultureInfo.InvariantCulture) + " bytes/s of payload");
                Console.WriteLine("  host update    " + (hostTime.Elapsed.TotalMilliseconds / Math.Max(1, frames)).ToString("0.000", CultureInfo.InvariantCulture) + " ms a frame on average, "
                    + worstUpdate.ToString("0.00", CultureInfo.InvariantCulture) + " ms the worst, " + (hostTime.Elapsed.TotalSeconds / window * 100).ToString("0.0", CultureInfo.InvariantCulture) + " % of one core at "
                    + (frames / window).ToString("0", CultureInfo.InvariantCulture) + " frames/s");
                Console.WriteLine("  the " + clients + " clients together: " + (clientTime.Elapsed.TotalSeconds / window * 100).ToString("0.0", CultureInfo.InvariantCulture) + " % of one core");
                foreach (NetworkManager manager in managers)
                {
                    manager.Stop();
                }

                host.Stop();
                return 0;
            }
        }
    }

    /// <summary>
    /// "netprobe load-join": that many walking clients, in this process, that join a host that is somewhere else (a real
    /// game), in rows around a point of its map.
    /// </summary>
    public static int Join(string[] args, ILogSink sink, string gameVersion)
    {
        int clients = int.Parse(args[1], CultureInfo.InvariantCulture);
        double seconds = double.Parse(args[2], CultureInfo.InvariantCulture);
        float x = float.Parse(args[3], CultureInfo.InvariantCulture);
        float y = float.Parse(args[4], CultureInfo.InvariantCulture);
        float z = float.Parse(args[5], CultureInfo.InvariantCulture);
        float spread = args.Length > 7 ? float.Parse(args[7], CultureInfo.InvariantCulture) : 3f;
        int port = args.Length > 8 ? int.Parse(args[8], CultureInfo.InvariantCulture) : 27016;

        var log = new Logger { MinimumLevel = LogLevel.Warning };
        log.AddSink(sink);
        string version = UpdateCheck.TagOf(BuildInfo.InformationalVersion);
        var managers = new List<NetworkManager>();
        var centres = new List<PlayerPose>();
        var stores = new List<ProbeSongs>();
        int side = (int)Math.Ceiling(Math.Sqrt(clients));
        var clock = Stopwatch.StartNew();
        double nextJoin = 0;
        double nextFrame = 0;
        double nextReport = 2;
        Console.WriteLine(clients + " walking client(s) join 127.0.0.1:" + port + " as game " + gameVersion + ", around (" + x + ", " + y + ", " + z + "), for " + seconds + " s.");
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (managers.Count < clients && now >= nextJoin)
            {
                nextJoin = now + 0.5;
                int i = managers.Count;
                var manager = new NetworkManager(log, 2000 + i) { ModVersion = version, GameVersion = gameVersion, MaxPlayers = clients + 1 };
                manager.StartClient(new UdpTransport(IPAddress.Loopback, 0), "udp", UdpTransport.PeerId(IPAddress.Loopback, port), "bot" + i);
                var songs = new ProbeSongs();
                var world = new ProbeSongs();
                manager.Songs?.SetStore(songs);
                manager.SavedWorld?.SetStore(world);
                stores.Add(songs);
                stores.Add(world);
                managers.Add(manager);
                centres.Add(new PlayerPose(x + (i % side - (side - 1) * 0.5f) * spread, y, z + (i / side) * spread, 0f, 0f, BotPath.StandingEyes));
            }

            if (now >= nextFrame)
            {
                nextFrame = now - nextFrame > 1 ? now + FrameSeconds : nextFrame + FrameSeconds;
                for (int i = 0; i < managers.Count; i++)
                {
                    if (!managers[i].IsActive)
                    {
                        continue;
                    }

                    managers[i].SetLocalPlayer(true, BotPath.Evaluate(now + i * 1.7, centres[i]));
                    managers[i].Update(now);
                    if (managers[i].IsActive && managers[i].Players != null)
                    {
                        managers[i].Players.Sample(now);
                    }
                }
            }
            else
            {
                Thread.Sleep(1);
            }

            if (now >= nextReport)
            {
                nextReport = now + 5;
                int active = 0;
                foreach (NetworkManager manager in managers)
                {
                    active += manager.IsActive ? 1 : 0;
                }

                Console.WriteLine("  " + now.ToString("0", CultureInfo.InvariantCulture) + " s: " + active + " of " + managers.Count + " started are in a session; each sees at least " + Seen(managers) + " other(s).");
            }
        }

        foreach (NetworkManager manager in managers)
        {
            manager.Stop();
        }

        return 0;
    }

    // The fewest other players in the game world that any client sees.
    private static int Seen(List<NetworkManager> managers)
    {
        int fewest = int.MaxValue;
        foreach (NetworkManager manager in managers)
        {
            int seen = 0;
            if (manager.IsActive && manager.Players != null)
            {
                foreach (RemotePlayer remote in manager.Players.Remotes)
                {
                    seen += remote.Visible ? 1 : 0;
                }
            }

            fewest = Math.Min(fewest, seen);
        }

        return managers.Count == 0 ? 0 : fewest;
    }

    private static string Rate(double bytesPerSecond)
    {
        double kilobits = bytesPerSecond * 8 / 1000;
        return kilobits >= 1000 ? (kilobits / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " Mbit/s" : kilobits.ToString("0.0", CultureInfo.InvariantCulture) + " kbit/s";
    }

    private sealed class Counting : ITransport
    {
        private readonly ITransport inner;

        public Counting(ITransport inner)
        {
            this.inner = inner;
        }

        public bool Counts { get; private set; }

        public long BytesSent { get; private set; }

        public long BytesReceived { get; private set; }

        public long DatagramsSent { get; private set; }

        public long DatagramsReceived { get; private set; }

        public int MaxDatagramSize => inner.MaxDatagramSize;

        public void Reset()
        {
            Counts = true;
            BytesSent = BytesReceived = DatagramsSent = DatagramsReceived = 0;
        }

        public void Send(ulong peer, byte[] data, int count)
        {
            BytesSent += count;
            DatagramsSent++;
            inner.Send(peer, data, count);
        }

        public bool Receive(byte[] buffer, out int count, out ulong peer)
        {
            bool any = inner.Receive(buffer, out count, out peer);
            if (any)
            {
                BytesReceived += count;
                DatagramsReceived++;
            }

            return any;
        }
    }
}
