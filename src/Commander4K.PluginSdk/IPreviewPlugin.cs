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

/// <summary>An HTML page from the plugin folder, shown in the browser engine.</summary>
/// <param name="RootFolder">Absolute folder (<c>&lt;plugin folder&gt;/&lt;webRoot&gt;</c>) the page and its files are served from.</param>
/// <param name="Page">Path of the page inside <paramref name="RootFolder"/>, with <c>/</c> separators.</param>
/// <param name="StateJson">Raw JSON of the result's <c>state</c>, or null.</param>
public sealed record WebPreview(string PluginId, string RootFolder, string Page, string? StateJson) : PreviewContent;

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
