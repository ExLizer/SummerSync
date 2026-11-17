using System;

namespace SummerSync.Core.Vehicles
{
    /// <summary>
    /// Whether the speed read off two successive places of a vehicle another machine runs is a speed at all. A copy with its
    /// own physics (within reach of a vehicle simulated here) is driven at the speed its owner moves it; a jump of the owner's
    /// states (a hitch of either game, a late state, the vehicle put somewhere by the game) read as a speed drives the copy
    /// into whatever is beside it.
    ///
    /// Why (docs/GAME_NOTES.md, "El Satsuma que vuelve a la casa", 2026-11-17): the Satsuma that the game of the player who
    /// ran it put back in the home garage arrived in the host's game as a copy within reach of the host's tractor, started its
    /// physics at 45.7 m/s (a 0.9 m jump of its states taken as a step of speed) and was thrown into the tractor and the house.
    /// </summary>
    public static class OwnerSteps
    {
        /// <summary>
        /// Metres per second gained in one second that no driving gives (about 25 g): faster than that, and not slowing down,
        /// the places jumped.
        /// </summary>
        public const double MostGain = 250.0;

        /// <summary>
        /// Whether going from the speed <paramref name="before"/> to <paramref name="after"/> (each x, y, z, m/s) in one step
        /// of <paramref name="step"/> seconds is a jump of the places rather than a speed. Slowing down at once is a crash or a
        /// stop, which the copy takes as it comes.
        /// </summary>
        public static bool IsJump(double beforeX, double beforeY, double beforeZ, double afterX, double afterY, double afterZ, double step)
        {
            if (!(step > 0.0))
            {
                return false;
            }

            double dx = afterX - beforeX;
            double dy = afterY - beforeY;
            double dz = afterZ - beforeZ;
            double change = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (!(change > MostGain * step))
            {
                return false;
            }

            double before = Math.Sqrt((beforeX * beforeX) + (beforeY * beforeY) + (beforeZ * beforeZ));
            double after = Math.Sqrt((afterX * afterX) + (afterY * afterY) + (afterZ * afterZ));
            return after > before - 1.0;
        }
    }
}
