using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

public class GameBuildTests
{
    [Fact]
    public void The_identity_is_the_unity_version_and_a_short_hash_of_the_assembly()
    {
        string identity = GameBuild.Identify("5.0.0f4", [1, 2, 3]);

        Assert.Matches("^5\\.0\\.0f4/[0-9a-f]{8}$", identity);
        Assert.Equal(identity, GameBuild.Identify("5.0.0f4", [1, 2, 3]));
    }

    [Fact]
    public void A_different_assembly_or_engine_gives_a_different_identity()
    {
        string identity = GameBuild.Identify("5.0.0f4", [1, 2, 3]);

        Assert.NotEqual(identity, GameBuild.Identify("5.0.0f4", [1, 2, 4]));
        Assert.NotEqual(identity, GameBuild.Identify("5.0.0f5", [1, 2, 3]));
    }

    [Fact]
    public void It_fits_in_the_handshake()
    {
        string identity = GameBuild.Identify(GameBuild.ExpectedUnityVersion, new byte[1000]);

        Assert.Equal(identity, Protocol.LimitText(identity, Protocol.MaxVersionBytes));
    }
}
