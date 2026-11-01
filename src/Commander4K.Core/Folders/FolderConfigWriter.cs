using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Commander4K.Core.Folders;

public enum FolderConfigWrite
{
    Written,
    AlreadyExists,
    Failed,
}

/// <summary>Adds dashboard tiles to a <c>c4k.dir</c> text (the "add API card" wizard); the rest of the file is kept.</summary>
public static class FolderConfigWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A new <c>c4k.dir</c> with one action (<c>cm_FolderView</c>, an accepted folder suggestion).</summary>
    /// <param name="where"><c>both</c>, <c>preview</c> or <c>enter</c>.</param>
    public static string CreateForAction(JsonNode? action, string where)
    {
        var root = new JsonObject { ["$schema"] = FolderConfig.SchemaUrl };
        if (where is "both" or "preview")
        {
            root["preview"] = action?.DeepClone();
        }

        if (where is "both" or "enter")
        {
            root["enter"] = action?.DeepClone();
        }

        return root.ToJsonString(Indented) + "\n";
    }

    /// <summary>
    /// Writes a new <c>c4k.dir</c> but never replaces one (an accepted suggestion must not destroy a hand-written file).
    /// A hung share is abandoned after <paramref name="timeout"/>; every failure is reported, never thrown.
    /// </summary>
    public static async Task<(FolderConfigWrite Result, string? Error)> CreateNewAsync(string file, string json, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => WriteNewAsync(file, json), CancellationToken.None).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            return (FolderConfigWrite.Failed, ex.Message);
        }
    }

    private static async Task<(FolderConfigWrite Result, string? Error)> WriteNewAsync(string file, string json)
    {
        try
        {
            await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
            return (FolderConfigWrite.Written, null);
        }
        catch (IOException) when (File.Exists(file))
        {
            return (FolderConfigWrite.AlreadyExists, null);
        }
        catch (Exception ex)
        {
            return (FolderConfigWrite.Failed, ex.Message);
        }
    }

    /// <summary>The <c>c4k.dir</c> action for a suggested view: a view name, or <c>{ plugin, preview?, options? }</c>.</summary>
    public static JsonNode ActionFor(PluginSdk.FolderSuggestionView view)
    {
        if (view.Plugin is null)
        {
            return JsonValue.Create(view.View ?? FolderViewIds.Files);
        }

        var action = new JsonObject { ["plugin"] = view.Plugin };
        if (view.Preview is not null)
        {
            action["preview"] = view.Preview;
        }

        if (view.OptionsJson is not null)
        {
            action["options"] = JsonNode.Parse(view.OptionsJson);
        }

        return action;
    }

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
    /// preview becomes the dashboard and the old preview moves to Enter when Enter was empty. When
    /// <paramref name="preferPreview"/> is true, the dashboard shown in the preview is updated or created instead.
    /// </summary>
    /// <param name="preferPreview">Prefer the preview dashboard over an Enter dashboard.</param>
    /// <exception cref="InvalidOperationException">The selected dashboard is missing and replacement is disabled.</exception>
    public static string AppendTile(string? existing, JsonObject tile, bool replacePreview, bool preferPreview = false)
    {
        var root = existing is null
            ? new JsonObject { ["$schema"] = FolderConfig.SchemaUrl }
            : JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidOperationException("c4k.dir must contain a JSON object.");

        if (preferPreview && Dashboard(root["preview"]) is { } preferredPreview)
        {
            Tiles(preferredPreview).Add(tile);
        }
        else if (preferPreview && IsDashboardName(root["preview"]))
        {
            var dashboard = Dashboard(root["enter"]) is { } enterDashboardForPreview
                ? (JsonObject)enterDashboardForPreview.DeepClone()
                : new JsonObject { ["view"] = FolderViewIds.Dashboard };
            Tiles(dashboard).Add(tile);
            root["preview"] = dashboard;
            if (root["enter"] is null)
            {
                root["enter"] = dashboard.DeepClone();
            }
        }
        else if (preferPreview && root["preview"] is null && Dashboard(root["enter"]) is { } enterDashboardForCopy)
        {
            var dashboard = (JsonObject)enterDashboardForCopy.DeepClone();
            Tiles(dashboard).Add(tile);
            root["preview"] = dashboard;
        }
        else if (preferPreview && root["preview"] is not null && replacePreview)
        {
            ReplacePreviewWithDashboard(root, tile);
        }
        else if (preferPreview && root["preview"] is not null)
        {
            throw new InvalidOperationException("The c4k.dir preview is not a dashboard.");
        }
        else if (Dashboard(root["enter"]) is { } defaultEnterDashboard)
        {
            Tiles(defaultEnterDashboard).Add(tile);
        }
        else if (Dashboard(root["preview"]) is { } defaultPreview)
        {
            Tiles(defaultPreview).Add(tile);
        }
        else if (root["enter"] is null && (root["preview"] is null || IsDashboardName(root["preview"])))
        {
            root["enter"] = new JsonObject { ["view"] = FolderViewIds.Dashboard, ["tiles"] = new JsonArray(tile) };
            root["preview"] = FolderViewIds.Dashboard;
        }
        else if (replacePreview)
        {
            ReplacePreviewWithDashboard(root, tile);
        }
        else
        {
            throw new InvalidOperationException("The c4k.dir has no dashboard.");
        }

        return root.ToJsonString(Indented) + "\n";
    }

    private static void ReplacePreviewWithDashboard(JsonObject root, JsonObject tile)
    {
        var old = root["preview"];
        root.Remove("preview");
        root["preview"] = new JsonObject { ["view"] = FolderViewIds.Dashboard, ["tiles"] = new JsonArray(tile) };
        if (root["enter"] is null && old is not null)
        {
            root["enter"] = old;
        }
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
