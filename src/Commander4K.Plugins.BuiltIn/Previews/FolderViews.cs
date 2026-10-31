using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Dashboard;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>
/// The views a <c>c4k.dir</c> can name with <c>"view"</c>: a gallery of the folder's images, a dashboard of tiles, the
/// notes overview, a Markdown file, or the ordinary folder preview. Everything is read through the host (fs.read).
/// </summary>
public static class FolderViews
{
    private const int MaxImages = 400;
    private const int MaxImageBytes = 16 * 1024 * 1024;
    private const int MaxMarkdownBytes = 4 * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    internal static bool IsImage(VfsEntry entry) =>
        entry.Kind == VfsEntryKind.File && ImageExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Link a dashboard tile uses for its <c>run</c>: the window looks the command up in the same <c>c4k.dir</c>.</summary>
    public static string RunTarget(string section, int tile) => $"c4k:run/{section}/{tile}";

    /// <param name="section"><c>preview</c> or <c>enter</c> — which part of the file <paramref name="action"/> came from.</param>
    public static async Task<PreviewContent> LoadAsync(FolderConfig config, FolderAction action, string section, VfsEntry folder, IPluginHost host,
        IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var view = (action.View ?? FolderViewIds.Files).ToLowerInvariant();
        switch (view)
        {
            case FolderViewIds.Gallery:
                return await GalleryAsync(config, folder, host, progress, cancellationToken).ConfigureAwait(false);
            case FolderViewIds.Dashboard:
                return await WithPicturesAsync(Dashboard(config, action, section), config, host, cancellationToken).ConfigureAwait(false);
            case FolderViewIds.Notes:
                progress?.Report(new MarkdownPreview(Loc.T("⏳ Czytam notatki i ich powiązania…"), CanEdit: false));
                var listing = await host.ListAsync(folder.Path, cancellationToken).ConfigureAwait(false);
                var vault = await NotesOverview.FindVaultRootAsync(host, folder.Path, listing, cancellationToken).ConfigureAwait(false);
                return new MarkdownPreview(await NotesOverview.BuildAsync(host, folder.Path, vault, cancellationToken).ConfigureAwait(false), CanEdit: false);
            case FolderViewIds.Markdown:
                var file = config.Resolve(action.File ?? "README.md");
                var (data, length, _) = await PreviewReading.ReadHeadAsync(host, file, MaxMarkdownBytes, cancellationToken).ConfigureAwait(false);
                return new MarkdownPreview(PreviewReading.Decode(data, length), CanEdit: false);
            case FolderViewIds.Files:
                return await new FolderPreviewPlugin().LoadAsync(folder, host, progress, cancellationToken).ConfigureAwait(false);
            default:
                return new MessagePreview(Loc.T("c4k.dir: nieznany widok „{0}” (dostępne: {1}).", action.View ?? string.Empty, string.Join(", ", FolderViewIds.All)));
        }
    }

    /// <summary>Tiles appear at once with icons; the pictures fill in as they are read.</summary>
    private static async Task<TilesPreview> GalleryAsync(FolderConfig config, VfsEntry folder, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken)
    {
        var listing = await host.ListAsync(folder.Path, cancellationToken).ConfigureAwait(false);
        var images = listing
            .Where(e => e.Kind == VfsEntryKind.File && ImageExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxImages)
            .ToList();
        var title = config.Title ?? folder.Name;
        var description = config.Description ?? Loc.P(images.Count, "{0} obraz|{0} obrazy|{0} obrazów");
        var tiles = images.Select(e => new PreviewTile(e.Name, Size(e.Size), "🖼", null, e.Path)).ToArray();
        progress?.Report(new TilesPreview(title, [.. tiles], description, 220));

        for (var i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (images[i].Size is > MaxImageBytes)
            {
                continue;
            }

            try
            {
                var (data, length, truncated) = await PreviewReading.ReadHeadAsync(host, images[i].Path, MaxImageBytes, cancellationToken).ConfigureAwait(false);
                if (!truncated)
                {
                    tiles[i] = tiles[i] with { Image = data.AsMemory(0, length) };
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            if (i % 12 == 11)
            {
                progress?.Report(new TilesPreview(title, [.. tiles], description, 220));
            }
        }

        return new TilesPreview(title, tiles, description, 220);
    }

    /// <summary>The dashboard's tiles as shortcuts (no I/O); the window turns card tiles into live cards.</summary>
    public static DashboardPreview Dashboard(FolderConfig config, FolderAction action, string section)
    {
        var slots = new List<DashboardSlot>();
        for (var i = 0; i < action.Tiles.Count; i++)
        {
            var tile = action.Tiles[i];
            var target = tile.Run is not null ? RunTarget(section, i)
                : tile.Url is { } url ? url
                : tile.Open is { } open ? config.Resolve(open)
                : null;
            var subtitle = tile.Subtitle ?? (tile.Run is { } run ? "▶ " + run : null);
            slots.Add(new DashboardSlot(i, tile, new PreviewTile(tile.Title, subtitle, tile.Icon ?? (tile.Run is not null ? "▶" : tile.Url is not null ? "🌐" : "📁"), null, target)));
        }

        return new DashboardPreview(config.Title ?? Path.GetFileName(config.Folder.TrimEnd('\\', '/')),
            config.Description ?? (slots.Count == 0 ? Loc.T("Dodaj kafelki w c4k.dir: lista „tiles” z polami title oraz open, url albo run.") : null),
            config, section, slots);
    }

    /// <summary>Reads the tiles' own pictures (<c>image</c>).</summary>
    private static async Task<DashboardPreview> WithPicturesAsync(DashboardPreview dashboard, FolderConfig config, IPluginHost host, CancellationToken cancellationToken)
    {
        var slots = dashboard.Slots.ToArray();
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].Tile.Image is not { } picture)
            {
                continue;
            }

            try
            {
                var (data, length, truncated) = await PreviewReading.ReadHeadAsync(host, config.Resolve(picture), MaxImageBytes, cancellationToken).ConfigureAwait(false);
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
