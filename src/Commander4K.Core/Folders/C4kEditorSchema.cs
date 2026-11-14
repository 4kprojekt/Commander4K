using System.Text.Json;
using System.Text.Json.Nodes;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Folders;

/// <summary>A preview or panel of a plugin as the editor offers it.</summary>
public sealed record C4kEditorTarget(string Id, string Title, bool Card, IReadOnlyList<OptionContribution> Options);

/// <summary>A plugin the editor can suggest for "plugin". Installed = false: only in the catalog (id and description are suggested, nothing else is known).</summary>
public sealed record C4kEditorPlugin(string Id, string Name, string? Description, bool Installed, IReadOnlyList<C4kEditorTarget> Previews, IReadOnlyList<C4kEditorTarget> Panels);

/// <summary>A built-in value of "card" (clock, note, disks…) with the options it reads.</summary>
public sealed record C4kEditorCard(string Id, string Description, IReadOnlyList<OptionContribution> Options);

/// <summary>
/// Builds the JSON Schema the .c4k editor uses: the static base schema plus completion and hints from the installed
/// plugins and built-in cards. Everything added is a suggestion: unknown ids and options stay valid.
/// </summary>
public static class C4kEditorSchema
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly HashSet<string> JsonTypes = ["string", "number", "integer", "boolean", "array", "object"];

    public static C4kEditorPlugin FromManifest(PluginManifest manifest, bool installed) => new(
        manifest.Id, manifest.Name, manifest.Description, installed,
        [.. (manifest.Contributes?.Previews ?? []).Select(p => new C4kEditorTarget(p.Id, p.Title, p.Card, p.Options ?? []))],
        [.. (manifest.Contributes?.Panels ?? []).Select(p => new C4kEditorTarget(p.Id, p.Title, p.Card, p.Options ?? []))]);

    public static JsonObject Build(string baseSchemaJson, IReadOnlyList<C4kEditorPlugin> plugins, IReadOnlyList<C4kEditorCard> cards)
    {
        var root = JsonNode.Parse(baseSchemaJson, documentOptions: Lenient)?.AsObject() ?? throw new JsonException("The base schema is not an object.");
        var properties = root["properties"] as JsonObject ?? [];
        root["properties"] = properties;
        var allOf = root["allOf"] as JsonArray ?? [];
        root["allOf"] = allOf;

        if (plugins.Count > 0)
        {
            // anyOf: the enum gives completion, the plain string keeps unknown or misspelt ids valid.
            var old = properties["plugin"] as JsonObject;
            properties["plugin"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = old?["description"]?.DeepClone() ?? "Plugin id.",
                ["anyOf"] = new JsonArray(Suggest(plugins.Select(p => p.Id), plugins.Select(p => Describe(p))), new JsonObject { ["type"] = "string" }),
            };
        }

        if (cards.Count > 0 && properties["card"] is JsonObject card && card["oneOf"] is JsonArray alternatives)
        {
            // anyOf, not oneOf: a card id matches both "string" and the suggestions, which oneOf would report.
            card.Remove("oneOf");
            alternatives.Add(Suggest(cards.Select(c => c.Id), cards.Select(c => c.Description)));
            card["anyOf"] = alternatives;
        }

        foreach (var plugin in plugins.Where(p => p.Installed && (p.Previews.Count + p.Panels.Count) > 0))
        {
            var then = new JsonObject();
            var thenProperties = new JsonObject();
            then["properties"] = thenProperties;
            if (plugin.Previews.Count > 0)
            {
                thenProperties["preview"] = TargetIds(plugin.Previews, "The plugin's preview id.");
            }

            if (plugin.Panels.Count > 0)
            {
                thenProperties["panel"] = TargetIds(plugin.Panels, "The plugin's panel id.");
            }

            if (OptionsSchema(plugin.Previews.Concat(plugin.Panels).SelectMany(t => t.Options)) is { } options)
            {
                thenProperties["options"] = options;
            }

            allOf.Add(Branch("plugin", plugin.Id, then));
        }

        foreach (var builtIn in cards)
        {
            if (OptionsSchema(builtIn.Options) is { } options)
            {
                allOf.Add(Branch("card", builtIn.Id, new JsonObject { ["properties"] = new JsonObject { ["options"] = options } }));
            }
        }

        return root;
    }

    private static string Describe(C4kEditorPlugin plugin) => plugin.Description is { Length: > 0 } d ? $"{plugin.Name}: {d}" : plugin.Name;

    private static JsonObject Suggest(IEnumerable<string> ids, IEnumerable<string> descriptions)
    {
        var texts = descriptions.ToList();
        return new JsonObject
        {
            ["enum"] = new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i)!)]),
            ["enumDescriptions"] = new JsonArray([.. texts.Select(t => (JsonNode)JsonValue.Create(t)!)]),
            ["markdownEnumDescriptions"] = new JsonArray([.. texts.Select(t => (JsonNode)JsonValue.Create(t)!)]),
        };
    }

    private static JsonObject TargetIds(IReadOnlyList<C4kEditorTarget> targets, string description) => new()
    {
        ["description"] = description,
        ["anyOf"] = new JsonArray(Suggest(targets.Select(t => t.Id), targets.Select(t => t.Title)), new JsonObject { ["type"] = "string" }),
    };

    private static JsonObject Branch(string key, string value, JsonObject then) => new()
    {
        ["if"] = new JsonObject
        {
            ["properties"] = new JsonObject { [key] = new JsonObject { ["const"] = value } },
            ["required"] = new JsonArray(key),
        },
        ["then"] = then,
    };

    /// <summary>Declared options as hints (first declaration of an id wins); no "required" and no closed set, so no valid file turns invalid.</summary>
    private static JsonObject? OptionsSchema(IEnumerable<OptionContribution> options)
    {
        var properties = new JsonObject();
        foreach (var option in options.Where(o => !properties.ContainsKey(o.Id)))
        {
            var property = new JsonObject { ["type"] = JsonTypes.Contains(option.Type) ? option.Type : "string" };
            if (option.Description is { Length: > 0 })
            {
                property["description"] = option.Description;
            }

            if (option.Values is { Count: > 0 })
            {
                property["examples"] = new JsonArray([.. option.Values.Select(v => (JsonNode)JsonValue.Create(v)!)]);
            }

            properties[option.Id] = property;
        }

        return properties.Count == 0 ? null : new JsonObject { ["properties"] = properties, ["additionalProperties"] = true };
    }
}
