using System.Diagnostics;
using System.Net;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

/// <summary>These use real sockets on the loopback interface, so they run in real time.</summary>
public class UdpTransportTests
{
    private static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return condition();
    }

    [Fact]
    public void Peer_ids_encode_address_and_port()
    {
        ulong id = UdpTransport.PeerId(IPAddress.Parse("192.168.1.20"), 27016);

        IPEndPoint endpoint = UdpTransport.EndPointOf(id);
        Assert.Equal("192.168.1.20", endpoint.Address.ToString());
        Assert.Equal(27016, endpoint.Port);
        Assert.NotEqual(id, UdpTransport.PeerId(IPAddress.Parse("192.168.1.20"), 27017));
        Assert.Throws<ArgumentException>(() => UdpTransport.PeerId(IPAddress.IPv6Loopback, 1));
    }

    [Fact]
    public void Two_sockets_exchange_datagrams_on_loopback()
    {
        using var a = new UdpTransport(IPAddress.Loopback, 0);
        using var b = new UdpTransport(IPAddress.Loopback, 0);
        var buffer = new byte[a.MaxDatagramSize];
        int count = 0;
        ulong from = 0;

        a.Send(UdpTransport.PeerId(IPAddress.Loopback, b.LocalPort), [1, 2, 3], 3);

        Assert.True(WaitFor(() => b.Receive(buffer, out count, out from)));
        Assert.Equal(3, count);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.Take(3));
        Assert.Equal(UdpTransport.PeerId(IPAddress.Loopback, a.LocalPort), from);
        Assert.False(b.Receive(buffer, out _, out _));
    }

    [Fact]
    public void Sending_to_a_closed_port_does_not_break_the_socket()
    {
        using var a = new UdpTransport(IPAddress.Loopback, 0);
        using var b = new UdpTransport(IPAddress.Loopback, 0);
        var buffer = new byte[a.MaxDatagramSize];
        int closedPort;
        using (var temporary = new UdpTransport(IPAddress.Loopback, 0))
        {
            closedPort = temporary.LocalPort;
        }

        // Windows reports this as a "connection reset" on a later receive.
        a.Send(UdpTransport.PeerId(IPAddress.Loopback, closedPort), [9], 1);
        Thread.Sleep(50);
        b.Send(UdpTransport.PeerId(IPAddress.Loopback, a.LocalPort), [7], 1);

        int count = 0;
        Assert.True(WaitFor(() => a.Receive(buffer, out count, out _)));
        Assert.Equal((1, (byte)7), (count, buffer[0]));
    }

    [Fact]
    public void A_whole_session_runs_over_real_sockets()
    {
        using var hostSocket = new UdpTransport(IPAddress.Loopback, 0);
        using var clientSocket = new UdpTransport(IPAddress.Loopback, 0);
        SessionConfig Config() => new() { ModVersion = "0.1.0", GameVersion = "test" };
        var host = new NetSession(hostSocket, Config(), new Logger());
        var client = new NetSession(clientSocket, Config(), new Logger());
        var received = new List<byte>();
        host.MessageReceived += (_, data, _) => received.Add(data[1]);
        var watch = Stopwatch.StartNew();
        void Step()
        {
            host.Update(watch.Elapsed.TotalSeconds);
            client.Update(watch.Elapsed.TotalSeconds);
        }

        host.StartHost("Host");
        client.Connect(UdpTransport.PeerId(IPAddress.Loopback, hostSocket.LocalPort), "Client");

        Assert.True(WaitFor(() => { Step(); return client.State == SessionState.Connected && client.Players.Count == 2; }));
        client.Send(Protocol.HostPlayerId, [Protocol.FirstUserMessage, 42], 0, 2, reliable: true);
        Assert.True(WaitFor(() => { Step(); return received.Count == 1; }));

        Assert.Equal(42, received[0]);
        Assert.Equal(2, host.Players.Count);
        Assert.True(WaitFor(() => { Step(); return client.IsClockSynced; }));
        Assert.InRange(client.SessionTime - host.SessionTime, -0.05, 0.05);
    }
}
