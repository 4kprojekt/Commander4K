using System.Globalization;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>
/// The small forms of the folder views for dashboard cards: counts instead of lists, four thumbnails instead of a
/// gallery. Everything is read through the host (fs.read), and only what the card shows is read.
/// </summary>
public static class FolderCards
{
    private const int Thumbnails = 4;
    private const int MaxImageBytes = 16 * 1024 * 1024;
    private const int MaxExcerpt = 300;

    /// <param name="view">A built-in view: files, gallery, notes, markdown or dashboard.</param>
    /// <param name="config">The target folder's <c>c4k.dir</c>; used by the markdown and dashboard cards.</param>
    public static async Task<PreviewCard> LoadAsync(string view, VfsEntry folder, FolderConfig? config, IPluginHost host,
        PreviewCardRequest request, FolderSizeCache sizes, CancellationToken cancellationToken)
    {
        CardAction[] open = [new(Loc.T("Otwórz"), folder.Path)];
        switch (view.ToLowerInvariant())
        {
            case FolderViewIds.Files:
            {
                var listing = await host.ListAsync(folder.Path, cancellationToken).ConfigureAwait(false);
                var changed = folder.LastWriteTime != default ? folder.LastWriteTime : listing.Select(e => e.LastWriteTime).DefaultIfEmpty().Max();
                var size = sizes.GetOrStart(host, folder);
                return new PreviewCard(null, null, "📁", CardStatus.None,
                [
                    new CardValue(Loc.T("Pliki"), Count(listing.Count(e => e.Kind == VfsEntryKind.File))),
                    new CardValue(Loc.T("Foldery"), Count(listing.Count(e => e.Kind == VfsEntryKind.Directory))),
                    new CardValue(Loc.T("Zmieniono"), changed == default ? "—" : changed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                    new CardValue(Loc.T("Rozmiar"), size is not { } s ? Loc.T("liczę…") : s.Bytes < 0 ? "—" : FormatBytes(s.Bytes)),
                ], open);
            }

            case FolderViewIds.Gallery:
            {
                var listing = await host.ListAsync(folder.Path, cancellationToken).ConfigureAwait(false);
                var images = listing.Where(FolderViews.IsImage).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                var pictures = new List<ReadOnlyMemory<byte>>();
                foreach (var image in images.Where(e => e.Size is >= 0 and <= MaxImageBytes).Take(Thumbnails))
                {
                    try
                    {
                        var (data, length, truncated) = await PreviewReading.ReadHeadAsync(host, image.Path, MaxImageBytes, cancellationToken).ConfigureAwait(false);
                        if (!truncated)
                        {
                            pictures.Add(data.AsMemory(0, length));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                return new PreviewCard(null, null, "🖼", CardStatus.None,
                [
                    new CardThumbnails(pictures),
                    new CardValue(Loc.T("Obrazy"), Count(images.Count)),
                    new CardValue(Loc.T("Rozmiar"), FormatBytes(images.Sum(e => Math.Max(0, e.Size)))),
                ], open);
            }

            case FolderViewIds.Notes:
            {
                var listing = await host.ListAsync(folder.Path, cancellationToken).ConfigureAwait(false);
                var notes = listing.Where(e => e.Kind == VfsEntryKind.File && e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList();
                var latest = notes.OrderByDescending(e => e.LastWriteTime).FirstOrDefault();
                return new PreviewCard(null, null, "🗒", CardStatus.None,
                [
                    new CardValue(Loc.T("Notatki"), Count(notes.Count)),
                    new CardValue(Loc.T("Ostatnio zmieniona"), notes.Count == 0 ? "—" : Path.GetFileNameWithoutExtension(latest.Name)),
                ], open);
            }

            case FolderViewIds.Markdown:
            {
                var file = config?.Resolve(config.Preview?.File ?? config.Enter?.File ?? "README.md") ?? FolderConfig.ChildPath(folder.Path, "README.md");
                var (data, length, _) = await PreviewReading.ReadHeadAsync(host, file, 64 * 1024, cancellationToken).ConfigureAwait(false);
                return new PreviewCard(null, null, "📄", CardStatus.None, [new CardText(FirstParagraph(PreviewReading.Decode(data, length)))], open);
            }

            case FolderViewIds.Dashboard:
            {
                var tiles = (config?.Enter is { View: FolderViewIds.Dashboard } enter ? enter : config?.Preview)?.Tiles ?? [];
                return new PreviewCard(config?.Title ?? folder.Name, config?.Description, "▦", CardStatus.None,
                [
                    new CardValue(Loc.T("Kafelki"), Count(tiles.Count)),
                    new CardText(string.Join(" · ", tiles.Take(3).Select(t => t.Title))),
                ], open);
            }

            default:
                return new PreviewCard(null, Loc.T("Nieznany widok karty „{0}”.", view), "⚠", CardStatus.Error, [], []);
        }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("N1", CultureInfo.CurrentCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("N1", CultureInfo.CurrentCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("N2", CultureInfo.CurrentCulture) + " GB",
    };

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The first paragraph of prose: front matter, headings and blank lines are skipped.</summary>
    internal static string FirstParagraph(string markdown)
    {
        var lines = markdown.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var start = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            start = Array.FindIndex(lines, 1, l => l.Trim() == "---") + 1;
        }

        var paragraph = lines.Skip(start)
            .SkipWhile(l => l.Trim().Length == 0 || l.TrimStart().StartsWith('#'))
            .TakeWhile(l => l.Trim().Length > 0)
            .Select(l => l.Trim());
        var text = string.Join(" ", paragraph);
        return text.Length <= MaxExcerpt ? text : text[..MaxExcerpt] + "…";
    }
}
