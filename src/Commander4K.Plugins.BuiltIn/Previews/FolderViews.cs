using System.Diagnostics;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Dashboard;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>
/// The views a <c>.c4k</c> item can name with <c>"view"</c>: a gallery of the folder's images, the
/// notes overview, a Markdown file, or the ordinary folder preview; plus the dashboard of a folder with several items. Everything is read through the host (fs.read).
/// </summary>
public static class FolderViews
{
    private const int MaxImages = 400;
    private const int MaxImageBytes = 16 * 1024 * 1024;
    private const int MaxMarkdownBytes = 4 * 1024 * 1024;
    private const int GalleryBatch = 12;
    private static readonly TimeSpan GalleryPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan GalleryRedraw = TimeSpan.FromMilliseconds(500);

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    internal static bool IsImage(VfsEntry entry) =>
        entry.Kind == VfsEntryKind.File && ImageExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>An item file: not part of the folder's content when judging what the folder is.</summary>
    internal static bool IsC4k(VfsEntry entry) => entry.Name.EndsWith(C4kReader.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Link a dashboard tile uses for its <c>run</c>: the window reads the command again from that file.</summary>
    public static string RunTarget(string filePath) => "c4k:run/" + filePath;

    /// <summary>The view of one item.</summary>
    /// <param name="thumbnails">The gallery's thumbnails (<see cref="FolderPreviewPlugin.Thumbnails"/>).</param>
    public static async Task<PreviewContent> LoadAsync(C4kItem item, VfsEntry folder, IPluginHost host,
        ThumbnailStore thumbnails, IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var view = (item.View ?? FolderViewIds.Files).ToLowerInvariant();
        switch (view)
        {
            case FolderViewIds.Gallery:
                return await GalleryAsync(item, folder, host, thumbnails, progress, cancellationToken).ConfigureAwait(false);
            case FolderViewIds.Notes:
                progress?.Report(new MarkdownPreview(Loc.T("⏳ Czytam notatki i ich powiązania…"), CanEdit: false));
                var listing = await host.ListAsync(folder.Path, cancellationToken).ConfigureAwait(false);
                var vault = await NotesOverview.FindVaultRootAsync(host, folder.Path, listing, cancellationToken).ConfigureAwait(false);
                return new MarkdownPreview(await NotesOverview.BuildAsync(host, folder.Path, vault, cancellationToken).ConfigureAwait(false), CanEdit: false);
            case FolderViewIds.Markdown:
                var file = item.Resolve(item.File ?? "README.md");
                var (data, length, _) = await PreviewReading.ReadHeadAsync(host, file, MaxMarkdownBytes, cancellationToken).ConfigureAwait(false);
                return new MarkdownPreview(PreviewReading.Decode(data, length), CanEdit: false);
            case FolderViewIds.Files:
                return await new FolderPreviewPlugin().LoadAsync(folder, host, progress, cancellationToken).ConfigureAwait(false);
            default:
                return new MessagePreview(Loc.T(".c4k: nieznany widok „{0}” (dostępne: {1}).", item.View ?? string.Empty, string.Join(", ", FolderViewIds.All)));
        }
    }

    /// <summary>
    /// Tiles appear at once with icons; the pictures are small thumbnails that <see cref="ThumbnailStore"/> reads from
    /// the folder's <c>.thumbnail.c4k</c> or makes on its own threads, so a share full of photos never holds the view.
    /// </summary>
    private static async Task<TilesPreview> GalleryAsync(C4kItem item, VfsEntry folder, IPluginHost host, ThumbnailStore thumbnails,
        IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var listing = await host.ListAsync(folder.Path, cancellationToken).ConfigureAwait(false);
        var images = listing
            .Where(IsImage)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxImages)
            .ToList();
        var title = item.Title;
        var description = item.Description ?? Loc.P(images.Count, "{0} obraz|{0} obrazy|{0} obrazów");
        var tiles = images.Select(e => new PreviewTile(e.Name, Size(e.Size), "🖼", null, e.Path)).ToArray();
        progress?.Report(new TilesPreview(title, [.. tiles], description, 220));

        var gate = new Lock();
        var changed = 0;
        var job = thumbnails.RequestAsync(host, folder.Path, images, (index, picture) =>
        {
            lock (gate)
            {
                tiles[index] = tiles[index] with { Image = picture };
                changed++;
            }
        }, cancellationToken);

        TilesPreview Snapshot()
        {
            lock (gate)
            {
                changed = 0;
                return new TilesPreview(title, [.. tiles], description, 220);
            }
        }

        // Pictures arrive on the thumbnail threads; the view is redrawn in batches, never per picture.
        var reported = Stopwatch.GetTimestamp();
        while (!job.IsCompleted)
        {
            await Task.WhenAny(job, Task.Delay(GalleryPoll, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            bool due;
            lock (gate)
            {
                due = changed >= GalleryBatch || (changed > 0 && Stopwatch.GetElapsedTime(reported) >= GalleryRedraw);
            }

            if (due && !job.IsCompleted)
            {
                progress?.Report(Snapshot());
                reported = Stopwatch.GetTimestamp();
            }
        }

        await job.ConfigureAwait(false);
        return Snapshot();
    }

    /// <summary>The folder's items as shortcut tiles (no I/O); the window turns card items into live cards.</summary>
    public static DashboardPreview Dashboard(C4kFolder folder)
    {
        var slots = new List<DashboardSlot>();
        for (var i = 0; i < folder.Items.Count; i++)
        {
            var item = folder.Items[i];
            var target = item.Url ?? (item.Open is { } open ? item.Resolve(open) : item.Run is not null ? RunTarget(item.FilePath) : null);
            var subtitle = item.Description ?? (item.Run is { } run ? "▶ " + run : null);
            slots.Add(new DashboardSlot(i, item, new PreviewTile(item.Title, subtitle, item.Icon ?? (item.Run is not null ? "▶" : item.Url is not null ? "🌐" : "📁"), null, target)));
        }

        return new DashboardPreview(Path.GetFileName(folder.Folder.TrimEnd('\\', '/')),
            folder.Skipped > 0 ? Loc.T("Pominięto {0} plików .c4k", folder.Skipped) : null, folder, slots);
    }

    /// <summary>Reads the tiles' own pictures (<c>image</c>); the window calls it before showing the dashboard.</summary>
    public static async Task<DashboardPreview> WithPicturesAsync(DashboardPreview dashboard, IPluginHost host, CancellationToken cancellationToken)
    {
        var slots = dashboard.Slots.ToArray();
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].Item.Image is not { } picture)
            {
                continue;
            }

            try
            {
                var (data, length, truncated) = await PreviewReading.ReadHeadAsync(host, slots[i].Item.Resolve(picture), MaxImageBytes, cancellationToken).ConfigureAwait(false);
                if (!truncated)
                {
                    slots[i] = slots[i] with { Shortcut = slots[i].Shortcut with { Image = data.AsMemory(0, length) } };
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }

        return dashboard with { Slots = slots };
    }

    private static string? Size(long bytes) => bytes switch
    {
        < 0 => null,
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        _ => $"{bytes / (1024.0 * 1024):N1} MB",
    };
}
