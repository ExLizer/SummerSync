using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Tests.Vehicles;

public class StepClockTests
{
    private const double Step = 0.02;
    private const double GameLimit = 0.05;

    // A game loop as Unity runs it: the frame's time (counted as the game's limit at most), the physics steps it owes, and the
    // mod's frame afterwards, which reads the session's clock a little late and not always equally late.
    private sealed class Loop
    {
        private readonly StepClock clock = new StepClock();
        private readonly System.Random random = new System.Random(7);
        private double real;
        private double game;
        private double fixedTime;

        public List<double> Shown { get; } = new List<double>();

        public List<double> Stamps { get; } = new List<double>();

        public List<double> RealAtStep { get; } = new List<double>();

        public StepClock Clock => clock;

        public double Real => real;

        public void Frames(int count, double seconds, double jitter = 0.002)
        {
            for (int i = 0; i < count; i++)
            {
                Frame(seconds, jitter);
            }
        }

        public void Frame(double seconds, double jitter = 0.002)
        {
            real += seconds;
            game += Math.Min(seconds, GameLimit);
            while (fixedTime + Step <= game + 1e-9)
            {
                fixedTime += Step;
                Shown.Add(clock.Follow(fixedTime, Step));
                Stamps.Add(clock.At(fixedTime));
                RealAtStep.Add(real);
            }

            double read = real + 0.003 + ((random.NextDouble() * 2.0) - 1.0) * jitter;
            clock.Frame(read, game, seconds > GameLimit);
        }
    }

    private static List<double> Increments(List<double> times, int from)
    {
        var steps = new List<double>();
        for (int i = Math.Max(1, from); i < times.Count; i++)
        {
            steps.Add(times[i] - times[i - 1]);
        }

        return steps;
    }

    [Fact]
    public void At_a_good_frame_rate_vehicles_are_shown_at_the_games_pace()
    {
        var loop = new Loop();
        loop.Frames(600, 1.0 / 60.0);

        List<double> steps = Increments(loop.Shown, 120);
        Assert.All(steps, s => Assert.InRange(s, Step * 0.95, Step * 1.05));
        Assert.Equal(0, loop.Clock.ShortFrames);
        Assert.Equal(0, loop.Clock.Snaps);
    }

    [Fact]
    public void Below_twenty_frames_a_second_they_go_at_the_clocks_pace_without_jumping()
    {
        // 15 frames a second: the game counts 0.05 s of every 0.067 s frame.
        var loop = new Loop();
        loop.Frames(30, 1.0 / 60.0);
        loop.Frames(300, 1.0 / 15.0);

        List<double> steps = Increments(loop.Shown, loop.Shown.Count - 100);
        for (int i = 1; i < steps.Count; i++)
        {
            // From one step to the next the shown time changes its pace a little at most: no vehicle is put ahead.
            Assert.InRange(steps[i] - steps[i - 1], -StepClock.PaceChange * Step - 1e-9, StepClock.PaceChange * Step + 1e-9);
        }

        // On the whole it goes as fast as the session's clock, which is a third faster than the game's time.
        double pace = (loop.Shown[loop.Shown.Count - 1] - loop.Shown[loop.Shown.Count - 101]) / (100 * Step);
        Assert.InRange(pace, 1.30, 1.37);

        // And a little behind it, never far.
        double behind = loop.Stamps[loop.Stamps.Count - 1] - loop.Shown[loop.Shown.Count - 1];
        Assert.InRange(behind, 0.0, 0.15);
        Assert.Equal(0, loop.Clock.Snaps);
    }

    [Fact]
    public void Below_twenty_frames_a_second_the_stamps_keep_up_with_the_session_clock()
    {
        var loop = new Loop();
        loop.Frames(30, 1.0 / 60.0);
        loop.Frames(600, 1.0 / 15.0);

        // What a step is stamped with is the session's clock of that frame, give or take the frame: it does not fall
        // behind and catch up by jumps.
        for (int i = loop.Stamps.Count - 200; i < loop.Stamps.Count; i++)
        {
            Assert.InRange(loop.RealAtStep[i] - loop.Stamps[i], -0.01, 0.09);
        }

        Assert.True(loop.Clock.ShortFrames >= 600);
        Assert.InRange(loop.Clock.Lost, 0.0167 * 590, 0.0167 * 610);
    }

    [Fact]
    public void After_a_pause_of_the_game_they_catch_up_without_a_jump()
    {
        // A pause of the garbage collector: one frame of 0.2 s, of which the game counts 0.05 s.
        var loop = new Loop();
        loop.Frames(120, 1.0 / 60.0);
        int before = loop.Shown.Count;
        loop.Frame(0.2);
        loop.Frames(120, 1.0 / 60.0);

        List<double> steps = Increments(loop.Shown, before);
        for (int i = 0; i < steps.Count; i++)
        {
            Assert.InRange(steps[i], Step * (StepClock.LeastPace - 0.01), Step * (StepClock.MostPace + 0.01));
            if (i > 0)
            {
                Assert.InRange(steps[i] - steps[i - 1], -StepClock.PaceChange * Step - 1e-9, StepClock.PaceChange * Step + 1e-9);
            }
        }

        // Two seconds later it is where it belongs again, and it went at the game's pace once caught up.
        double behind = loop.Stamps[loop.Stamps.Count - 1] - loop.Shown[loop.Shown.Count - 1];
        Assert.InRange(behind, -0.005, 0.005);
        Assert.InRange(loop.Clock.TakeMostBehind(), 0.1, 0.16);
        Assert.Equal(0, loop.Clock.Snaps);
    }

    [Fact]
    public void A_long_stop_is_not_crawled_back_but_taken_at_once()
    {
        var loop = new Loop();
        loop.Frames(60, 1.0 / 60.0);
        loop.Frame(2.0);
        loop.Frames(10, 1.0 / 60.0);

        Assert.Equal(1, loop.Clock.Snaps);
        double behind = loop.Stamps[loop.Stamps.Count - 1] - loop.Shown[loop.Shown.Count - 1];
        Assert.InRange(behind, -0.01, 0.01);
    }

    [Fact]
    public void A_jump_of_the_clocks_that_no_short_frame_explains_is_taken_at_once_for_the_stamps()
    {
        var clock = new StepClock();
        clock.Frame(100.0, 10.0, false);
        clock.Frame(100.0167, 10.0167, false);
        Assert.InRange(clock.At(10.0167), 100.016, 100.018);

        // The session's clock moved 0.3 s on (set again against the host's): the stamps go with it.
        clock.Frame(100.3333, 10.0333, false);
        Assert.InRange(clock.At(10.0333), 100.332, 100.335);
        Assert.Equal(0, clock.ShortFrames);
    }

    [Fact]
    public void The_shown_time_starts_at_the_clock_again_after_a_reset()
    {
        var loop = new Loop();
        loop.Frames(60, 1.0 / 60.0);
        loop.Clock.ResetFollow();
        loop.Frames(2, 1.0 / 60.0);
        Assert.InRange(loop.Stamps[loop.Stamps.Count - 1] - loop.Shown[loop.Shown.Count - 1], -0.001, 0.001);
    }
}
