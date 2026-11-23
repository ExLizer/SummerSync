using System;

namespace SummerSync.Core.Vehicles
{
    /// <summary>
    /// The session's clock at the game's steps of physics: when the vehicles this machine runs were where they are (what
    /// their states are stamped with), and the moment to show the ones another machine runs at, step by step.
    ///
    /// Why (docs/GAME_NOTES.md, "El auto que se traba y salta cerca de los edificios", 2026-11-23): the game counts a frame
    /// as 0.05 s at most (its "Maximum Allowed Timestep"), so a frame that takes longer leaves its time behind the session's
    /// clock. The difference was smoothed as if it were the jitter of reading two clocks, and taken at once only past 0.05 s:
    /// in a game below 20 frames a second (beside the house, the store, the repair shop) the vehicles of the others went on at
    /// the game's slower pace and every few frames jumped ahead to where they belonged; after a pause of the garbage collector
    /// (0.15 to 0.22 s, measured in a player's log) they jumped 1.6 to 3.5 m at once.
    ///
    /// How: a frame the game cut short (<see cref="Frame"/> is told so) moves the clock by exactly what it lost; the rest is
    /// smoothed as before, and a jump past <see cref="Jump"/> that no short frame explains is taken at once. The vehicles
    /// another machine runs are shown at a time of their own (<see cref="Follow"/>) that goes at the game's pace and catches
    /// up with the clock a little faster, never by a jump, as long as it is less than <see cref="MostBehind"/> behind.
    /// </summary>
    public sealed class StepClock
    {
        /// <summary>A change of the difference between the two clocks past this, not explained by a short frame, is taken at once.</summary>
        public const double Jump = 0.05;

        /// <summary>The part of what is left of the difference taken each frame: the two clocks read a few milliseconds apart.</summary>
        public const double Blend = 0.02;

        /// <summary>Further than this behind the clock, the time vehicles are shown at is put where it belongs.</summary>
        public const double MostBehind = 0.5;

        /// <summary>How much faster than the game's pace the shown time goes per second it is behind.</summary>
        public const double CatchUp = 5.0;

        /// <summary>The fastest and slowest the shown time goes, as times the game's pace.</summary>
        public const double MostPace = 2.0;

        public const double LeastPace = 0.5;

        /// <summary>How much the pace changes from one step to the next at most.</summary>
        public const double PaceChange = 0.15;

        private bool known;
        private double offset;
        private double lastRaw;
        private bool followKnown;
        private double followTime;
        private double pace = 1.0;

        /// <summary>Frames the game cut short since the clock started.</summary>
        public int ShortFrames { get; private set; }

        /// <summary>Seconds of the session's clock the game's time did not cover, since the clock started.</summary>
        public double Lost { get; private set; }

        /// <summary>The furthest the time vehicles are shown at was behind the clock since the last <see cref="TakeMostBehind"/>.</summary>
        public double MostBehindSeen { get; private set; }

        /// <summary>Times the shown time was put where it belongs instead of catching up, since the clock started.</summary>
        public int Snaps { get; private set; }

        /// <summary>The pace of the last step, as times the game's pace.</summary>
        public double Pace
        {
            get { return pace; }
        }

        /// <summary>
        /// Once a frame: the session's clock now, the game's time of the frame, and whether the game counted the frame as
        /// shorter than it was (it took longer than the game's limit for one frame).
        /// </summary>
        public void Frame(double now, double gameTime, bool cutShort)
        {
            double raw = now - gameTime;
            if (!known)
            {
                known = true;
                offset = raw;
                lastRaw = raw;
                return;
            }

            double change = raw - lastRaw;
            lastRaw = raw;
            if (cutShort && change > 0.0)
            {
                // What the game's time did not cover of this frame is real time all the same: taken as it is.
                offset += change;
                ShortFrames++;
                Lost += change;
            }

            double gap = raw - offset;
            if (Math.Abs(gap) > Jump)
            {
                offset = raw;
            }
            else
            {
                offset += gap * Blend;
            }
        }

        /// <summary>The session's clock at the step of physics that ends at the game's <paramref name="fixedTime"/>.</summary>
        public double At(double fixedTime)
        {
            return offset + fixedTime;
        }

        /// <summary>
        /// Once per step of physics: the time to show what another machine runs at, for the step that ends at the game's
        /// <paramref name="fixedTime"/> and lasts <paramref name="step"/> seconds.
        /// </summary>
        public double Follow(double fixedTime, double step)
        {
            double exact = At(fixedTime);
            if (!followKnown || !(step > 0.0))
            {
                followKnown = true;
                followTime = exact;
                pace = 1.0;
                return followTime;
            }

            // How far behind it would be going on at the game's pace.
            double behind = exact - (followTime + step);
            double wanted = Math.Max(LeastPace, Math.Min(MostPace, 1.0 + (CatchUp * behind)));
            pace += Math.Max(-PaceChange, Math.Min(PaceChange, wanted - pace));
            followTime += step * pace;
            double left = exact - followTime;
            if (Math.Abs(left) > MostBehind)
            {
                followTime = exact;
                pace = 1.0;
                Snaps++;
                left = 0.0;
            }

            MostBehindSeen = Math.Max(MostBehindSeen, left);
            return followTime;
        }

        /// <summary>The furthest the shown time was behind since the last call, and from now on from zero.</summary>
        public double TakeMostBehind()
        {
            double most = MostBehindSeen;
            MostBehindSeen = 0.0;
            return most;
        }

        /// <summary>The shown time starts again at the clock: nothing was shown for a while.</summary>
        public void ResetFollow()
        {
            followKnown = false;
            pace = 1.0;
        }
    }
}
