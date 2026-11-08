using SummerSync.Core.Logging;
using SummerSync.Core.World;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.World;

/// <summary>Checksums by section, and the partial resync of what differs (Fase 8).</summary>
public class ChecksumReplicationTests
{
    /// <summary>A section over a dictionary of things, standing for what a game shows.</summary>
    private sealed class FakeSection(byte id, string name) : ICheckSection
    {
        public Dictionary<ulong, ulong> Things { get; } = new();

        public List<ulong> FixedHere { get; } = new();

        public List<(byte Player, List<ulong> Keys)> FixedFor { get; } = new();

        public byte Id { get; } = id;

        public string Name { get; } = name;

        public bool Ready { get; set; } = true;

        public bool CountsMissing { get; set; } = true;

        /// <summary>Places, compared within 0.3 m and sent as entries every round, instead of exact values.</summary>
        public bool Places { get; set; }

        public bool SendsEntries => Places;

        public void Read(List<CheckEntry> entries)
        {
            foreach (KeyValuePair<ulong, ulong> thing in Things)
            {
                entries.Add(new CheckEntry(thing.Key, thing.Value));
            }
        }

        public ulong Coarse(ulong value) => Places ? CheckPlace.Coarse(value, 1f) : value;

        public bool Same(ulong host, ulong here) => Places ? CheckPlace.Near(host, here, 0.3f) : host == here;

        public void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere)
        {
            FixedHere.AddRange(different);
            FixedHere.AddRange(onlyHost);
            FixedHere.AddRange(onlyHere);
        }

        public void FixFor(byte player, IList<ulong> keys) => FixedFor.Add((player, keys.ToList()));
    }

    private sealed class Machine
    {
        public Machine(SessionNet.Node node, params FakeSection[] sections)
        {
            Node = node;
            Checks = new ChecksumReplication(node.Session, node.Logger);
            foreach (FakeSection section in sections)
            {
                Checks.Add(section);
            }

            Sections = sections;
            Action<double>? earlier = node.BeforeUpdate;
            node.BeforeUpdate = time =>
            {
                earlier?.Invoke(time);
                Checks.Update(time);
            };
        }

        public SessionNet.Node Node { get; }

        public ChecksumReplication Checks { get; }

        public FakeSection[] Sections { get; }

        public IEnumerable<string> Log => Node.Log.Where(e => e.Category == LogCategory.Net).Select(e => e.Message);
    }

    private static (SessionNet Net, Machine Host, Machine Client) Pair(Action<FakeSection, FakeSection>? fill = null, bool places = false)
    {
        var net = new SessionNet();
        var hostSection = new FakeSection(1, "objects") { Places = places };
        var clientSection = new FakeSection(1, "objects") { Places = places };
        fill?.Invoke(hostSection, clientSection);
        var host = new Machine(net.Host, hostSection);
        var client = new Machine(net.AddClient("Julian"), clientSection);
        return (net, host, client);
    }

    [Fact]
    public void Worlds_that_agree_are_checked_and_nothing_is_asked()
    {
        var (net, host, client) = Pair((h, c) =>
        {
            for (ulong i = 1; i <= 20; i++)
            {
                h.Things[i] = i * 7;
                c.Things[i] = i * 7;
            }
        });

        net.Run(35);

        Assert.True(host.Checks.Rounds >= 3);
        Assert.True(client.Checks.Rounds >= 3);
        Assert.Equal(0, client.Checks.Asked);
        Assert.Empty(client.Sections[0].FixedHere);
    }

    [Fact]
    public void A_difference_seen_twice_is_asked_for_and_only_what_differs_is_set_right()
    {
        var (net, host, client) = Pair((h, c) =>
        {
            for (ulong i = 1; i <= 150; i++)
            {
                h.Things[i] = i;
                c.Things[i] = i;
            }

            // Forced here: one thing in another state, one the host does not have, one missing here.
            c.Things[42] = 4242;
            c.Things[999] = 1;
            c.Things.Remove(7);
        });

        // One round is not enough: a change on its way would differ once.
        net.Run(15);
        Assert.Equal(0, client.Checks.Asked);

        net.Run(12);

        Assert.Equal(1, client.Checks.Asked);
        Assert.Equal(new ulong[] { 42, 7, 999 }, client.Sections[0].FixedHere);
        (byte player, List<ulong> keys) = Assert.Single(host.Sections[0].FixedFor);
        Assert.Equal(client.Node.Session.LocalPlayerId, player);
        Assert.Equal(new ulong[] { 42, 7 }, keys);
        Assert.Equal(3, client.Checks.Fixed);
        Assert.Contains(client.Log, m => m.StartsWith("Checksums: 'objects' differed from the host's in 3 entr(ies) (1 different, 1 only the host's, 1 only here;"));
    }

    [Fact]
    public void A_difference_that_goes_away_by_itself_is_not_asked_for()
    {
        var (net, host, client) = Pair((h, c) =>
        {
            h.Things[1] = 10;
            c.Things[1] = 11;
        });

        net.Run(15);
        client.Sections[0].Things[1] = 10;
        net.Run(30);

        Assert.Equal(0, client.Checks.Asked);
    }

    [Fact]
    public void Places_a_few_centimetres_apart_are_the_same()
    {
        var (net, host, client) = Pair(
            (h, c) =>
            {
                h.Things[1] = CheckPlace.Pack(9.95f, 0.5f, 3f);
                c.Things[1] = CheckPlace.Pack(10.05f, 0.5f, 3f);
            },
            places: true);

        net.Run(35);

        Assert.Empty(client.Sections[0].FixedHere);
        Assert.Empty(host.Sections[0].FixedFor);
        Assert.Equal(0, client.Checks.Asked);
    }

    [Fact]
    public void A_thing_moved_half_a_metre_is_set_right_the_second_round_it_differs()
    {
        var (net, host, client) = Pair(
            (h, c) =>
            {
                h.Things[1] = CheckPlace.Pack(5f, 0.5f, 3f);
                c.Things[1] = CheckPlace.Pack(5f, 0.5f, 3.6f);
                h.Things[2] = CheckPlace.Pack(8f, 0.5f, 3f);
                c.Things[2] = CheckPlace.Pack(8f, 0.5f, 3f);
            },
            places: true);

        net.Run(15);
        Assert.Empty(client.Sections[0].FixedHere);

        net.Run(10);

        Assert.Equal(new ulong[] { 1 }, client.Sections[0].FixedHere);
        Assert.Equal(new ulong[] { 1 }, Assert.Single(host.Sections[0].FixedFor).Keys);
    }

    [Fact]
    public void A_place_on_its_way_is_not_set_right()
    {
        var (net, host, client) = Pair(
            (h, c) =>
            {
                h.Things[1] = CheckPlace.Pack(5f, 0.5f, 3f);
                c.Things[1] = CheckPlace.Pack(5f, 0.5f, 3f);
            },
            places: true);

        net.Run(12);

        // The host's thing was moved just before a round; the client gets there before the next.
        host.Sections[0].Things[1] = CheckPlace.Pack(5f, 0.5f, 9f);
        net.Run(9);
        client.Sections[0].Things[1] = CheckPlace.Pack(5f, 0.5f, 9f);
        net.Run(20);

        Assert.Empty(client.Sections[0].FixedHere);
    }

    [Fact]
    public void A_section_that_lets_things_come_and_go_counts_only_what_both_have()
    {
        var (net, host, client) = Pair((h, c) =>
        {
            h.CountsMissing = false;
            c.CountsMissing = false;
            h.Things[1] = 1;
            h.Things[2] = 2;
            c.Things[2] = 3;
            c.Things[3] = 3;
        });

        net.Run(25);

        Assert.Equal(new ulong[] { 2 }, client.Sections[0].FixedHere);
    }

    [Fact]
    public void A_client_whose_world_is_not_ready_does_not_compare()
    {
        var (net, host, client) = Pair((h, c) => h.Things[1] = 1);
        client.Sections[0].Ready = false;

        net.Run(35);

        Assert.Equal(0, client.Checks.Rounds);
        Assert.Equal(0, client.Checks.Asked);
    }

    [Fact]
    public void A_place_survives_packing()
    {
        ulong packed = CheckPlace.Pack(-1551.33f, 3.8f, 1181.92f);
        CheckPlace.Unpack(packed, out float x, out float y, out float z);
        Assert.Equal(-1551.35f, x, 2);
        Assert.Equal(3.8f, y, 2);
        Assert.Equal(1181.9f, z, 2);
        Assert.True(CheckPlace.Near(packed, CheckPlace.Pack(-1551.3f, 3.8f, 1182f), 0.3f));
        Assert.False(CheckPlace.Near(packed, CheckPlace.Pack(-1551.3f, 3.8f, 1183f), 0.3f));
    }
}
