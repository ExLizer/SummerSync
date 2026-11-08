using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SummerSync.Core.Net.Wire
{
    /// <summary>
    /// Writes values into a fixed-size buffer in little-endian order. The capacity is the datagram limit, so
    /// running out of space is an error of the caller and throws instead of growing.
    /// </summary>
    public sealed class PacketWriter
    {
        private readonly byte[] buffer;
        private int length;

        public PacketWriter(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException("capacity");
            }

            buffer = new byte[capacity];
        }

        public byte[] Buffer
        {
            get { return buffer; }
        }

        public int Length
        {
            get { return length; }
        }

        public int Remaining
        {
            get { return buffer.Length - length; }
        }

        /// <summary>Bytes <see cref="WriteVarUInt32"/> needs for a value.</summary>
        public static int VarUInt32Size(uint value)
        {
            int size = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                size++;
            }

            return size;
        }

        public void Reset()
        {
            length = 0;
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            buffer[length++] = value;
        }

        public void WriteBool(bool value)
        {
            WriteByte(value ? (byte)1 : (byte)0);
        }

        public void WriteUInt16(ushort value)
        {
            Ensure(2);
            buffer[length++] = (byte)value;
            buffer[length++] = (byte)(value >> 8);
        }

        public void WriteUInt32(uint value)
        {
            Ensure(4);
            buffer[length++] = (byte)value;
            buffer[length++] = (byte)(value >> 8);
            buffer[length++] = (byte)(value >> 16);
            buffer[length++] = (byte)(value >> 24);
        }

        public void WriteInt32(int value)
        {
            WriteUInt32(unchecked((uint)value));
        }

        public void WriteUInt64(ulong value)
        {
            WriteUInt32((uint)value);
            WriteUInt32((uint)(value >> 32));
        }

        public void WriteSingle(float value)
        {
            var bits = default(FloatBits);
            bits.Float = value;
            WriteUInt32(bits.Bits);
        }

        public void WriteDouble(double value)
        {
            WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        }

        /// <summary>7 bits per byte, least significant group first. Small numbers take one byte.</summary>
        public void WriteVarUInt32(uint value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        /// <summary>UTF-8 with a length prefix. Null is written as an empty string.</summary>
        public void WriteString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteVarUInt32(0);
                return;
            }

            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteVarUInt32((uint)byteCount);
            Ensure(byteCount);
            Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, length);
            length += byteCount;
        }

        public void WriteBytes(byte[] data, int offset, int count)
        {
            Ensure(count);
            System.Buffer.BlockCopy(data, offset, buffer, length, count);
            length += count;
        }

        private void Ensure(int count)
        {
            if (count > buffer.Length - length)
            {
                throw new InvalidOperationException(
                    "Packet is full: " + count + " more byte(s) do not fit in " + buffer.Length + " (" + length + " used).");
            }
        }
    }

    /// <summary>Reinterprets the bits of a float as an integer without allocating.</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [FieldOffset(0)]
        public float Float;

        [FieldOffset(0)]
        public uint Bits;
    }
}
