using System.Globalization;

namespace SummerSync.Core.Net
{
    /// <summary>
    /// Watches the sends towards one peer in windows of <see cref="WindowSeconds"/>: how many of them could not fit everything
    /// unreliable that was queued (a send carries <see cref="ConnectionConfig.MaxPacketsPerFlush"/> packets at most, reliable
    /// messages first), so that the poses and vehicle states left over went out a frame or more late.
    ///
    /// Why (docs/GAME_NOTES.md, "El rubber banding cerca de los edificios", 2026-11-17): a vehicle that another machine runs
    /// jumps when its states come late, and the log could not tell a crowded link from a hitch of a game.
    /// </summary>
    public sealed class LinkBacklog
    {
        /// <summary>Seconds of sends that make a window.</summary>
        public const double WindowSeconds = 10.0;

        private bool open;
        private double startedAt;
        private int sends;
        private int leaving;
        private int mostLeftOver;
        private int mostReliable;

        /// <summary>
        /// After each send. True when a window ends in which at least one send left unreliable messages waiting, with what
        /// happened in it.
        /// </summary>
        public bool Note(double now, int unreliableLeftOver, int reliableQueue, out string said)
        {
            said = null;
            if (!open)
            {
                Begin(now);
            }

            sends++;
            if (unreliableLeftOver > 0)
            {
                leaving++;
            }

            if (unreliableLeftOver > mostLeftOver)
            {
                mostLeftOver = unreliableLeftOver;
            }

            if (reliableQueue > mostReliable)
            {
                mostReliable = reliableQueue;
            }

            double seconds = now - startedAt;
            if (seconds < WindowSeconds)
            {
                return false;
            }

            bool crowded = leaving > 0;
            if (crowded)
            {
                said = "in the last " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + leaving + " of " + sends
                    + " send(s) left unreliable messages (poses, vehicle states) for a later one (the most waiting: " + mostLeftOver
                    + "); the reliable queue reached " + mostReliable + ".";
            }

            Begin(now);
            return crowded;
        }

        private void Begin(double now)
        {
            open = true;
            startedAt = now;
            sends = 0;
            leaving = 0;
            mostLeftOver = 0;
            mostReliable = 0;
        }
    }
}
