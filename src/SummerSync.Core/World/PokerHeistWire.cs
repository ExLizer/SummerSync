using SummerSync.Core.Net;

namespace SummerSync.Core.World
{
    /// <summary>
    /// The tally that says the video poker was torn out of the pub ("poker/broken"): [1, robber + 1], where the robber is the
    /// player in whose game the rope pulled it out. That game simulates the machine from then on, wherever it takes it; the
    /// others break their copy loose the game's way and show it where that game has it. A tally without the second number
    /// (or with 0 there) says nobody in particular: the host's game takes it.
    /// </summary>
    public static class PokerHeistWire
    {
        public const string BrokenKey = "poker/broken";

        public static int[] PackBroken(byte robber)
        {
            return new[] { 1, robber + 1 };
        }

        /// <summary>False if the tally does not say the machine is torn out; then <paramref name="robber"/> is the host.</summary>
        public static bool TryUnpackBroken(int[] values, out byte robber)
        {
            robber = Protocol.HostPlayerId;
            if (values == null || values.Length < 1 || values[0] == 0)
            {
                return false;
            }

            if (values.Length >= 2 && values[1] >= 1 && values[1] <= 256)
            {
                robber = (byte)(values[1] - 1);
            }

            return true;
        }
    }
}
