using System.Text;
using SummerSync.Core.Hashing;

namespace SummerSync.Core.Tests.Hashing;

public class Fnv1aTests
{
    // Published FNV-1a 64 bit test vectors.
    [Theory]
    [InlineData("", "cbf29ce484222325")]
    [InlineData("a", "af63dc4c8601ec8c")]
    [InlineData("foobar", "85944171f73967e8")]
    public void Matches_the_reference_vectors(string text, string expected)
    {
        Assert.Equal(expected, Fnv1a.ToHex(Fnv1a.Hash64(Encoding.ASCII.GetBytes(text))));
    }

    [Fact]
    public void Hashing_in_pieces_gives_the_same_result()
    {
        byte[] data = Encoding.ASCII.GetBytes("the quick brown fox");

        ulong pieces = Fnv1a.Hash64(data, 0, 7, Fnv1a.OffsetBasis);
        pieces = Fnv1a.Hash64(data, 7, data.Length - 7, pieces);

        Assert.Equal(Fnv1a.Hash64(data), pieces);
    }

    [Fact]
    public void Hex_is_always_sixteen_digits()
    {
        Assert.Equal("000000000000002a", Fnv1a.ToHex(42));
    }
}
