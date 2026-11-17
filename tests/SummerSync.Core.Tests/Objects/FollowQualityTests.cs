using SummerSync.Core.Objects;
using SummerSync.Core.Replication;

namespace SummerSync.Core.Tests.Objects;

public class FollowQualityTests
{
    private const double Step = 0.02;

    // Steps the vehicle for that long, the way the given function says each step went, closing once a frame (three steps).
    private static List<FollowReport> Run(FollowQuality quality, double from, double seconds, Func<int, (SnapshotSource, bool, double)> step, Func<double, FollowCounters> totals)
    {
        var reports = new List<FollowReport>();
        int n = 0;
        for (double time = from; time < from + seconds; time += Step, n++)
        {
            (SnapshotSource source, bool jolted, double jolt) = step(n);
            quality.Step(source, jolted, jolt, 0.4);
            if (n % 3 == 0 && quality.Close(time, totals(time), out FollowReport report))
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    private static FollowCounters Received(double time)
    {
        return new FollowCounters { Received = (long)(time * 50) };
    }

    [Fact]
    public void An_even_stream_makes_smooth_windows_of_ten_seconds()
    {
        var quality = new FollowQuality();
        List<FollowReport> reports = Run(quality, 100, 31, _ => (SnapshotSource.Interpolated, false, 0), Received);

        Assert.Equal(3, reports.Count);
        Assert.All(reports, report => Assert.False(report.Rough));
        Assert.All(reports, report => Assert.InRange(report.Seconds, 10, 10.1));
        Assert.All(reports, report => Assert.InRange(report.Received, 495, 510));
        Assert.Equal(3, quality.Windows);
        Assert.Equal(0, quality.RoughWindows);
    }

    [Fact]
    public void A_jolt_makes_its_window_rough_and_says_how_large_it_was()
    {
        var quality = new FollowQuality();
        List<FollowReport> reports = Run(
            quality,
            0,
            21,
            n => n == 700 ? (SnapshotSource.Interpolated, true, 3.1) : n == 720 ? (SnapshotSource.Interpolated, true, 1.2) : (SnapshotSource.Interpolated, false, 0),
            Received);

        Assert.Equal(2, reports.Count);
        Assert.False(reports[0].Rough);
        Assert.True(reports[1].Rough);
        Assert.Equal(2, reports[1].Jolts);
        Assert.Equal(3.1, reports[1].LargestJolt, 3);
        Assert.Equal(0.4, reports[1].LargestJoltAfter, 3);
        Assert.Contains("2 jolt(s) (the largest 3.1 m after a step of 0.40 m)", reports[1].Describe());
        Assert.Equal(1, quality.RoughWindows);
    }

    [Fact]
    public void Steps_held_past_the_newest_state_and_pauses_of_either_game_are_rough()
    {
        var held = new FollowQuality();
        List<FollowReport> heldReports = Run(held, 0, 11, n => n == 100 ? (SnapshotSource.Held, false, 0) : (SnapshotSource.Interpolated, false, 0), Received);
        Assert.True(heldReports[0].Rough);
        Assert.Equal(1, heldReports[0].Held);
        Assert.Equal(1, heldReports[0].Extrapolated);

        var paused = new FollowQuality();
        List<FollowReport> pausedReports = Run(
            paused,
            0,
            11,
            _ => (SnapshotSource.Interpolated, false, 0),
            time => new FollowCounters { Received = (long)(time * 50), SenderPauses = time > 5 ? 1 : 0, LongestSenderPause = time > 5 ? 0.18 : 0 });
        Assert.True(pausedReports[0].Rough);
        Assert.Equal(1, pausedReports[0].SenderPauses);
        Assert.Contains("1 pause(s) of the sending game", pausedReports[0].Describe());
        Assert.Contains("a pause of the sending game 0.18 s", pausedReports[0].Describe());
    }

    [Fact]
    public void A_little_extrapolation_is_not_rough_but_more_than_one_step_in_twenty_is()
    {
        var little = new FollowQuality();
        Assert.False(Run(little, 0, 11, n => n % 40 == 0 ? (SnapshotSource.Extrapolated, false, 0) : (SnapshotSource.Interpolated, false, 0), Received)[0].Rough);

        var much = new FollowQuality();
        Assert.True(Run(much, 0, 11, n => n % 10 == 0 ? (SnapshotSource.Extrapolated, false, 0) : (SnapshotSource.Interpolated, false, 0), Received)[0].Rough);
    }

    [Fact]
    public void A_window_in_which_the_vehicle_stood_or_the_playback_started_over_is_dropped()
    {
        var quality = new FollowQuality();
        Assert.False(quality.Close(0, Received(0), out _));

        // Ten seconds without a step: nothing to say.
        Assert.False(quality.Close(10.5, Received(10.5), out _));

        // A new playback (the vehicle changed hands) starts from zero: the window begins again from there.
        quality.Step(SnapshotSource.Interpolated, true, 5, 0.4);
        Assert.False(quality.Close(15, new FollowCounters { Received = 3 }, out _));
        for (int i = 0; i < FollowQuality.MovingStepsAtLeast; i++)
        {
            quality.Step(SnapshotSource.Interpolated, false, 0, 0.4);
        }

        Assert.True(quality.Close(25.1, new FollowCounters { Received = 503 }, out FollowReport report));
        Assert.Equal(0, report.Jolts);
        Assert.Equal(500, report.Received);
    }

    [Fact]
    public void A_vehicle_standing_where_its_owner_left_it_says_nothing_however_long_it_is_held()
    {
        // Seen on 2026-11-17: a parked boat and flatbed, one state in ten seconds, every step standing past it.
        var quality = new FollowQuality();
        quality.Close(0, Received(0), out _);
        for (int i = 0; i < 500; i++)
        {
            quality.Step(SnapshotSource.Held, false, 0, 0, moving: false);
        }

        Assert.False(quality.Close(10.1, new FollowCounters { Received = 1 }, out _));

        // Moving, the same steps held past the newest state are what a player sees as the vehicle standing and jumping.
        for (int i = 0; i < 500; i++)
        {
            quality.Step(i < 30 ? SnapshotSource.Held : SnapshotSource.Interpolated, false, 0, 0, moving: i >= 30);
        }

        Assert.True(quality.Close(20.2, new FollowCounters { Received = 300 }, out FollowReport report));
        Assert.True(report.Rough);
        Assert.Equal(30, report.Held);
    }

    [Fact]
    public void Stopping_drops_the_window_under_way()
    {
        var quality = new FollowQuality();
        quality.Close(0, Received(0), out _);
        quality.Step(SnapshotSource.Held, true, 2, 0.4);
        quality.Stop();
        Assert.False(quality.Close(10.5, Received(10.5), out _));
        for (int i = 0; i < FollowQuality.MovingStepsAtLeast; i++)
        {
            quality.Step(SnapshotSource.Interpolated, false, 0, 0.4);
        }

        Assert.True(quality.Close(21, Received(21), out FollowReport report));
        Assert.False(report.Rough);
    }
}
