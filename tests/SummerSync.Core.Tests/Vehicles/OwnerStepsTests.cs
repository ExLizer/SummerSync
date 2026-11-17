using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class OwnerStepsTests
{
    private const double Step = 0.02;

    [Fact]
    public void Driving_braking_and_crashing_are_speeds()
    {
        // Pulling away hard, a sharp turn at speed, braking at once and a crash that stops the car dead or bounces it back.
        Assert.False(OwnerSteps.IsJump(0, 0, 0, 0.2, 0, 0, Step));
        Assert.False(OwnerSteps.IsJump(20, 0, 0, 19.8, 0, 1.2, Step));
        Assert.False(OwnerSteps.IsJump(20, 0, 0, 19, 0, 0, Step));
        Assert.False(OwnerSteps.IsJump(20, 0, 0, 0, 0, 0, Step));
        Assert.False(OwnerSteps.IsJump(20, 0, 0, -6, 2, 0, Step));
    }

    [Fact]
    public void A_jump_of_the_places_is_not_a_speed()
    {
        // At rest, then 0.9 m in a step: the game put it somewhere (2026-11-17, the rescued Satsuma read as 45.7 m/s).
        Assert.True(OwnerSteps.IsJump(0, 0, 0, 45.7, 0, 0, Step));

        // Going at 20 m/s, then 3.1 m in a step after a hitch, and the same jump backwards.
        Assert.True(OwnerSteps.IsJump(20, 0, 0, 155, 0, 0, Step));
        Assert.True(OwnerSteps.IsJump(20, 0, 0, -20, 0, 0, Step));

        // Straight up out of a garage.
        Assert.True(OwnerSteps.IsJump(0, 0, 0, 0, 30, 0, Step));
    }

    [Fact]
    public void Without_a_step_of_time_nothing_is_a_jump()
    {
        Assert.False(OwnerSteps.IsJump(0, 0, 0, 1000, 0, 0, 0));
        Assert.False(OwnerSteps.IsJump(0, 0, 0, 1000, 0, 0, double.NaN));
    }
}
