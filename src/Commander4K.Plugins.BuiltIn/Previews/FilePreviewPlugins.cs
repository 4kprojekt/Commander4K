using System.Text;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Viewers;

namespace Commander4K.Plugins.BuiltIn.Previews;

internal static class PreviewReading
{
    /// <summary>Reads at most <paramref name="maxBytes"/> through the host (permission-checked, audited).</summary>
    public static async Task<(byte[] Data, int Length, bool Truncated)> ReadHeadAsync(IPluginHost host, string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await host.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return (buffer, Math.Min(length, maxBytes), length > maxBytes);
    }

    public static string Decode(byte[] data, int length)
    {
        using var reader = new StreamReader(new MemoryStream(data, 0, length), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>Markdown rendered with formatting; switchable to (or opened directly in) side-by-side editing.</summary>
public sealed class MarkdownPreviewPlugin : IPreviewPlugin
{
    private const int MaxBytes = 4 * 1024 * 1024;

    private static readonly string[] Extensions = [".md", ".markdown", ".mdx", ".mdown"];

    public string Id => "builtin.preview.markdown";

    public string DisplayName => "Markdown";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.preview.markdown", Loc.T("Podgląd Markdown"),
        Loc.T("Sformatowany podgląd i edycja plików Markdown."), PluginPermissions.FileRead, PluginPermissions.FileWrite);

    public int Priority => 50;

    public bool CanPreview(VfsEntry entry) => entry.Kind == VfsEntryKind.File && Extensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase);

    public async Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var (data, length, truncated) = await PreviewReading.ReadHeadAsync(host, entry.Path, MaxBytes, cancellationToken).ConfigureAwait(false);
        return new MarkdownPreview(PreviewReading.Decode(data, length), CanEdit: !truncated);
    }
}

/// <summary>Text (editable) or, for binary content, a hex dump; the fallback for every file.</summary>
public sealed class TextPreviewPlugin : IPreviewPlugin
{
    private const int MaxBytes = 2 * 1024 * 1024;

    public string Id => "builtin.preview.text";

    public string DisplayName => Loc.T("Tekst / hex");

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.preview.text", Loc.T("Podgląd tekstu"),
        Loc.T("Podgląd i edycja plików tekstowych, zrzut szesnastkowy dla binarnych."), PluginPermissions.FileRead, PluginPermissions.FileWrite);

    public int Priority => 0;

    public bool CanPreview(VfsEntry entry) => entry.Kind is VfsEntryKind.File or VfsEntryKind.RegistryValue;

    public async Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var (data, length, truncated) = await PreviewReading.ReadHeadAsync(host, entry.Path, MaxBytes, cancellationToken).ConfigureAwait(false);
        if (TextHexViewerPlugin.LooksBinary(data.AsSpan(0, Math.Min(length, 8192))))
        {
            return new HexPreview(data.AsMemory(0, Math.Min(length, 64 * 1024)), entry.Size > 0 ? entry.Size : length);
        }

        return new TextPreview(PreviewReading.Decode(data, length), CanEdit: !truncated && entry.Kind == VfsEntryKind.File, truncated);
    }
}

public sealed class ImagePreviewPlugin : IPreviewPlugin
{
    private const int MaxBytes = 64 * 1024 * 1024;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    public string Id => "builtin.preview.image";

    public string DisplayName => Loc.T("Obrazy");

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.preview.image", Loc.T("Podgląd obrazów"),
        Loc.T("Podgląd plików graficznych."), PluginPermissions.FileRead);

    public int Priority => 40;

    public bool CanPreview(VfsEntry entry) => entry.Kind == VfsEntryKind.File && Extensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase);

    public async Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var (data, length, truncated) = await PreviewReading.ReadHeadAsync(host, entry.Path, MaxBytes, cancellationToken).ConfigureAwait(false);
        return truncated ? new MessagePreview(Loc.T("Obraz jest większy niż 64 MB.")) : new ImagePreview(data.AsMemory(0, length));
    }
}

/// <summary>
/// Folder contents with sizes: files immediately, then every subfolder's recursive size computed in parallel through
/// the host and pushed as it completes. Moving the cursor cancels the whole computation.
/// </summary>
public sealed class FolderPreviewPlugin : IPreviewCardPlugin, IFolderSuggestingPreview
{
    public Task<FolderSuggestion?> SuggestAsync(FolderSnapshot folder, IPluginHost host, CancellationToken cancellationToken) =>
        Task.FromResult(FolderSuggestions.Gallery(folder) ?? FolderSuggestions.Notes(folder));

    private const int MaxItems = 5000;
    private const int Parallelism = 4;

    /// <summary>Recursive sizes shown on folder cards, computed once per folder version.</summary>
    public FolderSizeCache Sizes { get; } = new();

    public Task<PreviewCard> LoadCardAsync(PreviewCardRequest request, IPluginHost host, CancellationToken cancellationToken) =>
        request.Target is { } folder
            ? FolderCards.LoadAsync(Core.Folders.FolderViewIds.Files, folder, null, host, request, Sizes, cancellationToken)
            : throw new ArgumentException("A folder card needs a target.", nameof(request));

    public string Id => "builtin.preview.folder";

    public string DisplayName => "Folder";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.preview.folder", Loc.T("Podgląd folderu"),
        Loc.T("Objętość folderu i podfolderów, doczytywana rekurencyjnie."), PluginPermissions.FileRead);

    public int Priority => 10;

    public bool CanPreview(VfsEntry entry) => entry.Kind is VfsEntryKind.Directory or VfsEntryKind.Drive or VfsEntryKind.Share;

    public async Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var listing = await host.ListAsync(entry.Path, cancellationToken).ConfigureAwait(false);

        // The view follows the content: notes (an Obsidian vault or a folder of Markdown) get their links and excerpts.
        if (entry.Kind == VfsEntryKind.Directory && listing.Any(e => e.Kind == VfsEntryKind.File && e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            var vault = await NotesOverview.FindVaultRootAsync(host, entry.Path, listing, cancellationToken).ConfigureAwait(false);
            if (vault is not null || NotesOverview.LooksLikeNotes(listing))
            {
                progress?.Report(new MarkdownPreview(Loc.T("⏳ Czytam notatki i ich powiązania…"), CanEdit: false));
                return new MarkdownPreview(await NotesOverview.BuildAsync(host, entry.Path, vault, cancellationToken).ConfigureAwait(false), CanEdit: false);
            }
        }

        var items = listing
            .Where(e => e.Kind is VfsEntryKind.File or VfsEntryKind.Directory)
            .Take(MaxItems)
            .Select(e => new FolderPreviewItem(e.Name, e.Path, e.Kind == VfsEntryKind.Directory, e.Kind == VfsEntryKind.Directory ? null : Math.Max(0, e.Size)))
            .ToArray();
        var gate = new Lock();
        long folderFiles = 0, folderFolders = 0;

        FolderPreview Snapshot(bool complete)
        {
            lock (gate)
            {
                var files = items.Count(i => !i.IsDirectory) + folderFiles;
                var folders = items.Count(i => i.IsDirectory) + folderFolders;
                return new FolderPreview(entry.Path, [.. items], items.Sum(i => i.Size ?? 0), files, folders, complete);
            }
        }

        progress?.Report(Snapshot(false));
        var directories = Enumerable.Range(0, items.Length).Where(i => items[i].IsDirectory).ToList();
        await Parallel.ForEachAsync(directories, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, async (index, ct) =>
        {
            DirectorySizeInfo size;
            try
            {
                size = await host.CalculateSizeAsync(items[index].Path, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                size = default;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // The cursor moved on; Parallel.ForEachAsync reports the cancellation itself.
            }

            lock (gate)
            {
                items[index] = items[index] with { Size = size.Bytes };
                folderFiles += size.Files;
                folderFolders += size.Folders;
            }

            progress?.Report(Snapshot(false));
        }).ConfigureAwait(false);

        return Snapshot(true);
    }
}
