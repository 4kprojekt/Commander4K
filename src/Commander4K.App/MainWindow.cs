using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Commander4K.App.Browser;
using Commander4K.App.Composition;
using Commander4K.App.Dialogs;
using Commander4K.App.Input;
using Commander4K.App.Palettes;
using Commander4K.App.Panels;
using Commander4K.App.PluginPanels;
using Commander4K.App.Preview;
using Commander4K.App.Theming;
using Commander4K.Core.Configuration;
using Commander4K.Core.Input;
using Commander4K.Core.Jobs;
using Commander4K.Core.Security;
using Commander4K.Core.Updates;
using Commander4K.Core.Web;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Network;
using Commander4K.PluginSdk;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Commander4K.App;

/// <summary>
/// Dual-pane shell. The window shows immediately; <see cref="AppHost"/> (config, SQLite, plugins) is created in the
/// background and the panels are built once it is ready.
/// </summary>
public sealed class MainWindow : Window, IShell
{
    private static readonly (string Key, string Label, string Command)[] FunctionKeys =
    [
        ("1", "Ustawienia", CommandIds.Settings),
        ("2", "Panele", CommandIds.PanelModes),
        ("3", "Podgląd", CommandIds.View),
        ("4", "Edycja", CommandIds.Edit),
        ("5", "Kopiuj", CommandIds.Copy),
        ("6", "Przenieś", CommandIds.Move),
        ("7", "Katalog", CommandIds.MakeDirectory),
        ("8", "Usuń", CommandIds.Delete),
        ("9", "Terminal", CommandIds.OpenTerminal),
        ("10", "Wyjście", CommandIds.Exit),
    ];

    private readonly DispatcherTimer _jobsTimer;
    private readonly DispatcherTimer _notificationTimer;
    private AppHost? _host;
    private SkinManager? _skins;
    private FilePanelView _left = null!;
    private FilePanelView _right = null!;
    private FilePanelView _active = null!;
    private GridSplitter _splitter = null!;
    private Border _jobsBar = null!;
    private StackPanel _jobsList = null!;
    private Border _commandBar = null!;
    private TextBlock _prompt = null!;
    private TextBox _commandLine = null!;
    private TextBlock _notification = null!;
    private Grid _keyBar = null!;
    private bool _jobsDirty;
    private UpdateInfo? _pendingUpdate;
    private PluginHostAdapter? _pluginHost;
    private Menu _menu = null!;
    private Grid _leftDock = null!;
    private Grid _rightDock = null!;
    private Grid _betweenDock = null!;
    private StackPanel _bottomDock = null!;
    private readonly List<PaletteView> _paletteViews = [];
    private IReadOnlyList<IPalettePlugin> _builtInPalettes = [];
    private string? _navigationKey;

    public MainWindow()
    {
        Title = $"Commander4K {AppVersion.Display}";
        Width = 1360;
        Height = 860;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new TextBlock()
            .Text("Commander4K — uruchamianie…")
            .HorizontalAlignment(HorizontalAlignment.Center)
            .VerticalAlignment(VerticalAlignment.Center);

        _jobsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => RefreshJobsBar());
        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            _notification.Text = string.Empty;
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await StartAsync();
        Closing += (_, _) => SaveState();
    }

    /// <summary>For tests and screenshots: builds the shell around an existing host without the async start-up.</summary>
    public void Attach(AppHost host)
    {
        _host = host;
        _skins = new SkinManager(host, host.Skins.Get(host.Settings.Skin));
        BuildShell();
    }

    public FilePanelView LeftPanel => _left;

    public FilePanelView RightPanel => _right;

    public SkinManager? Skins => _skins;

    private async Task StartAsync()
    {
        if (_host is not null)
        {
            return; // Already attached by the caller (tests, tooling).
        }

        try
        {
            Attach(await AppHost.CreateAsync());
        }
        catch (Exception ex)
        {
            Content = new SelectableTextBlock().Text("Nie udało się uruchomić Commander4K:\n\n" + ex).Margin(20).TextWrapping(TextWrapping.Wrap);
        }
    }

    private void BuildShell()
    {
        var host = _host!;
        var skins = _skins!;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _left = new FilePanelView(host, skins, host.Settings.LeftPath ?? home);
        _right = new FilePanelView(host, skins, host.Settings.RightPath ?? Plugins.BuiltIn.Local.LocalFileSystemPlugin.RootPath);
        foreach (var panel in new[] { _left, _right })
        {
            var p = panel;
            p.Activated += SetActive;
            p.FileActivated += entry => OpenFile(p, entry);
            p.AddConnectionRequested += sender => _ = ExecuteAsync(CommandIds.AddConnection);
            p.AuthenticationRequired += request => _ = LoginAndRetryAsync(request);
        }

        _keyBar = BuildKeyBar();
        _pluginHost = new PluginHostAdapter(
            () => (_active, Other),
            Notify,
            (title, text) => ShowTextWindowAsync(title, text),
            (title, message) => new ConfirmDialog(_skins!.Current, title, message).ShowAsync(this),
            (title, message, initial) => new InputDialog(_skins!.Current, title, message, initial).ShowAsync(this),
            OpenPanelAsync,
            async path => await host.Tools.EditAsync(path),
            url =>
            {
                OpenBrowser(url);
                return Task.CompletedTask;
            },
            SearchWebAsync);
        host.PluginUi.Target = _pluginHost;
        host.PowerShell.CommandsChanged += () => Dispatcher.UIThread.Post(RebuildMenu);
        host.PluginsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            RebuildMenu();
            RegisterPaletteSettings();
            RebuildPalettes();
        });
        _menu = new Menu();

        // The between-panels dock sits inside the right half so the splitter keeps resizing two star columns.
        var rightHalf = new DockPanel().Col(2);
        _betweenDock = new Grid().Margin(0, 2, 2, 0);
        DockPanel.SetDock(_betweenDock, Dock.Left);
        rightHalf.Children.Add(_betweenDock);
        rightHalf.Children.Add(_right.Margin(0, 2, 2, 0));
        var shell = new Grid()
            .Rows("*,Auto,Auto,Auto,Auto")
            .Children(
                new Grid()
                    .Row(0)
                    .Cols("Auto,*,Auto")
                    .Children(
                        new Grid().Ref(out _leftDock).Col(0).Margin(2, 2, 0, 0),
                        new Grid()
                            .Col(1)
                            .Cols("*,4,*")
                            .Children(
                                _left.Col(0).Margin(2, 2, 0, 0),
                                new GridSplitter().Ref(out _splitter).Col(1).ResizeDirection(GridResizeDirection.Columns),
                                rightHalf),
                        new Grid().Ref(out _rightDock).Col(2).Margin(0, 2, 2, 0)),
                new StackPanel().Ref(out _bottomDock).Row(1),
                new Border()
                    .Ref(out _jobsBar)
                    .Row(2)
                    .IsVisible(false)
                    .Padding(8, 4)
                    .Child(new StackPanel().Ref(out _jobsList).Spacing(4)),
                new Border()
                    .Ref(out _commandBar)
                    .Row(3)
                    .Padding(4, 2)
                    .Child(new Grid().Cols("Auto,*,Auto").Children(
                        new TextBlock().Ref(out _prompt).VerticalAlignment(VerticalAlignment.Center).Margin(4, 0),
                        new TextBox().Ref(out _commandLine).Col(1).BorderThickness(new Thickness(0)).MinHeight(24).Padding(4, 2),
                        new TextBlock().Ref(out _notification).Col(2).VerticalAlignment(VerticalAlignment.Center).Margin(8, 0)
                            .MaxWidth(520).TextTrimming(TextTrimming.CharacterEllipsis))),
                _keyBar.Row(4));
        DockPanel.SetDock(_menu, Dock.Top);
        Content = new DockPanel().Children(_menu, shell);

        RegisterCommands();
        RebuildMenu();
        _builtInPalettes = [new QuickActionsPalette(this), new FavoritesPalette(this), new ViewsPalette(this), new RecentPalette(this)];
        RegisterPaletteSettings();
        host.Jobs.JobChanged += _ => Dispatcher.UIThread.Post(() => _jobsDirty = true);
        host.Remote.HostKeyValidator = ValidateHostKey;
        _jobsTimer.Start();
        skins.Changed += ApplySkin;
        ApplySkin(skins.Current);
        SetActive(_left);
        RebuildPalettes();

        _notification.PointerPressed += async (_, _) =>
        {
            if (_pendingUpdate is { } update)
            {
                await new UpdateDialog(host, skins.Current, update).ShowAsync(this);
            }
        };
        UpdateService.CleanupPreviousVersion(Environment.ProcessPath);
        _ = CheckForUpdatesInBackgroundAsync();
    }

    /// <summary>Daily, silent check; a found release is announced in the status area and opened by clicking it.</summary>
    private async Task CheckForUpdatesInBackgroundAsync()
    {
        var settings = _host!.Settings;
        if (!settings.CheckForUpdates || settings.LastUpdateCheck > DateTimeOffset.UtcNow.AddHours(-24))
        {
            return;
        }

        try
        {
            var update = await _host.Updates.CheckAsync(settings.UpdateRepository, AppVersion.Current, settings.IncludePrereleases, CancellationToken.None, UpdateService.DetectInstallKind(Environment.ProcessPath));
            settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            await _host.SaveSettingsAsync();
            if (update is not null && update.Version.ToString() != settings.SkippedVersion)
            {
                _pendingUpdate = update;
                Notify($"⬆ Dostępna wersja {update.Version} — kliknij, aby zaktualizować");
                _notificationTimer.Stop(); // Keep it visible until acted upon.
            }
        }
        catch (UpdateException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Update check failed: {ex.Message}");
        }
    }

    /// <summary>Manual check (command palette, settings): always reports the outcome.</summary>
    private async Task CheckForUpdatesNowAsync()
    {
        var settings = _host!.Settings;
        Notify("Sprawdzanie aktualizacji…");
        try
        {
            var update = await _host.Updates.CheckAsync(settings.UpdateRepository, AppVersion.Current, settings.IncludePrereleases, CancellationToken.None, UpdateService.DetectInstallKind(Environment.ProcessPath));
            settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            await _host.SaveSettingsAsync();
            if (update is null)
            {
                Notify($"Masz najnowszą wersję ({AppVersion.Display}).");
                return;
            }

            _pendingUpdate = update;
            await new UpdateDialog(_host, _skins!.Current, update).ShowAsync(this);
        }
        catch (UpdateException ex)
        {
            Notify(ex.Message, isError: true);
        }
    }

    private void OpenSettings(string? query = null)
    {
        var window = new SettingsWindow(_host!, _skins!.Current, OnSettingChanged, CheckForUpdatesNowAsync, query);
        window.Closed += (_, _) => RebuildMenu(); // Key bindings shown in the menu may have changed.
        window.Show(this);
    }

    private void OnSettingChanged(string key)
    {
        switch (key)
        {
            case "skin":
                _skins!.Apply(_host!.Skins.Get(_host.Settings.Skin));
                break;
            case "showHidden":
                _ = _left.ActiveTab.RefreshAsync();
                _ = _right.ActiveTab.RefreshAsync();
                break;
            case "showPalettes":
            case var palette when palette.StartsWith(PaletteSettingPrefix, StringComparison.Ordinal):
                RebuildPalettes();
                break;
        }
    }

    private const string PaletteSettingPrefix = "palette:";

    private IEnumerable<IPalettePlugin> AllPalettes => [.. _builtInPalettes, .. _host!.ExternalPalettes];

    private static readonly IReadOnlyList<SettingChoice> DockChoices =
    [
        new("bottom", "Na dole"), new("left", "Po lewej"), new("right", "Po prawej"), new("between", "Między panelami"), new("hidden", "Ukryta"),
    ];

    private static string DockName(PaletteDock dock) => dock switch
    {
        PaletteDock.Left => "left",
        PaletteDock.Right => "right",
        PaletteDock.BetweenPanels => "between",
        PaletteDock.Hidden => "hidden",
        _ => "bottom",
    };

    private PaletteDock DockOf(IPalettePlugin palette) =>
        _host!.Settings.PaletteDocks.TryGetValue(palette.Id, out var name) ? name switch
        {
            "bottom" => PaletteDock.Bottom,
            "left" => PaletteDock.Left,
            "right" => PaletteDock.Right,
            "between" => PaletteDock.BetweenPanels,
            "hidden" => PaletteDock.Hidden,
            _ => palette.DefaultDock,
        } : palette.DefaultDock;

    /// <summary>One "position" setting per palette (built-in and from plugins), so they are searchable like the rest.</summary>
    private void RegisterPaletteSettings()
    {
        _host!.ExtraSettings = [.. AllPalettes.Select(p => new SettingDescriptor(
            PaletteSettingPrefix + p.Id,
            SettingsCatalog.Palettes,
            $"Paleta „{p.Title}”",
            p.Manifest.IsBuiltIn
                ? $"Gdzie pokazywać paletę: na dole, po bokach, między panelami albo wcale. {p.Manifest.Description}"
                : $"Paleta wtyczki {p.Manifest.Name} ({p.Manifest.Id}): na dole, po bokach, między panelami albo wcale.",
            SettingKind.Choice,
            s => s.PaletteDocks.TryGetValue(p.Id, out var dock) ? dock : DockName(p.DefaultDock),
            (s, v) => s.PaletteDocks[p.Id] = (string)v!,
            ["paleta", "pasek", "położenie", "dokowanie", "przyciski", p.Title])
        { Choices = () => DockChoices })];
    }

    /// <summary>Recreates every palette view in its dock (after a dock change, skin-independent plugin reloads, Ctrl+B).</summary>
    private void RebuildPalettes()
    {
        foreach (var view in _paletteViews)
        {
            view.Detach();
        }

        _paletteViews.Clear();
        Panel[] docks = [_leftDock, _rightDock, _betweenDock, _bottomDock];
        foreach (var dock in docks)
        {
            dock.Children.Clear();
        }

        if (_host!.Settings.ShowPalettes)
        {
            foreach (var palette in AllPalettes)
            {
                var position = DockOf(palette);
                if (position == PaletteDock.Hidden)
                {
                    continue;
                }

                var view = new PaletteView(palette, _host.HostFor(palette.Manifest), position, _skins!.Current, Notify);
                _paletteViews.Add(view);
                (position switch
                {
                    PaletteDock.Left => _leftDock,
                    PaletteDock.Right => _rightDock,
                    PaletteDock.BetweenPanels => _betweenDock,
                    _ => (Panel)_bottomDock,
                }).Children.Add(view);
            }
        }

        // Several palettes in one side dock share its height.
        foreach (var grid in new[] { _leftDock, _rightDock, _betweenDock })
        {
            grid.RowDefinitions.Clear();
            for (var i = 0; i < grid.Children.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                Grid.SetRow(grid.Children[i], i);
            }
        }
    }

    private async Task TogglePalettesAsync()
    {
        _host!.Settings.ShowPalettes = !_host.Settings.ShowPalettes;
        RebuildPalettes();
        await _host.SaveSettingsAsync();
    }

    private async Task AddFavoriteAsync()
    {
        var favorites = (FavoritesPalette)_builtInPalettes.First(p => p is FavoritesPalette);
        var host = _host!.HostFor(favorites.Manifest);
        await favorites.InvokeActionAsync("add", null, host, CancellationToken.None);
        if (DockOf(favorites) == PaletteDock.Hidden || !_host.Settings.ShowPalettes)
        {
            Notify($"Dodano do ulubionych: {host.ActivePath} (paleta „Ulubione” jest ukryta — Ctrl+B / ustawienia).");
        }
    }

    AppHost IShell.App => _host!;

    public ViewSetting CaptureView(string name) => new()
    {
        Name = name,
        LeftPath = _left.ActiveTab.Path,
        RightPath = _right.ActiveTab.Path,
        LeftMode = ModeId(_left),
        RightMode = ModeId(_right),
    };

    private const string BrowserModePrefix = "browser:";

    private static string? ModeId(FilePanelView panel) => panel.Mode switch
    {
        PreviewPanelView => "preview",
        PluginPanelView view => CommandIds.PanelPrefix + view.Plugin.Id,
        BrowserPanelView browser => BrowserModePrefix + browser.CurrentUrl,
        _ => null,
    };

    /// <summary>
    /// Shows the browser in the mode target (the opposite panel), reusing one that is already open there;
    /// <paramref name="url"/> <c>null</c> keeps the current page (or the start page for a new browser).
    /// </summary>
    private BrowserPanelView OpenBrowser(string? url, FilePanelView? target = null)
    {
        target ??= ModeTarget().Target;
        if (target.Mode is BrowserPanelView existing)
        {
            if (url is not null)
            {
                existing.Navigate(url);
            }

            return existing;
        }

        var browser = new BrowserPanelView(_host!, _host!.HostFor(BrowserPlugin.Instance.Manifest), url, _skins!.Current, Notify, RunShortcut);
        target.ShowMode(browser);
        SetActive(target);
        return browser;
    }

    /// <summary><see cref="IPluginHost.SearchWebAsync"/>: <c>null</c> = search engine, <c>assistant</c> = AI assistant, else an engine id.</summary>
    private Task SearchWebAsync(string query, string? engine)
    {
        var settings = _host!.Settings;
        var resolved = engine switch
        {
            null or "" => SearchEngines.Get(settings.SearchEngine),
            "assistant" => SearchEngines.Get(settings.AssistantEngine, SearchEngines.DefaultAssistant),
            _ => SearchEngines.Get(engine),
        };
        return OpenBrowser(null).SearchAsync(query, resolved);
    }

    private async Task AskAiAsync()
    {
        var assistant = SearchEngines.Get(_host!.Settings.AssistantEngine, SearchEngines.DefaultAssistant);
        var question = await new InputDialog(_skins!.Current, $"Zapytaj AI ({assistant.Name})",
            "Pytanie (asystenta zmienisz w ustawieniach: „Asystent AI”):", string.Empty).ShowAsync(this);
        if (!string.IsNullOrWhiteSpace(question))
        {
            await SearchWebAsync(question, "assistant");
        }
    }

    /// <summary>App shortcuts pressed while a web page has focus (the page reports them; Avalonia never sees them).</summary>
    private bool RunShortcut(KeyStroke stroke)
    {
        var result = _host!.Keymap.Process(stroke);
        switch (result.Kind)
        {
            case KeymapResultKind.Command:
                _ = ExecuteAsync(result.CommandId!);
                return true;
            case KeymapResultKind.ChordPending:
                Notify(stroke + " …");
                return true;
            default:
                return false;
        }
    }

    /// <summary>Applies a saved view: modes switch at once, both panels navigate in parallel.</summary>
    public async Task RestoreViewAsync(ViewSetting view)
    {
        _left.CloseMode();
        _right.CloseMode();
        var navigations = new List<Task>();
        if (view.LeftPath is { Length: > 0 } leftPath)
        {
            navigations.Add(_left.NavigateAsync(leftPath));
        }

        if (view.RightPath is { Length: > 0 } rightPath)
        {
            navigations.Add(_right.NavigateAsync(rightPath));
        }

        await ApplyModeAsync(_left, view.LeftMode);
        await ApplyModeAsync(_right, view.RightMode);
        SetActive(_left.Mode is null || _right.Mode is not null ? _left : _right);
        try
        {
            await Task.WhenAll(navigations);
        }
        catch (Exception ex)
        {
            Notify($"Widok „{view.Name}”: {ex.Message}", isError: true);
        }
    }

    private async Task ApplyModeAsync(FilePanelView target, string? mode)
    {
        var source = target == _left ? _right : _left;
        switch (mode)
        {
            case null:
                break;
            case "preview":
                target.ShowMode(new PreviewPanelView(_host!, source, _skins!.Current, Notify));
                break;
            case var browser when browser.StartsWith(BrowserModePrefix, StringComparison.Ordinal):
                OpenBrowser(browser[BrowserModePrefix.Length..] is { Length: > 0 } url ? url : null, target);
                break;
            case var panel when panel.StartsWith(CommandIds.PanelPrefix, StringComparison.OrdinalIgnoreCase):
                await OpenPanelInAsync(panel[CommandIds.PanelPrefix.Length..], null, target);
                break;
        }
    }

    public Task<PickerItem?> PickAsync(string title, IReadOnlyList<PickerItem> items) =>
        new PickerDialog(_skins!.Current, title, items, "Wpisz, aby wyszukać:").ShowAsync(this);

    /// <summary>Ctrl+Shift+P: every command, skin and setting in one searchable list.</summary>
    private async ValueTask ShowCommandPaletteAsync(CommandInvocation _)
    {
        var host = _host!;
        var items = new List<PickerItem>();
        foreach (var command in host.ListCommands().OrderBy(c => c.Description, StringComparer.CurrentCulture))
        {
            var keys = string.Join(", ", host.KeyBindings.Where(b => string.Equals(b.Value, command.Id, StringComparison.OrdinalIgnoreCase)).Select(b => b.Key));
            items.Add(new PickerItem(command.Description, keys.Length > 0 ? keys : null, "cmd:" + command.Id, command.Id));
        }

        items.AddRange(host.Skins.Skins.Select(s => new PickerItem($"Skórka: {s.DisplayName}", null, "skin:" + s.Id, "motyw kolory theme")));
        items.AddRange(host.SettingsCatalog.Select(s => new PickerItem($"Ustawienia: {s.Title}", s.Category, "set:" + s.Title, string.Join(' ', s.Keywords ?? []))));

        var choice = await new PickerDialog(_skins!.Current, "Paleta komend (Ctrl+Shift+P)", items, "Wpisz, czego szukasz — komendy, skórki, ustawienia:").ShowAsync(this);
        switch (choice?.Value.Split(':', 2))
        {
            case ["cmd", var id]:
                await ExecuteAsync(id);
                break;
            case ["skin", var id]:
                _skins.Apply(host.Skins.Get(id));
                break;
            case ["set", var title]:
                OpenSettings(title);
                break;
        }
    }

    private void SetActive(FilePanelView panel)
    {
        _active = panel;
        _left.IsActive = panel == _left;
        _right.IsActive = panel == _right;
        UpdatePrompt();
    }

    private FilePanelView Other => _active == _left ? _right : _left;

    private void UpdatePrompt()
    {
        if (_prompt is not null && _active is not null)
        {
            // The command line works on the visible file list, which is the other panel while this one shows a mode.
            _prompt.Text = (_active.Mode is null ? _active : Other).ActiveTab.Path + ">";
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_host is null || _active is null)
        {
            return;
        }

        var focused = FocusManager?.GetFocusedElement();
        if (focused == _commandLine)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_commandLine.Text))
            {
                RunCommandLine();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                _commandLine.Text = string.Empty;
                Focus();
                e.Handled = true;
                return;
            }
        }

        var inTextBox = focused is TextBox;

        // Inside a mode (preview editor, plugin filter) its own shortcuts — F-keys, Ctrl/Alt combos, Esc — come
        // first; plain typing keys stay with the text box.
        if (inTextBox && _active.Mode is { } focusedMode && focused is Visual visual && focusedMode.View.IsVisualAncestorOf(visual)
            && (KeyMapping.IsFunctionKey(e.Key) || e.Key == Key.Escape
                || (e.KeyModifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt)) != 0)
            && focusedMode.HandleKey(e))
        {
            e.Handled = true;
            return;
        }
        var hasCommandModifier = (e.KeyModifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt)) != 0;
        if (inTextBox && !hasCommandModifier && !KeyMapping.IsFunctionKey(e.Key) && e.Key != Key.Tab)
        {
            return; // Let the text box have ordinary typing, cursor keys, Enter and Backspace.
        }

        // A plugin panel gets first pick (its actions may reuse Delete/F3/Enter); the rest falls through to the keymap.
        if (!inTextBox && _active.Mode is { } plugin && plugin.HandleKey(e))
        {
            e.Handled = true;
            return;
        }

        if (!inTextBox && _active.Mode is null && _active.HandleNavigationKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (KeyMapping.ToStroke(e) is not { } stroke)
        {
            return;
        }

        var result = _host.Keymap.Process(stroke);
        switch (result.Kind)
        {
            case KeymapResultKind.Command:
                e.Handled = true;
                _ = ExecuteAsync(result.CommandId!);
                break;
            case KeymapResultKind.ChordPending:
                e.Handled = true;
                Notify(stroke + " …");
                break;
        }
    }

    /// <summary>Typing while a panel has focus goes to the command line, as in Total Commander.</summary>
    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (_commandLine is null || FocusManager?.GetFocusedElement() is TextBox || string.IsNullOrEmpty(e.Text) || e.Text == " ")
        {
            return;
        }

        if (_active.Mode is { } plugin)
        {
            plugin.AppendText(e.Text);
            e.Handled = true;
            return;
        }

        _commandLine.Text += e.Text;
        _commandLine.CaretIndex = _commandLine.Text.Length;
        _commandLine.Focus();
        e.Handled = true;
    }

    private Grid BuildKeyBar()
    {
        var bar = new Grid().Cols(string.Join(",", FunctionKeys.Select(_ => "*")));
        for (var i = 0; i < FunctionKeys.Length; i++)
        {
            var (key, label, command) = FunctionKeys[i];
            var cell = new Border()
                .Col(i)
                .Cursor(new Cursor(StandardCursorType.Hand))
                .Child(new DockPanel().Children(
                    new TextBlock().Text(key).Padding(4, 2).Tag("number"),
                    new Border().Margin(0, 0, 2, 0).Tag("label").Child(new TextBlock().Text(label).Padding(4, 2).TextTrimming(TextTrimming.CharacterEllipsis))));
            cell.PointerPressed += (_, _) => _ = ExecuteAsync(command);
            bar.Children.Add(cell);
        }

        return bar;
    }

    private void ApplySkin(SkinBrushes b)
    {
        Background = b.WindowBackground;
        _splitter.Background = b.WindowBackground;
        _jobsBar.Background = b.StatusBackground;
        _commandBar.Background = b.CommandLineBackground;
        _prompt.Foreground = b.CommandLineForeground;
        _prompt.FontFamily = b.FontFamily;
        _commandLine.Background = b.CommandLineBackground;
        _commandLine.Foreground = b.CommandLineForeground;
        _commandLine.FontFamily = b.FontFamily;
        _notification.Foreground = b.StatusForeground;
        ApplyMenuSkin(b);
        _keyBar.Background = b.FunctionKeyNumberBackground;
        foreach (var cell in _keyBar.Children.OfType<Border>())
        {
            var dock = (DockPanel)cell.Child!;
            var number = (TextBlock)dock.Children[0];
            var labelBorder = (Border)dock.Children[1];
            number.Foreground = b.FunctionKeyNumberForeground;
            number.FontFamily = b.FontFamily;
            number.FontWeight = FontWeight.Bold;
            labelBorder.Background = b.FunctionKeyLabelBackground;
            var label = (TextBlock)labelBorder.Child!;
            label.Foreground = b.FunctionKeyLabelForeground;
            label.FontFamily = b.FontFamily;
        }

        foreach (var palette in _paletteViews)
        {
            palette.ApplySkin(b);
        }

        _jobsDirty = true;
    }

    private void Notify(string message, bool isError = false)
    {
        if (_notification is null)
        {
            return;
        }

        _notification.Text = message;
        _notification.Foreground = isError ? _skins!.Current.Error : _skins!.Current.StatusForeground;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void RefreshJobsBar()
    {
        UpdatePrompt(); // Cheap; follows navigation and tab switches without per-tab subscriptions.
        if (_left is not null && _active is not null)
        {
            var navigation = $"{_left.ActiveTab.Path}|{_right.ActiveTab.Path}|{_active == _left}";
            if (navigation != _navigationKey)
            {
                _navigationKey = navigation;
                foreach (var palette in _paletteViews.Where(p => p.FollowsNavigation))
                {
                    palette.Refresh();
                }
            }
        }

        if (!_jobsDirty || _host is null)
        {
            return;
        }

        _jobsDirty = false;
        var b = _skins!.Current;
        var jobs = _host.Jobs.Jobs;
        var anyRunning = false;
        _jobsList.Children.Clear();
        foreach (var job in jobs)
        {
            anyRunning |= job.State is JobState.Queued or JobState.Running;
            var metrics = job.Metrics;
            var fraction = metrics.Fraction;
            var speed = job.BytesPerSecond > 0 ? $" · {FileViewportCanvas.FormatSize((long)job.BytesPerSecond)}/s" : string.Empty;
            var state = job.State switch
            {
                JobState.Queued => "w kolejce",
                JobState.Running => fraction >= 0 ? $"{fraction:P0}{speed}" : "…",
                JobState.Completed => "✓ gotowe",
                JobState.Canceled => "anulowano",
                _ => "⚠ " + job.Error?.Message,
            };
            var handle = job;
            _jobsList.Children.Add(new Grid().Cols("2*,3*,Auto,Auto").Children(
                new TextBlock().Text(job.Title).Foreground(b.StatusForeground).TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center),
                new ProgressBar().Col(1).Minimum(0).Maximum(1).Value(Math.Max(0, fraction)).IsIndeterminate(job.State == JobState.Running && fraction < 0)
                    .Foreground(b.Progress).Margin(8, 0).MinHeight(6),
                new TextBlock().Col(2).Text(state + (metrics.CurrentItem is { } item && job.State == JobState.Running ? "  " + item : string.Empty))
                    .Foreground(job.State == JobState.Failed ? b.Error : b.StatusForeground).MaxWidth(360).TextTrimming(TextTrimming.CharacterEllipsis)
                    .VerticalAlignment(VerticalAlignment.Center),
                new Button().Col(3).Content("✕").Padding(6, 0).Margin(8, 0, 0, 0).IsVisible(job.State is JobState.Queued or JobState.Running)
                    .OnClick(_ => handle.Cancel())));
        }

        _jobsBar.IsVisible = jobs.Count > 0;
        if (!anyRunning && jobs.Count > 0)
        {
            // Leave finished jobs visible briefly, then clear them.
            DispatcherTimer.RunOnce(() =>
            {
                _host.Jobs.ClearFinished();
                _jobsDirty = true;
            }, TimeSpan.FromSeconds(4));
        }
    }

    private void SaveState()
    {
        if (_host is null)
        {
            return;
        }

        _host.Settings.LeftPath = _left.ActiveTab.Path;
        _host.Settings.RightPath = _right.ActiveTab.Path;
        try
        {
            // Runs on the thread pool internally, so blocking here cannot deadlock the dispatcher.
            _host.SaveSettingsAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private void RegisterCommands()
    {
        var c = _host!.Commands;
        c.Register(CommandIds.Help, "Lista skrótów klawiszowych", ShowHelpAsync)
         .Register(CommandIds.SwitchPanel, "Przełącz panel", () => SetActive(Other))
         .Register(CommandIds.Open, "Otwórz / wejdź", OpenCurrent)
         .Register(CommandIds.GoToParent, "Katalog wyżej", () => _active.GoToParent())
         .Register(CommandIds.Refresh, "Odśwież", () => _ = _active.ActiveTab.RefreshAsync())
         .Register(CommandIds.NewTab, "Nowa zakładka", () => _active.AddTab(_active.ActiveTab.Path))
         .Register(CommandIds.CloseTab, "Zamknij zakładkę", () => _active.CloseActiveTab())
         .Register(CommandIds.LeftDriveMenu, "Menu dysków (lewy)", _ => ShowDriveMenuAsync(_left))
         .Register(CommandIds.RightDriveMenu, "Menu dysków (prawy)", _ => ShowDriveMenuAsync(_right))
         .Register(CommandIds.OpenNetwork, "Sieć (net://)", () => _ = _active.NavigateAsync(NetworkNeighborhoodVfsPlugin.RootPath))
         .Register(CommandIds.OpenRegistry, "Rejestr (reg://)", () => _ = _active.NavigateAsync("reg://"))
         .Register(CommandIds.AddConnection, "Dodaj połączenie sieciowe", AddConnectionAsync)
         .Register(CommandIds.GoToFrecent, "Częste lokalizacje", GoToFrecentAsync)
         .Register(CommandIds.ToggleSelection, "Zaznacz", ToggleMarkAndAdvance)
         .Register(CommandIds.CalculateDirSizes, "Rozmiar katalogu / zaznacz", CalculateSize)
         .Register(CommandIds.View, "Podgląd", ViewCurrent)
         .Register(CommandIds.QuickView, "Szybki podgląd", TogglePreview)
         .Register(CommandIds.PanelModes, "Tryb panelu (pliki, podgląd, panele wtyczek)", ShowPanelModesAsync)
         .Register(CommandIds.Browser, "Przeglądarka (WWW, wyszukiwarki, asystenci AI)", () => OpenBrowser(null))
         .Register(CommandIds.AskAi, "Zapytaj AI (ChatGPT, Gemini, Claude…) w przeglądarce", _ => new ValueTask(AskAiAsync()))
         .Register(CommandIds.TogglePalettes, "Palety: pokaż / ukryj", _ => new ValueTask(TogglePalettesAsync()))
         .Register(CommandIds.Palettes, "Palety: położenie (szybkie akcje, ulubione, widoki, wtyczki)", () => OpenSettings("paleta"))
         .Register(CommandIds.AddFavorite, "Dodaj bieżący folder do ulubionych", _ => new ValueTask(AddFavoriteAsync()))
         .Register(CommandIds.Edit, "Edycja", EditCurrentAsync)
         .Register(CommandIds.Copy, "Kopiuj", _ => TransferAsync(TransferMode.Copy))
         .Register(CommandIds.Move, "Przenieś", _ => TransferAsync(TransferMode.Move))
         .Register(CommandIds.MakeDirectory, "Nowy katalog", MakeDirectoryAsync)
         .Register(CommandIds.Delete, "Usuń", DeleteAsync)
         .Register(CommandIds.SearchAsync, "Szukaj", OpenSearch)
         .Register(CommandIds.Compare, "Porównaj pliki", CompareAsync)
         .Register(CommandIds.OpenTerminal, "Terminal w bieżącym katalogu", OpenTerminalAsync)
         .Register(CommandIds.CancelCurrentOperation, "Przerwij", CancelCurrent)
         .Register(CommandIds.SelectSkin, "Wybór skórki", _ => new SkinPickerDialog(_host, _skins!).ShowAsync(this).ContinueWith(_ => { }, TaskScheduler.Default).AsValueTask())
         .Register(CommandIds.NextSkin, "Następna skórka", NextSkin)
         .Register(CommandIds.ToggleHidden, "Pokaż / ukryj pliki ukryte", ToggleHiddenAsync)
         .Register(CommandIds.CommandPalette, "Paleta komend", ShowCommandPaletteAsync)
         .Register(CommandIds.Settings, "Ustawienia", () => OpenSettings())
         .Register(CommandIds.CheckForUpdates, "Sprawdź aktualizacje", _ => new ValueTask(CheckForUpdatesNowAsync()))
         .Register(CommandIds.About, "O programie", _ => new ValueTask(new AboutDialog(_skins!.Current).ShowAsync(this)))
         .Register(CommandIds.Exit, "Wyjście", Close);
        c.Freeze();
    }

    /// <summary>File operations make no sense while the active panel shows a plugin (they would hit the hidden file list).</summary>
    private static readonly HashSet<string> FileCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        CommandIds.Open, CommandIds.GoToParent, CommandIds.ToggleSelection, CommandIds.CalculateDirSizes, CommandIds.View,
        CommandIds.Edit, CommandIds.Copy, CommandIds.Move, CommandIds.MakeDirectory, CommandIds.Delete, CommandIds.NewTab, CommandIds.CloseTab,
    };

    /// <summary>Runs any command id (built-in, <c>panel:</c>, <c>plugin:</c>) as if invoked from the keymap; for automation and tests.</summary>
    public Task ExecuteCommandAsync(string commandId) => ExecuteAsync(commandId);

    private async Task ExecuteAsync(string commandId)
    {
        try
        {
            if (commandId.StartsWith(CommandIds.PanelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await OpenPanelAsync(commandId[CommandIds.PanelPrefix.Length..], null);
                return;
            }

            if (commandId.StartsWith(CommandIds.PluginPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var parts = commandId.Split(':', 3);
                if (parts.Length == 3 && _host!.FindCommandPlugin(parts[1]) is { } plugin)
                {
                    await plugin.ExecuteAsync(parts[2], _host.HostFor(plugin.Manifest), CancellationToken.None);
                }
                else
                {
                    Notify($"Brak wtyczki dla „{commandId}”.", isError: true);
                }

                return;
            }

            if (_active.Mode is not null && FileCommands.Contains(commandId))
            {
                Notify("Aktywny panel pokazuje wtyczkę — zamknij go (Esc), aby działać na plikach.");
                return;
            }

            if (!await _host!.Commands.ExecuteAsync(commandId))
            {
                Notify($"Nieznana komenda „{commandId}” w keymap.json.", isError: true);
            }
        }
        catch (Exception ex)
        {
            Notify(ex.Message, isError: true);
        }
    }

    private ValueTask ShowHelpAsync(CommandInvocation _)
    {
        var text = new StringBuilder();
        foreach (var (sequence, id) in _host!.Keymap.Bindings.OrderBy(b => b.Value, StringComparer.Ordinal))
        {
            var description = _host.ListCommands().FirstOrDefault(cmd => cmd.Id == id)?.Description ?? id;
            text.AppendLine($"{sequence,-16} {description}");
        }

        text.AppendLine();
        text.AppendLine($"Konfiguracja: {_host.Config.ConfigDirectory}");
        return new ValueTask(new MessageDialog(_skins!.Current, "Skróty klawiszowe", text.ToString(), monospace: true).ShowAsync(this));
    }

    private void OpenCurrent()
    {
        if (!string.IsNullOrWhiteSpace(_commandLine.Text))
        {
            RunCommandLine();
        }
        else if (_active.ActiveTab.CurrentEntry is { } entry)
        {
            _active.Open(entry);
        }
    }

    private void OpenFile(FilePanelView panel, VfsEntry entry)
    {
        if (VfsPath.GetScheme(entry.Path) is VfsPath.FileScheme or VfsPath.SmbScheme)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    using var _ = Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(entry.Path) ?? string.Empty });
                }
                catch (Win32Exception ex)
                {
                    Dispatcher.UIThread.Post(() => Notify(ex.Message, isError: true));
                }
            });
        }
        else
        {
            new ViewerWindow(_host!, _skins!.Current, entry).Show(this);
        }
    }

    private void ViewCurrent()
    {
        if (_active.ActiveTab.CurrentEntry is not { } entry)
        {
            return;
        }

        if (entry.IsContainer)
        {
            CalculateSize();
        }
        else if (_host!.Viewer.CanView(entry))
        {
            new ViewerWindow(_host, _skins!.Current, entry).Show(this);
        }
    }

    private void ToggleMarkAndAdvance()
    {
        var tab = _active.ActiveTab;
        if (tab.CurrentEntry is { } entry)
        {
            tab.ToggleMark(entry);
            tab.MoveCursor(tab.Cursor + 1);
            _active.Canvas.EnsureCursorVisible();
        }
    }

    /// <summary>Space: Total Commander marks the entry and, for directories, computes its size in the background.</summary>
    private void CalculateSize()
    {
        var tab = _active.ActiveTab;
        if (tab.CurrentEntry is not { } entry)
        {
            return;
        }

        if (entry.IsContainer && entry.Kind is not (VfsEntryKind.ParentLink or VfsEntryKind.Host))
        {
            _ = tab.CalculateSizeAsync(entry);
        }

        tab.ToggleMark(entry);
    }

    private async ValueTask EditCurrentAsync(CommandInvocation _)
    {
        if (_active.ActiveTab.CurrentEntry is not { Kind: VfsEntryKind.File } entry || !IsFileSystemPath(entry.Path))
        {
            Notify("Edycja działa dla plików na dyskach i udziałach sieciowych.");
            return;
        }

        var result = await _host!.Tools.EditAsync(entry.Path);
        if (!result.Launched)
        {
            Notify(result.Error ?? "Nie udało się uruchomić edytora.", isError: true);
        }
    }

    private async ValueTask CompareAsync(CommandInvocation _)
    {
        if (_left.ActiveTab.CurrentEntry is not { Kind: VfsEntryKind.File } left
            || _right.ActiveTab.CurrentEntry is not { Kind: VfsEntryKind.File } right)
        {
            Notify("Ustaw kursor na pliku w obu panelach, aby je porównać.");
            return;
        }

        var result = await _host!.Tools.DiffAsync(left.Path, right.Path);
        if (!result.Launched)
        {
            Notify(result.Error ?? "Nie znaleziono narzędzia do porównywania.", isError: true);
        }
    }

    private async ValueTask OpenTerminalAsync(CommandInvocation _)
    {
        var path = _active.ActiveTab.Path;
        var directory = IsFileSystemPath(path) ? LocalFileSystemPlugin.ToLocalPath(path) ?? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await _host!.Tools.OpenTerminalAsync(directory);
        if (!result.Launched)
        {
            Notify(result.Error ?? "Nie znaleziono terminala.", isError: true);
        }
    }

    private async ValueTask TransferAsync(TransferMode mode)
    {
        var sources = _active.ActiveTab.MarkedOrCurrent();
        if (sources.Count == 0)
        {
            return;
        }

        var target = Other.ActiveTab.Path;
        var request = await new TransferDialog(_skins!.Current, mode == TransferMode.Move, DescribeSelection(sources), target).ShowAsync(this);
        if (request is null)
        {
            return;
        }

        var job = new VfsTransferJob(_host!.Router, sources, request.Target, mode, request.Overwrite);
        RunJob(job, request.Queue ? JobQueueManager.DefaultQueue : null, refreshBoth: true);
        _active.ActiveTab.Marked.Clear();
    }

    private async ValueTask MakeDirectoryAsync(CommandInvocation _)
    {
        var tab = _active.ActiveTab;
        var name = await new InputDialog(_skins!.Current, "Nowy katalog", $"Utwórz katalog w {tab.Path}:").ShowAsync(this);
        if (name is null)
        {
            return;
        }

        try
        {
            await _host!.Router.CreateDirectoryAsync(_host.Router.Combine(tab.Path, name), CancellationToken.None);
            await tab.NavigateAsync(tab.Path, name);
        }
        catch (Exception ex)
        {
            Notify(ex.Message, isError: true);
        }
    }

    private async ValueTask DeleteAsync(CommandInvocation _)
    {
        var targets = _active.ActiveTab.MarkedOrCurrent();
        if (targets.Count == 0)
        {
            return;
        }

        if (_host!.Settings.ConfirmDelete && !await new ConfirmDialog(_skins!.Current, "Usuń", $"Usunąć {DescribeSelection(targets)}? Tej operacji nie można cofnąć.", "Usuń").ShowAsync(this))
        {
            return;
        }

        RunJob(new VfsDeleteJob(_host!.Router, targets), queue: null, refreshBoth: true);
        _active.ActiveTab.Marked.Clear();
    }

    private void RunJob(IJob job, string? queue, bool refreshBoth)
    {
        var handle = _host!.Jobs.Enqueue(job, queue);
        _jobsDirty = true;
        _ = handle.Completion.ContinueWith(
            completed => Dispatcher.UIThread.Post(() =>
            {
                if (handle.State == JobState.Failed)
                {
                    Notify($"{handle.Title}: {handle.Error?.Message}", isError: true);
                }

                if (refreshBoth)
                {
                    _ = _left.ActiveTab.RefreshAsync();
                    _ = _right.ActiveTab.RefreshAsync();
                }
            }),
            TaskScheduler.Default);
    }

    private void OpenSearch()
    {
        var panel = _active;
        new SearchWindow(_host!, _skins!.Current, panel.ActiveTab.Path, entry =>
        {
            var parent = _host!.Router.GetParentPath(entry.Path);
            if (entry.IsContainer)
            {
                _ = panel.NavigateAsync(entry.Path);
            }
            else if (parent is not null)
            {
                _ = panel.NavigateAsync(parent, entry.Name);
            }

            Activate();
        }).Show(this);
    }

    private async ValueTask ShowDriveMenuAsync(FilePanelView panel)
    {
        SetActive(panel);
        var items = new List<PickerItem>();
        await foreach (var drive in _host!.Router.EnumerateAsync(LocalFileSystemPlugin.RootPath, CancellationToken.None))
        {
            items.Add(new PickerItem(drive.Name, drive.Description, drive.Path));
        }

        items.Add(new PickerItem("Sieć", "net:// – hosty w sieci LAN, udziały SMB, SFTP/FTP", NetworkNeighborhoodVfsPlugin.RootPath));
        items.Add(new PickerItem("Rejestr Windows", "reg://", "reg://"));
        items.AddRange(_host.PanelPlugins.Select(p => new PickerItem("Panel: " + p.Title, p.Description, CommandIds.PanelPrefix + p.Id)));
        items.AddRange(_host.Settings.Connections.Select(c => new PickerItem(c.Name, c.Path, c.Path)));

        var choice = await new PickerDialog(_skins!.Current, "Zmień dysk", items).ShowAsync(this);
        if (choice is null)
        {
            return;
        }

        if (choice.Value.StartsWith(CommandIds.PanelPrefix, StringComparison.Ordinal))
        {
            await ExecuteAsync(choice.Value);
            return;
        }

        panel.CloseMode();
        await panel.NavigateAsync(choice.Value);
    }

    /// <summary>
    /// Opens a panel plugin in the opposite panel (like Total Commander's quick view), so the file list the user is in
    /// stays visible and remains the plugin's context; a panel already showing a plugin is reused.
    /// File-bound panels (log analysis) get the file under the cursor as their argument.
    /// </summary>
    private Task OpenPanelAsync(string pluginId, string? argument) => OpenPanelInAsync(pluginId, argument, null);

    /// <param name="target">Panel to show the plugin in; <c>null</c> = the usual mode target (see <see cref="ModeTarget"/>).</param>
    private Task OpenPanelInAsync(string pluginId, string? argument, FilePanelView? target)
    {
        var host = _host!;
        if (host.FindPanel(pluginId) is not { } plugin)
        {
            Notify($"Nie znaleziono panelu „{pluginId}”.", isError: true);
            return Task.CompletedTask;
        }

        target ??= ModeTarget().Target;
        var source = target == _left ? _right : _left;
        if (plugin.RequiresArgument)
        {
            if (argument is null && source.Mode is null && source.ActiveTab.CurrentEntry is { Kind: VfsEntryKind.File } file)
            {
                argument = file.Path;
            }

            if (argument is null)
            {
                Notify($"{plugin.Title}: ustaw kursor na pliku i spróbuj ponownie.", isError: true);
                return Task.CompletedTask;
            }
        }

        var request = new PanelRequest(argument, null);
        var services = new PluginPanelServices(
            (title, message) => new ConfirmDialog(_skins!.Current, title, message).ShowAsync(this),
            ShowTextWindowAsync,
            Notify);
        target.ShowMode(new PluginPanelView(plugin, plugin.CreateSession(request, host.HostFor(plugin.Manifest)), request, _skins!.Current, services));
        SetActive(target);
        return Task.CompletedTask;
    }

    /// <summary>The panel a mode goes to: the opposite one, or the active one if it already shows a mode.</summary>
    private (FilePanelView Target, FilePanelView Source) ModeTarget() =>
        _active.Mode is null ? (Other, _active) : (_active, Other);

    /// <summary>Ctrl+Q: quick view of the entry under the cursor, in the opposite panel; focus stays on the file list.</summary>
    private void TogglePreview()
    {
        var (target, source) = ModeTarget();
        if (target.Mode is PreviewPanelView)
        {
            target.CloseMode();
            return;
        }

        target.ShowMode(new PreviewPanelView(_host!, source, _skins!.Current, Notify));
        SetActive(source);
    }

    /// <summary>F2: choose what the opposite panel shows — files, quick view or any plugin panel.</summary>
    private async ValueTask ShowPanelModesAsync(CommandInvocation _)
    {
        var host = _host!;
        var (target, _) = ModeTarget();
        var side = target == _left ? "lewy" : "prawy";
        var items = new List<PickerItem>
        {
            new("Pliki", "lista plików (zamyka tryb panelu)", "files", "lista katalog"),
            new("Podgląd (Ctrl+Q)", "markdown z edycją, tekst, obrazy, rozmiary folderów — dla pliku pod kursorem", "preview", "quick view markdown"),
            new("Przeglądarka (Ctrl+Alt+W)", "strony WWW, wyszukiwarki, ChatGPT / Gemini / Claude — logowanie jest zapamiętywane", "browser", "www internet web ai"),
        };
        items.AddRange(host.PanelPlugins.Select(p => new PickerItem(p.Title, p.Description, CommandIds.PanelPrefix + p.Id, p.Manifest.Name)));

        var choice = await new PickerDialog(_skins!.Current, $"Tryb panelu ({side})", items, $"Co ma pokazywać {side} panel?").ShowAsync(this);
        switch (choice?.Value)
        {
            case null:
                return;
            case "files":
                target.CloseMode();
                SetActive(target);
                break;
            case "preview":
                if (target.Mode is not PreviewPanelView)
                {
                    TogglePreview();
                }

                break;
            case "browser":
                OpenBrowser(null, target);
                break;
            default:
                await ExecuteAsync(choice.Value);
                break;
        }
    }

    private Task ShowTextWindowAsync(string title, string text)
    {
        new TextWindow(_skins!.Current, title, text).Show(this);
        return Task.CompletedTask;
    }

    /// <summary>Menu bar generated from the command list, so plugins and key bindings show up automatically.</summary>
    private void RebuildMenu()
    {
        var host = _host!;
        var all = host.ListCommands().ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        MenuItem Item(string commandId, string? header = null)
        {
            var item = new MenuItem { Header = header ?? all.GetValueOrDefault(commandId)?.Description ?? commandId };
            var sequence = host.KeyBindings.FirstOrDefault(b => string.Equals(b.Value, commandId, StringComparison.OrdinalIgnoreCase)).Key;
            if (sequence is not null && !sequence.Contains(' ', StringComparison.Ordinal))
            {
                try
                {
                    item.InputGesture = KeyGesture.Parse(sequence);
                }
                catch (ArgumentException)
                {
                    // Not expressible as an Avalonia gesture (e.g. "Ctrl+,"); the binding still works.
                }
            }

            item.Click += (_, _) => _ = ExecuteAsync(commandId);
            return item;
        }

        MenuItem Group(string header, IEnumerable<object> children)
        {
            var group = new MenuItem { Header = header };
            foreach (var child in children)
            {
                group.Items.Add(child);
            }

            return group;
        }

        var pluginMenu = new MenuItem { Header = "_Wtyczki" };
        foreach (var command in all.Values.Where(c => c.Category is not null && c.Id.StartsWith(CommandIds.PluginPrefix, StringComparison.Ordinal)))
        {
            // "PowerShell/Sieć" → Wtyczki ▸ PowerShell ▸ Sieć ▸ command.
            var parent = pluginMenu;
            foreach (var level in command.Category!.Split('/'))
            {
                var existing = parent.Items.OfType<MenuItem>().FirstOrDefault(m => Equals(m.Header, level));
                if (existing is null)
                {
                    existing = new MenuItem { Header = level };
                    parent.Items.Add(existing);
                }

                parent = existing;
            }

            parent.Items.Add(Item(command.Id, command.Description[(command.Description.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]));
        }

        pluginMenu.Items.Add(new Separator());
        var blocked = host.DiscoveredPlugins.Count(p => host.PluginBlocker(p) is not null);
        pluginMenu.Items.Add(Item(CommandIds.PanelPrefix + PluginManagerPanel.PluginId,
            blocked > 0 ? $"Menedżer wtyczek… ({blocked} nieaktywnych)" : "Menedżer wtyczek…"));

        _menu.Items.Clear();
        _menu.Items.Add(Group("_Plik", [Item(CommandIds.Copy), Item(CommandIds.Move), Item(CommandIds.MakeDirectory), Item(CommandIds.Delete), new Separator(),
            Item(CommandIds.NewTab), Item(CommandIds.CloseTab), new Separator(), Item(CommandIds.Exit)]));
        _menu.Items.Add(Group("P_olecenia", [Item(CommandIds.SearchAsync), Item(CommandIds.GoToFrecent), Item(CommandIds.CalculateDirSizes), Item(CommandIds.Compare),
            Item(CommandIds.OpenTerminal), Item(CommandIds.Refresh), Item(CommandIds.ToggleHidden), new Separator(), Item(CommandIds.AddConnection)]));
        _menu.Items.Add(Group("Pa_nele", [Item(CommandIds.LeftDriveMenu), Item(CommandIds.RightDriveMenu), Item(CommandIds.OpenNetwork), Item(CommandIds.OpenRegistry), new Separator(),
            .. host.PanelPlugins.Select(p => Item(CommandIds.PanelPrefix + p.Id, p.Title))]));
        _menu.Items.Add(pluginMenu);
        _menu.Items.Add(Group("_Konfiguracja", [Item(CommandIds.Settings), Item(CommandIds.SelectSkin), Item(CommandIds.NextSkin), Item(CommandIds.CommandPalette)]));
        _menu.Items.Add(Group("Pomo_c", [Item(CommandIds.Help), Item(CommandIds.CheckForUpdates), Item(CommandIds.About)]));
        if (_skins is not null)
        {
            ApplyMenuSkin(_skins.Current);
        }
    }

    private void ApplyMenuSkin(SkinBrushes b)
    {
        _menu.Background = b.HeaderBackground;
        _menu.Foreground = b.HeaderForeground;
        _menu.FontFamily = b.FontFamily;
    }

    private async ValueTask GoToFrecentAsync(CommandInvocation _)
    {
        if (_host!.Index is not { } index)
        {
            Notify("Baza częstych lokalizacji jest niedostępna.", isError: true);
            return;
        }

        var frecent = await index.GetFrecentAsync(null, 60);
        var items = frecent.Select(f => new PickerItem(f.Path, $"{f.Visits}× · ostatnio {f.LastVisit.ToLocalTime():g}", f.Path)).ToList();
        var choice = await new PickerDialog(_skins!.Current, "Częste lokalizacje (Ctrl+G)", items, "Najczęściej i najświeżej odwiedzane katalogi:").ShowAsync(this);
        if (choice is not null)
        {
            await _active.NavigateAsync(choice.Value);
        }
    }

    private async ValueTask AddConnectionAsync(CommandInvocation _)
    {
        var connection = await new AddConnectionDialog(_skins!.Current).ShowAsync(this);
        if (connection is null)
        {
            return;
        }

        _host!.Settings.Connections.Add(connection);
        _host.ApplySettings();
        await _host.SaveSettingsAsync();
        if (_active.ActiveTab.Path.StartsWith(NetworkNeighborhoodVfsPlugin.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            await _active.ActiveTab.RefreshAsync();
        }
    }

    private void CancelCurrent()
    {
        var tab = _active.ActiveTab;
        if (tab.IsLoading)
        {
            tab.CancelLoading();
        }
        else if (_host!.Keymap.IsChordPending)
        {
            _host.Keymap.CancelChord();
        }
        else if (!string.IsNullOrEmpty(_commandLine.Text))
        {
            _commandLine.Text = string.Empty;
        }
        else if (tab.Marked.Count > 0)
        {
            tab.Marked.Clear();
            tab.MoveCursor(tab.Cursor);
        }
    }

    private void NextSkin()
    {
        var next = _host!.Skins.Next(_skins!.Current.Skin.Id);
        _skins.Apply(next);
        Notify($"Skórka: {next.DisplayName}");
    }

    private async ValueTask ToggleHiddenAsync(CommandInvocation _)
    {
        _host!.Settings.ShowHidden = !_host.Settings.ShowHidden;
        Notify(_host.Settings.ShowHidden ? "Pokazuję pliki ukryte." : "Ukrywam pliki ukryte i systemowe.");
        await _host.SaveSettingsAsync();
        await Task.WhenAll(_left.ActiveTab.RefreshAsync(), _right.ActiveTab.RefreshAsync());
    }

    private void RunCommandLine()
    {
        var command = _commandLine.Text!.Trim();
        _commandLine.Text = string.Empty;
        Focus();

        if (command.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || command.Equals("cd..", StringComparison.OrdinalIgnoreCase))
        {
            var target = command.Equals("cd..", StringComparison.OrdinalIgnoreCase) ? ".." : command[3..].Trim();
            if (target == "..")
            {
                _active.GoToParent();
            }
            else
            {
                var path = VfsPath.GetScheme(target) is not null ? target : _host!.Router.Combine(_active.ActiveTab.Path, target);
                _ = _active.NavigateAsync(path);
            }

            return;
        }

        var current = _active.ActiveTab.Path;
        var directory = IsFileSystemPath(current) ? LocalFileSystemPlugin.ToLocalPath(current) : null;
        _ = Task.Run(() =>
        {
            try
            {
                var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, WorkingDirectory = directory ?? string.Empty };
                info.ArgumentList.Add("/k");
                info.ArgumentList.Add(command);
                using var _ = Process.Start(info);
            }
            catch (Win32Exception ex)
            {
                Dispatcher.UIThread.Post(() => Notify(ex.Message, isError: true));
            }
        });
    }

    /// <summary>Credentials for SMB shares (WNetAddConnection2) or SFTP/FTP sessions, then retries the listing.</summary>
    private async Task LoginAndRetryAsync(AuthenticationRequest request)
    {
        var ex = request.Exception;
        var realm = ex.Realm ?? ex.Path;
        string? user = null;
        var at = realm.IndexOf('@');
        if (realm.Contains("://", StringComparison.Ordinal) && at > 0)
        {
            user = realm[(realm.IndexOf("://", StringComparison.Ordinal) + 3)..at];
        }

        var credential = await new LoginDialog(_skins!.Current, realm, string.IsNullOrEmpty(user) ? null : user).ShowAsync(this);
        if (credential is null)
        {
            return;
        }

        try
        {
            if (VfsPath.GetScheme(ex.Path) is "sftp" or "ftp")
            {
                _host!.Remote.SetCredentials(realm, credential);
            }
            else
            {
                await _host!.Executor.RunAsync(
                    _ =>
                    {
                        _host.Network.Connect(realm, credential.UserName, credential.Password);
                        return true;
                    },
                    CancellationToken.None,
                    TimeSpan.FromSeconds(30));
            }

            await request.Tab.NavigateAsync(request.Tab.Path);
        }
        catch (Exception error)
        {
            Notify($"Logowanie nieudane: {error.Message}", isError: true);
        }
    }

    /// <summary>Called on SSH.NET's connection thread: blocks it (never the UI thread) while the user decides.</summary>
    private bool ValidateHostKey(string host, int port, string fingerprint)
    {
        var known = _host!.KnownHosts;
        var status = known.Check(host, port, fingerprint);
        if (status == HostKeyStatus.Trusted)
        {
            return true;
        }

        var trusted = Dispatcher.UIThread
            .InvokeAsync(() => new HostKeyDialog(_skins!.Current, host, port, fingerprint, status == HostKeyStatus.Mismatch).ShowAsync(this))
            .GetAwaiter()
            .GetResult();
        if (trusted)
        {
            known.Trust(host, port, fingerprint);
        }

        return trusted;
    }

    private static bool IsFileSystemPath(string path) => VfsPath.GetScheme(path) is VfsPath.FileScheme or VfsPath.SmbScheme;

    private static string DescribeSelection(IReadOnlyList<VfsEntry> entries) =>
        entries.Count == 1 ? $"„{entries[0].Name}”" : $"{entries.Count} zaznaczonych elementów";
}

internal static class TaskExtensions
{
    public static ValueTask AsValueTask(this Task task) => new(task);
}
