using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Previews;
using Commander4K.Plugins.BuiltIn.Remote;

namespace Commander4K.Plugins.BuiltIn.Dashboard;

/// <summary>Reads one card; <paramref name="previous"/> is the last card it returned, for charts and differences.</summary>
public delegate Task<PreviewCard> CardLoader(PreviewCard? previous, CancellationToken cancellationToken);

/// <summary>How a tile's card is read.</summary>
/// <param name="NeedsTrust">The card may change something or sign in somewhere (POST, SSH): it waits for the user's trust in the <c>c4k.dir</c>.</param>
/// <param name="Unavailable">Why such a card cannot run from where the <c>c4k.dir</c> lives; the loader is never called then.</param>
public sealed record CardSource(CardLoader Load, bool NeedsTrust, string? Unavailable);

/// <summary>
/// Decides which preview draws a dashboard tile as a card: a built-in view, the API or SSH card, a plugin, or — for
/// <c>"card": true</c> — what Ctrl+Q would show for the target, including the target folder's own <c>c4k.dir</c>.
/// </summary>
public sealed class CardResolver(
    IReadOnlyList<IPreviewPlugin> previews,
    Func<PluginManifest, IPluginHost> hostFor,
    Func<string, CancellationToken, Task<FolderConfig?>> readFolderConfig,
    Func<string, bool> inCatalog,
    ApiCards api,
    SshMonitor ssh)
{
    private static readonly string[] FolderCardViews = [FolderViewIds.Files, FolderViewIds.Gallery, FolderViewIds.Notes, FolderViewIds.Markdown, FolderViewIds.Dashboard];

    private readonly FolderPreviewPlugin _folders = previews.OfType<FolderPreviewPlugin>().FirstOrDefault() ?? new FolderPreviewPlugin();

    /// <returns><c>null</c> for a plain shortcut tile.</returns>
    public async Task<CardSource?> ResolveAsync(FolderConfig config, FolderTile tile, CancellationToken cancellationToken)
    {
        if (tile.Card is not { } card)
        {
            return null;
        }

        if (tile.Error is { } error)
        {
            return Fixed(ErrorCard(error));
        }

        var target = tile.Open is { } open ? config.Resolve(open) : null;
        if (string.Equals(card, FolderViewIds.Api, StringComparison.OrdinalIgnoreCase) && tile.Api is { } request)
        {
            var host = hostFor(api.Manifest);
            return Guarded(config, request.HasSideEffects, (previous, ct) => api.LoadCardAsync(request, host, previous, ct));
        }

        if (string.Equals(card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase))
        {
            return target is not null && VfsPath.GetScheme(target) == "sftp"
                ? Guarded(config, needsTrust: true, (previous, ct) => ssh.LoadCardAsync(target, tile.Options, previous, ct))
                : Fixed(ErrorCard(Loc.T("Karta „ssh” potrzebuje „open” ze ścieżką sftp://.")));
        }

        // Reading a remote target signs in with saved logins or the user's keys: that waits for trust in the file,
        // and nothing connects while the card is only being resolved.
        if (target is not null && VfsPath.GetScheme(target) is "sftp" or "ftp")
        {
            return Guarded(config, needsTrust: true, async (previous, ct) =>
            {
                var inner = await ResolveTargetAsync(config, tile, card, target, ct).ConfigureAwait(false);
                return inner.Unavailable is { } why ? ErrorCard(why) : await inner.Load(previous, ct).ConfigureAwait(false);
            });
        }

        return await ResolveTargetAsync(config, tile, card, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Folder views, "card": true and plugin cards.</summary>
    private async Task<CardSource> ResolveTargetAsync(FolderConfig config, FolderTile tile, string card, string? target, CancellationToken cancellationToken)
    {
        if (FolderCardViews.Contains(card, StringComparer.OrdinalIgnoreCase))
        {
            return target is null
                ? Fixed(ErrorCard(Loc.T("Karta „{0}” potrzebuje „open” z folderem.", card)))
                : new CardSource((previous, ct) => FolderCardAsync(card, target, tile, previous, ct), false, null);
        }

        if (string.Equals(card, FolderTile.AutoCard, StringComparison.Ordinal))
        {
            return target is null
                ? Fixed(ErrorCard(Loc.T("Karta potrzebuje „open”: ścieżki, którą pokazuje.")))
                : await AutoAsync(config, tile, target, cancellationToken).ConfigureAwait(false);
        }

        return PluginCard(card, target, tile);
    }

    /// <summary>What Ctrl+Q would show: the target folder's own <c>c4k.dir</c> preview first, then the best preview plugin.</summary>
    private async Task<CardSource> AutoAsync(FolderConfig config, FolderTile tile, string target, CancellationToken cancellationToken)
    {
        var folderHost = hostFor(_folders.Manifest);
        var entry = await folderHost.GetEntryAsync(target, cancellationToken).ConfigureAwait(false);
        if (entry is not { } found)
        {
            return Fixed(ErrorCard(Loc.T("Nie znaleziono {0}.", target)));
        }

        if (found.IsContainer && VfsPath.GetScheme(target) is VfsPath.FileScheme or VfsPath.SmbScheme or "sftp" or "ftp")
        {
            var own = await readFolderConfig(target, cancellationToken).ConfigureAwait(false);
            if (own?.Preview is { IsPlainFiles: false, Kind: FolderActionKind.View } action)
            {
                if (action.Plugin is { } pluginId)
                {
                    return PluginCard(action.Preview is { } preview ? $"{pluginId}/{preview}" : pluginId, target, tile with { Options = tile.Options ?? action.Options });
                }

                if (action.View is FolderViewIds.Api && action.Api is { } request)
                {
                    // Trust is given to the dashboard's own file; a request that may change something, named by
                    // another folder's c4k.dir, is never sent from here.
                    if (request.HasSideEffects)
                    {
                        return new CardSource((_, _) => Task.FromResult(ErrorCard(string.Empty)), false,
                            Loc.T("Zapytanie {0} z c4k.dir innego folderu nie zostanie wysłane. Dodaj kartę API do tego dashboardu.", request.Method));
                    }

                    var host = hostFor(api.Manifest);
                    return new CardSource((previous, ct) => api.LoadCardAsync(request, host, previous, ct), false, null);
                }

                if (action.View is { } view && FolderCardViews.Contains(view, StringComparer.OrdinalIgnoreCase))
                {
                    return new CardSource((previous, ct) => FolderCards.LoadAsync(view, found, own, folderHost,
                        new PreviewCardRequest(found, tile.Options, previous), _folders.Sizes, ct), false, null);
                }
            }

            if (own?.Enter is { View: FolderViewIds.Dashboard })
            {
                return new CardSource((previous, ct) => FolderCards.LoadAsync(FolderViewIds.Dashboard, found, own, folderHost,
                    new PreviewCardRequest(found, tile.Options, previous), _folders.Sizes, ct), false, null);
            }
        }

        var best = previews.Where(p => p.CanPreview(found)).OrderByDescending(p => p.Priority).FirstOrDefault();
        return best is null ? Fixed(DefaultCard(tile, target)) : PreviewCard(best, found, tile, target);
    }

    private async Task<PreviewCard> FolderCardAsync(string view, string target, FolderTile tile, PreviewCard? previous, CancellationToken cancellationToken)
    {
        var host = hostFor(_folders.Manifest);
        if (await host.GetEntryAsync(target, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return ErrorCard(Loc.T("Nie znaleziono {0}.", target));
        }

        // The markdown and dashboard cards describe the target through its own c4k.dir (which file, which tiles).
        var own = view is FolderViewIds.Markdown or FolderViewIds.Dashboard ? await readFolderConfig(target, cancellationToken).ConfigureAwait(false) : null;
        return await FolderCards.LoadAsync(view, entry, own, host, new PreviewCardRequest(entry, tile.Options, previous), _folders.Sizes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A card named by plugin id (<c>plugin</c> or <c>plugin/preview</c>); a missing plugin offers to install it.</summary>
    private CardSource PluginCard(string name, string? target, FolderTile tile)
    {
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        var (pluginId, previewId) = slash < 0 ? (name, null) : (name[..slash], name[(slash + 1)..]);
        var plugin = previews
            .Where(p => string.Equals(p.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => previewId is not null && p.Id.EndsWith("/" + previewId, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (plugin is null)
        {
            var action = inCatalog(pluginId)
                ? new CardAction(Loc.T("Zainstaluj"), "c4k:install/" + pluginId)
                : new CardAction(Loc.T("Wtyczki"), "c4k:plugins");
            return Fixed(new PreviewCard(null, Loc.T("Brak wtyczki „{0}”.", pluginId), "🧩", CardStatus.Warning, [], [action]));
        }

        if (target is null)
        {
            return plugin is IPreviewCardPlugin cards
                ? new CardSource((previous, ct) => cards.LoadCardAsync(new PreviewCardRequest(null, tile.Options, previous), hostFor(plugin.Manifest), ct), false, null)
                : Fixed(DefaultCard(tile, null));
        }

        return new CardSource(async (previous, ct) =>
        {
            var entry = await hostFor(_folders.Manifest).GetEntryAsync(target, ct).ConfigureAwait(false);
            return entry is { } found
                ? await PreviewCard(plugin, found, tile, target).Load(previous, ct).ConfigureAwait(false)
                : ErrorCard(Loc.T("Nie znaleziono {0}.", target));
        }, false, null);
    }

    private CardSource PreviewCard(IPreviewPlugin plugin, VfsEntry entry, FolderTile tile, string target) =>
        plugin is IPreviewCardPlugin cards
            ? new CardSource((previous, ct) => cards.LoadCardAsync(new PreviewCardRequest(entry, tile.Options, previous), hostFor(plugin.Manifest), ct), false, null)
            : Fixed(DefaultCard(tile, target));

    /// <summary>POST and SSH cards: only from a <c>c4k.dir</c> on a disk or share, where the user can trust the file.</summary>
    private static CardSource Guarded(FolderConfig config, bool needsTrust, CardLoader load) =>
        new(load, needsTrust, needsTrust && VfsPath.GetScheme(config.Folder) is not (VfsPath.FileScheme or VfsPath.SmbScheme)
            ? Loc.T("Ta karta działa tylko z pliku c4k.dir na dysku albo w udziale sieciowym.")
            : null);

    private static CardSource Fixed(PreviewCard card) => new((_, _) => Task.FromResult(card), false, null);

    private static PreviewCard ErrorCard(string message) => new(null, message, "⚠", CardStatus.Error, [], []);

    /// <summary>A preview without a card form: the tile's own look with an Open button.</summary>
    private static PreviewCard DefaultCard(FolderTile tile, string? target) =>
        new(null, tile.Subtitle, tile.Icon, CardStatus.None, [], target is null ? [] : [new CardAction(Loc.T("Otwórz"), target)]);
}
