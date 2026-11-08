using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Objects;

/// <summary>
/// What a game hangs from something (the engine block from the hoist) hangs only in that game: the machine whose game
/// hangs it keeps it, as a hand would, until it no longer hangs.
/// </summary>
public class ObjectHungTests
{
    private static readonly BodyPose Block = BodyPose.At(0, 0.3f, 0);
    private static readonly BodyPose Crate = BodyPose.At(5, 0, 0);

    private static (string, BodyPose)[] Garage() => new[] { ("block(Clone)", Block), ("beer case(itemx)", Crate) };

    [Fact]
    public void What_hangs_in_the_host_s_game_stays_with_the_host_however_near_a_client_is()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Garage());
        var client = new FakeWorld(net.AddClient("Julian"), Garage());
        net.Run(2);

        host.Replication.SetHung(host[0].Shared, true);
        net.Run(1);
        Assert.True(host[0].Shared.Held);
        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.True(client[0].Shared.Held);
        Assert.Contains(host.Log, m => m.EndsWith("'block(Clone)' hangs from something in this game: held here while it does."));

        // Standing next to it takes nothing, and neither does reaching for it.
        for (int second = 0; second < 5; second++)
        {
            client.Replication.SetNear(client[0].Shared, true);
            net.Run(1);
        }

        Assert.Equal(0, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);
        client.Grab(client[0], 2f);
        net.Run(1);
        Assert.Equal(0, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);
        client.LetGo();
        net.Run(1);

        // Once it no longer hangs it is like anything else lying near the client.
        host.Replication.SetHung(host[0].Shared, false);
        net.Run(1);
        Assert.False(host[0].Shared.Held);
        Assert.False(client[0].Shared.Held);
        client.Replication.SetNear(client[0].Shared, true);
        net.Run(1);
        Assert.Equal(1, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Simulate, client[0].Shared.Role);
    }

    [Fact]
    public void A_client_whose_game_hangs_it_takes_it_and_keeps_it_until_it_no_longer_hangs()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Garage());
        var client = new FakeWorld(net.AddClient("Julian"), Garage());
        net.Run(2);

        client.Replication.SetHung(client[0].Shared, true);
        Assert.Equal(BodyRole.Simulate, client[0].Shared.Role);
        net.Run(1);
        Assert.Equal(1, host[0].Shared.Owner);
        Assert.True(host[0].Shared.Held);
        Assert.Equal(BodyRole.Follow, host[0].Shared.Role);

        // Far from it and at rest, it is not handed back: no other game has it hanging there.
        client.Replication.SetNear(client[0].Shared, false);
        net.Run(15);
        Assert.Equal(1, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Simulate, client[0].Shared.Role);

        // The host's hand cannot take it either.
        host.Grab(host[0], 3f);
        net.Run(1);
        Assert.Equal(1, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, host[0].Shared.Role);
        host.LetGo();

        // Off the hook, it goes back to the host once it lies still.
        client.Replication.SetHung(client[0].Shared, false);
        net.Run(2);
        Assert.Equal(0, host[0].Shared.Owner);
        Assert.False(host[0].Shared.Held);
        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);
    }

    [Fact]
    public void Letting_go_with_the_hand_does_not_let_go_of_what_still_hangs()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Garage());
        var client = new FakeWorld(net.AddClient("Julian"), Garage());
        net.Run(2);

        client.Grab(client[0]);
        net.Run(0.5);
        client.Replication.SetHung(client[0].Shared, true);
        client.LetGo();
        client.Replication.SetNear(client[0].Shared, false);
        net.Run(5);

        Assert.Equal(1, host[0].Shared.Owner);
        Assert.True(host[0].Shared.Held);
        Assert.Equal(BodyRole.Simulate, client[0].Shared.Role);
    }

    [Fact]
    public void Hung_in_two_games_it_stays_with_the_first_and_the_other_takes_it_when_the_first_lets_go()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Garage());
        var first = new FakeWorld(net.AddClient("Julian"), Garage());
        var second = new FakeWorld(net.AddClient("Friend"), Garage());
        net.Run(2);

        first.Replication.SetHung(first[0].Shared, true);
        net.Run(1);

        // The second game hangs its copy too, and says so on every frame, as the game side does.
        for (int frame = 0; frame < 20; frame++)
        {
            second.Replication.SetHung(second[0].Shared, true);
            net.Run(0.1);
        }

        Assert.Equal(1, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, second[0].Shared.Role);
        Assert.True(second[0].Shared.Held);

        first.Replication.SetHung(first[0].Shared, false);
        for (int frame = 0; frame < 30; frame++)
        {
            second.Replication.SetHung(second[0].Shared, true);
            net.Run(0.1);
        }

        Assert.Equal(2, host[0].Shared.Owner);
        Assert.True(host[0].Shared.Held);
        Assert.Equal(BodyRole.Simulate, second[0].Shared.Role);
        Assert.Equal(BodyRole.Follow, first[0].Shared.Role);
    }

    [Fact]
    public void Once_it_hangs_in_the_host_s_game_the_host_takes_it_from_a_client_who_only_stood_near()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Garage());
        var client = new FakeWorld(net.AddClient("Julian"), Garage());
        net.Run(2);
        client.Replication.SetNear(client[0].Shared, true);
        net.Run(1);
        Assert.Equal(1, host[0].Shared.Owner);

        host.Replication.SetHung(host[0].Shared, true);
        net.Run(1);

        Assert.Equal(0, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);
        Assert.True(client[0].Shared.Held);
    }
}
