using SummerSync.Core.Diagnostics;

namespace SummerSync.Core.Tests.Diagnostics;

public class FrameRateCounterTests
{
    [Fact]
    public void Reports_nothing_until_the_first_window_completes()
    {
        var counter = new FrameRateCounter(0.5f);

        bool completed = counter.Tick(0.1f);

        Assert.False(completed);
        Assert.Equal(0f, counter.FramesPerSecond);
    }

    [Fact]
    public void Steady_frame_time_gives_its_reciprocal()
    {
        var counter = new FrameRateCounter(0.5f);
        bool completed = false;

        for (int frame = 0; frame < 40 && !completed; frame++)
        {
            completed = counter.Tick(1f / 60f);
        }

        Assert.True(completed);
        Assert.Equal(60f, counter.FramesPerSecond, precision: 1);
    }

    [Fact]
    public void Uneven_frames_are_averaged_over_the_window()
    {
        var counter = new FrameRateCounter(1f);

        counter.Tick(0.1f);
        counter.Tick(0.4f);
        bool completed = counter.Tick(0.5f);

        Assert.True(completed);
        Assert.Equal(3f, counter.FramesPerSecond, precision: 3);
    }

    [Fact]
    public void Each_window_starts_from_scratch()
    {
        var counter = new FrameRateCounter(1f);
        counter.Tick(1f);
        Assert.Equal(1f, counter.FramesPerSecond, precision: 3);

        counter.Tick(0.25f);
        counter.Tick(0.25f);
        counter.Tick(0.25f);
        counter.Tick(0.25f);

        Assert.Equal(4f, counter.FramesPerSecond, precision: 3);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    public void Invalid_frame_times_are_ignored(float deltaSeconds)
    {
        var counter = new FrameRateCounter(1f);

        Assert.False(counter.Tick(deltaSeconds));
        counter.Tick(1f);

        Assert.Equal(1f, counter.FramesPerSecond, precision: 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Window_must_be_positive(float windowSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRateCounter(windowSeconds));
    }
}
