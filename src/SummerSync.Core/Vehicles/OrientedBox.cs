using System;

namespace SummerSync.Core.Vehicles
{
    /// <summary>
    /// A box turned any way in the world: its centre, its three axes (unit vectors at right angles, in the world's
    /// coordinates) and how far it reaches along each. It is what tells that two vehicles are about to touch: the box of
    /// one vehicle's colliders, grown by what it covers in a moment, against the other's.
    /// </summary>
    public struct OrientedBox
    {
        public float X;
        public float Y;
        public float Z;

        public float RightX;
        public float RightY;
        public float RightZ;

        public float UpX;
        public float UpY;
        public float UpZ;

        public float ForwardX;
        public float ForwardY;
        public float ForwardZ;

        public float HalfRight;
        public float HalfUp;
        public float HalfForward;

        /// <summary>The same box, reaching <paramref name="margin"/> further along every side.</summary>
        public OrientedBox Grown(float margin)
        {
            OrientedBox grown = this;
            grown.HalfRight += margin;
            grown.HalfUp += margin;
            grown.HalfForward += margin;
            return grown;
        }

        /// <summary>
        /// Whether the two boxes overlap. The separating axis test: two boxes are apart exactly when, along one of their
        /// six axes or of the nine crossings of an axis of one with an axis of the other, their shadows do not overlap.
        /// No allocation: it runs every frame for every pair of vehicles.
        /// </summary>
        public static bool Meet(ref OrientedBox a, ref OrientedBox b)
        {
            // A small slack against crossings of almost parallel axes, whose length is next to nothing.
            const float Slack = 1e-5f;

            // How much each axis of b goes along each axis of a: r[i][j] is a's axis i against b's axis j.
            float r00 = Dot(a.RightX, a.RightY, a.RightZ, b.RightX, b.RightY, b.RightZ);
            float r01 = Dot(a.RightX, a.RightY, a.RightZ, b.UpX, b.UpY, b.UpZ);
            float r02 = Dot(a.RightX, a.RightY, a.RightZ, b.ForwardX, b.ForwardY, b.ForwardZ);
            float r10 = Dot(a.UpX, a.UpY, a.UpZ, b.RightX, b.RightY, b.RightZ);
            float r11 = Dot(a.UpX, a.UpY, a.UpZ, b.UpX, b.UpY, b.UpZ);
            float r12 = Dot(a.UpX, a.UpY, a.UpZ, b.ForwardX, b.ForwardY, b.ForwardZ);
            float r20 = Dot(a.ForwardX, a.ForwardY, a.ForwardZ, b.RightX, b.RightY, b.RightZ);
            float r21 = Dot(a.ForwardX, a.ForwardY, a.ForwardZ, b.UpX, b.UpY, b.UpZ);
            float r22 = Dot(a.ForwardX, a.ForwardY, a.ForwardZ, b.ForwardX, b.ForwardY, b.ForwardZ);

            float q00 = Math.Abs(r00) + Slack;
            float q01 = Math.Abs(r01) + Slack;
            float q02 = Math.Abs(r02) + Slack;
            float q10 = Math.Abs(r10) + Slack;
            float q11 = Math.Abs(r11) + Slack;
            float q12 = Math.Abs(r12) + Slack;
            float q20 = Math.Abs(r20) + Slack;
            float q21 = Math.Abs(r21) + Slack;
            float q22 = Math.Abs(r22) + Slack;

            // From a's centre to b's, along a's axes.
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float dz = b.Z - a.Z;
            float t0 = Dot(dx, dy, dz, a.RightX, a.RightY, a.RightZ);
            float t1 = Dot(dx, dy, dz, a.UpX, a.UpY, a.UpZ);
            float t2 = Dot(dx, dy, dz, a.ForwardX, a.ForwardY, a.ForwardZ);

            float a0 = a.HalfRight;
            float a1 = a.HalfUp;
            float a2 = a.HalfForward;
            float b0 = b.HalfRight;
            float b1 = b.HalfUp;
            float b2 = b.HalfForward;

            // a's axes.
            if (Math.Abs(t0) > a0 + (b0 * q00) + (b1 * q01) + (b2 * q02)
                || Math.Abs(t1) > a1 + (b0 * q10) + (b1 * q11) + (b2 * q12)
                || Math.Abs(t2) > a2 + (b0 * q20) + (b1 * q21) + (b2 * q22))
            {
                return false;
            }

            // b's axes.
            if (Math.Abs((t0 * r00) + (t1 * r10) + (t2 * r20)) > (a0 * q00) + (a1 * q10) + (a2 * q20) + b0
                || Math.Abs((t0 * r01) + (t1 * r11) + (t2 * r21)) > (a0 * q01) + (a1 * q11) + (a2 * q21) + b1
                || Math.Abs((t0 * r02) + (t1 * r12) + (t2 * r22)) > (a0 * q02) + (a1 * q12) + (a2 * q22) + b2)
            {
                return false;
            }

            // The crossings: a's axis i with b's axis j.
            if (Math.Abs((t2 * r10) - (t1 * r20)) > (a1 * q20) + (a2 * q10) + (b1 * q02) + (b2 * q01)
                || Math.Abs((t2 * r11) - (t1 * r21)) > (a1 * q21) + (a2 * q11) + (b0 * q02) + (b2 * q00)
                || Math.Abs((t2 * r12) - (t1 * r22)) > (a1 * q22) + (a2 * q12) + (b0 * q01) + (b1 * q00)
                || Math.Abs((t0 * r20) - (t2 * r00)) > (a0 * q20) + (a2 * q00) + (b1 * q12) + (b2 * q11)
                || Math.Abs((t0 * r21) - (t2 * r01)) > (a0 * q21) + (a2 * q01) + (b0 * q12) + (b2 * q10)
                || Math.Abs((t0 * r22) - (t2 * r02)) > (a0 * q22) + (a2 * q02) + (b0 * q11) + (b1 * q10)
                || Math.Abs((t1 * r00) - (t0 * r10)) > (a0 * q10) + (a1 * q00) + (b1 * q22) + (b2 * q21)
                || Math.Abs((t1 * r01) - (t0 * r11)) > (a0 * q11) + (a1 * q01) + (b0 * q22) + (b2 * q20)
                || Math.Abs((t1 * r02) - (t0 * r12)) > (a0 * q12) + (a1 * q02) + (b0 * q21) + (b1 * q20))
            {
                return false;
            }

            return true;
        }

        private static float Dot(float ax, float ay, float az, float bx, float by, float bz)
        {
            return (ax * bx) + (ay * by) + (az * bz);
        }
    }
}
