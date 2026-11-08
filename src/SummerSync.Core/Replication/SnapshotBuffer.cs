using System;

namespace SummerSync.Core.Replication
{
    /// <summary>How the value returned by <see cref="SnapshotBuffer{T}.Sample"/> was obtained.</summary>
    public enum SnapshotSource
    {
        /// <summary>Nothing has arrived yet.</summary>
        None,

        /// <summary>Between two received states: the normal case.</summary>
        Interpolated,

        /// <summary>Past the newest state, continuing its movement for a moment.</summary>
        Extrapolated,

        /// <summary>Past the newest state for too long: standing still where the movement was last taken to.</summary>
        Held,
    }

    /// <summary>
    /// What a <see cref="SnapshotBuffer{T}"/> needs to know about the thing it interpolates: where it is, and
    /// how to get from one state of it to another.
    /// </summary>
    public interface ISnapshot<T>
    {
        /// <summary>Metres between the two positions.</summary>
        double DistanceTo(T other);

        /// <summary>The state a fraction <paramref name="t"/> of the way from this one to <paramref name="other"/>.</summary>
        T LerpTo(T other, double t);

        /// <summary>This position minus the position of <paramref name="other"/>, in metres.</summary>
        void OffsetFrom(T other, out double x, out double y, out double z);

        /// <summary>The same state, moved by that many metres.</summary>
        T Moved(double x, double y, double z);
    }

    public sealed class SnapshotBufferConfig
    {
        public SnapshotBufferConfig()
        {
            MinDelay = 0.1;
            MaxDelay = 0.4;
            MaxExtrapolation = 0.25;
            CorrectionTime = 0.1;
            SnapDistance = 5.0;
            MaxClockSlew = 0.05;
            ResyncAfter = 0.5;
            StaleResyncSeconds = 2.0;
        }

        /// <summary>Seconds the playback runs behind what was received, on a perfect link.</summary>
        public double MinDelay { get; set; }

        /// <summary>The delay grows with the jitter of the link, up to this.</summary>
        public double MaxDelay { get; set; }

        /// <summary>Seconds a movement is continued along its last known velocity when no state arrives.</summary>
        public double MaxExtrapolation { get; set; }

        /// <summary>Time constant, in seconds, over which a position error is blended away instead of jumped.</summary>
        public double CorrectionTime { get; set; }

        /// <summary>An error larger than this many metres is not blended: the thing is moved there at once.</summary>
        public double SnapDistance { get; set; }

        /// <summary>How much faster or slower than real time the playback may run to follow the link (0.05 = 5 %).</summary>
        public double MaxClockSlew { get; set; }

        /// <summary>A state this many seconds fresher than expected means the clocks jumped: start over from it.</summary>
        public double ResyncAfter { get; set; }

        /// <summary>States that keep arriving that stale for this long mean the link got slower: start over.</summary>
        public double StaleResyncSeconds { get; set; }
    }

    /// <summary>
    /// Snapshot interpolation for one remote thing: a player, a loose object. States arrive late, unevenly and
    /// with gaps; each carries the sender's clock. The buffer plays them back a little behind the newest one,
    /// so that there is almost always a state on either side of the moment being shown, and moves between them
    /// in a straight line.
    ///
    /// When the buffer runs dry it continues the last movement briefly and then stands still. Whenever what it
    /// would show jumps for a reason other than the thing's own movement (states resuming, a late state
    /// filling a gap, the clocks being realigned), the jump is blended away over a tenth of a second.
    ///
    /// Nothing here reads a clock: the caller passes the local time, in seconds, to every call.
    /// </summary>
    public class SnapshotBuffer<T>
        where T : struct, ISnapshot<T>
    {
        private const int Capacity = 64;
        private const double KeepBehindPlayback = 1.0;
        private const double OffsetGain = 0.1;
        private const double JitterGain = 0.1;
        private const double IntervalGain = 0.1;
        private const double DefaultInterval = 1.0 / 30.0;
        private const double LocalPauseSeconds = 1.0;

        // The first states after the clock is started set it by plain averaging, and the playback follows without
        // a speed limit: one state is a poor estimate of the link, and nobody is watching closely yet.
        private const int WarmUpStates = 10;

        // Consecutive states this much further apart than the sender's rhythm: its game stood still in between.
        private const double SenderPauseSeconds = 0.15;

        // Two samples this far apart: this game stood still in between.
        private const double LocalStallSeconds = 0.1;

        // Slack, in metres, when deciding whether a movement is the thing's own. It covers float rounding.
        private const double StepMargin = 0.01;

        private readonly SnapshotBufferConfig config;
        private readonly double[] times = new double[Capacity];
        private readonly T[] states = new T[Capacity];
        private int count;

        // Playback clock: local time + effective = the sender's time being shown.
        private bool hasClock;
        private double targetOffset;
        private double effective;
        private bool alignNow;
        private double jitter;
        private double interval = DefaultInterval;
        private double staleSince = double.NaN;
        private double backlogAt = double.NaN;
        private int statesSinceStart;

        // The sender's numbering of its states, unwrapped: which ones exist, as opposed to which ones arrived.
        private bool hasNumbers;
        private ushort newestSequence;
        private long newestNumber;
        private long firstNumber;
        private double newestNumberTime;
        private long numbered;
        private long lostBefore;

        // Set by AddFinal: the newest state is where the thing came to rest, and nothing continues past it.
        private bool ended;

        private bool hasOutput;
        private double lastNow;
        private double playbackTime;
        private T lastRaw;
        private T lastOutput;
        private double errorX;
        private double errorY;
        private double errorZ;

        public SnapshotBuffer()
            : this(new SnapshotBufferConfig())
        {
        }

        public SnapshotBuffer(SnapshotBufferConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            this.config = config;
        }

        /// <summary>How the last sampled state was obtained.</summary>
        public SnapshotSource Source { get; private set; }

        /// <summary>Seconds the playback currently runs behind the link.</summary>
        public double Delay { get; private set; }

        /// <summary>The moment being shown, on the sender's clock, as of the last <see cref="Sample"/>.</summary>
        public double PlaybackTime
        {
            get { return playbackTime; }
        }

        /// <summary>States accepted.</summary>
        public long Received { get; private set; }

        /// <summary>States that arrived twice.</summary>
        public long Duplicates { get; private set; }

        /// <summary>States that arrived after the moment they describe had already been shown.</summary>
        public long Late { get; private set; }

        /// <summary>Times the playback clock was started over.</summary>
        public long Resyncs { get; private set; }

        /// <summary>Seconds sampled in total.</summary>
        public double PlayedSeconds { get; private set; }

        /// <summary>Seconds sampled without a state ahead of the playback.</summary>
        public double StarvedSeconds { get; private set; }

        /// <summary>Jumps that were blended away.</summary>
        public long Corrections { get; private set; }

        /// <summary>The largest blended jump, in metres.</summary>
        public double LargestCorrection { get; private set; }

        /// <summary>Jumps too large to blend, shown as they are.</summary>
        public long Snaps { get; private set; }

        /// <summary>States the sender numbered that never arrived.</summary>
        public long Lost
        {
            get { return lostBefore + (hasNumbers ? Math.Max(0L, (newestNumber - firstNumber + 1) - numbered) : 0L); }
        }

        /// <summary>Times the sender's game stood still: nothing was lost, it simply sent nothing for a while.</summary>
        public long SenderPauses { get; private set; }

        /// <summary>The longest of those pauses, in seconds beyond the sender's normal rhythm.</summary>
        public double LongestSenderPause { get; private set; }

        /// <summary>Times this side went more than a tenth of a second without sampling: its own game stood still.</summary>
        public long LocalStalls { get; private set; }

        public double LongestLocalStall { get; private set; }

        /// <summary>States waiting ahead of the playback.</summary>
        public int Ahead
        {
            get
            {
                int ahead = 0;
                for (int i = count - 1; i >= 0 && times[i] > playbackTime; i--)
                {
                    ahead++;
                }

                return ahead;
            }
        }

        public bool HasData
        {
            get { return count > 0; }
        }

        /// <summary>
        /// Whether the playback has reached a final state (see <see cref="AddFinal"/>): the thing is shown at
        /// rest and will stay there until something new arrives.
        /// </summary>
        public bool AtRest
        {
            get { return ended && hasOutput && count > 0 && playbackTime >= times[count - 1]; }
        }

        /// <summary>Forgets everything received, e.g. when the player leaves the game world. Statistics are kept.</summary>
        public void Clear()
        {
            lostBefore = Lost;
            hasNumbers = false;
            numbered = 0;
            count = 0;
            hasClock = false;
            hasOutput = false;
            ended = false;
            staleSince = double.NaN;
            jitter = 0;
            errorX = errorY = errorZ = 0;
            Source = SnapshotSource.None;
        }

        /// <summary>
        /// Stores a state. <paramref name="senderTime"/> is the sender's clock when it was captured and
        /// <paramref name="now"/> the local clock on arrival, both in seconds. The two clocks need not agree.
        /// </summary>
        public void Add(double senderTime, T state, double now)
        {
            double sampleOffset = senderTime - now;
            if (!hasClock)
            {
                StartClock(sampleOffset);
            }
            else if (hasOutput && now - lastNow > LocalPauseSeconds)
            {
                // Nothing was drawn for a while: this side was frozen (a loading screen) or the thing was not
                // being shown. What arrives now is a backlog, most of it old. The freshest state in it says
                // where the link is; the rest must not drag the clock back.
                if (backlogAt != now || sampleOffset > targetOffset)
                {
                    backlogAt = now;
                    StartClock(sampleOffset);
                }
            }
            else
            {
                double deviation = sampleOffset - targetOffset;
                if (deviation > config.ResyncAfter)
                {
                    // Much fresher than anything before: the sender's clock or ours jumped. Whatever is buffered
                    // belongs to the old alignment.
                    Resyncs++;
                    count = 0;
                    StartClock(sampleOffset);
                }
                else if (deviation < -config.ResyncAfter)
                {
                    // Much staler. After a freeze on this side a whole queue of old states arrives in one frame;
                    // they must not drag the clock back. Only a link that stays this slow does.
                    if (double.IsNaN(staleSince))
                    {
                        staleSince = now;
                    }
                    else if (now - staleSince >= config.StaleResyncSeconds)
                    {
                        Resyncs++;
                        count = 0;
                        StartClock(sampleOffset);
                    }
                }
                else
                {
                    staleSince = double.NaN;
                    statesSinceStart++;
                    double weight = Math.Max(OffsetGain, 1.0 / statesSinceStart);
                    targetOffset += deviation * weight;
                    jitter += (Math.Abs(deviation) - jitter) * Math.Max(JitterGain, weight);
                }
            }

            // Anything newer than a final state means the thing is on the move again.
            if (ended && count > 0 && senderTime > times[count - 1])
            {
                ended = false;
            }

            Insert(senderTime, state);
        }

        /// <summary>
        /// Stores a state that the sender numbered. The numbers are what tell a state lost on the way from one
        /// that was never sent.
        /// </summary>
        public void Add(double senderTime, T state, double now, ushort sequence)
        {
            long before = Received;
            Add(senderTime, state, now);
            if (Received != before)
            {
                Number(sequence, senderTime);
            }
        }

        /// <summary>
        /// Stores the state in which the thing came to rest. The playback ends exactly there: nothing is
        /// continued past it, and being there does not count as running dry.
        /// </summary>
        public void AddFinal(double senderTime, T state, double now)
        {
            Add(senderTime, state, now);
            if (count > 0 && senderTime >= times[count - 1])
            {
                ended = true;
            }
        }

        /// <summary>
        /// The state to show at local time <paramref name="now"/>. Returns false while nothing has arrived.
        /// Call it once per rendered frame: it advances the playback clock and the statistics.
        /// </summary>
        public bool Sample(double now, out T state)
        {
            if (!hasClock || count == 0)
            {
                state = lastOutput;
                Source = SnapshotSource.None;
                return false;
            }

            double elapsed = hasOutput ? Math.Max(0.0, now - lastNow) : 0.0;
            lastNow = now;
            if (elapsed > LocalStallSeconds)
            {
                LocalStalls++;
                LongestLocalStall = Math.Max(LongestLocalStall, elapsed);
            }

            // Two send intervals cover one lost state; the jitter term covers states that arrive late. It is kept
            // small on purpose: running dry now and then is cheap (see the extrapolation), delay is paid always.
            double wantedDelay = Math.Max(config.MinDelay, Math.Min(config.MaxDelay, (2.0 * interval) + (1.5 * jitter)));
            double wanted = targetOffset - wantedDelay;
            if (alignNow || !hasOutput || statesSinceStart < WarmUpStates)
            {
                effective = wanted;
                alignNow = false;
            }
            else
            {
                double slew = config.MaxClockSlew * elapsed;
                effective += Math.Max(-slew, Math.Min(slew, wanted - effective));
            }

            Delay = targetOffset - effective;
            double previousPlayback = playbackTime;
            playbackTime = now + effective;

            SnapshotSource source;
            T raw = At(playbackTime, out source);
            Source = source;

            if (!hasOutput)
            {
                errorX = errorY = errorZ = 0;
            }
            else if (raw.DistanceTo(lastRaw) > Travelled(previousPlayback, playbackTime) + StepMargin)
            {
                // The thing did not move like this; the link did. Keep showing where it was and let the
                // difference fade.
                lastOutput.OffsetFrom(raw, out errorX, out errorY, out errorZ);
                double size = Math.Sqrt((errorX * errorX) + (errorY * errorY) + (errorZ * errorZ));
                if (size > config.SnapDistance)
                {
                    errorX = errorY = errorZ = 0;
                    Snaps++;
                }
                else
                {
                    Corrections++;
                    LargestCorrection = Math.Max(LargestCorrection, size);
                }
            }
            else
            {
                double decay = config.CorrectionTime > 0 ? Math.Exp(-elapsed / config.CorrectionTime) : 0.0;
                errorX *= decay;
                errorY *= decay;
                errorZ *= decay;
            }

            state = raw.Moved(errorX, errorY, errorZ);

            // A long gap between two samples was this side not drawing, not the thing being shown.
            if (elapsed <= LocalPauseSeconds)
            {
                PlayedSeconds += elapsed;
                if (source != SnapshotSource.Interpolated)
                {
                    StarvedSeconds += elapsed;
                }
            }

            lastRaw = raw;
            lastOutput = state;
            hasOutput = true;
            Trim();
            return true;
        }

        private void Number(ushort sequence, double senderTime)
        {
            if (!hasNumbers)
            {
                hasNumbers = true;
                newestSequence = sequence;
                newestNumber = 0;
                firstNumber = 0;
                newestNumberTime = senderTime;
                numbered = 1;
                return;
            }

            numbered++;
            long number = newestNumber + unchecked((short)(sequence - newestSequence));
            if (number > newestNumber)
            {
                // Longer than the states in between account for: the sender was not sending.
                double pause = (senderTime - newestNumberTime) - ((number - newestNumber) * interval);
                if (pause > SenderPauseSeconds)
                {
                    SenderPauses++;
                    LongestSenderPause = Math.Max(LongestSenderPause, pause);
                }

                newestNumber = number;
                newestSequence = sequence;
                newestNumberTime = senderTime;
            }
            else if (number < firstNumber)
            {
                firstNumber = number;
            }
        }

        private void StartClock(double sampleOffset)
        {
            hasClock = true;
            targetOffset = sampleOffset;
            statesSinceStart = 1;
            jitter = 0;
            staleSince = double.NaN;
            alignNow = true;
        }

        private void Insert(double time, T state)
        {
            int at = count;
            while (at > 0 && times[at - 1] > time)
            {
                at--;
            }

            if (at > 0 && times[at - 1] == time)
            {
                Duplicates++;
                return;
            }

            Received++;
            if (hasOutput && time < playbackTime)
            {
                Late++;
            }

            if (at == count && count > 0)
            {
                double delta = time - times[count - 1];
                if (delta < 1.0)
                {
                    interval += (delta - interval) * IntervalGain;
                }
            }

            if (count == Capacity)
            {
                if (at == 0)
                {
                    // Older than everything in a full buffer: of no use.
                    return;
                }

                Array.Copy(times, 1, times, 0, count - 1);
                Array.Copy(states, 1, states, 0, count - 1);
                count--;
                at--;
            }

            Array.Copy(times, at, times, at + 1, count - at);
            Array.Copy(states, at, states, at + 1, count - at);
            times[at] = time;
            states[at] = state;
            count++;
        }

        // The state at sender time t according to what is in the buffer.
        private T At(double t, out SnapshotSource source)
        {
            if (t <= times[0])
            {
                source = SnapshotSource.Interpolated;
                return states[0];
            }

            int last = count - 1;
            if (t < times[last])
            {
                int after = 1;
                while (times[after] < t)
                {
                    after++;
                }

                source = SnapshotSource.Interpolated;
                double fraction = (t - times[after - 1]) / (times[after] - times[after - 1]);
                return states[after - 1].LerpTo(states[after], fraction);
            }

            if (ended)
            {
                source = SnapshotSource.Interpolated;
                return states[last];
            }

            // Past the newest state. Two states that are close in time give a velocity to continue with; when
            // that runs out the thing stops where it took it, not back at the last state.
            double past = t - times[last];
            double span = count >= 2 ? times[last] - times[last - 1] : double.PositiveInfinity;
            source = past <= config.MaxExtrapolation && span <= config.MaxExtrapolation ? SnapshotSource.Extrapolated : SnapshotSource.Held;
            if (past == 0.0)
            {
                source = SnapshotSource.Interpolated;
            }

            if (span > config.MaxExtrapolation)
            {
                return states[last];
            }

            double factor = Math.Min(past, config.MaxExtrapolation) / span;
            double x;
            double y;
            double z;
            states[last].OffsetFrom(states[last - 1], out x, out y, out z);
            return states[last].Moved(x * factor, y * factor, z * factor);
        }

        // How far the thing moved between two playback times according to the states now in the buffer. A
        // shown movement longer than this is not its own: it is an artefact of the link.
        private double Travelled(double from, double to)
        {
            if (to < from)
            {
                double swap = from;
                from = to;
                to = swap;
            }

            SnapshotSource ignored;
            T position = At(from, out ignored);
            double distance = 0.0;
            for (int i = 0; i < count && times[i] < to; i++)
            {
                if (times[i] > from)
                {
                    distance += position.DistanceTo(states[i]);
                    position = states[i];
                }
            }

            return distance + position.DistanceTo(At(to, out ignored));
        }

        private void Trim()
        {
            int drop = 0;
            while (count - drop > 2 && times[drop + 1] < playbackTime - KeepBehindPlayback)
            {
                drop++;
            }

            if (drop > 0)
            {
                Array.Copy(times, drop, times, 0, count - drop);
                Array.Copy(states, drop, states, 0, count - drop);
                count -= drop;
            }
        }
    }
}
