using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.Tests.Net;

public class WireTests
{
    [Fact]
    public void Every_type_round_trips()
    {
        var writer = new PacketWriter(256);
        writer.WriteByte(200);
        writer.WriteBool(true);
        writer.WriteUInt16(65000);
        writer.WriteUInt32(4_000_000_000);
        writer.WriteInt32(-123456);
        writer.WriteUInt64(0x0123456789ABCDEFUL);
        writer.WriteSingle(-1.5f);
        writer.WriteDouble(Math.PI);
        writer.WriteVarUInt32(300);
        writer.WriteString("Satsuma (557kg) ñ \U0001F697");
        writer.WriteString(null!);
        writer.WriteBytes([9, 8, 7, 6], 1, 2);

        var reader = new PacketReader(writer.Buffer, 0, writer.Length);
        Assert.Equal(200, reader.ReadByte());
        Assert.True(reader.ReadBool());
        Assert.Equal(65000, reader.ReadUInt16());
        Assert.Equal(4_000_000_000, reader.ReadUInt32());
        Assert.Equal(-123456, reader.ReadInt32());
        Assert.Equal(0x0123456789ABCDEFUL, reader.ReadUInt64());
        Assert.Equal(-1.5f, reader.ReadSingle());
        Assert.Equal(Math.PI, reader.ReadDouble());
        Assert.Equal(300u, reader.ReadVarUInt32());
        Assert.Equal("Satsuma (557kg) ñ \U0001F697", reader.ReadString(256));
        Assert.Equal("", reader.ReadString(256));
        Assert.Equal(new byte[] { 8, 7 }, reader.ReadBytes(2));
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 2)]
    [InlineData(16383u, 2)]
    [InlineData(16384u, 3)]
    [InlineData(uint.MaxValue, 5)]
    public void Variable_length_integers_are_compact_and_exact(uint value, int expectedSize)
    {
        var writer = new PacketWriter(8);
        writer.WriteVarUInt32(value);

        Assert.Equal(expectedSize, writer.Length);
        Assert.Equal(expectedSize, PacketWriter.VarUInt32Size(value));
        Assert.Equal(value, new PacketReader(writer.Buffer, 0, writer.Length).ReadVarUInt32());
    }

    [Fact]
    public void Writer_refuses_to_overflow_its_buffer()
    {
        var writer = new PacketWriter(3);
        writer.WriteUInt16(1);

        Assert.Throws<InvalidOperationException>(() => writer.WriteUInt16(2));
        Assert.Equal(2, writer.Length);

        writer.Reset();
        Assert.Equal(0, writer.Length);
        Assert.Equal(3, writer.Remaining);
    }

    [Fact]
    public void Truncated_input_is_a_format_error_not_a_crash()
    {
        var reader = new PacketReader([1, 2, 3], 0, 3);
        reader.ReadUInt16();

        Assert.Throws<PacketFormatException>(() => reader.ReadUInt16());
        Assert.Throws<PacketFormatException>(() => new PacketReader([], 0, 0).ReadByte());
    }

    [Fact]
    public void Reader_stays_inside_its_range()
    {
        var reader = new PacketReader([10, 20, 30, 40], 1, 2);

        Assert.Equal(20, reader.ReadByte());
        Assert.Equal(30, reader.ReadByte());
        Assert.Throws<PacketFormatException>(() => reader.ReadByte());
    }

    [Fact]
    public void Hostile_lengths_are_rejected_before_reading()
    {
        // Claims a 2 GB string in a 6 byte packet.
        var huge = new PacketReader([0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x41], 0, 6);
        Assert.Throws<PacketFormatException>(() => huge.ReadString(64));

        // Claims 10 bytes but only 1 follows.
        var shortRead = new PacketReader([10, 0x41], 0, 2);
        Assert.Throws<PacketFormatException>(() => shortRead.ReadString(64));

        // Never-ending variable length integer.
        var endless = new PacketReader([0x80, 0x80, 0x80, 0x80, 0x80, 0x80], 0, 6);
        Assert.Throws<PacketFormatException>(() => endless.ReadVarUInt32());
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(5, 5, false)]
    [InlineData(0, 65535, true)]
    [InlineData(65535, 0, false)]
    [InlineData(32767, 0, true)]
    [InlineData(32768, 0, false)]
    public void Sequence_numbers_compare_across_the_wrap(int a, int b, bool expected)
    {
        Assert.Equal(expected, SequenceMath.IsNewer((ushort)a, (ushort)b));
    }

    [Fact]
    public void Sequence_distance_wraps()
    {
        Assert.Equal(3, SequenceMath.Distance(2, 65535));
        Assert.Equal(10, SequenceMath.Distance(110, 100));
    }
}
