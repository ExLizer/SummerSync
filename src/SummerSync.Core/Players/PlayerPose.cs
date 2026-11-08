using System;
using SummerSync.Core.Replication;
using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Players
{
    /// <summary>What a player is doing with their hands that the others see on them.</summary>
    public enum PlayerActivity : byte
    {
        None = 0,

        /// <summary>Drinking; the detail is what from (the game side numbers the drinks from 1, 0 unknown).</summary>
        Drinking = 1,

        /// <summary>Smoking; the detail is the moment of it (<see cref="PlayerPose.SmokeHolding"/> and the others).</summary>
        Smoking = 2,

        Peeing = 3,
    }

    /// <summary>
    /// Where a player stands and where they look. The position is the point under their feet, in world
    /// coordinates and metres. Angles are in degrees with Unity's conventions: yaw 0 looks along +Z and grows
    /// towards +X; pitch is positive looking up.
    /// </summary>
    public struct PlayerPose : ISnapshot<PlayerPose>
    {
        public float X;
        public float Y;
        public float Z;
        public float Yaw;
        public float Pitch;

        /// <summary>Height of the eyes above the feet. It is what shows a remote player crouching or sitting.</summary>
        public float EyeHeight;

        /// <summary>
        /// The vehicle the player rides, as the vehicles' replication numbers it, or 0 on foot. Riding, the
        /// position and the yaw are relative to the vehicle: whoever shows the player puts them where their own
        /// copy of the vehicle is, and the two cannot drift apart.
        /// </summary>
        public ushort Parent;

        /// <summary>
        /// A <see cref="Parent"/> from here up is not a vehicle but one of the NPCs that move by themselves (the bus),
        /// by its place in their list: <see cref="NpcParentBase"/> + place.
        /// </summary>
        public const ushort NpcParentBase = 0xFF00;

        /// <summary>Whether the player is at the wheel of <see cref="Parent"/>. Then <see cref="Controls"/> counts.</summary>
        public bool Driving;

        public VehicleControls Controls;

        /// <summary>
        /// Whether the player has their hands out to push something heavy: a vehicle, most of the time. Whoever
        /// shows the player puts hands there that push, so that the push arrives on the machine that runs the
        /// physics of what is being pushed.
        /// </summary>
        public bool Pushing;

        /// <summary>What the player is doing with their hands, and a detail of it (0 to 15): drinking what, at which moment
        /// of a cigarette.</summary>
        public PlayerActivity Activity;

        public byte ActivityDetail;

        /// <summary>
        /// For a player riding <see cref="Parent"/> without driving it: the passenger seat they sit in, from 1 (the
        /// vehicle file's "seats", in order), or 0 standing. Up to <see cref="MaxSeat"/>.
        /// </summary>
        public byte Seat;

        public const byte MaxSeat = 15;

        /// <summary>For a seated passenger: their seatbelt is fastened. Their head holds more in a crash.</summary>
        public bool Belted;

        /// <summary>For a passenger, seated or standing: they wear a helmet. Their head holds more in a crash.</summary>
        public bool Helmet;

        /// <summary>The moments of a cigarette, as <see cref="ActivityDetail"/>: held, drawn on, breathed out.</summary>
        public const byte SmokeHolding = 0;

        public const byte SmokeDrawing = 1;

        public const byte SmokeBreathing = 2;

        public PlayerPose(float x, float y, float z, float yaw, float pitch, float eyeHeight)
        {
            X = x;
            Y = y;
            Z = z;
            Yaw = yaw;
            Pitch = pitch;
            EyeHeight = eyeHeight;
            Parent = 0;
            Driving = false;
            Controls = default(VehicleControls);
            Pushing = false;
            Activity = PlayerActivity.None;
            ActivityDetail = 0;
            Seat = 0;
            Belted = false;
            Helmet = false;
        }

        /// <summary>Metres between the two positions.</summary>
        public static double Distance(PlayerPose a, PlayerPose b)
        {
            // Getting on or off a vehicle changes what the numbers are measured from. It is not a jump.
            if (a.Parent != b.Parent)
            {
                return 0.0;
            }

            double x = a.X - b.X;
            double y = a.Y - b.Y;
            double z = a.Z - b.Z;
            return Math.Sqrt((x * x) + (y * y) + (z * z));
        }

        /// <summary>
        /// The pose a fraction <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>.
        /// The yaw turns the short way round.
        /// </summary>
        public static PlayerPose Lerp(PlayerPose a, PlayerPose b, double t)
        {
            if (a.Parent != b.Parent)
            {
                // Nothing lies between a place in the world and a place in a vehicle.
                return t < 0.5 ? a : b;
            }

            var pose = new PlayerPose(
                Mix(a.X, b.X, t),
                Mix(a.Y, b.Y, t),
                Mix(a.Z, b.Z, t),
                LerpAngle(a.Yaw, b.Yaw, t),
                Mix(a.Pitch, b.Pitch, t),
                Mix(a.EyeHeight, b.EyeHeight, t));
            pose.Parent = b.Parent;
            pose.Pushing = t < 0.5 ? a.Pushing : b.Pushing;
            pose.Activity = t < 0.5 ? a.Activity : b.Activity;
            pose.ActivityDetail = t < 0.5 ? a.ActivityDetail : b.ActivityDetail;
            pose.Driving = t < 0.5 ? a.Driving : b.Driving;
            pose.Seat = t < 0.5 ? a.Seat : b.Seat;
            pose.Belted = t < 0.5 ? a.Belted : b.Belted;
            pose.Helmet = t < 0.5 ? a.Helmet : b.Helmet;
            if (a.Driving && b.Driving)
            {
                pose.Controls = VehicleControls.Lerp(a.Controls, b.Controls, t);
            }
            else if (pose.Driving)
            {
                pose.Controls = a.Driving ? a.Controls : b.Controls;
            }

            return pose;
        }

        public double DistanceTo(PlayerPose other)
        {
            return Distance(this, other);
        }

        public PlayerPose LerpTo(PlayerPose other, double t)
        {
            return Lerp(this, other, t);
        }

        public void OffsetFrom(PlayerPose other, out double x, out double y, out double z)
        {
            if (Parent != other.Parent)
            {
                x = 0.0;
                y = 0.0;
                z = 0.0;
                return;
            }

            x = X - other.X;
            y = Y - other.Y;
            z = Z - other.Z;
        }

        public PlayerPose Moved(double x, double y, double z)
        {
            PlayerPose moved = this;
            moved.X = (float)(X + x);
            moved.Y = (float)(Y + y);
            moved.Z = (float)(Z + z);
            return moved;
        }

        /// <summary>Interpolates between two angles in degrees the short way round. The result is in [0, 360).</summary>
        public static float LerpAngle(float a, float b, double t)
        {
            double delta = (b - a) % 360.0;
            if (delta > 180.0)
            {
                delta -= 360.0;
            }
            else if (delta < -180.0)
            {
                delta += 360.0;
            }

            double angle = (a + (delta * t)) % 360.0;
            if (angle < 0.0)
            {
                angle += 360.0;
            }

            // A value just under 360 can round up to it as a float.
            float result = (float)angle;
            return result >= 360f ? 0f : result;
        }

        private static float Mix(float a, float b, double t)
        {
            return (float)(a + ((b - a) * t));
        }
    }
}
