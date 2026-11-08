using System.Reflection;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Players;

public class ChatReplicationTests
{
    private sealed class Chatter
    {
        private readonly List<ChatLine> lines = new();

        public Chatter(SessionNet.Node node)
        {
            Node = node;
            Chat = new ChatReplication(node.Session, node.Logger);
            node.BeforeUpdate = time => Chat.Update(time);
        }

        public SessionNet.Node Node { get; }

        public ChatReplication Chat { get; }

        /// <summary>Every line so far, as "Kind Name: text" (with a star for this player's own).</summary>
        public List<string> Lines
        {
            get
            {
                Chat.Take(lines);
                return lines.Select(line => line.Kind + " " + line.Name + (line.IsOwn ? "*" : string.Empty) + (line.Text.Length > 0 ? ": " + line.Text : string.Empty)).ToList();
            }
        }

        public List<string> Said => Lines.Where(line => line.StartsWith("Said ", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void What_anyone_writes_reaches_everyone_once_with_their_name()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, JitterMs = 20, LossPercent = 10 });
        var host = new Chatter(net.Host);
        var ana = new Chatter(net.AddClient("Ana"));
        var beto = new Chatter(net.AddClient("Beto"));
        net.Run(2);

        Assert.Equal(ChatSayResult.Sent, ana.Chat.Say("hola, estoy en el garaje"));
        net.Run(1);
        Assert.Equal(ChatSayResult.Sent, host.Chat.Say("voy para allá"));
        net.Run(1);
        Assert.Equal(ChatSayResult.Sent, beto.Chat.Say("traigan cerveza"));
        net.Run(2);

        string[] expected = { "hola, estoy en el garaje", "voy para allá", "traigan cerveza" };
        Assert.Equal(new[] { "Said Ana*: " + expected[0], "Said Host: " + expected[1], "Said Beto: " + expected[2] }, ana.Said);
        Assert.Equal(new[] { "Said Ana: " + expected[0], "Said Host*: " + expected[1], "Said Beto: " + expected[2] }, host.Said);
        Assert.Equal(new[] { "Said Ana: " + expected[0], "Said Host: " + expected[1], "Said Beto*: " + expected[2] }, beto.Said);
        Assert.Equal(0, host.Chat.Malformed + ana.Chat.Malformed + beto.Chat.Malformed);
    }

    [Fact]
    public void Players_coming_and_going_are_lines_and_who_was_there_is_told_apart()
    {
        var net = new SessionNet();
        var host = new Chatter(net.Host);
        var ana = new Chatter(net.AddClient("Ana"));
        net.Run(2);
        SessionNet.Node betoNode = net.AddClient("Beto");
        var beto = new Chatter(betoNode);
        net.Run(2);
        betoNode.Session.Close();
        net.Run(2);

        Assert.Equal(new[] { "Joined Ana", "Joined Beto", "Left Beto" }, host.Lines);
        Assert.Equal(new[] { "Present Host", "Joined Beto", "Left Beto" }, ana.Lines);
        Assert.Equal(new[] { "Present Host", "Present Ana" }, beto.Lines);
    }

    [Fact]
    public void A_line_is_one_line_of_plain_text_cut_to_its_length()
    {
        Assert.Equal("a b c", ChatReplication.Clean("  a\tb\nc \r\n"));
        Assert.Equal("ab", ChatReplication.Clean("a\u0000\u0007b\u001B"));
        Assert.Equal(string.Empty, ChatReplication.Clean(" \n\t "));
        Assert.Equal(string.Empty, ChatReplication.Clean(null!));
        Assert.Equal("äöå ñ ¿qué?", ChatReplication.Clean("äöå ñ ¿qué?"));

        string longest = ChatReplication.Clean(new string('x', 500));
        Assert.Equal(ChatReplication.MaxCharacters, longest.Length);

        // A character that takes two is not cut in half.
        string emoji = new string('x', ChatReplication.MaxCharacters - 1) + "\U0001F600" + "tail";
        string cut = ChatReplication.Clean(emoji);
        Assert.Equal(ChatReplication.MaxCharacters - 1, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[cut.Length - 1]));
    }

    [Fact]
    public void Nothing_is_sent_for_an_empty_line_or_without_a_session()
    {
        var net = new SessionNet();
        var host = new Chatter(net.Host);
        var ana = new Chatter(net.AddClient("Ana"));
        Assert.Equal(ChatSayResult.NotConnected, ana.Chat.Say("too early"));
        net.Run(2);

        Assert.Equal(ChatSayResult.Empty, ana.Chat.Say("   "));
        net.Run(1);

        Assert.Empty(host.Said);
        Assert.Empty(ana.Said);
        Assert.Equal(0, ana.Chat.Said);
    }

    [Fact]
    public void A_player_who_writes_too_fast_waits_and_the_host_holds_everyone_to_it()
    {
        var net = new SessionNet();
        var host = new Chatter(net.Host);
        var ana = new Chatter(net.AddClient("Ana"));
        net.Run(2);

        var results = new List<ChatSayResult>();
        for (int i = 0; i < ChatReplication.Burst + 2; i++)
        {
            results.Add(ana.Chat.Say("line " + i));
        }

        Assert.Equal(Enumerable.Repeat(ChatSayResult.Sent, ChatReplication.Burst).Concat(new[] { ChatSayResult.TooFast, ChatSayResult.TooFast }), results);
        net.Run(ChatReplication.SecondsPerLine * 1.5);
        Assert.Equal(ChatSayResult.Sent, ana.Chat.Say("after a pause"));
        net.Run(1);

        Assert.Equal(ChatReplication.Burst + 1, host.Said.Count);
        Assert.Equal(0, host.Chat.Dropped);
    }

    [Fact]
    public void A_line_to_pass_on_that_reaches_a_client_is_refused()
    {
        var net = new SessionNet();
        _ = new Chatter(net.Host);
        var ana = new Chatter(net.AddClient("Ana"));
        net.Run(2);

        byte[] wrong = { ChatWire.MessageSay, 2, (byte)'h', (byte)'i' };
        net.Host.Session.Send(Protocol.Broadcast, wrong, 0, wrong.Length, true);
        net.Run(1);

        Assert.Equal(1, ana.Chat.Malformed);
        Assert.Empty(ana.Said);
    }

    [Fact]
    public void After_the_session_nothing_is_heard()
    {
        var net = new SessionNet();
        var host = new Chatter(net.Host);
        var ana = new Chatter(net.AddClient("Ana"));
        net.Run(2);
        int before = host.Lines.Count;

        host.Chat.Detach();
        Assert.Equal(ChatSayResult.NotConnected, host.Chat.Say("anyone?"));
        Assert.Equal(ChatSayResult.Sent, ana.Chat.Say("hello?"));
        net.Run(1);

        Assert.Equal(before, host.Lines.Count);
    }

    [Fact]
    public void The_chats_messages_follow_the_last_ones_and_no_two_messages_share_a_number()
    {
        Assert.Equal(115, ChatWire.MessageSay);
        Assert.Equal(116, ChatWire.MessageLine);

        var owners = new Dictionary<byte, string>();
        var clashes = new List<string>();
        foreach (Type type in typeof(ChatWire).Assembly.GetTypes())
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!field.IsLiteral || field.FieldType != typeof(byte) || !field.Name.StartsWith("Message", StringComparison.Ordinal))
                {
                    continue;
                }

                byte value = (byte)field.GetRawConstantValue()!;
                if (value < Protocol.FirstUserMessage)
                {
                    continue;
                }

                string name = type.Name + "." + field.Name;
                if (owners.TryGetValue(value, out string? other))
                {
                    clashes.Add(value + ": " + other + " and " + name);
                }
                else
                {
                    owners[value] = name;
                }
            }
        }

        Assert.Empty(clashes);
    }
}
