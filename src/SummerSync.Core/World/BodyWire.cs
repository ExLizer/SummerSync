using System;

namespace SummerSync.Core.World
{
    /// <summary>Where one bone of a body is, in the world: a place in metres and a turn.</summary>
    public struct BonePose
    {
        public float X;
        public float Y;
        public float Z;
        public float QX;
        public float QY;
        public float QZ;
        public float QW;

        public BonePose(float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            X = x;
            Y = y;
            Z = z;
            QX = qx;
            QY = qy;
            QZ = qz;
            QW = qw;
        }
    }

    /// <summary>
    /// A body as it travels, in named tallies (<see cref="TallyReplication"/>):
    ///
    /// - "body/&lt;name&gt;" [player + 1, generation]: whose game simulates it, said by whoever takes it. A word counts only
    ///   if its generation is above the last that counted (<see cref="BodyClaim"/>): every game hears the words in the order
    ///   the host passes them on, so every game ends up with the same owner.
    /// - "body/&lt;name&gt;/&lt;bone&gt;" [generation, x, y, z in millimetres, two numbers of the turn]: where that game has a
    ///   bone. One of another generation is from an owner that has since lost it, and is not shown.
    ///
    /// The host keeps the last of each, which is what a player who joins later is told.
    /// </summary>
    public static class BodyWire
    {
        public const string Prefix = "body/";

        /// <summary>How many numbers a bone's tally has.</summary>
        public const int BoneValues = 6;

        /// <summary>How many numbers an owner's tally has.</summary>
        public const int OwnerValues = 2;

        /// <summary>Most bones a body has (the game's rag dolls have eleven).</summary>
        public const int MaxBones = 32;

        private const float Quarter = 32767f;

        // A place further than this many millimetres from the map's centre is not a place.
        private const int Far = 100000000;

        public static string OwnerKey(string body)
        {
            return Prefix + body;
        }

        public static string BoneKey(string body, int bone)
        {
            return Prefix + body + "/" + bone.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Which body and which of its bones a tally is about; <paramref name="bone"/> is -1 for its owner. False if the name
        /// is not a body's.
        /// </summary>
        public static bool TryParseKey(string key, out string body, out int bone)
        {
            body = null;
            bone = -1;
            if (key == null || !key.StartsWith(Prefix, StringComparison.Ordinal) || key.Length == Prefix.Length)
            {
                return false;
            }

            string rest = key.Substring(Prefix.Length);
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                body = rest;
                return true;
            }

            if (slash == 0 || slash == rest.Length - 1)
            {
                return false;
            }

            int number = 0;
            for (int i = slash + 1; i < rest.Length; i++)
            {
                char c = rest[i];
                if (c < '0' || c > '9' || number > MaxBones)
                {
                    return false;
                }

                number = (number * 10) + (c - '0');
            }

            if (number >= MaxBones)
            {
                return false;
            }

            body = rest.Substring(0, slash);
            bone = number;
            return true;
        }

        public static int[] PackOwner(byte owner, int generation)
        {
            return new[] { owner + 1, generation };
        }

        /// <summary>False if the numbers are not an owner's (the wrong count, nobody, no generation).</summary>
        public static bool TryUnpackOwner(int[] values, out byte owner, out int generation)
        {
            owner = 0;
            generation = 0;
            if (values == null || values.Length != OwnerValues || values[0] < 1 || values[0] > 256 || values[1] < 1)
            {
                return false;
            }

            owner = (byte)(values[0] - 1);
            generation = values[1];
            return true;
        }

        public static int[] PackBone(int generation, BonePose pose)
        {
            float qx = pose.QX;
            float qy = pose.QY;
            float qz = pose.QZ;
            float qw = pose.QW;
            double length = Math.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
            if (length < 1e-6)
            {
                qx = 0f;
                qy = 0f;
                qz = 0f;
                qw = 1f;
                length = 1.0;
            }

            // A turn and its opposite are the same turn: the one with its last part not below zero travels.
            double sign = qw < 0f ? -1.0 : 1.0;
            return new[]
            {
                generation,
                Millimetres(pose.X),
                Millimetres(pose.Y),
                Millimetres(pose.Z),
                Pair(sign * qx / length, sign * qy / length),
                Pair(sign * qz / length, sign * qw / length),
            };
        }

        /// <summary>False if the numbers are not a bone's (the wrong count, a place off the map).</summary>
        public static bool TryUnpackBone(int[] values, out int generation, out BonePose pose)
        {
            generation = 0;
            pose = default(BonePose);
            if (values == null || values.Length != BoneValues || values[0] < 1)
            {
                return false;
            }

            if (Math.Abs(values[1]) > Far || Math.Abs(values[2]) > Far || Math.Abs(values[3]) > Far)
            {
                return false;
            }

            generation = values[0];
            double qx = First(values[4]);
            double qy = Second(values[4]);
            double qz = First(values[5]);
            double qw = Second(values[5]);
            double length = Math.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
            if (length < 0.5)
            {
                return false;
            }

            pose = new BonePose(
                values[1] / 1000f,
                values[2] / 1000f,
                values[3] / 1000f,
                (float)(qx / length),
                (float)(qy / length),
                (float)(qz / length),
                (float)(qw / length));
            return true;
        }

        /// <summary>Whether a bone has gone further than so many metres, or turned more than so many degrees, from where it was.</summary>
        public static bool Moved(BonePose from, BonePose to, float metres, float degrees)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float dz = to.Z - from.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) > metres * metres)
            {
                return true;
            }

            return Degrees(from, to) > degrees;
        }

        /// <summary>The angle between two turns, in degrees.</summary>
        public static float Degrees(BonePose a, BonePose b)
        {
            double dot = Math.Abs((a.QX * b.QX) + (a.QY * b.QY) + (a.QZ * b.QZ) + (a.QW * b.QW));
            if (dot >= 1.0)
            {
                return 0f;
            }

            return (float)(2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }

        private static int Millimetres(float metres)
        {
            return (int)Math.Round(metres * 1000.0);
        }

        private static int Pair(double low, double high)
        {
            int a = Part(low);
            int b = Part(high);
            return unchecked((a & 0xFFFF) | (b << 16));
        }

        private static int Part(double value)
        {
            double clamped = value < -1.0 ? -1.0 : value > 1.0 ? 1.0 : value;
            return (int)Math.Round(clamped * Quarter);
        }

        private static double First(int packed)
        {
            return (short)(packed & 0xFFFF) / (double)Quarter;
        }

        private static double Second(int packed)
        {
            return (short)((packed >> 16) & 0xFFFF) / (double)Quarter;
        }
    }

    /// <summary>
    /// Whose game simulates a body, as every game hears it (<see cref="BodyWire"/>): the words in the order the host passes
    /// them on, each counting only with a generation above the last that counted. Two players who take the body at once
    /// both say the next generation; the first the host passed on keeps it, in every game.
    /// </summary>
    public sealed class BodyClaim
    {
        /// <summary>Whose game simulates the body; meaningless while <see cref="Known"/> is false.</summary>
        public byte Owner { get; private set; }

        /// <summary>How many times it has been taken; 0 while nobody has.</summary>
        public int Generation { get; private set; }

        /// <summary>Whether anybody has taken it in this session.</summary>
        public bool Known
        {
            get { return Generation > 0; }
        }

        /// <summary>The generation a game that takes the body now says.</summary>
        public int Next
        {
            get { return Generation + 1; }
        }

        /// <summary>A word about the owner, heard in the order the host passed it on. True if it counted.</summary>
        public bool Hear(byte owner, int generation)
        {
            if (generation <= Generation)
            {
                return false;
            }

            Owner = owner;
            Generation = generation;
            return true;
        }

        /// <summary>Whether a bone's place is from the owner that has the body now.</summary>
        public bool IsCurrent(int generation)
        {
            return Known && generation == Generation;
        }

        /// <summary>The session is over: nobody has it.</summary>
        public void Forget()
        {
            Owner = 0;
            Generation = 0;
        }
    }
}
