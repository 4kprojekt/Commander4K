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
}
