using SummerSync.Core.Hashing;
using SummerSync.Core.Net;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class WorldShareTests
{
    // Files kept in memory: whole ones on the host, and on a client whatever has arrived.
    private sealed class MemoryStore : IFileStore
    {
        private readonly Dictionary<ulong, byte[]> whole = new();
        private readonly Dictionary<ulong, byte[]> partial = new();

        public int Completed { get; private set; }

        public WorldFile Add(int name, byte[] bytes)
        {
            ulong id = Fnv1a.Hash64(bytes);
            whole[id] = bytes;
            return new WorldFile(name, id, bytes.Length);
        }

        public byte[]? Get(ulong id) => whole.TryGetValue(id, out byte[]? bytes) ? bytes : null;

        public int Read(ulong id, int offset, byte[] buffer, int bufferOffset, int count)
        {
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
            partial.Remove(id);
            if (Fnv1a.Hash64(bytes) != id)
            {
                return false;
            }

            whole[id] = bytes;
            Completed++;
            return true;
        }

        public void Discard(ulong id) => partial.Remove(id);
    }

    private sealed class Peer
    {
        public Peer(SessionNet.Node node)
        {
            Node = node;
            Share = new WorldShare(node.Session, node.Logger);
            Share.SetStore(Store);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Share.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public WorldShare Share { get; }

        public MemoryStore Store { get; } = new();
    }

    private static byte[] Bytes(int seed, int size)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // A world like a real one: the main file, the items, the shapes of bent metal.
    private static WorldFile[] World(MemoryStore store, int seed)
    {
        return new[]
        {
            store.Add(0, Bytes(seed, 64_000)),
            store.Add(1, Bytes(seed + 1, 3_500)),
            store.Add(2, Bytes(seed + 2, 300_000)),
        };
    }

    [Fact]
    public void A_client_ends_up_with_the_hosts_world_whole()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40, LossPercent = 5 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        WorldFile[] world = World(host.Store, 10);

        Assert.True(host.Share.Offer(world, 1_760_000_000));
        Assert.False(client.Share.IsReady);
        net.Run(10);

        Assert.True(client.Share.IsReady);
        Assert.Equal(host.Share.Manifest!.Id, client.Share.Manifest!.Id);
        Assert.Equal(1_760_000_000, client.Share.Manifest.SavedAt);
        foreach (WorldFile file in world)
        {
            Assert.Equal(host.Store.Get(file.Id), client.Store.Get(file.Id));
        }

        Assert.Equal(1f, client.Share.Progress);
        Assert.Equal(0, host.Share.Malformed + client.Share.Malformed + host.Share.Files.Malformed + client.Share.Files.Malformed);
        Assert.StartsWith("World: the host's ", client.Share.Describe());
        Assert.False(host.Share.IsReady);
    }

    [Fact]
    public void A_client_hears_which_of_the_hosts_worlds_it_is()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        WorldFile[] first = World(host.Store, 10);
        host.Share.Offer(first, 100, 0xABCDEF0123456789UL);
        net.Run(2);
        Assert.Equal(0xABCDEF0123456789UL, client.Share.Manifest!.Key);

        // The same files under another key: another of the host's worlds, the same contents.
        host.Share.Offer(first, 100, 5UL);
        net.Run(2);
        Assert.Equal(5UL, client.Share.Manifest!.Key);
        Assert.Equal(host.Share.Manifest!.Id, client.Share.Manifest.Id);

        // An older build says none.
        host.Share.Offer(first, 100);
        net.Run(2);
        Assert.Equal(0UL, client.Share.Manifest!.Key);
        Assert.Equal(0, client.Share.Malformed);
    }

    [Fact]
    public void A_world_saved_again_is_fetched_again_but_only_what_changed()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        WorldFile[] first = World(host.Store, 10);
        host.Share.Offer(first, 100);
        net.Run(8);
        Assert.True(client.Share.IsReady);
        ulong before = client.Share.Manifest!.Id;

        // Saved again: the main file changed, the rest did not.
        WorldFile[] second = { host.Store.Add(0, Bytes(99, 65_000)), first[1], first[2] };
        host.Share.Offer(second, 200);
        net.Run(0.2);
        Assert.NotEqual(before, client.Share.Manifest!.Id);
        Assert.False(client.Share.IsReady);
        net.Run(8);

        Assert.True(client.Share.IsReady);
        Assert.Equal(host.Share.Manifest!.Id, client.Share.Manifest.Id);
        Assert.Equal(4, client.Store.Completed);
    }

    [Fact]
    public void A_world_changed_while_it_was_on_its_way_ends_up_as_the_last_one()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, LossPercent = 3 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        host.Share.Offer(new[] { host.Store.Add(0, Bytes(1, 900_000)) }, 100);
        net.Run(0.5);
        Assert.True(client.Share.Progress is > 0f and < 1f);

        WorldFile[] last = World(host.Store, 50);
        host.Share.Offer(last, 200);
        net.Run(12);

        Assert.True(client.Share.IsReady);
        Assert.Equal(host.Share.Manifest!.Id, client.Share.Manifest!.Id);
        Assert.Equal(3, client.Share.Manifest.Files.Length);
    }

    [Fact]
    public void Whoever_comes_in_later_gets_the_world_too()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        host.Share.Offer(World(host.Store, 10), 100);
        net.Run(1);
        var late = new Peer(net.AddClient("Late"));
        net.Run(10);

        Assert.True(late.Share.IsReady);
        Assert.Equal(host.Share.Manifest!.Id, late.Share.Manifest!.Id);
    }

    [Fact]
    public void A_host_without_a_saved_world_says_so()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);

        Assert.Equal("World: waiting to hear of the host's", client.Share.Describe());
        host.Share.Offer(new WorldFile[0], 0);
        net.Run(1);

        Assert.NotNull(client.Share.Manifest);
        Assert.False(client.Share.Manifest!.HasWorld);
        Assert.Equal(0UL, client.Share.Manifest.Id);
        Assert.False(client.Share.IsReady);
        Assert.Equal("World: the host has none saved", client.Share.Describe());

        // Files without the main one are not a world either.
        host.Share.Offer(new[] { host.Store.Add(1, Bytes(3, 100)) }, 0);
        net.Run(2);
        Assert.False(client.Share.Manifest!.HasWorld);
        Assert.False(client.Share.IsReady);
    }

    [Fact]
    public void Two_files_with_the_same_bytes_travel_once()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);
        byte[] same = Bytes(7, 120);
        WorldFile trophies = host.Store.Add(4, same);
        WorldFile leaderboard = host.Store.Add(5, (byte[])same.Clone());

        Assert.True(host.Share.Offer(new[] { host.Store.Add(0, Bytes(1, 5_000)), trophies, leaderboard }, 0));
        net.Run(4);

        Assert.True(client.Share.IsReady);
        Assert.Equal(3, client.Share.Manifest!.Files.Length);
        Assert.Equal(2, client.Share.Files.Files.Count);
        Assert.Equal(2, client.Store.Completed);
    }

    [Fact]
    public void A_world_that_cannot_be_shared_is_not_offered_half()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        net.Run(1);

        WorldFile main = host.Store.Add(0, Bytes(1, 500));
        Assert.False(host.Share.Offer(new[] { main, new WorldFile(2, 12345, WorldFiles.MaxFileBytes + 1) }, 0));
        Assert.False(host.Share.Offer(new[] { main, main }, 0));
        Assert.False(host.Share.Offer(new[] { main, new WorldFile(WorldFiles.Names.Length, 1, 10) }, 0));
        net.Run(1);

        Assert.False(host.Share.Manifest!.HasWorld);
        Assert.False(client.Share.Manifest!.HasWorld);
        Assert.Contains(host.Node.Log, entry => entry.Message.Contains("cannot be shared"));
    }

    [Fact]
    public void Only_the_host_says_which_world_there_is()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var client = new Peer(net.AddClient("Julian"));
        var other = new Peer(net.AddClient("Other"));
        net.Run(1);
        host.Share.Offer(World(host.Store, 10), 100);
        net.Run(8);

        // A client offering a world of its own reaches nobody as a world.
        client.Share.Offer(World(client.Store, 70), 300);
        net.Run(2);

        Assert.Equal(host.Share.Manifest!.Id, other.Share.Manifest!.Id);
        Assert.True(other.Share.IsReady);
    }

    [Fact]
    public void Files_never_pile_up_in_front_of_what_is_said_every_flush()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Peer(net.Host);
        var songs = new FileTransfer(net.Host.Session, net.Host.Logger);
        var songStore = new MemoryStore();
        songs.SetStore(songStore);
        var client = new Peer(net.AddClient("Julian"));
        var clientSongs = new FileTransfer(client.Node.Session, client.Node.Logger);
        clientSongs.SetStore(new MemoryStore());
        int most = 0;
        int samples = 0;
        Action<double>? earlier = net.Host.BeforeUpdate;
        net.Host.BeforeUpdate = time =>
        {
            earlier?.Invoke(time);
            songs.Update(time);
            int waiting = net.Host.Session.UnreliableWaiting(1);
            most = Math.Max(most, waiting);
            samples++;
        };
        Action<double>? clientEarlier = client.Node.BeforeUpdate;
        client.Node.BeforeUpdate = time =>
        {
            clientEarlier?.Invoke(time);
            clientSongs.Update(time);
        };
        net.Run(1);

        // Two channels at once, each with more parts per flush than a flush can take with everything else.
        host.Share.Offer(World(host.Store, 10), 100);
        songs.Offer(new[] { new SharedFile(songStore.Add(0, Bytes(80, 400_000)).Id, 400_000) });
        net.Run(15);

        Assert.True(client.Share.IsReady);
        Assert.Equal(1, clientSongs.Fetched);
        Assert.True(samples > 100);
        Assert.True(most <= 2 * FileChannel.Songs.PartsPerFlush, "At most one flush's worth of parts waited; " + most + " did.");
    }

    [Fact]
    public void The_same_files_are_the_same_world_and_nothing_is_no_world()
    {
        var a = new WorldManifest(1, 10, new[] { new WorldFile(0, 5, 100), new WorldFile(1, 6, 50) });
        var b = new WorldManifest(9, 99, new[] { new WorldFile(0, 5, 100), new WorldFile(1, 6, 50) });
        var c = new WorldManifest(1, 10, new[] { new WorldFile(0, 5, 100), new WorldFile(1, 7, 50) });

        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Id, c.Id);
        Assert.Equal(0UL, new WorldManifest(1, 10, new WorldFile[0]).Id);
        Assert.Equal(150, a.TotalBytes);
        Assert.Equal(1, a.Find(1));
        Assert.Equal(-1, a.Find(3));
        Assert.Equal(0, WorldFiles.IndexOf("DEFAULTES2FILE.TXT"));
        Assert.Equal(-1, WorldFiles.IndexOf("options.txt"));
    }

    [Fact]
    public void The_worlds_messages_follow_the_last_ones_in_use()
    {
        Assert.Equal(75, WorldShareWire.MessageFiles);
        Assert.Equal(78, WorldShareWire.MessageManifest);
        Assert.Equal(39, Protocol.Version);
    }
}
