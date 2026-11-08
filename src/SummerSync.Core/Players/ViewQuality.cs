using System;
using System.Globalization;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.Players
{
    /// <summary>
    /// How one player's movement is coming through on someone's screen, in numbers small enough to send.
    ///
    /// Every client reports these to the host now and then. The log of another player's machine is hard to get
    /// hold of and has arrived empty before; with the reports, the host's own log says how the session looked
    /// from each seat.
    /// </summary>
    public struct ViewQuality
    {
        /// <summary>Bytes of an encoded value.</summary>
        public const int Size = 22;

        public ushort ShownSeconds;
        public ushort DelayMs;

        /// <summary>Thousandths of the time shown that passed without a state ahead of the playback.</summary>
        public ushort StarvedPermille;
        public ushort Corrections;
        public ushort LargestCorrectionCm;
        public ushort Snaps;
        public ushort Lost;
        public ushort SenderPauses;
        public ushort LongestSenderPauseCs;
        public ushort LocalStalls;
        public ushort LongestLocalStallCs;

        public static ViewQuality Of(PoseBuffer buffer)
        {
            double starved = buffer.PlayedSeconds > 0 ? 1000.0 * buffer.StarvedSeconds / buffer.PlayedSeconds : 0.0;
            return new ViewQuality
            {
                ShownSeconds = Clamp(buffer.PlayedSeconds),
                DelayMs = Clamp(buffer.Delay * 1000.0),
                StarvedPermille = Clamp(starved),
                Corrections = Clamp(buffer.Corrections),
                LargestCorrectionCm = Clamp(buffer.LargestCorrection * 100.0),
                Snaps = Clamp(buffer.Snaps),
                Lost = Clamp(buffer.Lost),
                SenderPauses = Clamp(buffer.SenderPauses),
                LongestSenderPauseCs = Clamp(buffer.LongestSenderPause * 100.0),
                LocalStalls = Clamp(buffer.LocalStalls),
                LongestLocalStallCs = Clamp(buffer.LongestLocalStall * 100.0),
            };
        }

        public static ViewQuality Read(PacketReader reader)
        {
            return new ViewQuality
            {
                ShownSeconds = reader.ReadUInt16(),
                DelayMs = reader.ReadUInt16(),
                StarvedPermille = reader.ReadUInt16(),
                Corrections = reader.ReadUInt16(),
                LargestCorrectionCm = reader.ReadUInt16(),
                Snaps = reader.ReadUInt16(),
                Lost = reader.ReadUInt16(),
                SenderPauses = reader.ReadUInt16(),
                LongestSenderPauseCs = reader.ReadUInt16(),
                LocalStalls = reader.ReadUInt16(),
                LongestLocalStallCs = reader.ReadUInt16(),
            };
        }

        public void Write(PacketWriter writer)
        {
            writer.WriteUInt16(ShownSeconds);
            writer.WriteUInt16(DelayMs);
            writer.WriteUInt16(StarvedPermille);
            writer.WriteUInt16(Corrections);
            writer.WriteUInt16(LargestCorrectionCm);
            writer.WriteUInt16(Snaps);
            writer.WriteUInt16(Lost);
            writer.WriteUInt16(SenderPauses);
            writer.WriteUInt16(LongestSenderPauseCs);
            writer.WriteUInt16(LocalStalls);
            writer.WriteUInt16(LongestLocalStallCs);
        }

        /// <summary>"delay 108 ms, no fresh data 0.4 %, 2 jump(s) smoothed (largest 0.06 m), 0 teleport(s)".</summary>
        public string DescribeSmoothness()
        {
            return "delay " + DelayMs + " ms"
                + ", no fresh data " + (StarvedPermille / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + " %"
                + ", " + Corrections + " jump(s) smoothed (largest " + (LargestCorrectionCm / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " m)"
                + ", " + Snaps + " teleport(s)";
        }

        /// <summary>What went missing on the way and whose game stood still.</summary>
        public string DescribeDelivery()
        {
            return Lost + " state(s) lost, their game paused " + SenderPauses + " time(s) (longest "
                + (LongestSenderPauseCs / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " s), this game paused " + LocalStalls
                + " time(s) (longest " + (LongestLocalStallCs / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " s)";
        }

        private static ushort Clamp(double value)
        {
            if (!(value > 0.0))
            {
                return 0;
            }

            return value >= ushort.MaxValue ? ushort.MaxValue : (ushort)Math.Round(value);
        }
    }
}
