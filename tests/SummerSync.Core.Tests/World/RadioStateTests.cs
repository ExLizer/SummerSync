using SummerSync.Core.Diagnostics;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class RadioStateTests
{
    [Fact]
    public void A_radio_state_survives_the_wire()
    {
        var sent = new RadioState { Clip = "polaventris-transsi_utare", Position = 83.25f, Playing = true };
        var writer = new PacketWriter(64);
        sent.Write(writer);
        var reader = new PacketReader();
        reader.SetBuffer(writer.Buffer, 0, writer.Length);

        RadioState read = RadioState.Read(reader);

        Assert.Equal("polaventris-transsi_utare", read.Clip);
        Assert.Equal(83.25f, read.Position);
        Assert.True(read.Playing);
    }

    [Fact]
    public void A_silent_station_is_a_state_too()
    {
        var writer = new PacketWriter(64);
        default(RadioState).Write(writer);
        var reader = new PacketReader();
        reader.SetBuffer(writer.Buffer, 0, writer.Length);

        RadioState read = RadioState.Read(reader);

        Assert.False(read.Playing);
        Assert.True(string.IsNullOrEmpty(read.Clip));
        Assert.True(read.SameRecording(new RadioState { Clip = null }));
    }

    [Fact]
    public void A_recording_that_plays_goes_on_and_one_that_does_not_stays()
    {
        var playing = new RadioState { Clip = "song", Position = 10f, Playing = true };
        var silent = new RadioState { Clip = "song", Position = 10f, Playing = false };

        Assert.Equal(12.5f, playing.Later(2.5).Position);
        Assert.Equal(10f, silent.Later(2.5).Position);
        Assert.False(playing.SameRecording(silent));
        Assert.True(playing.SameRecording(playing.Later(60)));
        Assert.False(playing.SameRecording(new RadioState { Clip = "another", Position = 10f, Playing = true }));
    }

    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 0, 0, 0x80, 0x7F, 0 })]
    [InlineData(new byte[] { 1, 0, 0, 0x80, 0xBF, 0 })]
    [InlineData(new byte[] { 1, 0, 0 })]
    public void Bytes_that_are_not_a_radio_state_are_refused(byte[] bytes)
    {
        var reader = new PacketReader();
        reader.SetBuffer(bytes, 0, bytes.Length);

        Assert.Throws<PacketFormatException>(() => RadioState.Read(reader));
    }

    [Fact]
    public void The_messages_of_the_world_and_of_the_log_do_not_share_a_number()
    {
        Assert.Equal(58, LogRelayWire.MessageLine);
        Assert.Equal(59, WorldWire.MessageRadio);
        Assert.Equal(60, SongWire.MessageSongs);
        Assert.Equal(62, SongWire.MessagePart);
    }
}

public class RadioReplicationTests
{
    // A machine whose station plays one recording after another, each from its start.
    private sealed class Station
    {
        private string clip = "first song";
        private double startedAt;
        private bool playing = true;

        public Station(SessionNet.Node node)
        {
            Replication = new WorldReplication(node.Session, node.Logger);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Now = time;
                Replication.Update(time);
                if (node.Session.Role == SessionRole.Host)
                {
                    Replication.SetRadio(OnAir);
                }
            };
        }

        public WorldReplication Replication { get; }

        public double Now { get; private set; }

        public RadioState OnAir => new() { Clip = playing ? clip : null, Position = playing ? (float)(Now - startedAt) : 0f, Playing = playing };

        public void Play(string recording)
        {
            clip = recording;
            startedAt = Now;
            playing = true;
        }

        public void Seek(double seconds)
        {
            startedAt = Now - seconds;
        }

        public void Stop()
        {
            playing = false;
        }
    }

    [Fact]
    public void A_client_hears_what_the_hosts_station_plays_and_where_it_is_in_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Station(net.Host);
        var client = new Station(net.AddClient("Julian"));
        Assert.False(client.Replication.TryGetRadio(out _));

        net.Run(1);
        Assert.True(client.Replication.TryGetRadio(out RadioState heard));
        Assert.Equal("first song", heard.Clip);
        Assert.True(heard.Playing);

        // In step between one word from the host and the next, within the delay of the network.
        for (int i = 0; i < 60; i++)
        {
            net.Run(0.41);
            Assert.True(client.Replication.TryGetRadio(out heard));
            Assert.True(Math.Abs(heard.Position - host.OnAir.Position) < 0.15, $"{heard.Position - host.OnAir.Position:0.000} s apart after {net.Now:0.0} s");
        }

        // That was a word every ten seconds or so.
        Assert.InRange(host.Replication.Radios, 3, 6);
        Assert.Equal(host.Replication.Radios, client.Replication.Radios);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);
    }

    [Fact]
    public void The_next_recording_is_told_at_once()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Station(net.Host);
        var client = new Station(net.AddClient("Julian"));
        net.Run(2);
        long before = host.Replication.Radios;

        host.Play("Kansanrunot_jakso02");
        net.Run(0.3);

        Assert.Equal(before + 1, host.Replication.Radios);
        Assert.True(client.Replication.TryGetRadio(out RadioState heard));
        Assert.Equal("Kansanrunot_jakso02", heard.Clip);
        Assert.True(Math.Abs(heard.Position - host.OnAir.Position) < 0.15);
    }

    [Fact]
    public void A_recording_that_jumps_and_a_station_that_falls_silent_are_told_at_once()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Station(net.Host);
        var client = new Station(net.AddClient("Julian"));
        net.Run(2);
        long before = host.Replication.Radios;

        host.Seek(120);
        net.Run(0.3);
        Assert.Equal(before + 1, host.Replication.Radios);
        Assert.True(client.Replication.TryGetRadio(out RadioState heard));
        Assert.True(Math.Abs(heard.Position - host.OnAir.Position) < 0.15);

        host.Stop();
        net.Run(0.3);
        Assert.Equal(before + 2, host.Replication.Radios);
        Assert.True(client.Replication.TryGetRadio(out heard));
        Assert.False(heard.Playing);
        Assert.True(string.IsNullOrEmpty(heard.Clip));
    }

    [Fact]
    public void Whoever_comes_in_later_is_told_without_waiting_for_the_next_turn()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Station(net.Host);
        var first = new Station(net.AddClient("Julian"));
        net.Run(3);

        var late = new Station(net.AddClient("Second"));
        net.Run(1);

        Assert.True(late.Replication.TryGetRadio(out RadioState heard));
        Assert.Equal("first song", heard.Clip);
        Assert.True(Math.Abs(heard.Position - host.OnAir.Position) < 0.15);
        Assert.True(first.Replication.TryGetRadio(out _));
    }

    [Fact]
    public void The_two_channels_are_told_apart()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Station(net.Host);
        var client = new Station(net.AddClient("Julian"));
        net.Run(1);
        Assert.False(client.Replication.TryGetRadio(WorldReplication.FolkChannel, out _));

        host.Replication.SetRadio(WorldReplication.FolkChannel, new RadioState { Clip = "#00000000000000aa", Position = 7f, Playing = true });
        net.Run(0.5);

        Assert.True(client.Replication.TryGetRadio(WorldReplication.FolkChannel, out RadioState folk));
        Assert.Equal("#00000000000000aa", folk.Clip);
        Assert.InRange(folk.Position, 7f, 8f);
        Assert.True(client.Replication.TryGetRadio(WorldReplication.MainChannel, out RadioState main));
        Assert.Equal("first song", main.Clip);
        Assert.Equal(0, client.Replication.Malformed);
    }

    [Fact]
    public void The_host_has_no_host_to_hear()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Station(net.Host);
        _ = new Station(net.AddClient("Julian"));
        net.Run(1);

        Assert.False(host.Replication.TryGetRadio(out _));
    }
}
