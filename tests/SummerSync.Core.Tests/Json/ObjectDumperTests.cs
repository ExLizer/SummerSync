using System.Collections;
using SummerSync.Core.Json;

namespace SummerSync.Core.Tests.Json;

public class ObjectDumperTests
{
#pragma warning disable CS0649, CS0414 // Fields are read through reflection only.
    private enum Mode
    {
        Idle,
        Running,
    }

    private sealed class Sample
    {
        public string Name = "engine";
        public int Count = 3;
        public float Ratio = 0.5f;
        public bool Enabled = true;
        public Mode Mode = Mode.Running;
        public long Big = 5_000_000_000;
        public byte Small = 7;
        public string? Missing;
        public int[] Numbers = { 1, 2, 3 };
        public Inner Child = new();
        public Point Position = new() { X = 1, Y = 2 };
        private int hidden = 99;

        public int SideEffects => throw new InvalidOperationException("property getters must not be called");
    }

    private sealed class Inner
    {
        public string Label = "inner";
    }

    private struct Point
    {
        public int X;
        public int Y;
    }

    private sealed class Node
    {
        public string Name = "node";
        public Node? Next;
    }

    private sealed class Level
    {
        public int Depth;
        public Level? Deeper;
    }

    private sealed class Special
    {
        public string Value = "custom";
    }

    private sealed class Holder
    {
        public Special Item = new();
        public Special[] Items = { new(), new() };
    }
#pragma warning restore CS0649, CS0414

    private static string Dump(object? value, ObjectDumper? dumper = null)
    {
        using var text = new StringWriter();
        var writer = new JsonWriter(text);
        (dumper ?? new ObjectDumper()).Write(writer, value!);
        Assert.True(writer.IsComplete);
        return text.ToString();
    }

    [Fact]
    public void Writes_public_fields_and_never_touches_properties_or_private_fields()
    {
        string json = Dump(new Sample());

        Assert.Equal(
            "{\"Name\":\"engine\",\"Count\":3,\"Ratio\":0.5,\"Enabled\":true,\"Mode\":\"Running\",\"Big\":5000000000,\"Small\":7,"
            + "\"Missing\":null,\"Numbers\":[1,2,3],\"Child\":{\"Label\":\"inner\"},\"Position\":{\"X\":1,\"Y\":2}}",
            json);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("text", "\"text\"")]
    [InlineData(12, "12")]
    [InlineData(1.25f, "1.25")]
    [InlineData(2.5d, "2.5")]
    [InlineData(false, "false")]
    [InlineData('x', "\"x\"")]
    public void Scalars_are_written_directly(object? value, string expected)
    {
        Assert.Equal(expected, Dump(value));
    }

    [Fact]
    public void Cycles_are_cut()
    {
        var first = new Node();
        first.Next = new Node { Name = "second", Next = first };

        Assert.Equal("{\"Name\":\"node\",\"Next\":{\"Name\":\"second\",\"Next\":\"<cycle Node>\"}}", Dump(first));
    }

    [Fact]
    public void The_same_object_in_two_fields_is_not_a_cycle()
    {
        var shared = new Inner();

        Assert.Equal("[{\"Label\":\"inner\"},{\"Label\":\"inner\"}]", Dump(new[] { shared, shared }));
    }

    [Fact]
    public void Depth_is_bounded()
    {
        var chain = new Level { Depth = 1, Deeper = new Level { Depth = 2, Deeper = new Level { Depth = 3 } } };

        string json = Dump(chain, new ObjectDumper(null, maxDepth: 2, maxItems: 8));

        Assert.Equal("{\"Depth\":1,\"Deeper\":{\"Depth\":2,\"Deeper\":\"<Level>\"}}", json);
    }

    [Fact]
    public void Long_collections_are_truncated_with_a_count()
    {
        string json = Dump(Enumerable.Range(1, 10).ToArray(), new ObjectDumper(null, maxDepth: 3, maxItems: 3));

        Assert.Equal("[1,2,3,\"<truncated 7 more>\"]", json);
    }

    [Fact]
    public void Lists_and_dictionaries_are_supported()
    {
        var table = new Hashtable { ["fuel"] = 12.5f };
        var list = new ArrayList { "a", 1, null };

        Assert.Equal("{\"fuel\":12.5}", Dump(table));
        Assert.Equal("[\"a\",1,null]", Dump(list));
    }

    [Fact]
    public void Dictionary_keys_with_the_same_text_stay_distinct()
    {
        var table = new System.Collections.Specialized.ListDictionary { [1] = "int", ["1"] = "string" };

        Assert.Equal("{\"1\":\"int\",\"1#2\":\"string\"}", Dump(table));
    }

    [Fact]
    public void Custom_writer_gets_the_first_chance_at_every_level()
    {
        var dumper = new ObjectDumper(
            (value, writer) =>
            {
                if (value is not Special special)
                {
                    return false;
                }

                writer.Value("!" + special.Value);
                return true;
            },
            maxDepth: 3,
            maxItems: 8);

        Assert.Equal("{\"Item\":\"!custom\",\"Items\":[\"!custom\",\"!custom\"]}", Dump(new Holder(), dumper));
    }

    [Fact]
    public void Types_and_delegates_are_named_not_expanded()
    {
        Assert.Equal("[\"<RuntimeType>\",\"<Action>\"]", Dump(new object[] { typeof(string), new Action(() => { }) }));
    }

    [Fact]
    public void Field_properties_can_be_written_into_an_open_object()
    {
        using var text = new StringWriter();
        var writer = new JsonWriter(text);
        writer.BeginObject().Property("type", "Inner");
        new ObjectDumper().WriteFieldProperties(writer, new Inner());
        writer.EndObject();

        Assert.Equal("{\"type\":\"Inner\",\"Label\":\"inner\"}", text.ToString());
    }

    [Fact]
    public void Limits_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectDumper(null, 0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectDumper(null, 3, 0));
    }
}
