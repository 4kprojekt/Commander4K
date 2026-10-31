using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Commander4K.Core.Folders;

/// <summary>Adds dashboard tiles to a <c>c4k.dir</c> text (the "add API card" wizard); the rest of the file is kept.</summary>
public static class FolderConfigWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Comments are dropped when the file is written again, so the wizard warns about them first.</summary>
    public static bool HasComments(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Comment)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>
    /// The file text with <paramref name="tile"/> added to its dashboard. A new file opens the dashboard on Enter and
    /// shows it as the preview. A file whose preview is something else needs <paramref name="replacePreview"/>: the
    /// preview becomes the dashboard and the old preview moves to Enter when Enter was empty.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file has no dashboard and <paramref name="replacePreview"/> is false.</exception>
    public static string AppendTile(string? existing, JsonObject tile, bool replacePreview)
    {
        var root = existing is null
            ? new JsonObject { ["$schema"] = FolderConfig.SchemaUrl }
            : JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidOperationException("c4k.dir must contain a JSON object.");

        if (Dashboard(root["enter"]) is { } enter)
        {
            Tiles(enter).Add(tile);
        }
        else if (Dashboard(root["preview"]) is { } preview)
        {
            Tiles(preview).Add(tile);
        }
        else if (root["enter"] is null && (root["preview"] is null || IsDashboardName(root["preview"])))
        {
            root["enter"] = new JsonObject { ["view"] = FolderViewIds.Dashboard, ["tiles"] = new JsonArray(tile) };
            root["preview"] = FolderViewIds.Dashboard;
        }
        else if (replacePreview)
        {
            var old = root["preview"];
            root.Remove("preview");
            root["preview"] = new JsonObject { ["view"] = FolderViewIds.Dashboard, ["tiles"] = new JsonArray(tile) };
            if (root["enter"] is null && old is not null)
            {
                root["enter"] = old;
            }
        }
        else
        {
            throw new InvalidOperationException("The c4k.dir has no dashboard.");
        }

        return root.ToJsonString(Indented) + "\n";
    }

    private static JsonObject? Dashboard(JsonNode? action) =>
        action is JsonObject obj && obj["view"] is JsonValue view && view.TryGetValue<string>(out var name) && name == FolderViewIds.Dashboard ? obj : null;

    private static bool IsDashboardName(JsonNode? action) =>
        action is JsonValue value && value.TryGetValue<string>(out var name) && name == FolderViewIds.Dashboard;

    private static JsonArray Tiles(JsonObject dashboard)
    {
        if (dashboard["tiles"] is JsonArray tiles)
        {
            return tiles;
        }

        var created = new JsonArray();
        dashboard["tiles"] = created;
        return created;
    }
}
