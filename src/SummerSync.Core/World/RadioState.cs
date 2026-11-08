using System;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>
    /// What the game's radio station has on the air: which recording, and how far into it. The station plays
    /// whether or not anyone has a radio on, so this is a fact about the world like the time of day.
    /// </summary>
    public struct RadioState
    {
        /// <summary>Upper bound for the encoded name of a recording.</summary>
        public const int MaxClipBytes = 96;

        /// <summary>The name of the recording, as the game calls it. Null or empty while the station is silent.</summary>
        public string Clip;

        /// <summary>Seconds into the recording.</summary>
        public float Position;

        /// <summary>Whether it is playing. False between a programme and the next song.</summary>
        public bool Playing;

        /// <summary>The same broadcast some seconds later: further into the recording if it plays.</summary>
        public RadioState Later(double seconds)
        {
            RadioState later = this;
            if (Playing)
            {
                later.Position = (float)(Position + seconds);
            }

            return later;
        }

        /// <summary>Whether both are the same recording, playing or not alike. Where in it they are does not count.</summary>
        public bool SameRecording(RadioState other)
        {
            return Playing == other.Playing && string.Equals(Clip ?? string.Empty, other.Clip ?? string.Empty, StringComparison.Ordinal);
        }

        public void Write(PacketWriter writer)
        {
            writer.WriteByte(Playing ? (byte)1 : (byte)0);
            writer.WriteSingle(Position);
            writer.WriteString(Net.Protocol.LimitText(Clip ?? string.Empty, MaxClipBytes));
        }

        /// <exception cref="PacketFormatException">The bytes are not a radio state.</exception>
        public static RadioState Read(PacketReader reader)
        {
            var state = default(RadioState);
            byte playing = reader.ReadByte();
            if (playing > 1)
            {
                throw new PacketFormatException("A radio that is neither playing nor silent: " + playing + ".");
            }

            state.Playing = playing == 1;
            state.Position = reader.ReadSingle();
            state.Clip = reader.ReadString(MaxClipBytes);
            if (float.IsNaN(state.Position) || float.IsInfinity(state.Position) || state.Position < 0f)
            {
                throw new PacketFormatException("A radio at no place in its recording.");
            }

            return state;
        }
    }
}
