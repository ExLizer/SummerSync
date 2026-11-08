using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Objects;

/// <summary>Objects that one game made while it ran, and the others made after it.</summary>
public class ObjectBornTests
{
    private static readonly BodyPose Counter = BodyPose.At(-1550f, 4f, 1180f);

    [Fact]
    public void What_a_client_made_is_its_own_to_simulate_from_the_start_and_goes_back_to_the_host_once_put_down()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new FakeWorld(net.Host, ("beer case(itemx)", BodyPose.At(0, 0, 0)));
        var buyer = new FakeWorld(net.AddClient("Julian"), ("beer case(itemx)", BodyPose.At(0, 0, 0)));
        net.Run(2);
        byte buyerId = buyer.Node.Session.LocalPlayerId;

        // The buyer's game made the bag and the buyer took it at once; the host's made it a moment later.
        FakeWorld.Thing bag = buyer.Add("shoppingbagx4", Counter);
        buyer.Grab(bag);
        net.Run(0.1);
        FakeWorld.Thing copy = host.AddBorn("shoppingbagx4", Counter, buyerId);
        Assert.Equal(buyerId, copy.Shared.Owner);
        Assert.Equal(BodyRole.Follow, copy.Shared.Role);
        net.Run(1.5);

        Assert.Equal(BodyRole.Simulate, bag.Shared.Role);
        Assert.Equal(0, bag.HandOffs);
        Assert.True(copy.Pose.DistanceTo(bag.Pose) < 0.5, copy.Pose.DistanceTo(bag.Pose).ToString("0.00") + " m apart");

        buyer.LetGo();
        net.Run(2);
        Assert.Equal(Protocol.HostPlayerId, copy.Shared.Owner);
        Assert.Equal(BodyRole.Simulate, copy.Shared.Role);
        Assert.Equal(BodyRole.Follow, bag.Shared.Role);
        Assert.True(copy.Pose.DistanceTo(bag.Pose) < 0.01);
    }

    [Fact]
    public void What_the_host_made_for_a_client_that_does_not_have_it_goes_back_to_the_host()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new FakeWorld(net.Host, ("beer case(itemx)", BodyPose.At(0, 0, 0)));
        var buyer = new FakeWorld(net.AddClient("Julian"), ("beer case(itemx)", BodyPose.At(0, 0, 0)));
        net.Run(2);

        // Gone from the buyer's world before it was ever found there: eaten, say.
        FakeWorld.Thing copy = host.AddBorn("sausagesx4", Counter, buyer.Node.Session.LocalPlayerId);
        net.Run(ObjectReplication.OrphanSeconds - 1);
        Assert.Equal(buyer.Node.Session.LocalPlayerId, copy.Shared.Owner);

        net.Run(2);
        Assert.Equal(Protocol.HostPlayerId, copy.Shared.Owner);
        Assert.Equal(BodyRole.Simulate, copy.Shared.Role);
        Assert.Contains(buyer.Log, line => line.Contains("'sausagesx4' is this machine's to simulate, but this world does not have it"));
    }

    [Fact]
    public void Something_picked_up_before_it_was_paired_stays_in_the_hand_when_it_pairs()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60 });
        var host = new FakeWorld(net.Host, ("beer case(itemx)", BodyPose.At(0, 0, 0)));
        var buyer = new FakeWorld(net.AddClient("Julian"), ("beer case(itemx)", BodyPose.At(0, 0, 0)));
        net.Run(2);

        // The buyer took it at once; the host's copy turned up as the host's own, on the counter.
        FakeWorld.Thing bag = buyer.Add("shoppingbagx5", Counter);
        buyer.Grab(bag);
        net.Run(0.5);
        FakeWorld.Thing copy = host.Add("shoppingbagx5", Counter);
        net.Run(1.5);

        Assert.Equal(BodyRole.Simulate, bag.Shared.Role);
        Assert.True(bag.Shared.Held);
        Assert.Equal(0.0, bag.LargestStep);
        Assert.Equal(buyer.Node.Session.LocalPlayerId, copy.Shared.Owner);
        Assert.True(copy.Shared.Held);
        Assert.True(copy.Pose.DistanceTo(bag.Pose) < 0.5, copy.Pose.DistanceTo(bag.Pose).ToString("0.00") + " m apart");
    }

    [Fact]
    public void What_one_game_eats_or_throws_away_goes_in_every_world_once()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, LossPercent = 5 });
        (string, BodyPose)[] kitchen = { ("sausagesx1", BodyPose.At(1, 0, 0)), ("milkx1", BodyPose.At(2, 0, 0)) };
        var host = new FakeWorld(net.Host, kitchen);
        var eater = new FakeWorld(net.AddClient("Julian"), kitchen);
        var other = new FakeWorld(net.AddClient("Second"), kitchen);
        var gone = new Dictionary<FakeWorld, List<string>> { [host] = new(), [eater] = new(), [other] = new() };
        foreach (FakeWorld world in new[] { host, eater, other })
        {
            // What a game does when told: throws its copy away, which takes it out of its world.
            world.Replication.GoneWanted += (body, player) =>
            {
                gone[world].Add(body.Name + " by " + player);
                world.Remove(world.Things.Single(thing => thing.Shared == body));
            };
        }

        net.Run(2);
        FakeWorld.Thing sausages = eater.Things.Single(thing => thing.Shared.Name == "sausagesx1");
        eater.Replication.Consume(sausages.Shared);
        eater.Remove(sausages);
        net.Run(1);

        byte eaterId = eater.Node.Session.LocalPlayerId;
        Assert.Equal(new[] { "sausagesx1 by " + eaterId }, gone[host]);
        Assert.Equal(new[] { "sausagesx1 by " + eaterId }, gone[other]);
        Assert.Empty(gone[eater]);
        Assert.All(new[] { host, eater, other }, world => Assert.DoesNotContain(world.Things, thing => thing.Shared.Name == "sausagesx1"));
        Assert.Equal(1, eater.Replication.GoneTold);
        Assert.Equal(0, host.Replication.GoneTold);

        // The host throws something away too.
        FakeWorld.Thing milk = host.Things.Single(thing => thing.Shared.Name == "milkx1");
        host.Replication.Consume(milk.Shared);
        host.Remove(milk);
        net.Run(1);
        Assert.Equal(new[] { "sausagesx1 by " + eaterId, "milkx1 by 0" }, gone[other]);
        Assert.Equal(new[] { "milkx1 by 0" }, gone[eater]);
    }

    [Fact]
    public void Born_with_the_host_itself_or_with_nobody_there_is_the_host_s()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host);
        net.AddClient("Julian");
        net.Run(1);

        Assert.Equal(Protocol.HostPlayerId, host.AddBorn("beercase3", Counter, Protocol.HostPlayerId).Shared.Owner);
        Assert.Equal(Protocol.HostPlayerId, host.AddBorn("beercase4", Counter, 9).Shared.Owner);
        Assert.All(host.Things, thing => Assert.Equal(BodyRole.Simulate, thing.Shared.Role));
    }
}
