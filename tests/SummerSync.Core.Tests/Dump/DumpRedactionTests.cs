using SummerSync.Core.Dump;

namespace SummerSync.Core.Tests.Dump;

public class DumpRedactionTests
{
    [Theory]
    [InlineData("WWWdb_password")]
    [InlineData("WWWChatPassword")]
    [InlineData("AuthToken")]
    [InlineData("client_secret")]
    [InlineData("ApiKey")]
    public void Credential_like_names_are_secret(string name)
    {
        Assert.True(DumpRedaction.IsSecret(name));
    }

    [Theory]
    [InlineData("WWWdb_username")]
    [InlineData("PlayerMoney")]
    [InlineData("PlayerName")]
    [InlineData("GUIinteraction")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_names_are_not(string? name)
    {
        Assert.False(DumpRedaction.IsSecret(name!));
    }
}
