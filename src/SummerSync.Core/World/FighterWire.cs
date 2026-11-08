using System;
using System.Collections.Generic;
using System.Text;

namespace SummerSync.Core.World
{
    /// <summary>
    /// Where the game that runs a fighter has him, as the numbers of one tally (<see cref="TallyReplication"/>,
    /// "fighter/&lt;name&gt;/pose"): who runs him, his place and heading, the clip of each of his animations, whether he is
    /// calm and out, and which of his shown and loose things are on or off.
    /// </summary>
    public struct FighterPose
    {
        public const int Count = 8;

        /// <summary>The player whose game runs him.</summary>
        public byte Runner;

        public float X;
        public float Y;
        public float Z;

        /// <summary>Degrees about the vertical.</summary>
        public float Yaw;

        /// <summary>For each animation of his (<see cref="FighterDefinition.Animations"/>), its clip by place in its sorted list, plus one; 0: none.</summary>
        public byte Clip0;
        public byte Clip1;
        public byte Clip2;
        public byte Clip3;

        /// <summary>Not fighting (<see cref="FighterDefinition.Calm"/>): another game can take him over.</summary>
        public bool Calm;

        /// <summary>Out (switched on) in the game that runs him.</summary>
        public bool Out;

        /// <summary>A bit for each of <see cref="FighterDefinition.Shown"/> (on) and then of <see cref="FighterDefinition.Loose"/> (come off).</summary>
        public byte Things;

        /// <summary>Counts up with each pose told.</summary>
        public int Serial;

        public byte Clip(int animation)
        {
            switch (animation)
            {
                case 0: return Clip0;
                case 1: return Clip1;
                case 2: return Clip2;
                case 3: return Clip3;
                default: return 0;
            }
        }

        public void SetClip(int animation, byte clip)
        {
            switch (animation)
            {
                case 0: Clip0 = clip; break;
                case 1: Clip1 = clip; break;
                case 2: Clip2 = clip; break;
                case 3: Clip3 = clip; break;
            }
        }

        public int[] Pack()
        {
            return new[]
            {
                Runner + 1,
                Millimetres(X),
                Millimetres(Y),
                Millimetres(Z),
                (int)Math.Round(Repeat(Yaw) * 100.0),
                unchecked(Clip0 | (Clip1 << 8) | (Clip2 << 16) | (Clip3 << 24)),
                (Calm ? 1 : 0) | (Out ? 2 : 0) | (Things << 8),
                Serial,
            };
        }

        /// <summary>False if the numbers are not a pose (the wrong count, nobody running him, a place off the map).</summary>
        public static bool TryUnpack(int[] values, out FighterPose pose)
        {
            pose = default(FighterPose);
            if (values == null || values.Length != Count || values[0] < 1 || values[0] > 255)
            {
                return false;
            }

            const int Far = 100000000;
            if (Math.Abs(values[1]) > Far || Math.Abs(values[2]) > Far || Math.Abs(values[3]) > Far)
            {
                return false;
            }

            pose.Runner = (byte)(values[0] - 1);
            pose.X = values[1] / 1000f;
            pose.Y = values[2] / 1000f;
            pose.Z = values[3] / 1000f;
            pose.Yaw = (float)Repeat(values[4] / 100.0);
            pose.Clip0 = (byte)(values[5] & 0xFF);
            pose.Clip1 = (byte)((values[5] >> 8) & 0xFF);
            pose.Clip2 = (byte)((values[5] >> 16) & 0xFF);
            pose.Clip3 = (byte)((values[5] >> 24) & 0xFF);
            pose.Calm = (values[6] & 1) != 0;
            pose.Out = (values[6] & 2) != 0;
            pose.Things = (byte)((values[6] >> 8) & 0xFF);
            pose.Serial = values[7];
            return true;
        }

        private static int Millimetres(float metres)
        {
            return (int)Math.Round(metres * 1000.0);
        }

        private static double Repeat(double degrees)
        {
            double d = degrees % 360.0;
            return d < 0.0 ? d + 360.0 : d;
        }
    }

    /// <summary>A player who could run a fighter, and how far from him that player is.</summary>
    public struct FighterCandidate
    {
        public FighterCandidate(byte player, float distance)
        {
            Player = player;
            Distance = distance;
        }

        public byte Player;
        public float Distance;
    }

    /// <summary>Whose game runs a fighter: the nearest player's, kept while he fights.</summary>
    public static class FighterOwnership
    {
        /// <summary>How much nearer another player has to be, in metres, to take a calm fighter over.</summary>
        public const float Margin = 2f;

        /// <summary>
        /// The player whose game runs him now, or -1 for nobody. <paramref name="runner"/> is who runs him (-1: nobody),
        /// <paramref name="calm"/> whether he is calm there, <paramref name="candidates"/> the players in the world. One
        /// who is not among them (gone, or out of the world) hands him to the nearest at once; otherwise he changes hands
        /// only calm, to a player nearer by more than <see cref="Margin"/>.
        /// </summary>
        public static int Choose(int runner, bool calm, IList<FighterCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return -1;
            }

            int nearest = 0;
            int current = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Distance < candidates[nearest].Distance)
                {
                    nearest = i;
                }

                if (candidates[i].Player == runner)
                {
                    current = i;
                }
            }

            if (current < 0)
            {
                return candidates[nearest].Player;
            }

            if (!calm || nearest == current)
            {
                return runner;
            }

            return candidates[nearest].Distance + Margin < candidates[current].Distance ? candidates[nearest].Player : runner;
        }
    }

    /// <summary>A short text (a sound's name) in a few of a tally's numbers: four characters of ASCII in each.</summary>
    public static class TallyText
    {
        /// <summary>False if the text does not fit in <paramref name="count"/> numbers, or is not plain ASCII.</summary>
        public static bool TryPack(string text, int[] into, int start, int count)
        {
            if (text == null || text.Length > count * 4)
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < 32 || text[i] > 126)
                {
                    return false;
                }
            }

            for (int n = 0; n < count; n++)
            {
                int packed = 0;
                for (int b = 0; b < 4; b++)
                {
                    int at = n * 4 + b;
                    int c = at < text.Length ? text[at] : 0;
                    packed |= c << (b * 8);
                }

                into[start + n] = packed;
            }

            return true;
        }

        public static string Unpack(int[] from, int start, int count)
        {
            var text = new StringBuilder();
            for (int n = 0; n < count && start + n < from.Length; n++)
            {
                for (int b = 0; b < 4; b++)
                {
                    int c = (from[start + n] >> (b * 8)) & 0xFF;
                    if (c == 0)
                    {
                        return text.ToString();
                    }

                    text.Append(c >= 32 && c <= 126 ? (char)c : '?');
                }
            }

            return text.ToString();
        }
    }
}
