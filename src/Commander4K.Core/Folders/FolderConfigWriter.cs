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

/// <summary>Edits dashboard tiles in a <c>c4k.dir</c> file.</summary>
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

    /// <summary>Removes the tile at <paramref name="index"/> from the dashboard in the selected section.</summary>
    public static string RemoveTile(string existing, string section, int index, string? expectedTitle = null) =>
        EditTile(existing, section, index, expectedTitle, remove: true, moveBy: 0);

    /// <summary>Moves the tile at <paramref name="index"/> one place in the selected dashboard.</summary>
    public static string MoveTile(string existing, string section, int index, int moveBy, string? expectedTitle = null)
    {
        if (moveBy is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(moveBy), "A tile can only move one place up or down.");
        }

        return EditTile(existing, section, index, expectedTitle, remove: false, moveBy);
    }

    private static string EditTile(string existing, string section, int index, string? expectedTitle, bool remove, int moveBy)
    {
        var root = JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new InvalidOperationException("c4k.dir must contain a JSON object.");
        if (section is not ("preview" or "enter"))
        {
            throw new ArgumentOutOfRangeException(nameof(section), "Dashboard section must be preview or enter.");
        }

        var otherSection = section == "preview" ? "enter" : "preview";
        var otherNode = root[otherSection];
        var otherTiles = otherNode is JsonObject otherObject && IsDashboard(otherObject)
            ? otherObject["tiles"] as JsonArray
            : null;
        var action = root[section];
        JsonArray targetTiles;
        if (action is JsonObject dashboard && IsDashboard(dashboard))
        {
            if (dashboard["tiles"] is JsonArray configuredTiles && !(configuredTiles.Count == 0 && otherTiles is { Count: > 0 }))
            {
                targetTiles = configuredTiles;
            }
            else if (otherTiles is not null)
            {
                targetTiles = otherTiles;
            }
            else
            {
                targetTiles = new JsonArray();
                dashboard["tiles"] = targetTiles;
            }
        }
        else if (IsDashboardName(action))
        {
            if (otherTiles is not null)
            {
                targetTiles = otherTiles;
            }
            else
            {
                var dashboardObject = new JsonObject { ["view"] = FolderViewIds.Dashboard, ["tiles"] = new JsonArray() };
                root[section] = dashboardObject;
                targetTiles = (JsonArray)dashboardObject["tiles"]!;
            }
        }
        else
        {
            throw new InvalidOperationException($"The {section} section is not a dashboard.");
        }

        if ((uint)index >= (uint)targetTiles.Count && otherTiles is not null && (uint)index < (uint)otherTiles.Count)
        {
            targetTiles = otherTiles;
        }

        if (expectedTitle is not null && !HasTitleAt(targetTiles, index, expectedTitle))
        {
            var matchingIndex = FindTitle(targetTiles, expectedTitle);
            if (matchingIndex < 0 && otherTiles is not null)
            {
                var matchingOtherIndex = FindTitle(otherTiles, expectedTitle);
                if (matchingOtherIndex >= 0)
                {
                    targetTiles = otherTiles;
                    matchingIndex = matchingOtherIndex;
                }
            }

            if (matchingIndex >= 0)
            {
                index = matchingIndex;
            }
        }

        var mirror = otherTiles is not null && !ReferenceEquals(targetTiles, otherTiles) && JsonNode.DeepEquals(targetTiles, otherTiles)
            ? otherTiles
            : null;
        Edit(targetTiles, index, remove, moveBy);
        if (mirror is not null)
        {
            Edit(mirror, index, remove, moveBy);
        }

        return root.ToJsonString(Indented) + "\n";
    }

    private static bool IsDashboard(JsonObject action) =>
        action["view"] is JsonValue value && value.TryGetValue<string>(out var name)
        && string.Equals(name, FolderViewIds.Dashboard, StringComparison.OrdinalIgnoreCase);

    private static void Edit(JsonArray tiles, int index, bool remove, int moveBy)
    {
        if ((uint)index >= (uint)tiles.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "The dashboard tile does not exist.");
        }

        if (remove)
        {
            tiles.RemoveAt(index);
            return;
        }

        var destination = index + moveBy;
        if ((uint)destination >= (uint)tiles.Count)
        {
            throw new InvalidOperationException("The tile is already at the edge of the dashboard.");
        }

        var tile = tiles[index];
        tiles.RemoveAt(index);
        tiles.Insert(destination, tile);
    }

    private static bool HasTitleAt(JsonArray tiles, int index, string title) =>
        (uint)index < (uint)tiles.Count
        && tiles[index] is JsonObject tile
        && tile["title"] is JsonValue value
        && value.TryGetValue<string>(out var actual)
        && string.Equals(actual, title, StringComparison.Ordinal);

    private static int FindTitle(JsonArray tiles, string title)
    {
        for (var index = 0; index < tiles.Count; index++)
        {
            if (HasTitleAt(tiles, index, title))
            {
                return index;
            }
        }

        return -1;
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
