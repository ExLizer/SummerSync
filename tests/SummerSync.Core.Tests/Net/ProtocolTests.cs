using System.Text;
using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

public class ProtocolTests
{
    [Theory]
    [InlineData("Julian", 64, "Julian")]
    [InlineData("Julian", 3, "Jul")]
    [InlineData("ñandú", 2, "ñ")]
    [InlineData("ñandú", 1, "")]
    [InlineData("", 8, "")]
    [InlineData(null, 8, "")]
    public void Text_is_cut_to_fit_its_utf8_size(string? text, int maxBytes, string expected)
    {
        Assert.Equal(expected, Protocol.LimitText(text!, maxBytes));
    }

    [Fact]
    public void A_cut_never_leaves_half_of_a_surrogate_pair()
    {
        string text = "ab\U0001F697\U0001F697";

        string cut = Protocol.LimitText(text, 7);

        Assert.Equal("ab\U0001F697", cut);
        Assert.Equal(6, Encoding.UTF8.GetByteCount(cut));
    }

    // Steam persona names can be longer, in bytes, than what the handshake accepts.
    [Fact]
    public void A_player_with_a_very_long_name_can_still_connect()
    {
        string longName = new string('ñ', 60) + "\U0001F697";
        var net = new SessionNet();
        SessionNet.Node client = net.AddClient(longName);

        net.Run(1);

        Assert.Equal(SessionState.Connected, client.Session.State);
        string seenByHost = net.Host.Session.Players.Single(p => p.Id == 1).Name;
        Assert.Equal(new string('ñ', 32), seenByHost);
        Assert.Equal(seenByHost, client.Session.Players.Single(p => p.IsLocal).Name);
    }
}
