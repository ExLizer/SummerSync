using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

/// <summary>The guests' own state, kept by the host with its save (Fase 8).</summary>
public class GuestReplicationTests
{
    private static GuestRecord Record(string key, float hunger, float x)
    {
        var record = new GuestRecord { Key = key, Name = "Julian", HasPlace = true, X = x, Y = 0.5f, Z = -3.25f, Yaw = 90f };
        record.Values["PlayerHunger"] = hunger;
        record.Values["PlayerThirst"] = 12.5f;
        record.Values["PlayerKeyHome"] = 1f;
        return record;
    }

    private sealed class Machine
    {
        public Machine(SessionNet.Node node)
        {
            Node = node;
            Guests = new GuestReplication(node.Session, node.Logger);
            Guests.RestoreWanted += record =>
            {
                Restores++;
                Restored = record;
            };
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Guests.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public GuestReplication Guests { get; }

        public int Restores { get; private set; }

        public GuestRecord? Restored { get; private set; }
    }

    [Fact]
    public void The_book_reads_what_it_writes()
    {
        var first = Record("76561198000000001", 33.25f, 12f);
        var second = new GuestRecord { Key = "D0C", Name = "a|friend;with=odd" };
        second.Values["PlayerUrine"] = 0.5f;

        List<GuestRecord> read = GuestBook.Read(GuestBook.Write(new[] { first, second }));

        Assert.Equal(2, read.Count);
        Assert.Equal("76561198000000001", read[0].Key);
        Assert.True(read[0].HasPlace);
        Assert.Equal(12f, read[0].X);
        Assert.Equal(-3.25f, read[0].Z);
        Assert.Equal(33.25f, read[0].Values["PlayerHunger"]);
        Assert.Equal(3, read[0].Values.Count);
        Assert.False(read[1].HasPlace);
        Assert.Equal("a friend with odd", read[1].Name);
        Assert.Equal(0.5f, read[1].Values["PlayerUrine"]);
    }

    [Fact]
    public void A_line_that_does_not_read_is_left_out()
    {
        List<GuestRecord> read = GuestBook.Read("# comment\nnot a record\n|nokey|||||\nk|n|1|2|3|4|PlayerHunger=x;PlayerThirst=2\n");

        GuestRecord only = Assert.Single(read);
        Assert.Equal("k", only.Key);
        Assert.False(only.Values.ContainsKey("PlayerHunger"));
        Assert.Equal(2f, only.Values["PlayerThirst"]);
    }

    [Fact]
    public void A_guest_coming_into_the_hosts_world_takes_up_what_the_save_has_of_it()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        host.Guests.LoadSaved(new[] { Record("guest", 40f, 7f) });
        var client = new Machine(net.AddClient("Julian"));
        net.Run(1);

        // Its world was loaded from the save: it comes with the save's player's needs.
        client.Guests.Report(Record("guest", 3f, -8f), true);
        net.Run(1);

        Assert.Equal(1, client.Restores);
        Assert.Equal(40f, client.Restored!.Values["PlayerHunger"]);
        Assert.Equal(7f, client.Restored.X);

        // That first word is not taken as the guest's: the save keeps what it had until the guest says more.
        Assert.Equal(40f, host.Guests.Snapshot().Single().Values["PlayerHunger"]);
    }

    [Fact]
    public void What_a_guest_says_is_saved_with_the_hosts_game()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        var client = new Machine(net.AddClient("Julian"));
        net.Run(1);
        client.Guests.Report(Record("guest", 3f, -8f), true);
        net.Run(1);
        Assert.Null(client.Restored);
        Assert.Equal(1, client.Restores);

        client.Guests.Report(Record("guest", 55f, 21f), false);
        net.Run(1);
        client.Guests.Report(Record("guest", 56f, 22f), false);
        net.Run(1);

        GuestRecord saved = host.Guests.Snapshot().Single();
        Assert.Equal(56f, saved.Values["PlayerHunger"]);
        Assert.Equal(22f, saved.X);

        // It asks once a session; asking again changes nothing.
        client.Guests.Report(Record("guest", 1f, 1f), true);
        net.Run(1);
        Assert.Equal(1, client.Restores);
    }

    [Fact]
    public void A_guest_who_leaves_and_comes_back_takes_up_its_last_word()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        SessionNet.Node node = net.AddClient("Julian");
        var client = new Machine(node);
        net.Run(1);
        client.Guests.Report(Record("guest", 3f, -8f), true);
        client.Guests.Report(Record("guest", 70f, 30f), false);
        net.Run(1);
        node.Session.Close();
        net.Run(1);

        SessionNet.Node again = net.AddClient("Julian");
        var back = new Machine(again);
        net.Run(1);
        back.Guests.Report(Record("guest", 3f, -8f), true);
        net.Run(1);

        Assert.Equal(70f, back.Restored!.Values["PlayerHunger"]);
    }
}
