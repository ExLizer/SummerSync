using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class OrientedBoxTests
{
    // A van-sized box (2 m wide, 2 m high, 5 m long) at a place, turned by some degrees about the vertical.
    private static OrientedBox Car(float x, float z, float degrees, float halfRight = 1f, float halfUp = 1f, float halfForward = 2.5f)
    {
        double radians = degrees * System.Math.PI / 180.0;
        float cos = (float)System.Math.Cos(radians);
        float sin = (float)System.Math.Sin(radians);
        return new OrientedBox
        {
            X = x,
            Y = 1f,
            Z = z,
            RightX = cos,
            RightY = 0f,
            RightZ = -sin,
            UpX = 0f,
            UpY = 1f,
            UpZ = 0f,
            ForwardX = sin,
            ForwardY = 0f,
            ForwardZ = cos,
            HalfRight = halfRight,
            HalfUp = halfUp,
            HalfForward = halfForward,
        };
    }

    [Fact]
    public void BoxesThatOverlapMeet()
    {
        OrientedBox a = Car(0f, 0f, 0f);
        OrientedBox b = Car(1.5f, 1f, 0f);
        Assert.True(OrientedBox.Meet(ref a, ref b));
        Assert.True(OrientedBox.Meet(ref b, ref a));
    }

    [Fact]
    public void BoxesSideBySideWithAGapDoNotMeet()
    {
        // Two parked vans, 2.6 m between their centres: 60 cm between their sides.
        OrientedBox a = Car(0f, 0f, 0f);
        OrientedBox b = Car(2.6f, 0f, 0f);
        Assert.False(OrientedBox.Meet(ref a, ref b));

        // Grown by half a metre each, they would.
        OrientedBox grownA = a.Grown(0.5f);
        OrientedBox grownB = b.Grown(0.5f);
        Assert.True(OrientedBox.Meet(ref grownA, ref grownB));
    }

    [Fact]
    public void NoseToNoseOnlyWhenTheyReach()
    {
        // Head on along the road: 5 m long each, so they touch when their centres are 5 m apart.
        OrientedBox a = Car(0f, 0f, 0f);
        OrientedBox near = Car(0f, 4.9f, 180f);
        OrientedBox far = Car(0f, 5.2f, 180f);
        Assert.True(OrientedBox.Meet(ref a, ref near));
        Assert.False(OrientedBox.Meet(ref a, ref far));
    }

    [Fact]
    public void ATurnedBoxIsSeenApartWhereAnAxisAlignedTestWouldNot()
    {
        // A square turned 45 degrees beside the corner of another: their boxes in the world's axes overlap, the boxes do not.
        OrientedBox a = Car(0f, 0f, 0f, 1f, 1f, 1f);
        OrientedBox b = Car(2.3f, 2.3f, 45f, 1f, 1f, 1f);
        Assert.False(OrientedBox.Meet(ref a, ref b));

        // Closer, the turned one's corner is inside.
        OrientedBox closer = Car(1.9f, 1.9f, 45f, 1f, 1f, 1f);
        Assert.False(OrientedBox.Meet(ref a, ref closer));
        OrientedBox touching = Car(1.6f, 1.6f, 45f, 1f, 1f, 1f);
        Assert.True(OrientedBox.Meet(ref a, ref touching));
    }

    [Fact]
    public void AtACrossingTheyMeetWhereTheyCross()
    {
        // One along the road, one across it, its nose in the other's side.
        OrientedBox along = Car(0f, 0f, 0f);
        OrientedBox across = Car(3.4f, 0f, 90f);
        Assert.True(OrientedBox.Meet(ref along, ref across));
        OrientedBox stopped = Car(3.6f, 0f, 90f);
        Assert.False(OrientedBox.Meet(ref along, ref stopped));
    }

    [Fact]
    public void OneAboveTheOtherDoesNotMeet()
    {
        OrientedBox below = Car(0f, 0f, 0f);
        OrientedBox above = Car(0f, 0f, 30f);
        above.Y = 3.2f;
        Assert.False(OrientedBox.Meet(ref below, ref above));
        above.Y = 2.8f;
        Assert.True(OrientedBox.Meet(ref below, ref above));
    }
}
