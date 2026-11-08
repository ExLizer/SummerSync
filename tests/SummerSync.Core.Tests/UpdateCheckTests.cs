namespace SummerSync.Core.Tests;

/// <summary>The main menu's offer to update: the latest release against this build, and its version.txt.</summary>
public class UpdateCheckTests
{
    private const string Hash = "0B3A9D71C2E4F5061728394A5B6C7D8E9F00112233445566778899AABBCCDDEE";

    [Fact]
    public void A_build_is_published_under_its_version_with_a_dash()
    {
        Assert.Equal("v0.1.0-1ac84f76", UpdateCheck.TagOf("0.1.0+1ac84f76"));
        Assert.Equal("v0.1.0", UpdateCheck.TagOf("0.1.0"));
        Assert.Equal(string.Empty, UpdateCheck.TagOf(null));
    }

    [Fact]
    public void Only_another_build_is_offered()
    {
        Assert.False(UpdateCheck.Differs("v0.1.0-1ac84f76", "0.1.0+1ac84f76"));
        Assert.False(UpdateCheck.Differs(" v0.1.0-1AC84F76 ", "0.1.0+1ac84f76"));
        Assert.True(UpdateCheck.Differs("v0.1.0-2bd95a87", "0.1.0+1ac84f76"));
        Assert.True(UpdateCheck.Differs("v0.1.0-1ac84f76", "0.1.0"));
        Assert.False(UpdateCheck.Differs(null, "0.1.0+1ac84f76"));
        Assert.False(UpdateCheck.Differs(string.Empty, "0.1.0+1ac84f76"));
    }

    [Fact]
    public void A_releases_version_file_is_read()
    {
        Assert.True(UpdateCheck.ReadVersion("v0.1.0-700c3918\n" + Hash.ToLowerInvariant() + "\n", out string? tag, out string? hash));
        Assert.Equal("v0.1.0-700c3918", tag);
        Assert.Equal(Hash, hash);

        Assert.True(UpdateCheck.ReadVersion("﻿v0.1.0-700c3918\r\n" + Hash + "\r\n", out tag, out hash));
        Assert.Equal("v0.1.0-700c3918", tag);
    }

    [Fact]
    public void Anything_else_is_not_a_version_file()
    {
        Assert.False(UpdateCheck.ReadVersion("v0.1.0-700c3918", out _, out _));
        Assert.False(UpdateCheck.ReadVersion("<html>Not Found</html>\n" + Hash, out _, out _));
        Assert.False(UpdateCheck.ReadVersion("v0.1.0-700c3918\n" + Hash.Substring(1), out _, out _));
        Assert.False(UpdateCheck.ReadVersion("v0.1.0-700c3918\n" + Hash.Replace('0', 'G'), out _, out _));
        Assert.False(UpdateCheck.ReadVersion(null, out _, out _));
    }

    [Fact]
    public void A_hash_is_written_as_the_file_has_it()
    {
        Assert.Equal("00FF7A", UpdateCheck.Hex(new byte[] { 0, 255, 122 }));
    }
}
