using System;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.World;

namespace SummerSync.Core.Vehicles
{
    /// <summary>Message numbers of vehicle replication.</summary>
    public static class VehicleWire
    {
        /// <summary>
        /// First of the nine message types of the vehicles' own <see cref="Objects.ObjectReplication"/>. A vehicle
        /// is shared like a loose object, with the driver's seat in the place of the hand: whoever drives it
        /// simulates it, and nobody can take it from them.
        /// </summary>
        public const byte FirstBodyMessage = SwitchWire.MessageSet + 1;

        /// <summary>Name of that replication in the log.</summary>
        public const string Label = "Vehicles";
    }

    /// <summary>
    /// What a driver is doing with a vehicle, as far as someone looking at it from outside can tell: where the
    /// wheels point, how the engine sounds, whether the brake lights are on, how far each pedal is down. It travels
    /// inside the driver's own state. The vehicle's position does not come from here.
    /// </summary>
    public struct VehicleControls
    {
        /// <summary>Bytes of an encoded set of controls.</summary>
        public const int Size = 8;

        public const byte FlagHandbrake = 1;

        /// <summary>Largest engine speed that fits. No engine in the game gets near it.</summary>
        public const float MaxRpm = 16000f;

        /// <summary>From -1, full left, to 1, full right.</summary>
        public float Steering;

        /// <summary>From 0 to 1.</summary>
        public float Throttle;

        /// <summary>From 0 to 1.</summary>
        public float Brake;

        /// <summary>How far the clutch pedal is down, from 0 (let up) to 1, as the game's own pedal shows it.</summary>
        public float Clutch;

        /// <summary>Revolutions per minute of the engine; 0 with the engine off.</summary>
        public float Rpm;

        /// <summary>The gear as the game numbers it: 0 is reverse, 1 neutral, 2 first.</summary>
        public int Gear;

        public byte Flags;

        public bool Handbrake
        {
            get { return (Flags & FlagHandbrake) != 0; }
        }

        /// <summary>
        /// The controls a fraction <paramref name="t"/> of the way from <paramref name="a"/> to
        /// <paramref name="b"/>. Gear and flags do not blend: they are those of the nearer end.
        /// </summary>
        public static VehicleControls Lerp(VehicleControls a, VehicleControls b, double t)
        {
            VehicleControls nearer = t < 0.5 ? a : b;
            return new VehicleControls
            {
                Steering = Mix(a.Steering, b.Steering, t),
                Throttle = Mix(a.Throttle, b.Throttle, t),
                Brake = Mix(a.Brake, b.Brake, t),
                Clutch = Mix(a.Clutch, b.Clutch, t),
                Rpm = Mix(a.Rpm, b.Rpm, t),
                Gear = nearer.Gear,
                Flags = nearer.Flags,
            };
        }

        public void Write(PacketWriter writer)
        {
            writer.WriteByte((byte)(sbyte)Math.Round(Clamp(Steering, -1f, 1f) * 127.0));
            writer.WriteByte((byte)Math.Round(Clamp(Throttle, 0f, 1f) * 255.0));
            writer.WriteByte((byte)Math.Round(Clamp(Brake, 0f, 1f) * 255.0));
            writer.WriteByte((byte)Math.Round(Clamp(Clutch, 0f, 1f) * 255.0));
            writer.WriteUInt16((ushort)Math.Round(Clamp(Rpm, 0f, MaxRpm) * (65535.0 / MaxRpm)));
            writer.WriteByte((byte)(sbyte)Math.Max(-8, Math.Min(16, Gear)));
            writer.WriteByte(Flags);
        }

        public static VehicleControls Read(PacketReader reader)
        {
            var controls = default(VehicleControls);
            controls.Steering = (sbyte)reader.ReadByte() / 127f;
            controls.Throttle = reader.ReadByte() / 255f;
            controls.Brake = reader.ReadByte() / 255f;
            controls.Clutch = reader.ReadByte() / 255f;
            controls.Rpm = reader.ReadUInt16() * (MaxRpm / 65535f);
            controls.Gear = (sbyte)reader.ReadByte();
            controls.Flags = reader.ReadByte();
            return controls;
        }

        // Written so that NaN, which fails every comparison, comes out as the lower bound.
        private static float Clamp(float value, float min, float max)
        {
            return value >= min ? (value <= max ? value : max) : min;
        }

        private static float Mix(float a, float b, double t)
        {
            return (float)(a + ((b - a) * t));
        }
    }
}
