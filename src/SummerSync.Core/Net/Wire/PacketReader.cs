using System;
using System.Text;

namespace SummerSync.Core.Net.Wire
{
    /// <summary>
    /// Thrown when received bytes do not form a valid packet. Datagrams come from the network, so every reader
    /// of one must expect this and drop the datagram instead of failing.
    /// </summary>
    public sealed class PacketFormatException : Exception
    {
        public PacketFormatException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Reads what <see cref="PacketWriter"/> wrote. Never reads outside the given range.</summary>
    public sealed class PacketReader
    {
        private byte[] buffer;
        private int position;
        private int end;

        public PacketReader()
        {
            buffer = new byte[0];
        }

        public PacketReader(byte[] buffer, int offset, int count)
        {
            SetBuffer(buffer, offset, count);
        }

        public int Remaining
        {
            get { return end - position; }
        }

        public void SetBuffer(byte[] data, int offset, int count)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            if (offset < 0 || count < 0 || offset > data.Length - count)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            buffer = data;
            position = offset;
            end = offset + count;
        }

        public byte ReadByte()
        {
            Require(1);
            return buffer[position++];
        }

        public bool ReadBool()
        {
            return ReadByte() != 0;
        }

        public ushort ReadUInt16()
        {
            Require(2);
            ushort value = (ushort)(buffer[position] | (buffer[position + 1] << 8));
            position += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            Require(4);
            uint value = (uint)(buffer[position] | (buffer[position + 1] << 8) | (buffer[position + 2] << 16) | (buffer[position + 3] << 24));
            position += 4;
            return value;
        }

        public int ReadInt32()
        {
            return unchecked((int)ReadUInt32());
        }

        public ulong ReadUInt64()
        {
            ulong low = ReadUInt32();
            ulong high = ReadUInt32();
            return low | (high << 32);
        }

        public float ReadSingle()
        {
            var bits = default(FloatBits);
            bits.Bits = ReadUInt32();
            return bits.Float;
        }

        public double ReadDouble()
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64()));
        }

        public uint ReadVarUInt32()
        {
            uint value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte next = ReadByte();
                value |= (uint)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new PacketFormatException("Variable-length integer is longer than 5 bytes.");
        }

        /// <param name="maxBytes">Upper bound for the encoded length, so a hostile length cannot cause a huge read.</param>
        public string ReadString(int maxBytes)
        {
            int byteCount = ReadLength(maxBytes);
            if (byteCount == 0)
            {
                return string.Empty;
            }

            string value = Encoding.UTF8.GetString(buffer, position, byteCount);
            position += byteCount;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0)
            {
                throw new PacketFormatException("Negative byte count.");
            }

            Require(count);
            var data = new byte[count];
            Buffer.BlockCopy(buffer, position, data, 0, count);
            position += count;
            return data;
        }

        /// <summary>Reads a length prefix and checks it against both a limit and the bytes actually left.</summary>
        public int ReadLength(int max)
        {
            uint length = ReadVarUInt32();
            if (length > (uint)max)
            {
                throw new PacketFormatException("Length " + length + " exceeds the limit of " + max + ".");
            }

            Require((int)length);
            return (int)length;
        }

        private void Require(int count)
        {
            if (count > end - position)
            {
                throw new PacketFormatException("Packet is truncated: needs " + count + " byte(s), " + (end - position) + " left.");
            }
        }
    }
}
