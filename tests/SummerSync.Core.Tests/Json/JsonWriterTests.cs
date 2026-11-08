using System.Text.Json;
using SummerSync.Core.Json;

namespace SummerSync.Core.Tests.Json;

public class JsonWriterTests
{
    private static string Write(Action<JsonWriter> body)
    {
        using var text = new StringWriter();
        var writer = new JsonWriter(text);
        body(writer);
        Assert.True(writer.IsComplete);
        return text.ToString();
    }

    [Fact]
    public void Writes_compact_nested_structures()
    {
        string json = Write(w => w
            .BeginObject()
                .Property("name", "Satsuma")
                .Property("gear", 3)
                .Property("running", true)
                .Name("wheels").BeginArray().Value(1).Value(2).EndArray()
                .Name("driver").Null()
                .Name("empty").BeginObject().EndObject()
            .EndObject());

        Assert.Equal("{\"name\":\"Satsuma\",\"gear\":3,\"running\":true,\"wheels\":[1,2],\"driver\":null,\"empty\":{}}", json);
    }

    [Fact]
    public void Output_is_accepted_by_a_real_parser()
    {
        string json = Write(w =>
        {
            w.BeginObject();
            w.Name("items").BeginArray();
            for (int i = 0; i < 3; i++)
            {
                w.Break();
                w.BeginObject().Property("id", i).Property("ratio", i / 3f).Property("big", 9_000_000_000L).EndObject();
            }

            w.Break();
            w.EndArray();
            w.EndObject();
        });

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement items = document.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(2, items[2].GetProperty("id").GetInt32());
        Assert.Equal(2 / 3f, items[2].GetProperty("ratio").GetSingle());
        Assert.Equal(9_000_000_000L, items[0].GetProperty("big").GetInt64());
        Assert.Equal(5, json.Split('\n').Length);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("quote \" and backslash \\")]
    [InlineData("line\nbreak\r\ttab")]
    [InlineData("control \u0001 \u001f chars")]
    [InlineData("accents áéíóú ñ and emoji \U0001F697")]
    [InlineData("path/with/slashes (557kg, 248)")]
    [InlineData("")]
    public void Strings_round_trip_through_a_real_parser(string value)
    {
        string json = Write(w => w.BeginArray().Value(value).EndArray());

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(value, document.RootElement[0].GetString());
    }

    [Fact]
    public void Unpaired_surrogates_are_escaped_instead_of_corrupting_the_file()
    {
        string json = Write(w => w.BeginArray().Value("a\ud83db").Value("c\udc00d").EndArray());

        Assert.Equal("[\"a\\ud83db\",\"c\\udc00d\"]", json);
    }

    [Fact]
    public void Property_names_are_escaped_too()
    {
        string json = Write(w => w.BeginObject().Property("we\"ird\nname", 1).EndObject());

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("we\"ird\nname").GetInt32());
    }

    [Theory]
    [InlineData(1.5f, "1.5")]
    [InlineData(-0.25f, "-0.25")]
    [InlineData(0f, "0")]
    [InlineData(1e-7f, "1E-07")]
    public void Floats_use_invariant_round_trip_format(float value, string expected)
    {
        Assert.Equal("[" + expected + "]", Write(w => w.BeginArray().Value(value).EndArray()));
    }

    [Fact]
    public void Floats_do_not_depend_on_the_current_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-AR");
        try
        {
            Assert.Equal("[1234.5,0.1]", Write(w => w.BeginArray().Value(1234.5f).Value(0.1d).EndArray()));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Non_finite_numbers_become_strings()
    {
        string json = Write(w => w.BeginArray()
            .Value(float.NaN).Value(float.PositiveInfinity).Value(double.NegativeInfinity)
            .EndArray());

        Assert.Equal("[\"NaN\",\"Infinity\",\"-Infinity\"]", json);
    }

    [Fact]
    public void Null_string_is_written_as_null()
    {
        Assert.Equal("[null]", Write(w => w.BeginArray().Value((string)null!).EndArray()));
    }

    [Fact]
    public void Raw_values_are_inserted_with_the_right_separators()
    {
        string json = Write(w => w.BeginObject()
            .Name("items").BeginArray().Raw("{\"a\":1}").Raw("[2]").Value(3).EndArray()
            .Name("one").Raw("null")
            .EndObject());

        Assert.Equal("{\"items\":[{\"a\":1},[2],3],\"one\":null}", json);
        Assert.Throws<ArgumentException>(() => new JsonWriter(new StringWriter()).Raw(""));
    }

    [Fact]
    public void A_scalar_can_be_the_root()
    {
        Assert.Equal("42", Write(w => w.Value(42)));
    }

    [Fact]
    public void Misuse_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).Name("x"));
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).BeginArray().Name("x"));
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).BeginObject().Value(1));
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).BeginObject().Name("a").Name("b"));
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).BeginObject().Name("a").EndObject());
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).BeginObject().Name("a").Break());
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).BeginArray().EndObject());
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).EndArray());
        Assert.Throws<InvalidOperationException>(() => new JsonWriter(new StringWriter()).Value(1).Value(2));
        Assert.Throws<ArgumentNullException>(() => new JsonWriter(null!));
    }

    [Fact]
    public void Incomplete_documents_are_reported()
    {
        var writer = new JsonWriter(new StringWriter());
        Assert.False(writer.IsComplete);

        writer.BeginObject();
        Assert.False(writer.IsComplete);

        writer.EndObject();
        Assert.True(writer.IsComplete);
    }
}
