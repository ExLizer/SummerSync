namespace SummerSync.Core.Hashing
{
    /// <summary>
    /// FNV-1a, 64 bit. Fast, tiny and with no dependencies, which matters because the game's runtime lacks
    /// parts of the usual class library. It detects differences; it is not a defence against tampering.
    /// </summary>
    public static class Fnv1a
    {
        public const ulong OffsetBasis = 14695981039346656037UL;

        private const ulong Prime = 1099511628211UL;

        public static ulong Hash64(byte[] data)
        {
            return Hash64(data, 0, data.Length, OffsetBasis);
        }

        /// <summary>Hashes a range, continuing from <paramref name="hash"/> so data can be fed in pieces.</summary>
        public static ulong Hash64(byte[] data, int offset, int count, ulong hash)
        {
            unchecked
            {
                for (int i = offset; i < offset + count; i++)
                {
                    hash = (hash ^ data[i]) * Prime;
                }
            }

            return hash;
        }

        /// <summary>The hash as 16 lower-case hexadecimal digits.</summary>
        public static string ToHex(ulong hash)
        {
            return hash.ToString("x16");
        }
    }
}
