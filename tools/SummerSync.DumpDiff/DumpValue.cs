using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SummerSync.DumpDiff;

internal enum DumpValueKind
{
    Null,
    Bool,
    Number,
    String,

    /// <summary>An array made only of numbers: a vector, quaternion, colour or rect.</summary>
    Numbers,

    /// <summary>Any other array or object, kept as its JSON text.</summary>
    Json,
}

/// <summary>
/// A value from a dump (a variable, an action parameter, a collection). Dumps hold values of mixed JSON types;
/// this keeps them compact and lets numbers be compared with a tolerance.
/// </summary>
[JsonConverter(typeof(DumpValueConverter))]
internal readonly struct DumpValue
{
    private DumpValue(DumpValueKind kind, double number, string? text, double[]? numbers)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Numbers = numbers;
    }

    public DumpValueKind Kind { get; }

    public double Number { get; }

    /// <summary>The string value, or the raw JSON text for <see cref="DumpValueKind.Json"/>.</summary>
    public string? Text { get; }

    public double[]? Numbers { get; }

    public static DumpValue Null => default;

    public static DumpValue FromBool(bool value) => new(DumpValueKind.Bool, value ? 1 : 0, null, null);

    public static DumpValue FromNumber(double value) => new(DumpValueKind.Number, value, null, null);

    public static DumpValue FromString(string value) => new(DumpValueKind.String, 0, value, null);

    public static DumpValue FromNumbers(double[] values) => new(DumpValueKind.Numbers, 0, null, values);

    public static DumpValue FromJson(string rawJson) => new(DumpValueKind.Json, 0, rawJson, null);

    /// <summary>Largest difference between the numeric components of two values of the same numeric kind.</summary>
    public static double Distance(DumpValue a, DumpValue b)
    {
        if (a.Kind == DumpValueKind.Number && b.Kind == DumpValueKind.Number)
        {
            return Math.Abs(a.Number - b.Number);
        }

        if (a.Kind == DumpValueKind.Numbers && b.Kind == DumpValueKind.Numbers && a.Numbers!.Length == b.Numbers!.Length)
        {
            double max = 0;
            for (int i = 0; i < a.Numbers.Length; i++)
            {
                max = Math.Max(max, Math.Abs(a.Numbers[i] - b.Numbers[i]));
            }

            return max;
        }

        return double.PositiveInfinity;
    }

    /// <summary>
    /// Whether two values are the same, treating numbers as equal when they differ by no more than
    /// <paramref name="absolute"/> plus <paramref name="relative"/> times their magnitude.
    /// </summary>
    public bool EqualsWithin(DumpValue other, double absolute, double relative)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        switch (Kind)
        {
            case DumpValueKind.Null:
                return true;
            case DumpValueKind.Bool:
                return Number == other.Number;
            case DumpValueKind.Number:
                return Close(Number, other.Number, absolute, relative);
            case DumpValueKind.String:
                return Text == other.Text;
            case DumpValueKind.Numbers:
                if (Numbers!.Length != other.Numbers!.Length)
                {
                    return false;
                }

                for (int i = 0; i < Numbers.Length; i++)
                {
                    if (!Close(Numbers[i], other.Numbers[i], absolute, relative))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return Text == other.Text || JsonEquals(Text!, other.Text!, absolute, relative);
        }
    }

    public override string ToString() => Kind switch
    {
        DumpValueKind.Null => "null",
        DumpValueKind.Bool => Number != 0 ? "true" : "false",
        DumpValueKind.Number => Format(Number),
        DumpValueKind.String => "\"" + Text + "\"",
        DumpValueKind.Numbers => "[" + string.Join(", ", Numbers!.Select(Format)) + "]",
        _ => Shorten(Text!),
    };

    public static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..157] + "...";

    private static bool Close(double a, double b, double absolute, double relative) =>
        a == b || Math.Abs(a - b) <= absolute + (relative * Math.Max(Math.Abs(a), Math.Abs(b)));

    private static bool JsonEquals(string a, string b, double absolute, double relative)
    {
        using JsonDocument first = JsonDocument.Parse(a);
        using JsonDocument second = JsonDocument.Parse(b);
        return ElementEquals(first.RootElement, second.RootElement, absolute, relative);
    }

    private static bool ElementEquals(JsonElement a, JsonElement b, double absolute, double relative)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Number:
                return Close(a.GetDouble(), b.GetDouble(), absolute, relative);
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength())
                {
                    return false;
                }

                for (int i = 0; i < a.GetArrayLength(); i++)
                {
                    if (!ElementEquals(a[i], b[i], absolute, relative))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Object:
                int count = 0;
                foreach (JsonProperty property in a.EnumerateObject())
                {
                    count++;
                    if (!b.TryGetProperty(property.Name, out JsonElement other) || !ElementEquals(property.Value, other, absolute, relative))
                    {
                        return false;
                    }
                }

                return count == b.EnumerateObject().Count();
            default:
                return true;
        }
    }
}

internal sealed class DumpValueConverter : JsonConverter<DumpValue>
{
    public override DumpValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return DumpValue.Null;
            case JsonTokenType.True:
                return DumpValue.FromBool(true);
            case JsonTokenType.False:
                return DumpValue.FromBool(false);
            case JsonTokenType.Number:
                return DumpValue.FromNumber(reader.GetDouble());
            case JsonTokenType.String:
                return DumpValue.FromString(reader.GetString()!);
            default:
                using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                        && root.EnumerateArray().All(element => element.ValueKind == JsonValueKind.Number))
                    {
                        return DumpValue.FromNumbers(root.EnumerateArray().Select(element => element.GetDouble()).ToArray());
                    }

                    return DumpValue.FromJson(root.GetRawText());
                }
        }
    }

    // A null JSON token must reach Read, otherwise the struct would never see it as an explicit null.
    public override bool HandleNull => true;

    public override void Write(Utf8JsonWriter writer, DumpValue value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Dumps are written by the in-game dumper only.");
}
