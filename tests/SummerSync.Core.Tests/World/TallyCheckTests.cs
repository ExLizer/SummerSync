using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

/// <summary>The tallies as a checksum section: what the host remembers against what a client heard last (Fase 8).</summary>
public class TallyCheckTests
{
    private sealed class Machine
    {
        public Machine(SessionNet.Node node)
        {
            Node = node;
            Tallies = new TallyReplication(node.Session, node.Logger);
            Checks = new ChecksumReplication(node.Session, node.Logger);
            Checks.Add(new TallyCheckSection(Tallies));
            Tallies.StateWanted += (name, values) => Counts[name] = values;
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Tallies.Update(time);
                Checks.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public TallyReplication Tallies { get; }

        public ChecksumReplication Checks { get; }

        public Dictionary<string, int[]> Counts { get; } = new();
    }

    [Fact]
    public void A_tally_a_client_heard_otherwise_is_told_again()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        var client = new Machine(net.AddClient("Julian"));
        net.Run(1);
        host.Tallies.Set("elec/unpaid", new[] { 7874 });
        host.Tallies.Set("elec/bill", new[] { 1 });
        client.Tallies.RequestAll();
        net.Run(1);
        Assert.Equal(7874, client.Counts["elec/unpaid"][0]);

        // Forced: the client hears another number than the host remembers, from nothing of the host's tallies.
        var writer = new PacketWriter(200);
        writer.WriteByte(TallyWire.MessageState);
        writer.WriteString("elec/unpaid");
        writer.WriteByte(1);
        writer.WriteVarUInt32(SpawnWire.ZigZag(1));
        host.Node.Session.Send(client.Node.Session.LocalPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(1);
        Assert.Equal(1, client.Counts["elec/unpaid"][0]);

        net.Run(30);

        Assert.Equal(7874, client.Counts["elec/unpaid"][0]);
        Assert.Equal(1, client.Checks.Asked);
        Assert.Equal(1, client.Checks.Fixed);
    }

    [Fact]
    public void Tallies_that_agree_are_not_asked_for()
    {
        var net = new SessionNet();
        var host = new Machine(net.Host);
        var client = new Machine(net.AddClient("Julian"));
        net.Run(1);
        host.Tallies.Set("mount/diskette", new[] { 1, 12, 0 });
        client.Tallies.RequestAll();
        net.Run(35);

        Assert.True(client.Checks.Rounds >= 2);
        Assert.Equal(0, client.Checks.Asked);
    }
}
