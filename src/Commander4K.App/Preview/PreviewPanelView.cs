using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Commander4K.App.Composition;
using Commander4K.App.Browser;
using Commander4K.App.Connections;
using Commander4K.App.Input;
using Commander4K.App.Panels;
using Commander4K.App.Theming;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Previews;
using Commander4K.Plugins.BuiltIn.Viewers;

namespace Commander4K.App.Preview;

/// <summary>
/// Quick view of the entry under the cursor in the source panel. The best <see cref="IPreviewPlugin"/> renders it; a
/// new cursor position or folder cancels the running load at once. Markdown and text can be edited (F4) and saved
/// (Ctrl+S) through the plugin's host, which checks fs.write and audits the write.
/// </summary>
public sealed class PreviewPanelView : Grid, IPanelModeView
{
    private const int MaxFolderRows = 300;
    private static readonly TimeSpan PreviewProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly AppHost _app;
    private readonly FilePanelView _source;
    private readonly Action<string, bool> _notify;
    private readonly object _renderGate = new();
    private PanelTab? _watchedTab;
    private CancellationTokenSource? _debounce;
    private bool _renderScheduled;
    private readonly Border _header;
    private readonly TextBlock _title;
    private readonly Button _editButton;
    private readonly Button _saveButton;
    private readonly ContentControl _content = new();
    private readonly TextBlock _status;
    private readonly WaveDots _busy = new() { IsVisible = false, Margin = new Thickness(8, 0, 0, 0) };
    private SkinBrushes _brushes;
    private string? _loadedKey;
    private CancellationTokenSource? _loading;
    private PreviewContent? _shown;
    private (CancellationTokenSource Owner, PreviewContent Content)? _pendingRender;
    private VfsEntry? _entry;
    private IPreviewPlugin? _plugin;
    private bool _editing;
    private bool _dirty;
    private bool _escapeArmed;
    private string _savedText = string.Empty;
    private TextBox? _editor;
    private WebPreviewView? _web;
    private WebPreview? _webShown;
    private BrowserPanelView? _browserPreview;
    private ScrollViewer? _livePreview;
    private DispatcherTimer? _liveDelay;

    /// <summary>A folder's own view shown on entering it (<c>c4k.dir</c> <c>"enter"</c>), instead of following the cursor.</summary>
    public sealed record PinnedFolder(VfsEntry Folder, FolderConfig Config, FolderAction Action);

    /// <summary>Folder views opened one from another (a dashboard picked on a dashboard); empty when following the cursor.</summary>
    private readonly ViewHistory<PinnedFolder> _history = new();
    private readonly Button _backButton;

    /// <summary>"📄 Pliki (Esc)" (or "Zamknij"): back to the file list; the spotlight after a suggested view points here.</summary>
    public Button FilesButton { get; }
    private readonly StackPanel _trail = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, Bitmap?> _thumbnails = new(StringComparer.Ordinal);
    private const int SmallPictureBytes = 256 * 1024;

    private PinnedFolder? _pinned => _history.Trail.Count > 0 ? _history.Current : null;

    /// <summary>Form edits reach the plugin this long after the last keystroke (and always before a button runs).</summary>
    private const int FormDebounceMs = 800;

    /// <summary>A file opened with Enter (its preview has <c>"enter": true</c>): shown pinned, Esc returns to the files.</summary>
    private readonly VfsEntry? _pinnedFile;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>The <c>c4k.dir</c> options the current plugin was loaded with; passed to its actions too.</summary>
    private string? _options;

    /// <summary>Who runs a content's <c>c4k:plugin/…</c> buttons: fixed when the content is rendered, never read from the view's current state.</summary>
    private sealed record ActionReceiver(IInteractivePreviewPlugin Plugin, VfsEntry Entry, string? Options);

    /// <param name="Shortcut">The usable shortcut (<see cref="PreviewShortcuts.Normalize"/>), or <c>null</c>.</param>
    private sealed record BoundAction(PreviewAction Action, ActionReceiver? Receiver, string? Shortcut);

    /// <summary>The receiver of the content being rendered (<see cref="Show"/>).</summary>
    private ActionReceiver? _receiver;

    /// <summary>Buttons of the content on screen and their shortcuts; both are switched off as soon as another load starts.</summary>
    private readonly List<BoundAction> _shortcuts = [];
    private readonly List<Button> _actionButtons = [];
    private bool _acting;

    /// <summary>The form on screen, its current values and who hears about edits (fixed when the form appears).</summary>
    private FormPreview? _form;
    private Dictionary<string, string>? _formValues;
    private ActionReceiver? _formOwner;
    private DispatcherTimer? _formTimer;
    private bool _formPending;

    /// <param name="pinnedFile">A file opened with Enter, shown in <paramref name="source"/> itself instead of following the cursor.</param>
    public PreviewPanelView(AppHost app, FilePanelView source, SkinBrushes brushes, Action<string, bool> notify, PinnedFolder? pinned = null, VfsEntry? pinnedFile = null)
    {
        _pinnedFile = pinned is null ? pinnedFile : null;
        if (pinned is not null)
        {
            _history.Push(pinned);
        }

        _app = app;
        _source = source;
        _brushes = brushes;
        _notify = notify;

        _title = new TextBlock().FontWeight(FontWeight.SemiBold).VerticalAlignment(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis);
        _editButton = new Button().Content(Loc.T("Edytuj (F4)")).Padding(8, 2).FontSize(12).IsVisible(false).OnClick(_ => ToggleEdit());
        _saveButton = new Button().Content(Loc.T("Zapisz (Ctrl+S)")).Padding(8, 2).FontSize(12).Margin(4, 0, 0, 0).IsVisible(false).OnClick(async _ => await SaveAsync());
        // A folder's own view always offers the way back (←, Alt+←, Backspace) and the way to its files (Esc).
        _backButton = new Button().Content("←").Padding(8, 2).FontSize(12).Margin(0, 0, 6, 0).IsVisible(pinned is not null).OnClick(async _ => await BackAsync());
        ToolTip.SetTip(_backButton, Loc.T("Wstecz (Alt+←, Backspace)"));
        _header = new Border().Padding(8, 4).Child(new Grid().Cols("Auto,*,Auto,Auto,Auto").Children(
            _backButton,
            _title.Col(1).IsVisible(pinned is null),
            _trail.Col(1).IsVisible(pinned is not null),
            _editButton.Col(2),
            _saveButton.Col(3),
            FilesButton = new Button().Col(4).Content(pinned is null && _pinnedFile is null ? Loc.T("Zamknij (Esc)") : Loc.T("📄 Pliki (Esc)")).Padding(8, 2).FontSize(12).Margin(4, 0, 0, 0).OnClick(_ => CloseRequested?.Invoke())));
        _status = new TextBlock().Margin(8, 2).TextTrimming(TextTrimming.CharacterEllipsis);

        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        Children.Add(_header);
        Children.Add(_content.Row(1));
        Children.Add(new Grid().Row(2).Cols("Auto,*").Children(_busy, _status.Col(1)));
        PointerPressed += (_, _) => Activated?.Invoke();

        ApplySkin(brushes);
        if (pinned is not null)
        {
            RefreshTrail();
            _ = LoadAsync(pinned.Folder);
            return;
        }

        if (_pinnedFile is { } file)
        {
            _ = LoadAsync(file);
            return;
        }

        // Event-driven: the source tab reports cursor moves, navigation and loading; switching tabs rebinds.
        _source.ActiveTabChanged += Rebind;
        Rebind();
    }

    public event Action? CloseRequested;

    public event Action? Activated;

    /// <summary>Opens the catalog and AI prompt options for an entry no preview recognises.</summary>
    public Func<VfsEntry, Task>? ShowUnknownPreviewOptions { get; init; }

    /// <summary>Installs a matching plugin from the official catalog through the normal permission flow.</summary>
    public Func<string, Task>? InstallCatalogPreview { get; init; }

    /// <summary>Opens the official plugin catalog.</summary>
    public Func<Task>? OpenPluginCatalog { get; init; }

    /// <summary>Configures a folder that does not yet have a <c>c4k.dir</c>.</summary>
    public Func<string, Task>? ConfigureFolderView { get; init; }

    /// <summary>A tile was clicked; the folder is the one whose preview (or <c>c4k.dir</c>) produced it.</summary>
    public Func<PreviewTile, VfsEntry?, Task>? OpenTile { get; init; }

    public Func<Plugins.BuiltIn.Dashboard.DashboardPreview, int, DashboardTileEdit, Task>? EditDashboardTile { get; init; }

    public Func<string, BrowserPanelView>? CreateBrowserPreview { get; init; }

    public Func<string, Task>? ConfigureSftpConnection { get; init; }

    /// <summary>App shortcuts pressed inside a web preview page (Avalonia never sees those keys); Esc closes the preview first.</summary>
    public Func<Core.Input.KeyStroke, bool>? RunShortcut { get; init; }
    /// <summary>Asks the user to trust a <c>c4k.dir</c> whose cards sign in or change something (POST, SSH); locked when null.</summary>
    public Func<FolderConfig, Task<bool>>? AskFolderTrust { get; init; }

    public string Title => _pinned is { } pinned ? pinned.Config.Title ?? pinned.Folder.Name : _pinnedFile?.Name ?? Loc.T("Podgląd");

    public Control View => this;

    /// <summary>Shows a folder's own view (entered through its <c>c4k.dir</c>) or a file opened with Enter, rather than following the cursor.</summary>
    public bool IsPinned => _pinned is not null || _pinnedFile is not null;

    /// <summary>A folder's own view, where another folder's view can open in place (<see cref="OpenPinnedAsync"/>).</summary>
    public bool IsPinnedFolder => _pinned is not null;

    /// <summary>The folder currently shown by this preview, if it can hold a dashboard.</summary>
    public VfsEntry? DashboardFolder => _pinned?.Folder ??
        (_entry is { Kind: VfsEntryKind.Directory or VfsEntryKind.Drive or VfsEntryKind.Share } entry ? entry : null);

    public async Task ReloadCurrentAsync()
    {
        if (_pinned is { } pinned)
        {
            var config = await Task.Run(() => _app.ReadFolderConfigAsync(pinned.Folder.Path, CancellationToken.None));
            if (config is { Error: null } && (config.Preview ?? config.Enter) is { } action)
            {
                _history.ReplaceCurrent(pinned with { Config = config, Action = action });
            }

            await LoadAsync(_pinned.Folder);
            return;
        }

        await LoadAsync(_entry);
    }

    public bool IsActivePanel
    {
        set { }
    }

    /// <summary>Opens another folder's view inside this one (a dashboard card); Back returns here.</summary>
    public async Task OpenPinnedAsync(PinnedFolder next)
    {
        if (_dirty || _pinned is null)
        {
            return;
        }

        _history.Push(next);
        await ShowPinnedAsync(next);
    }

    private async Task BackAsync()
    {
        if (!_dirty && _history.CanGoBack)
        {
            await ShowPinnedAsync(_history.Back());
        }
    }

    private async Task JumpToAsync(int index)
    {
        if (!_dirty && index < _history.Trail.Count - 1)
        {
            await ShowPinnedAsync(_history.JumpTo(index));
        }
    }

    /// <summary>The file panel underneath follows, so Esc shows the files of the folder on screen.</summary>
    private async Task ShowPinnedAsync(PinnedFolder view)
    {
        RefreshTrail();
        _ = _source.NavigateAsync(view.Folder.Path);
        await LoadAsync(view.Folder);
    }

    /// <summary>Breadcrumbs of the views opened so far; a click goes back to that one.</summary>
    private void RefreshTrail()
    {
        _trail.Children.Clear();
        var trail = _history.Trail;
        for (var i = 0; i < trail.Count; i++)
        {
            var name = trail[i].Config.Title ?? trail[i].Folder.Name;
            if (i > 0)
            {
                _trail.Children.Add(new TextBlock { Text = " › ", Foreground = _brushes.MutedForeground, VerticalAlignment = VerticalAlignment.Center });
            }

            if (i == trail.Count - 1)
            {
                _trail.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = _brushes.HeaderForeground, VerticalAlignment = VerticalAlignment.Center });
                continue;
            }

            var index = i;
            var crumb = new Button { Content = name, Padding = new Thickness(4, 0), FontSize = 12, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = _brushes.HeaderForeground };
            crumb.Click += async (_, _) => await JumpToAsync(index);
            _trail.Children.Add(crumb);
        }

        _backButton.IsEnabled = _history.CanGoBack;
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        Background = brushes.PanelBackground;
        _header.Background = brushes.HeaderBackground;
        _title.Foreground = brushes.HeaderForeground;
        _status.Foreground = brushes.StatusForeground;
        _busy.Foreground = brushes.StatusForeground;
        if (_history.Trail.Count > 0)
        {
            RefreshTrail();
        }
        if (_shown is { } shown)
        {
            Show(shown);
        }
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (_browserPreview?.HandleKey(e) == true)
        {
            return true;
        }

        if (e.Key == Key.F4 && CanEdit)
        {
            ToggleEdit();
            return true;
        }

        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _dirty)
        {
            _ = SaveAsync();
            return true;
        }

        // The window passes keys here only while this panel is active; inside a text field only Ctrl/Alt combos,
        // F-keys and Esc arrive, so a plain-letter shortcut never fires while the user types.
        // Reserved keys (Esc, Enter, Tab, Backspace, Alt+←/→) never reach a plugin, and while a field is being edited
        // its own chords (Ctrl+C/V/Z, Ctrl+arrows…) stay with the field.
        if (_shortcuts.Count > 0 && KeyMapping.ToStroke(e) is { } stroke && !PreviewShortcuts.IsReserved(stroke)
            && !(FocusedTextBox() is { IsReadOnly: false } && PreviewShortcuts.IsTextEditing(stroke)))
        {
            var pressed = stroke.ToString();
            if (_shortcuts.FirstOrDefault(a => a.Shortcut == pressed) is { } action)
            {
                _ = RunActionAsync(action);
                return true;
            }
        }

        var typing = FocusedTextBox() is { IsReadOnly: false };
        var back = (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt) || (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && !typing);
        if (back && !_dirty && _history.CanGoBack)
        {
            _ = BackAsync();
            return true;
        }

        if (e.Key == Key.Escape || (e.Key == Key.Back && !typing))
        {
            if (_dirty && !_escapeArmed)
            {
                _escapeArmed = true;
                _notify(Loc.T("Niezapisane zmiany: Ctrl+S zapisuje, ponowne Esc je odrzuca."), false);
                return true;
            }

            CloseRequested?.Invoke();
            return true;
        }

        return false;
    }

    public void AppendText(string text)
    {
        if (_browserPreview is { } browser)
        {
            browser.AppendText(text);
            return;
        }

        // Typing goes to the editor when editing; otherwise the preview ignores it.
        if (_editor is { IsReadOnly: false } editor)
        {
            editor.Focus();
        }
    }

    public ValueTask DisposeModeAsync()
    {
        _loading?.Cancel();
        _debounce?.Cancel();
        DisposeWeb();
        DisposeBrowserPreview();
        StopDashboard();
        _ = FlushFormAsync(); // the last edit still reaches the plugin (its own time limit, not the view's)
        _lifetime.Cancel();
        if (IsPinned)
        {
            return ValueTask.CompletedTask;
        }

        _source.ActiveTabChanged -= Rebind;
        if (_watchedTab is not null)
        {
            _watchedTab.Changed -= OnSourceChanged;
        }

        return ValueTask.CompletedTask;
    }

    private bool CanEdit => Inner(_shown) is MarkdownPreview { CanEdit: true } or TextPreview { CanEdit: true };

    /// <summary>The content under any action bars.</summary>
    private static PreviewContent? Inner(PreviewContent? content) => content switch { ActionsPreview actions => Inner(actions.Content), MovedPreview moved => Inner(moved.Content), _ => content };

    /// <summary>Changes the content under the action bars and keeps the bars.</summary>
    private static PreviewContent MapInner(PreviewContent content, Func<PreviewContent, PreviewContent> change) =>
        content switch
        {
            ActionsPreview actions => actions with { Content = MapInner(actions.Content, change) },
            MovedPreview moved => moved with { Content = MapInner(moved.Content, change) },
            _ => change(content),
        };

    private TextBox? FocusedTextBox() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box && this.IsVisualAncestorOf(box) ? box : null;

    private void Rebind()
    {
        if (_watchedTab is not null)
        {
            _watchedTab.Changed -= OnSourceChanged;
        }

        _watchedTab = _source.ActiveTab;
        _watchedTab.Changed += OnSourceChanged;
        OnSourceChanged();
    }

    private void OnSourceChanged()
    {
        if (_dirty)
        {
            return; // Never throw away edits because the cursor moved.
        }

        var tab = _source.ActiveTab;
        var entry = tab.CurrentEntry;
        var key = $"{tab.Path}|{entry?.Path}";
        // A folder still loading (a slow share, net:// discovering hosts) does not stop the preview: the entry under
        // the cursor is already known, and the key keeps new batches from reloading it.
        if (key == _loadedKey || (tab.IsLoading && entry is null or { Kind: VfsEntryKind.ParentLink }))
        {
            return;
        }

        // The previous target is no longer interesting: stop it now, start the new one once the cursor rests
        // briefly, so scrolling quickly through a list does not start a load per row.
        _loading?.Cancel();
        _debounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _debounce = debounce;
        _ = LoadAfterPauseAsync(key, entry, tab, debounce.Token);
    }

    private async Task LoadAfterPauseAsync(string key, VfsEntry? entry, PanelTab tab, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _loadedKey = key;
        var target = entry is { Kind: VfsEntryKind.ParentLink }
            ? new VfsEntry(tab.Title, tab.Path, VfsEntryKind.Directory)
            : entry is null && !tab.IsLoading && tab.Error is null
                && tab.Entries.All(item => item.Kind is VfsEntryKind.ParentLink or VfsEntryKind.Action)
                    ? new VfsEntry(tab.Title, tab.Path, VfsEntryKind.Directory)
            : entry;
        await LoadAsync(target);
    }

    /// <summary>Progress is coalesced so rebuilding Avalonia controls happens at most four times per second.</summary>
    private async Task RenderSoonAsync()
    {
        await Task.Delay(PreviewProgressInterval);
        (CancellationTokenSource Owner, PreviewContent Content)? pending;
        lock (_renderGate)
        {
            pending = _pendingRender;
            _pendingRender = null;
            _renderScheduled = false;
        }

        if (pending is { } update && _loading == update.Owner && !update.Owner.IsCancellationRequested)
        {
            Show(update.Content);
        }
    }

    private void QueuePreviewProgress(CancellationTokenSource owner, PreviewContent content)
    {
        lock (_renderGate)
        {
            if (_loading != owner || owner.IsCancellationRequested)
            {
                return;
            }

            _pendingRender = (owner, content);
            if (_renderScheduled)
            {
                return;
            }

            _renderScheduled = true;
        }

        Dispatcher.UIThread.Post(() => _ = RenderSoonAsync());
    }

    private void ClearPendingPreviewProgress()
    {
        lock (_renderGate)
        {
            _pendingRender = null;
        }
    }

    private async Task LoadAsync(VfsEntry? entry)
    {
        _loading?.Cancel();
        StopDashboard();
        _ = FlushFormAsync(); // a draft typed into the previous form is not lost by moving on
        DisableActions(); // the old buttons stay on screen until the new content arrives, but no longer act
        _form = null;
        _options = null;
        _editing = false;
        if (_entry?.Path != entry?.Path)
        {
            _thumbnails.Clear(); // Pictures of the previous folder's tiles.
        }

        _entry = entry;
        if (entry is not { } target)
        {
            Show(new MessagePreview(Loc.T("Brak elementu pod kursorem.")));
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_renderGate)
        {
            _loading = cts;
            _pendingRender = null;
        }

        _title.Text = target.Name;

        // A folder may say how it wants to be shown (c4k.dir); the file is read only for folders on the way here.
        Func<IPluginHost, IProgress<PreviewContent>, CancellationToken, Task<PreviewContent>>? folderView = null;
        IPreviewPlugin? plugin = null;
        if (target.IsContainer && target.Kind is not (VfsEntryKind.Host or VfsEntryKind.ParentLink))
        {
            FolderConfig? config;
            FolderAction? action;
            string section;
            if (_pinned is { } pinned)
            {
                (config, action, section) = (pinned.Config, pinned.Action, "enter");
            }
            else
            {
                try
                {
                    config = await Task.Run(() => _app.ReadFolderConfigAsync(target.Path, cts.Token), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                (action, section) = (config?.Preview, "preview");
            }

            if (_loading != cts)
            {
                return;
            }

            if (config?.Error is { } error)
            {
                Show(new MessagePreview(Loc.T("⚠ {0}: {1}", config.FilePath, error)));
                return;
            }

            if (config is not null && action is { IsPlainFiles: false })
            {
                (plugin, folderView) = ResolveFolderView(config, action, section, target);
                _title.Text = config.Title ?? target.Name;
            }
        }

        plugin ??= PreviewPluginResolver.Resolve(_app.PreviewPlugins, target);
        _plugin = plugin;
        if (plugin is null)
        {
            Show(new MessagePreview(Loc.T("Brak podglądu dla „{0}”.", target.Name)));
            return;
        }

        SetStatus($"{plugin.DisplayName}: wczytywanie…", busy: true);
        var host = _app.HostFor(plugin.Manifest);
        var progress = new CallbackProgress<PreviewContent>(partial => QueuePreviewProgress(cts, partial));
        try
        {
            // External previews hear whether this view stays on the entry (Enter) or follows the cursor (Ctrl+Q).
            var pinned = IsPinned;
            var result = await Task.Run(() => folderView is not null
                ? folderView(host, progress, cts.Token)
                : plugin is ExternalPreviewPlugin external
                    ? external.LoadWithOptionsAsync(target, host, null, pinned, cts.Token)
                    : plugin.LoadAsync(target, host, progress, cts.Token), cts.Token);
            result = ViewOnlyWhereReadOnly(target, result);

            if (_loading == cts)
            {
                ClearPendingPreviewProgress();
                // "Edit right away" applies once per loaded file; F4 still switches back to the formatted view.
                _editing = Inner(result) is MarkdownPreview { CanEdit: true } && _app.Settings.MarkdownEditByDefault;
                Show(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_loading == cts)
            {
                ClearPendingPreviewProgress();
                Show(new MessagePreview($"⚠ {ex.Message}"));
            }
        }
    }

    /// <summary>Files on read-only locations (inside archives, net://) cannot be saved back, so they are view-only.</summary>
    private PreviewContent ViewOnlyWhereReadOnly(VfsEntry target, PreviewContent result) =>
        _app.Router.TryResolve(target.Path, out var provider) && (provider.Capabilities & VfsCapabilities.Write) != 0
            ? result
            : MapInner(result, inner => inner switch
            {
                MarkdownPreview markdown => markdown with { CanEdit = false },
                TextPreview text => text with { CanEdit = false },
                _ => inner,
            });

    /// <summary>
    /// What renders a <c>c4k.dir</c> action: a plugin's preview (with the file's options) or a built-in view. A plugin
    /// that is not installed, or not enabled yet, becomes a tile that installs or enables it.
    /// </summary>
    private (IPreviewPlugin Plugin, Func<IPluginHost, IProgress<PreviewContent>, CancellationToken, Task<PreviewContent>> Load) ResolveFolderView(
        FolderConfig config, FolderAction action, string section, VfsEntry folder)
    {
        var folderPlugin = _app.BuiltInPreviews.OfType<Plugins.BuiltIn.Previews.FolderPreviewPlugin>().First();
        if (action.Kind != FolderActionKind.View)
        {
            var what = action.Kind switch
            {
                FolderActionKind.Panel => Loc.T("panel „{0}”", action.PanelId ?? string.Empty),
                FolderActionKind.Open => action.Open ?? string.Empty,
                FolderActionKind.Url => action.Url ?? string.Empty,
                _ => action.Run?.ToString() ?? string.Empty,
            };
            return (folderPlugin, (_, _, _) => Task.FromResult<PreviewContent>(new TilesPreview(config.Title ?? folder.Name,
                [new PreviewTile(Loc.T("Otwórz"), what, "↪", null, folder.Path)],
                Loc.T("Ten podgląd z c4k.dir otwiera się po wejściu do folderu (Enter)."))));
        }

        if (action.Plugin is null && action.View is FolderViewIds.Api && action.Api is { } request)
        {
            var api = _app.ApiCards;
            return (folderPlugin, (_, _, ct) => api.LoadViewAsync(request, _app.HostFor(api.Manifest), ct));
        }

        if (action.Plugin is not { } pluginId)
        {
            return (folderPlugin, (host, progress, ct) => Plugins.BuiltIn.Previews.FolderViews.LoadAsync(config, action, section, folder, host, folderPlugin.Thumbnails, progress, ct));
        }

        var external = _app.PreviewPlugins.OfType<ExternalPreviewPlugin>()
            .Where(p => string.Equals(p.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => action.Preview is { } id && p.Id.EndsWith("/" + id, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (external is not null)
        {
            _options = action.Options;
            var pinned = section == "enter";
            return (external, (host, _, ct) => external.LoadWithOptionsAsync(folder, host, action.Options, pinned, ct));
        }

        var installed = _app.DiscoveredPlugins.FirstOrDefault(p => string.Equals(p.Manifest?.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        var entry = _app.PluginCatalog.Find(pluginId);
        PreviewTile tile = installed is not null
            ? new(Loc.T("Włącz wtyczkę „{0}”", installed.Manifest!.Name), _app.PluginBlocker(installed) ?? Loc.T("Wtyczka nie ma podglądu „{0}”.", action.Preview ?? string.Empty), "🧩", null, "c4k:plugins")
            : entry is not null
                ? new(Loc.T("Zainstaluj „{0}”", entry.Name), entry.LocalizedDescription, "⬇", null, "c4k:install/" + entry.Id)
                : new(Loc.T("Nieznana wtyczka „{0}”", pluginId), Loc.T("Nie ma jej w katalogu oficjalnych wtyczek."), "❓", null, "c4k:plugins");
        return (folderPlugin, (_, _, _) => Task.FromResult<PreviewContent>(new TilesPreview(config.Title ?? folder.Name, [tile],
            Loc.T("Ten folder chce być pokazany przez wtyczkę „{0}” (c4k.dir).", pluginId), TileWidth: 340)));
    }

    private void Show(PreviewContent content)
    {
        // The plugin renamed the entry (a message marked read) or switched to another file (a draft): follow it.
        while (content is MovedPreview moved)
        {
            if (_entry is { } current && PreviewMoves.IsAcceptable(current.Path, moved.Entry.Path))
            {
                _entry = moved.Entry;
            }
            content = moved.Content;
        }

        // The same dashboard shown again (a skin change) keeps its live cards; anything else stops them.
        if (_dashboard is { } live)
        {
            if (ReferenceEquals(_shown, content))
            {
                live.ApplySkin(_brushes);
                return;
            }

            StopDashboard();
        }

        // The same content drawn again (a skin change) keeps its receiver even while another load is under way.
        if (!ReferenceEquals(_shown, content))
        {
            _receiver = _plugin is IInteractivePreviewPlugin interactive && _entry is { } entry ? new ActionReceiver(interactive, entry, _options) : null;
        }

        _shown = content;
        _editor = null;
        _livePreview = null;
        DisableActions();
        var editable = CanEdit;
        _editButton.IsVisible = editable;
        _editButton.Content = _editing ? Loc.T("Podgląd (F4)") : Loc.T("Edytuj (F4)");
        _saveButton.IsVisible = _editing;
        SetStatus(_plugin is null ? string.Empty : $"{_plugin.DisplayName} · {_entry?.Path}");
        if (content is not WebPreview)
        {
            DisposeWeb(); // Another kind of preview: the page and its WebView2 go away.
        }

        if (content is not BrowserPagePreview)
        {
            DisposeBrowserPreview();
        }

        if (Inner(content) is not FormPreview)
        {
            _form = null;
        }

        var view = Render(content);

        // An entry nothing recognises (binary shown as hex, or no preview at all): offer official previews and AI prompts.
        if (_entry is { Kind: not (VfsEntryKind.ParentLink or VfsEntryKind.Action) } targetEntry
            && (content is HexPreview || (_plugin is null && content is MessagePreview))
            && (ShowUnknownPreviewOptions is not null || InstallCatalogPreview is not null) && view is not null)
        {
            var dock = new DockPanel();
            var banner = UnknownFileBanner(targetEntry);
            DockPanel.SetDock(banner, Dock.Top);
            dock.Children.Add(banner);
            dock.Children.Add(view);
            view = dock;
        }

        _content.Content = view;
    }

    private Control? Render(PreviewContent content) => content switch
    {
        BrowserPagePreview browser => BrowserView(browser),
        SftpConnectionPreview sftp => SftpConnectionView(sftp),
        WebPreview web => WebView(web),
        ActionsPreview actions => ActionsView(actions),
        FormPreview form => FormView(form),
        MarkdownPreview markdown when _editing => MarkdownEditor(markdown.Markdown),
        MarkdownPreview markdown => Scroll(MarkdownRenderer.Render(markdown.Markdown, _brushes)),
        TextPreview text => TextView(text),
        ImagePreview image => ImageView(image),
        HexPreview hex => Monospace(TextHexViewerPlugin.FormatHex(hex.Data.Span), Loc.T("Hex · pokazano {0:N0} z {1:N0} B", hex.Data.Length, hex.TotalLength)),
        FolderPreview folder => FolderView(folder),
        TilesPreview tiles => TilesView(tiles),
        Plugins.BuiltIn.Dashboard.DashboardPreview dashboard => DashboardView(dashboard),
        ChartPreview chart => ChartView(chart),
        MessagePreview message => new TextBlock { Text = message.Message, Foreground = _brushes.HiddenForeground, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap },
        _ => null,
    };

    private Control SftpConnectionView(SftpConnectionPreview preview)
    {
        var stack = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = Loc.T("Połączenie SFTP"),
            FontSize = _brushes.FontSize + 4,
            FontWeight = FontWeight.Bold,
            Foreground = _brushes.DirectoryForeground,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.T("Skonfiguruj użytkownika i sposób logowania. Przy pierwszym połączeniu aplikacja poprosi o sprawdzenie klucza hosta SSH."),
            Foreground = _brushes.MutedForeground,
            TextWrapping = TextWrapping.Wrap,
        });

        var configure = new Button
        {
            Content = Loc.T("Skonfiguruj"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6),
            IsEnabled = ConfigureSftpConnection is not null,
        };
        configure.Click += async (_, _) =>
        {
            if (ConfigureSftpConnection is { } action)
            {
                await action(preview.Path);
            }
        };
        stack.Children.Add(configure);
        return Scroll(stack);
    }

    private Control BrowserView(BrowserPagePreview page)
    {
        if (_browserPreview is null)
        {
            if (CreateBrowserPreview is not { } create)
            {
                return new TextBlock { Text = Loc.T("Brak widoku przeglądarki dla „{0}”.", page.Url), Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
            }

            _browserPreview = create(page.Url);
            _browserPreview.CloseRequested += () => CloseRequested?.Invoke();
            _browserPreview.Activated += () => Activated?.Invoke();
            _browserPreview.TitleChanged += () =>
            {
                if (_browserPreview is { } current && _shown is BrowserPagePreview)
                {
                    _title.Text = current.Title;
                }
            };
        }
        else if (!string.Equals(_browserPreview.CurrentUrl, page.Url, StringComparison.OrdinalIgnoreCase))
        {
            _browserPreview.Navigate(page.Url);
        }

        _browserPreview.ApplySkin(_brushes);
        return _browserPreview;
    }

    private void DisposeBrowserPreview()
    {
        if (_browserPreview is { } browser)
        {
            _browserPreview = null;
            _ = browser.DisposeModeAsync();
        }
    }

    /// <summary>A bar of the plugin's buttons above its content (Reply, Delete…); shortcuts work while this panel is active.</summary>
    private Control ActionsView(ActionsPreview actions)
    {
        var bar = new WrapPanel();
        foreach (var action in actions.Actions)
        {
            bar.Children.Add(ActionButton(action, primary: false));
        }

        var dock = new DockPanel();
        var border = new Border
        {
            Background = _brushes.Surface2,
            BorderBrush = _brushes.PanelBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6, 0, 6),
            Child = bar,
        };
        DockPanel.SetDock(border, Dock.Top);
        dock.Children.Add(border);
        if (Render(actions.Content) is { } inner)
        {
            dock.Children.Add(inner);
        }

        return dock;
    }

    /// <summary>A plugin button bound to the content's receiver: <c>c4k:plugin/…</c> goes back to that plugin, anything else opens like a tile.</summary>
    private Button ActionButton(PreviewAction action, bool primary)
    {
        var bound = new BoundAction(action, _receiver, PreviewShortcuts.Normalize(action.Shortcut));
        _shortcuts.Add(bound);
        var button = BannerButton(bound.Shortcut is not null ? Loc.T("{0} ({1})", action.Label, action.Shortcut!) : action.Label, () => RunActionAsync(bound), primary);
        button.Margin = new Thickness(0, 2, 8, 2);
        ToolTip.SetTip(button, action.Target);
        _actionButtons.Add(button);
        return button;
    }

    private void DisableActions()
    {
        _shortcuts.Clear();
        foreach (var button in _actionButtons)
        {
            button.IsEnabled = false;
        }

        _actionButtons.Clear();
    }

    /// <summary>Fields, attached files and buttons of a plugin form (composing a message…); edits reach the plugin after a pause.</summary>
    private Control FormView(FormPreview form)
    {
        // The same form shown again (a skin change) keeps what the user typed; a new one starts from its values.
        if (!ReferenceEquals(_form, form))
        {
            _form = form;
            _formValues = form.Fields.GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            _formOwner = _receiver;
            _formPending = false;
            _formTimer?.Stop();
        }

        var stack = new StackPanel { Margin = new Thickness(12, 8), Spacing = 8 };
        if (form.Title.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = form.Title, FontSize = _brushes.FontSize + 4, FontWeight = FontWeight.Bold, Foreground = _brushes.DirectoryForeground, TextWrapping = TextWrapping.Wrap });
        }

        if (form.Message is { Length: > 0 } message)
        {
            stack.Children.Add(new Border
            {
                Background = form.MessageIsError ? _brushes.ErrorBackground : _brushes.HighlightBackground,
                BorderBrush = form.MessageIsError ? _brushes.Error : _brushes.PanelBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(_brushes.ControlRadius),
                Padding = new Thickness(10, 6),
                Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = form.MessageIsError ? _brushes.Error : _brushes.FileForeground },
            });
        }

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        TextBox? first = null;
        foreach (var field in form.Fields)
        {
            var row = fields.RowDefinitions.Count;
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock
            {
                Text = field.Label,
                Foreground = _brushes.MutedForeground,
                VerticalAlignment = field.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = new Thickness(0, field.Multiline ? 6 : 0, 12, 6),
            };
            Grid.SetRow(label, row);
            fields.Children.Add(label);

            var id = field.Id;
            var box = new TextBox
            {
                Text = _formValues?.GetValueOrDefault(id) ?? field.Value,
                AcceptsReturn = field.Multiline,
                TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = field.Multiline ? 180 : 0,
                Margin = new Thickness(0, 0, 0, 6),
            };
            if (field.Password)
            {
                box.PasswordChar = '•';
            }

            box.TextChanged += (_, _) => FormEdited(id, box.Text ?? string.Empty);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            fields.Children.Add(box);
            first ??= box;
        }

        stack.Children.Add(fields);
        if (form.Files.Count > 0)
        {
            var files = new StackPanel { Spacing = 2 };
            files.Children.Add(new TextBlock { Text = Loc.P(form.Files.Count, "{0} załącznik|{0} załączniki|{0} załączników"), Foreground = _brushes.MutedForeground });
            foreach (var file in form.Files)
            {
                var line = new TextBlock
                {
                    Text = Loc.T("📎 {0} · {1}", file.Name, FileViewportCanvas.FormatSize(file.Size)),
                    Foreground = _brushes.FileForeground,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                ToolTip.SetTip(line, file.Path);
                files.Children.Add(line);
            }

            stack.Children.Add(files);
        }

        if (form.Buttons.Count > 0)
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            for (var i = 0; i < form.Buttons.Count; i++)
            {
                buttons.Children.Add(ActionButton(form.Buttons[i], primary: i == 0));
            }

            stack.Children.Add(buttons);
        }

        // Opened with Enter, the form is what the user came for: the first field takes the keyboard.
        if (IsPinned && first is not null)
        {
            Dispatcher.UIThread.Post(() => first.Focus(), DispatcherPriority.Loaded);
        }

        return Scroll(stack);
    }

    private void FormEdited(string id, string text)
    {
        // TextChanged also fires when the text is first assigned: only real changes count.
        if (_formValues is null || (_formValues.TryGetValue(id, out var old) && old == text))
        {
            return;
        }

        _formValues[id] = text;
        _formPending = true;
        if (_formTimer is null)
        {
            _formTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FormDebounceMs) };
            _formTimer.Tick += (_, _) => _ = FlushFormAsync();
        }

        _formTimer.Stop();
        _formTimer.Start();
    }

    /// <summary>Sends the form's values to the plugin now if an edit is still waiting for the pause.</summary>
    private async Task FlushFormAsync()
    {
        _formTimer?.Stop();
        if (!_formPending || _formOwner is not { } owner || _formValues is null)
        {
            _formPending = false;
            return;
        }

        _formPending = false;
        var values = new Dictionary<string, string>(_formValues, StringComparer.Ordinal);
        var host = _app.HostFor(owner.Plugin.Manifest);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await Task.Run(() => owner.Plugin.FormChangedAsync(owner.Entry, owner.Options, values, host, timeout.Token), timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notify(Loc.T("{0}: {1}", owner.Plugin.DisplayName, ex.Message), true);
        }
    }

    /// <summary>
    /// A button or its shortcut. <c>c4k:plugin/…</c> runs in the plugin the button was rendered for (after the last form
    /// edit reached it) and shows what it returns, or renders the entry again; other targets open like a tile. One action
    /// at a time; the busy status never outlives the action.
    /// </summary>
    private async Task RunActionAsync(BoundAction bound)
    {
        var action = bound.Action;
        if (!PreviewActionTargets.TryParse(action.Target, out var name, out var arguments))
        {
            if (OpenTile is { } open)
            {
                await open(new PreviewTile(action.Label, Target: action.Target), bound.Receiver?.Entry ?? _entry);
            }

            return;
        }

        if (bound.Receiver is not { } receiver)
        {
            _notify(Loc.T("Ten podgląd nie obsługuje akcji „{0}”.", name), true);
            return;
        }

        if (_acting)
        {
            return;
        }

        _acting = true;
        var status = _status.Text;
        var busy = Loc.T("{0}: {1}…", receiver.Plugin.DisplayName, action.Label);
        SetStatus(busy, busy: true);
        try
        {
            await FlushFormAsync();
            var values = ReferenceEquals(_formOwner, receiver) && _formValues is { } current ? new Dictionary<string, string>(current, StringComparer.Ordinal) : null;
            var request = new PreviewActionRequest(receiver.Entry, receiver.Options, name, arguments, values);
            var host = _app.HostFor(receiver.Plugin.Manifest);
            var token = _lifetime.Token;
            var result = await Task.Run(() => receiver.Plugin.InvokeActionAsync(request, host, token), token);
            if (_entry?.Path != receiver.Entry.Path || _lifetime.IsCancellationRequested)
            {
                return; // the view moved on meanwhile
            }

            if (result is null)
            {
                await LoadAsync(receiver.Entry);
            }
            else
            {
                _loading?.Cancel(); // a reload still running must not replace what the action returned
                _editing = false;
                (_plugin, _options) = (receiver.Plugin, receiver.Options);
                Show(ViewOnlyWhereReadOnly(receiver.Entry, result));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notify(Loc.T("{0}: {1}", receiver.Plugin.DisplayName, ex.Message), true);
        }
        finally
        {
            _acting = false;
            // Whatever happened, the "working…" line goes away unless something newer already replaced it.
            if (_status.Text == busy)
            {
                SetStatus(status);
            }
        }
    }

    /// <summary>
    /// One <see cref="WebPreviewView"/> serves consecutive web previews (the same page gets an "entry" event instead of
    /// a reload); re-showing the same content (a skin change) only repaints it.
    /// </summary>
    private Control WebView(WebPreview web)
    {
        if (!WebPreviewView.IsSupported)
        {
            return MessageText(WebPreviewView.UnavailableMessage);
        }

        if (_entry is not { } entry || _plugin is not ExternalPreviewPlugin plugin)
        {
            return MessageText(Loc.T("Strony podglądu pokazują tylko wtyczki zewnętrzne."));
        }

        if (_web is null)
        {
            _web = new WebPreviewView(_app, _brushes, RunWebShortcut);
        }
        else
        {
            _web.ApplySkin(_brushes);
        }

        if (!ReferenceEquals(_webShown, web))
        {
            _webShown = web;
            _ = _web.ShowAsync(web, entry, plugin, _app.HostFor(plugin.Manifest));
        }

        return _web;
    }

    private bool RunWebShortcut(Core.Input.KeyStroke stroke)
    {
        if (stroke.Key == "ESCAPE" && stroke.Modifiers == Core.Input.KeyModifiers.None)
        {
            CloseRequested?.Invoke();
            return true;
        }

        return RunShortcut?.Invoke(stroke) ?? false;
    }

    private void DisposeWeb()
    {
        _web?.Dispose();
        _web = null;
        _webShown = null;
    }

    private TextBlock MessageText(string text) =>
        new() { Text = text, Foreground = _brushes.HiddenForeground, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };

    private Border UnknownFileBanner(VfsEntry file)
    {
        var extension = Path.GetExtension(file.Name) is { Length: > 0 } ext ? ext : file.Name;
        var matches = _app.PluginCatalog.FindPreviewCandidates(file);
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = matches.Count > 0
                ? Loc.T("Brak aktywnego podglądu dla „{0}”. Znaleziono pasujące wtyczki w oficjalnym katalogu.", extension)
                : Loc.T("Brak aktywnego podglądu dla „{0}”. Oficjalny katalog został sprawdzony i nie zawiera pasującej wtyczki.", extension),
            TextWrapping = TextWrapping.Wrap,
            Foreground = _brushes.FileForeground,
        });

        if (matches.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = Loc.T("Pasujące podglądy z oficjalnego katalogu:"),
                FontWeight = FontWeight.SemiBold,
                Foreground = _brushes.DirectoryForeground,
            });
            foreach (var match in matches)
            {
                var description = new TextBlock().Text($"{match.Name} — {match.LocalizedDescription}")
                    .TextWrapping(TextWrapping.Wrap).VerticalAlignment(VerticalAlignment.Center)
                    .Foreground(_brushes.MutedForeground);
                content.Children.Add(InstallCatalogPreview is { } install
                    ? new Grid().Cols("*,Auto").Children(description, BannerButton(Loc.T("Pobierz"), () => install(match.Id), primary: false).Col(1))
                    : description);
            }
        }
        if (OpenPluginCatalog is { } openCatalog)
        {
            content.Children.Add(BannerButton(Loc.T("Otwórz oficjalny katalog wtyczek"), openCatalog, primary: false));
        }

        if (ShowUnknownPreviewOptions is { } options)
        {
            content.Children.Add(BannerButton(Loc.T("Przygotuj prompt dla asystenta AI"), () => options(file), primary: true));
        }

        return new Border
        {
            Background = _brushes.HighlightBackground,
            BorderBrush = _brushes.PanelBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8),
            Child = content,
        };
    }

    private Button BannerButton(string text, Func<Task> action, bool primary)
    {
        var button = Ds.Button(_brushes, text, async () => await action(), primary ? ButtonKind.Primary : ButtonKind.Toolbar);
        button.Margin = new Thickness(0, 0, 8, 0);
        return button;
    }


    private void ToggleEdit()
    {
        if (!CanEdit)
        {
            return;
        }

        if (_editing && _editor is { } editor)
        {
            // Keep the edited text when going back to the formatted view.
            _shown = MapInner(_shown!, inner => inner switch
            {
                MarkdownPreview m => m with { Markdown = editor.Text ?? string.Empty },
                TextPreview t => t with { Text = editor.Text ?? string.Empty },
                var other => other,
            });
        }

        _editing = !_editing;
        Show(_shown!);
        _editor?.Focus();
    }

    private async Task SaveAsync()
    {
        if (_entry is not { } entry || _plugin is null || _editor is null)
        {
            return;
        }

        try
        {
            await _app.HostFor(_plugin.Manifest).WriteTextAsync(entry.Path, _editor.Text ?? string.Empty);
            _savedText = _editor.Text ?? string.Empty;
            _dirty = false;
            _escapeArmed = false;
            _saveButton.Content = Loc.T("Zapisz (Ctrl+S)");
            _notify(Loc.T("Zapisano {0}.", entry.Name), false);
        }
        catch (Exception ex)
        {
            _notify(Loc.T("Nie zapisano: {0}", ex.Message), true);
        }
    }

    private Control MarkdownEditor(string markdown)
    {
        var editor = CreateEditor(markdown);
        _livePreview = Scroll(MarkdownRenderer.Render(markdown, _brushes));
        _liveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _liveDelay.Tick += (_, _) =>
        {
            _liveDelay.Stop();
            if (_livePreview is not null)
            {
                _livePreview.Content = MarkdownRenderer.Render(editor.Text ?? string.Empty, _brushes);
            }
        };
        editor.TextChanged += (_, _) =>
        {
            _liveDelay.Stop();
            _liveDelay.Start();
        };

        var grid = new Grid().Cols("*,4,*");
        grid.Children.Add(editor);
        grid.Children.Add(new GridSplitter().Col(1).Background(_brushes.PanelBorder));
        grid.Children.Add(_livePreview.Col(2));
        return grid;
    }

    private Control TextView(TextPreview text)
    {
        if (_editing)
        {
            return CreateEditor(text.Text);
        }

        var view = Monospace(text.Text, text.Truncated ? Loc.T("Pokazano początek pliku (2 MB).") : null);
        if (_app.Settings.SyntaxHighlighting && text.FileName is not null && FindMonospaceText(view) is { } block)
        {
            _ = HighlightAsync(block, text);
        }

        return view;
    }

    private static SelectableTextBlock? FindMonospaceText(Control view) => (view as ScrollViewer)?.Content as SelectableTextBlock;

    /// <summary>Tokenizes off the UI thread, then swaps the plain text for coloured runs if the preview is still the one on screen.</summary>
    private async Task HighlightAsync(SelectableTextBlock block, TextPreview text)
    {
        var dark = _brushes.PanelBackground.Color is { } c && (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) < 128;
        var cts = _loading;
        try
        {
            var spans = await Task.Run(() =>
            {
                try
                {
                    return SyntaxHighlighter.Highlight(text.Text, text.FileName, dark, cts?.Token ?? CancellationToken.None);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ArgumentException or IOException)
                {
                    return null; // Cancelled, or a grammar failed to load: stay plain.
                }
            }).ConfigureAwait(true);

            if (spans is null || !ReferenceEquals(Inner(_shown), text) || TopLevel.GetTopLevel(block) is null)
            {
                return;
            }

            block.Text = null;
            foreach (var span in spans)
            {
                var run = new Avalonia.Controls.Documents.Run(span.Text);
                if (span.Rgb is { } rgb)
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                }

                block.Inlines!.Add(run);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private TextBox CreateEditor(string text)
    {
        var editor = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = _brushes.MonospaceFamily,
            FontSize = _brushes.FontSize,
            Background = _brushes.PanelBackground,
            Foreground = _brushes.FileForeground,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10),
        };
        // Dirty means "differs from what was loaded or last saved", not "a TextChanged happened" (Avalonia raises
        // it when the text is first assigned).
        _savedText = text;
        editor.TextChanged += (_, _) =>
        {
            _dirty = !string.Equals(editor.Text ?? string.Empty, _savedText, StringComparison.Ordinal);
            _escapeArmed = false;
            _saveButton.Content = _dirty ? Loc.T("● Zapisz (Ctrl+S)") : Loc.T("Zapisz (Ctrl+S)");
        };
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _ = SaveAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F4)
            {
                ToggleEdit();
                e.Handled = true;
            }
        };
        _editor = editor;
        return editor;
    }

    private Control Monospace(string text, string? note)
    {
        var box = new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = _brushes.MonospaceFamily,
            FontSize = _brushes.FontSize - 1,
            Background = _brushes.PanelBackground,
            Foreground = _brushes.FileForeground,
            Padding = new Thickness(4, 2),
        };
        if (note is not null)
        {
            SetStatus(note);
        }

        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = box };
    }

    private Control ImageView(ImagePreview image)
    {
        try
        {
            using var stream = new MemoryStream(image.EncodedImage.ToArray());
            var bitmap = new Bitmap(stream);
            SetStatus($"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} px · {_entry?.Path}");
            return new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new TextBlock { Text = Loc.T("Nie można odczytać obrazu: ") + ex.Message, Foreground = _brushes.Error, Margin = new Thickness(16) };
        }
    }

    private Control FolderView(FolderPreview folder)
    {
        var known = folder.Items.Where(i => i.Size is not null).Sum(i => i.Size!.Value);
        var max = Math.Max(1, folder.Items.Select(i => i.Size ?? 0).DefaultIfEmpty(0).Max()); // Empty folders have no items.
        var ordered = folder.Items.OrderByDescending(i => i.Size ?? -1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var pending = folder.Items.Count(i => i.Size is null);

        if (folder.Complete && folder.Items.Count == 0 && _entry is { } emptyFolder
            && VfsPath.GetScheme(emptyFolder.Path) is VfsPath.FileScheme or VfsPath.SmbScheme
            && ConfigureFolderView is { } configure)
        {
            SetStatus(Loc.T("Gotowe · {0}", folder.Path));
            var message = new TextBlock
            {
                Text = Loc.T("Ten folder jest pusty. Wybierz, co ma pokazywać — ustawienia zostaną zapisane w pliku c4k.dir."),
                Foreground = _brushes.MutedForeground,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var button = new Button
            {
                Content = Loc.T("Skonfiguruj jako specjalny folder"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(16, 8),
                Background = _brushes.Accent,
                Foreground = _brushes.AccentForeground,
            };
            button.Click += async (_, _) => await configure(emptyFolder.Path);
            return new Grid
            {
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 16,
                        MaxWidth = 440,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(24),
                        Children = { message, button },
                    },
                },
            };
        }

        var list = new StackPanel { Spacing = 1, Margin = new Thickness(12, 4) };
        list.Children.Add(new TextBlock
        {
            Text = $"Razem: {FileViewportCanvas.FormatSize(known)}" + (folder.Complete ? string.Empty : Loc.T("  (liczenie… {0} folderów)", pending)) +
                "   ·   " + Loc.P(folder.Files, "{0:N0} plik|{0:N0} pliki|{0:N0} plików") + ", " + Loc.P(folder.Folders, "{0:N0} folder|{0:N0} foldery|{0:N0} folderów"),
            FontSize = _brushes.FontSize + 2,
            FontWeight = FontWeight.Bold,
            Foreground = _brushes.DirectoryForeground,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (var item in ordered.Take(MaxFolderRows))
        {
            var fraction = (item.Size ?? 0) / (double)max;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,Auto,3*"), Height = _brushes.RowHeight };
            row.Children.Add(new TextBlock
            {
                Text = (item.IsDirectory ? "▸ " : "  ") + item.Name,
                Foreground = item.IsDirectory ? _brushes.DirectoryForeground : _brushes.FileForeground,
                FontWeight = item.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var size = new TextBlock
            {
                Text = item.Size is { } bytes ? FileViewportCanvas.FormatSize(bytes) : "…",
                Foreground = _brushes.FileForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0),
                MinWidth = 80,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            var bar = new Border
            {
                Background = item.IsDirectory ? _brushes.Accent : _brushes.Skeleton,
                Height = Math.Max(4, _brushes.RowHeight * 0.45),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var barHost = new Border { Child = bar };
            barHost.SizeChanged += (_, e) => bar.Width = Math.Max(1, e.NewSize.Width * fraction);
            Grid.SetColumn(barHost, 2);
            row.Children.Add(barHost);
            list.Children.Add(row);
        }

        if (ordered.Count > MaxFolderRows)
        {
            list.Children.Add(new TextBlock { Text = $"… i {ordered.Count - MaxFolderRows} mniejszych", Foreground = _brushes.HiddenForeground, Margin = new Thickness(0, 6) });
        }

        SetStatus(folder.Complete ? Loc.T("Gotowe · {0}", folder.Path) : Loc.T("Liczenie rozmiarów… · {0}", folder.Path), busy: !folder.Complete);
        return Scroll(list);
    }

    /// <summary>Tiles in rows that wrap with the panel's width; pictures are decoded off the UI thread and kept while the view updates.</summary>
    private Control TilesView(TilesPreview tiles)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 8), Spacing = 6 };
        if (tiles.Title.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = tiles.Title, FontSize = _brushes.FontSize + 4, FontWeight = FontWeight.Bold, Foreground = _brushes.DirectoryForeground });
        }

        if (tiles.Description is { Length: > 0 } description)
        {
            stack.Children.Add(new TextBlock { Text = description, Foreground = _brushes.MutedForeground, TextWrapping = TextWrapping.Wrap });
        }

        var wrap = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
        foreach (var tile in tiles.Tiles)
        {
            wrap.Children.Add(TileButton(tile, tiles.TileWidth));
        }

        stack.Children.Add(wrap);
        SetStatus(Loc.P(tiles.Tiles.Count, "{0} kafelek|{0} kafelki|{0} kafelków") + (_entry is { } shown ? " · " + shown.Path : string.Empty));
        return Scroll(stack);
    }

    /// <summary>One tile: its picture (or icon), title and subtitle; a click goes to <see cref="OpenTile"/>.</summary>
    private Button TileButton(PreviewTile tile, double width)
    {
        var folder = _entry;
        var imageHeight = Math.Round(width * 0.5);
        Control picture;
        if (tile.Image is { } data)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Height = imageHeight, Width = width - 14 };
            var key = $"{tile.Target}|{tile.Title}|{data.Length}";
            if (_thumbnails.TryGetValue(key, out var cached))
            {
                image.Source = cached;
            }
            else
            {
                _thumbnails[key] = null;
                _ = DecodeThumbnailAsync(key, data, (int)Math.Min(width * 2, 800), image);
            }

            picture = new Border { CornerRadius = new CornerRadius(_brushes.ControlRadius), ClipToBounds = true, Child = image, Background = _brushes.Skeleton };
        }
        else
        {
            picture = new Border
            {
                Height = Math.Min(imageHeight, 72),
                Background = _brushes.Surface2,
                CornerRadius = new CornerRadius(_brushes.ControlRadius),
                Child = new TextBlock { Text = tile.Icon ?? "▪", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        }

        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(picture);
        content.Children.Add(new TextBlock { Text = tile.Title, FontWeight = FontWeight.SemiBold, Foreground = _brushes.FileForeground, TextTrimming = TextTrimming.CharacterEllipsis });
        if (tile.Subtitle is { Length: > 0 } subtitle)
        {
            content.Children.Add(new TextBlock { Text = subtitle, FontSize = _brushes.FontSize - 1, Foreground = _brushes.MutedForeground, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var button = new Button
        {
            Content = content,
            Width = width,
            Margin = new Thickness(6),
            Padding = new Thickness(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = _brushes.PanelBackground,
            BorderBrush = _brushes.PanelBorder,
            BorderThickness = new Thickness(1),
            IsEnabled = tile.Target is not null && OpenTile is not null,
        };
        if (tile.Target is { } target)
        {
            ToolTip.SetTip(button, target);
            button.Click += async (_, _) => await OpenTile!(tile, folder);
        }

        return button;
    }

    private DashboardView? _dashboard;

    /// <summary>A dashboard's cards stay live while it is shown; <see cref="StopDashboard"/> ends them.</summary>
    private Control DashboardView(Plugins.BuiltIn.Dashboard.DashboardPreview dashboard)
    {
        var folder = _entry;
        _dashboard = new DashboardView(_app, dashboard, _brushes,
            tile => OpenTile is { } open ? open(tile, folder) : Task.CompletedTask,
            TileButton,
            AskFolderTrust,
            _notify,
            async (index, edit) =>
            {
                if (EditDashboardTile is { } editTile)
                {
                    await editTile(dashboard, index, edit);
                    await ReloadCurrentAsync();
                }
            });
        SetStatus(Loc.P(dashboard.Slots.Count, "{0} kafelek|{0} kafelki|{0} kafelków") + (folder is { } shown ? " · " + shown.Path : string.Empty));
        return _dashboard;
    }

    private void StopDashboard()
    {
        if (_dashboard is { } dashboard)
        {
            _dashboard = null;
            _ = dashboard.StopAsync();
        }
    }

    private Control ChartView(ChartPreview chart)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 8), Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = chart.Title, FontSize = _brushes.FontSize + 4, FontWeight = FontWeight.Bold, Foreground = _brushes.DirectoryForeground });
        if (chart.Description is { Length: > 0 } description)
        {
            stack.Children.Add(new TextBlock { Text = description, Foreground = _brushes.MutedForeground, TextWrapping = TextWrapping.Wrap });
        }

        stack.Children.Add(new ChartControl(_brushes) { Chart = chart.Chart, Height = 240 });
        return Scroll(stack);
    }

    private async Task DecodeThumbnailAsync(string key, ReadOnlyMemory<byte> data, int width, Image image)
    {
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(data.ToArray());
                // A stored thumbnail is already small: decoded as is (DecodeToWidth would blow it up to the tile).
                return data.Length <= SmallPictureBytes ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, width);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or IOException)
            {
                return null; // Not a picture after all: the tile keeps its grey placeholder.
            }
        });
        _thumbnails[key] = bitmap;
        image.Source = bitmap;
    }


    private static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Status line text; <paramref name="busy"/> shows the wave dots in front of it.</summary>
    private void SetStatus(string? text, bool busy = false)
    {
        _status.Text = text;
        _busy.IsVisible = busy;
    }
}
