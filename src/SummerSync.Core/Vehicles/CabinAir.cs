using System;

namespace SummerSync.Core.Vehicles
{
    /// <summary>
    /// How much cigarette smoke there is in a vehicle's cabin, from nothing (0) to so thick that it is hard to see (1).
    /// Every second that someone in it breathes smoke out adds to it; it goes away slowly by itself with the doors shut,
    /// and fast through each door that is open. Each machine works it out from what it already knows (who smokes where,
    /// which doors are open): nothing of it is sent.
    /// </summary>
    public sealed class CabinAir
    {
        /// <summary>
        /// What a second of breathing smoke out adds: a breath is about a second and a half, so a whole cigarette smoked
        /// with the doors shut fills a cabin most of the way.
        /// </summary>
        public const double PerBreathSecond = 0.06;

        /// <summary>Seconds for half the smoke to go with every door shut: through the cracks of an old car.</summary>
        public const double ShutHalfLife = 90.0;

        /// <summary>Seconds for half the smoke to go out of each door that is open.</summary>
        public const double OpenHalfLife = 2.5;

        /// <summary>From 0 to 1.</summary>
        public double Level { get; private set; }

        /// <summary>How much of <see cref="Level"/> went out through the open doors in the last step.</summary>
        public double LeftLastStep { get; private set; }

        /// <summary>
        /// Moves the air on by <paramref name="seconds"/>: <paramref name="breathing"/> people in the cabin were breathing
        /// smoke out the whole time, and <paramref name="openDoors"/> doors were open.
        /// </summary>
        public void Step(double seconds, int breathing, int openDoors)
        {
            if (seconds <= 0 || double.IsNaN(seconds))
            {
                LeftLastStep = 0;
                return;
            }

            double shut = Math.Log(2.0) / ShutHalfLife;
            double open = Math.Max(0, openDoors) * Math.Log(2.0) / OpenHalfLife;
            double gone = Level * (1.0 - Math.Exp(-(shut + open) * seconds));
            LeftLastStep = gone * open / (shut + open);
            Level = Math.Max(0.0, Math.Min(1.0, Level - gone + Math.Max(0, breathing) * PerBreathSecond * seconds));
        }

        /// <summary>Clean air again (the world is gone, or the session).</summary>
        public void Clear()
        {
            Level = 0;
            LeftLastStep = 0;
        }
    }
}
