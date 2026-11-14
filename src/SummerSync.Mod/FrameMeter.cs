using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SummerSync.Core.Logging;
using UnityEngine;

namespace SummerSync.Unity
{
    /// <summary>
    /// Writes down the frames that take long, with how much of each the mod spent and on what, whether the garbage collector
    /// ran in it, and once a minute how much memory each part of the mod asked for.
    ///
    /// Why (2026-11-14 reports): vehicles seen to stand and then be somewhere else when their driver comes near the house, the
    /// repair shop or the store, and a log with 41 such stops in four minutes of a ride. A machine that stops for a moment
    /// sends nothing meanwhile, and the others' copy of its vehicle stands and jumps. Measured with two copies the same day:
    /// most long frames are the game's, and a few of about 155 ms fell on whichever part of the mod was running (the loose
    /// objects once, the doors another time): the size of a pause of the game's garbage collector, which stops everything and
    /// lands where memory was asked for. The less the mod asks for, the fewer of those pauses.
    /// </summary>
    internal sealed class FrameMeter
    {
        /// <summary>What a frame's time is spent on, by the mod.</summary>
        internal enum Part
        {
            Physics,
            Update,
            Vehicles,
            Players,
            Objects,
            Doors,
            Rest,
            Count,
        }

        // A frame slower than this is written down, at most once in this many seconds.
        private const float SlowFrame = 0.15f;
        private const double LogEvery = 2.0;

        // How often what the mod asked of the memory is written down.
        private const double MemoryEvery = 60.0;
        private const int DetailShown = 10;

        private static readonly string[] Names = { "physics steps", "its update", "vehicles", "players", "loose objects", "doors", "the rest" };

        private readonly Logger log;
        private readonly double[] spent = new double[(int)Part.Count];
        private readonly double[] lastFrame = new double[(int)Part.Count];
        private readonly long[] asked = new long[(int)Part.Count];
        private double markAt;
        private long markMemory;
        private double nextLog;
        private double nextMemory = double.NaN;
        private int slow;
        private int unsaid;
        private int collectionsAtFrame;
        private int collectionsInLastFrame;
        private int collectionsAtMemory;

        // The meter of the running game, for the marks of the parts that have no meter of their own.
        private static FrameMeter current;

        // What each finer part of the mod asked of the memory (the code since the mark before), named by a fixed text.
        private readonly Dictionary<string, long> detail = new Dictionary<string, long>();
        private long detailMemory;

        public FrameMeter(Logger log)
        {
            this.log = log;
            collectionsAtFrame = Collections();
            collectionsAtMemory = collectionsAtFrame;
            current = this;
        }

        /// <summary>
        /// What the mod asked of the memory since the last mark goes to that name (a fixed text, never one made on the spot):
        /// to tell which part of the mod makes the most garbage.
        /// </summary>
        public static void Mark(string name)
        {
            FrameMeter meter = current;
            if (meter == null)
            {
                return;
            }

            long memory = GC.GetTotalMemory(false);
            if (memory > meter.detailMemory)
            {
                long had;
                meter.detail.TryGetValue(name, out had);
                meter.detail[name] = had + (memory - meter.detailMemory);
            }

            meter.detailMemory = memory;
        }

        /// <summary>Starts timing a stretch of the mod's work.</summary>
        public void Start(double now)
        {
            markAt = now;
            markMemory = GC.GetTotalMemory(false);
            detailMemory = markMemory;
        }

        /// <summary>The time since the last mark goes to that part, and what it asked of the memory.</summary>
        public void Add(Part part, double now)
        {
            double took = now - markAt;
            if (took > 0.0 && took < 60.0)
            {
                spent[(int)part] += took;
            }

            // A collection in between makes it less, not more: then nothing is counted.
            long memory = GC.GetTotalMemory(false);
            if (memory > markMemory)
            {
                asked[(int)part] += memory - markMemory;
            }

            markAt = now;
            markMemory = memory;
        }

        /// <summary>At the end of the frame's work: what the mod spent is kept to be told if the frame turns out slow.</summary>
        public void EndFrame()
        {
            for (int i = 0; i < spent.Length; i++)
            {
                lastFrame[i] = spent[i];
                spent[i] = 0.0;
            }

            int collections = Collections();
            collectionsInLastFrame = collections - collectionsAtFrame;
            collectionsAtFrame = collections;
        }

        /// <summary>
        /// At the start of a frame: if the one before took long, writes it down with what the mod spent in it, where the local
        /// player was and how fast it went; and once a minute, in a session, what the mod asked of the memory.
        /// </summary>
        public void CheckLastFrame(double now, Transform player, bool inSession, float speed)
        {
            if (!inSession)
            {
                nextMemory = double.NaN;
                return;
            }

            SayMemory(now);
            float frame = Time.unscaledDeltaTime;
            if (frame <= SlowFrame)
            {
                return;
            }

            slow++;
            if (now < nextLog)
            {
                unsaid++;
                return;
            }

            nextLog = now + LogEvery;
            double mod = 0.0;
            for (int i = 0; i < lastFrame.Length; i++)
            {
                mod += lastFrame[i];
            }

            var text = new StringBuilder(300);
            text.Append("A frame took ").Append(Milliseconds(frame));
            if (player != null)
            {
                Vector3 at = player.position;
                text.Append(" at (").Append(at.x.ToString("0", CultureInfo.InvariantCulture)).Append(", ").Append(at.y.ToString("0", CultureInfo.InvariantCulture))
                    .Append(", ").Append(at.z.ToString("0", CultureInfo.InvariantCulture)).Append(')');
            }

            text.Append(speed > 0.5f ? ", driving at " + speed.ToString("0", CultureInfo.InvariantCulture) + " m/s" : string.Empty)
                .Append(": the mod's part ").Append(Milliseconds((float)mod));
            bool first = true;
            for (int i = 0; i < lastFrame.Length; i++)
            {
                if (lastFrame[i] >= 0.002)
                {
                    text.Append(first ? " (" : ", ").Append(Names[i]).Append(' ').Append(Milliseconds((float)lastFrame[i]));
                    first = false;
                }
            }

            text.Append(first ? string.Empty : ")").Append(", the rest the game's")
                .Append(collectionsInLastFrame > 0 ? "; the garbage collector ran in it" : string.Empty)
                .Append(". ").Append(slow).Append(" slow frame(s) so far");
            text.Append(unsaid > 0 ? ", " + unsaid + " of them not written." : ".");
            unsaid = 0;
            log.Info(LogCategory.Vehicle, text.ToString());
        }

        // Once a minute: how much the mod asked of the memory, part by part, and how often the collector ran.
        private void SayMemory(double now)
        {
            if (double.IsNaN(nextMemory))
            {
                nextMemory = now + MemoryEvery;
                Array.Clear(asked, 0, asked.Length);
                collectionsAtMemory = Collections();
                return;
            }

            if (now < nextMemory)
            {
                return;
            }

            double seconds = now - nextMemory + MemoryEvery;
            nextMemory = now + MemoryEvery;
            long total = 0;
            for (int i = 0; i < asked.Length; i++)
            {
                total += asked[i];
            }

            int collections = Collections();
            var text = new StringBuilder(300);
            text.Append("Memory: in the last ").Append(seconds.ToString("0", CultureInfo.InvariantCulture)).Append(" s the mod asked for ").Append(Kilobytes(total));
            bool first = true;
            for (int i = 0; i < asked.Length; i++)
            {
                if (asked[i] >= 1024)
                {
                    text.Append(first ? " (" : ", ").Append(Names[i]).Append(' ').Append(Kilobytes(asked[i]));
                    first = false;
                }
            }

            text.Append(first ? string.Empty : ")").Append("; the garbage collector ran ").Append(collections - collectionsAtMemory)
                .Append(" time(s); the heap has ").Append((GC.GetTotalMemory(false) / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)).Append(" MB in use.");
            var most = new List<KeyValuePair<string, long>>(detail);
            most.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < most.Count && i < DetailShown && most[i].Value >= 1024; i++)
            {
                text.Append(i == 0 ? " Most of it: " : ", ").Append(most[i].Key).Append(' ').Append(Kilobytes(most[i].Value));
            }

            text.Append(most.Count > 0 && most[0].Value >= 1024 ? "." : string.Empty);
            log.Info(LogCategory.Vehicle, text.ToString());
            Array.Clear(asked, 0, asked.Length);
            detail.Clear();
            collectionsAtMemory = collections;
        }

        private static int Collections()
        {
            return GC.CollectionCount(0);
        }

        private static string Milliseconds(float seconds)
        {
            return (seconds * 1000f).ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        private static string Kilobytes(long bytes)
        {
            return (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " kB";
        }
    }
}
