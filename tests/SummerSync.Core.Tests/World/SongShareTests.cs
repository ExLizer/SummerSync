using SummerSync.Core.Hashing;
using SummerSync.Core.Net;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class SongShareTests
{
    // Songs kept in memory: whole ones on the host, and on a client whatever has arrived.
    private sealed class MemoryStore : IFileStore
    {
        private readonly Dictionary<ulong, byte[]> whole = new();
        private readonly Dictionary<ulong, byte[]> partial = new();

        public int Reads { get; private set; }

        public int Discarded { get; private set; }

        /// <summary>Makes what a client puts together come out wrong, the given number of times.</summary>
        public int Spoil { get; set; }

        public SharedFile Add(byte[] bytes)
        {
            ulong id = Fnv1a.Hash64(bytes);
            whole[id] = bytes;
            return new SharedFile(id, bytes.Length);
        }

        public byte[]? Get(ulong id) => whole.TryGetValue(id, out byte[]? bytes) ? bytes : null;

        public int Read(ulong id, int offset, byte[] buffer, int bufferOffset, int count)
        {
            Reads++;
            if (!whole.TryGetValue(id, out byte[]? bytes) || offset + count > bytes.Length)
            {
                return 0;
            }

            Array.Copy(bytes, offset, buffer, bufferOffset, count);
            return count;
        }

        public bool Has(ulong id, int size) => whole.TryGetValue(id, out byte[]? bytes) && bytes.Length == size;

        public void Write(ulong id, int size, int offset, byte[] data, int dataOffset, int count)
        {
            if (!partial.TryGetValue(id, out byte[]? bytes))
            {
                partial[id] = bytes = new byte[size];
            }

            Array.Copy(data, dataOffset, bytes, offset, count);
        }

        public bool Complete(ulong id, int size)
        {
            byte[] bytes = partial[id];
            if (Spoil > 0)
            {
                Spoil--;
                bytes[0] ^= 0xFF;
            }

            if (Fnv1a.Hash64(bytes) != id)
            {
                return false;
            }

            whole[id] = bytes;
            partial.Remove(id);
            return true;
        }

        public void Discard(ulong id)
        {
            Discarded++;
            partial.Remove(id);
        }
    }

    private sealed class Peer
    {
        public Peer(SessionNet.Node node)
            : this(node, FileChannel.Songs)
        {
        }

        public Peer(SessionNet.Node node, FileChannel channel)
        {
            Node = node;
            Share = new FileTransfer(node.Session, node.Logger, channel);
            Share.SetStore(Store);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Share.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public FileTransfer Share { get; }

        public MemoryStore Store { get; } = new();
    }

    private static byte[] Song(int seed, int size)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void The_cds_go_on_a_channel_of_their_own_beside_the_songs()
    {
        Assert.Equal(109, CdWire.MessageTracks);
        Assert.Equal(110, CdWire.MessageWant);
        Assert.Equal(111, CdWire.MessagePart);
        Assert.Equal(48, FileChannel.Cds.MaxFiles);

        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var hostCds = new Peer(net.Host, FileChannel.Cds);
        SessionNet.Node node = net.AddClient("Julian");
        var client = new Peer(node);
        var clientCds = new Peer(node, FileChannel.Cds);
        net.Run(1);
        SharedFile[] songs = { host.Store.Add(Song(1, 50_000)) };
        SharedFile[] tracks = { hostCds.Store.Add(Song(2, 80_000)), hostCds.Store.Add(Song(3, 3000)) };

        host.Share.Offer(songs);
        hostCds.Share.Offer(tracks);
        net.Run(8);

        Assert.Single(client.Share.Files);
        Assert.Equal(2, clientCds.Share.Files.Count);
        Assert.True(client.Share.IsHere(songs[0].Id));
        Assert.All(tracks, track => Assert.Equal(hostCds.Store.Get(track.Id), clientCds.Store.Get(track.Id)));
        Assert.Null(clientCds.Store.Get(songs[0].Id));
        Assert.Equal("CDs: 2 of the host's 2 here", clientCds.Share.Describe());
        Assert.Equal(0, client.Share.Malformed + clientCds.Share.Malformed + host.Share.Malformed + hostCds.Share.Malformed);
    }

    [Fact]
    public void The_hosts_car_picture_goes_on_a_channel_of_its_own()
    {
        Assert.Equal(112, ArtWire.MessageList);
        Assert.Equal(113, ArtWire.MessageWant);
        Assert.Equal(114, ArtWire.MessagePart);
        Assert.Equal(1, FileChannel.Art.MaxFiles);

        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var hostCds = new Peer(net.Host, FileChannel.Cds);
        var hostArt = new Peer(net.Host, FileChannel.Art);
        SessionNet.Node node = net.AddClient("Julian");
        var clientCds = new Peer(node, FileChannel.Cds);
        var clientArt = new Peer(node, FileChannel.Art);
        net.Run(1);
        SharedFile[] picture = { hostArt.Store.Add(Song(4, 120_000)) };

        hostArt.Share.Offer(picture);
        net.Run(8);

        Assert.Equal(hostArt.Store.Get(picture[0].Id), clientArt.Store.Get(picture[0].Id));
        Assert.Empty(clientCds.Share.Files);
        Assert.Equal(0, clientArt.Share.Malformed + hostArt.Share.Malformed + clientCds.Share.Malformed + hostCds.Share.Malformed);
    }

    [Fact]
    public void A_client_ends_up_with_every_song_of_the_hosts()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        SharedFile[] songs = { host.Store.Add(Song(1, 200_000)), host.Store.Add(Song(2, 961)), host.Store.Add(Song(3, 90_000)) };

        host.Share.Offer(songs);
        net.Run(8);

        Assert.Equal(3, client.Share.Songs.Count);
        Assert.Equal(3, client.Share.Fetched);
        foreach (SharedFile song in songs)
        {
            Assert.True(client.Share.IsHere(song.Id));
            Assert.Equal(host.Store.Get(song.Id), client.Store.Get(song.Id));
        }

        Assert.Equal(0, host.Share.Malformed + client.Share.Malformed);
        Assert.Equal("Songs: 3 of the host's 3 here", client.Share.Describe());
    }

    [Fact]
    public void Lost_parts_are_asked_for_again_and_nothing_else_is_held_up()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, JitterMs = 15, LossPercent = 8 }, seed: 7);
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        SharedFile song = host.Store.Add(Song(4, 400_000));

        host.Share.Offer(new[] { song });
        net.Run(25);

        Assert.True(client.Share.IsHere(song.Id), client.Share.Describe());
        Assert.Equal(host.Store.Get(song.Id), client.Store.Get(song.Id));

        // With eight parts in a hundred lost, not much more is sent than the song has.
        int parts = (song.Size + SongWire.PartSize - 1) / SongWire.PartSize;
        Assert.InRange(host.Share.Parts, parts, parts * 13 / 10);
    }

    [Fact]
    public void The_host_sends_at_its_own_pace_however_much_is_asked_for()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        host.Share.PartsPerFlush = 2;
        host.Share.Offer(new[] { host.Store.Add(Song(5, 2_000_000)) });

        net.Run(5);

        // Two parts thirty times a second.
        Assert.InRange(host.Share.Parts, 250, 320);
        Assert.InRange(client.Share.Progress(host.Share.Songs[0].Id), 0.1f, 0.2f);
    }

    [Fact]
    public void The_song_on_the_air_comes_before_the_others()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        SharedFile first = host.Store.Add(Song(6, 600_000));
        SharedFile second = host.Store.Add(Song(7, 600_000));
        SharedFile onAir = host.Store.Add(Song(8, 150_000));
        host.Share.Offer(new[] { first, second, onAir });
        net.Run(1);
        Assert.False(client.Share.IsHere(onAir.Id));

        client.Share.Want(onAir.Id);
        net.Run(3);

        Assert.True(client.Share.IsHere(onAir.Id));
        Assert.False(client.Share.IsHere(first.Id));
        net.Run(15);
        Assert.True(client.Share.IsHere(first.Id));
        Assert.True(client.Share.IsHere(second.Id));
    }

    [Fact]
    public void A_song_the_client_has_from_before_is_not_sent_again()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        byte[] known = Song(9, 300_000);
        SharedFile song = host.Store.Add(known);
        client.Store.Add(known);

        host.Share.Offer(new[] { song });
        net.Run(3);

        Assert.True(client.Share.IsHere(song.Id));
        Assert.Equal(0, host.Share.Parts);
        Assert.Equal(0, client.Share.Fetched);
    }

    [Fact]
    public void What_does_not_hash_to_its_number_is_thrown_away_and_fetched_once_more()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        SharedFile song = host.Store.Add(Song(10, 50_000));
        client.Store.Spoil = 1;

        host.Share.Offer(new[] { song });
        net.Run(6);

        Assert.Equal(1, client.Store.Discarded);
        Assert.True(client.Share.IsHere(song.Id));
        Assert.Contains(client.Node.Log, entry => entry.Message.Contains("is not that song"));
    }

    [Fact]
    public void A_song_that_keeps_coming_out_wrong_is_given_up_on()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        SharedFile bad = host.Store.Add(Song(11, 20_000));
        SharedFile good = host.Store.Add(Song(12, 20_000));
        client.Store.Spoil = 2;

        host.Share.Offer(new[] { bad, good });
        net.Run(8);

        Assert.False(client.Share.IsHere(bad.Id));
        Assert.True(client.Share.IsHere(good.Id));
        Assert.Equal(2, client.Store.Discarded);
        Assert.Contains(client.Node.Log, entry => entry.Message.Contains("Given up on it"));
    }

    [Fact]
    public void Whoever_comes_in_later_gets_the_list_and_the_songs_too()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var first = new Peer(net.AddClient("Julian"));
        net.Run(1);
        SharedFile song = host.Store.Add(Song(13, 120_000));
        host.Share.Offer(new[] { song });
        net.Run(3);
        Assert.True(first.Share.IsHere(song.Id));

        var late = new Peer(net.AddClient("Second"));
        net.Run(4);

        Assert.True(late.Share.IsHere(song.Id));
    }

    [Fact]
    public void The_host_offers_only_what_can_be_shared()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Peer(net.Host);
        _ = new Peer(net.AddClient("Julian"));
        net.Run(1);
        SharedFile song = host.Store.Add(Song(14, 1000));

        host.Share.Offer(new[] { song, song, new SharedFile(5, 0), new SharedFile(6, FileChannel.Songs.MaxFileBytes + 1) });

        Assert.Single(host.Share.Songs);
        Assert.Equal("0000000000000005", SharedFile.Name(5));
    }

    [Fact]
    public void Without_a_place_to_keep_them_nothing_is_fetched()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        client.Share.SetStore(null!);
        net.Run(1);
        host.Share.Offer(new[] { host.Store.Add(Song(15, 5000)) });

        net.Run(3);

        Assert.Single(client.Share.Songs);
        Assert.Equal(0, host.Share.Parts);
    }
}
