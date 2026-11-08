using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Previews;
using Commander4K.Plugins.BuiltIn.Remote;

namespace Commander4K.Plugins.BuiltIn.Dashboard;

/// <summary>Reads one card; <paramref name="previous"/> is the last card it returned, for charts and differences.</summary>
public delegate Task<PreviewCard> CardLoader(PreviewCard? previous, CancellationToken cancellationToken);

/// <summary>Runs a card's <c>c4k:plugin/&lt;action&gt;</c> button in the preview that drew the card.</summary>
public delegate Task CardActionHandler(string action, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken);

/// <summary>How a tile's card is read.</summary>
/// <param name="NeedsTrust">The card may change something or sign in somewhere (POST, SSH): it waits for the user's trust in the <c>.c4k</c>.</param>
/// <param name="Unavailable">Why such a card cannot run from where the <c>.c4k</c> lives; the loader is never called then.</param>
/// <param name="Invoke">The card's <c>c4k:plugin/…</c> actions, when an <see cref="IInteractivePreviewPlugin"/> draws it for an entry.</param>
public sealed record CardSource(CardLoader Load, bool NeedsTrust, string? Unavailable, CardActionHandler? Invoke = null);

/// <summary>
/// Decides which preview draws a dashboard tile as a card: a built-in view, the API or SSH card, a preview or panel
/// plugin (<see cref="IPanelCardPlugin"/>: every panel can be a widget), or — for <c>"card": true</c> — what Ctrl+Q
/// would show for the target, including the target folder's own <c>.c4k</c>.
/// </summary>
public sealed class CardResolver(
    IReadOnlyList<IPreviewPlugin> previews,
    IReadOnlyList<IPanelPlugin> panels,
    Func<PluginManifest, IPluginHost> hostFor,
    Func<string, CancellationToken, Task<C4kFolder?>> readFolder,
    Func<string, bool> inCatalog,
    ApiCards api,
    SshMonitor ssh)
{
    private static readonly string[] FolderCardViews = [FolderViewIds.Files, FolderViewIds.Gallery, FolderViewIds.Notes, FolderViewIds.Markdown, FolderViewIds.Dashboard];

    private readonly FolderPreviewPlugin _folders = previews.OfType<FolderPreviewPlugin>().FirstOrDefault() ?? new FolderPreviewPlugin();

    /// <returns><c>null</c> for a plain shortcut tile.</returns>
    public async Task<CardSource?> ResolveAsync(C4kItem tile, CancellationToken cancellationToken)
    {
        if (tile.Card is not { } card)
        {
            return null;
        }

        if (tile.Error is { } error)
        {
            return Fixed(ErrorCard(error));
        }

        var target = tile.Open is { } open ? tile.Resolve(open) : null;
        if (string.Equals(card, FolderViewIds.Api, StringComparison.OrdinalIgnoreCase) && tile.Api is { } request)
        {
            var host = hostFor(api.Manifest);
            return Guarded(tile, request.HasSideEffects, (previous, ct) => api.LoadCardAsync(request, host, previous, ct));
        }

        if (string.Equals(card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase))
        {
            return target is not null && VfsPath.GetScheme(target) == "sftp"
                ? Guarded(tile, needsTrust: true, (previous, ct) => ssh.LoadCardAsync(target, tile.Options, previous, ct, tile.Resolve))
                : Fixed(ErrorCard(Loc.T("Karta „ssh” potrzebuje „open” ze ścieżką sftp://.")));
        }

        // Reading a remote target signs in with saved logins or the user's keys: that waits for trust in the file,
        // and nothing connects while the card is only being resolved.
        if (target is not null && VfsPath.GetScheme(target) is "sftp" or "ftp")
        {
            return Guarded(tile, needsTrust: true, async (previous, ct) =>
            {
                var inner = await ResolveTargetAsync(tile, card, target, ct).ConfigureAwait(false);
                return inner.Unavailable is { } why ? ErrorCard(why) : await inner.Load(previous, ct).ConfigureAwait(false);
            });
        }

        return await ResolveTargetAsync(tile, card, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Folder views, "card": true and plugin cards.</summary>
    private async Task<CardSource> ResolveTargetAsync(C4kItem tile, string card, string? target, CancellationToken cancellationToken)
    {
        if (WidgetCards.IsWidget(card))
        {
            var host = hostFor(_folders.Manifest);
            return new CardSource((previous, ct) => WidgetCards.LoadAsync(card, tile, target, host, DateTimeOffset.Now, ct), false, null);
        }

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
                : await AutoAsync(tile, target, cancellationToken).ConfigureAwait(false);
        }

        return PluginCard(card, target, tile);
    }

    /// <summary>What Ctrl+Q would show: the target folder's own <c>.c4k</c> preview first, then the best preview plugin.</summary>
    private async Task<CardSource> AutoAsync(C4kItem tile, string target, CancellationToken cancellationToken)
    {
        var folderHost = hostFor(_folders.Manifest);
        var entry = await folderHost.GetEntryAsync(target, cancellationToken).ConfigureAwait(false);
        if (entry is not { } found)
        {
            return Fixed(ErrorCard(Loc.T("Nie znaleziono {0}.", target)));
        }

        if (found.IsContainer && VfsPath.GetScheme(target) is VfsPath.FileScheme or VfsPath.SmbScheme or "sftp" or "ftp")
        {
            var own = await readFolder(target, cancellationToken).ConfigureAwait(false);
            if (own?.Single is { IsPlainFiles: false } single)
            {
                if (single.Kind == FolderActionKind.Panel && single.PanelId is { } panelId)
                {
                    return PluginCard(panelId, null, tile with { Options = tile.Options ?? single.Options });
                }

                if (single.Kind == FolderActionKind.View)
                {
                    if (single.Plugin is { } pluginId)
                    {
                        return PluginCard(single.PreviewId is { } preview ? $"{pluginId}/{preview}" : pluginId, target, tile with { Options = tile.Options ?? single.Options });
                    }

                    if (single.View is FolderViewIds.Api && single.Api is { } request)
                    {
                        // Trust is given to the dashboard's own file; a request that may change something, named by
                        // another folder's .c4k, is never sent from here.
                        if (request.HasSideEffects)
                        {
                            return new CardSource((_, _) => Task.FromResult(ErrorCard(string.Empty)), false,
                                Loc.T("Zapytanie {0} z .c4k innego folderu nie zostanie wysłane. Dodaj kartę API do tego dashboardu.", request.Method));
                        }

                        var host = hostFor(api.Manifest);
                        return new CardSource((previous, ct) => api.LoadCardAsync(request, host, previous, ct), false, null);
                    }

                    if (single.View is { } view && FolderCardViews.Contains(view, StringComparer.OrdinalIgnoreCase))
                    {
                        return new CardSource((previous, ct) => FolderCards.LoadAsync(view, found, own, folderHost,
                            new PreviewCardRequest(found, tile.Options, previous), _folders.Sizes, ct), false, null);
                    }
                }
            }

            if (own is { Layout: C4kLayout.Dashboard })
            {
                return new CardSource((previous, ct) => FolderCards.LoadAsync(FolderViewIds.Dashboard, found, own, folderHost,
                    new PreviewCardRequest(found, tile.Options, previous), _folders.Sizes, ct), false, null);
            }
        }

        var best = PreviewPluginResolver.Resolve(previews, found);
        return best is null ? Fixed(DefaultCard(tile, target)) : PreviewCard(best, found, tile, target);
    }

    private async Task<PreviewCard> FolderCardAsync(string view, string target, C4kItem tile, PreviewCard? previous, CancellationToken cancellationToken)
    {
        var host = hostFor(_folders.Manifest);
        if (await host.GetEntryAsync(target, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return ErrorCard(Loc.T("Nie znaleziono {0}.", target));
        }

        // The markdown and dashboard cards describe the target through its own .c4k (which file, which tiles).
        var own = view is FolderViewIds.Markdown or FolderViewIds.Dashboard ? await readFolder(target, cancellationToken).ConfigureAwait(false) : null;
        return await FolderCards.LoadAsync(view, entry, own, host, new PreviewCardRequest(entry, tile.Options, previous), _folders.Sizes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A card named by plugin id (<c>plugin</c> or <c>plugin/preview</c>); a missing plugin offers to install it.</summary>
    private CardSource PluginCard(string name, string? target, C4kItem tile)
    {
        // A panel named exactly (builtin.sysinfo, <plugin>/<panel>) wins over a preview of the same plugin.
        if (panels.FirstOrDefault(p => string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase)) is { } panel)
        {
            return PanelCard(panel, target, tile);
        }

        var slash = name.IndexOf('/', StringComparison.Ordinal);
        var (pluginId, previewId) = slash < 0 ? (name, null) : (name[..slash], name[(slash + 1)..]);
        var plugin = previews
            .Where(p => string.Equals(p.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => previewId is not null && p.Id.EndsWith("/" + previewId, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (plugin is null && slash < 0 && panels.FirstOrDefault(p => p is IPanelCardPlugin && string.Equals(p.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase)) is { } pluginPanel)
        {
            return PanelCard(pluginPanel, target, tile);
        }

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
        }, false, null, Actions(plugin, ct => hostFor(_folders.Manifest).GetEntryAsync(target, ct), tile));
    }

    /// <summary>A panel's widget; a panel without one gets its description and a button that opens it.</summary>
    private CardSource PanelCard(IPanelPlugin panel, string? target, C4kItem tile)
    {
        if (panel is not IPanelCardPlugin cards)
        {
            return Fixed(new PreviewCard(null, tile.Description ?? panel.Description, tile.Icon ?? "🧩", CardStatus.None, [],
                [new CardAction(Loc.T("Otwórz"), CardActions.OpenPanel(panel.Id))]));
        }

        return new CardSource(async (previous, ct) =>
        {
            var entry = target is null ? null : await hostFor(_folders.Manifest).GetEntryAsync(target, ct).ConfigureAwait(false);
            return await cards.LoadCardAsync(new PreviewCardRequest(entry, tile.Options, previous), hostFor(panel.Manifest), ct).ConfigureAwait(false);
        }, false, null);
    }

    private CardSource PreviewCard(IPreviewPlugin plugin, VfsEntry entry, C4kItem tile, string target) =>
        plugin is IPreviewCardPlugin cards
            ? new CardSource((previous, ct) => cards.LoadCardAsync(new PreviewCardRequest(entry, tile.Options, previous), hostFor(plugin.Manifest), ct), false, null,
                Actions(plugin, _ => Task.FromResult<VfsEntry?>(entry), tile))
            : Fixed(DefaultCard(tile, target));

    /// <summary>A card button <c>c4k:plugin/…</c> goes to the preview's <c>preview.action</c> with the card's entry.</summary>
    private CardActionHandler? Actions(IPreviewPlugin plugin, Func<CancellationToken, Task<VfsEntry?>> entry, C4kItem tile) =>
        plugin is IInteractivePreviewPlugin interactive
            ? async (action, arguments, ct) =>
            {
                if (await entry(ct).ConfigureAwait(false) is { } found)
                {
                    await interactive.InvokeActionAsync(new PreviewActionRequest(found, tile.Options, action, arguments, null), hostFor(plugin.Manifest), ct).ConfigureAwait(false);
                }
            }
            : null;

    /// <summary>POST and SSH cards: only from a <c>.c4k</c> on a disk or share, where the user can trust the file (<see cref="C4kItem.NeedsTrust"/>).</summary>
    private static CardSource Guarded(C4kItem item, bool needsTrust, CardLoader load) =>
        new(load, needsTrust, needsTrust && VfsPath.GetScheme(item.Folder) is not (VfsPath.FileScheme or VfsPath.SmbScheme)
            ? Loc.T("Ta karta działa tylko z pliku .c4k na dysku albo w udziale sieciowym.")
            : null);

    private static CardSource Fixed(PreviewCard card) => new((_, _) => Task.FromResult(card), false, null);

    private static PreviewCard ErrorCard(string message) => new(null, message, "⚠", CardStatus.Error, [], []);

    /// <summary>A preview without a card form: the tile's own look with an Open button.</summary>
    private static PreviewCard DefaultCard(C4kItem tile, string? target) =>
        new(null, tile.Description, tile.Icon, CardStatus.None, [], target is null ? [] : [new CardAction(Loc.T("Otwórz"), target)]);
}
