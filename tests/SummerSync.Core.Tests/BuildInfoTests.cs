namespace SummerSync.Core.Tests;

public class BuildInfoTests
{
    [Theory]
    [InlineData("0.1.0+3f2a9c1b7d5e4f60718293a4b5c6d7e8f9012345", "0.1.0+3f2a9c1b")]
    [InlineData("0.1.0+3f2a9c1b", "0.1.0+3f2a9c1b")]
    [InlineData("0.1.0+abc", "0.1.0+abc")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Shortens_only_long_revisions(string? informationalVersion, string? expected)
    {
        Assert.Equal(expected, BuildInfo.ShortenRevision(informationalVersion!));
    }

    [Fact]
    public void Version_has_three_numeric_parts()
    {
        string[] parts = BuildInfo.Version.Split('.');

        Assert.Equal(3, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out _)));
    }

    [Fact]
    public void Informational_version_starts_with_the_version()
    {
        Assert.StartsWith(BuildInfo.Version, BuildInfo.InformationalVersion);
    }
}
