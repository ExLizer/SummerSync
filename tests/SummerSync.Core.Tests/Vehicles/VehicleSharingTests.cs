using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.Players;
using SummerSync.Core.Tests.Net;
using SummerSync.Core.Tests.Objects;
using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class VehicleSharingTests
{
    private const string Van = "HAYOSIKO(1500kg, 250)";

    private static readonly BodyPose Parked = BodyPose.At(600, -3, -800);

    private static FakeWorld Things(SessionNet.Node node) =>
        new(node, ("beer case(itemx)", BodyPose.At(0, 0, 0)), ("sugar(itemx)", BodyPose.At(2, 0, 0)));

    private static FakeWorld Vehicles(SessionNet.Node node, BodyPose van) =>
        new(node, VehicleWire.FirstBodyMessage, VehicleWire.Label, (Van, van));

    private static IEnumerable<string> Log(SessionNet.Node node) =>
        node.Log.Where(e => e.Category == LogCategory.Authority).Select(e => e.Message);

    [Fact]
    public void Vehicles_and_loose_objects_are_shared_side_by_side_without_mixing()
    {
        var net = new SessionNet();
        FakeWorld hostThings = Things(net.Host);
        FakeWorld hostCars = Vehicles(net.Host, Parked);
        SessionNet.Node guest = net.AddClient("Julian");
        FakeWorld guestThings = Things(guest);

        // The guest's save has the van somewhere else. It is still the same van, and it goes where the host has it.
        FakeWorld guestCars = Vehicles(guest, BodyPose.At(22, 0, -47));

        net.Run(2);

        Assert.Equal(2, guestThings.Replication.Shared);
        Assert.Equal(1, guestCars.Replication.Shared);
        Assert.Equal(0, guestCars.Replication.MissingHere);
        Assert.Equal(0, guestCars.Replication.OnlyHere);
        Assert.Equal(0, guestThings.Replication.OnlyHere);
        Assert.Equal(BodyRole.Follow, guestCars[0].Shared.Role);
        Assert.True(Parked.DistanceTo(guestCars[0].Pose) < 0.01);

        // Both number their things from 1: a number means nothing without knowing whose it is.
        Assert.Equal(hostThings[0].Shared.Id, hostCars[0].Shared.Id);
        Assert.Equal(0, hostThings.Replication.Malformed + hostCars.Replication.Malformed);
        Assert.Equal(0, guestThings.Replication.Malformed + guestCars.Replication.Malformed);
        Assert.Contains("Objects: 2 shared with the host, 0 of the host's not found here, 0 only here.", Log(guest));
        Assert.Contains("Vehicles: Objects: 1 shared with the host, 0 of the host's not found here, 0 only here.", Log(guest));
    }

    [Fact]
    public void Whoever_drives_a_vehicle_simulates_it_and_nobody_can_take_the_wheel_from_them()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 40 });
        FakeWorld hostThings = Things(net.Host);
        FakeWorld hostCars = Vehicles(net.Host, Parked);
        SessionNet.Node guest = net.AddClient("Julian");
        FakeWorld guestThings = Things(guest);
        FakeWorld guestCars = Vehicles(guest, Parked);
        net.Run(2);

        // The guest gets behind the wheel and drives around.
        guestCars.Grab(guestCars[0], -3f);
        net.Run(3);

        Assert.Equal(BodyRole.Simulate, guestCars[0].Shared.Role);
        Assert.Equal(BodyRole.Follow, hostCars[0].Shared.Role);
        Assert.True(hostCars[0].Shared.Held);
        Assert.Equal(1, hostCars[0].Shared.Owner);
        Assert.True(hostCars[0].Pose.DistanceTo(guestCars[0].Pose) < 1.0, "the host shows the van where the guest drove it a moment ago");
        Assert.True(hostCars[0].Pose.DistanceTo(Parked) > 0.5, "the van moved");

        // Driving the van took nothing else from the host.
        Assert.All(hostThings.Things, thing => Assert.Equal(BodyRole.Simulate, thing.Shared.Role));
        Assert.All(guestThings.Things, thing => Assert.Equal(BodyRole.Follow, thing.Shared.Role));

        // The host tries the driver's seat too: it stays the guest's.
        hostCars.Grab(hostCars[0], -3f);
        net.Run(1);
        Assert.Equal(BodyRole.Follow, hostCars[0].Shared.Role);
        Assert.Equal(BodyRole.Simulate, guestCars[0].Shared.Role);
        hostCars.LetGo();

        // The guest gets out. Once the van stands still it is the host's again, where the guest left it.
        guestCars.LetGo();
        net.Run(3);
        Assert.Equal(BodyRole.Simulate, hostCars[0].Shared.Role);
        Assert.False(hostCars[0].Shared.Held);
        Assert.Equal(BodyRole.Follow, guestCars[0].Shared.Role);
        Assert.True(hostCars[0].Pose.DistanceTo(guestCars[0].Pose) < 0.01);
    }

    [Fact]
    public void A_driver_who_leaves_the_session_leaves_the_vehicle_to_the_host()
    {
        var net = new SessionNet();
        FakeWorld hostCars = Vehicles(net.Host, Parked);
        SessionNet.Node guest = net.AddClient("Julian");
        FakeWorld guestCars = Vehicles(guest, Parked);
        net.Run(2);
        guestCars.Grab(guestCars[0], -3f);
        net.Run(2);
        Assert.Equal(BodyRole.Follow, hostCars[0].Shared.Role);

        guest.Session.Close();
        net.Run(1);

        Assert.Equal(BodyRole.Simulate, hostCars[0].Shared.Role);
        Assert.False(hostCars[0].Shared.Held);
        Assert.Contains("Vehicles: Player 1 left while simulating 1 object(s); the host has them again.", Log(net.Host));
    }

    [Fact]
    public void Everyone_sees_which_vehicle_a_player_rides_and_what_a_driver_does_with_it()
    {
        var net = new SessionNet(new NetworkConditions { LatencyMs = 30 });
        var host = new PlayerReplication(net.Host.Session, net.Host.Logger);
        SessionNet.Node drivingNode = net.AddClient("Driver");
        var driver = new PlayerReplication(drivingNode.Session, drivingNode.Logger);
        SessionNet.Node watchingNode = net.AddClient("Watcher");
        var watcher = new PlayerReplication(watchingNode.Session, watchingNode.Logger);

        // The driver sits still in the seat while the engine speeds up; the others stand around.
        net.Host.BeforeUpdate = time => Step(host, time, new PlayerPose(0, 0, 0, 0, 0, 1.6f));
        watchingNode.BeforeUpdate = time => Step(watcher, time, new PlayerPose(5, 0, 0, 0, 0, 1.6f));
        drivingNode.BeforeUpdate = time =>
        {
            var seat = new PlayerPose(-0.4f, 0.9f, 0.8f, 10, 0, 1.0f) { Parent = 7, Driving = true };
            seat.Controls = new VehicleControls { Steering = 0.25f, Throttle = 1f, Rpm = (float)(1000 + (500 * time)), Gear = 3 };
            Step(driver, time, seat);
        };

        net.Run(4);

        foreach (PlayerReplication viewer in new[] { host, watcher })
        {
            RemotePlayer seen = viewer.Find(1)!;
            Assert.True(seen.Visible);
            Assert.Equal(7, seen.Shown.Parent);
            Assert.True(seen.Shown.Driving);
            Assert.Equal(-0.4f, seen.Shown.X, 3);
            Assert.Equal(0.8f, seen.Shown.Z, 3);
            Assert.Equal(3, seen.Shown.Controls.Gear);
            Assert.InRange(seen.Shown.Controls.Steering, 0.24f, 0.26f);

            // Between two states the engine speed is somewhere between the two, not stuck on the older one.
            Assert.InRange(seen.Shown.Controls.Rpm, 2700f, 3000f);
            Assert.Equal(0, viewer.Malformed);
        }

        // The ones on foot are still on foot.
        Assert.Equal(0, driver.Find(0)!.Shown.Parent);
        Assert.False(driver.Find(2)!.Shown.Driving);
    }

    private static void Step(PlayerReplication replication, double time, PlayerPose pose)
    {
        replication.SetLocal(true, pose);
        replication.Update(time);
        replication.Sample(time);
    }
}
