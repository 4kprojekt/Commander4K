namespace Commander4K.PluginSdk;

/// <summary>What a preview shows; the host renders each kind (and offers editing where <c>CanEdit</c> is set).</summary>
public abstract record PreviewContent;

/// <summary>Markdown rendered with headings, lists, tables, code and quotes; editable side by side when <paramref name="CanEdit"/>.</summary>
public sealed record MarkdownPreview(string Markdown, bool CanEdit) : PreviewContent;

public sealed record TextPreview(string Text, bool CanEdit, bool Truncated = false) : PreviewContent;

public sealed record ImagePreview(ReadOnlyMemory<byte> EncodedImage) : PreviewContent;

public sealed record HexPreview(ReadOnlyMemory<byte> Data, long TotalLength) : PreviewContent;

/// <param name="Size">Bytes, or <c>null</c> while a folder's size is still being computed.</param>
public sealed record FolderPreviewItem(string Name, string Path, bool IsDirectory, long? Size);

/// <summary>Contents of a folder with sizes; previews push updated instances through progress as sizes arrive.</summary>
public sealed record FolderPreview(string Path, IReadOnlyList<FolderPreviewItem> Items, long TotalBytes, long Files, long Folders, bool Complete) : PreviewContent;

/// <summary>One tile of a <see cref="TilesPreview"/>.</summary>
/// <param name="Icon">An emoji or a short text shown when there is no image.</param>
/// <param name="Image">Encoded image (PNG, JPEG…) shown on the tile.</param>
/// <param name="Target">
/// What a click does: a path (opened like Enter), an <c>http(s)</c> URL (the browser panel), another URI such as
/// <c>steam://rungameid/570</c> (opened by Windows after the user confirms the scheme once), or a <c>c4k:</c> link
/// (<c>c4k:install/&lt;plugin id&gt;</c>, <c>c4k:run/&lt;tile&gt;</c> for a <c>c4k.dir</c> script). <c>null</c> = not clickable.
/// </param>
public sealed record PreviewTile(string Title, string? Subtitle = null, string? Icon = null, ReadOnlyMemory<byte>? Image = null, string? Target = null);

/// <summary>A grid of tiles: a gallery, a dashboard, a game library…</summary>
/// <param name="TileWidth">Tile width in pixels (images keep their aspect ratio).</param>
public sealed record TilesPreview(string Title, IReadOnlyList<PreviewTile> Tiles, string? Description = null, double TileWidth = 200) : PreviewContent;

/// <summary>A chart as the whole preview.</summary>
public sealed record ChartPreview(string Title, ChartData Chart, string? Description = null) : PreviewContent;

/// <summary>Plain message (nothing to preview, access denied...).</summary>
public sealed record MessagePreview(string Message) : PreviewContent;

/// <summary>A button or link of an interactive preview.</summary>
/// <param name="Target">Like a card action target, plus <c>c4k:plugin/&lt;action&gt;[?k=v&amp;…]</c> which calls <see cref="IInteractivePreviewPlugin.InvokeActionAsync"/>.</param>
public sealed record PreviewAction(string Label, string Target, string? Shortcut = null);

/// <summary>Another preview with a row of actions under it.</summary>
public sealed record ActionsPreview(PreviewContent Content, IReadOnlyList<PreviewAction> Actions) : PreviewContent;

public sealed record FormField(string Id, string Label, string Value, bool Multiline = false);

public sealed record FormFile(string Name, string Path, long Size);

/// <summary>Editable fields, attached files and buttons (composing a message…).</summary>
public sealed record FormPreview(
    string Title,
    IReadOnlyList<FormField> Fields,
    IReadOnlyList<FormFile> Files,
    IReadOnlyList<PreviewAction> Buttons,
    string? Message = null,
    bool MessageIsError = false) : PreviewContent;

/// <param name="FormValues">Current field values when the action comes from a <see cref="FormPreview"/>.</param>
public sealed record PreviewActionRequest(
    VfsEntry Entry,
    string? OptionsJson,
    string Action,
    IReadOnlyDictionary<string, string> Arguments,
    IReadOnlyDictionary<string, string>? FormValues);

/// <summary>A preview that reacts to its <see cref="PreviewAction"/> buttons and form edits.</summary>
public interface IInteractivePreviewPlugin : IPreviewPlugin
{
    /// <returns>New content to show, or <c>null</c> to render the entry again.</returns>
    Task<PreviewContent?> InvokeActionAsync(PreviewActionRequest request, IPluginHost host, CancellationToken cancellationToken);

    Task FormChangedAsync(VfsEntry entry, string? optionsJson, IReadOnlyDictionary<string, string> values, IPluginHost host, CancellationToken cancellationToken);
}

/// <summary>Parses <c>c4k:plugin/&lt;action&gt;?k=v&amp;…</c> targets.</summary>
public static class PreviewActionTargets
{
    public const string PluginPrefix = "c4k:plugin/";

    public static bool TryParse(string target, out string action, out IReadOnlyDictionary<string, string> arguments)
    {
        action = string.Empty;
        arguments = new Dictionary<string, string>();
        if (target is null || !target.StartsWith(PluginPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = target[PluginPrefix.Length..];
        var query = rest.IndexOf('?');
        var name = query < 0 ? rest : rest[..query];
        if (name.Length == 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query >= 0)
        {
            foreach (var pair in rest[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                values[key] = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        action = Uri.UnescapeDataString(name);
        arguments = values;
        return true;
    }
}

/// <summary>
/// Quick view (Ctrl+Q / F2 ▸ Podgląd) for the entry under the cursor. The host picks the highest-priority plugin whose
/// <see cref="CanPreview"/> accepts the entry, cancels the load as soon as the cursor or folder changes, and saves edits
/// through <see cref="IPluginHost.WriteTextAsync"/> (so editing needs <see cref="PluginPermissions.FileWrite"/>).
/// </summary>
public interface IPreviewPlugin : IPlugin
{
    int Priority { get; }

    bool CanPreview(VfsEntry entry);

    /// <param name="progress">Intermediate states for slow previews (folder sizes filling in).</param>
    Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken);
}

public readonly record struct DirectorySizeInfo(long Bytes, long Files, long Folders, int Inaccessible);
