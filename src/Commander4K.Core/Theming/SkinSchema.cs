using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Commander4K.Core.Configuration;

namespace Commander4K.Core.Theming;

/// <summary>JSON Schema of a skin file, derived from the <see cref="Skin"/> model so it can never drift from it.</summary>
public static class SkinSchema
{
    public const string FileName = "skin.schema.json";

    private const string ColorPattern = "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$";
    private const string ColorDescription = "Colour as #RRGGBB or #AARRGGBB (hex).";

    /// <summary>Documentation schema for editors and for pasting into a chat with Claude.</summary>
    public static JsonNode Create() => Export(strictForApi: false);

    /// <summary>
    /// Variant for Claude API structured outputs: every object closed (<c>additionalProperties: false</c>) with all
    /// properties required, and no <c>pattern</c> (unsupported there); colours are validated when parsing instead.
    /// </summary>
    public static JsonNode CreateForStructuredOutput() => Export(strictForApi: true);

    public static string CreateJson() => Create().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonNode Export(bool strictForApi)
    {
        var options = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (context.PropertyInfo is { Name: "themeVariant" } && node is JsonObject theme)
                {
                    theme["enum"] = new JsonArray("Dark", "Light");
                    return theme;
                }

                if (context.TypeInfo.Type == typeof(RgbColor) || context.TypeInfo.Type == typeof(RgbColor?))
                {
                    var color = new JsonObject { ["type"] = "string", ["description"] = ColorDescription };
                    if (!strictForApi)
                    {
                        color["pattern"] = ColorPattern;
                    }

                    return color;
                }

                if (strictForApi && node is JsonObject obj && obj["properties"] is JsonObject properties)
                {
                    obj["additionalProperties"] = false;
                    obj["required"] = new JsonArray([.. properties.Select(p => (JsonNode)JsonValue.Create(p.Key))]);
                }

                return node;
            },
        };

        var schema = ConfigJsonContext.Default.Skin.GetJsonSchemaAsNode(options);
        if (!strictForApi && schema is JsonObject root)
        {
            root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            root["title"] = "Commander4K skin";
        }

        return schema;
    }
}
