using System.Text;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.Tests.Net;

/// <summary>Two connections talking through a loopback network, with optional bad conditions and a manual clock.</summary>
internal sealed class ConnectionPair
{
    private const ulong IdA = 1;
    private const ulong IdB = 2;

    private readonly SimulatedTransport transportA;
    private readonly SimulatedTransport transportB;
    private readonly byte[] buffer = new byte[2048];
    private readonly PacketReader reader = new();

    public ConnectionPair(NetworkConditions? conditions = null, int seed = 1234, ConnectionConfig? config = null)
    {
        var network = new LoopbackNetwork();
        Conditions = conditions ?? new NetworkConditions();
        transportA = new SimulatedTransport(network.CreateEndpoint(IdA), Conditions, seed);
        transportB = new SimulatedTransport(network.CreateEndpoint(IdB), Conditions, seed + 1);
        A = new Connection(config ?? new ConnectionConfig());
        B = new Connection(config ?? new ConnectionConfig());
    }

    public NetworkConditions Conditions { get; }

    public Connection A { get; }

    public Connection B { get; }

    public List<ReceivedMessage> ReceivedByA { get; } = new();

    public List<ReceivedMessage> ReceivedByB { get; } = new();

    public double Now { get; private set; }

    /// <summary>While this says so, B does not run at all: a game on a loading screen. Only <see cref="RunAsTwoGames"/> asks.</summary>
    public Func<double, bool>? BIsFrozen { get; set; }

    /// <summary>Runs both ends at 60 steps per second for the given simulated time.</summary>
    public void Run(double seconds, Action<double>? eachStep = null)
    {
        const double step = 1.0 / 60.0;
        double end = Now + seconds;
        while (Now < end)
        {
            Now += step;
            eachStep?.Invoke(Now);
            transportA.Update(Now);
            transportB.Update(Now);
            Pump(transportA, A, ReceivedByA);
            Pump(transportB, B, ReceivedByB);
            A.Flush(Now, (data, count) => transportA.Send(IdB, data, count));
            B.Flush(Now, (data, count) => transportB.Send(IdA, data, count));
        }
    }

    /// <summary>
    /// Runs each end like a game: it looks at the network on every frame of its own, and sends on the frames
    /// where the send schedule says so, as <c>NetSession</c> does. Frame times wander by a fifth around the
    /// average. An answer therefore waits for a frame of whoever owes it, and then for one of whoever reads it.
    /// </summary>
    public void RunAsTwoGames(double seconds, double framesPerSecondA, double framesPerSecondB, Action<double>? beforeSendOfA = null, Action<double>? beforeSendOfB = null, double sendRate = 30)
    {
        const double step = 0.0005;
        var frames = new Random(77);
        double end = Now + seconds;
        double nextFrameA = Now + (1.0 / framesPerSecondA);
        double nextFrameB = Now + (0.4 / framesPerSecondB);
        double nextSendA = double.NegativeInfinity;
        double nextSendB = double.NegativeInfinity;
        while (Now < end)
        {
            Now += step;
            if (Now >= nextFrameA)
            {
                nextFrameA = Now + ((0.8 + (0.4 * frames.NextDouble())) / framesPerSecondA);
                transportA.Update(Now);
                Pump(transportA, A, ReceivedByA);
                if (SendIsDue(ref nextSendA, sendRate))
                {
                    beforeSendOfA?.Invoke(Now);
                    A.Flush(Now, (data, count) => transportA.Send(IdB, data, count));
                }
            }

            if (Now >= nextFrameB)
            {
                nextFrameB = Now + ((0.8 + (0.4 * frames.NextDouble())) / framesPerSecondB);
                if (BIsFrozen != null && BIsFrozen(Now))
                {
                    continue;
                }

                transportB.Update(Now);
                Pump(transportB, B, ReceivedByB);
                if (SendIsDue(ref nextSendB, sendRate))
                {
                    beforeSendOfB?.Invoke(Now);
                    B.Flush(Now, (data, count) => transportB.Send(IdA, data, count));
                }
            }
        }
    }

    private bool SendIsDue(ref double nextSend, double sendRate)
    {
        if (Now < nextSend)
        {
            return false;
        }

        double interval = 1.0 / sendRate;
        nextSend = Now - nextSend > interval ? Now + interval : nextSend + interval;
        return true;
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static string Text(ReceivedMessage message) => Encoding.UTF8.GetString(message.Data);

    private void Pump(ITransport transport, Connection connection, List<ReceivedMessage> output)
    {
        while (transport.Receive(buffer, out int count, out _))
        {
            reader.SetBuffer(buffer, 1, count - 1);
            Assert.Equal(Connection.DataKind, buffer[0]);
            connection.Receive(Now, reader, count, output);
        }
    }
}

public class ConnectionTests
{
    private static void SendReliable(Connection connection, string text)
    {
        byte[] data = ConnectionPair.Bytes(text);
        connection.QueueReliable(data, 0, data.Length);
    }

    private static void SendUnreliable(Connection connection, string text)
    {
        byte[] data = ConnectionPair.Bytes(text);
        connection.QueueUnreliable(data, 0, data.Length);
    }

    [Fact]
    public void Delivers_reliable_and_unreliable_messages_over_a_clean_link()
    {
        var pair = new ConnectionPair();
        SendReliable(pair.A, "one");
        SendUnreliable(pair.A, "two");
        SendReliable(pair.B, "three");

        pair.Run(0.2);

        Assert.Equal(new[] { "one", "two" }, pair.ReceivedByB.Select(ConnectionPair.Text));
        Assert.True(pair.ReceivedByB[0].Reliable);
        Assert.False(pair.ReceivedByB[1].Reliable);
        Assert.Equal("three", ConnectionPair.Text(Assert.Single(pair.ReceivedByA)));
        Assert.Equal(0, pair.A.ReliableQueueLength);
        Assert.Equal(0, pair.B.ReliableQueueLength);
    }

    [Theory]
    [InlineData(5f, 0f, 0f, 1)]
    [InlineData(30f, 200f, 60f, 2)]
    [InlineData(50f, 80f, 80f, 3)]
    public void Reliable_messages_arrive_exactly_once_and_in_order_whatever_the_network_does(float loss, float latency, float jitter, int seed)
    {
        var conditions = new NetworkConditions { LossPercent = loss, LatencyMs = latency, JitterMs = jitter, DuplicatePercent = 10 };
        var pair = new ConnectionPair(conditions, seed);
        const int count = 400;
        int sent = 0;

        pair.Run(20, _ =>
        {
            // A steady stream rather than one burst, in both directions.
            for (int i = 0; i < 2 && sent < count; i++, sent++)
            {
                SendReliable(pair.A, "a" + sent);
                SendReliable(pair.B, "b" + sent);
            }
        });
        pair.Run(40);

        Assert.Equal(Enumerable.Range(0, count).Select(i => "a" + i), pair.ReceivedByB.Select(ConnectionPair.Text));
        Assert.Equal(Enumerable.Range(0, count).Select(i => "b" + i), pair.ReceivedByA.Select(ConnectionPair.Text));
        Assert.Equal(0, pair.A.ReliableQueueLength);
        Assert.True(pair.A.ReliableResends > 0, "with loss, something must have been resent");
    }

    [Fact]
    public void Unreliable_messages_may_be_lost_but_are_never_duplicated()
    {
        var conditions = new NetworkConditions { LossPercent = 20, LatencyMs = 50, JitterMs = 40, DuplicatePercent = 50 };
        var pair = new ConnectionPair(conditions);
        int sent = 0;

        pair.Run(10, _ => SendUnreliable(pair.A, "u" + sent++));
        pair.Run(2);

        List<string> received = pair.ReceivedByB.Select(ConnectionPair.Text).ToList();
        Assert.Equal(received.Count, received.Distinct().Count());
        Assert.InRange(received.Count, sent / 2, sent - 1);
    }

    [Fact]
    public void Measures_round_trip_time()
    {
        var pair = new ConnectionPair(new NetworkConditions { LatencyMs = 100 });

        pair.Run(5, _ => SendUnreliable(pair.A, "x"));

        // 100 ms each way.
        Assert.True(pair.A.HasRoundTripTime);
        Assert.InRange(pair.A.RoundTripTime, 0.190, 0.240);
        Assert.InRange(pair.B.RoundTripTime, 0.190, 0.240);
    }

    [Fact]
    public void Round_trip_time_does_not_include_the_time_the_peer_sat_on_the_packet()
    {
        // B answers only with keep-alives, up to 100 ms after receiving. That wait must not count as latency.
        // 20 ms each way is 40 ms; the test harness steps time in 16.7 ms increments, which rounds each leg up.
        var pair = new ConnectionPair(new NetworkConditions { LatencyMs = 20 });

        pair.Run(5);

        Assert.InRange(pair.A.RoundTripTime, 0.035, 0.080);
    }

    [Fact]
    public void Estimates_packet_loss()
    {
        var pair = new ConnectionPair(new NetworkConditions { LossPercent = 20, LatencyMs = 30 });

        pair.Run(30, _ => SendUnreliable(pair.A, "x"));

        Assert.InRange(pair.A.PacketLoss, 0.08, 0.35);
    }

    [Fact]
    public void A_clean_link_reports_no_loss()
    {
        var pair = new ConnectionPair(new NetworkConditions { LatencyMs = 30 });

        pair.Run(10, _ => SendUnreliable(pair.A, "x"));

        Assert.InRange(pair.A.PacketLoss, 0, 0.001);
        Assert.Equal(0, pair.A.ReliableResends);
    }

    [Theory]
    [InlineData(29, 30, 17)]
    [InlineData(30, 60, 17)]
    [InlineData(20, 30, 50)]
    [InlineData(60, 25, 5)]
    public void Two_games_on_a_clean_link_do_not_resend(double framesPerSecondA, double framesPerSecondB, int latencyMs)
    {
        // Seen in the game on 2026-10-13: 35 ms round trip, no loss, and a third of the reliable messages sent
        // twice. The ack waits for the peer's next frame and then for ours; neither wait is network time.
        var pair = new ConnectionPair(new NetworkConditions { LatencyMs = latencyMs });
        int sentByA = 0;
        int sentByB = 0;

        pair.RunAsTwoGames(
            60,
            framesPerSecondA,
            framesPerSecondB,
            _ =>
            {
                SendReliable(pair.A, "a");
                sentByA++;
            },
            _ =>
            {
                SendReliable(pair.B, "b");
                sentByB++;
            });

        Assert.True(pair.A.ReliableResends <= sentByA / 200, "A resent " + pair.A.ReliableResends + " of " + sentByA);
        Assert.True(pair.B.ReliableResends <= sentByB / 200, "B resent " + pair.B.ReliableResends + " of " + sentByB);
    }

    [Fact]
    public void A_peer_on_a_loading_screen_is_not_sent_its_whole_queue_over_and_over()
    {
        // Seen in the game on 2026-10-13: 2256 resends for 6729 messages on a link that lost nothing. The host
        // had spent 6.5 s on a loading screen, and what was queued for it went out again every 70 ms meanwhile.
        var pair = new ConnectionPair(new NetworkConditions { LatencyMs = 5 });
        pair.BIsFrozen = now => now >= 10 && now < 17;
        int sent = 0;

        pair.RunAsTwoGames(
            30,
            29,
            60,
            now =>
            {
                // As NetSession does with its test traffic: nothing new for a peer that has been quiet for 2 s.
                if (now < 12 || now >= 17.5)
                {
                    SendReliable(pair.A, sent.ToString());
                    sent++;
                }
            });

        Assert.Equal(Enumerable.Range(0, pair.ReceivedByB.Count).Select(i => i.ToString()), pair.ReceivedByB.Select(ConnectionPair.Text));
        Assert.InRange(pair.ReceivedByB.Count, sent - 3, sent);
        Assert.True(pair.A.ReliableResends > 0);

        // About 58 messages wait through the freeze. At most 4 tries each on the way to one per second, then 7.
        Assert.True(pair.A.ReliableResends < 58 * 11, "A resent " + pair.A.ReliableResends + " time(s) for " + sent + " message(s)");
    }

    [Fact]
    public void A_lost_message_is_still_resent_promptly_between_two_games()
    {
        var pair = new ConnectionPair(new NetworkConditions { LatencyMs = 17, LossPercent = 10 });
        var sentAt = new List<double>();
        var arrivedAt = new List<double>();

        pair.RunAsTwoGames(
            60,
            29,
            30,
            now =>
            {
                while (arrivedAt.Count < pair.ReceivedByB.Count)
                {
                    arrivedAt.Add(now);
                }

                SendReliable(pair.A, "a");
                sentAt.Add(now);
            });

        Assert.True(arrivedAt.Count > 1500);
        double worst = 0;
        for (int i = 0; i < arrivedAt.Count; i++)
        {
            worst = Math.Max(worst, arrivedAt[i] - sentAt[i]);
        }

        // With 10 % loss some messages need two or three tries; none should sit for most of a second.
        Assert.True(worst < 0.7, "the slowest reliable message took " + worst.ToString("0.000") + " s");
        Assert.True(pair.A.ReliableResends > 0);
    }

    [Fact]
    public void An_idle_connection_only_sends_keep_alives()
    {
        var pair = new ConnectionPair();

        pair.Run(10);

        // One bare packet per keep-alive interval, not one per frame: about 10 per second, not 60.
        Assert.InRange(pair.A.PacketsSent, 80, 130);
        Assert.InRange(pair.B.PacketsSent, 80, 130);
        Assert.True(pair.A.HasRoundTripTime);
    }

    [Fact]
    public void A_backlog_is_spread_over_several_packets_and_flushes()
    {
        var pair = new ConnectionPair();
        string big = new('x', 600);
        for (int i = 0; i < 40; i++)
        {
            SendReliable(pair.A, i + big);
        }

        pair.Run(1);

        Assert.Equal(Enumerable.Range(0, 40).Select(i => i + big), pair.ReceivedByB.Select(ConnectionPair.Text));
    }

    [Fact]
    public void Messages_that_cannot_fit_in_one_packet_are_refused_up_front()
    {
        var connection = new Connection(new ConnectionConfig { MaxDatagramSize = 200 });
        var tooBig = new byte[connection.MaxMessageSize + 1];
        var justFits = new byte[connection.MaxMessageSize];

        Assert.Throws<ArgumentException>(() => connection.QueueReliable(tooBig, 0, tooBig.Length));
        Assert.Throws<ArgumentException>(() => connection.QueueUnreliable(tooBig, 0, tooBig.Length));
        connection.QueueReliable(justFits, 0, justFits.Length);

        int packetSize = 0;
        connection.Flush(0, (_, count) => packetSize = count);
        Assert.InRange(packetSize, connection.MaxMessageSize, 200);
    }

    [Fact]
    public void Sequence_numbers_survive_wrapping_around()
    {
        // 16-bit sequences wrap after 65536 packets: about 18 minutes at 60 packets per second.
        var pair = new ConnectionPair(new NetworkConditions { LossPercent = 2, LatencyMs = 10 });
        int sent = 0;

        pair.Run(1300, _ =>
        {
            SendUnreliable(pair.A, "u");
            if (sent < 70000)
            {
                SendReliable(pair.A, "r" + sent++);
            }
        });
        pair.Run(5);

        Assert.True(pair.A.PacketsSent > 70000);
        Assert.Equal(70000, pair.ReceivedByB.Count(m => m.Reliable));
        Assert.Equal("r69999", ConnectionPair.Text(pair.ReceivedByB.Last(m => m.Reliable)));
        Assert.InRange(pair.A.PacketLoss, 0, 0.1);
    }

    [Fact]
    public void A_dead_peer_makes_the_reliable_queue_grow_until_it_is_reported()
    {
        var config = new ConnectionConfig { MaxReliableQueue = 50 };
        var connection = new Connection(config);
        for (int i = 0; i < 60; i++)
        {
            connection.QueueReliable([1], 0, 1);
            connection.Flush(i * 0.1, (_, _) => { });
        }

        Assert.True(connection.IsOverloaded);
        Assert.Equal(60, connection.ReliableQueueLength);
    }

    [Fact]
    public void Garbage_is_rejected_as_a_format_error()
    {
        var connection = new Connection(new ConnectionConfig());
        var output = new List<ReceivedMessage>();

        // A valid header followed by a record that claims more bytes than there are.
        var writer = new PacketWriter(64);
        writer.WriteUInt16(0);
        writer.WriteUInt16(0);
        writer.WriteUInt32(0);
        writer.WriteUInt16(ushort.MaxValue);
        writer.WriteUInt16(0);
        writer.WriteByte(0);
        writer.WriteVarUInt32(500);

        Assert.Throws<PacketFormatException>(() => connection.Receive(0, new PacketReader(writer.Buffer, 0, writer.Length), writer.Length, output));
        Assert.Throws<PacketFormatException>(() => connection.Receive(0, new PacketReader([1, 2, 3], 0, 3), 3, output));
        Assert.Empty(output);
    }

    [Fact]
    public void Acks_received_before_the_peer_heard_from_us_are_ignored()
    {
        // B has received nothing yet, so the ack fields of its first packet mean nothing. If A took them at face
        // value it would count its own packet 0 as delivered and measure a bogus round trip.
        var a = new Connection(new ConnectionConfig());
        var b = new Connection(new ConnectionConfig());
        byte[]? fromB = null;
        a.QueueReliable([7], 0, 1);
        a.Flush(0, (_, _) => { });
        b.Flush(0, (data, count) => fromB = data.Take(count).ToArray());

        a.Receive(5, new PacketReader(fromB!, 1, fromB!.Length - 1), fromB.Length, new List<ReceivedMessage>());

        Assert.False(a.HasRoundTripTime);
        Assert.Equal(1, a.ReliableQueueLength);
    }
}

public class SimulatedTransportTests
{
    private static (SimulatedTransport Sender, ITransport Receiver) Create(NetworkConditions conditions, int seed = 7)
    {
        var network = new LoopbackNetwork();
        return (new SimulatedTransport(network.CreateEndpoint(1), conditions, seed), network.CreateEndpoint(2));
    }

    private static int Drain(ITransport transport, List<byte>? firstBytes = null)
    {
        var buffer = new byte[1200];
        int count = 0;
        while (transport.Receive(buffer, out _, out ulong peer))
        {
            Assert.Equal(1ul, peer);
            firstBytes?.Add(buffer[0]);
            count++;
        }

        return count;
    }

    [Fact]
    public void Perfect_conditions_pass_straight_through()
    {
        var (sender, receiver) = Create(new NetworkConditions());

        sender.Send(2, [42], 1);

        Assert.Equal(1, Drain(receiver));
    }

    [Fact]
    public void Latency_holds_datagrams_until_they_are_due()
    {
        var (sender, receiver) = Create(new NetworkConditions { LatencyMs = 200 });
        sender.Update(10.0);
        sender.Send(2, [1], 1);

        sender.Update(10.19);
        Assert.Equal(0, Drain(receiver));
        Assert.Equal(1, sender.InFlight);

        sender.Update(10.21);
        Assert.Equal(1, Drain(receiver));
        Assert.Equal(0, sender.InFlight);
    }

    [Fact]
    public void Releasing_everything_sends_what_was_held_back_in_order()
    {
        var (sender, receiver) = Create(new NetworkConditions { LatencyMs = 500 });
        sender.Update(1.0);
        sender.Send(2, [1], 1);
        sender.Send(2, [2], 1);

        sender.ReleaseAll();
        var order = new List<byte>();

        Assert.Equal(2, Drain(receiver, order));
        Assert.Equal(new byte[] { 1, 2 }, order);
        Assert.Equal(0, sender.InFlight);
    }

    [Fact]
    public void Loss_drops_roughly_the_configured_share()
    {
        var (sender, receiver) = Create(new NetworkConditions { LossPercent = 25 });
        for (int i = 0; i < 4000; i++)
        {
            sender.Send(2, [1], 1);
        }

        sender.Update(1);
        int delivered = Drain(receiver);

        Assert.InRange(delivered, 2800, 3200);
        Assert.Equal(4000 - delivered, sender.Dropped);
    }

    [Fact]
    public void Jitter_reorders_and_duplication_duplicates()
    {
        var (sender, receiver) = Create(new NetworkConditions { LatencyMs = 100, JitterMs = 90, DuplicatePercent = 100 });
        sender.Update(0);
        for (byte i = 0; i < 50; i++)
        {
            sender.Send(2, [i], 1);
        }

        sender.Update(5);
        var order = new List<byte>();
        int delivered = Drain(receiver, order);

        Assert.Equal(100, delivered);
        Assert.NotEqual(order.OrderBy(b => b), order);
    }

    [Fact]
    public void Same_seed_gives_the_same_behaviour()
    {
        int Run(int seed)
        {
            var (sender, receiver) = Create(new NetworkConditions { LossPercent = 40 }, seed);
            for (int i = 0; i < 200; i++)
            {
                sender.Send(2, [1], 1);
            }

            sender.Update(1);
            return Drain(receiver);
        }

        Assert.Equal(Run(99), Run(99));
    }

    [Fact]
    public void Datagrams_are_copied_so_the_caller_can_reuse_its_buffer()
    {
        var (sender, receiver) = Create(new NetworkConditions { LatencyMs = 50 });
        byte[] shared = [1];
        sender.Update(0);
        sender.Send(2, shared, 1);
        shared[0] = 99;

        sender.Update(1);
        var seen = new List<byte>();
        Drain(receiver, seen);

        Assert.Equal(new byte[] { 1 }, seen);
    }

    [Fact]
    public void Loopback_delivers_between_endpoints_and_drops_what_has_no_destination()
    {
        var network = new LoopbackNetwork(64);
        ITransport a = network.CreateEndpoint(10);
        ITransport b = network.CreateEndpoint(20);
        var buffer = new byte[64];

        a.Send(20, [5, 6], 2);
        a.Send(999, [1], 1);

        Assert.True(b.Receive(buffer, out int count, out ulong peer));
        Assert.Equal((2, 10ul, (byte)5), (count, peer, buffer[0]));
        Assert.False(b.Receive(buffer, out _, out _));
        Assert.False(a.Receive(buffer, out _, out _));
        Assert.Throws<ArgumentException>(() => a.Send(20, new byte[65], 65));

        network.RemoveEndpoint(20);
        a.Send(20, [1], 1);
        Assert.False(b.Receive(buffer, out _, out _));
    }
}
