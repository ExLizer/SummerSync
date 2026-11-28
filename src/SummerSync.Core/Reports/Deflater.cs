using System;
using System.IO;

namespace SummerSync.Core.Reports
{
    /// <summary>
    /// A small DEFLATE encoder (RFC 1951): repeats found by hash chains in a 32 KiB window, written with the fixed Huffman
    /// codes. It exists because the game's Mono has no working compression of its own: its <c>GZipStream</c> calls into
    /// <c>MonoPosixHelper</c>, which the game does not ship (docs/GAME_NOTES.md). Measured on a player's six logs (3 MB):
    /// 16 % of their size, where zlib's fastest level makes 20 % and its default 13 %.
    ///
    /// Each call compresses one piece on its own (nothing refers back into an earlier piece) and ends byte-aligned with an
    /// empty stored block, so pieces can be compressed one by one, some of them dropped, and the rest put one after the
    /// other into a single stream that ends with <see cref="FinalBlock"/>.
    /// </summary>
    public sealed class Deflater
    {
        /// <summary>The last block of a stream: a fixed-code block, marked final, with nothing in it.</summary>
        public static readonly byte[] FinalBlock = { 0x03, 0x00 };

        private const int WindowSize = 32768;
        private const int WindowMask = WindowSize - 1;
        private const int HashBits = 15;
        private const int HashSize = 1 << HashBits;
        private const int MinMatch = 3;
        private const int MaxMatch = 258;
        private const int EndOfBlock = 256;

        private static readonly int[] LengthBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258,
        };

        private static readonly int[] LengthExtra =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
        };

        private static readonly int[] DistanceBase =
        {
            1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
            8193, 12289, 16385, 24577,
        };

        private static readonly int[] DistanceExtra =
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
        };

        // The fixed codes, already reversed for a stream written from the lowest bit up.
        private static readonly int[] LiteralCode = new int[288];
        private static readonly int[] LiteralBits = new int[288];
        private static readonly int[] DistanceCode = new int[30];

        // Which length code a match length takes, from 3 to 258.
        private static readonly byte[] LengthSymbol = new byte[MaxMatch + 1];

        private readonly int[] head = new int[HashSize];
        private readonly int[] previous = new int[WindowSize];
        private readonly int chainLimit;

        static Deflater()
        {
            for (int symbol = 0; symbol < 288; symbol++)
            {
                int code;
                int bits;
                if (symbol < 144)
                {
                    code = 0x30 + symbol;
                    bits = 8;
                }
                else if (symbol < 256)
                {
                    code = 0x190 + (symbol - 144);
                    bits = 9;
                }
                else if (symbol < 280)
                {
                    code = symbol - 256;
                    bits = 7;
                }
                else
                {
                    code = 0xC0 + (symbol - 280);
                    bits = 8;
                }

                LiteralCode[symbol] = Reverse(code, bits);
                LiteralBits[symbol] = bits;
            }

            for (int symbol = 0; symbol < 30; symbol++)
            {
                DistanceCode[symbol] = Reverse(symbol, 5);
            }

            for (int symbol = 0; symbol < LengthBase.Length; symbol++)
            {
                int from = LengthBase[symbol];
                // 227 to 257 is code 284; 258 alone is code 285.
                int to = symbol + 1 < LengthBase.Length ? LengthBase[symbol + 1] - 1 : MaxMatch;
                for (int length = from; length <= to; length++)
                {
                    LengthSymbol[length] = (byte)symbol;
                }
            }
        }

        /// <param name="chainLimit">How many earlier places with the same three bytes are tried for each repeat.</param>
        public Deflater(int chainLimit)
        {
            this.chainLimit = Math.Max(1, chainLimit);
        }

        public Deflater()
            : this(48)
        {
        }

        /// <summary>
        /// These bytes as DEFLATE blocks that are not the stream's last, ending on a byte boundary (an empty stored block, as
        /// zlib's sync flush does). Nothing in them refers to bytes outside this piece.
        /// </summary>
        public byte[] Compress(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            var output = new BitWriter(Math.Max(64, count / 3));
            for (int i = 0; i < HashSize; i++)
            {
                head[i] = -1;
            }

            // A fixed-code block, not the last: BFINAL 0, BTYPE 01.
            output.Write(0, 1);
            output.Write(1, 2);
            int end = offset + count;
            int position = offset;
            while (position < end)
            {
                int bestLength = 0;
                int bestDistance = 0;
                if (end - position >= MinMatch)
                {
                    int hash = Hash(data, position);
                    int candidate = head[hash];
                    int limit = Math.Min(MaxMatch, end - position);
                    int tries = chainLimit;
                    while (candidate >= offset && position - candidate <= WindowSize && tries-- > 0)
                    {
                        if (data[candidate + bestLength] == data[position + bestLength] && data[candidate] == data[position])
                        {
                            int length = 0;
                            while (length < limit && data[candidate + length] == data[position + length])
                            {
                                length++;
                            }

                            if (length > bestLength)
                            {
                                bestLength = length;
                                bestDistance = position - candidate;
                                if (length == limit)
                                {
                                    break;
                                }
                            }
                        }

                        int next = previous[candidate & WindowMask];
                        if (next >= candidate)
                        {
                            break;
                        }

                        candidate = next;
                    }

                    Insert(data, position, hash);
                }

                if (bestLength >= MinMatch)
                {
                    WriteMatch(output, bestLength, bestDistance);
                    for (int i = 1; i < bestLength; i++)
                    {
                        int at = position + i;
                        if (end - at >= MinMatch)
                        {
                            Insert(data, at, Hash(data, at));
                        }
                    }

                    position += bestLength;
                }
                else
                {
                    int literal = data[position];
                    output.Write(LiteralCode[literal], LiteralBits[literal]);
                    position++;
                }
            }

            output.Write(LiteralCode[EndOfBlock], LiteralBits[EndOfBlock]);

            // An empty stored block, not the last, which ends on a byte boundary: BFINAL 0, BTYPE 00, LEN 0, NLEN 0xFFFF.
            output.Write(0, 3);
            output.Align();
            output.WriteByte(0x00);
            output.WriteByte(0x00);
            output.WriteByte(0xFF);
            output.WriteByte(0xFF);
            return output.ToArray();
        }

        private static int Hash(byte[] data, int position)
        {
            uint value = (uint)((data[position] << 16) | (data[position + 1] << 8) | data[position + 2]);
            return (int)((value * 0x9E3779B1u) >> (32 - HashBits));
        }

        private void Insert(byte[] data, int position, int hash)
        {
            previous[position & WindowMask] = head[hash];
            head[hash] = position;
        }

        private static void WriteMatch(BitWriter output, int length, int distance)
        {
            int lengthSymbol = LengthSymbol[length];
            int literal = 257 + lengthSymbol;
            output.Write(LiteralCode[literal], LiteralBits[literal]);
            if (LengthExtra[lengthSymbol] > 0)
            {
                output.Write(length - LengthBase[lengthSymbol], LengthExtra[lengthSymbol]);
            }

            int distanceSymbol = DistanceSymbol(distance);
            output.Write(DistanceCode[distanceSymbol], 5);
            if (DistanceExtra[distanceSymbol] > 0)
            {
                output.Write(distance - DistanceBase[distanceSymbol], DistanceExtra[distanceSymbol]);
            }
        }

        private static int DistanceSymbol(int distance)
        {
            int symbol = 0;
            while (symbol + 1 < DistanceBase.Length && DistanceBase[symbol + 1] <= distance)
            {
                symbol++;
            }

            return symbol;
        }

        private static int Reverse(int code, int bits)
        {
            int reversed = 0;
            for (int i = 0; i < bits; i++)
            {
                reversed = (reversed << 1) | (code & 1);
                code >>= 1;
            }

            return reversed;
        }

        // Bits written from the lowest up, as DEFLATE packs them.
        private sealed class BitWriter
        {
            private readonly MemoryStream bytes;
            private uint pending;
            private int pendingBits;

            public BitWriter(int capacity)
            {
                bytes = new MemoryStream(capacity);
            }

            public void Write(int value, int bits)
            {
                pending |= (uint)value << pendingBits;
                pendingBits += bits;
                while (pendingBits >= 8)
                {
                    bytes.WriteByte((byte)pending);
                    pending >>= 8;
                    pendingBits -= 8;
                }
            }

            public void Align()
            {
                if (pendingBits > 0)
                {
                    bytes.WriteByte((byte)pending);
                    pending = 0;
                    pendingBits = 0;
                }
            }

            public void WriteByte(byte value)
            {
                bytes.WriteByte(value);
            }

            public byte[] ToArray()
            {
                return bytes.ToArray();
            }
        }
    }
}
