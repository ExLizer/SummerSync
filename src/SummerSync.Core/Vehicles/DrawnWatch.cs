using System;
using SummerSync.Core.Objects;

namespace SummerSync.Core.Vehicles
{
    /// <summary>What a window of following a vehicle came to, for the log.</summary>
    public enum DrawnVerdict
    {
        /// <summary>Nothing to say: the window is not over, or it was like the ones before it.</summary>
        None,

        /// <summary>The second window in a row in which the vehicle was not drawn where its owner moves it.</summary>
        Started,

        /// <summary>Still so, a while after it was last said.</summary>
        Lasting,

        /// <summary>Drawn where it belongs again, after it was said that it was not.</summary>
        Ended,
    }

    /// <summary>
    /// Whether a vehicle that this machine follows is drawn where its owner moves it: one window of a second at a time, how
    /// far the drawn vehicle (its transform), its physics body and the place its owner's states give went, and how far apart
    /// the drawn vehicle and its body stood beyond what a step of interpolation explains.
    ///
    /// Why (docs/GAME_NOTES.md, "El Satsuma que se queda mientras sus puertas se van", 2026-11-17): a guest saw the Satsuma
    /// the host drove stay where it was parked, with the host at its wheel, while its doors, boot lid and bonnet went on, and
    /// all of it snap into place when the guest came near. Two copies of the game on one PC did not show it. The parts with
    /// a body of their own hang by joints from the vehicle's physics body, and the driver is drawn on its transform: the
    /// window tells which of the two stayed behind, so that the log of the machine where it happens says it.
    /// </summary>
    public sealed class DrawnWatch
    {
        /// <summary>The length of one window, in seconds.</summary>
        public const double WindowSeconds = 1.0;

        /// <summary>Metres the owner's states move in a window for the vehicle to count as on its way.</summary>
        public const double MovingAtLeast = 1.0;

        /// <summary>The share of that way the drawn vehicle has to go for it to count as following.</summary>
        public const double FollowsAtLeast = 0.5;

        /// <summary>Metres the drawn vehicle may stand from its body beyond two steps of its way (interpolation).</summary>
        public const double GapSlack = 0.5;

        /// <summary>
        /// Metres the drawn vehicle may stand from its owner's place beyond two steps of its way: farther, the mod puts it
        /// there in its next step, so standing there frame after frame is something else putting it elsewhere.
        /// </summary>
        public const double FarFromOwner = 4.0;

        /// <summary>The share of a window's frames drawn that far from the owner's place for the window to be wrong.</summary>
        public const double FarShare = 0.25;

        /// <summary>Windows in a row that are wrong before it is said.</summary>
        public const int WrongWindowsToSay = 2;

        /// <summary>Windows between two lines while it lasts.</summary>
        public const int WindowsBetweenLines = 5;

        private bool open;
        private double openedAt;
        private BodyPose drawnFrom;
        private BodyPose bodyFrom;
        private BodyPose targetFrom;
        private double worstGap;
        private int frames;
        private int farFrames;
        private int wrongInARow;
        private int windowsSinceLine;
        private double wrongSince;

        /// <summary>Whether it has been said that the vehicle is not drawn where it belongs, and not yet that it is again.</summary>
        public bool Said { get; private set; }

        /// <summary>Of the last window that ended: how far the drawn vehicle went, in metres.</summary>
        public double DrawnMoved { get; private set; }

        /// <summary>Of the last window that ended: how far its physics body went.</summary>
        public double BodyMoved { get; private set; }

        /// <summary>Of the last window that ended: how far the place its owner's states give went.</summary>
        public double TargetMoved { get; private set; }

        /// <summary>Of the last window that ended: the most the drawn vehicle stood from its body beyond interpolation.</summary>
        public double WorstGap { get; private set; }

        /// <summary>Of the last window that ended: the share of its frames in which what was drawn stood far from the owner's place.</summary>
        public double FarFrames { get; private set; }

        /// <summary>Of the last window that ended: how far what was drawn stood from the owner's place at its end.</summary>
        public double OwnerGap { get; private set; }

        /// <summary>Since when (the clock given to <see cref="Note"/>) the windows have been wrong, while they are.</summary>
        public double WrongSince
        {
            get { return wrongSince; }
        }

        /// <summary>
        /// Once a frame while the vehicle is followed as a kinematic copy: where it is drawn, where its physics body is,
        /// where its owner's states put it, and the length of its last step of following (metres). Says something only when
        /// a window ends.
        /// </summary>
        public DrawnVerdict Note(double now, BodyPose drawn, BodyPose body, BodyPose target, double stepLength)
        {
            double slack = 2.0 * Math.Max(0.0, stepLength);
            double gap = drawn.DistanceTo(body) - slack;
            double fromOwner = drawn.DistanceTo(target);
            if (!open)
            {
                Open(now, drawn, body, target);
            }

            if (gap > worstGap)
            {
                worstGap = gap;
            }

            frames++;
            if (fromOwner > FarFromOwner + slack)
            {
                farFrames++;
            }

            if (now - openedAt < WindowSeconds)
            {
                return DrawnVerdict.None;
            }

            DrawnMoved = drawn.DistanceTo(drawnFrom);
            BodyMoved = body.DistanceTo(bodyFrom);
            TargetMoved = target.DistanceTo(targetFrom);
            WorstGap = Math.Max(0.0, worstGap);
            FarFrames = frames > 0 ? (double)farFrames / frames : 0.0;
            OwnerGap = fromOwner;
            bool wrong = worstGap > GapSlack || (TargetMoved >= MovingAtLeast && DrawnMoved < FollowsAtLeast * TargetMoved) || FarFrames > FarShare;
            double started = openedAt;
            Open(now, drawn, body, target);
            if (!wrong)
            {
                wrongInARow = 0;
                if (Said)
                {
                    Said = false;
                    return DrawnVerdict.Ended;
                }

                return DrawnVerdict.None;
            }

            if (wrongInARow == 0)
            {
                wrongSince = started;
            }

            wrongInARow++;
            if (!Said)
            {
                if (wrongInARow < WrongWindowsToSay)
                {
                    return DrawnVerdict.None;
                }

                Said = true;
                windowsSinceLine = 0;
                return DrawnVerdict.Started;
            }

            windowsSinceLine++;
            if (windowsSinceLine >= WindowsBetweenLines)
            {
                windowsSinceLine = 0;
                return DrawnVerdict.Lasting;
            }

            return DrawnVerdict.None;
        }

        /// <summary>
        /// The vehicle is not followed as a kinematic copy now (simulated here, a copy with its own physics, at rest, or
        /// gone): the window starts again. Returns <see cref="DrawnVerdict.Ended"/> if it had been said that it was wrong.
        /// </summary>
        public DrawnVerdict Stop()
        {
            open = false;
            wrongInARow = 0;
            if (Said)
            {
                Said = false;
                return DrawnVerdict.Ended;
            }

            return DrawnVerdict.None;
        }

        private void Open(double now, BodyPose drawn, BodyPose body, BodyPose target)
        {
            open = true;
            openedAt = now;
            drawnFrom = drawn;
            bodyFrom = body;
            targetFrom = target;
            worstGap = double.NegativeInfinity;
            frames = 0;
            farFrames = 0;
        }
    }

    /// <summary>
    /// Whether the states of a vehicle that another player drives keep arriving: a vehicle in someone's hands is told in every
    /// send, moving or not, so a stream that has said nothing new for <see cref="SilentSeconds"/> while its driver is seen at
    /// the wheel is one this machine no longer hears (or throws away), and its copy stands wherever the last state left it.
    /// </summary>
    public sealed class SilentStream
    {
        /// <summary>Seconds without a new state, with a driver at the wheel, before it is said.</summary>
        public const double SilentSeconds = 3.0;

        private bool watching;
        private long lastReceived;
        private double heardAt;

        /// <summary>Whether it has been said that nothing arrives, and not yet that it does again.</summary>
        public bool Said { get; private set; }

        /// <summary>Since when (the clock given to <see cref="Note"/>) nothing new has arrived.</summary>
        public double SilentSince
        {
            get { return heardAt; }
        }

        /// <summary>
        /// Once a frame: how many states of the vehicle this machine has taken so far, and whether another player is seen
        /// driving it. <see cref="DrawnVerdict.Started"/> once the stream has been silent long enough with a driver,
        /// <see cref="DrawnVerdict.Ended"/> when a state arrives again or nobody drives it any more.
        /// </summary>
        public DrawnVerdict Note(double now, long received, bool driven)
        {
            if (!driven)
            {
                watching = false;
                return End();
            }

            if (!watching || received != lastReceived)
            {
                watching = true;
                lastReceived = received;
                heardAt = now;
                return End();
            }

            if (!Said && now - heardAt >= SilentSeconds)
            {
                Said = true;
                return DrawnVerdict.Started;
            }

            return DrawnVerdict.None;
        }

        private DrawnVerdict End()
        {
            if (!Said)
            {
                return DrawnVerdict.None;
            }

            Said = false;
            return DrawnVerdict.Ended;
        }
    }
}
