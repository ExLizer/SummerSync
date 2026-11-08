using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.World;

namespace SummerSync.Core.Tests.World;

public class SwitchReplicationTests
{
    private static readonly string[] HouseDoors =
    {
        "YARD/Building/BATHROOM/DoorBathroom", "YARD/Building/BEDROOM1/DoorBedroom1", "YARD/Building/KITCHEN/DoorPantry",
        "YARD/Building/LIVINGROOM/DoorFront", "YARD/Building/LIVINGROOM/DoorWC", "YARD/Building/SAUNA/DoorSauna",
        "YARD/Building/MIDDLEROOM/DoorMiddleroom", "YARD/Building/MIDDLEROOM/DoorRear", "YARD/Building/BEDROOM2/DoorBedroom2",
    };

    private const string Sauna = "YARD/Building/SAUNA/DoorSauna";
    private const string Garage = "YARD/Building/GARAGE/DoorGarage";

    // Another world's list: without the sauna's door, with a garage's, and in another order, so that every door both
    // have is somewhere else in it.
    private static readonly string[] OtherDoors =
        HouseDoors.Where(door => door != Sauna).Append(Garage).OrderBy(door => door, StringComparer.Ordinal).ToArray();

    /// <summary>One machine's doors: what the game shows, kept in step with the replication the way the mod does.</summary>
    private sealed class House
    {
        public House(SessionNet.Node node, string[]? doors = null, params int[] open)
        {
            Node = node;
            Replication = new SwitchReplication(node.Session, node.Logger);
            node.BeforeUpdate = time =>
            {
                Replication.Update(time);
                while (Replication.TryTakeChange(out int index, out bool value))
                {
                    Doors[index] = value;
                    Applied.Add((index, value));
                }
            };
            if (doors != null)
            {
                Load(doors, open);
            }
        }

        public SessionNet.Node Node { get; }

        public SwitchReplication Replication { get; }

        public string[] Names { get; private set; } = Array.Empty<string>();

        public bool[] Doors { get; private set; } = Array.Empty<bool>();

        /// <summary>Every change the network asked the game to show.</summary>
        public List<(int Index, bool Value)> Applied { get; } = new();

        public void Load(string[] doors, params int[] open)
        {
            Names = doors;
            Doors = new bool[doors.Length];
            foreach (int index in open)
            {
                Doors[index] = true;
            }

            Replication.SetWorld(doors, Doors);
        }

        /// <summary>The local player uses a door.</summary>
        public void Use(int index)
        {
            Doors[index] = !Doors[index];
            Replication.SetLocal(index, Doors[index]);
        }

        public void Use(string name) => Use(IndexOf(name));

        public int IndexOf(string name) => Array.IndexOf(Names, name);

        public bool IsOpen(string name) => Doors[IndexOf(name)];

        /// <summary>The doors that are open here, by name, in the order of their names.</summary>
        public string Open => string.Join(",", Names.Where(IsOpen).OrderBy(name => name, StringComparer.Ordinal).Select(name => name.Substring(name.LastIndexOf('/') + 1)));

        public string State => string.Concat(Doors.Select(open => open ? '1' : '0'));

        public int Heard(byte type) => Node.Messages.Count(message => message.Data[0] == type);
    }

    [Fact]
    public void A_client_starts_with_the_doors_as_the_host_has_them()
    {
        var net = new SessionNet();
        var host = new House(net.Host, HouseDoors, 2, 8);
        var client = new House(net.AddClient("Julian"), HouseDoors, 0);

        net.Run(1);

        Assert.True(host.Replication.Shared);
        Assert.True(client.Replication.Shared);
        Assert.False(client.Replication.Mismatch);
        Assert.Equal("001000001", host.State);
        Assert.Equal("001000001", client.State);

        // Only the three that differed were touched.
        Assert.Equal(new[] { (0, false), (2, true), (8, true) }, client.Applied.OrderBy(change => change.Index));
        Assert.Empty(host.Applied);
        Assert.Contains(client.Node.Log, e => e.Message == "Doors and switches: 9 shared with the host, 3 set to the host's state.");

        // Each by the name it was handed over with (what "ss doors" writes for netprobe).
        Assert.Equal(HouseDoors[8], client.Replication.NameOf(8));
    }

    [Fact]
    public void A_door_opened_by_anyone_opens_for_everyone_once()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 80, JitterMs = 20, LossPercent = 5 });
        var host = new House(net.Host, HouseDoors);
        var first = new House(net.AddClient("Julian"), HouseDoors);
        var second = new House(net.AddClient("Friend"), HouseDoors);
        net.Run(2);

        host.Use(4);
        net.Run(1);
        first.Use(1);
        net.Run(1);
        second.Use(4);
        net.Run(1);

        Assert.All(new[] { host, first, second }, house => Assert.Equal("010000000", house.State));

        // Nobody was asked to show a change they made themselves, and nobody was asked twice.
        Assert.Equal(new[] { (1, true), (4, false) }, host.Applied);
        Assert.Equal(new[] { (4, true), (4, false) }, first.Applied);
        Assert.Equal(new[] { (4, true), (1, true) }, second.Applied);
    }

    [Fact]
    public void Two_players_using_the_same_door_at_the_same_moment_end_up_agreeing()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 150 });
        var host = new House(net.Host, HouseDoors);
        var first = new House(net.AddClient("Julian"), HouseDoors);
        var second = new House(net.AddClient("Friend"), HouseDoors);
        net.Run(2);

        // All three reach for the handle of the closed door in the same frame.
        host.Use(3);
        first.Use(3);
        second.Use(3);
        net.Run(2);

        Assert.All(new[] { host, first, second }, house => Assert.Equal("000100000", house.State));

        // One opens it, the other closes it, each before hearing of the other.
        first.Use(3);
        second.Use(0);
        host.Use(3);
        net.Run(2);

        Assert.Equal(host.State, first.State);
        Assert.Equal(host.State, second.State);
        Assert.Equal('1', host.State[0]);
    }

    [Fact]
    public void A_client_with_one_switch_more_and_one_less_shares_all_the_others()
    {
        var net = new SessionNet();
        var host = new House(net.Host, HouseDoors, 2, 5, 8);
        var client = new House(net.AddClient("Julian"), OtherDoors, Array.IndexOf(OtherDoors, Garage));
        net.Run(1);

        Assert.True(client.Replication.Shared);
        Assert.True(client.Replication.Mismatch);
        Assert.Equal(8, client.Replication.SharedCount);
        Assert.Equal(1, client.Replication.OnlyHere);
        Assert.Equal(1, client.Replication.OnlyHost);
        Assert.True(client.Replication.IsOnlyHere(client.IndexOf(Garage)));
        Assert.False(client.Replication.IsOnlyHere(client.IndexOf(HouseDoors[2])));

        // Every door both have is as the host has it, wherever it is in this list; the garage's is as this game had it.
        foreach (string door in HouseDoors.Where(door => door != Sauna))
        {
            Assert.Equal(host.IsOpen(door), client.IsOpen(door));
        }

        Assert.Equal("DoorBedroom2,DoorGarage,DoorPantry", client.Open);
        Assert.Equal(new[] { (client.IndexOf(HouseDoors[8]), true), (client.IndexOf(HouseDoors[2]), true) }, client.Applied.OrderBy(change => change.Index));
        Assert.Equal(0, client.Replication.Malformed);

        // Each in its own places: the game side never sees the host's.
        Assert.Equal(Garage, client.Replication.NameOf(client.IndexOf(Garage)));
    }

    [Fact]
    public void Changes_flow_both_ways_when_the_lists_differ_and_each_keeps_what_only_it_has()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, JitterMs = 20, LossPercent = 5 });
        var host = new House(net.Host, HouseDoors);
        var other = new House(net.AddClient("Julian"), OtherDoors);
        var same = new House(net.AddClient("Friend"), HouseDoors);
        net.Run(2);

        host.Use("YARD/Building/LIVINGROOM/DoorFront");
        net.Run(1);
        other.Use("YARD/Building/BEDROOM1/DoorBedroom1");
        net.Run(1);

        // What only one world has stays there.
        other.Use(Garage);
        host.Use(Sauna);
        net.Run(1);

        Assert.Equal("DoorBedroom1,DoorFront,DoorSauna", host.Open);
        Assert.Equal("DoorBedroom1,DoorFront,DoorSauna", same.Open);
        Assert.Equal("DoorBedroom1,DoorGarage,DoorFront", other.Open);

        // Each was asked to show what someone else did, once, in its own places.
        Assert.Equal(new[] { (other.IndexOf("YARD/Building/LIVINGROOM/DoorFront"), true) }, other.Applied);
        Assert.Equal(new[] { (1, true) }, host.Applied);
        Assert.Equal(new[] { (3, true), (1, true), (5, true) }, same.Applied);

        // The one that is shared both ways closes from the other side too.
        same.Use("YARD/Building/BEDROOM1/DoorBedroom1");
        net.Run(1);
        Assert.False(other.IsOpen("YARD/Building/BEDROOM1/DoorBedroom1"));
        Assert.All(new[] { host, other, same }, house => Assert.Equal(0, house.Replication.Malformed));
    }

    [Fact]
    public void A_client_with_the_hosts_exact_list_keeps_the_fast_path()
    {
        var net = new SessionNet();
        var host = new House(net.Host, HouseDoors, 4);
        var other = new House(net.AddClient("Julian"), OtherDoors);
        var same = new House(net.AddClient("Friend"), HouseDoors);
        net.Run(1);

        Assert.True(same.Replication.Shared);
        Assert.False(same.Replication.Mismatch);
        Assert.Equal(9, same.Replication.SharedCount);
        Assert.Equal(0, same.Heard(SwitchWire.MessageNames));
        Assert.Equal(0, same.Heard(SwitchWire.MessageNamedStates));
        Assert.Equal(1, same.Heard(SwitchWire.MessageStates));
        Assert.Contains(same.Node.Log, e => e.Message == "Doors and switches: 9 shared with the host, 1 set to the host's state.");

        Assert.Equal(1, other.Heard(SwitchWire.MessageNames));
        Assert.Equal(1, other.Heard(SwitchWire.MessageNamedStates));
        Assert.Equal(0, other.Heard(SwitchWire.MessageStates));
    }

    [Fact]
    public void The_log_names_the_switches_only_one_side_has()
    {
        var net = new SessionNet();
        var host = new House(net.Host, HouseDoors, 1);
        var client = new House(net.AddClient("Julian"), OtherDoors);
        var renamed = new House(net.AddClient("Friend"), HouseDoors.Select(door => door.Replace("SAUNA", "Sauna")).ToArray());
        net.Run(1);

        Assert.Contains(
            client.Node.Log,
            e => e.Level == LogLevel.Warning
                && e.Message == "Doors and switches: the host's world has 9 and this one 9, not the same ones. The 8 both have are shared by name, 1 set to the host's state; "
                    + "only here (left as this game has them): 1 ('" + Garage + "'); only in the host's world: 1 ('" + Sauna + "').");
        Assert.Contains(
            renamed.Node.Log,
            e => e.Level == LogLevel.Warning && e.Message.Contains("only here (left as this game has them): 1 ('YARD/Building/Sauna/DoorSauna'); only in the host's world: 1 ('" + Sauna + "')"));
        Assert.Contains(host.Node.Log, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Player 1 has 9 door(s) and switch(es) and this world has 9, or not the same ones"));
        Assert.Contains(host.Node.Log, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Player 2 has 9 door(s) and switch(es) and this world has 9, or not the same ones"));
        Assert.Equal(8, renamed.Replication.SharedCount);
    }

    [Fact]
    public void A_long_list_goes_in_runs_and_every_name_both_have_is_found()
    {
        // As many as a world with every vehicle has, with long names that share their start, some of them not ASCII, and
        // two in a row that share half of a character.
        string[] hostList = Enumerable.Range(0, 700)
            .Select(i => "PERÄJÄRVI/Building/LOD/Doors/" + (i % 3 == 0 ? "Ovi🚪" : "Door") + "/Pivot/Handle/Use@0/" + (i / 10) + "/" + (i % 10) + "/2/7/16/0/1/" + new string('x', i % 60))
            .Concat(new[] { "Sign/🚪", "Sign/🚫" })
            .ToArray();
        string[] missing = hostList.Where((_, i) => i % 97 == 5).ToArray();
        string[] extra = { "SATSUMA(557kg, 248)/Body/pivot_door_left/door left(Clone)@0/1", "GIFU(750/450psi)/LOD/Dashboard/Knobs#beacon.0" };
        string[] clientList = hostList.Except(missing).Concat(extra).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var net = new SessionNet(new NetworkConditions { LatencyMs = 50, JitterMs = 10, LossPercent = 10 });
        var host = new House(net.Host, hostList, Enumerable.Range(0, hostList.Length).Where(i => i % 7 == 0).ToArray());
        var client = new House(net.AddClient("Julian"), clientList);
        net.Run(4);

        Assert.True(client.Replication.Shared);
        Assert.Equal(hostList.Length - missing.Length, client.Replication.SharedCount);
        Assert.Equal(extra.Length, client.Replication.OnlyHere);
        Assert.Equal(missing.Length, client.Replication.OnlyHost);
        Assert.True(client.Heard(SwitchWire.MessageNames) > 10);
        foreach (string name in clientList.Where(hostList.Contains))
        {
            Assert.Equal(host.IsOpen(name), client.IsOpen(name));
        }

        host.Use(hostList[691]);
        client.Use(hostList[692]);
        client.Use("Sign/🚫");
        net.Run(1);
        Assert.True(client.IsOpen(hostList[691]));
        Assert.True(host.IsOpen(hostList[692]));
        Assert.True(host.IsOpen("Sign/🚫"));
        Assert.True(client.IsOpen("Sign/🚪"));
        Assert.Equal(0, client.Replication.Malformed);
        Assert.Equal(0, host.Replication.Malformed);
    }

    [Fact]
    public void A_client_that_loads_the_hosts_list_after_another_shares_all_of_it()
    {
        var net = new SessionNet();
        var host = new House(net.Host, HouseDoors, 5);
        var client = new House(net.AddClient("Julian"), OtherDoors);
        net.Run(1);
        Assert.True(client.Replication.Mismatch);

        client.Replication.ClearWorld();
        client.Load(HouseDoors);
        net.Run(1);

        Assert.True(client.Replication.Shared);
        Assert.False(client.Replication.Mismatch);
        Assert.Equal(9, client.Replication.SharedCount);
        Assert.Equal("000001000", client.State);
        host.Use(0);
        client.Use(8);
        net.Run(1);
        Assert.Equal("100001001", host.State);
        Assert.Equal("100001001", client.State);
    }

    [Fact]
    public void Names_out_of_order_and_states_before_their_names_are_dropped()
    {
        var net = new SessionNet();
        var host = new House(net.Host, HouseDoors);
        var client = new House(net.AddClient("Julian"));
        net.Run(1);

        // A run that does not start the list, states of a list whose names did not all come, and names from a client.
        byte[] middle = { SwitchWire.MessageNames, 9, 0, 4, 0, 1, 0, 1, (byte)'a' };
        host.Node.Session.Send(1, middle, 0, middle.Length, true);
        byte[] start = { SwitchWire.MessageNames, 9, 0, 0, 0, 1, 0, 1, (byte)'a' };
        host.Node.Session.Send(1, start, 0, start.Length, true);
        byte[] early = { SwitchWire.MessageNamedStates, 9, 0, 0, 0 };
        host.Node.Session.Send(1, early, 0, early.Length, true);
        byte[] backwards = { SwitchWire.MessageNames, 9, 0, 0, 0, 1, 0, 1, (byte)'a' };
        client.Node.Session.Send(Protocol.HostPlayerId, backwards, 0, backwards.Length, true);
        net.Run(1);

        Assert.Equal(2, client.Replication.Malformed);
        Assert.Equal(1, host.Replication.Malformed);
        Assert.False(client.Replication.Shared);
    }

    [Fact]
    public void The_new_messages_take_99_and_100()
    {
        Assert.Equal(99, SwitchWire.MessageNames);
        Assert.Equal(100, SwitchWire.MessageNamedStates);
    }

    [Fact]
    public void A_client_that_loads_before_the_host_is_answered_when_the_host_has_loaded()
    {
        var net = new SessionNet();
        var host = new House(net.Host);
        var client = new House(net.AddClient("Julian"), HouseDoors, 5);
        net.Run(2);
        Assert.False(client.Replication.Shared);

        // Used before the host has a world: it will be overruled.
        client.Use(7);
        host.Load(HouseDoors, 0);
        net.Run(1);

        Assert.True(client.Replication.Shared);
        Assert.Equal("100000000", client.State);
        host.Use(6);
        client.Use(0);
        net.Run(1);
        Assert.Equal("000000100", host.State);
        Assert.Equal("000000100", client.State);
    }

    [Fact]
    public void A_world_that_is_loaded_again_starts_over()
    {
        var net = new SessionNet();
        var host = new House(net.Host, HouseDoors, 3);
        var client = new House(net.AddClient("Julian"), HouseDoors);
        net.Run(1);
        Assert.Equal("000100000", client.State);

        // The client's doors are all closed again after it reloaded; the host opened another meanwhile.
        client.Replication.ClearWorld();
        host.Use(4);
        net.Run(1);
        client.Applied.Clear();
        client.Load(HouseDoors);
        net.Run(1);

        Assert.True(client.Replication.Shared);
        Assert.Equal("000110000", client.State);
        Assert.Equal(new[] { (3, true), (4, true) }, client.Applied);
    }

    [Fact]
    public void Rubbish_is_counted_and_a_stranger_cannot_flip_anything()
    {
        var net = new SessionNet();
        var host = new House(net.Host, HouseDoors);
        var client = new House(net.AddClient("Julian"), HouseDoors);
        var outsider = new House(net.AddClient("Friend"));
        net.Run(1);

        byte[] beyond = { SwitchWire.MessageSet, 200, 0, 1 };
        client.Node.Session.Send(Protocol.HostPlayerId, beyond, 0, beyond.Length, true);
        byte[] cut = { SwitchWire.MessageSet, 1 };
        client.Node.Session.Send(Protocol.HostPlayerId, cut, 0, cut.Length, true);
        byte[] wrongWay = { SwitchWire.MessageStates, 1, 9, 0, 255, 255 };
        client.Node.Session.Send(Protocol.HostPlayerId, wrongWay, 0, wrongWay.Length, true);

        // This one has said nothing of a world, so the host does not listen to it.
        byte[] fromOutsider = { SwitchWire.MessageSet, 1, 0, 1 };
        outsider.Node.Session.Send(Protocol.HostPlayerId, fromOutsider, 0, fromOutsider.Length, true);
        net.Run(1);

        Assert.Equal(3, host.Replication.Malformed);
        Assert.Equal("000000000", host.State);
        Assert.Equal("000000000", client.State);
        Assert.DoesNotContain(host.Node.Log, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void The_hash_of_a_list_changes_with_its_names_their_order_and_their_number()
    {
        ulong hash = SwitchReplication.HashOf(HouseDoors);

        Assert.Equal(hash, SwitchReplication.HashOf(HouseDoors.ToArray()));
        Assert.NotEqual(hash, SwitchReplication.HashOf(HouseDoors.Reverse().ToArray()));
        Assert.NotEqual(hash, SwitchReplication.HashOf(HouseDoors.Take(8).ToArray()));
        Assert.NotEqual(SwitchReplication.HashOf(new[] { "ab", "c" }), SwitchReplication.HashOf(new[] { "a", "bc" }));
    }
}
