using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

/// <summary>The network's clock against the game's stopwatch, which jumps 51 hours back on a machine that has been up long.</summary>
public class SteadyTimeTests
{
    private const double Wrap = 184467.44073709552;

    [Fact]
    public void It_follows_the_stopwatch_while_the_two_agree()
    {
        var time = new SteadyTime();
        time.Read(10.0, 5000);
        time.Read(10.0167, 5016);
        double now = time.Read(11.5, 6500);

        Assert.Equal(1.5, now, 6);
        Assert.Equal(0, time.Jumps);
    }

    [Fact]
    public void A_stopwatch_that_jumps_51_hours_back_does_not_take_the_clock_with_it()
    {
        var time = new SteadyTime();
        time.Read(4800.0, 1000);
        time.Read(4800.0333, 1033);

        // The game's stopwatch, a frame later: 51 hours less.
        double now = time.Read(4800.0666 - Wrap, 1066);
        Assert.Equal(1, time.Jumps);
        Assert.True(now > 0.06 && now < 0.07, "now " + now);

        // And from there it goes on at the stopwatch's pace.
        double later = time.Read(4810.0666 - Wrap, 11066);
        Assert.Equal(10.066, later, 2);
        Assert.Equal(1, time.Jumps);
    }

    [Fact]
    public void A_stopwatch_that_jumps_forward_does_not_either()
    {
        var time = new SteadyTime();
        time.Read(100.0, 0);
        double now = time.Read(100.02 + Wrap, 20);

        Assert.Equal(0.02, now, 6);
        Assert.Equal(1, time.Jumps);
    }

    [Fact]
    public void The_tick_count_is_read_across_its_own_wrap()
    {
        var time = new SteadyTime();
        time.Read(50.0, int.MaxValue - 10);
        double now = time.Read(50.03 - Wrap, int.MinValue + 19);

        Assert.Equal(0.03, now, 6);
    }

    [Fact]
    public void A_long_frame_counts_whole_when_the_two_agree()
    {
        var time = new SteadyTime();
        time.Read(0.0, 0);
        double now = time.Read(42.5, 42510);

        Assert.Equal(42.5, now, 6);
        Assert.Equal(0, time.Jumps);
    }

    [Fact]
    public void It_never_goes_back()
    {
        var time = new SteadyTime();
        time.Read(5.0, 100);
        time.Read(6.0, 1100);
        double now = time.Read(5.9999, 1100);

        Assert.Equal(1.0, now, 6);
    }
}
