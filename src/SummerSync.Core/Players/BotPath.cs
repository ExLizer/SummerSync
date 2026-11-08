using System;

namespace SummerSync.Core.Players
{
    /// <summary>
    /// The path of a test player that walks by itself, as a function of time. It strings together the
    /// movements that are hardest to show smoothly on a bad link: a steady walk on a curve, standing still
    /// while looking around, a sprint with instant about-turns, crouching, and a weave of changing speed.
    ///
    /// Being a pure function, whoever watches the bot can compute where it really was at any moment and
    /// measure how far off the picture is.
    /// </summary>
    public static class BotPath
    {
        public const double WalkSpeed = 2.0;
        public const double SprintSpeed = 3.5;

        public const float StandingEyes = 1.4f;
        public const float CrouchedEyes = 0.85f;
        public const float LowEyes = 0.3f;

        private const double Radius = 3.0;
        private const double CircleSeconds = 2.0 * Math.PI * Radius / WalkSpeed;
        private const double StandSeconds = 2.0;
        private const double LegSeconds = 2.0 * Radius / SprintSpeed;
        private const int Legs = 4;
        private const double SprintSeconds = Legs * LegSeconds;
        private const double CrouchMove = 0.3;
        private const double CrouchHold = 1.5;
        private const double CrouchSeconds = (3.0 * CrouchMove) + (2.0 * CrouchHold);
        private const double WeaveSeconds = 8.0;

        /// <summary>Seconds after which the path repeats.</summary>
        public const double CycleSeconds = CircleSeconds + StandSeconds + SprintSeconds + CrouchSeconds + WeaveSeconds;

        /// <summary>
        /// The bot's pose <paramref name="time"/> seconds into its path. The path is laid out around
        /// <paramref name="centre"/>, on the horizontal plane at its height, within about 3.5 metres of it.
        /// </summary>
        public static PlayerPose Evaluate(double time, PlayerPose centre)
        {
            double t = time % CycleSeconds;
            if (t < 0)
            {
                t += CycleSeconds;
            }

            // Offsets from the centre: x to the east, z to the north. Every stage starts and ends at (Radius, 0).
            double x = Radius;
            double z = 0.0;
            double yaw = 0.0;
            double pitch = 0.0;
            double eyes = StandingEyes;

            if (t < CircleSeconds)
            {
                double angle = t * WalkSpeed / Radius;
                x = Radius * Math.Cos(angle);
                z = Radius * Math.Sin(angle);
                yaw = Heading(-Math.Sin(angle), Math.Cos(angle));
            }
            else if ((t -= CircleSeconds) < StandSeconds)
            {
                // Standing still, looking left and right and up and down.
                yaw = 90.0 * Math.Sin(t * Math.PI);
                pitch = 30.0 * Math.Sin(t * 2.0 * Math.PI);
            }
            else if ((t -= StandSeconds) < SprintSeconds)
            {
                int leg = Math.Min(Legs - 1, (int)(t / LegSeconds));
                double along = (t - (leg * LegSeconds)) * SprintSpeed;
                bool outward = leg % 2 == 0;
                x = outward ? Radius - along : -Radius + along;
                yaw = outward ? 270.0 : 90.0;
            }
            else if ((t -= SprintSeconds) < CrouchSeconds)
            {
                eyes = CrouchEyes(t);
                yaw = 270.0;
            }
            else
            {
                t -= CrouchSeconds;
                double u = t / WeaveSeconds;
                x = Radius - (1.5 * (1.0 - Math.Cos(2.0 * Math.PI * u)));
                z = Math.Sin(4.0 * Math.PI * u);
                yaw = Heading(-1.5 * Math.Sin(2.0 * Math.PI * u), 2.0 * Math.Cos(4.0 * Math.PI * u));
                pitch = -10.0;
            }

            return new PlayerPose(
                (float)(centre.X + x),
                centre.Y,
                (float)(centre.Z + z),
                (float)((yaw % 360.0 + 360.0) % 360.0),
                (float)pitch,
                (float)eyes);
        }

        /// <summary>A point <paramref name="metres"/> ahead of where <paramref name="viewer"/> looks, at their feet's height.</summary>
        public static PlayerPose CentreInFrontOf(PlayerPose viewer, double metres)
        {
            double yaw = viewer.Yaw * Math.PI / 180.0;
            return new PlayerPose(
                (float)(viewer.X + (Math.Sin(yaw) * metres)),
                viewer.Y,
                (float)(viewer.Z + (Math.Cos(yaw) * metres)),
                0f,
                0f,
                StandingEyes);
        }

        // Down to a crouch, hold, down to the floor, hold, back up.
        private static double CrouchEyes(double t)
        {
            if (t < CrouchMove)
            {
                return Ease(StandingEyes, CrouchedEyes, t / CrouchMove);
            }

            if ((t -= CrouchMove) < CrouchHold)
            {
                return CrouchedEyes;
            }

            if ((t -= CrouchHold) < CrouchMove)
            {
                return Ease(CrouchedEyes, LowEyes, t / CrouchMove);
            }

            if ((t -= CrouchMove) < CrouchHold)
            {
                return LowEyes;
            }

            return Ease(LowEyes, StandingEyes, Math.Min(1.0, (t - CrouchHold) / CrouchMove));
        }

        private static double Ease(double from, double to, double u)
        {
            double smooth = u * u * (3.0 - (2.0 * u));
            return from + ((to - from) * smooth);
        }

        // Unity yaw of a horizontal direction: 0 along +z, 90 along +x.
        private static double Heading(double dx, double dz)
        {
            return Math.Atan2(dx, dz) * 180.0 / Math.PI;
        }
    }
}
