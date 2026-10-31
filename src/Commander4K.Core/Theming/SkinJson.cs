using System.Text.Json;
using Commander4K.Core.Configuration;

namespace Commander4K.Core.Theming;

/// <summary>Skin (de)serialisation with the same options as <c>config/skins/*.json</c>.</summary>
public static class SkinJson
{
    public static string Serialize(Skin skin) => JsonSerializer.Serialize(skin, ConfigJsonContext.Default.Skin);

    /// <exception cref="JsonException">Malformed JSON, a bad colour or a missing required property.</exception>
    public static Skin Deserialize(string json) =>
        JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Skin) ?? throw new JsonException("Empty skin document.");

    /// <summary>The skin JSON out of a whole pasted chat answer: the ```json block, else the outermost braces.</summary>
    public static string Extract(string text)
    {
        var fence = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence) + 1;
            var end = start > 0 ? text.IndexOf("```", start, StringComparison.Ordinal) : -1;
            if (end > start)
            {
                return text[start..end].Trim();
            }
        }

        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        return open >= 0 && close > open ? text[open..(close + 1)] : text.Trim();
    }
}
