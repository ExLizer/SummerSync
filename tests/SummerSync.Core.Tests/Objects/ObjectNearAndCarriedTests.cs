using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;
using SummerSync.Core.Tests.Net;

namespace SummerSync.Core.Tests.Objects;

public class ObjectNearAndCarriedTests
{
    private static readonly BodyPose CrateA = BodyPose.At(0, 0, 0);
    private static readonly BodyPose CrateB = BodyPose.At(5, 0, 0);
    private static readonly BodyPose Bottle = BodyPose.At(1, 0.9f, 1);

    private static (string, BodyPose)[] Yard() =>
        new[] { ("beer case(itemx)", CrateA), ("beer case(itemx)", CrateB), ("booze(itemx)", Bottle) };

    [Fact]
    public void A_client_runs_the_physics_of_what_is_near_it_and_gives_it_back_when_it_moves_away()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);

        // The client walks up to a crate: it is theirs from that moment, without waiting for the host.
        client.Replication.SetNear(client[0].Shared, true);
        Assert.Equal(BodyRole.Simulate, client[0].Shared.Role);
        net.Run(1);

        Assert.Equal(BodyRole.Follow, host[0].Shared.Role);
        Assert.Equal(1, host[0].Shared.Owner);
        Assert.False(host[0].Shared.Held);
        Assert.Equal(BodyRole.Simulate, host[1].Shared.Role);

        // Lying still next to the client it stays the client's, however long.
        for (int second = 0; second < 15; second++)
        {
            client.Replication.SetNear(client[0].Shared, true);
            net.Run(1);
        }

        Assert.Equal(BodyRole.Simulate, client[0].Shared.Role);
        Assert.Equal(1, host[0].Shared.Owner);

        // The client's car knocks it: it moves by the client's physics, and the host shows that.
        client.Push(client[0], 1.5);
        net.Run(1);
        Assert.True(host[0].Pose.DistanceTo(CrateA) > 0.2, "the host saw it move");
        Assert.True(host[0].Pose.DistanceTo(client[0].Pose) < 0.6);

        // The client drives on. Once the crate lies still it is the host's again, where it ended up.
        net.Run(2);
        client.Replication.SetNear(client[0].Shared, false);
        net.Run(2);

        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(0, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);
        Assert.True(host[0].Pose.DistanceTo(client[0].Pose) < 0.01);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);
    }

    [Fact]
    public void Being_near_takes_nothing_from_another_player_but_a_hand_takes_what_is_only_near()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Yard());
        var first = new FakeWorld(net.AddClient("Julian"), Yard());
        var second = new FakeWorld(net.AddClient("Friend"), Yard());
        net.Run(2);

        // Both are next to the same crate at the same moment, and each starts simulating it.
        first.Replication.SetNear(first[1].Shared, true);
        second.Replication.SetNear(second[1].Shared, true);
        net.Run(1);

        // The host gave it to one of them; the other is back to showing it.
        byte winner = host[1].Shared.Owner;
        Assert.InRange(winner, (byte)1, (byte)2);
        FakeWorld has = winner == 1 ? first : second;
        FakeWorld hasNot = winner == 1 ? second : first;
        Assert.Equal(BodyRole.Simulate, has[1].Shared.Role);
        Assert.Equal(BodyRole.Follow, hasNot[1].Shared.Role);
        Assert.Equal(winner, hasNot[1].Shared.Owner);

        // Still next to it, the other keeps asking nothing: it is not the host's to give.
        hasNot.Replication.SetNear(hasNot[1].Shared, true);
        net.Run(2);
        Assert.Equal(winner, host[1].Shared.Owner);
        Assert.Equal(BodyRole.Follow, hasNot[1].Shared.Role);

        // But picking it up is something else: a hand takes what is merely near someone.
        hasNot.Grab(hasNot[1]);
        net.Run(1);
        Assert.Equal(winner == 1 ? 2 : 1, host[1].Shared.Owner);
        Assert.True(host[1].Shared.Held);
        Assert.Equal(BodyRole.Follow, has[1].Shared.Role);

        // And what is in a hand is not taken by walking up to it.
        has.Replication.SetNear(has[1].Shared, true);
        net.Run(2);
        Assert.Equal(BodyRole.Follow, has[1].Shared.Role);
        Assert.Equal(winner == 1 ? 2 : 1, host[1].Shared.Owner);
        Assert.Equal(0, host.Replication.Malformed);
    }

    [Fact]
    public void The_host_has_no_use_for_near_and_a_claim_of_an_unknown_kind_is_refused()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        host.Replication.SetNear(host[0].Shared, true);
        net.Run(1);
        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(0, client[0].Shared.Owner);

        var writer = new PacketWriter(16);
        writer.WriteByte(ObjectWire.MessageClaim);
        writer.WriteUInt16(host[0].Shared.Id);
        writer.WriteByte(3);
        client.Node.Session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
        net.Run(1);

        Assert.Equal(1, host.Replication.Malformed);
        Assert.Equal(0, host[0].Shared.Owner);
    }

    [Fact]
    public void What_a_vehicle_carries_is_shown_where_it_lies_on_the_vehicle()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 60, JitterMs = 20 });
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        // The host drives off with the crate in the back. In the world the crate moves at 10 m/s; on the van
        // it lies still.
        var place = BodyPose.At(0.2f, 0.5f, -1f);
        host[0].CarriedBy = 7;
        host[0].PlaceOnVehicle = place;
        host[0].Awake = true;
        host[0].Path = time => FakeWorld.VehicleAt(time).Moved(place.X, place.Y, place.Z);
        net.Run(3);

        Assert.Equal(7, client[0].Shared.Target.Parent);
        Assert.True(client[0].Pose.DistanceTo(new BodyPose(0.2f, 0.5f, -1f, 0, 0, 0, 1) { Parent = 7 }) < 0.001, "on the van it has not moved a millimetre");
        Assert.True(client[0].LargestStep < 0.001, "and never did, whatever the network was doing");

        // Someone who joins now is told where the crate is in the world, and pairs it with their own.
        var late = new FakeWorld(net.AddClient("Friend"), Yard());
        net.Run(2);
        Assert.Equal(3, late.Replication.Shared);
        Assert.Equal(0, late.Replication.MissingHere);
        // Of its two crates, the one nearer to where the host's is in the world became that crate.
        FakeWorld.Thing paired = late.Things.Single(thing => thing.Shared.Id == host[0].Shared.Id);
        Assert.Equal(7, paired.Pose.Parent);
        Assert.True(paired.LargestStep > 100, "it was told a place in the world first, far from where it lay");

        // The van stops and the crate lies still: the last word is a place in the world.
        host[0].CarriedBy = 0;
        host[0].Path = null;
        host[0].Awake = false;
        net.Run(2);

        Assert.Equal(0, client[0].Pose.Parent);
        Assert.True(client[0].Pose.DistanceTo(host[0].Pose) < 0.01);
        Assert.True(host[0].Pose.X > 120, "which is a long way from where it started");
    }

    [Fact]
    public void What_the_driver_takes_from_the_host_inside_the_vehicle_stays_on_the_vehicle_on_the_host_s_screen()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        // The host rides in the back of the van the client drives, and has just dropped a crate there: the
        // host's machine simulates it, and reports it where it lies on the van.
        var place = BodyPose.At(0.2f, 0.5f, -1f);
        host[0].CarriedBy = 7;
        host[0].PlaceOnVehicle = place;
        host[0].Path = time => FakeWorld.VehicleAt(time).Moved(place.X, place.Y, place.Z);
        host[0].Awake = true;
        net.Run(1);
        Assert.Equal(7, client[0].Pose.Parent);

        // It lies in the client's van, so the client's machine asks for it. Say it lies still there: the client
        // has nothing to tell about it for now.
        client.Replication.SetNear(client[0].Shared, true);
        net.Run(0.5);

        // Until the client says otherwise the host shows it on the van, not at the point of the road where it
        // was when it changed hands.
        Assert.Equal(BodyRole.Follow, host[0].Shared.Role);
        Assert.Equal(1, host[0].Shared.Owner);
        Assert.Equal(7, host[0].Pose.Parent);
        Assert.Equal(place.X, host[0].Pose.X, 2);
        Assert.Equal(place.Y, host[0].Pose.Y, 2);
        Assert.Equal(place.Z, host[0].Pose.Z, 2);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);
    }

    [Fact]
    public void Being_near_takes_nothing_from_the_vehicle_the_host_moves_it_on()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        // The host drives the tractor (vehicle 7) with a jerrycan on it; the van (vehicle 3) runs on the client.
        host.Replication.RunsVehicle = vehicle => vehicle == 7;
        var place = BodyPose.At(-0.2f, 1f, -0.4f);
        host[0].CarriedBy = 7;
        host[0].PlaceOnVehicle = place;
        host[0].Path = time => FakeWorld.VehicleAt(time).Moved(place.X, place.Y, place.Z);
        host[0].Awake = true;
        net.Run(1);
        Assert.Equal(7, client[0].Pose.Parent);

        // The client rides past on a moped, and its game takes the jerrycan for something near its player.
        client.Replication.SetNear(client[0].Shared, true);
        net.Run(1);

        // The host keeps it, and the client goes back to showing it on the tractor.
        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(0, host[0].Shared.Owner);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);
        Assert.Equal(0, client[0].Shared.Owner);
        Assert.Equal(1, host.Replication.NearClaimsOnBoard);
        net.Run(1);
        Assert.Equal(7, client[0].Pose.Parent);

        // What the host carries on a vehicle that runs on the client is the client's to ask for.
        host[1].CarriedBy = 3;
        host[1].PlaceOnVehicle = place;
        host[1].Path = time => FakeWorld.VehicleAt(time).Moved(place.X, place.Y, place.Z);
        host[1].Awake = true;
        net.Run(1);
        client.Replication.SetNear(client[1].Shared, true);
        net.Run(1);
        Assert.Equal(1, host[1].Shared.Owner);
        Assert.Equal(BodyRole.Simulate, client[1].Shared.Role);
        Assert.Equal(1, host.Replication.NearClaimsOnBoard);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);
    }

    [Fact]
    public void Taking_over_what_a_vehicle_carried_starts_from_its_place_in_the_world()
    {
        var net = new SessionNet();
        var host = new FakeWorld(net.Host, Yard());
        SessionNet.Node driver = net.AddClient("Julian");
        var client = new FakeWorld(driver, Yard());
        net.Run(2);

        // The client drives with the crate in the back, and drops out of the session on the way.
        var place = BodyPose.At(0.2f, 0.5f, -1f);
        client.Grab(client[0]);
        client[0].CarriedBy = 7;
        client[0].PlaceOnVehicle = place;
        net.Run(3);
        Assert.Equal(7, host[0].Pose.Parent);

        driver.Session.Close();
        net.Run(1);

        // The host goes on from where its own copy of the van had the crate, not from (0.2, 0.5, -1) in the world.
        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(1, host[0].HandOffs);
        Assert.Equal(0, host[0].Pose.Parent);
        Assert.True(host[0].Pose.X > 100, "x is " + host[0].Pose.X);
    }

    [Fact]
    public void What_is_let_go_inside_someone_else_s_vehicle_is_handed_over_at_once_and_on_the_vehicle()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        var host = new FakeWorld(net.Host, Yard());
        var client = new FakeWorld(net.AddClient("Julian"), Yard());
        net.Run(2);

        // The host drives; the client, riding, has the crate in hand and throws it on the van's floor, where
        // it would bounce about for a long time.
        var place = BodyPose.At(0.2f, 0.5f, -1f);
        client.Grab(client[0]);
        client[0].CarriedBy = 7;
        client[0].PlaceOnVehicle = place;
        net.Run(1);
        client.LetGo(settleSeconds: 30);

        // Left to itself the client would keep it until it lay still, or for ten seconds.
        net.Run(1);
        Assert.Equal(BodyRole.Simulate, client[0].Shared.Role);
        Assert.Equal(1, host[0].Shared.Owner);

        // It is in a vehicle the client does not drive: the driver's machine has to have it.
        client.Replication.GiveBack(client[0].Shared);
        net.Run(1);

        Assert.Equal(BodyRole.Simulate, host[0].Shared.Role);
        Assert.Equal(0, host[0].Shared.Owner);
        Assert.Equal(1, host[0].HandOffs);

        // The host was told where it is on the van, not where the client's copy of the van had it in the world.
        Assert.Equal(7, host[0].Pose.Parent);
        Assert.True(host[0].Pose.DistanceTo(new BodyPose(0.2f, 0.5f, -1f, 0, 0, 0, 1) { Parent = 7 }) < 0.001);
        Assert.Equal(BodyRole.Follow, client[0].Shared.Role);
        Assert.Equal(0, host.Replication.Malformed + client.Replication.Malformed);

        // In the hand, or already the host's, there is nothing to give back.
        client.Grab(client[1]);
        client.Replication.GiveBack(client[1].Shared);
        client.Replication.GiveBack(client[2].Shared);
        net.Run(1);
        Assert.Equal(1, host[1].Shared.Owner);
        Assert.True(host[1].Shared.Held);
        Assert.Equal(0, host[2].Shared.Owner);
    }

    [Fact]
    public void A_pose_on_a_vehicle_survives_the_wire_and_does_not_blend_with_one_in_the_world()
    {
        var onVan = new BodyPose(0.2f, 0.5f, -1f, 0, 0, 0, 1) { Parent = 65535 };
        var writer = new PacketWriter(64);
        onVan.Write(writer);
        Assert.Equal(BodyPose.Size, writer.Length);
        BodyPose read = BodyPose.Read(new PacketReader(writer.Buffer, 0, writer.Length));
        Assert.Equal(65535, read.Parent);
        Assert.Equal(0.2f, read.X);

        var inWorld = BodyPose.At(600, -3, -800);
        Assert.Equal(0.0, inWorld.DistanceTo(onVan));
        Assert.Equal(600f, inWorld.LerpTo(onVan, 0.4).X);
        Assert.Equal(0, inWorld.LerpTo(onVan, 0.4).Parent);
        Assert.Equal(0.2f, inWorld.LerpTo(onVan, 0.6).X);
        Assert.Equal(65535, inWorld.LerpTo(onVan, 0.6).Parent);
        onVan.OffsetFrom(inWorld, out double x, out double y, out double z);
        Assert.Equal((0.0, 0.0, 0.0), (x, y, z));

        // On the same vehicle two places blend as usual, and stay on it.
        var further = onVan;
        further.Z = 1f;
        Assert.Equal(0f, onVan.LerpTo(further, 0.5).Z, 3);
        Assert.Equal(65535, onVan.LerpTo(further, 0.5).Parent);
        Assert.Equal(2.0, onVan.DistanceTo(further), 3);
    }
}
