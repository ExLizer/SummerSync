using SummerSync.Core.Players;
using SummerSync.Core.Replication;

namespace SummerSync.Core.Tests.Players;

/// <summary>
/// A sender that moves along a known path and a watcher that shows it through a <see cref="PoseBuffer"/>,
/// joined by a link with latency, jitter and loss. Everything runs on a manual clock, so a run is repeatable.
/// </summary>
internal sealed class PoseLink
{
    private const double SendInterval = 1.0 / 30.0;

    private readonly Random random;
    private readonly List<(double Due, double SenderTime, PlayerPose Pose, ushort Number)> inFlight = new();
    private ushort nextNumber = 65000;
    private double nextSend;
    private bool hasShown;
    private PlayerPose lastShown;

    public PoseLink(Func<double, PlayerPose> path, int seed = 1, SnapshotBufferConfig? config = null)
    {
        Path = path;
        random = new Random(seed);
        Buffer = new PoseBuffer(config ?? new SnapshotBufferConfig());
    }

    public Func<double, PlayerPose> Path { get; }

    public PoseBuffer Buffer { get; }

    public double Latency { get; set; }

    public double Jitter { get; set; }

    public double Loss { get; set; }

    /// <summary>The sender's clock runs this far from the watcher's, as two machines' clocks do.</summary>
    public double ClockDifference { get; set; } = 5000.0;

    public bool Sending { get; set; } = true;

    /// <summary>While set the sender's game stands still: it sends nothing, and nothing is numbered.</summary>
    public bool SenderFrozen { get; set; }

    public double FrameSeconds { get; set; } = 1.0 / 60.0;

    public double Now { get; private set; }

    public bool Visible { get; private set; }

    public PlayerPose Shown => lastShown;

    public double LargestStep { get; private set; }

    /// <summary>Largest and mean distance between what was shown and where the sender really was at that moment.</summary>
    public double LargestError { get; private set; }

    public double MeanError => frames == 0 ? 0 : errorSum / frames;

    private double errorSum;
    private long frames;

    /// <summary>Seconds without a state ahead of the playback, and jumps smoothed, since the measurements were reset.</summary>
    public double Starved => Buffer.StarvedSeconds - starvedAtReset;

    public long Corrections => Buffer.Corrections - correctionsAtReset;

    private double starvedAtReset;
    private long correctionsAtReset;

    public void ResetMeasurements()
    {
        starvedAtReset = Buffer.StarvedSeconds;
        correctionsAtReset = Buffer.Corrections;
        LargestStep = 0;
        LargestError = 0;
        errorSum = 0;
        frames = 0;
    }

    public void Run(double seconds, bool watcherFrozen = false)
    {
        double end = Now + seconds;
        while (Now < end - 1e-9)
        {
            Now += FrameSeconds;
            while (nextSend <= Now)
            {
                if (!SenderFrozen)
                {
                    // Every state gets a number, also the ones the link then loses.
                    ushort number = nextNumber++;
                    if (Sending && random.NextDouble() >= Loss)
                    {
                        double delay = Latency + (((random.NextDouble() * 2.0) - 1.0) * Jitter);
                        inFlight.Add((nextSend + Math.Max(0.0, delay), nextSend + ClockDifference, Path(nextSend), number));
                    }
                }

                nextSend += SendInterval;
            }

            if (!watcherFrozen)
            {
                Frame();
            }
        }
    }

    // One frame of the watching game: read what has arrived, then draw.
    private void Frame()
    {
        inFlight.Sort((a, b) => a.Due.CompareTo(b.Due));
        int arrived = 0;
        while (arrived < inFlight.Count && inFlight[arrived].Due <= Now)
        {
            Buffer.Add(inFlight[arrived].SenderTime, inFlight[arrived].Pose, Now, inFlight[arrived].Number);
            arrived++;
        }

        inFlight.RemoveRange(0, arrived);

        Visible = Buffer.Sample(Now, out PlayerPose pose);
        if (!Visible)
        {
            hasShown = false;
            return;
        }

        if (hasShown)
        {
            LargestStep = Math.Max(LargestStep, PlayerPose.Distance(pose, lastShown));
        }

        double error = PlayerPose.Distance(pose, Path(Buffer.PlaybackTime - ClockDifference));
        LargestError = Math.Max(LargestError, error);
        errorSum += error;
        frames++;
        lastShown = pose;
        hasShown = true;
    }
}

public class PoseBufferTests
{
    private static readonly PlayerPose Centre = new(100, 5, -200, 0, 0, 1.4f);

    private static PlayerPose Bot(double t) => BotPath.Evaluate(t, Centre);

    // Walking east at 2 m/s.
    private static PlayerPose Walk(double t) => new((float)(2.0 * t), 0, 0, 90, 0, 1.4f);

    [Fact]
    public void Nothing_to_show_before_the_first_state()
    {
        var buffer = new PoseBuffer();

        Assert.False(buffer.Sample(1.0, out _));
        Assert.Equal(SnapshotSource.None, buffer.Source);
        Assert.False(buffer.HasData);
    }

    [Fact]
    public void On_a_perfect_link_a_walker_is_shown_a_tenth_of_a_second_behind()
    {
        var link = new PoseLink(Walk);

        link.Run(1);
        link.ResetMeasurements();
        link.Run(5);

        Assert.True(link.Visible);
        Assert.Equal(SnapshotSource.Interpolated, link.Buffer.Source);
        Assert.Equal(0.1, link.Buffer.Delay, 2);

        // Arrival is only noticed once per frame, which the buffer sees as up to a frame of latency.
        Assert.InRange((2.0 * link.Now) - link.Shown.X, 0.2, 0.2 + (2.0 / 60.0) + 0.01);
        Assert.Equal(0, link.Buffer.StarvedSeconds);
        Assert.Equal(0, link.Buffer.Corrections);
        Assert.Equal(0, link.Buffer.Snaps);
        Assert.InRange(link.LargestStep, 0.03, 2.0 / 60.0 * 1.06);
    }

    // The acceptance criterion of Phase 3: with simulated latency the other player moves smoothly and does
    // not teleport. The bot's path is the worst a player on foot can do.
    [Theory]
    [InlineData(0.0, 0.0, 0.0, 60)]
    [InlineData(0.2, 0.03, 0.05, 60)]
    [InlineData(0.2, 0.03, 0.05, 30)]
    [InlineData(0.2, 0.03, 0.05, 144)]
    [InlineData(0.05, 0.02, 0.10, 60)]
    [InlineData(0.4, 0.06, 0.05, 60)]
    public void With_a_bad_link_the_other_player_moves_smoothly_and_does_not_teleport(double latency, double jitter, double loss, int fps)
    {
        var link = new PoseLink(Bot, seed: 7) { Latency = latency, Jitter = jitter, Loss = loss, FrameSeconds = 1.0 / fps };

        link.Run(2);
        link.ResetMeasurements();
        link.Run(2 * BotPath.CycleSeconds);

        PoseBuffer buffer = link.Buffer;
        string report = $"step {link.LargestStep:0.000} m, error mean {link.MeanError:0.000} max {link.LargestError:0.000} m, "
            + $"starved {buffer.StarvedSeconds:0.00}/{buffer.PlayedSeconds:0.0} s, {buffer.Corrections} corrections up to {buffer.LargestCorrection:0.000} m, "
            + $"{buffer.Snaps} snaps, delay {buffer.Delay * 1000:0} ms, {buffer.Late} late of {buffer.Received}";

        // Smooth: never further in one frame than a sprinting player goes, plus what a correction may add.
        double sprintStep = BotPath.SprintSpeed / fps;
        Assert.True(link.LargestStep <= (sprintStep * 1.06) + 0.06, report);
        Assert.True(buffer.Snaps == 0, report);

        // Truthful: what is shown is where the player really was a moment ago.
        // The largest error is at an about-turn in mid sprint, cut short by the straight line between two states.
        Assert.True(link.MeanError < 0.01, report);
        Assert.True(link.LargestError < 0.2, report);

        // Fed: hardly ever without a state ahead of the playback.
        Assert.True(buffer.StarvedSeconds < 0.02 * buffer.PlayedSeconds, report);
        Assert.True(buffer.Delay <= 0.4, report);
    }

    // Why the delay is not simply fixed at a tenth of a second. With little jitter that is enough, and the
    // delay stays there; with a lot, the states arrive too unevenly for it.
    [Theory]
    [InlineData(0.03, false)]
    [InlineData(0.12, true)]
    [InlineData(0.2, true)]
    public void The_delay_adapts_only_when_a_fixed_one_would_run_dry(double jitter, bool fixedRunsDry)
    {
        var fixedDelay = new PoseLink(Bot, seed: 7, config: new SnapshotBufferConfig { MaxDelay = 0.1 }) { Latency = 0.25, Jitter = jitter, Loss = 0.05 };
        var adaptable = new PoseLink(Bot, seed: 7) { Latency = 0.25, Jitter = jitter, Loss = 0.05 };
        foreach (PoseLink link in new[] { fixedDelay, adaptable })
        {
            link.Run(5);
            link.ResetMeasurements();
            link.Run(120);
        }

        string report = $"fixed: starved {fixedDelay.Starved:0.00} s, {fixedDelay.Corrections} corrections, error up to {fixedDelay.LargestError:0.00} m; "
            + $"adaptable: starved {adaptable.Starved:0.00} s, {adaptable.Corrections} corrections, error up to {adaptable.LargestError:0.00} m, delay {adaptable.Buffer.Delay * 1000:0} ms";
        Assert.True(adaptable.Starved < 0.05, report);
        Assert.True(adaptable.Corrections <= 1, report);
        Assert.True(adaptable.LargestError < 0.1, report);
        if (fixedRunsDry)
        {
            Assert.True(fixedDelay.Starved > 0.3, report);
            Assert.True(fixedDelay.LargestError > 0.15, report);
            Assert.True(adaptable.Buffer.Delay > 0.2, report);
        }
        else
        {
            Assert.True(fixedDelay.Starved < 0.05, report);
            Assert.True(adaptable.Buffer.Delay < 0.13, report);
        }
    }

    [Fact]
    public void The_delay_grows_with_jitter_and_shrinks_again()
    {
        var link = new PoseLink(Walk) { Latency = 0.1 };
        link.Run(5);
        double calm = link.Buffer.Delay;

        link.Jitter = 0.06;
        link.Run(10);
        double jittery = link.Buffer.Delay;

        link.Jitter = 0;
        link.Run(15);

        Assert.Equal(0.1, calm, 2);
        Assert.InRange(jittery, 0.13, 0.4);
        Assert.Equal(0.1, link.Buffer.Delay, 2);
    }

    [Fact]
    public void When_states_stop_the_player_carries_on_briefly_and_then_stands_still()
    {
        var link = new PoseLink(Walk);
        link.Run(3);
        link.ResetMeasurements();
        link.Sending = false;

        link.Run(0.2);
        Assert.Equal(SnapshotSource.Extrapolated, link.Buffer.Source);
        float whileExtrapolating = link.Shown.X;

        link.Run(1.0);
        Assert.Equal(SnapshotSource.Held, link.Buffer.Source);
        float held = link.Shown.X;
        link.Run(1.0);

        Assert.True(whileExtrapolating > 5.8f, "kept walking: " + whileExtrapolating);
        Assert.Equal(held, link.Shown.X);

        // It stops where the extrapolation took it: a quarter of a second past the last state.
        Assert.InRange(held, 6.4f, 6.6f);
        Assert.True(link.LargestStep <= 2.0 / 60.0 * 1.06, "step " + link.LargestStep);
    }

    [Fact]
    public void When_states_resume_the_gap_is_closed_smoothly()
    {
        var link = new PoseLink(Walk);
        link.Run(3);
        link.Sending = false;
        link.Run(1.5);
        float stuckAt = link.Shown.X;
        link.ResetMeasurements();

        link.Sending = true;
        link.Run(1.0);

        // The player really is 2.5 m further on. That is closed in a glide, not in one frame.
        Assert.True(link.Buffer.Corrections >= 1);
        Assert.Equal(0, link.Buffer.Snaps);
        Assert.InRange(link.LargestStep, 0.1, 0.45);
        Assert.True(link.Shown.X - stuckAt > 4.0f);
        Assert.InRange((2.0 * link.Now) - link.Shown.X, 0.15, 0.3);
    }

    [Fact]
    public void A_jump_too_large_to_be_a_link_artefact_is_shown_at_once()
    {
        var link = new PoseLink(Walk);
        link.Run(3);
        link.Sending = false;
        link.Run(5);
        link.Sending = true;

        link.Run(1.0);

        // Ten metres behind: gliding there would look like a sprint through walls.
        Assert.Equal(1, link.Buffer.Snaps);
        Assert.InRange((2.0 * link.Now) - link.Shown.X, 0.15, 0.3);
    }

    [Fact]
    public void A_player_who_really_teleports_is_not_slowed_down()
    {
        // Standing still, then somewhere else: going to bed, respawning.
        static PlayerPose Teleporting(double t) => new(t < 3.0 ? 0 : 500, 0, 0, 0, 0, 1.4f);
        var link = new PoseLink(Teleporting);

        link.Run(3.5);

        Assert.Equal(500f, link.Shown.X);
        Assert.Equal(0, link.Buffer.Corrections);
        Assert.Equal(0, link.Buffer.Snaps);
    }

    [Fact]
    public void States_that_arrive_twice_or_out_of_order_are_put_in_their_place()
    {
        var buffer = new PoseBuffer();
        double[] order = { 0.00, 0.10, 0.05, 0.10, 0.20, 0.15, 0.00, 0.25 };
        foreach (double t in order)
        {
            buffer.Add(t, Walk(t), 1000.0 + 0.25);
        }

        Assert.Equal(6, buffer.Received);
        Assert.Equal(2, buffer.Duplicates);

        // The playback sits 0.1 s behind the estimated link time; whatever it is, the pose is on the path.
        Assert.True(buffer.Sample(1000.3, out PlayerPose pose));
        Assert.Equal(SnapshotSource.Interpolated, buffer.Source);
        Assert.InRange(pose.X, 0f, 0.5f);
        Assert.Equal(0f, pose.Z);
    }

    [Fact]
    public void After_this_side_was_frozen_the_queue_of_old_states_does_not_set_the_clock_back()
    {
        var link = new PoseLink(Walk) { Latency = 0.05 };
        link.Run(3);

        // A loading screen: thirty seconds of states pile up and arrive in a single frame.
        link.Run(30, watcherFrozen: true);
        link.Run(0.5);

        Assert.InRange((2.0 * link.Now) - link.Shown.X, 0.25, 0.45);
        Assert.Equal(0, link.Buffer.Resyncs);
        Assert.InRange(link.Buffer.Delay, 0.09, 0.2);
    }

    [Fact]
    public void A_link_that_becomes_much_slower_is_followed_after_a_moment()
    {
        var link = new PoseLink(Walk) { Latency = 0.05 };
        link.Run(3);

        link.Latency = 1.5;
        link.Run(6);

        Assert.Equal(1, link.Buffer.Resyncs);
        Assert.Equal(SnapshotSource.Interpolated, link.Buffer.Source);
        Assert.InRange((2.0 * link.Now) - link.Shown.X, 3.1, 3.4);
    }

    [Fact]
    public void A_sender_whose_clock_starts_over_is_followed_at_once()
    {
        var buffer = new PoseBuffer();
        for (int i = 0; i < 30; i++)
        {
            buffer.Add(900.0 + (i / 30.0), Walk(i / 30.0), 10.0 + (i / 30.0));
            buffer.Sample(10.0 + (i / 30.0), out _);
        }

        // The same player again, with a clock that restarted from a much later value.
        for (int i = 0; i < 30; i++)
        {
            buffer.Add(5000.0 + (i / 30.0), Walk(50 + (i / 30.0)), 11.0 + (i / 30.0));
            buffer.Sample(11.0 + (i / 30.0), out _);
        }

        Assert.Equal(1, buffer.Resyncs);
        Assert.True(buffer.Sample(12.0, out PlayerPose pose));
        Assert.InRange(pose.X, 101.0f, 102.1f);
    }

    [Fact]
    public void Clearing_hides_the_player_until_new_states_arrive()
    {
        var link = new PoseLink(Walk);
        link.Run(2);

        link.Buffer.Clear();

        Assert.False(link.Buffer.Sample(link.Now, out _));
        Assert.False(link.Buffer.HasData);
        link.Run(0.5);
        Assert.True(link.Visible);
        Assert.InRange((2.0 * link.Now) - link.Shown.X, 0.15, 0.3);
    }

    [Fact]
    public void A_crouch_and_a_turn_are_interpolated_too()
    {
        var link = new PoseLink(Bot) { Latency = 0.1, Jitter = 0.02, Loss = 0.05 };
        var eyes = new List<float>();
        while (link.Now < BotPath.CycleSeconds)
        {
            link.Run(1.0 / 60.0);
            if (link.Visible)
            {
                eyes.Add(link.Shown.EyeHeight);
            }
        }

        Assert.Equal(BotPath.LowEyes, eyes.Min(), 2);
        Assert.Equal(BotPath.StandingEyes, eyes.Max(), 2);

        // No frame lowers the eyes by more than the crouch itself does in that time.
        double largest = 0;
        for (int i = 1; i < eyes.Count; i++)
        {
            largest = Math.Max(largest, Math.Abs(eyes[i] - eyes[i - 1]));
        }

        Assert.InRange(largest, 0.01, 0.12);
    }

    // The buffer running dry has three possible causes, and the summary in the log has to say which.
    [Fact]
    public void Lost_states_are_counted_and_not_mistaken_for_a_pause()
    {
        var link = new PoseLink(Walk, seed: 3) { Latency = 0.05, Jitter = 0.02, Loss = 0.1 };

        link.Run(60);

        // 1800 states were numbered and about a tenth of them never arrived.
        Assert.InRange(link.Buffer.Lost, 130, 230);
        Assert.InRange(link.Buffer.Lost + link.Buffer.Received, 1795, 1802);
        Assert.Equal(0, link.Buffer.SenderPauses);
        Assert.Equal(0, link.Buffer.LocalStalls);
    }

    [Fact]
    public void A_sender_whose_game_stands_still_is_told_apart_from_a_lossy_link()
    {
        var link = new PoseLink(Walk) { Latency = 0.05 };
        link.Run(3);

        for (int i = 0; i < 4; i++)
        {
            link.SenderFrozen = true;
            link.Run(0.4);
            link.SenderFrozen = false;
            link.Run(2);
        }

        Assert.Equal(4, link.Buffer.SenderPauses);
        Assert.InRange(link.Buffer.LongestSenderPause, 0.33, 0.45);
        Assert.Equal(0, link.Buffer.Lost);
        Assert.Equal(0, link.Buffer.LocalStalls);

        // Each pause leaves the buffer dry for most of its length, and nothing here could have prevented it.
        Assert.InRange(link.Buffer.StarvedSeconds, 0.8, 1.6);
        Assert.Equal(0, link.Buffer.Snaps);
    }

    [Fact]
    public void A_pause_is_recognised_even_when_states_around_it_were_lost()
    {
        var link = new PoseLink(Walk, seed: 9) { Latency = 0.05, Loss = 0.3 };
        link.Run(3);

        for (int i = 0; i < 10; i++)
        {
            link.SenderFrozen = true;
            link.Run(0.5);
            link.SenderFrozen = false;
            link.Run(1.5);
        }

        // Three lost in a row is 0.13 s of silence: not a pause. Half a second with nothing numbered is.
        Assert.Equal(10, link.Buffer.SenderPauses);
        Assert.InRange(link.Buffer.LongestSenderPause, 0.4, 0.55);
    }

    [Fact]
    public void This_side_standing_still_is_counted_too()
    {
        var link = new PoseLink(Walk) { Latency = 0.05 };
        link.Run(3);

        link.Run(0.3, watcherFrozen: true);
        link.Run(2);

        Assert.Equal(1, link.Buffer.LocalStalls);
        Assert.InRange(link.Buffer.LongestLocalStall, 0.3, 0.34);
        Assert.Equal(0, link.Buffer.SenderPauses);
        Assert.Equal(0, link.Buffer.Lost);
    }

    [Fact]
    public void The_numbering_wraps_around_without_inventing_losses()
    {
        // The test link starts numbering close to the top of the 16 bits.
        var link = new PoseLink(Walk) { Latency = 0.05, Jitter = 0.04 };

        link.Run(40);

        Assert.Equal(0, link.Buffer.Lost);
        Assert.InRange(link.Buffer.Received, 1195, 1202);
        Assert.Equal(0, link.Buffer.SenderPauses);
    }

    [Fact]
    public void What_was_lost_is_remembered_when_the_player_leaves_the_world_and_comes_back()
    {
        var link = new PoseLink(Walk, seed: 5) { Loss = 0.2 };
        link.Run(20);
        long lost = link.Buffer.Lost;

        link.Buffer.Clear();
        long afterClearing = link.Buffer.Lost;
        link.Run(20);

        Assert.InRange(lost, 80, 160);
        Assert.Equal(lost, afterClearing);
        Assert.InRange(link.Buffer.Lost, lost + 80, lost + 160);
    }
}
