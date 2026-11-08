using SummerSync.Core.Hashing;
using SummerSync.Core.World;

namespace SummerSync.NetProbe;

/// <summary>
/// Songs kept in memory, for trying the song share between two processes without the game: a host makes some up,
/// and a client keeps what arrives and checks it.
/// </summary>
internal sealed class ProbeSongs : IFileStore
{
    private readonly Dictionary<ulong, byte[]> whole = new();
    private readonly Dictionary<ulong, byte[]> partial = new();

    /// <summary>Songs that arrived and hashed to their number.</summary>
    public int Arrived { get; private set; }

    /// <summary>Songs that arrived and did not.</summary>
    public int Wrong { get; private set; }

    /// <summary>Makes up songs that begin like an Ogg file and go on as noise, the same for the same seed.</summary>
    public List<SharedFile> MakeUp(int count, int kilobytes)
    {
        var songs = new List<SharedFile>();
        for (int i = 0; i < count; i++)
        {
            var bytes = new byte[Math.Max(1, kilobytes) * 1024 + i];
            new Random(1000 + i).NextBytes(bytes);
            "OggS"u8.CopyTo(bytes);
            ulong id = Fnv1a.Hash64(bytes);
            whole[id] = bytes;
            songs.Add(new SharedFile(id, bytes.Length));
        }

        return songs;
    }

    /// <summary>
    /// Makes up a saved world: the first file is the main one, the rest follow the game's list. A different
    /// <paramref name="save"/> changes only the main file, the way saving again does.
    /// </summary>
    public List<WorldFile> MakeUpWorld(int files, int kilobytes, int save)
    {
        var world = new List<WorldFile>();
        for (int i = 0; i < files; i++)
        {
            var bytes = new byte[Math.Max(1, kilobytes) * 1024 + i];
            new Random(5000 + i + (i == 0 ? save * 100 : 0)).NextBytes(bytes);
            ulong id = Fnv1a.Hash64(bytes);
            whole[id] = bytes;
            world.Add(new WorldFile(i, id, bytes.Length));
        }

        return world;
    }

    public int Read(ulong id, int offset, byte[] buffer, int bufferOffset, int count)
    {
        if (!whole.TryGetValue(id, out byte[]? bytes) || offset < 0 || offset + count > bytes.Length)
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
        if (!partial.Remove(id, out byte[]? bytes) || bytes.Length != size || Fnv1a.Hash64(bytes) != id)
        {
            Wrong++;
            return false;
        }

        whole[id] = bytes;
        Arrived++;
        return true;
    }

    public void Discard(ulong id) => partial.Remove(id);
}
