using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Objects;

/// <summary>
/// Things paired by what the game calls them in the save (Fase 8): a guest loads the host's last save while the host may
/// have played on, so the host's things may have moved far, been renamed, been made, or be gone.
/// </summary>
public class ObjectKeyTests
{
    private static readonly BodyPose Fridge = BodyPose.At(-10, 0.5f, 12);
    private static readonly BodyPose Table = BodyPose.At(-8, 0.9f, 10);
    private static readonly BodyPose Yard = BodyPose.At(30, 0.2f, -5);

    [Fact]
    public void A_thing_moved_far_and_renamed_by_the_hosts_game_pairs_by_its_key()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        host.AddKeyed("empty plastic can(itemx)", Yard, "juiceconcentrate0");
        host.AddKeyed("juice(itemx)", Table, "juiceconcentrate1");

        // The save: both cans full, the first on the table, the second in the fridge.
        var client = new FakeWorld(net.AddClient("Julian"));
        FakeWorld.Thing first = client.AddKeyed("juice(itemx)", Table, "juiceconcentrate0");
        FakeWorld.Thing second = client.AddKeyed("juice(itemx)", Fridge, "juiceconcentrate1");

        host.Ready();
        net.Run(2);

        Assert.Equal(2, client.Replication.Shared);
        Assert.Equal(0, client.Replication.MissingHere);
        Assert.Equal(host.Things[0].Shared.Id, first.Shared.Id);
        Assert.Equal(host.Things[1].Shared.Id, second.Shared.Id);

        // The nearest of the name would have crossed them over.
        Assert.True(first.Shared.Target.DistanceTo(Yard) < 0.01);
        Assert.True(second.Shared.Target.DistanceTo(Table) < 0.01);
    }

    [Fact]
    public void Two_things_with_different_keys_are_two_things_whatever_their_names()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        host.AddKeyed("beer case(itemx)", Table, "beercase0");
        var client = new FakeWorld(net.AddClient("Julian"));
        client.AddKeyed("beer case(itemx)", Table, "beercase1");

        host.Ready();
        net.Run(2);

        Assert.Equal(0, client.Replication.Shared);
        Assert.Equal(1, client.Replication.MissingHere);
        Assert.Equal(1, client.Replication.OnlyHere);
    }

    [Fact]
    public void A_thing_with_no_key_still_pairs_by_name_and_place()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, ("wood carrier(itemx)", Yard));
        host.AddKeyed("beer case(itemx)", Table, "beercase0");
        var client = new FakeWorld(net.AddClient("Julian"), ("wood carrier(itemx)", Yard.Moved(0.05, 0, 0)));
        client.AddKeyed("beer case(itemx)", Table, "beercase0");

        host.Ready();
        net.Run(2);

        Assert.Equal(2, client.Replication.Shared);
        Assert.Equal(0, client.Replication.OnlyHere);
    }

    [Fact]
    public void A_client_knows_when_the_hosts_whole_list_is_here_and_which_keys_it_has()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        for (int i = 0; i < 25; i++)
        {
            host.AddKeyed("bottle(itemx)", BodyPose.At(i, 0, 0), "bottle" + i);
        }

        // Made by the host's game since the save, and eaten there since the save.
        host.AddKeyed("beer case(itemx)", Table, "beercase1");
        var client = new FakeWorld(net.AddClient("Julian"));
        for (int i = 0; i < 25; i++)
        {
            client.AddKeyed("bottle(itemx)", BodyPose.At(i, 0, 0), "bottle" + i);
        }

        client.AddKeyed("pizza(itemx)", Fridge, "pizza0");
        Assert.False(client.Replication.ListComplete);

        host.Ready();
        net.Run(2);

        Assert.True(client.Replication.ListComplete);
        Assert.True(client.Replication.HostHasKey("bottle24"));
        Assert.True(client.Replication.HostHasKey("beercase1"));
        Assert.False(client.Replication.HostHasKey("pizza0"));
        Assert.False(client.Replication.HostHasKey(string.Empty));
        MissingThing missing = Assert.Single(client.Replication.MissingKeyed);
        Assert.Equal("beercase1", missing.Key);
        Assert.Equal("beer case(itemx)", missing.Name);
        Assert.True(missing.Pose.DistanceTo(Table) < 0.01);
        Assert.Contains(client.Log, m => m == "The host's whole list is here: 26 object(s) with a key from the save.");
    }

    [Fact]
    public void What_the_hosts_game_made_with_no_key_is_missing_here_and_told_apart_from_what_is_only_here()
    {
        var net = new SessionNet();

        // Two pieces of firewood split by the host since the save, one more far from the client's, and a can.
        var host = new FakeWorld(net.Host, ("firewood(Clone)", Yard), ("firewood(Clone)", Yard.Moved(0.3, 0, 0)), ("firewood(Clone)", Table));
        host.AddKeyed("beer case(itemx)", Table, "beercase0");
        var client = new FakeWorld(net.AddClient("Julian"), ("firewood(Clone)", Fridge.Moved(40, 0, 0)));
        client.AddKeyed("beer case(itemx)", Table, "beercase0");

        host.Ready();
        net.Run(2);

        IList<MissingThing> missing = client.Replication.MissingUnkeyed;
        Assert.All(missing, m => Assert.Equal("firewood(Clone)", m.Name));
        Assert.All(missing, m => Assert.Equal(string.Empty, m.Key));
        Assert.Equal(client.Replication.MissingHere, missing.Count);
        Assert.Empty(client.Replication.MissingKeyed);

        // What this world has of the name and the host's does not: so many of the host's are not to be made again.
        Assert.Equal(client.Replication.OnlyHere, client.Replication.CountOnlyHere("firewood(Clone)"));
        Assert.Equal(0, client.Replication.CountOnlyHere("beer case(itemx)"));
        Assert.Equal(2, missing.Count - client.Replication.CountOnlyHere("firewood(Clone)"));
    }

    [Fact]
    public void A_thing_the_hosts_world_loses_takes_its_key_with_it()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        FakeWorld.Thing pizza = host.AddKeyed("pizza(itemx)", Fridge, "pizza0");
        var client = new FakeWorld(net.AddClient("Julian"));
        client.AddKeyed("pizza(itemx)", Fridge, "pizza0");
        host.Ready();
        net.Run(2);
        Assert.True(client.Replication.HostHasKey("pizza0"));

        host.Remove(pizza);
        net.Run(1);

        Assert.False(client.Replication.HostHasKey("pizza0"));
    }

    [Fact]
    public void An_empty_world_is_a_whole_list_too()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        var client = new FakeWorld(net.AddClient("Julian"));
        client.Replication.RequestList();

        host.Ready();
        net.Run(2);

        Assert.True(client.Replication.ListComplete);
        Assert.Empty(client.Replication.MissingKeyed);
    }

    [Fact]
    public void A_host_still_loading_its_world_does_not_give_a_whole_list_until_it_is_ready()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        var client = new FakeWorld(net.AddClient("Julian"));
        client.AddKeyed("pizza(itemx)", Fridge, "pizza0");
        net.Run(2);

        // Nothing the host lacks can be taken as gone from its world yet.
        Assert.False(client.Replication.ListComplete);
        Assert.False(client.Replication.HostHasKey("pizza0"));

        host.AddKeyed("pizza(itemx)", Fridge, "pizza0");
        host.Ready();
        net.Run(1);

        Assert.True(client.Replication.ListComplete);
        Assert.True(client.Replication.HostHasKey("pizza0"));
        Assert.Equal(1, client.Replication.Shared);
    }

    [Fact]
    public void The_host_hears_when_a_client_has_its_whole_list()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        host.AddKeyed("beer case(itemx)", Table, "beercase0");
        var subscribed = new List<byte>();
        host.Replication.Subscribed += subscribed.Add;
        SessionNet.Node node = net.AddClient("Julian");
        var client = new FakeWorld(node);
        client.AddKeyed("beer case(itemx)", Table, "beercase0");

        host.Ready();
        net.Run(2);

        Assert.Equal(new[] { node.Session.LocalPlayerId }, subscribed);
    }
}
