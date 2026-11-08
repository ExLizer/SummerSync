using System;

namespace SummerSync.Core.Objects
{
    /// <summary>
    /// How long a thing that another machine simulates has been shown standing still here, worked out from the
    /// poses it is shown at, frame after frame.
    ///
    /// A client takes over the things near it, so that they give way when it walks or drives into them. Taking
    /// one over while it is on its way somewhere is a different matter: it would start again from where it is
    /// shown here, which is a moment behind where its owner has it, and standing still. Seen between two PCs on
    /// 2026-10-13: a thing thrown at the other player stopped in the air two metres from them and dropped, and
    /// one put down beside them fell twice. So a thing is only asked for once it has settled.
    ///
    /// Still means still in the coordinates it is shown in: something lying in a moving vehicle is still.
    /// </summary>
    public sealed class Stillness
    {
        /// <summary>Metres per second. Shown slower than this, a thing counts as not moving.</summary>
        public const double Speed = 0.5;

        /// <summary>How long it has to have been shown that slow before it counts as settled.</summary>
        public const double Seconds = 0.5;

        // A frame longer than this is a hitch of the game, not time in which the thing could have moved.
        private const double LongestFrame = 0.25;

        private bool known;
        private float x;
        private float y;
        private float z;
        private ushort parent;
        private double since;
        private double last;

        /// <summary>
        /// Once per frame while the thing is shown: where, at local time <paramref name="now"/>, and whether
        /// its owner has it in hand. In a hand it is never still, however little the hand moves.
        /// </summary>
        public void Note(BodyPose shown, bool held, double now)
        {
            bool still = false;
            if (known && !held && parent == shown.Parent)
            {
                double reach = Speed * Math.Max(0.0, Math.Min(now - last, LongestFrame));
                double dx = shown.X - x;
                double dy = shown.Y - y;
                double dz = shown.Z - z;
                still = (dx * dx) + (dy * dy) + (dz * dz) <= reach * reach;
            }

            if (!still)
            {
                since = now;
            }

            x = shown.X;
            y = shown.Y;
            z = shown.Z;
            parent = shown.Parent;
            last = now;
            known = true;
        }

        /// <summary>The thing is no longer being shown from someone else's reports. The count starts again.</summary>
        public void Forget()
        {
            known = false;
        }

        /// <summary>Whether it has been shown still for long enough to be taken over without anyone noticing.</summary>
        public bool Settled(double now)
        {
            return known && now - since >= Seconds;
        }
    }
}
