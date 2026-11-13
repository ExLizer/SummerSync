using System;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Vehicles;

namespace SummerSync.Core.Players
{
    /// <summary>
    /// Wire format of player states. A state is the sender's clock, a running number, a flags byte and, when
    /// the player is in the game world, the pose in 17 bytes: position as three floats, yaw and pitch as 16
    /// bits each, eye height in centimetres. A player riding a vehicle adds the vehicle's number, and one who
    /// drives it adds the <see cref="VehicleControls"/>, and one who sits, is belted or wears a helmet adds a byte that
    /// says so (a driver sits in no passenger seat). One who drinks, smokes or pees adds a byte after the pose: what, and a detail of it.
    /// One on foot who wears a helmet says so in a flag (<see cref="FlagHelmet"/>).
    ///
    /// The number counts the states a player has sent. It is what lets the receiving end tell a state that was
    /// lost on the way from one that was never sent because the sender's game stood still.
    /// </summary>
    public static class PlayerWire
    {
        /// <summary>Client to host: the sender's own state.</summary>
        public const byte MessageState = Protocol.FirstUserMessage;

        /// <summary>Host to client: a count, then a player id and a state for each of the other players.</summary>
        public const byte MessageStates = Protocol.FirstUserMessage + 1;

        /// <summary>
        /// Client to host, reliable, every few seconds: a count, then a player id and a <see cref="ViewQuality"/>
        /// for each player the sender is showing.
        /// </summary>
        public const byte MessageViewReport = Protocol.FirstUserMessage + 2;

        /// <summary>The player has a body in the game world. Clear in the main menu and on loading screens.</summary>
        public const byte FlagInWorld = 1;

        /// <summary>The player rides a vehicle: its number follows the pose, and the pose is relative to it.</summary>
        public const byte FlagRiding = 2;

        /// <summary>The player drives the vehicle they ride: the controls follow its number.</summary>
        public const byte FlagDriving = 4;

        /// <summary>The player has their hands out to push. Nothing follows: it is all in the flag.</summary>
        public const byte FlagPushing = 8;

        /// <summary>The player drinks, smokes or pees: a byte follows the pose, the detail times 16 and the activity.</summary>
        public const byte FlagActivity = 16;

        /// <summary>
        /// The player rides and sits, is belted or wears a helmet: a byte follows the vehicle's number, the passenger seat (0
        /// to 15, always 0 for a driver), and <see cref="RiderBelted"/> and <see cref="RiderHelmet"/>.
        /// </summary>
        public const byte FlagRider = 32;

        /// <summary>
        /// The player wears a helmet and rides nothing (a rider says it in the rider's byte). Nothing follows: the others put
        /// the helmet this player holds on the head of their figure.
        /// </summary>
        public const byte FlagHelmet = 64;

        public const byte RiderBelted = 16;

        public const byte RiderHelmet = 32;

        /// <summary>Largest encoded state: clock, number, flags, pose, activity, vehicle, the rider's byte and controls.</summary>
        public const int MaxStateBytes = 4 + 2 + 1 + 17 + 1 + 2 + 1 + VehicleControls.Size;

        public const float MaxEyeHeight = 2.55f;

        /// <summary>Positions further than this from the origin are refused: the map is a few kilometres wide.</summary>
        public const float MaxCoordinate = 100000f;

        public static void WriteState(PacketWriter writer, uint timeMs, ushort sequence, bool inWorld, PlayerPose pose)
        {
            writer.WriteUInt32(timeMs);
            writer.WriteUInt16(sequence);
            bool riding = inWorld && pose.Parent != 0;
            bool driving = riding && pose.Driving;
            bool pushing = inWorld && pose.Pushing;
            bool acting = inWorld && pose.Activity != PlayerActivity.None && pose.Activity <= PlayerActivity.Peeing;
            byte seat = pose.Seat <= PlayerPose.MaxSeat && !driving ? pose.Seat : (byte)0;
            bool rider = riding && (seat != 0 || pose.Belted || pose.Helmet);
            bool helmet = inWorld && !riding && pose.Helmet;
            writer.WriteByte((byte)((inWorld ? FlagInWorld : 0) | (riding ? FlagRiding : 0) | (driving ? FlagDriving : 0) | (pushing ? FlagPushing : 0)
                | (acting ? FlagActivity : 0) | (rider ? FlagRider : 0) | (helmet ? FlagHelmet : 0)));
            if (!inWorld)
            {
                return;
            }

            writer.WriteSingle(pose.X);
            writer.WriteSingle(pose.Y);
            writer.WriteSingle(pose.Z);
            writer.WriteUInt16(EncodeYaw(pose.Yaw));
            writer.WriteUInt16(EncodePitch(pose.Pitch));
            writer.WriteByte(EncodeEyeHeight(pose.EyeHeight));
            if (acting)
            {
                writer.WriteByte((byte)(((pose.ActivityDetail & 0x0F) << 4) | (byte)pose.Activity));
            }

            if (riding)
            {
                writer.WriteUInt16(pose.Parent);
            }

            if (rider)
            {
                writer.WriteByte((byte)(seat | (pose.Belted ? RiderBelted : 0) | (pose.Helmet ? RiderHelmet : 0)));
            }

            if (driving)
            {
                pose.Controls.Write(writer);
            }
        }

        /// <exception cref="PacketFormatException">The state is cut short or holds a position no player can be at.</exception>
        public static void ReadState(PacketReader reader, out uint timeMs, out ushort sequence, out bool inWorld, out PlayerPose pose)
        {
            timeMs = reader.ReadUInt32();
            sequence = reader.ReadUInt16();
            byte flags = reader.ReadByte();
            inWorld = (flags & FlagInWorld) != 0;
            pose = default(PlayerPose);
            if (!inWorld)
            {
                return;
            }

            pose.X = ReadCoordinate(reader);
            pose.Y = ReadCoordinate(reader);
            pose.Z = ReadCoordinate(reader);
            pose.Yaw = reader.ReadUInt16() * (360f / 65536f);
            pose.Pitch = (reader.ReadUInt16() * (180f / 65535f)) - 90f;
            pose.EyeHeight = reader.ReadByte() / 100f;
            pose.Pushing = (flags & FlagPushing) != 0;
            pose.Helmet = (flags & FlagHelmet) != 0;
            if ((flags & FlagActivity) != 0)
            {
                byte activity = reader.ReadByte();
                pose.Activity = (PlayerActivity)(activity & 0x0F);
                pose.ActivityDetail = (byte)(activity >> 4);
                if (pose.Activity == PlayerActivity.None || pose.Activity > PlayerActivity.Peeing)
                {
                    throw new PacketFormatException("A player does something unknown with their hands: " + (activity & 0x0F) + ".");
                }
            }

            if ((flags & FlagRiding) != 0)
            {
                pose.Parent = reader.ReadUInt16();
                if (pose.Parent == 0)
                {
                    throw new PacketFormatException("A player rides vehicle 0, which is none.");
                }
            }

            if ((flags & FlagHelmet) != 0 && pose.Parent != 0)
            {
                throw new PacketFormatException("A rider says their helmet in the flag of those on foot.");
            }

            if ((flags & FlagRider) != 0)
            {
                if (pose.Parent == 0)
                {
                    throw new PacketFormatException("A player says how they ride without riding anything.");
                }

                byte rider = reader.ReadByte();
                if ((rider & ~(PlayerPose.MaxSeat | RiderBelted | RiderHelmet)) != 0 || ((flags & FlagDriving) != 0 && (rider & PlayerPose.MaxSeat) != 0))
                {
                    throw new PacketFormatException("A rider's byte holds something unknown, or a passenger seat for a driver: " + rider + ".");
                }

                pose.Seat = (byte)(rider & PlayerPose.MaxSeat);
                pose.Belted = (rider & RiderBelted) != 0;
                pose.Helmet = (rider & RiderHelmet) != 0;
            }

            if ((flags & FlagDriving) != 0)
            {
                if (pose.Parent == 0)
                {
                    throw new PacketFormatException("A player drives without riding anything.");
                }

                pose.Driving = true;
                pose.Controls = VehicleControls.Read(reader);
            }
        }

        private static float ReadCoordinate(PacketReader reader)
        {
            float value = reader.ReadSingle();

            // The comparison is written so that NaN fails it too. A NaN position would poison every transform
            // it is assigned to.
            if (!(value >= -MaxCoordinate && value <= MaxCoordinate))
            {
                throw new PacketFormatException("Player position out of range.");
            }

            return value;
        }

        private static ushort EncodeYaw(float yaw)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw))
            {
                return 0;
            }

            double turns = (yaw / 360.0) % 1.0;
            if (turns < 0.0)
            {
                turns += 1.0;
            }

            return (ushort)((int)Math.Round(turns * 65536.0) & 0xFFFF);
        }

        private static ushort EncodePitch(float pitch)
        {
            if (float.IsNaN(pitch))
            {
                pitch = 0f;
            }

            double clamped = Math.Max(-90.0, Math.Min(90.0, pitch));
            return (ushort)Math.Round((clamped + 90.0) * (65535.0 / 180.0));
        }

        private static byte EncodeEyeHeight(float eyeHeight)
        {
            if (float.IsNaN(eyeHeight))
            {
                eyeHeight = 0f;
            }

            double clamped = Math.Max(0.0, Math.Min(MaxEyeHeight, eyeHeight));
            return (byte)Math.Round(clamped * 100.0);
        }
    }
}
