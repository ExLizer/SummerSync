using SummerSync.Core.Objects;
using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class DrawnWatchTests
{
    private const double Frame = 1.0 / 60.0;

    // Frames from `from` for `seconds`: the owner's states go at `speed` along x from `start`, the body follows them with
    // `bodyShare` of that way, and the drawn vehicle with `drawnShare` (a frame behind, as interpolation leaves it).
    private static DrawnVerdict[] Run(DrawnWatch watch, ref double now, double seconds, double start, double speed, double bodyShare, double drawnShare)
    {
        var verdicts = new System.Collections.Generic.List<DrawnVerdict>();
        double end = now + seconds;
        double travelled = 0.0;
        for (; now < end; now += Frame)
        {
            double target = start + travelled;
            double body = start + (travelled * bodyShare);
            double drawn = start + (System.Math.Max(0.0, travelled - (speed * Frame)) * drawnShare);
            DrawnVerdict verdict = watch.Note(now, BodyPose.At((float)drawn, 0f, 0f), BodyPose.At((float)body, 0f, 0f), BodyPose.At((float)target, 0f, 0f), speed * 0.02);
            if (verdict != DrawnVerdict.None)
            {
                verdicts.Add(verdict);
            }

            travelled += speed * Frame;
        }

        return verdicts.ToArray();
    }

    [Fact]
    public void A_vehicle_drawn_where_it_goes_says_nothing()
    {
        var watch = new DrawnWatch();
        double now = 100.0;

        // Parked, then driven at 25 m/s with the drawn vehicle a frame behind its body.
        Assert.Empty(Run(watch, ref now, 3.0, 50.0, 0.0, 1.0, 1.0));
        Assert.Empty(Run(watch, ref now, 10.0, 50.0, 25.0, 1.0, 1.0));
        Assert.False(watch.Said);
    }

    [Fact]
    public void A_body_that_goes_on_without_its_drawn_vehicle_is_said_once_and_then_now_and_then()
    {
        var watch = new DrawnWatch();
        double now = 0.0;

        // The body and the doors on it go with the owner's states; what is drawn stays where it was parked.
        DrawnVerdict[] verdicts = Run(watch, ref now, 8.5, 0.0, 8.0, 1.0, 0.0);
        Assert.Equal(new[] { DrawnVerdict.Started, DrawnVerdict.Lasting }, verdicts);
        Assert.True(watch.Said);
        Assert.True(watch.WorstGap > 50.0);
        Assert.True(watch.BodyMoved > 7.0);
        Assert.True(watch.DrawnMoved < 0.01);
        Assert.Equal(0.0, watch.WrongSince, 6);
    }

    [Fact]
    public void A_vehicle_that_stays_altogether_while_its_owner_moves_it_is_said_too()
    {
        var watch = new DrawnWatch();
        double now = 0.0;

        // Neither body nor drawn vehicle move: the gap says nothing, the way the owner's states went does.
        DrawnVerdict[] verdicts = Run(watch, ref now, 2.5, 0.0, 6.0, 0.0, 0.0);
        Assert.Equal(new[] { DrawnVerdict.Started }, verdicts);
        Assert.True(watch.TargetMoved > 5.0);
        Assert.True(watch.WorstGap < 0.01);
    }

    [Fact]
    public void A_vehicle_put_elsewhere_again_and_again_while_its_owner_holds_it_still_is_said()
    {
        var watch = new DrawnWatch();
        double now = 0.0;
        BodyPose owner = BodyPose.At(-16f, -405.8f, 20f);
        BodyPose spawn = BodyPose.At(55.4f, -0.8f, -86.2f);
        var verdicts = new System.Collections.Generic.List<DrawnVerdict>();

        // The owner's place stands still under the map; every other frame the game puts the copy back where the scene had it
        // and the mod puts it at the owner's place again, body and all.
        for (int frame = 0; frame < 150; frame++, now += Frame)
        {
            BodyPose at = frame % 2 == 0 ? owner : spawn;
            DrawnVerdict verdict = watch.Note(now, at, at, owner, 0.0);
            if (verdict != DrawnVerdict.None)
            {
                verdicts.Add(verdict);
            }
        }

        Assert.Equal(new[] { DrawnVerdict.Started }, verdicts);
        Assert.True(watch.FarFrames > 0.4);
        Assert.True(watch.WorstGap < 0.01);
        Assert.Equal(0.0, watch.TargetMoved, 6);
    }

    [Fact]
    public void One_wrong_window_is_not_said()
    {
        var watch = new DrawnWatch();
        double now = 0.0;

        // A second of a hitch (one window wrong), and then following again.
        Assert.Empty(Run(watch, ref now, 1.0, 0.0, 6.0, 0.0, 0.0));
        Assert.Empty(Run(watch, ref now, 5.0, 6.0, 6.0, 1.0, 1.0));
        Assert.False(watch.Said);
    }

    [Fact]
    public void Drawn_where_it_belongs_again_ends_it()
    {
        var watch = new DrawnWatch();
        double now = 0.0;
        Run(watch, ref now, 3.0, 0.0, 8.0, 1.0, 0.0);
        Assert.True(watch.Said);

        // Put where the owner has it, with what hangs from it: following again.
        DrawnVerdict[] verdicts = Run(watch, ref now, 2.5, 24.0, 8.0, 1.0, 1.0);
        Assert.Equal(new[] { DrawnVerdict.Ended }, verdicts);
        Assert.False(watch.Said);
    }

    [Fact]
    public void A_stream_that_goes_quiet_under_a_driver_is_said_once_and_ends_when_states_come_again()
    {
        var silent = new SilentStream();
        Assert.Equal(DrawnVerdict.None, silent.Note(10.0, 100, true));
        Assert.Equal(DrawnVerdict.None, silent.Note(11.0, 130, true));

        // Nothing new for three seconds with the driver at the wheel.
        Assert.Equal(DrawnVerdict.None, silent.Note(13.9, 130, true));
        Assert.Equal(DrawnVerdict.Started, silent.Note(14.0, 130, true));
        Assert.Equal(11.0, silent.SilentSince, 6);
        Assert.Equal(DrawnVerdict.None, silent.Note(20.0, 130, true));
        Assert.True(silent.Said);

        // A state again.
        Assert.Equal(DrawnVerdict.Ended, silent.Note(20.5, 131, true));
        Assert.False(silent.Said);
    }

    [Fact]
    public void A_stream_without_a_driver_says_nothing()
    {
        var silent = new SilentStream();

        // Parked, nobody at the wheel: a quiet stream is how it should be.
        Assert.Equal(DrawnVerdict.None, silent.Note(0.0, 5, false));
        Assert.Equal(DrawnVerdict.None, silent.Note(60.0, 5, false));

        // Someone gets in: the count starts from when they did.
        Assert.Equal(DrawnVerdict.None, silent.Note(61.0, 5, true));
        Assert.Equal(DrawnVerdict.None, silent.Note(63.5, 5, true));
        Assert.Equal(DrawnVerdict.Started, silent.Note(64.0, 5, true));

        // And gets out before anything arrived.
        Assert.Equal(DrawnVerdict.Ended, silent.Note(65.0, 5, false));
    }

    [Fact]
    public void Stopping_ends_what_was_said_and_starts_the_window_again()
    {
        var watch = new DrawnWatch();
        double now = 0.0;
        Run(watch, ref now, 3.0, 0.0, 8.0, 1.0, 0.0);
        Assert.Equal(DrawnVerdict.Ended, watch.Stop());
        Assert.Equal(DrawnVerdict.None, watch.Stop());

        // A new window from where it is now: one more wrong window alone is not said.
        Assert.Empty(Run(watch, ref now, 1.0, 100.0, 8.0, 1.0, 0.0));
    }
}
