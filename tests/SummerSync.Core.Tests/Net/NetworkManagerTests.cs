using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

public class NetworkManagerTests
{
    private const ulong HostPeer = 10;
    private const ulong ClientPeer = 20;

    private static NetworkManager Create(string modVersion = "0.1.0") =>
        new(new Logger(), seed: 5) { ModVersion = modVersion, GameVersion = "5.0.0f4/abc" };

    /// <summary>Runs the managers together at 60 frames per second and returns the time reached.</summary>
    private static double Run(double from, double seconds, params NetworkManager[] managers)
    {
        const double step = 1.0 / 60.0;
        double now = from;
        while (now < from + seconds)
        {
            now += step;
            foreach (NetworkManager manager in managers)
            {
                manager.Update(now);
            }
        }

        return now;
    }

    private static string Describe(NetworkManager manager)
    {
        var text = new StringBuilder();
        NetworkStatus.Describe(manager, text);
        return text.ToString();
    }

    [Fact]
    public void Starts_offline()
    {
        NetworkManager manager = Create();

        manager.Update(1);
        manager.Stop();

        Assert.Equal(NetworkMode.Offline, manager.Mode);
        Assert.False(manager.IsActive);
        Assert.Null(manager.Session);
        Assert.Equal("Net: offline", Describe(manager));
    }

    [Fact]
    public void Loopback_test_connects_a_host_and_a_client_in_one_process()
    {
        NetworkManager manager = Create();

        manager.StartLoopbackTest("Julian");
        Run(0, 3, manager);

        Assert.Equal(NetworkMode.LoopbackTest, manager.Mode);
        Assert.Equal(SessionState.Connected, manager.Session!.State);
        Assert.Equal(SessionRole.Client, manager.Session.Role);
        Assert.Equal(new[] { "Julian (host)", "Julian (client)" }, manager.LoopbackHostSession!.Players.Select(p => p.Name));
        Assert.True(manager.Session.IsClockSynced);
        Assert.True(manager.Session.Traffic.ReliableReceived > 60);
        Assert.Equal(0, manager.Session.Traffic.ReliableErrors + manager.LoopbackHostSession.Traffic.ReliableErrors);

        string status = Describe(manager);
        Assert.StartsWith("Net: loopback self-test | player #1 of 2 | tick ", status);
        Assert.Contains("\n host sees #1 Julian (client): rtt ", status);
        Assert.Contains("\n client sees #0 Julian (host): rtt ", status);
        Assert.Contains("\n Test traffic: reliable ", status);
        Assert.DoesNotContain("Sim", status);
    }

    // The acceptance criterion of the phase, through the same code path the game uses.
    [Fact]
    public void Loopback_test_survives_200_ms_and_5_percent_loss()
    {
        NetworkManager manager = Create();
        manager.Conditions.LatencyMs = 200;
        manager.Conditions.JitterMs = 30;
        manager.Conditions.LossPercent = 5;
        var stops = new List<string>();
        manager.Stopped += (mode, reason, _) => stops.Add(mode + ":" + reason);

        manager.StartLoopbackTest("Julian");
        Run(0, 60, manager);

        TrafficTestStats client = manager.Session!.Traffic;
        TrafficTestStats host = manager.LoopbackHostSession!.Traffic;
        Assert.Empty(stops);
        Assert.Equal(0, client.ReliableErrors + host.ReliableErrors);
        Assert.InRange(client.ReliableReceived, host.ReliableSent - 60, host.ReliableSent);
        Assert.InRange(host.ReliableReceived, client.ReliableSent - 60, client.ReliableSent);
        Assert.InRange((double)client.UnreliableReceived / host.UnreliableSent, 0.90, 0.99);
        Assert.InRange(manager.Session.GetPeerStats()[0].RoundTripMs, 380, 480);
        Assert.InRange(manager.Session.GetPeerStats()[0].LossPercent, 1.5, 10);
        Assert.True(manager.SimulatedDrops > 0);
        Assert.Contains("\nSim (what this side sends): 200 ms +-30 ms, loss 5 % | ", Describe(manager));
    }

    [Fact]
    public void Stopping_goes_offline_and_reports_it_once()
    {
        NetworkManager manager = Create();
        var stops = new List<string>();
        manager.Stopped += (mode, reason, _) => stops.Add(mode + ":" + reason);
        manager.StartLoopbackTest("Julian");
        double now = Run(0, 1, manager);

        manager.Stop();
        manager.Stop();
        Run(now, 1, manager);

        Assert.Equal(NetworkMode.Offline, manager.Mode);
        Assert.Null(manager.Session);
        Assert.Null(manager.LoopbackHostSession);
        Assert.Equal(new[] { "LoopbackTest:ClosedByUser" }, stops);
    }

    [Fact]
    public void A_new_session_can_start_after_the_previous_one_ended()
    {
        NetworkManager manager = Create();
        manager.StartLoopbackTest("Julian");
        double now = Run(0, 1, manager);
        manager.Stop();

        manager.StartLoopbackTest("Julian");
        Run(now, 1, manager);

        Assert.Equal(SessionState.Connected, manager.Session!.State);
        Assert.Equal(0, manager.Session.Traffic.ReliableErrors);
    }

    [Fact]
    public void Two_managers_host_and_join_and_show_each_other()
    {
        var network = new LoopbackNetwork();
        NetworkManager host = Create();
        NetworkManager client = Create();

        host.StartHost(network.CreateEndpoint(HostPeer), "udp", "Julian");
        client.StartClient(network.CreateEndpoint(ClientPeer), "udp", HostPeer, "Amigo");
        Assert.Equal("Net: client over udp | connecting...", Describe(client));
        Run(0, 3, host, client);

        Assert.Equal((NetworkMode.Host, NetworkMode.Client), (host.Mode, client.Mode));
        Assert.Equal("udp", client.TransportName);
        string hostStatus = Describe(host);
        string clientStatus = Describe(client);
        Assert.StartsWith("Net: host over udp | 2/4 players | tick ", hostStatus);
        Assert.Contains("\n #1 Amigo: rtt ", hostStatus);
        Assert.Contains("\n Players: #0 Julian (host, you), #1 Amigo", hostStatus);
        Assert.StartsWith("Net: client over udp | player #1 of 2 | tick ", clientStatus);
        Assert.Contains(" | clock ", clientStatus);
        Assert.Contains("\n #0 Julian: rtt ", clientStatus);
        Assert.Contains("\n Players: #0 Julian (host), #1 Amigo (you)", clientStatus);
    }

    [Fact]
    public void The_client_goes_offline_when_the_host_stops_even_with_simulated_latency()
    {
        var network = new LoopbackNetwork();
        NetworkManager host = Create();
        NetworkManager client = Create();
        host.Conditions.LatencyMs = 300;
        var hostTransport = new DisposableTransport(network.CreateEndpoint(HostPeer));
        var clientTransport = new DisposableTransport(network.CreateEndpoint(ClientPeer));
        var stops = new List<string>();
        client.Stopped += (mode, reason, _) => stops.Add(mode + ":" + reason);
        host.StartHost(hostTransport, "udp", "Julian");
        client.StartClient(clientTransport, "udp", HostPeer, "Amigo");
        double now = Run(0, 3, host, client);
        Assert.Equal(SessionState.Connected, client.Session!.State);

        // The goodbye must not die in the host's delay queue: the client would only notice by timing out.
        host.Stop();
        Run(now, 0.5, client);

        Assert.Equal(new[] { "Client:HostClosed" }, stops);
        Assert.Equal(NetworkMode.Offline, client.Mode);
        Assert.True(hostTransport.Disposed);
        Assert.True(clientTransport.Disposed);
    }

    [Fact]
    public void A_rejected_client_ends_up_offline_with_the_reason()
    {
        var network = new LoopbackNetwork();
        NetworkManager host = Create("0.2.0");
        NetworkManager client = Create("0.1.0");
        var stops = new List<string>();
        client.Stopped += (mode, reason, text) => stops.Add(mode + ":" + reason + ":" + text);
        host.StartHost(network.CreateEndpoint(HostPeer), "udp", "Julian");
        client.StartClient(network.CreateEndpoint(ClientPeer), "udp", HostPeer, "Amigo");

        Run(0, 1, host, client);

        Assert.Equal(new[] { "Client:ModVersionMismatch:Host runs SummerSync 0.2.0, you run 0.1.0. Every player needs the same build of the mod." }, stops);
        Assert.False(client.IsActive);
        Assert.True(host.IsActive);
    }

    [Fact]
    public void A_client_with_nobody_to_talk_to_gives_up()
    {
        NetworkManager client = Create();
        var stops = new List<string>();
        client.Stopped += (mode, reason, _) => stops.Add(mode + ":" + reason);

        client.StartClient(new LoopbackNetwork().CreateEndpoint(ClientPeer), "udp", HostPeer, "Amigo");
        double now = Run(0, 19, client);
        Assert.Empty(stops);
        Run(now, 2, client);

        Assert.Equal(new[] { "Client:ConnectFailed" }, stops);
    }

    [Fact]
    public void Starting_twice_is_refused_and_does_not_leak_the_second_transport()
    {
        NetworkManager manager = Create();
        manager.StartLoopbackTest("Julian");
        var transport = new DisposableTransport(new LoopbackNetwork().CreateEndpoint(HostPeer));

        Assert.Throws<InvalidOperationException>(() => manager.StartHost(transport, "udp", "Julian"));
        Assert.Throws<InvalidOperationException>(() => manager.StartLoopbackTest("Julian"));

        Assert.True(transport.Disposed);
        Assert.Equal(NetworkMode.LoopbackTest, manager.Mode);
    }

    [Fact]
    public void Test_traffic_can_be_switched_off()
    {
        NetworkManager manager = Create();
        manager.TestTraffic = false;

        manager.StartLoopbackTest("Julian");
        Run(0, 2, manager);

        Assert.Equal(0, manager.Session!.Traffic.ReliableSent);
        Assert.DoesNotContain("Test traffic", Describe(manager));
    }

    [Fact]
    public void A_failing_stop_handler_does_not_break_the_manager()
    {
        NetworkManager manager = Create();
        manager.Stopped += (_, _, _) => throw new InvalidOperationException("handler bug");
        manager.StartLoopbackTest("Julian");

        manager.Stop();

        Assert.False(manager.IsActive);
    }

    // Steam is shut down under the mod when the game returns to the main menu; sends start throwing.
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Stopping_works_even_when_the_transport_has_died(int simulatedLatencyMs)
    {
        NetworkManager manager = Create();
        manager.Conditions.LatencyMs = simulatedLatencyMs;
        var transport = new DisposableTransport(new LoopbackNetwork().CreateEndpoint(ClientPeer));
        var stops = new List<string>();
        manager.Stopped += (mode, reason, _) => stops.Add(mode + ":" + reason);
        manager.StartClient(transport, "steam", HostPeer, "Amigo");
        manager.Update(0.01);
        transport.Broken = true;

        manager.Stop();

        Assert.False(manager.IsActive);
        Assert.True(transport.Disposed);
        Assert.Equal(new[] { "Client:ClosedByUser" }, stops);
        manager.StartLoopbackTest("Julian");
        Assert.Equal(NetworkMode.LoopbackTest, manager.Mode);
    }

    [Fact]
    public void Offline_status_shows_a_simulator_that_was_left_on()
    {
        NetworkManager manager = Create();
        manager.Conditions.LatencyMs = 150;
        manager.Conditions.LossPercent = 2.5f;
        manager.Conditions.DuplicatePercent = 1;

        Assert.Equal("Net: offline\nSim (what this side sends): 150 ms, loss 2.5 %, duplicates 1 %", Describe(manager));
        Assert.Equal("off", NetworkStatus.DescribeConditions(new NetworkConditions()));
    }

    private sealed class DisposableTransport(ITransport inner) : ITransport, IDisposable
    {
        public bool Disposed { get; private set; }

        /// <summary>When set, sending throws, as a transport does once what is underneath it has gone away.</summary>
        public bool Broken { get; set; }

        public int MaxDatagramSize => inner.MaxDatagramSize;

        public void Send(ulong peer, byte[] data, int count)
        {
            if (Broken)
            {
                throw new InvalidOperationException("The transport is gone.");
            }

            inner.Send(peer, data, count);
        }

        public bool Receive(byte[] buffer, out int count, out ulong peer) => inner.Receive(buffer, out count, out peer);

        public void Dispose() => Disposed = true;
    }
}
