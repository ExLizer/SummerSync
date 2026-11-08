using SummerSync.Core.Players;

namespace SummerSync.Core.Tests.Players;

public class BotPathTests
{
    private static readonly PlayerPose Centre = new(50, 3, -20, 0, 0, 1.4f);

    [Fact]
    public void The_path_has_no_gaps_and_never_exceeds_a_sprint()
    {
        const double step = 0.001;
        double largest = 0;
        PlayerPose previous = BotPath.Evaluate(0, Centre);
        for (double t = step; t < (2 * BotPath.CycleSeconds) + 1; t += step)
        {
            PlayerPose pose = BotPath.Evaluate(t, Centre);
            largest = Math.Max(largest, PlayerPose.Distance(pose, previous));
            previous = pose;
        }

        // Float positions around x = 50 are only exact to a few micrometres.
        Assert.InRange(largest, BotPath.SprintSpeed * step * 0.99, (BotPath.SprintSpeed * step) + 0.0001);
    }

    [Fact]
    public void The_path_repeats_and_stays_around_its_centre()
    {
        for (double t = 0; t < BotPath.CycleSeconds; t += 0.05)
        {
            PlayerPose pose = BotPath.Evaluate(t, Centre);
            PlayerPose again = BotPath.Evaluate(t + (3 * BotPath.CycleSeconds), Centre);

            Assert.Equal(pose.X, again.X, 3);
            Assert.Equal(pose.Z, again.Z, 3);
            Assert.Equal(Centre.Y, pose.Y);
            Assert.InRange(Math.Sqrt(Math.Pow(pose.X - Centre.X, 2) + Math.Pow(pose.Z - Centre.Z, 2)), 0.0, 3.6);
            Assert.InRange(pose.Yaw, 0f, 359.9999f);
            Assert.InRange(pose.Pitch, -90f, 90f);
            Assert.InRange(pose.EyeHeight, BotPath.LowEyes - 0.001f, BotPath.StandingEyes + 0.001f);
        }
    }

    [Fact]
    public void The_path_includes_every_kind_of_movement()
    {
        var eyes = new List<float>();
        double standing = 0;
        double sprinting = 0;
        const double step = 0.01;
        for (double t = 0; t < BotPath.CycleSeconds; t += step)
        {
            double speed = PlayerPose.Distance(BotPath.Evaluate(t, Centre), BotPath.Evaluate(t + step, Centre)) / step;
            eyes.Add(BotPath.Evaluate(t, Centre).EyeHeight);
            standing += speed < 0.01 ? step : 0;
            sprinting += speed > 3.4 ? step : 0;
        }

        Assert.Equal(BotPath.LowEyes, eyes.Min(), 3);
        Assert.Equal(BotPath.StandingEyes, eyes.Max(), 3);
        Assert.InRange(standing, 5.0, 7.0);
        Assert.InRange(sprinting, 6.0, 7.0);
        Assert.InRange(BotPath.CycleSeconds, 25.0, 40.0);
    }

    [Fact]
    public void The_path_starts_at_the_centre_s_height_whatever_the_time()
    {
        PlayerPose early = BotPath.Evaluate(-12.5, Centre);

        Assert.Equal(Centre.Y, early.Y);
        Assert.InRange(early.X, Centre.X - 3.6f, Centre.X + 3.6f);
    }

    [Fact]
    public void The_centre_is_placed_in_front_of_whoever_watches()
    {
        var lookingEast = new PlayerPose(10, 2, 30, 90, -20, 1.4f);

        PlayerPose centre = BotPath.CentreInFrontOf(lookingEast, 6);

        Assert.Equal(16f, centre.X, 3);
        Assert.Equal(2f, centre.Y, 3);
        Assert.Equal(30f, centre.Z, 3);
    }
}
