using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class CabinAirTests
{
    // A cigarette: ten breaths of a second and a half, one every five seconds.
    private static void SmokeOne(CabinAir air, int smokers, int openDoors)
    {
        for (int breath = 0; breath < 10; breath++)
        {
            for (int step = 0; step < 15; step++)
            {
                air.Step(0.1, smokers, openDoors);
            }

            for (int step = 0; step < 35; step++)
            {
                air.Step(0.1, 0, openDoors);
            }
        }
    }

    [Fact]
    public void A_cigarette_smoked_with_the_doors_shut_fills_the_cabin_most_of_the_way()
    {
        var air = new CabinAir();
        SmokeOne(air, 1, 0);

        Assert.InRange(air.Level, 0.6, 0.9);

        // Two of them smoking fill it to the top, and no further.
        SmokeOne(air, 2, 0);
        Assert.InRange(air.Level, 0.95, 1.0);
        air.Step(5, 2, 0);
        Assert.Equal(1.0, air.Level, 6);
    }

    [Fact]
    public void With_the_doors_shut_it_clears_slowly_and_through_an_open_door_in_seconds()
    {
        var air = new CabinAir();
        SmokeOne(air, 2, 0);

        // A minute later most of it is still there.
        for (int step = 0; step < 600; step++)
        {
            air.Step(0.1, 0, 0);
        }

        Assert.InRange(air.Level, 0.55, 0.7);

        // A door opens: in ten seconds hardly any is left, and what went is said to have gone out of it.
        double before = air.Level;
        double outOfTheDoor = 0;
        for (int step = 0; step < 100; step++)
        {
            air.Step(0.1, 0, 1);
            outOfTheDoor += air.LeftLastStep;
        }

        Assert.True(air.Level < 0.1 * before, "left: " + air.Level);
        Assert.InRange(outOfTheDoor, 0.9 * (before - air.Level), before - air.Level);

        // Two doors open empty it faster than one.
        var one = new CabinAir();
        var two = new CabinAir();
        SmokeOne(one, 1, 0);
        SmokeOne(two, 1, 0);
        one.Step(2, 0, 1);
        two.Step(2, 0, 2);
        Assert.True(two.Level < one.Level);
    }

    [Fact]
    public void Smoking_with_a_door_open_hardly_fills_it_and_nothing_strange_comes_in()
    {
        var air = new CabinAir();
        SmokeOne(air, 1, 1);
        Assert.True(air.Level < 0.15, "level: " + air.Level);

        air.Step(0, 1, 0);
        air.Step(double.NaN, 1, 0);
        air.Step(-1, 1, 0);
        air.Step(1, -3, -2);
        Assert.InRange(air.Level, 0.0, 0.15);

        air.Clear();
        Assert.Equal(0.0, air.Level);
        Assert.Equal(0.0, air.LeftLastStep);
    }
}
