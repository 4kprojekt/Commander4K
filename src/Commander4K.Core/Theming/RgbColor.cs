using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commander4K.Core.Theming;

/// <summary>UI-agnostic colour, written in JSON as <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
[JsonConverter(typeof(RgbColorJsonConverter))]
public readonly record struct RgbColor(byte R, byte G, byte B, byte A = 255)
{
    public static RgbColor Parse(string text)
    {
        if (!TryParse(text, out var color))
        {
            throw new FormatException($"'{text}' is not a #RRGGBB or #AARRGGBB colour.");
        }

        return color;
    }

    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        if (text is null || text.Length is not (7 or 9) || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var alpha = text.Length == 9 ? (byte)(value >> 24) : (byte)255;
        color = new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value, alpha);
        return true;
    }

    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

public sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RgbColor.TryParse(reader.GetString(), out var color)
            ? color
            : throw new JsonException($"Invalid colour '{reader.GetString()}'; expected #RRGGBB or #AARRGGBB.");

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
