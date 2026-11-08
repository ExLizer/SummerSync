using System;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>
    /// The time of the game world, as the game keeps it (docs/GAME_NOTES.md): a day of the week from 1 to 7, a
    /// block of two hours named after the hour it starts at, counted the game's way (2, 4 ... 22, and 24 for the
    /// one that starts at midnight), and how many seconds of the block have gone by. A block lasts
    /// <see cref="BlockSeconds"/> of real time, so a day lasts two hours.
    ///
    /// With it travel the things the game shows the time with and that follow from nothing else: the angle of
    /// the sun, the two hands of the clock all other clocks copy, and that clock's count of minutes.
    /// </summary>
    public struct WorldClock
    {
        public const int Days = 7;
        public const int BlocksPerDay = 12;
        public const float BlockSeconds = 600f;

        /// <summary>Seconds in a week of the game: the clock comes round after this long.</summary>
        public const double WeekSeconds = Days * BlocksPerDay * (double)BlockSeconds;

        /// <summary>Bytes of an encoded clock.</summary>
        public const int Size = 1 + 1 + 2 + 2 + 2 + 2 + 2;

        public byte Day;
        public byte Hour;
        public float Elapsed;

        /// <summary>Degrees, 0 to 360: the angle the game turns the sun to.</summary>
        public float SunAngle;

        /// <summary>Degrees, 0 to 360: where the hour hand of the game's clock points.</summary>
        public float HourHand;

        /// <summary>Degrees, 0 to 360: where its minute hand points.</summary>
        public float MinuteHand;

        /// <summary>The game's count of minutes within the hour, 0 to 60.</summary>
        public float Minutes;

        public WorldClock(int day, int hour, float elapsed)
        {
            Day = (byte)day;
            Hour = (byte)hour;
            Elapsed = elapsed;
            SunAngle = 0f;
            HourHand = 0f;
            MinuteHand = 0f;
            Minutes = 0f;
        }

        /// <summary>Whether day, block and seconds are ones the game can be at.</summary>
        public bool IsValid
        {
            get
            {
                return Day >= 1 && Day <= Days && Hour >= 2 && Hour <= 24 && Hour % 2 == 0
                    && Elapsed >= 0f && Elapsed <= BlockSeconds + 1f;
            }
        }

        /// <summary>Which block of the day this is, from 0 for the one that starts at midnight to 11.</summary>
        public int Block
        {
            get { return (Hour % 24) / 2; }
        }

        /// <summary>Seconds since the start of the week of the game.</summary>
        public double SecondsIntoWeek
        {
            get { return ((((Day - 1) * BlocksPerDay) + Block) * (double)BlockSeconds) + Math.Min(Elapsed, BlockSeconds); }
        }

        /// <summary>Whether both are in the same block of the same day.</summary>
        public bool SameBlock(WorldClock other)
        {
            return Day == other.Day && Hour == other.Hour;
        }

        /// <summary>
        /// How many seconds this clock is ahead of <paramref name="other"/>, the short way round the week:
        /// negative if it is behind.
        /// </summary>
        public double Ahead(WorldClock other)
        {
            double difference = SecondsIntoWeek - other.SecondsIntoWeek;
            if (difference > WeekSeconds / 2.0)
            {
                difference -= WeekSeconds;
            }
            else if (difference < -WeekSeconds / 2.0)
            {
                difference += WeekSeconds;
            }

            return difference;
        }

        /// <summary>
        /// The clock <paramref name="seconds"/> later, into the next block and the next day if it comes to
        /// that. The sun and the hands move on at the speeds the game moves them at.
        /// </summary>
        public WorldClock Later(double seconds)
        {
            if (seconds <= 0.0)
            {
                return this;
            }

            WorldClock later = this;
            double total = SecondsIntoWeek + seconds;
            total -= Math.Floor(total / WeekSeconds) * WeekSeconds;
            int block = (int)Math.Floor(total / BlockSeconds);
            later.Day = (byte)((block / BlocksPerDay) + 1);
            int ofDay = block % BlocksPerDay;
            later.Hour = (byte)(ofDay == 0 ? 24 : ofDay * 2);
            later.Elapsed = (float)(total - (block * (double)BlockSeconds));

            // The sun goes round once a day; the hands, once an hour and once every twelve, backwards as the
            // game turns them; the minutes, sixty to the hour of 300 seconds.
            later.SunAngle = Turn(SunAngle + (seconds * 360.0 / (BlocksPerDay * BlockSeconds)));
            later.MinuteHand = Turn(MinuteHand - (seconds * 360.0 / (BlockSeconds / 2.0)));
            later.HourHand = Turn(HourHand - (seconds * 30.0 / (BlockSeconds / 2.0)));
            double minutes = Minutes + (seconds * 60.0 / (BlockSeconds / 2.0));
            later.Minutes = (float)(minutes - (Math.Floor(minutes / 60.0) * 60.0));
            return later;
        }

        public void Write(PacketWriter writer)
        {
            writer.WriteByte(Day);
            writer.WriteByte(Hour);
            writer.WriteUInt16((ushort)Math.Round(Math.Max(0f, Math.Min(Elapsed, BlockSeconds)) * 100f));
            writer.WriteUInt16(PackAngle(SunAngle));
            writer.WriteUInt16(PackAngle(HourHand));
            writer.WriteUInt16(PackAngle(MinuteHand));
            writer.WriteUInt16((ushort)Math.Round(Math.Max(0f, Math.Min(Minutes, 60f)) * 1000f));
        }

        /// <exception cref="PacketFormatException">The clock is cut short or shows a time the game cannot be at.</exception>
        public static WorldClock Read(PacketReader reader)
        {
            WorldClock clock;
            clock.Day = reader.ReadByte();
            clock.Hour = reader.ReadByte();
            clock.Elapsed = reader.ReadUInt16() / 100f;
            clock.SunAngle = reader.ReadUInt16() * (360f / 65536f);
            clock.HourHand = reader.ReadUInt16() * (360f / 65536f);
            clock.MinuteHand = reader.ReadUInt16() * (360f / 65536f);
            clock.Minutes = reader.ReadUInt16() / 1000f;
            if (!clock.IsValid)
            {
                throw new PacketFormatException("A clock at day " + clock.Day + ", hour " + clock.Hour + ", which the game cannot be at.");
            }

            return clock;
        }

        /// <summary>Degrees from <paramref name="from"/> to <paramref name="to"/>, the short way round: -180 to 180.</summary>
        public static float AngleBetween(float from, float to)
        {
            double difference = (to - from) % 360.0;
            if (difference > 180.0)
            {
                difference -= 360.0;
            }
            else if (difference < -180.0)
            {
                difference += 360.0;
            }

            return (float)difference;
        }

        private static float Turn(double degrees)
        {
            return (float)(degrees - (Math.Floor(degrees / 360.0) * 360.0));
        }

        private static ushort PackAngle(float degrees)
        {
            double turned = degrees - (Math.Floor(degrees / 360.0) * 360.0);
            return (ushort)((int)Math.Round(turned * (65536.0 / 360.0)) & 0xFFFF);
        }
    }

    /// <summary>
    /// The game's weather: one front of clouds that crosses the map, and what falls from it. Where it rains is
    /// worked out by each machine from where its own player stands under the front.
    /// </summary>
    public struct WeatherState
    {
        /// <summary>Bytes of an encoded weather.</summary>
        public const int Size = 4 + 4 + 2 + 1 + 1 + 4;

        public const byte Dry = 0;
        public const byte Rain = 1;
        public const byte Thunder = 2;

        /// <summary>Where the front is, in world metres. It moves along X.</summary>
        public float X;
        public float Z;

        /// <summary>Degrees, 0 to 360: how the front is turned.</summary>
        public float Yaw;

        /// <summary><see cref="Dry"/>, <see cref="Rain"/> or <see cref="Thunder"/>.</summary>
        public byte Kind;

        /// <summary>Which of the game's clouds is the dark one, 1 to 6, or 0 for none.</summary>
        public byte Cloud;

        public float ThunderForce;

        /// <summary>Whether everything but the place along X is the same: a front does not change those on its way.</summary>
        public bool SameFront(WeatherState other)
        {
            return Kind == other.Kind && Cloud == other.Cloud && Math.Abs(Z - other.Z) < 1f
                && Math.Abs(WorldClock.AngleBetween(Yaw, other.Yaw)) < 0.5f && Math.Abs(ThunderForce - other.ThunderForce) < 0.5f;
        }

        public void Write(PacketWriter writer)
        {
            writer.WriteSingle(X);
            writer.WriteSingle(Z);
            double turned = Yaw - (Math.Floor(Yaw / 360.0) * 360.0);
            writer.WriteUInt16((ushort)((int)Math.Round(turned * (65536.0 / 360.0)) & 0xFFFF));
            writer.WriteByte(Kind);
            writer.WriteByte(Cloud);
            writer.WriteSingle(ThunderForce);
        }

        /// <exception cref="PacketFormatException">The weather is cut short or is none the game has.</exception>
        public static WeatherState Read(PacketReader reader)
        {
            WeatherState weather;
            weather.X = reader.ReadSingle();
            weather.Z = reader.ReadSingle();
            weather.Yaw = reader.ReadUInt16() * (360f / 65536f);
            weather.Kind = reader.ReadByte();
            weather.Cloud = reader.ReadByte();
            weather.ThunderForce = reader.ReadSingle();

            // Written so that NaN fails too.
            if (!(weather.X >= -100000f && weather.X <= 100000f) || !(weather.Z >= -100000f && weather.Z <= 100000f)
                || weather.Kind > Thunder || weather.Cloud > 6 || !(weather.ThunderForce >= 0f && weather.ThunderForce <= 100000f))
            {
                throw new PacketFormatException("A weather the game does not have.");
            }

            return weather;
        }
    }
}
