namespace SummerSync.Core.Reports
{
    /// <summary>
    /// The CRC-32 that gzip ends with (the polynomial 0xEDB88320, reflected, starting from all ones), and the CRC of two
    /// pieces put together from the CRCs of each and the second one's length: the pieces of a report are compressed one by
    /// one and dropped from memory, and the gzip trailer needs the CRC of all of them in their order.
    /// </summary>
    public static class Crc32
    {
        private const uint Polynomial = 0xEDB88320u;

        private static readonly uint[] Table = MakeTable();

        /// <summary>The CRC of these bytes.</summary>
        public static uint Of(byte[] data, int offset, int count)
        {
            return Update(0u, data, offset, count);
        }

        /// <summary>The CRC of what <paramref name="crc"/> was the CRC of, followed by these bytes.</summary>
        public static uint Update(uint crc, byte[] data, int offset, int count)
        {
            uint c = ~crc;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }

            return ~c;
        }

        /// <summary>
        /// The CRC of A followed by B, from the CRC of A, the CRC of B and B's length: the shift of A's CRC over B's length,
        /// as a power of the matrix of one zero bit, applied by repeated squaring.
        /// </summary>
        public static uint Combine(uint first, uint second, long secondLength)
        {
            if (secondLength <= 0)
            {
                return first;
            }

            var odd = new uint[32];
            var even = new uint[32];

            // The operator for one zero bit, then two, then four.
            odd[0] = Polynomial;
            uint row = 1;
            for (int n = 1; n < 32; n++)
            {
                odd[n] = row;
                row <<= 1;
            }

            Square(even, odd);
            Square(odd, even);

            uint crc = first;
            long length = secondLength;
            do
            {
                // A byte is eight zero bits: the first square here is the operator of one zero byte.
                Square(even, odd);
                if ((length & 1) != 0)
                {
                    crc = Times(even, crc);
                }

                length >>= 1;
                if (length == 0)
                {
                    break;
                }

                Square(odd, even);
                if ((length & 1) != 0)
                {
                    crc = Times(odd, crc);
                }

                length >>= 1;
            }
            while (length != 0);

            return crc ^ second;
        }

        private static uint Times(uint[] matrix, uint vector)
        {
            uint sum = 0;
            int i = 0;
            while (vector != 0)
            {
                if ((vector & 1) != 0)
                {
                    sum ^= matrix[i];
                }

                vector >>= 1;
                i++;
            }

            return sum;
        }

        private static void Square(uint[] square, uint[] matrix)
        {
            for (int n = 0; n < 32; n++)
            {
                square[n] = Times(matrix, matrix[n]);
            }
        }

        private static uint[] MakeTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
