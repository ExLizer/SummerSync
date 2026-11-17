using System;
using System.Globalization;
using System.Text;
using SummerSync.Core.Replication;

namespace SummerSync.Core.Objects
{
    /// <summary>The running totals of a followed thing's playback at one moment (see <see cref="SnapshotBuffer{T}"/>).</summary>
    public struct FollowCounters
    {
        public long Received;
        public long Late;
        public long Lost;
        public long Resyncs;
        public long Corrections;
        public long Snaps;
        public long SenderPauses;
        public long LocalStalls;
        public double Starved;
        public double LongestSenderPause;
        public double LongestLocalStall;
        public double LargestCorrection;
        public double Delay;

        /// <summary>The totals of that playback now.</summary>
        public static FollowCounters Of<T>(SnapshotBuffer<T> buffer)
            where T : struct, ISnapshot<T>
        {
            var counters = default(FollowCounters);
            if (buffer == null)
            {
                return counters;
            }

            counters.Received = buffer.Received;
            counters.Late = buffer.Late;
            counters.Lost = buffer.Lost;
            counters.Resyncs = buffer.Resyncs;
            counters.Corrections = buffer.Corrections;
            counters.Snaps = buffer.Snaps;
            counters.SenderPauses = buffer.SenderPauses;
            counters.LocalStalls = buffer.LocalStalls;
            counters.Starved = buffer.StarvedSeconds;
            counters.LongestSenderPause = buffer.LongestSenderPause;
            counters.LongestLocalStall = buffer.LongestLocalStall;
            counters.LargestCorrection = buffer.LargestCorrection;
            counters.Delay = buffer.Delay;
            return counters;
        }

        // Whether these totals can follow those: a new playback (the thing changed hands) starts from zero.
        internal bool Follows(FollowCounters before)
        {
            return Received >= before.Received && Late >= before.Late && Lost >= before.Lost && Resyncs >= before.Resyncs
                && Corrections >= before.Corrections && Snaps >= before.Snaps && SenderPauses >= before.SenderPauses
                && LocalStalls >= before.LocalStalls;
        }
    }

    /// <summary>What a window of following a moving vehicle looked like.</summary>
    public struct FollowReport
    {
        public double Seconds;
        public int Steps;
        public int Extrapolated;
        public int Held;
        public int Jolts;
        public double LargestJolt;
        public double LargestJoltAfter;
        public long Received;
        public long Late;
        public long Lost;
        public long Resyncs;
        public long Corrections;
        public long Snaps;
        public long SenderPauses;
        public long LocalStalls;
        public double Starved;
        public FollowCounters Totals;

        /// <summary>
        /// Whether the vehicle could be seen to stand, jump or be pulled back in that window: a jolt, a step held still past
        /// the newest state, a snap or a restart of the playback, a pause of either game, or more than one step in twenty
        /// past the newest state.
        /// </summary>
        public bool Rough
        {
            get
            {
                return Jolts > 0 || Held > 0 || Snaps > 0 || Resyncs > 0 || SenderPauses > 0 || LocalStalls > 0
                    || (Steps > 0 && Extrapolated * 20 > Steps);
            }
        }

        /// <summary>The window in one line of English, for the log.</summary>
        public string Describe()
        {
            var text = new StringBuilder(320);
            text.Append(Seconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s, ").Append(Steps).Append(" step(s): ")
                .Append(Extrapolated).Append(" past the newest state (").Append(Held).Append(" of them standing still), ")
                .Append(Jolts).Append(" jolt(s)");
            if (Jolts > 0)
            {
                text.Append(" (the largest ").Append(Metres(LargestJolt)).Append(" after a step of ").Append(Metres(LargestJoltAfter)).Append(')');
            }

            text.Append("; the stream: ").Append(Received).Append(" state(s), ").Append(Late).Append(" late, ").Append(Lost).Append(" lost, ")
                .Append(Starved.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s with nothing ahead, ")
                .Append(SenderPauses).Append(" pause(s) of the sending game, ").Append(LocalStalls).Append(" stall(s) of this game, ")
                .Append(Corrections).Append(" correction(s), ").Append(Snaps).Append(" snap(s), ").Append(Resyncs).Append(" restart(s); delay ")
                .Append(Totals.Delay.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s; the longest so far: a pause of the sending game ")
                .Append(Totals.LongestSenderPause.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s, a stall of this game ")
                .Append(Totals.LongestLocalStall.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s, a correction ")
                .Append(Metres(Totals.LargestCorrection));
            return text.ToString();
        }

        private static string Metres(double value)
        {
            return value.ToString(value < 1.0 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " m";
        }
    }

    /// <summary>
    /// Measures how evenly a vehicle that another machine runs is shown here, in windows of <see cref="WindowSeconds"/> while it
    /// moves: each step of physics says where its place came from (between two states, past the newest one, standing still
    /// there) and whether it jolted; at the end of a window the playback's totals say what the stream did meanwhile.
    ///
    /// Why (docs/GAME_NOTES.md, "El rubber banding cerca de los edificios", 2026-11-17): players see vehicles jump back and
    /// forth near the big buildings, and the log could not tell a hitch of either game from a gap in the stream or a hand-over.
    /// </summary>
    public sealed class FollowQuality
    {
        /// <summary>Seconds of following that make a window.</summary>
        public const double WindowSeconds = 10.0;

        /// <summary>
        /// Steps in which the vehicle moved that a window needs (half a second of physics): one that stood where its owner
        /// left it, with nothing new in the stream, says nothing about how it is followed.
        /// </summary>
        public const int MovingStepsAtLeast = 25;

        private bool open;
        private double startedAt;
        private FollowCounters startTotals;
        private int steps;
        private int movingSteps;
        private int extrapolated;
        private int held;
        private int jolts;
        private double largestJolt;
        private double largestJoltAfter;

        /// <summary>Windows closed so far, and how many of them were rough.</summary>
        public int Windows { get; private set; }

        public int RoughWindows { get; private set; }

        /// <summary>One step of physics of the vehicle being moved to where its owner has it.</summary>
        public void Step(SnapshotSource source, bool jolted, double jolt, double after, bool moving = true)
        {
            steps++;
            if (moving)
            {
                movingSteps++;
            }

            if (source == SnapshotSource.Extrapolated)
            {
                extrapolated++;
            }
            else if (source == SnapshotSource.Held)
            {
                extrapolated++;
                held++;
            }

            if (jolted)
            {
                jolts++;
                if (jolt > largestJolt)
                {
                    largestJolt = jolt;
                    largestJoltAfter = after;
                }
            }
        }

        /// <summary>
        /// Once a frame. True when a window ends, with what it looked like; a window with no step in it (the vehicle stood) is
        /// dropped, and so is one whose playback started over in between (the totals went down).
        /// </summary>
        public bool Close(double now, FollowCounters totals, out FollowReport report)
        {
            report = default(FollowReport);
            if (!open || !totals.Follows(startTotals))
            {
                Begin(now, totals);
                return false;
            }

            if (now - startedAt < WindowSeconds)
            {
                return false;
            }

            if (movingSteps < MovingStepsAtLeast)
            {
                Begin(now, totals);
                return false;
            }

            report.Seconds = now - startedAt;
            report.Steps = steps;
            report.Extrapolated = extrapolated;
            report.Held = held;
            report.Jolts = jolts;
            report.LargestJolt = largestJolt;
            report.LargestJoltAfter = largestJoltAfter;
            report.Received = totals.Received - startTotals.Received;
            report.Late = totals.Late - startTotals.Late;
            report.Lost = totals.Lost - startTotals.Lost;
            report.Resyncs = totals.Resyncs - startTotals.Resyncs;
            report.Corrections = totals.Corrections - startTotals.Corrections;
            report.Snaps = totals.Snaps - startTotals.Snaps;
            report.SenderPauses = totals.SenderPauses - startTotals.SenderPauses;
            report.LocalStalls = totals.LocalStalls - startTotals.LocalStalls;
            report.Starved = Math.Max(0.0, totals.Starved - startTotals.Starved);
            report.Totals = totals;
            Windows++;
            if (report.Rough)
            {
                RoughWindows++;
            }

            Begin(now, totals);
            return true;
        }

        /// <summary>The vehicle is no longer followed here: the next window starts afresh.</summary>
        public void Stop()
        {
            open = false;
        }

        private void Begin(double now, FollowCounters totals)
        {
            open = true;
            startedAt = now;
            startTotals = totals;
            steps = 0;
            movingSteps = 0;
            extrapolated = 0;
            held = 0;
            jolts = 0;
            largestJolt = 0.0;
            largestJoltAfter = 0.0;
        }
    }
}
