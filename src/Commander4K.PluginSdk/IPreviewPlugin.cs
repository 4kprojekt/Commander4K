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

/// <summary>Plain message (nothing to preview, access denied...).</summary>
public sealed record MessagePreview(string Message) : PreviewContent;

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
