using System;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Replication;

namespace SummerSync.Core.Objects
{
    /// <summary>
    /// Where a loose object is and how it is turned: a position in world coordinates and metres, and a rotation
    /// as a unit quaternion with Unity's component order.
    /// </summary>
    public struct BodyPose : ISnapshot<BodyPose>
    {
        /// <summary>Bytes of an encoded pose: three floats, four 16-bit quaternion components and the vehicle.</summary>
        public const int Size = 12 + 8 + 2;

        /// <summary>Positions further than this from the origin are refused: the map is a few kilometres wide.</summary>
        public const float MaxCoordinate = 100000f;

        public float X;
        public float Y;
        public float Z;
        public float QX;
        public float QY;
        public float QZ;
        public float QW;

        /// <summary>
        /// The vehicle that carries the object, as the vehicles' replication numbers it, or 0 for none. Carried,
        /// position and rotation are relative to the vehicle: whoever shows the object puts it on their own copy
        /// of the vehicle, and it cannot sink through a floor that arrives a moment earlier or later.
        /// </summary>
        public ushort Parent;

        public BodyPose(float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            Parent = 0;
            X = x;
            Y = y;
            Z = z;
            QX = qx;
            QY = qy;
            QZ = qz;
            QW = qw;
        }

        /// <summary>At that position, not turned at all.</summary>
        public static BodyPose At(float x, float y, float z)
        {
            return new BodyPose(x, y, z, 0f, 0f, 0f, 1f);
        }

        public static BodyPose Read(PacketReader reader)
        {
            BodyPose pose;
            pose.X = ReadCoordinate(reader);
            pose.Y = ReadCoordinate(reader);
            pose.Z = ReadCoordinate(reader);
            double qx = (short)reader.ReadUInt16();
            double qy = (short)reader.ReadUInt16();
            double qz = (short)reader.ReadUInt16();
            double qw = (short)reader.ReadUInt16();
            double length = Math.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
            if (length < 1.0)
            {
                // All zeros is not a rotation. Anything else is brought back to unit length.
                qx = qy = qz = 0.0;
                qw = 1.0;
                length = 1.0;
            }

            pose.QX = (float)(qx / length);
            pose.QY = (float)(qy / length);
            pose.QZ = (float)(qz / length);
            pose.QW = (float)(qw / length);
            pose.Parent = reader.ReadUInt16();
            return pose;
        }

        public void Write(PacketWriter writer)
        {
            writer.WriteSingle(X);
            writer.WriteSingle(Y);
            writer.WriteSingle(Z);
            writer.WriteUInt16(Pack(QX));
            writer.WriteUInt16(Pack(QY));
            writer.WriteUInt16(Pack(QZ));
            writer.WriteUInt16(Pack(QW));
            writer.WriteUInt16(Parent);
        }

        public double DistanceTo(BodyPose other)
        {
            // Being loaded on a vehicle or taken off it changes what the numbers are measured from. It is not a jump.
            if (Parent != other.Parent)
            {
                return 0.0;
            }

            double x = X - other.X;
            double y = Y - other.Y;
            double z = Z - other.Z;
            return Math.Sqrt((x * x) + (y * y) + (z * z));
        }

        /// <summary>Degrees between the two rotations, the short way round.</summary>
        public double AngleTo(BodyPose other)
        {
            double dot = Math.Abs((QX * other.QX) + (QY * other.QY) + (QZ * other.QZ) + (QW * other.QW));
            return 2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI;
        }

        public BodyPose LerpTo(BodyPose other, double t)
        {
            if (Parent != other.Parent)
            {
                // Nothing lies between a place in the world and a place on a vehicle.
                return t < 0.5 ? this : other;
            }

            // The same rotation has two quaternions; going towards the nearer one is the short way round.
            double sign = (QX * other.QX) + (QY * other.QY) + (QZ * other.QZ) + (QW * other.QW) < 0.0 ? -1.0 : 1.0;
            double qx = QX + (((other.QX * sign) - QX) * t);
            double qy = QY + (((other.QY * sign) - QY) * t);
            double qz = QZ + (((other.QZ * sign) - QZ) * t);
            double qw = QW + (((other.QW * sign) - QW) * t);
            double length = Math.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
            if (length < 1e-9)
            {
                qx = QX;
                qy = QY;
                qz = QZ;
                qw = QW;
                length = 1.0;
            }

            var pose = new BodyPose(
                (float)(X + ((other.X - X) * t)),
                (float)(Y + ((other.Y - Y) * t)),
                (float)(Z + ((other.Z - Z) * t)),
                (float)(qx / length),
                (float)(qy / length),
                (float)(qz / length),
                (float)(qw / length));
            pose.Parent = Parent;
            return pose;
        }

        public void OffsetFrom(BodyPose other, out double x, out double y, out double z)
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

        public BodyPose Moved(double x, double y, double z)
        {
            BodyPose moved = this;
            moved.X = (float)(X + x);
            moved.Y = (float)(Y + y);
            moved.Z = (float)(Z + z);
            return moved;
        }

        private static float ReadCoordinate(PacketReader reader)
        {
            float value = reader.ReadSingle();

            // Written so that NaN fails it too: a NaN position poisons every transform it is assigned to.
            if (!(value >= -MaxCoordinate && value <= MaxCoordinate))
            {
                throw new PacketFormatException("Object position out of range.");
            }

            return value;
        }

        private static ushort Pack(float component)
        {
            if (float.IsNaN(component))
            {
                component = 0f;
            }

            double clamped = Math.Max(-1.0, Math.Min(1.0, component));
            return unchecked((ushort)(short)Math.Round(clamped * 32767.0));
        }
    }

    /// <summary>
    /// How a loose object is moving: linear velocity in metres per second and angular velocity in radians per
    /// second. Sent only when the simulation of an object passes from one machine to another, so that it goes
    /// on from where it was instead of starting from rest.
    /// </summary>
    public struct BodyMotion
    {
        public const int Size = 24;

        /// <summary>No object in the game moves faster than this on purpose; anything above is refused.</summary>
        public const float MaxSpeed = 1000f;

        public float VX;
        public float VY;
        public float VZ;
        public float AX;
        public float AY;
        public float AZ;

        public static BodyMotion Read(PacketReader reader)
        {
            BodyMotion motion;
            motion.VX = ReadSpeed(reader);
            motion.VY = ReadSpeed(reader);
            motion.VZ = ReadSpeed(reader);
            motion.AX = ReadSpeed(reader);
            motion.AY = ReadSpeed(reader);
            motion.AZ = ReadSpeed(reader);
            return motion;
        }

        public void Write(PacketWriter writer)
        {
            writer.WriteSingle(VX);
            writer.WriteSingle(VY);
            writer.WriteSingle(VZ);
            writer.WriteSingle(AX);
            writer.WriteSingle(AY);
            writer.WriteSingle(AZ);
        }

        private static float ReadSpeed(PacketReader reader)
        {
            float value = reader.ReadSingle();
            if (!(value >= -MaxSpeed && value <= MaxSpeed))
            {
                throw new PacketFormatException("Object velocity out of range.");
            }

            return value;
        }
    }
}
