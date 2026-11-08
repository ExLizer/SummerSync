using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class DentReplicationTests
{
    // A machine that hears the dents and, on the host, who asks for them.
    private sealed class Listener
    {
        public Listener(SessionNet.Node node)
        {
            Node = node;
            Dents = new DentReplication(node.Session, node.Logger);
            Dents.DentWanted += dent => Heard.Add(dent);
            Dents.JournalWanted += player => Asked.Add(player);
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Dents.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public DentReplication Dents { get; }

        public List<Dent> Heard { get; } = new();

        public List<byte> Asked { get; } = new();
    }

    private static Dent Blow(string target, float strength, params (float X, float Y, float Z)[] points) => new()
    {
        Target = target,
        Strength = strength,
        Contacts = points.Select(p => new DentContact { X = p.X, Y = p.Y, Z = p.Z, NX = 0f, NY = 0f, NZ = -1f }).ToArray(),
    };

    [Fact]
    public void A_dent_made_by_a_client_is_made_everywhere_else_with_who_made_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, LossPercent = 5 });
        var host = new Listener(net.Host);
        var driver = new Listener(net.AddClient("Julian"));
        var other = new Listener(net.AddClient("Second"));
        net.Run(1);

        Assert.True(driver.Dents.Tell(Blow("HAYOSIKO(1500kg, 250)#0", 400f, (0.8f, 0.5f, 2.1f), (0.7f, 0.4f, 2.2f))));
        net.Run(1);

        Dent heard = Assert.Single(host.Heard);
        Assert.Equal("HAYOSIKO(1500kg, 250)#0", heard.Target);
        Assert.Equal(400f, heard.Strength);
        Assert.Equal(2, heard.Contacts.Length);
        Assert.Equal(2.2f, heard.Contacts[1].Z);
        Assert.Equal(-1f, heard.Contacts[0].NZ);
        Assert.Equal(driver.Node.Session.LocalPlayerId, heard.Player);
        Assert.False(heard.FromJournal);
        Dent passed = Assert.Single(other.Heard);
        Assert.Equal(driver.Node.Session.LocalPlayerId, passed.Player);
        Assert.Equal(0.8f, passed.Contacts[0].X);

        // The game that made it does not hear it back: it would dent twice.
        Assert.Empty(driver.Heard);
    }

    [Fact]
    public void Dents_arrive_in_the_order_they_were_made()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 80, JitterMs = 40, LossPercent = 10 });
        var host = new Listener(net.Host);
        var client = new Listener(net.AddClient("Julian"));
        net.Run(1);

        for (int i = 0; i < 20; i++)
        {
            host.Dents.Tell(Blow("FERNDALE(1630kg)/MESH/muscle_body#0", 100f + i, (i * 0.1f, 0f, 0f)));
        }

        net.Run(3);

        Assert.Equal(Enumerable.Range(0, 20).Select(i => 100f + i), client.Heard.Select(d => d.Strength));
    }

    [Fact]
    public void A_client_that_asks_gets_the_hosts_journal_marked_as_such()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50 });
        var host = new Listener(net.Host);
        var late = new Listener(net.AddClient("Late"));
        net.Run(1);

        Assert.True(late.Dents.AskJournal());
        net.Run(0.5);
        byte asker = Assert.Single(host.Asked);
        Assert.Equal(late.Node.Session.LocalPlayerId, asker);

        Dent kept = Blow("SATSUMA(557kg, 248)/Body/car body(xxxxx)#0", 250f, (0.1f, 0.2f, 0.3f));
        kept.Player = 0;
        Assert.True(host.Dents.TellTo(asker, kept));
        net.Run(0.5);

        Dent heard = Assert.Single(late.Heard);
        Assert.True(heard.FromJournal);
        Assert.Equal(0.3f, heard.Contacts[0].Z);

        // The host's own copy of it is untouched.
        Assert.False(kept.FromJournal);

        // Only the host is asked, and only a client asks.
        Assert.False(host.Dents.AskJournal());
    }

    [Fact]
    public void A_crash_sound_has_no_contacts_and_a_dent_must_have_some()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Listener(net.Host);
        var client = new Listener(net.AddClient("Julian"));
        net.Run(1);

        Assert.True(client.Dents.Tell(new Dent { Target = "HAYOSIKO(1500kg, 250)", Strength = 90f, Contacts = Array.Empty<DentContact>(), Flags = DentWire.FlagSound }));
        Assert.False(client.Dents.Tell(new Dent { Target = "HAYOSIKO(1500kg, 250)#0", Strength = 90f, Contacts = Array.Empty<DentContact>() }));
        Assert.False(client.Dents.Tell(Blow("HAYOSIKO(1500kg, 250)#0", float.NaN, (0f, 0f, 0f))));
        Assert.False(client.Dents.Tell(Blow(string.Empty, 50f, (0f, 0f, 0f))));
        Assert.False(client.Dents.Tell(Blow("x", 50f, Enumerable.Range(0, DentWire.MaxContacts + 1).Select(i => (0f, 0f, (float)i)).ToArray())));
        net.Run(0.5);

        Dent sound = Assert.Single(host.Heard);
        Assert.True(sound.SoundOnly);
        Assert.Empty(sound.Contacts);

        // A sound is news, never part of the journal.
        Assert.False(host.Dents.TellTo(client.Node.Session.LocalPlayerId, sound));
    }

    [Fact]
    public void A_repair_has_no_contacts_and_travels_like_a_dent()
    {
        var repair = new Dent { Target = "SATSUMA/Body/car body(xxxxx)#0", Flags = DentWire.FlagRepair, Contacts = new DentContact[0] };
        Assert.True(DentReplication.Valid(repair));
        Assert.True(repair.Repair);
        Assert.False(repair.SoundOnly);
        Assert.False(new Dent { Target = "a", Contacts = new DentContact[0] }.Repair);

        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new Listener(net.Host);
        var shop = new Listener(net.AddClient("Julian"));
        var other = new Listener(net.AddClient("Second"));
        net.Run(1);

        Assert.True(shop.Dents.Tell(repair));
        net.Run(1);

        Assert.True(Assert.Single(host.Heard).Repair);
        Dent passed = Assert.Single(other.Heard);
        Assert.True(passed.Repair);
        Assert.Empty(passed.Contacts);

        // From the host's journal, to whoever joins: still a repair, and marked as such.
        Assert.True(host.Dents.TellTo(other.Node.Session.LocalPlayerId, host.Heard[0]));
        net.Run(1);
        Dent kept = other.Heard[1];
        Assert.True(kept.Repair);
        Assert.True(kept.FromJournal);
    }

    [Fact]
    public void A_client_cannot_pass_a_journal_on_nor_send_numbers_that_are_not()
    {
        var net = new SessionNet(new NetworkConditions());
        var host = new Listener(net.Host);
        var client = new Listener(net.AddClient("Julian"));
        var other = new Listener(net.AddClient("Second"));
        net.Run(1);

        // A client says its dent is from the journal: it goes on as news.
        var writer = new PacketWriter(200);
        writer.WriteByte(DentWire.MessageDent);
        writer.WriteByte(0);
        writer.WriteByte(DentWire.FlagJournal);
        writer.WriteString("HAYOSIKO(1500kg, 250)#0");
        writer.WriteSingle(300f);
        writer.WriteByte(1);
        for (int i = 0; i < 6; i++)
        {
            writer.WriteSingle(i);
        }

        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);

        // And one with a contact that is not a number.
        writer.Reset();
        writer.WriteByte(DentWire.MessageDent);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteString("HAYOSIKO(1500kg, 250)#0");
        writer.WriteSingle(300f);
        writer.WriteByte(1);
        writer.WriteSingle(float.PositiveInfinity);
        for (int i = 0; i < 5; i++)
        {
            writer.WriteSingle(i);
        }

        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(0.5);

        Dent heard = Assert.Single(host.Heard);
        Assert.False(heard.FromJournal);
        Assert.False(Assert.Single(other.Heard).FromJournal);
        Assert.Equal(1, host.Dents.Malformed);
        Assert.Equal(107, DentWire.MessageDent);
        Assert.Equal(108, DentWire.MessageAsk);
    }
}
