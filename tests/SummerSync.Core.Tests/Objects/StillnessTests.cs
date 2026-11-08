using SummerSync.Core.Objects;

namespace SummerSync.Core.Tests.Objects;

public class StillnessTests
{
    private const double Frame = 1.0 / 60.0;

    // Shows the thing for a while at the place the function gives for each moment. Returns the time it ended at.
    private static double Show(Stillness stillness, double from, double seconds, Func<double, BodyPose> where, bool held = false)
    {
        double time = from;
        for (double end = from + seconds; time < end; time += Frame)
        {
            stillness.Note(where(time - from), held, time);
        }

        return time;
    }

    [Fact]
    public void Nothing_is_settled_before_it_has_been_shown()
    {
        var stillness = new Stillness();
        Assert.False(stillness.Settled(100));

        // Nor the moment it first is: it may be on its way somewhere.
        stillness.Note(BodyPose.At(1, 0, 1), false, 100);
        Assert.False(stillness.Settled(100));
        Assert.False(stillness.Settled(100.2));
    }

    [Fact]
    public void A_thing_lying_where_it_is_settles_after_half_a_second()
    {
        var stillness = new Stillness();
        double time = Show(stillness, 10, 0.4, _ => BodyPose.At(1, 0, 1));
        Assert.False(stillness.Settled(time));

        time = Show(stillness, time, 0.2, _ => BodyPose.At(1, 0, 1));
        Assert.True(stillness.Settled(time));
    }

    [Fact]
    public void A_thing_thrown_is_not_settled_until_it_has_landed_and_lain_there()
    {
        var stillness = new Stillness();
        double time = Show(stillness, 0, 1, _ => BodyPose.At(0, 1.2f, 0));
        Assert.True(stillness.Settled(time));

        // Thrown at 7 m/s: on its way for half a second.
        time = Show(stillness, time, 0.5, t => BodyPose.At((float)(7 * t), (float)(1.2 + (2 * t) - (4.9 * t * t)), 0));
        Assert.False(stillness.Settled(time));

        // It lands and lies there.
        BodyPose landed = BodyPose.At(3.5f, 0, 0);
        time = Show(stillness, time, 0.3, _ => landed);
        Assert.False(stillness.Settled(time));
        time = Show(stillness, time, 0.3, _ => landed);
        Assert.True(stillness.Settled(time));
    }

    [Fact]
    public void A_thing_let_go_of_is_not_settled_while_it_falls()
    {
        var stillness = new Stillness();

        // In its owner's hand, held quite still: never settled.
        double time = Show(stillness, 0, 2, _ => BodyPose.At(0, 1.2f, 0), held: true);
        Assert.False(stillness.Settled(time));

        // Let go: it starts from no speed at all, and is past half a metre per second in a twentieth of a second.
        time = Show(stillness, time, 0.45, t => BodyPose.At(0, (float)(1.2 - (4.9 * t * t)), 0));
        Assert.False(stillness.Settled(time));

        time = Show(stillness, time, 0.6, _ => BodyPose.At(0, 0.2f, 0));
        Assert.True(stillness.Settled(time));
    }

    [Fact]
    public void A_thing_lying_in_a_moving_vehicle_is_still()
    {
        var stillness = new Stillness();
        var onVan = new BodyPose(0.2f, 0.4f, -1f, 0, 0, 0, 1) { Parent = 3 };
        double time = Show(stillness, 0, 1, _ => onVan);
        Assert.True(stillness.Settled(time));

        // Taken off the van, or put on it, it is somewhere else: the count starts again.
        time = Show(stillness, time, 0.2, _ => BodyPose.At(600, -3, -800));
        Assert.False(stillness.Settled(time));
    }

    [Fact]
    public void Creeping_along_counts_as_still_and_a_hitch_of_the_game_does_not_excuse_a_jump()
    {
        var stillness = new Stillness();

        // Settling on a slope at 20 cm/s.
        double time = Show(stillness, 0, 1, t => BodyPose.At((float)(0.2 * t), 0, 0));
        Assert.True(stillness.Settled(time));

        // The game stops for two seconds and the thing turns up a metre away: at half a metre per second it
        // could have got there, but nobody saw it lying still meanwhile.
        stillness.Note(BodyPose.At(1.2f, 0, 0), false, time + 2);
        Assert.False(stillness.Settled(time + 2));
    }

    [Fact]
    public void Forgetting_starts_the_count_again()
    {
        var stillness = new Stillness();
        double time = Show(stillness, 0, 1, _ => BodyPose.At(1, 0, 1));
        Assert.True(stillness.Settled(time));

        stillness.Forget();
        Assert.False(stillness.Settled(time));
        time = Show(stillness, time, 0.3, _ => BodyPose.At(1, 0, 1));
        Assert.False(stillness.Settled(time));
        time = Show(stillness, time, 0.3, _ => BodyPose.At(1, 0, 1));
        Assert.True(stillness.Settled(time));
    }
}
