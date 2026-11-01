using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Commander4K.App.Ai;
using Commander4K.App.Mcp;
using Commander4K.Core.Mcp;
using Commander4K.App.Browser;
using Commander4K.App.Connections;
using Commander4K.App.Composition;
using Commander4K.App.Dialogs;
using Commander4K.App.Input;
using Commander4K.App.Panels;
using Commander4K.App.PluginPanels;
using Commander4K.App.Preview;
using Commander4K.App.Theming;
using Commander4K.Core.Configuration;
using Commander4K.Core.Folders;
using Commander4K.Core.Input;
using Commander4K.Core.Jobs;
using Commander4K.Core.Localization;
using Commander4K.Core.Security;
using Commander4K.Core.Updates;
using Commander4K.Core.Web;
using Commander4K.Plugins.BuiltIn.Archives;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Network;
using Commander4K.Plugins.BuiltIn.Remote;
using Commander4K.PluginSdk;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Commander4K.App;

/// <summary>
/// Dual-pane shell. The window shows immediately; <see cref="AppHost"/> (config, SQLite, plugins) is created in the
/// background and the panels are built once it is ready.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly (string Key, string Label, string Command)[] FunctionKeys =
    [
        ("1", Loc.T("Ustawienia"), CommandIds.Settings),
        ("2", Loc.T("Panele"), CommandIds.PanelModes),
        ("3", Loc.T("Podgląd"), CommandIds.View),
        ("4", Loc.T("Edycja"), CommandIds.Edit),
        ("5", Loc.T("Kopiuj"), CommandIds.Copy),
        ("6", Loc.T("Przenieś"), CommandIds.Move),
        ("7", Loc.T("Katalog"), CommandIds.MakeDirectory),
        ("8", Loc.T("Usuń"), CommandIds.Delete),
        ("9", "Terminal", CommandIds.OpenTerminal),
        ("10", Loc.T("Wyjście"), CommandIds.Exit),
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
    private string? _notificationTarget;
    private PluginHostAdapter? _pluginHost;
    private Menu _menu = null!;
    private Grid _menuBar = null!;
    private Button _paletteButton = null!;

    /// <summary>Saved window state, read by <c>Program</c> before the window exists; <c>null</c> = first run → maximized.</summary>
    public static WindowPlacement? StartupPlacement { get; set; }

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the shell is built (or start-up failed and shows its error); the splash waits for it.</summary>
    public Task Started => _started.Task;

    private PixelPoint? _normalPosition;
    private Size _normalSize = new(1360, 860);
    private bool _maximizedBeforeMinimize;

    /// <summary>First run opens maximized; later runs restore the last size, position (if still on a screen) and state.</summary>
    private void ApplyPlacement(WindowPlacement? placement)
    {
        if (placement is null)
        {
            WindowState = WindowState.Maximized;
            _maximizedBeforeMinimize = true;
            return;
        }

        if (placement.Width >= MinWidth && placement.Height >= MinHeight)
        {
            Width = placement.Width;
            Height = placement.Height;
            _normalSize = new Size(placement.Width, placement.Height);
        }

        // A monitor may have been unplugged since: only reuse the position when a screen still contains it.
        if (placement is { X: { } x, Y: { } y } && Screens.ScreenFromPoint(new PixelPoint(x + 40, y + 20)) is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            _normalPosition = Position;
        }

        WindowState = placement.Maximized ? WindowState.Maximized : WindowState.Normal;
        _maximizedBeforeMinimize = placement.Maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty && WindowState == WindowState.Normal && change.NewValue is Size size)
        {
            _normalSize = size;
        }
        else if (change.Property == WindowStateProperty && change.NewValue is WindowState state && state != WindowState.Minimized)
        {
            _maximizedBeforeMinimize = state == WindowState.Maximized;
        }
    }

    /// <summary>What to save on exit: the restored bounds (even when maximized) and whether it was maximized.</summary>
    private WindowPlacement CapturePlacement() => new()
    {
        Maximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _maximizedBeforeMinimize),
        X = _normalPosition?.X,
        Y = _normalPosition?.Y,
        Width = Math.Round(_normalSize.Width),
        Height = Math.Round(_normalSize.Height),
    };

    public MainWindow()
    {
        Title = ProcessElevation.IsAdministrator
            ? Loc.T("Commander4K {0} — Administrator", AppVersion.Display)
            : $"Commander4K {AppVersion.Display}";
        Width = 1360;
        Height = 860;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyPlacement(StartupPlacement);
        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _normalPosition = e.Point;
            }
        };
        Content = new TextBlock()
            .Text(Loc.T("Commander4K — uruchamianie…"))
            .HorizontalAlignment(HorizontalAlignment.Center)
            .VerticalAlignment(VerticalAlignment.Center);

        _jobsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => RefreshJobsBar());
        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            _notification.Text = string.Empty;
            _notificationTarget = null;
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await StartAsync();
        Closing += (_, _) =>
        {
            SaveState();
            StopMcp();
        };
    }

    /// <summary>For tests and screenshots: builds the shell around an existing host without the async start-up.</summary>
    public void Attach(AppHost host)
    {
        _host = host;
        _skins = new SkinManager(host, host.Skins.Get(host.Settings.Skin));
        BuildShell();
        _started.TrySetResult();
        _mcp = new McpHost(host, this);
        host.Mcp = _mcp;
        _mcp.Changed += UpdateMcpBadge;
        _ = ApplyMcpAsync();
    }

    /// <summary>The MCP server (Claude Code); <c>null</c> before <see cref="Attach"/>.</summary>
    internal McpHost? Mcp => _mcp;

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
            Content = new SelectableTextBlock().Text(Loc.T("Nie udało się uruchomić Commander4K:\n\n") + ex).Margin(20).TextWrapping(TextWrapping.Wrap);
            _started.TrySetResult();
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
            p.FolderEntering += entry => _ = EnterFolderAsync(p, entry);
            p.ContextMenuRequested += entry => ShowContextMenu(p, entry, fromKeyboard: false);
            p.ActionRequested += entry =>
            {
                SetActive(p);
                _ = entry.Path switch
                {
                    NetworkNeighborhoodVfsPlugin.AddLanHostPath => AddLanHostAsync(),
                    NetworkNeighborhoodVfsPlugin.ScanLanPath => ConfigureLanScanAsync(),
                    _ => ExecuteAsync(CommandIds.AddConnection),
                };
            };
            p.NewTabRequested += () => _ = ChooseNewTabAsync(p);
            p.PluginTabSelected += tab => ShowPluginTab(p, tab);
            p.LocationMenuRequested += sender => _ = ShowDriveMenuAsync(sender).AsTask();
            p.ModeRequested += request => SetPanelMode(request.Panel, request.Preview);
            p.ConfigureEmptyFolderRequested += folderPath =>
            {
                SetActive(p);
                _ = ConfigureFolderViewAsync(folderPath, p);
            };
            p.AuthenticationRequired += request => _ = LoginAndRetryAsync(request);
            WatchForSuggestions(p);
            p.Canvas.ColumnWidthsChanged += () =>
            {
                // Both panels share the widths.
                _left.Canvas.InvalidateVisual();
                _right.Canvas.InvalidateVisual();
                _ = host.SaveSettingsAsync();
            };
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
        host.InstallFromCatalog = InstallFromCatalogAsync;
        host.PowerShell.CommandsChanged += () => Dispatcher.UIThread.Post(RebuildMenu);
        host.PluginsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            RebuildMenu();
            RegisterColumnSettings();
        });
        _menu = new Menu();

        // Design system: panels are cards 6 px apart on the window background.
        var shell = new Grid()
            .Rows("*,Auto,Auto,Auto")
            .Children(
                new Grid()
                    .Row(0)
                    .Cols("*,6,*")
                    .Children(
                        _left.Col(0).Margin(6, 0, 0, 0),
                        new GridSplitter().Ref(out _splitter).Col(1).ResizeDirection(GridResizeDirection.Columns),
                        _right.Col(2).Margin(0, 0, 6, 0)),
                new Border()
                    .Ref(out _jobsBar)
                    .Row(1)
                    .IsVisible(false)
                    .Margin(6, 6, 6, 0)
                    .Padding(12, 4)
                    .MinHeight(34)
                    .Child(new StackPanel().Ref(out _jobsList).Spacing(4).VerticalAlignment(VerticalAlignment.Center)),
                new Border()
                    .Ref(out _commandBar)
                    .Row(2)
                    .Margin(0, 6, 0, 0)
                    .Height(30)
                    .Padding(12, 0)
                    .Child(new Grid().Cols("Auto,*,Auto,Auto").Children(
                        new TextBlock().Ref(out _prompt).VerticalAlignment(VerticalAlignment.Center),
                        new TextBox().Ref(out _commandLine).Col(1).BorderThickness(new Thickness(0)).MinHeight(24).Padding(8, 2)
                            .VerticalContentAlignment(VerticalAlignment.Center),
                        new TextBlock().Ref(out _notification).Col(2).VerticalAlignment(VerticalAlignment.Center).Margin(8, 0)
                            .MaxWidth(520).TextTrimming(TextTrimming.CharacterEllipsis),
                        new Button().Ref(out _mcpBadge).Col(3).IsVisible(false).Height(22).Padding(8, 0).Margin(4, 0, 0, 0)
                            .VerticalAlignment(VerticalAlignment.Center).VerticalContentAlignment(VerticalAlignment.Center)
                            .OnClick(click => OpenMcpConnect()))),
                _keyBar.Row(3));

        // Menu bar with the command-palette search box on the right (design system).
        _paletteButton = new Button()
            .Width(300)
            .Height(24)
            .Padding(10, 0, 4, 0)
            .Margin(0, 0, 6, 0)
            .VerticalAlignment(VerticalAlignment.Center)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .OnClick(click => _ = ExecuteAsync(CommandIds.CommandPalette))
            .Content(new Grid().Cols("Auto,*,Auto").Children(
                new TextBlock().Text("⌕").FontSize(14).VerticalAlignment(VerticalAlignment.Center).Margin(0, 0, 8, 0),
                new TextBlock().Col(1).Text(Loc.T("Paleta komend, pliki, ustawienia…")).FontSize(12).VerticalAlignment(VerticalAlignment.Center)
                    .TextTrimming(TextTrimming.CharacterEllipsis),
                new Border().Col(2).Padding(6, 1).CornerRadius(new CornerRadius(4)).BorderThickness(new Thickness(1))
                    .Child(new TextBlock().Text("Ctrl+Shift+P").FontSize(11))));
        _menuBar = new Grid().Cols("*,Auto").Height(32).Children(_menu.VerticalAlignment(VerticalAlignment.Center), _paletteButton.Col(1));
        DockPanel.SetDock(_menuBar, Dock.Top);
        Content = new DockPanel().Children(_menuBar, shell);

        RegisterCommands();
        RebuildMenu();
        RegisterColumnSettings();
        host.Jobs.JobChanged += _ => Dispatcher.UIThread.Post(() => _jobsDirty = true);
        host.Remote.HostKeyValidator = ValidateHostKey;
        host.MailPasswordPrompt = AskMailPasswordAsync;
        host.MailCertificatePrompt = ConfirmMailCertificateAsync;
        // Mermaid diagrams in previews and the AI chat render in a hidden WebView2 parented to this window.
        MermaidRenderer.Configure(() => TryGetPlatformHandle()?.Handle ?? 0, host.DataDirectory);
        _jobsTimer.Start();
        skins.Changed += ApplySkin;
        ApplySkin(skins.Current);
        SetActive(_left);
        if (host.Settings.LeftPath is null && host.Settings.RightPath is null)
        {
            _right.ShowMode(CreatePreview(_left));
        }

        ApplyColumns();

        _notification.PointerPressed += async (_, _) =>
        {
            if (_pendingUpdate is { } update)
            {
                await new UpdateDialog(host, skins.Current, update).ShowAsync(this);
            }
            else if (_notificationTarget is { } target)
            {
                _notificationTarget = null;
                _notification.Text = string.Empty;
                await OpenTileAsync(new PreviewTile(string.Empty, Target: target), null, _active);
            }
        };
        UpdateService.CleanupPreviousVersion(Environment.ProcessPath);
        _ = CheckForUpdatesInBackgroundAsync();
    }

    /// <summary>Silent start-up check; a found release is announced in the status area and opened by clicking it.</summary>
    private async Task CheckForUpdatesInBackgroundAsync()
    {
        var settings = _host!.Settings;
        if (!settings.CheckForUpdates)
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
                Notify(Loc.T("⬆ Dostępna wersja {0} — kliknij, aby zaktualizować", update.Version));
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
        Notify(Loc.T("Sprawdzanie aktualizacji…"));
        try
        {
            var update = await _host.Updates.CheckAsync(settings.UpdateRepository, AppVersion.Current, settings.IncludePrereleases, CancellationToken.None, UpdateService.DetectInstallKind(Environment.ProcessPath));
            settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            await _host.SaveSettingsAsync();
            if (update is null)
            {
                Notify(Loc.T("Masz najnowszą wersję ({0}).", AppVersion.Display));
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
        FollowSkin(window, window.ApplySkin);
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
            case "density":
                _skins!.Refresh();
                break;
            case "columnWidths":
                _left.Canvas.InvalidateVisual();
                _right.Canvas.InvalidateVisual();
                break;
            case "showHidden":
                _ = _left.ActiveTab.RefreshAsync();
                _ = _right.ActiveTab.RefreshAsync();
                break;
            case var column when column.StartsWith(ColumnSettingPrefix, StringComparison.Ordinal):
                ApplyColumns();
                break;
            case var mcp when mcp.StartsWith("mcp", StringComparison.Ordinal):
                // mcp.enabled / mcp.port / mcp.roots and the per-tool modes (mcp:<tool>): new snapshot, restart if needed.
                _ = ApplyMcpAsync();
                break;
        }
    }

    private const string McpSettingPrefix = "mcp:";

    private const string ColumnSettingPrefix = "column:";

    /// <summary>Columns from the settings, in the order they were switched on; unknown ids (removed plugins) are skipped.</summary>
    private IReadOnlyList<ActiveColumn> ActiveColumns()
    {
        var all = _host!.ColumnPlugins.SelectMany(p => p.Columns.Select(c => new ActiveColumn(p, c))).ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        return [.. _host.Settings.Columns.Select(key => all.GetValueOrDefault(key)).OfType<ActiveColumn>()];
    }

    private void ApplyColumns()
    {
        var columns = ActiveColumns();
        _left.Columns = columns;
        _right.Columns = columns;
    }

    /// <summary>Settings created at runtime: one on/off switch per plugin column, so they are searchable like the rest.</summary>
    private void RegisterColumnSettings()
    {
        _host!.ExtraSettings = [.. _host.ColumnPlugins.SelectMany(p => p.Columns.Select(c => new ActiveColumn(p, c))).Select(column => new SettingDescriptor(
            ColumnSettingPrefix + column.Key,
            SettingsCatalog.Columns,
            Loc.T("Kolumna „{0}”", column.Definition.Header),
            Loc.T("{0} Wartości wczytują się w tle, najpierw dla widocznych wierszy.", column.Plugin.Manifest.Description),
            SettingKind.Toggle,
            s => s.Columns.Contains(column.Key, StringComparer.OrdinalIgnoreCase),
            (s, v) =>
            {
                s.Columns.RemoveAll(k => string.Equals(k, column.Key, StringComparison.OrdinalIgnoreCase));
                if (v is true)
                {
                    s.Columns.Add(column.Key);
                }
            },
            ["kolumna", "kolumny", "wdx", column.Definition.Header, column.Plugin.DisplayName])), .. McpToolSettings()];
    }

    /// <summary>
    /// Every MCP tool the app offers (file, plugin and window tools). Definitions only; handlers are never called here,
    /// so this works without a running server.
    /// </summary>
    internal IReadOnlyList<McpToolDefinition> McpToolDefinitions() =>
        [.. McpTools(() => _host!.Settings.Mcp.AllowedRoots, () => McpHost.ProtectedRootsOf(_host!)).Select(t => t.Definition)];

    /// <summary>The tools the MCP server runs (<see cref="McpHost"/>); the settings list the same set.</summary>
    internal IReadOnlyList<McpTool> McpTools(Func<IReadOnlyList<string>> allowedRoots, Func<IReadOnlyList<string>> protectedRoots) =>
    [
        .. McpFileTools.Create(_host!.Router),
        .. McpPluginTools.Create(AiAppContext.SdkClient, Path.Combine(_host.DataDirectory, "plugins"), _host.Audit,
            id => _host.DiscoveredPlugins.Any(p => string.Equals(p.Manifest?.Id, id, StringComparison.OrdinalIgnoreCase))),
        .. McpAppTools.Create(this, _host, AiContext, allowedRoots, protectedRoots),
        .. McpConfigTools.Create(McpConfigContext()),
        .. McpSkinTools.Create(this, _host, () => _skins!),
    ];

    /// <summary>What the configuration tools change: the same settings, key bindings and skins as the settings window.</summary>
    private McpConfigContext McpConfigContext() => new(
        () => _host!.SettingsCatalog,
        _host!.Settings,
        action => Dispatcher.UIThread.InvokeAsync(action),
        async key =>
        {
            OnSettingChanged(key);
            await _host.SaveSettingsAsync();
        },
        () => _host.KeyBindings,
        async bindings =>
        {
            await _host.SaveKeyBindingsAsync(bindings);
            RebuildMenu(); // The menu shows the shortcuts.
        },
        id => _host.ListCommands().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))?.Description,
        _host.Config.ConfigDirectory,
        () => _host.Skins,
        () => _host.Settings.Skin);

    private async Task ApplyMcpAsync()
    {
        try
        {
            await _mcp!.ApplySettingsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(Loc.T("Nie udało się zapisać ustawień MCP: {0}", ex.Message), isError: true);
        }
    }

    /// <summary>Stops the listener on exit so no call starts while the window and the host go away.</summary>
    private void StopMcp()
    {
        if (_mcp is { } mcp)
        {
            _ = mcp.DisposeAsync().AsTask();
        }
    }

    /// <summary>Status bar: hidden while the server is off, the problem when it could not start, otherwise the clients.</summary>
    private void UpdateMcpBadge()
    {
        if (_mcp is not { } mcp || _skins is null)
        {
            return;
        }

        var b = _skins.Current;
        _mcpBadge.IsVisible = mcp.Status is not null;
        _mcpBadge.Content = mcp.Status switch
        {
            McpStartResult.PortBusy => Loc.T("MCP: port {0} zajęty", _host!.Settings.Mcp.Port),
            McpStartResult.Failed => Loc.T("MCP: błąd"),
            _ when mcp.Sessions > 0 => Loc.T("AI podłączone ({0})", mcp.Sessions),
            _ => Loc.T("MCP: gotowy"),
        };
        _mcpBadge.Foreground = mcp.Status is McpStartResult.Started ? (mcp.Sessions > 0 ? b.Accent : b.StatusForeground) : b.Error;
        ToolTip.SetTip(_mcpBadge, mcp.StatusText());
    }

    private void OpenMcpConnect()
    {
        if (_mcpConnect is { } open)
        {
            open.Activate();
            return;
        }

        if (_mcp is null)
        {
            return;
        }

        _mcpConnect = new McpConnectWindow(_host!, _mcp, _skins!.Current, () => _active.ActiveTab.Path);
        FollowSkin(_mcpConnect, _mcpConnect.ApplySkin);
        _mcpConnect.Closed += (sender, args) => _mcpConnect = null;
        _mcpConnect.Show(this);
    }

    private IEnumerable<SettingDescriptor> McpToolSettings() => McpToolDefinitions().Select(tool => new SettingDescriptor(
        McpSettingPrefix + tool.Name,
        SettingsCatalog.Mcp,
        Loc.T("Narzędzie MCP „{0}”", tool.Name),
        Loc.T("Co robi AI z Claude Code, gdy użyje tego narzędzia: zezwalaj, pytaj albo zablokuj. {0}", tool.Description),
        SettingKind.Choice,
        s => McpToolDispatcher.EffectiveMode(tool, s.Mcp.ToolModes.GetValueOrDefault(tool.Name), s.Mcp.ToolModes.ContainsKey(tool.Name)).ToString().ToLowerInvariant(),
        (s, v) => s.Mcp.ToolModes[tool.Name] = (string)v!,
        ["mcp", "claude", "claude code", "ai", tool.Name])
    {
        Choices = () => tool.AlwaysAsk
            ? [new("ask", Loc.T("Pytaj")), new("block", Loc.T("Zablokuj"))]
            : [new("allow", Loc.T("Zezwalaj")), new("ask", Loc.T("Pytaj")), new("block", Loc.T("Zablokuj"))],
    });

    /// <summary>
    /// Right click / Shift+F10: the Windows Explorer menu for files on disks and shares (marked set if the row is
    /// marked, else the row), or the app's own menu for other locations (archives, SFTP, registry).
    /// </summary>
    private void ShowContextMenu(FilePanelView panel, VfsEntry? clicked, bool fromKeyboard)
    {
        var tab = panel.ActiveTab;
        IReadOnlyList<VfsEntry> entries = clicked is { } row && !tab.Marked.Contains(row.Path) ? [row] : tab.MarkedOrCurrent();
        entries = [.. entries.Where(e => e.Kind is not (VfsEntryKind.ParentLink or VfsEntryKind.Action or VfsEntryKind.Service))];
        if (entries.Count == 0 || panel.Mode is not null)
        {
            return;
        }

        int x, y;
        if (fromKeyboard && panel.Canvas.CursorRowScreenPoint() is { } point)
        {
            (x, y) = (point.X, point.Y);
        }
        else
        {
            Shell.ShellContextMenu.GetCursorPos(out var cursor);
            (x, y) = (cursor.X, cursor.Y);
        }

        var paths = entries.Select(e => e.Path).ToList();
        var oneFolder = paths.Select(p => Path.GetDirectoryName(p.TrimEnd('\\'))).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        if (OperatingSystem.IsWindows() && oneFolder && paths.All(Shell.ShellContextMenu.Supports) && TryGetPlatformHandle() is { } handle)
        {
            if (Shell.ShellContextMenu.Show(handle.Handle, paths, x, y, extendedVerbs: Browser.WebView2Host.IsKeyDown(0x10)))
            {
                // Rename, delete, "Send to"… may have changed the folder; the watcher usually catches it, this is a safety net.
                DispatcherTimer.RunOnce(() =>
                {
                    _ = _left.ActiveTab.RefreshAsync();
                    _ = _right.ActiveTab.RefreshAsync();
                }, TimeSpan.FromMilliseconds(600));
            }

            return;
        }

        // Archives, SFTP/FTP, registry: Explorer knows nothing about them, so offer the app's own operations.
        var menu = new ContextMenu();
        foreach (var commandId in new[] { CommandIds.View, CommandIds.QuickView, CommandIds.Edit, CommandIds.Copy, CommandIds.Move, CommandIds.Delete, CommandIds.Pack })
        {
            var id = commandId;
            var item = new MenuItem { Header = _host!.ListCommands().FirstOrDefault(c => c.Id == id)?.Description ?? id };
            item.Click += async (_, _) => await ExecuteAsync(id);
            menu.Items.Add(item);
        }

        menu.Open(panel.Canvas);
    }

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

    private AiChatWindow? _aiChat;
    private AiProvidersWindow? _aiProviders;
    private McpHost? _mcp;
    private McpConnectWindow? _mcpConnect;
    private Button _mcpBadge = null!;

    /// <summary>One chat window; it knows the active folder so questions can refer to "here".</summary>
    private void OpenAiChat()
    {
        if (_aiChat is { } open)
        {
            open.Activate();
            return;
        }

        _aiChat = new AiChatWindow(AiContext, _skins!.Current, OpenAiProviders, OpenPluginManagerAsync);
        FollowSkin(_aiChat, _aiChat.ApplySkin);
        _aiChat.Closed += (_, _) => _aiChat = null;
        _aiChat.Show(this);
    }

    /// <param name="pinned">A folder's own view (c4k.dir "enter") shown in <paramref name="source"/> itself.</param>
    /// <param name="pinnedFile">A file opened with Enter by a preview that asks for it, shown in <paramref name="source"/> itself.</param>
    private PreviewPanelView CreatePreview(FilePanelView source, PreviewPanelView.PinnedFolder? pinned = null, VfsEntry? pinnedFile = null) =>
        new(_host!, source, _skins!.Current, Notify, pinned, pinnedFile)
        {
            ShowUnknownPreviewOptions = entry => ShowUnknownPreviewPromptsAsync(source, entry),
            InstallCatalogPreview = InstallFromCatalogAsync,
            OpenPluginCatalog = () =>
            {
                OpenBrowser("https://github.com/4kprojekt/Commander4K/tree/main/plugins-catalog");
                return Task.CompletedTask;
            },
            ConfigureFolderView = folderPath => ConfigureFolderViewAsync(folderPath, source),
            CreateBrowserPreview = url => new BrowserPanelView(_host!, _host!.HostFor(BrowserPlugin.Instance.Manifest), url, _skins!.Current, Notify, RunShortcut),
            ConfigureSftpConnection = path => ConfigureSftpConnectionAsync(source, path),
            OpenTile = (tile, folder) => OpenTileAsync(tile, folder, source),
            RunShortcut = RunShortcut,
            AskFolderTrust = AskFolderTrustAsync,
            EditDashboardTile = (dashboard, index, edit) => EditDashboardTileAsync(dashboard, index, edit),
        };

    /// <summary>
    /// Enter on a folder: its <c>c4k.dir</c> (when the setting is on) may show a view, open a panel plugin, go somewhere
    /// else (SFTP with a key), open a page or run a script; otherwise, and with Shift+Enter, the folder opens as files.
    /// Only disks, shares and SFTP/FTP are checked — one stat per Enter, abandoned after 2 s on a hung share.
    /// </summary>
    private async Task EnterFolderAsync(FilePanelView panel, VfsEntry folder)
    {
        FolderConfig? config = null;
        if (VfsPath.GetScheme(folder.Path) is VfsPath.FileScheme or VfsPath.SmbScheme or "sftp" or "ftp")
        {
            config = await Task.Run(() => _host!.ReadFolderConfigAsync(folder.Path, CancellationToken.None));
        }

        if (config?.Error is { } error)
        {
            Notify(Loc.T("{0}: {1}", config.FilePath, error), isError: true);
        }

        if (config?.Enter is not { IsPlainFiles: false } action)
        {
            await panel.NavigateAsync(folder.Path);
            return;
        }

        await RunFolderActionAsync(panel, folder, config, action);
    }

    /// <summary>
    /// cm_FolderView: writes a c4k.dir for the folder under the cursor (or the current one) from a short choice — a
    /// view, a catalog plugin, an SSH connection with a key, a web page or a script — for its preview, its Enter or both.
    /// </summary>
    /// <summary>
    /// cm_AddApiCard: a sample request to an API, a pick of fields from its answer, and a card added to the dashboard of
    /// the current folder's c4k.dir. An Authorization value goes to the secret store; the file only names it.
    /// </summary>
    private async ValueTask AddApiCardAsync(CommandInvocation _)
    {
        var folder = _active.ActiveTab.Path;
        if (VfsPath.GetScheme(folder) is not (VfsPath.FileScheme or VfsPath.SmbScheme) || folder.Contains("://", StringComparison.Ordinal))
        {
            Notify(Loc.T("Plik c4k.dir można utworzyć na dysku albo w udziale sieciowym."), isError: true);
            return;
        }

        var brushes = _skins!.Current;
        var url = (await new InputDialog(brushes, Loc.T("Dodaj kartę API"), Loc.T("Adres API (GET):"), "https://").ShowAsync(this))?.Trim();
        if (!Core.Ai.AiInstallInfo.IsWebUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var authorization = (await new InputDialog(brushes, Loc.T("Dodaj kartę API"), Loc.T("Nagłówek Authorization (np. Bearer …; puste = bez):")).ShowAsync(this))?.Trim();
        var origin = Plugins.BuiltIn.Dashboard.ApiCards.Origin(uri);
        string? secretName = null;
        if (!string.IsNullOrEmpty(authorization))
        {
            // One secret per origin; a name already bound elsewhere gets a suffix, so another card's token is never replaced.
            var stem = new string([.. $"{uri.Host}-{uri.Port}".Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
            secretName = await Task.Run(() =>
            {
                var name = stem;
                for (var n = 2; _host!.Secrets.Exists(name) && _host.Secrets.Get(name, origin) is null; n++)
                {
                    name = $"{stem}-{n}";
                }

                return name;
            });
            headers["Authorization"] = "{secret:" + secretName + "}";
        }

        var api = _host!.ApiCards;
        Notify(Loc.T("Pytam {0}…", uri.Host));
        HttpResponseData? sample;
        IReadOnlyList<(string Path, string Preview)> fields = [];
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var sampleHeaders = authorization is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Authorization"] = authorization };
            (sample, fields) = await Task.Run(async () =>
            {
                var response = await _host.HostFor(api.Manifest).HttpRequestAsync(new HttpRequestSpec("GET", uri, sampleHeaders), limit.Token);
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(response.Body);
                    return (response, Plugins.BuiltIn.Dashboard.JsonFieldPath.Flatten(document.RootElement));
                }
                catch (System.Text.Json.JsonException)
                {
                    return (response, (IReadOnlyList<(string, string)>)[]);
                }
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            Notify(Loc.T("Zapytanie nie powiodło się: {0}", ex.Message), isError: true);
            return;
        }

        var choice = await new ApiFieldsDialog(brushes, uri.Host, Loc.T("Odpowiedź: HTTP {0}, {1} znaków.", sample.Status, sample.Body.Length), fields).ShowAsync(this);
        if (choice is null)
        {
            return;
        }

        var tile = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = choice.Title,
            ["card"] = FolderViewIds.Api,
            ["api"] = new System.Text.Json.Nodes.JsonObject
            {
                ["url"] = url,
                ["headers"] = new System.Text.Json.Nodes.JsonObject([.. headers.Select(h => KeyValuePair.Create(h.Key, (System.Text.Json.Nodes.JsonNode?)h.Value))]),
                ["fields"] = new System.Text.Json.Nodes.JsonArray([.. choice.Fields.Select(f => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
                {
                    ["path"] = f.Path,
                    ["label"] = f.Label,
                    ["as"] = f.As.ToString().ToLowerInvariant(),
                })]),
            },
        };
        if (choice.Refresh is { } refresh)
        {
            tile["refresh"] = refresh;
        }

        var file = FolderConfig.ChildPath(folder, FolderConfig.FileName);
        var existing = await Task.Run(() => File.Exists(file) ? File.ReadAllText(file) : null);
        if (existing is not null && FolderConfigWriter.HasComments(existing)
            && !await new ConfirmDialog(brushes, Loc.T("Zapisać c4k.dir?"), Loc.T("{0} ma komentarze; zapis nowej karty je usunie.", file), Loc.T("Zapisz")).ShowAsync(this))
        {
            return;
        }

        var wasTrusted = existing is null || await Task.Run(() => _host.FolderTrust.IsTrusted(FolderConfig.Parse(folder, existing)));
        string text;
        try
        {
            text = FolderConfigWriter.AppendTile(existing, tile, replacePreview: false);
        }
        catch (InvalidOperationException)
        {
            if (!await new ConfirmDialog(brushes, Loc.T("Zamienić podgląd na dashboard?"),
                    Loc.T("{0} pokazuje teraz coś innego. Podgląd stanie się dashboardem z tą kartą; dotychczasowy podgląd zostanie po wejściu do folderu, jeśli nic tam nie ustawiono.", file), Loc.T("Zamień")).ShowAsync(this))
            {
                return;
            }

            text = FolderConfigWriter.AppendTile(existing, tile, replacePreview: true);
        }

        try
        {
            // The folder may be on a share: opening the file must not wait on the UI thread.
            await Task.Run(() => File.WriteAllTextAsync(file, text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify(ex.Message, isError: true);
            return;
        }

        // The token is stored only once the card exists, bound to the origin it was entered for.
        if (secretName is not null)
        {
            await Task.Run(() => _host.Secrets.Set(secretName, authorization!, origin));
        }

        // The user just wrote this file; trust carries over only from a file that was trusted (or did not exist).
        if (wasTrusted)
        {
            await Task.Run(() => _host.FolderTrust.Trust(FolderConfig.Parse(folder, text)));
        }

        Notify(Loc.T("Dodano kartę „{0}” do {1}.", choice.Title, file));
        await _active.ActiveTab.RefreshAsync();
    }

    /// <summary><c>c4k:secret/&lt;name&gt;</c>: asks for a secret an API card needs and binds it to the origin (scheme, host, port) of that card's request.</summary>
    private async Task AskSecretAsync(string name, VfsEntry? folder)
    {
        var config = folder is { } f ? await Task.Run(() => _host!.ReadFolderConfigAsync(f.Path, CancellationToken.None)) : null;
        var request = config is null ? null
            : new[] { config.Enter, config.Preview }.Where(a => a is not null).SelectMany(a => a!.Tiles.Select(t => t.Api).Append(a.Api))
                .FirstOrDefault(r => r is not null && (Core.Security.SecretStore.NamesIn(r.Url).Contains(name)
                    || r.Headers.Values.Any(v => Core.Security.SecretStore.NamesIn(v).Contains(name))
                    || (r.Body is { } body && Core.Security.SecretStore.NamesIn(body).Contains(name))));
        if (request is null || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            Notify(Loc.T("Nie znaleziono zapytania, które używa sekretu „{0}”.", name), isError: true);
            return;
        }

        var value = await new InputDialog(_skins!.Current, Loc.T("Sekret „{0}”", name), Loc.T("Wartość dla {0} (zapisana zaszyfrowana, tylko dla tego serwera):", Plugins.BuiltIn.Dashboard.ApiCards.Origin(uri))).ShowAsync(this);
        if (!string.IsNullOrEmpty(value))
        {
            await Task.Run(() => _host!.Secrets.Set(name, value, Plugins.BuiltIn.Dashboard.ApiCards.Origin(uri)));
            Notify(Loc.T("Zapisano sekret „{0}”. Odśwież kartę.", name));
        }
    }

    private async ValueTask ConfigureFolderViewAsync(CommandInvocation _)
    {
        var panel = _active;
        var tab = panel.ActiveTab;
        var folder = tab.CurrentEntry is { Kind: VfsEntryKind.Directory or VfsEntryKind.Drive or VfsEntryKind.Share } entry ? entry.Path : tab.Path;
        await ConfigureFolderViewAsync(folder, panel);
    }

    private async Task ConfigureFolderViewAsync(string folder, FilePanelView panel)
    {
        var tab = panel.ActiveTab;
        if (VfsPath.GetScheme(folder) is not (VfsPath.FileScheme or VfsPath.SmbScheme) || folder.Contains("://", StringComparison.Ordinal))
        {
            Notify(Loc.T("Plik c4k.dir można utworzyć na dysku albo w udziale sieciowym."), isError: true);
            return;
        }

        var brushes = _skins!.Current;
        var items = new List<PickerItem>
        {
            new(Loc.T("🖼 Galeria"), Loc.T("miniatury obrazów z folderu"), FolderViewIds.Gallery, "zdjęcia photos images"),
            new(Loc.T("▦ Kafelki"), Loc.T("dashboard: skróty do folderów, stron i skryptów"), FolderViewIds.Dashboard, "dashboard skróty tiles"),
            new(Loc.T("🗒 Notatki"), Loc.T("notatki Markdown z powiązaniami (Obsidian)"), FolderViewIds.Notes, "obsidian markdown"),
            new(Loc.T("📄 README"), Loc.T("plik Markdown folderu"), FolderViewIds.Markdown, "markdown opis"),
            new(Loc.T("🔐 Połączenie SSH"), Loc.T("wejście otwiera serwer SFTP z kluczem"), "ssh", "sftp klucz key serwer"),
            new(Loc.T("🌐 Strona WWW"), Loc.T("wejście otwiera stronę w przeglądarce panelu"), "url", "www http link"),
            new(Loc.T("▶ Skrypt"), Loc.T("wejście uruchamia polecenie (po Twojej zgodzie)"), "run", "script ps1 bat polecenie"),
        };
        items.AddRange(_host!.PluginCatalog.Plugins.Select(p => new PickerItem("🧩 " + p.Name, p.LocalizedDescription, "plugin:" + p.Id, string.Join(' ', p.Tags))));
        items.Add(new(Loc.T("✕ Zwykła lista plików"), Loc.T("usuwa c4k.dir"), "remove", "usuń delete"));

        var choice = (await new PickerDialog(brushes, Loc.T("Widok folderu: {0}", folder), items,
            Loc.T("Co ma pokazywać ten folder? Ustawienia zostaną zapisane w pliku c4k.dir w tym folderze.")).ShowAsync(this))?.Value;
        if (choice is null)
        {
            return;
        }

        var file = FolderConfig.ChildPath(folder, FolderConfig.FileName);
        if (choice == "remove")
        {
            try
            {
                if (await _host!.Router.GetEntryAsync(file, CancellationToken.None) is not null)
                {
                    await _host.Router.DeleteAsync(file, recursive: false, CancellationToken.None);
                }

                Notify(Loc.T("Usunięto {0}.", file));
                await tab.RefreshAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsAuthenticationRequiredException
                or VfsProviderNotFoundException or NotSupportedException or TimeoutException)
            {
                Notify(Loc.T("Nie można usunąć {0}: {1}", file, ex.Message), isError: true);
            }

            return;
        }

        System.Text.Json.Nodes.JsonNode? action;
        var where = "both";
        switch (choice)
        {
            case "ssh":
                var address = await new InputDialog(brushes, Loc.T("Połączenie SSH"), Loc.T("Adres (sftp://użytkownik@host/folder):"), "sftp://" + Environment.UserName + "@host/home/" + Environment.UserName).ShowAsync(this);
                if (string.IsNullOrWhiteSpace(address))
                {
                    return;
                }

                var key = await new InputDialog(brushes, Loc.T("Połączenie SSH"), Loc.T("Klucz prywatny (puste = domyślne klucze z ~/.ssh):"), "~/.ssh/id_ed25519").ShowAsync(this);
                action = new System.Text.Json.Nodes.JsonObject { ["open"] = address.Trim() };
                if (!string.IsNullOrWhiteSpace(key))
                {
                    action["keyFile"] = key.Trim();
                }

                where = "enter";
                break;
            case "url":
                var url = await new InputDialog(brushes, Loc.T("Strona WWW"), Loc.T("Adres strony:"), "https://").ShowAsync(this);
                if (!Core.Ai.AiInstallInfo.IsWebUrl(url))
                {
                    return;
                }

                action = new System.Text.Json.Nodes.JsonObject { ["url"] = url!.Trim() };
                where = "enter";
                break;
            case "run":
                var command = await new InputDialog(brushes, Loc.T("Skrypt"), Loc.T("Polecenie (np. build.ps1, npm, code):"), "build.ps1").ShowAsync(this);
                if (string.IsNullOrWhiteSpace(command))
                {
                    return;
                }

                action = new System.Text.Json.Nodes.JsonObject { ["run"] = new System.Text.Json.Nodes.JsonObject { ["command"] = command.Trim(), ["args"] = new System.Text.Json.Nodes.JsonArray() } };
                where = "enter";
                break;
            case FolderViewIds.Dashboard:
                action = System.Text.Json.Nodes.JsonNode.Parse("""
                    {
                      "view": "dashboard",
                      "tiles": [
                        { "title": "Dokumenty", "icon": "📁", "open": "~/Documents" },
                        { "title": "Commander4K", "icon": "🌐", "url": "https://github.com/4kprojekt/Commander4K" },
                        { "title": "Terminal", "icon": "▶", "run": { "command": "wt", "args": ["-d", "."] } }
                      ]
                    }
                    """);
                break;
            default:
                if (choice.StartsWith("plugin:", StringComparison.Ordinal))
                {
                    var plugin = _host.PluginCatalog.Find(choice[7..])!;
                    action = new System.Text.Json.Nodes.JsonObject { ["plugin"] = plugin.Id };
                    if (!IsInstalled(plugin.Id))
                    {
                        await OfferCatalogPluginAsync(plugin.Id);
                    }
                }
                else
                {
                    action = choice;
                }

                break;
        }

        if (where == "both")
        {
            var when = (await new PickerDialog(brushes, Loc.T("Kiedy?"),
            [
                new(Loc.T("Podgląd i wejście"), Loc.T("Ctrl+Q na folderze i Enter"), "both"),
                new(Loc.T("Tylko podgląd"), Loc.T("Enter otwiera zwykłą listę plików"), "preview"),
                new(Loc.T("Tylko wejście"), Loc.T("podgląd pokazuje rozmiary jak zwykle"), "enter"),
            ]).ShowAsync(this))?.Value;
            if (when is null)
            {
                return;
            }

            where = when;
        }

        string? existing;
        try
        {
            existing = await Task.Run(() => ReadFolderConfigTextAsync(file, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsAuthenticationRequiredException
            or VfsProviderNotFoundException or NotSupportedException or TimeoutException or OperationCanceledException)
        {
            Notify(Loc.T("Nie można odczytać {0}: {1}", file, ex.Message), isError: true);
            return;
        }

        if (existing is not null && !await new ConfirmDialog(brushes, Loc.T("Zastąpić c4k.dir?"), Loc.T("{0} już istnieje.", file), Loc.T("Zastąp")).ShowAsync(this))
        {
            return;
        }

        var text = FolderConfigWriter.CreateForAction(action, where);
        try
        {
            await WriteFolderConfigTextAsync(file, text, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsAuthenticationRequiredException
            or VfsProviderNotFoundException or NotSupportedException or TimeoutException or OperationCanceledException)
        {
            Notify(Loc.T("Nie można zapisać {0}: {1}", file, ex.Message), isError: true);
            return;
        }

        _host.FolderSuggestions.Forget(folder);
        Notify(Loc.T("Zapisano {0} — Enter na folderze go użyje, Shift+Enter pokazuje pliki, F4 edytuje.", file));
        var config = FolderConfig.Parse(folder, text);
        var configuredAction = where is "both" or "preview" ? config.Preview : config.Enter;
        if (configuredAction is { IsPlainFiles: false })
        {
            var folderEntry = new VfsEntry(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), folder, VfsEntryKind.Directory);
            await RunFolderActionAsync(panel, folderEntry, config, configuredAction);
        }
        else
        {
            await tab.RefreshAsync();
        }
    }

    /// <summary>Shift+Enter: the folder's files, whatever its <c>c4k.dir</c> says; a file opens with Windows even when a preview claims Enter.</summary>
    private void EnterFolderPlain()
    {
        if (_active.Mode is null && _active.ActiveTab.CurrentEntry is { IsContainer: true, Kind: not (VfsEntryKind.ParentLink or VfsEntryKind.Action) } entry)
        {
            _ = _active.NavigateAsync(entry.Path);
        }
        else if (_active.Mode is null && string.IsNullOrWhiteSpace(_commandLine.Text) && _active.ActiveTab.CurrentEntry is { IsContainer: false, Kind: not VfsEntryKind.Action } file)
        {
            OpenFile(_active, file, plain: true);
        }
        else
        {
            OpenCurrent();
        }
    }

    private async Task RunFolderActionAsync(FilePanelView panel, VfsEntry folder, FolderConfig config, FolderAction action)
    {
        var host = _host!;
        switch (action.Kind)
        {
            case FolderActionKind.Run when action.Run is { } run:
                await RunFolderCommandAsync(config, run);
                return;

            case FolderActionKind.Open when action.Open is { } open:
                var target = config.Resolve(open);
                if (VfsPath.GetScheme(target) is "sftp" or "ftp" && (action.KeyFile is not null || action.User is not null))
                {
                    // The login from c4k.dir is kept for this session only; saved connections remain the place for passwords.
                    host.Remote.SetLogin(target, new RemoteLogin(action.User ?? RemoteTransferVfsPlugin.UserNameOf(target) ?? Environment.UserName,
                        PrivateKeyFile: action.KeyFile is { } key ? config.Resolve(key) : null));
                }

                await panel.NavigateAsync(target);
                return;

            case FolderActionKind.Url when action.Url is { } url:
                if (!Core.Ai.AiInstallInfo.IsWebUrl(url))
                {
                    Notify(Loc.T("{0}: „url” musi być adresem http(s).", config.FilePath), isError: true);
                    break;
                }

                _ = panel.NavigateAsync(folder.Path);
                OpenBrowser(url, panel);
                return;

            case FolderActionKind.Panel when action.PanelId is { } panelId:
                if (host.FindPanel(panelId) is null && action.Plugin is { } owner && !IsInstalled(owner))
                {
                    await OfferCatalogPluginAsync(owner);
                    break;
                }

                _ = panel.NavigateAsync(folder.Path);
                await OpenPanelInAsync(panelId, folder.Path, panel, action.Options);
                return;

            case FolderActionKind.View:
                _ = panel.NavigateAsync(folder.Path);
                panel.ShowMode(CreatePreview(panel, new PreviewPanelView.PinnedFolder(folder, config, action)));
                SetActive(panel);
                return;
        }

        await panel.NavigateAsync(folder.Path);
    }

    private bool IsInstalled(string pluginId) =>
        _host!.DiscoveredPlugins.Any(p => string.Equals(p.Manifest?.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs a command from a <c>c4k.dir</c>. The first time (and after every change of the file) the user sees the
    /// exact command and decides; a folder copied from elsewhere cannot run anything on its own. Disks and shares only.
    /// </summary>
    /// <summary>
    /// Dashboard cards that sign in (SSH) or may change something (POST…) wait for trust in their c4k.dir. The dialog
    /// lists everything the trust unlocks, scripts included, because it is given to the whole file content.
    /// </summary>
    private async Task<bool> AskFolderTrustAsync(FolderConfig config)
    {
        if (VfsPath.GetScheme(config.Folder) is not (VfsPath.FileScheme or VfsPath.SmbScheme))
        {
            return false;
        }

        var message = Loc.T("Plik {0} chce:", config.FilePath) + "\n\n" + string.Join("\n", config.TrustSummary().Select(line => "    " + line)) + "\n\n" +
            Loc.T("Zgoda dotyczy tej treści pliku — gdy c4k.dir się zmieni, zapytam ponownie. Zezwalaj tylko na adresy i serwery, które znasz.");
        if (!await new ConfirmDialog(_skins!.Current, Loc.T("Zezwolić na ten plik c4k.dir?"), message, Loc.T("Zezwól")).ShowAsync(this))
        {
            return false;
        }

        await Task.Run(() => _host!.FolderTrust.Trust(config));
        _host!.Audit.Record("c4k.dir", "trust", config.FilePath);
        return true;
    }

    private async Task RunFolderCommandAsync(FolderConfig config, FolderRun run)
    {
        if (VfsPath.GetScheme(config.Folder) is not (VfsPath.FileScheme or VfsPath.SmbScheme))
        {
            Notify(Loc.T("Polecenia z c4k.dir działają tylko na dyskach i udziałach sieciowych."), isError: true);
            return;
        }

        var workingDirectory = run.WorkingDirectory is { } cwd ? config.Resolve(cwd) : config.Folder;
        if (!await Task.Run(() => _host!.FolderTrust.IsTrusted(config)))
        {
            var message = Loc.T("Plik {0} chce uruchomić:", config.FilePath) + "\n\n    " + run + "\n\n" +
                Loc.T("Folder roboczy: {0}", workingDirectory) + "\n\n" +
                Loc.T("Zgoda dotyczy tej treści pliku — gdy c4k.dir się zmieni, zapytam ponownie. Uruchamiaj tylko polecenia, które rozumiesz.");
            if (!await new ConfirmDialog(_skins!.Current, Loc.T("Uruchomić polecenie z c4k.dir?"), message, Loc.T("Uruchom")).ShowAsync(this))
            {
                return;
            }

            await Task.Run(() => _host!.FolderTrust.Trust(config));
        }

        _host!.Audit.Record("c4k.dir", "run", $"{config.FilePath}: {run}");
        await Task.Run(() =>
        {
            try
            {
                using var _ = Process.Start(FolderRunStartInfo(config, run, workingDirectory));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                Dispatcher.UIThread.Post(() => Notify(Loc.T("Nie udało się uruchomić „{0}”: {1}", run.Command, ex.Message), isError: true));
            }
        });
    }

    /// <summary>A script next to the c4k.dir runs with its interpreter (.ps1 → PowerShell 7); anything else goes through Windows (exe, bat, documents, PATH).</summary>
    private static ProcessStartInfo FolderRunStartInfo(FolderConfig config, FolderRun run, string workingDirectory)
    {
        var local = config.Resolve(run.Command);
        var command = File.Exists(local) ? local : run.Command;
        var info = Path.GetExtension(command).Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("pwsh") { ArgumentList = { "-NoLogo", "-File", command } }
            : new ProcessStartInfo(command);
        foreach (var argument in run.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.WorkingDirectory = workingDirectory;
        info.UseShellExecute = !run.Hidden;
        info.CreateNoWindow = run.Hidden;
        return info;
    }

    private readonly HashSet<string> _allowedUriSchemes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A tile click (gallery, dashboard, plugin tiles). Paths open like Enter in <paramref name="listPanel"/>; web pages in
    /// the browser panel; <c>c4k:</c> links install plugins or run the folder's own commands; other URIs (steam://…)
    /// go to Windows once the user allowed that scheme in this session.
    /// </summary>
    private async Task OpenTileAsync(PreviewTile tile, VfsEntry? folder, FilePanelView listPanel)
    {
        if (tile.Target is not { Length: > 0 } target)
        {
            return;
        }

        try
        {
            if (target == "c4k:plugins")
            {
                await OpenPluginManagerAsync();
            }
            else if (target.StartsWith("c4k:install/", StringComparison.Ordinal))
            {
                await InstallFromCatalogAsync(target["c4k:install/".Length..]);
            }
            else if (target.StartsWith("c4k:run/", StringComparison.Ordinal))
            {
                await RunTileCommandAsync(target["c4k:run/".Length..], folder);
            }
            else if (target.StartsWith(Plugins.BuiltIn.Dashboard.ApiCards.SecretActionPrefix, StringComparison.Ordinal))
            {
                await AskSecretAsync(target[Plugins.BuiltIn.Dashboard.ApiCards.SecretActionPrefix.Length..], folder);
            }
            else if (Core.Ai.AiInstallInfo.IsWebUrl(target))
            {
                OpenBrowser(target);
            }
            else if (Uri.TryCreate(target, UriKind.Absolute, out var page) && page.IsFile
                && Path.GetExtension(page.LocalPath).ToLowerInvariant() is ".htm" or ".html")
            {
                // A local page (the HTML preview's "open in browser"): shown in the browser panel, not by Windows.
                OpenBrowser(page.AbsoluteUri);
            }
            else if (_host!.Router.TryResolve(target, out _) && !IsForeignUri(target))
            {
                var entry = await Task.Run(() => _host.Router.GetEntryAsync(target, CancellationToken.None));

                // A folder with its own view, opened from a folder view (a dashboard picked on a dashboard), opens in
                // place so Back returns to the dashboard it came from.
                if (listPanel.Mode is PreviewPanelView { IsPinnedFolder: true } pinnedView && entry is { IsContainer: true } folderEntry
                    && VfsPath.GetScheme(folderEntry.Path) is VfsPath.FileScheme or VfsPath.SmbScheme or "sftp" or "ftp")
                {
                    var config = await Task.Run(() => _host.ReadFolderConfigAsync(folderEntry.Path, CancellationToken.None));
                    if (config is { Error: null } && (config.Enter ?? config.Preview) is { IsPlainFiles: false, Kind: FolderActionKind.View } view)
                    {
                        await pinnedView.OpenPinnedAsync(new PreviewPanelView.PinnedFolder(folderEntry, config, view));
                        return;
                    }
                }

                if (listPanel.Mode is PreviewPanelView { IsPinned: true })
                {
                    listPanel.CloseMode();
                }

                if (entry is null)
                {
                    Notify(Loc.T("Nie znaleziono {0}.", target), isError: true);
                }
                else if (entry.Value.IsContainer)
                {
                    await EnterFolderAsync(listPanel, entry.Value);
                }
                else
                {
                    OpenFile(listPanel, entry.Value);
                }
            }
            else
            {
                await OpenForeignUriAsync(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsAuthenticationRequiredException or InvalidOperationException or NotSupportedException)
        {
            Notify(ex.Message, isError: true);
        }
    }

    /// <summary>A URI scheme the VFS does not own (steam://, mailto:, ms-settings:), not a drive letter.</summary>
    private static bool IsForeignUri(string target)
    {
        var colon = target.IndexOf(':');
        return colon > 1 && target[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')
            && !(target[..colon] is "sftp" or "ftp" or "smb" or "net" or "reg" or "zip" or "7z" or "tar" or "rar" or "gz" or "local" or "file");
    }

    private async Task OpenForeignUriAsync(string uri)
    {
        var colon = uri.IndexOf(':');
        if (colon <= 1)
        {
            Notify(Loc.T("Nieznany cel kafelka: {0}", uri), isError: true);
            return;
        }

        var scheme = uri[..colon];
        if (!_allowedUriSchemes.Contains(scheme))
        {
            if (!await new ConfirmDialog(_skins!.Current, Loc.T("Otworzyć link {0}:?", scheme),
                    Loc.T("Kafelek otwiera „{0}” w programie, który Windows przypisał do „{1}:”. Pozwolić na linki {1}: do końca tej sesji?", uri, scheme), Loc.T("Otwórz")).ShowAsync(this))
            {
                return;
            }

            _allowedUriSchemes.Add(scheme);
        }

        await Task.Run(() =>
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Win32Exception ex)
            {
                Dispatcher.UIThread.Post(() => Notify(ex.Message, isError: true));
            }
        });
    }

    /// <summary><c>c4k:run/&lt;section&gt;/&lt;tile&gt;</c>: the command is read again from the folder's c4k.dir, never taken from the link.</summary>
    private async Task RunTileCommandAsync(string reference, VfsEntry? folder)
    {
        var parts = reference.Split('/');
        if (folder is null || parts.Length != 2 || !int.TryParse(parts[1], out var index))
        {
            return;
        }

        var config = await Task.Run(() => _host!.ReadFolderConfigAsync(folder.Value.Path, CancellationToken.None));
        var action = parts[0] == "enter" ? config?.Enter : config?.Preview;
        if (config is null || action is null || index < 0 || index >= action.Tiles.Count || action.Tiles[index].Run is not { } run)
        {
            Notify(Loc.T("Kafelek nie ma już polecenia w c4k.dir."), isError: true);
            return;
        }

        await RunFolderCommandAsync(config, run);
    }

    private async Task OfferCatalogPluginAsync(string pluginId)
    {
        if (_host!.PluginCatalog.Find(pluginId) is not { } entry)
        {
            Notify(Loc.T("Wtyczki „{0}” nie ma w katalogu oficjalnych wtyczek.", pluginId), isError: true);
            return;
        }

        if (await new ConfirmDialog(_skins!.Current, Loc.T("Ten folder potrzebuje wtyczki"),
                Loc.T("c4k.dir tego folderu korzysta z wtyczki „{0}”:\n\n{1}\n\nPobrać ją z katalogu oficjalnych wtyczek?", entry.Name, entry.LocalizedDescription), Loc.T("Pobierz")).ShowAsync(this))
        {
            await InstallFromCatalogAsync(pluginId);
        }
    }

    /// <summary>Downloads a catalog plugin (each file checked against the catalog's hash) and installs it disabled, through the usual consent.</summary>
    private async Task InstallFromCatalogAsync(string pluginId)
    {
        var host = _host!;
        if (host.PluginCatalog.Find(pluginId) is not { } entry)
        {
            Notify(Loc.T("Wtyczki „{0}” nie ma w katalogu oficjalnych wtyczek.", pluginId), isError: true);
            return;
        }

        IReadOnlyList<Core.Plugins.GeneratedFile> files;
        try
        {
            Notify(Loc.T("Pobieranie „{0}”…", entry.Name));
            files = await Task.Run(() => host.PluginCatalog.DownloadAsync(entry));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException)
        {
            await new MessageDialog(_skins!.Current, Loc.T("Nie można pobrać wtyczki"), ex.Message).ShowAsync(this);
            return;
        }

        var result = await AiPluginInstaller.InstallAsync(this, _skins!.Current, AiContext, files, OpenPluginManagerAsync, fromCatalog: true);
        Notify(result.Message);
    }


    /// <summary>Builds a full VFS target description off the UI thread and offers copyable prompts for an MCP client.</summary>
    private async Task ShowUnknownPreviewPromptsAsync(FilePanelView source, VfsEntry entry)
    {
        var host = _host!;
        var panelPath = source.ActiveTab.Path;
        var parent = panelPath;
        var cursor = source.ActiveTab.CurrentEntry;
        string sample;
        if (entry.Kind == VfsEntryKind.File)
        {
            try
            {
                sample = await Task.Run(() => AiAppContext.DescribeFileAsync(host, entry));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsProviderNotFoundException
                or VfsAuthenticationRequiredException or NotSupportedException or TimeoutException or OperationCanceledException)
            {
                sample = Loc.T("Nie można odczytać próbki pliku: {0}", ex.Message);
            }
        }
        else
        {
            sample = "No file byte sample is available for this VFS node.";
        }

        var context = new StringBuilder()
            .AppendLine($"- Full VFS path: {entry.Path}")
            .AppendLine($"- Parent path: {parent}")
            .AppendLine($"- Name: {entry.Name}")
            .AppendLine($"- Entry kind: {entry.Kind}")
            .AppendLine($"- Is container: {entry.IsContainer}")
            .AppendLine($"- Size: {(entry.Size >= 0 ? entry.Size.ToString("N0") + " bytes" : "unknown")}")
            .AppendLine($"- Last modified: {(entry.LastWriteTime == default ? "unknown" : entry.LastWriteTime.ToString("O"))}")
            .AppendLine($"- Flags: {entry.Flags}")
            .AppendLine($"- Description: {entry.Description ?? "(none)"}")
            .AppendLine($"- Source panel folder: {panelPath}")
            .AppendLine($"- Source panel cursor: {(cursor is { } current ? current.Name + " (" + current.Path + ")" : "(none)")}")
            .AppendLine()
            .AppendLine("File sample (when the target is a readable file):")
            .AppendLine(sample)
            .ToString();
        var connection = _mcp?.ConnectionDetails;
        await new UnknownPreviewPromptsDialog(_skins!.Current, entry, context, connection, OpenMcpConnect).ShowAsync(this);
    }

    /// <summary>What the AI chat is told about the app.</summary>
    internal AiAppContext AiContext => _aiContext ??= new AiAppContext(_host!, () => (_active, Other, _active == _left));

    private AiAppContext? _aiContext;

    private Task OpenPluginManagerAsync() => ExecuteAsync(CommandIds.PanelPrefix + PluginManagerPanel.PluginId);

    private void OpenAiProviders()
    {
        if (_aiProviders is { } open)
        {
            open.Activate();
            return;
        }

        _aiProviders = new AiProvidersWindow(_host!, _skins!.Current);
        FollowSkin(_aiProviders, _aiProviders.ApplySkin);
        _aiProviders.Closed += (_, _) => _aiProviders = null;
        _aiProviders.Show(this);
    }

    /// <summary>
    /// Repaints a non-modal window whenever the skin or density changes, for as long as it is open. Posted, so a
    /// window can be rebuilt by a change made from its own controls (the skin combo box in the settings).
    /// </summary>
    private void FollowSkin(Window window, Action<SkinBrushes> apply)
    {
        void OnChanged(SkinBrushes brushes) => Dispatcher.UIThread.Post(() => apply(brushes));
        _skins!.Changed += OnChanged;
        window.Closed += (_, _) => _skins.Changed -= OnChanged;
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

        items.AddRange(host.Skins.Skins.Select(s => new PickerItem(Loc.T("Skórka: {0}", s.DisplayName), null, "skin:" + s.Id, "motyw kolory theme")));
        items.AddRange(host.SettingsCatalog.Select(s => new PickerItem($"Ustawienia: {s.Title}", s.Category, "set:" + s.Title, string.Join(' ', s.Keywords ?? []))));

        var choice = await new PickerDialog(_skins!.Current, Loc.T("Paleta komend (Ctrl+Shift+P)"), items, Loc.T("Wpisz, czego szukasz — komendy, skórki, ustawienia:")).ShowAsync(this);
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
        var changed = _active != panel;
        _active = panel;
        _left.IsActive = panel == _left;
        _right.IsActive = panel == _right;
        UpdatePrompt();
        if (changed)
        {
            CancelSuggestion();
            OnSuggestionCursor(panel);
        }
    }

    // ---- folder view suggestions: a hint when the cursor rests on a folder that suits a preview ----

    private static readonly TimeSpan SuggestionDelay = TimeSpan.FromMilliseconds(700);
    private CancellationTokenSource? _suggestCancel;
    private FilePanelView? _suggestPanel;
    private string? _suggestFor;

    /// <summary>Follows the cursor of <paramref name="panel"/>'s active tab (no polling): each move restarts the delay.</summary>
    private void WatchForSuggestions(FilePanelView panel)
    {
        PanelTab? watched = null;
        void OnChanged() => OnSuggestionCursor(panel);
        void Rebind()
        {
            if (watched is not null)
            {
                watched.Changed -= OnChanged;
            }

            watched = panel.ActiveTab;
            watched.Changed += OnChanged;
            OnChanged();
        }

        panel.ActiveTabChanged += Rebind;
        Rebind();
    }

    private void OnSuggestionCursor(FilePanelView panel)
    {
        if (panel != _active || _host is null)
        {
            return;
        }

        var entry = panel.Mode is null ? panel.ActiveTab.CurrentEntry : null;
        if (entry is { } same && _suggestPanel == panel && string.Equals(same.Path, _suggestFor, StringComparison.OrdinalIgnoreCase))
        {
            return; // A refresh or size update on the same folder keeps the hint.
        }

        CancelSuggestion();
        if (!_host.Settings.FolderSuggestions || entry is not { } folder || !FolderSuggestionService.IsEligible(folder))
        {
            return;
        }

        _suggestPanel = panel;
        _suggestFor = folder.Path;
        _suggestCancel = new CancellationTokenSource();
        _ = SuggestAsync(panel, folder, _suggestCancel.Token);
    }

    private void CancelSuggestion()
    {
        _suggestCancel?.Cancel();
        _suggestCancel?.Dispose();
        _suggestCancel = null;
        _suggestPanel = null;
        _suggestFor = null;
        _left?.ShowSuggestion(null);
        _right?.ShowSuggestion(null);
    }

    /// <summary>After the cursor rested <see cref="SuggestionDelay"/> on <paramref name="folder"/>: the best suggestions as they arrive.</summary>
    private async Task SuggestAsync(FilePanelView panel, VfsEntry folder, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SuggestionDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // The cursor moved on.
        }

        var service = _host!.FolderSuggestions;
        await Task.Run(async () =>
        {
            // SuggestAsync ends quietly on cancellation and swallows plugin failures.
            await foreach (var suggestion in service.SuggestAsync(folder, cancellationToken).ConfigureAwait(false))
            {
                Dispatcher.UIThread.Post(() => ShowSuggestionCard(panel, suggestion, cancellationToken));
            }
        }, CancellationToken.None);
    }

    private void ShowSuggestionCard(FilePanelView panel, RankedSuggestion suggestion, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || panel.Mode is not null
            || !string.Equals(panel.ActiveTab.CurrentEntry?.Path, suggestion.Folder.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var card = new FolderSuggestionCard(_skins!.Current, suggestion);
        card.Accepted += s => _ = AcceptFolderSuggestionAsync(panel, s);
        card.Postponed += s =>
        {
            _host!.FolderSuggestions.Memory.MuteForSession(s.Folder.Path);
            CancelSuggestion();
        };
        card.Declined += s =>
        {
            var memory = _host!.FolderSuggestions.Memory;
            _ = Task.Run(() => memory.DeclineAsync(s.Folder.Path));
            CancelSuggestion();
            Notify(Loc.T("Nie będę proponować widoku dla tego folderu. Cofniesz to poleceniem „Wyczyść odrzucone podpowiedzi widoku folderu”."));
        };
        panel.ShowSuggestion(card);
    }

    /// <summary>cm_AcceptFolderSuggestion (Ctrl+Enter): the hint shown in the active panel, if any.</summary>
    private void AcceptShownSuggestion()
    {
        if (_active.Suggestion is { } card)
        {
            _ = AcceptFolderSuggestionAsync(_active, card.Suggestion);
        }
    }

    /// <summary>Writes the folder's c4k.dir and opens the view; when the write is refused the view still opens once.</summary>
    private async Task AcceptFolderSuggestionAsync(FilePanelView panel, RankedSuggestion suggestion)
    {
        CancelSuggestion();
        var folder = suggestion.Folder;
        var json = FolderConfigWriter.CreateForAction(FolderConfigWriter.ActionFor(suggestion.Suggestion.View), "both");
        var file = FolderConfig.ChildPath(folder.Path, FolderConfig.FileName);
        var (result, error) = await FolderConfigWriter.CreateNewAsync(file, json, TimeSpan.FromSeconds(5), CancellationToken.None);
        _host!.FolderSuggestions.Forget(folder.Path);
        if (result == FolderConfigWrite.AlreadyExists)
        {
            // The listing was cut short or the answer was stale: the folder's own file wins, never overwritten.
            Notify(Loc.T("Folder ma już własny c4k.dir — otwieram go zamiast propozycji."));
            await EnterFolderAsync(panel, folder);
            return;
        }

        Notify(result == FolderConfigWrite.Written
            ? Loc.T("Zapisano {0} — Enter na folderze go użyje, Shift+Enter pokazuje pliki, F4 edytuje.", file)
            : Loc.T("Nie zapisano c4k.dir ({0}) — widok otwarty tylko teraz.", error), isError: result == FolderConfigWrite.Failed);

        var config = FolderConfig.Parse(folder.Path, json);
        if (config.Enter is { } action)
        {
            await RunFolderActionAsync(panel, folder, config, action);
            ShowBackToFilesTour(panel);
        }
    }

    private Tour.SpotlightOverlay? _tour;

    /// <summary>
    /// After a suggested view opened: the spotlight on "📄 Pliki (Esc)", until the user closed it three times or once
    /// went back to the files that way.
    /// </summary>
    private void ShowBackToFilesTour(FilePanelView panel)
    {
        var memory = _host!.FolderSuggestions.Memory;
        if (!memory.ShouldShowTour || panel.Mode is not PreviewPanelView preview)
        {
            return;
        }

        var used = false;
        void Used()
        {
            preview.CloseRequested -= Used;
            used = true; // Recorded once, as used: closing the spotlight now is not a dismissal.
            _tour?.Close();
            _ = Task.Run(memory.RecordTourUsedAsync);
        }

        preview.CloseRequested += Used;
        Dispatcher.UIThread.Post(() =>
        {
            if (panel.Mode != preview || Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(this) is not { } layer)
            {
                return;
            }

            _tour?.Close();
            var tour = new Tour.SpotlightOverlay(_skins!.Current, preview.FilesButton,
                Loc.T("Tu (albo Esc) wrócisz do zwykłej listy plików. Shift+Enter wchodzi do folderu bez widoku."), Loc.T("OK"));
            tour.Closed += closed =>
            {
                layer.Children.Remove(closed);
                if (_tour == closed)
                {
                    _tour = null;
                }

                if (!used)
                {
                    _ = Task.Run(memory.RecordTourDismissedAsync);
                }
            };
            _tour = tour;
            layer.Children.Add(tour);
        }, DispatcherPriority.Loaded);
    }

    private async ValueTask ResetFolderSuggestionsAsync(CommandInvocation invocation)
    {
        var memory = _host!.FolderSuggestions.Memory;
        await Task.Run(memory.ClearAsync);
        Notify(Loc.T("Wyczyszczono odrzucone podpowiedzi."));
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

        // The spotlight closes on any key; Esc and Enter only close it, so the first Esc does not leave the view too.
        if (_tour is { } tour && e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
        {
            tour.Close();
            if (e.Key is Key.Escape or Key.Enter)
            {
                e.Handled = true;
                return;
            }
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
        // Design system: 28 px bar, cells 2 px apart with 3 px corners; the number in its own 26 px block.
        var bar = new Grid().Cols(string.Join(",", FunctionKeys.Select(_ => "*"))).Height(28);
        for (var i = 0; i < FunctionKeys.Length; i++)
        {
            var (key, label, command) = FunctionKeys[i];
            var cell = new Border()
                .Col(i)
                .Margin(i == 0 ? 2 : 1, 2, i == FunctionKeys.Length - 1 ? 2 : 1, 2)
                .ClipToBounds(true)
                .Cursor(new Cursor(StandardCursorType.Hand))
                .Child(new DockPanel().Children(
                    new Border().Width(26).Tag("number").Child(new TextBlock().Text(key).HorizontalAlignment(HorizontalAlignment.Center)
                        .VerticalAlignment(VerticalAlignment.Center)),
                    new Border().Tag("label").Child(new TextBlock().Text(label).Padding(8, 0).VerticalAlignment(VerticalAlignment.Center)
                        .TextTrimming(TextTrimming.CharacterEllipsis))));
            cell.PointerPressed += (_, _) => _ = ExecuteAsync(command);
            bar.Children.Add(cell);
        }

        return bar;
    }

    private void ApplySkin(SkinBrushes b)
    {
        WindowSkin.Apply(this, b);
        _splitter.Background = b.WindowBackground;
        var radius = new CornerRadius(b.IsModern ? 6 : 0);
        _jobsBar.Background = b.StatusBackground;
        _jobsBar.BorderBrush = b.PanelBorder;
        _jobsBar.BorderThickness = new Thickness(b.IsModern ? 1 : 0);
        _jobsBar.CornerRadius = radius;

        // Command line: monospace (as in a terminal), muted prompt, thin rules above and below.
        var mono = b.IsModern ? b.MonospaceFamily : b.FontFamily;
        var commandSize = b.IsModern ? b.FontSize - 0.5 : b.FontSize;
        _commandBar.Background = b.CommandLineBackground;
        _commandBar.BorderBrush = b.PanelBorder;
        _commandBar.BorderThickness = new Thickness(0, b.IsModern ? 1 : 0, 0, b.IsModern ? 1 : 0);
        _prompt.Foreground = b.IsModern ? b.MutedForeground : b.CommandLineForeground;
        _prompt.FontFamily = mono;
        _prompt.FontSize = commandSize;
        _commandLine.Background = Brushes.Transparent;
        _commandLine.Foreground = b.CommandLineForeground;
        _commandLine.FontFamily = mono;
        _commandLine.FontSize = commandSize;
        _commandLine.CaretBrush = b.Accent;
        _notification.Foreground = b.StatusForeground;
        _notification.FontSize = Math.Max(11, b.FontSize - 1);
        _mcpBadge.FontSize = Math.Max(11, b.FontSize - 1);
        UpdateMcpBadge();
        ApplyMenuSkin(b);

        _keyBar.Background = b.WindowBackground;
        foreach (var cell in _keyBar.Children.OfType<Border>())
        {
            var dock = (DockPanel)cell.Child!;
            var numberBorder = (Border)dock.Children[0];
            var number = (TextBlock)numberBorder.Child!;
            var labelBorder = (Border)dock.Children[1];
            cell.CornerRadius = new CornerRadius(b.IsModern ? 3 : 0);
            numberBorder.Background = b.FunctionKeyNumberBackground;
            number.Foreground = b.FunctionKeyNumberForeground;
            number.FontFamily = b.FontFamily;
            number.FontSize = Math.Max(11, b.FontSize - 1);
            number.FontWeight = FontWeight.Bold;
            labelBorder.Background = b.FunctionKeyLabelBackground;
            var label = (TextBlock)labelBorder.Child!;
            label.Foreground = b.FunctionKeyLabelForeground;
            label.FontFamily = b.FontFamily;
            label.FontSize = Math.Max(11, b.FontSize - 1);
        }

        _jobsDirty = true;
    }

    /// <summary>Text currently shown in the status bar's notification area (for automation and tests).</summary>
    public string LastNotification => _notification?.Text ?? string.Empty;

    private void Notify(string message, bool isError = false) => Notify(message, isError, null);

    /// <summary>With a <paramref name="target"/> the message is clickable and goes through <see cref="OpenTileAsync"/>.</summary>
    private void Notify(string message, bool isError, string? target)
    {
        if (_notification is null)
        {
            return;
        }

        _notificationTarget = target;
        _notification.Text = message;
        _notification.Foreground = isError ? _skins!.Current.Error : _skins!.Current.StatusForeground;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void RefreshJobsBar()
    {
        UpdatePrompt(); // Cheap; follows navigation and tab switches without per-tab subscriptions.
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
                JobState.Completed => Loc.T("✓ gotowe"),
                JobState.Canceled => Loc.T("anulowano"),
                _ => "⚠ " + job.Error?.Message,
            };
            var handle = job;
            // Design system: semibold title, a 6 px rounded progress track (max 320 px), state in the status colour, "✕ Anuluj".
            var small = Math.Max(11, b.FontSize - 1);
            _jobsList.Children.Add(new Grid().Cols("2*,3*,Auto,Auto").Children(
                new TextBlock().Text(job.Title).Foreground(b.WindowForeground).FontWeight(FontWeight.SemiBold).FontSize(small)
                    .TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center),
                new ProgressBar().Col(1).Minimum(0).Maximum(1).Value(Math.Max(0, fraction)).IsIndeterminate(job.State == JobState.Running && fraction < 0)
                    .Foreground(b.Progress).Background(b.PanelBorder).Margin(12, 0).Height(6).MinHeight(6).MaxWidth(320)
                    .CornerRadius(new CornerRadius(3)).VerticalAlignment(VerticalAlignment.Center),
                new TextBlock().Col(2).Text(state + (metrics.CurrentItem is { } item && job.State == JobState.Running ? "  " + item : string.Empty))
                    .Foreground(job.State == JobState.Failed ? b.Error : b.StatusForeground).FontSize(small).MaxWidth(360)
                    .TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center),
                new Button().Col(3).Content(Loc.T("✕ Anuluj")).Height(24).Padding(8, 0).FontSize(small).Margin(12, 0, 0, 0)
                    .Background(Brushes.Transparent).IsVisible(job.State is JobState.Queued or JobState.Running)
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
        _host.Settings.Window = CapturePlacement();
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
        c.Register(CommandIds.Help, Loc.T("Lista skrótów klawiszowych"), ShowHelpAsync)
         .Register(CommandIds.SwitchPanel, Loc.T("Przełącz panel"), () => SetActive(Other))
         .Register(CommandIds.Open, Loc.T("Otwórz / wejdź"), OpenCurrent)
         .Register(CommandIds.EnterFolderPlain, Loc.T("Wejdź do folderu jako lista plików (pomija c4k.dir)"), EnterFolderPlain)
         .Register(CommandIds.FolderView, Loc.T("Widok folderu (c4k.dir): galeria, kafelki, wtyczka, SSH, skrypt"), ConfigureFolderViewAsync)
         .Register(CommandIds.AcceptFolderSuggestion, Loc.T("Przyjmij podpowiedź widoku folderu"), AcceptShownSuggestion)
         .Register(CommandIds.ResetFolderSuggestions, Loc.T("Wyczyść odrzucone podpowiedzi widoku folderu"), ResetFolderSuggestionsAsync)
         .Register(CommandIds.AddApiCard, Loc.T("Dodaj kartę API do dashboardu folderu (wybór pól z odpowiedzi)"), AddApiCardAsync)
         .Register(CommandIds.GoToParent, Loc.T("Katalog wyżej"), () => _active.GoToParent())
         .Register(CommandIds.Refresh, Loc.T("Odśwież"), () => _ = _active.ActiveTab.RefreshAsync())
         .Register(CommandIds.NewTab, Loc.T("Nowa zakładka"), _ => new ValueTask(ChooseNewTabAsync(_active)))
         .Register(CommandIds.CloseTab, Loc.T("Zamknij zakładkę"), () => _active.CloseActiveTab())
         .Register(CommandIds.LeftDriveMenu, Loc.T("Menu dysków (lewy)"), _ => ShowDriveMenuAsync(_left))
         .Register(CommandIds.RightDriveMenu, Loc.T("Menu dysków (prawy)"), _ => ShowDriveMenuAsync(_right))
         .Register(CommandIds.OpenNetwork, Loc.T("Sieć (net://)"), () => _ = _active.NavigateAsync(NetworkNeighborhoodVfsPlugin.RootPath))
         .Register(CommandIds.OpenRegistry, Loc.T("Rejestr (reg://)"), () => _ = _active.NavigateAsync("reg://"))
         .Register(CommandIds.AddConnection, Loc.T("Dodaj połączenie sieciowe"), AddConnectionAsync)
         .Register(CommandIds.GoToFrecent, Loc.T("Częste lokalizacje"), GoToFrecentAsync)
         .Register(CommandIds.ToggleSelection, Loc.T("Zaznacz"), ToggleMarkAndAdvance)
         .Register(CommandIds.CalculateDirSizes, Loc.T("Rozmiar katalogu / zaznacz"), CalculateSize)
         .Register(CommandIds.View, Loc.T("Podgląd"), ViewCurrent)
         .Register(CommandIds.QuickView, Loc.T("Szybki podgląd"), TogglePreview)
         .Register(CommandIds.PanelModes, Loc.T("Tryb panelu (pliki, podgląd, panele wtyczek)"), ShowPanelModesAsync)
         .Register(CommandIds.Browser, Loc.T("Przeglądarka (WWW, wyszukiwarki, asystenci AI)"), () => OpenBrowser(null))
         .Register(CommandIds.McpConnect, Loc.T("Połącz Claude Code lub innego klienta MCP z Commander4K"), OpenMcpConnect)
         .Register(CommandIds.Edit, Loc.T("Edycja"), EditCurrentAsync)
         .Register(CommandIds.Copy, Loc.T("Kopiuj"), _ => TransferAsync(TransferMode.Copy))
         .Register(CommandIds.Move, Loc.T("Przenieś"), _ => TransferAsync(TransferMode.Move))
         .Register(CommandIds.ContextMenu, Loc.T("Menu kontekstowe Windows dla zaznaczonych plików"), () => ShowContextMenu(_active, null, fromKeyboard: true))
         .Register(CommandIds.Columns,Loc.T("Kolumny: Git, zdjęcia (EXIF), SHA-256"), () => OpenSettings(Loc.T("kolumna")))
         .Register(CommandIds.Translate, Loc.T("Przetłumacz interfejs z AI (nowy język)"), _ => new ValueTask(new TranslationDialog(_host!, _skins!.Current).ShowAsync(this)))
         .Register(CommandIds.Pack,Loc.T("Spakuj do archiwum (ZIP, TAR, TAR.GZ)"), PackAsync)
         .Register(CommandIds.Unpack, Loc.T("Rozpakuj archiwum"), UnpackAsync)
         .Register(CommandIds.MakeDirectory, Loc.T("Nowy katalog"), MakeDirectoryAsync)
         .Register(CommandIds.Delete, Loc.T("Usuń"), DeleteAsync)
         .Register(CommandIds.SearchAsync, Loc.T("Szukaj"), OpenSearch)
         .Register(CommandIds.Compare, Loc.T("Porównaj pliki"), CompareAsync)
         .Register(CommandIds.OpenTerminal, Loc.T("Terminal w bieżącym katalogu"), OpenTerminalAsync)
         .Register(CommandIds.OpenInVsCode, Loc.T("Otwórz w VS Code (bieżący folder i zaznaczone pliki)"), OpenInVsCodeAsync)
         .Register(CommandIds.CancelCurrentOperation, Loc.T("Przerwij"), CancelCurrent)
         .Register(CommandIds.SelectSkin, Loc.T("Wybór skórki"), _ => new ValueTask(new SkinPickerDialog(_host, _skins!).ShowAsync(this)))
         .Register(CommandIds.NextSkin, Loc.T("Następna skórka"), NextSkin)
         .Register(CommandIds.EditSkin, Loc.T("Edytor skórki (JSON)"), _ => new ValueTask(new SkinEditorDialog(_host, _skins!).ShowAsync(this)))
         .Register(CommandIds.ToggleHidden, Loc.T("Pokaż / ukryj pliki ukryte"), ToggleHiddenAsync)
         .Register(CommandIds.CommandPalette, Loc.T("Paleta komend"), ShowCommandPaletteAsync)
         .Register(CommandIds.Settings, Loc.T("Ustawienia"), () => OpenSettings())
         .Register(CommandIds.CheckForUpdates, Loc.T("Sprawdź aktualizacje"), _ => new ValueTask(CheckForUpdatesNowAsync()))
         .Register(CommandIds.About, Loc.T("O programie"), _ => new ValueTask(new AboutDialog(_skins!.Current).ShowAsync(this)))
         .Register(CommandIds.Exit, Loc.T("Wyjście"), Close);
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

    // MCP tools (Mcp/McpAppTools) call these on the UI thread, through Dispatcher.UIThread.InvokeAsync. Messages are English (for the model).

    /// <summary>The current skin's brushes, for dialogs the MCP tools show.</summary>
    internal SkinBrushes McpBrushes => _skins!.Current;

    /// <summary><c>left</c>, <c>right</c>, <c>active</c> or <c>other</c>.</summary>
    internal FilePanelView McpPanel(string which) => which.Trim().ToLowerInvariant() switch
    {
        "left" => _left,
        "right" => _right,
        "active" => _active,
        "other" => Other,
        _ => throw new ArgumentException($"Unknown panel '{which}': use left, right, active or other."),
    };

    /// <summary>Plain navigation (no c4k.dir action runs); completes when the listing has loaded or failed.</summary>
    internal Task McpNavigateAsync(FilePanelView panel, string path) => panel.NavigateAsync(path);

    /// <summary>Marks exactly <paramref name="names"/> (replacing earlier marks) and moves the cursor.</summary>
    /// <returns>How many entries are marked, the names not found (once each) and whether the cursor name was found.</returns>
    internal (int Marked, IReadOnlyList<string> Missing, bool CursorFound) McpSelect(FilePanelView panel, IReadOnlyList<string> names, string? cursor)
    {
        var tab = panel.ActiveTab;
        var missing = new List<string>();
        tab.Marked.Clear();
        foreach (var name in names)
        {
            if (FindRow(name) is { } index)
            {
                tab.Marked.Add(tab.Entries[index].Path);
            }
            else if (!missing.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                missing.Add(name);
            }
        }

        var target = tab.Cursor;
        var cursorFound = true;
        if (cursor is not null)
        {
            if (FindRow(cursor) is { } index)
            {
                target = index;
            }
            else
            {
                cursorFound = false;
            }
        }

        tab.MoveCursor(target); // Raises Changed, which repaints the marks too.
        return (tab.Marked.Count, missing, cursorFound);

        int? FindRow(string name)
        {
            var index = tab.Entries.FindIndex(e => e.Kind is not (VfsEntryKind.ParentLink or VfsEntryKind.Action or VfsEntryKind.Service)
                && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : null;
        }
    }

    /// <summary><c>files</c>, <c>preview</c> or <c>panel:&lt;id&gt;</c>; false when the mode is unknown or could not open.</summary>
    internal async Task<bool> McpSetModeAsync(FilePanelView panel, string mode)
    {
        var source = panel == _left ? _right : _left;
        switch (mode.Trim())
        {
            case var files when files.Equals("files", StringComparison.OrdinalIgnoreCase):
                panel.CloseMode();
                return true;
            case var preview when preview.Equals("preview", StringComparison.OrdinalIgnoreCase):
                if (panel.Mode is not PreviewPanelView)
                {
                    panel.ShowMode(CreatePreview(source));
                }

                return true;
            case var id when id.StartsWith(CommandIds.PanelPrefix, StringComparison.OrdinalIgnoreCase):
                var pluginId = id[CommandIds.PanelPrefix.Length..];
                if (_host!.FindPanel(pluginId) is null)
                {
                    return false;
                }

                var before = panel.Mode;
                await OpenPanelInAsync(pluginId, null, panel);
                return panel.Mode is PluginPanelView && panel.Mode != before;
            default:
                return false;
        }
    }

    /// <summary>
    /// What plugin code is told (<c>ExternalPlugin.Context</c>, via <see cref="PluginHostAdapter"/>): the visible file
    /// panel's path, the other path and the marked entries (or the cursor).
    /// </summary>
    internal Mcp.McpPanelContext McpPluginContext()
    {
        var panel = _active.Mode is null ? _active : Other;
        var other = (_active.Mode is null ? Other : _active).ActiveTab.Path;
        return new Mcp.McpPanelContext(panel.ActiveTab.Path, other, [.. panel.ActiveTab.MarkedOrCurrent()]);
    }

    /// <summary>What a built-in command acts on: the active panel (location and selection) and the other panel (copy/move target).</summary>
    internal Mcp.McpPanelContext McpCommandContext() =>
        new(_active.ActiveTab.Path, Other.ActiveTab.Path, [.. _active.ActiveTab.MarkedOrCurrent()]);

    /// <summary>Runs a plugin's command like the menu does; <c>null</c> on success, otherwise why it did not run.</summary>
    internal async Task<string?> McpRunPluginCommandAsync(string pluginId, string commandId, CancellationToken cancellationToken = default)
    {
        if (_host!.FindCommandPlugin(pluginId) is not { } plugin)
        {
            return $"No plugin with commands has the id '{pluginId}'. Call list_plugins or list_commands.";
        }

        var command = plugin.Commands.FirstOrDefault(c => string.Equals(c.Id, commandId, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            return $"Plugin '{pluginId}' has no command '{commandId}'. Its commands: {string.Join(", ", plugin.Commands.Select(c => c.Id).DefaultIfEmpty("none"))}.";
        }

        var host = _host.HostFor(plugin.Manifest);
        if (!plugin.CanExecute(command.Id, host))
        {
            return $"The command {pluginId}:{command.Id} cannot run in the current context.";
        }

        try
        {
            await plugin.ExecuteAsync(command.Id, host, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Like the keymap path (ExecuteAsync): a failing plugin is a result, not a crash.
            return $"The command {pluginId}:{command.Id} failed: {ex.Message}";
        }
    }

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
                    Notify(Loc.T("Brak wtyczki dla „{0}”.", commandId), isError: true);
                }

                return;
            }

            // F5 from a plugin panel that provides files copies them out; moving out of (or into) a plugin is not a thing.
            if (_active.Mode is PluginPanelView && string.Equals(commandId, CommandIds.Move, StringComparison.OrdinalIgnoreCase))
            {
                Notify(MoveNotSupported);
                return;
            }

            if (_active.Mode is not null && FileCommands.Contains(commandId)
                && !(_active.Mode is PluginPanelView { Session.CanProvide: true } && string.Equals(commandId, CommandIds.Copy, StringComparison.OrdinalIgnoreCase)))
            {
                Notify(ModeActive);
                return;
            }

            if (!await _host!.Commands.ExecuteAsync(commandId))
            {
                Notify(Loc.T("Nieznana komenda „{0}” w keymap.json.", commandId), isError: true);
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
        return new ValueTask(new MessageDialog(_skins!.Current, Loc.T("Skróty klawiszowe"), text.ToString(), monospace: true).ShowAsync(this));
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

    /// <param name="plain">Shift+Enter: skip a preview that claims Enter (<c>"enter": true</c>) and open the file as before.</param>
    private void OpenFile(FilePanelView panel, VfsEntry entry, bool plain = false)
    {
        var previewPlugin = PreviewPluginResolver.Resolve(_host!.PreviewPlugins, entry);
        if (previewPlugin is SftpConnectionPreviewPlugin)
        {
            panel.ShowMode(CreatePreview(panel, pinnedFile: entry));
            SetActive(panel);
            return;
        }

        if (previewPlugin is BrowserPlugin
            && Uri.TryCreate(entry.Path, UriKind.Absolute, out var url)
            && url.Scheme is "http" or "https")
        {
            OpenBrowser(url.AbsoluteUri, panel);
            return;
        }

        // A preview that asks for Enter (a mail message…) shows the file pinned over the panel; Esc returns to the files.
        if (!plain && previewPlugin is Core.Plugins.ExternalPreviewPlugin { OpensOnEnter: true })
        {
            panel.ShowMode(CreatePreview(panel, pinnedFile: entry));
            SetActive(panel);
            return;
        }

        // Enter on an archive opens it like a folder (Total Commander); Alt+F9 extracts, the shell is one F3/F4 away.
        if (ArchiveVfsPlugin.IsArchiveName(entry.Name))
        {
            _ = panel.NavigateAsync(ArchiveVfsPlugin.RootPath(entry.Path));
            return;
        }

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
            ShowViewer(entry);
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
            ShowViewer(entry);
        }
    }

    private void ShowViewer(VfsEntry entry)
    {
        var window = new ViewerWindow(_host!, _skins!.Current, entry);
        FollowSkin(window, window.ApplySkin);
        window.Show(this);
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
            Notify(Loc.T("Edycja działa dla plików na dyskach i udziałach sieciowych."));
            return;
        }

        var result = await _host!.Tools.EditAsync(entry.Path);
        if (!result.Launched)
        {
            Notify(result.Error ?? Loc.T("Nie udało się uruchomić edytora."), isError: true);
        }
    }

    private async ValueTask CompareAsync(CommandInvocation _)
    {
        if (_left.ActiveTab.CurrentEntry is not { Kind: VfsEntryKind.File } left
            || _right.ActiveTab.CurrentEntry is not { Kind: VfsEntryKind.File } right)
        {
            Notify(Loc.T("Ustaw kursor na pliku w obu panelach, aby je porównać."));
            return;
        }

        var result = await _host!.Tools.DiffAsync(left.Path, right.Path);
        if (!result.Launched)
        {
            Notify(result.Error ?? Loc.T("Nie znaleziono narzędzia do porównywania."), isError: true);
        }
    }

    private async ValueTask OpenTerminalAsync(CommandInvocation _)
    {
        var path = _active.ActiveTab.Path;
        var directory = IsFileSystemPath(path) ? LocalFileSystemPlugin.ToLocalPath(path) ?? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await _host!.Tools.OpenTerminalAsync(directory);
        if (!result.Launched)
        {
            Notify(result.Error ?? Loc.T("Nie znaleziono terminala."), isError: true);
        }
    }

    /// <summary>
    /// cm_OpenInVsCode: a folder under the cursor (nothing marked) opens as the workspace; otherwise the current folder
    /// opens with the marked files (or the file under the cursor). Disks and UNC shares only — VS Code cannot open
    /// archives, SFTP or the registry through Commander4K.
    /// </summary>
    private async ValueTask OpenInVsCodeAsync(CommandInvocation _)
    {
        var tab = _active.ActiveTab;
        if (!IsFileSystemPath(tab.Path) || LocalFileSystemPlugin.ToLocalPath(tab.Path) is not { } folder)
        {
            Notify(Loc.T("VS Code otwiera tylko foldery na dyskach i udziałach sieciowych (nie archiwa, SFTP ani rejestr)."), isError: true);
            return;
        }

        var chosen = tab.MarkedOrCurrent().Where(e => e.Kind != VfsEntryKind.ParentLink).ToList();
        IReadOnlyList<string> files = [];
        if (tab.Marked.Count == 0 && chosen is [{ IsContainer: true } single])
        {
            folder = LocalFileSystemPlugin.ToLocalPath(single.Path) ?? folder;
        }
        else
        {
            files = [.. chosen.Where(e => !e.IsContainer).Select(e => LocalFileSystemPlugin.ToLocalPath(e.Path) ?? e.Path)];
        }

        var result = await _host!.Tools.OpenInVsCodeAsync(folder, files);
        Notify(result.Launched
            ? Loc.T("VS Code: {0}{1}", folder, files.Count > 0 ? Loc.P(files.Count, " + {0} plik| + {0} pliki| + {0} plików") : string.Empty)
            : result.Error ?? Loc.T("Nie znaleziono VS Code."), isError: !result.Launched);
    }

    private static string MoveNotSupported => Loc.T("Przenoszenie z wtyczek i do wtyczek nie jest obsługiwane.");

    private static string DoesNotReceive(string title) => Loc.T("Panel „{0}” nie przyjmuje plików.", title);

    private static string ModeActive => Loc.T("Aktywny panel pokazuje wtyczkę — zamknij go (Esc), aby działać na plikach.");

    /// <summary>For <see cref="Core.Plugins.PanelTransferRouting"/>: only a preview (Ctrl+Q, pinned folder view) still shows the panel's folder.</summary>
    private static Core.Plugins.PanelModeKind KindOf(IPanelModeView? mode) => mode switch
    {
        null => Core.Plugins.PanelModeKind.Files,
        PreviewPanelView => Core.Plugins.PanelModeKind.Preview,
        PluginPanelView => Core.Plugins.PanelModeKind.Plugin,
        _ => Core.Plugins.PanelModeKind.Other,
    };

    /// <summary>The other panel still shows a folder files can be written to (file list, quick view or pinned folder view).</summary>
    private static bool ShowsFolder(FilePanelView panel) => KindOf(panel.Mode) is Core.Plugins.PanelModeKind.Files or Core.Plugins.PanelModeKind.Preview;

    /// <summary>
    /// F5/F6, routed by <see cref="Core.Plugins.PanelTransferRouting.Decide"/>: a plugin panel on either side changes
    /// what happens (the active one provides files, the other one receives them; copy only). A preview or pinned folder
    /// still copies into the folder it shows; any other mode takes nothing.
    /// </summary>
    private async ValueTask TransferAsync(TransferMode mode)
    {
        var route = Core.Plugins.PanelTransferRouting.Decide(
            KindOf(_active.Mode),
            _active.Mode is PluginPanelView { Session.CanProvide: true },
            KindOf(Other.Mode),
            Other.Mode is PluginPanelView { Session.CanReceive: true },
            mode == TransferMode.Move);
        switch (route)
        {
            case Core.Plugins.PanelTransferRoute.RefuseMove:
                Notify(MoveNotSupported);
                return;
            case Core.Plugins.PanelTransferRoute.BlockedModeActive:
                Notify(ModeActive);
                return;
            case Core.Plugins.PanelTransferRoute.RefuseNotAccepting:
                Notify(DoesNotReceive(Other.Mode?.Title ?? string.Empty));
                return;
            case Core.Plugins.PanelTransferRoute.ProvideFromPanel when _active.Mode is PluginPanelView source:
                await GuardPanelTransferAsync(() => ProvideFromPanelAsync(_active, Other, source));
                return;
            case Core.Plugins.PanelTransferRoute.SendToPanel when Other.Mode is PluginPanelView panel:
                await GuardPanelTransferAsync(() => SendToPanelAsync(_active, Other, panel));
                return;
            case Core.Plugins.PanelTransferRoute.OrdinaryCopy:
                break;
            default:
                return;
        }

        var sourcePanel = _active;
        var sources = sourcePanel.ActiveTab.MarkedOrCurrent();
        if (sources.Count == 0)
        {
            return;
        }

        if (mode == TransferMode.Copy && Other.Mode is PreviewPanelView preview && preview.DashboardFolder is { } dashboardFolder
            && await AddDashboardShortcutsAsync(sourcePanel, preview, dashboardFolder, sources))
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

    private async Task<bool> AddDashboardShortcutsAsync(FilePanelView source, PreviewPanelView preview, VfsEntry folder, IReadOnlyList<VfsEntry> sources)
    {
        if (VfsPath.GetScheme(folder.Path) is not (VfsPath.FileScheme or VfsPath.SmbScheme))
        {
            Notify(Loc.T("Dashboard można zapisać tylko na dysku lub udziale sieciowym."), isError: true);
            return true;
        }

        var entries = sources.Where(CanCreateDashboardTile).ToList();
        if (entries.Count == 0)
        {
            Notify(Loc.T("Nie można dodać zaznaczonych elementów jako kafelków dashboardu."), isError: true);
            return true;
        }

        var previewList = string.Join("\n", entries.Take(10).Select(entry => "• " + entry.Name));
        if (entries.Count > 10)
        {
            previewList += "\n" + Loc.P(entries.Count - 10, "… i {0} kolejny element|… i {0} kolejne elementy|… i {0} kolejnych elementów");
        }

        var skippedEntries = sources.Count - entries.Count;
        if (skippedEntries > 0)
        {
            previewList += "\n\n" + Loc.P(skippedEntries, "Pominięty zostanie {0} element, którego nie można otworzyć jako kafelka.|Pominięte zostaną {0} elementy, których nie można otworzyć jako kafelki.|Pominiętych zostanie {0} elementów, których nie można otworzyć jako kafelków.");
        }

        var confirmation = Loc.P(entries.Count,
            "Dodać {0} kafelek do dashboardu „{1}”?\n\n{2}|Dodać {0} kafelki do dashboardu „{1}”?\n\n{2}|Dodać {0} kafelków do dashboardu „{1}”?\n\n{2}",
            folder.Name, previewList);
        if (!await new ConfirmDialog(_skins!.Current, Loc.T("Dodaj kafelki do dashboardu?"), confirmation, Loc.T("Dodaj")).ShowAsync(this))
        {
            return true;
        }

        var file = FolderConfig.ChildPath(folder.Path, FolderConfig.FileName);
        string? existing;
        try
        {
            existing = await Task.Run(() => ReadFolderConfigTextAsync(file, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsAuthenticationRequiredException
            or VfsProviderNotFoundException or NotSupportedException or TimeoutException or OperationCanceledException)
        {
            Notify(Loc.T("Nie można odczytać {0}: {1}", file, ex.Message), isError: true);
            return true;
        }

        if (existing is not null && FolderConfig.Parse(folder.Path, existing).Error is { } configError)
        {
            Notify(Loc.T("{0}: {1}", file, configError), isError: true);
            return true;
        }

        if (existing is not null && FolderConfigWriter.HasComments(existing)
            && !await new ConfirmDialog(_skins!.Current, Loc.T("Zapisać c4k.dir?"),
                Loc.T("{0} ma komentarze; zapis nowych skrótów je usunie.", file), Loc.T("Zapisz")).ShowAsync(this))
        {
            return true;
        }

        var wasTrusted = existing is null || await Task.Run(() => _host!.FolderTrust.IsTrusted(FolderConfig.Parse(folder.Path, existing)));
        var tiles = entries.Select(entry =>
        {
            var tile = new System.Text.Json.Nodes.JsonObject { ["title"] = entry.Name };
            tile[Core.Ai.AiInstallInfo.IsWebUrl(entry.Path) ? "url" : "open"] = entry.Path;
            if (ManifestPreviewCardId(entry) is { } previewCardId)
            {
                tile["card"] = previewCardId;
            }

            return tile;
        }).ToList();
        var text = existing;
        try
        {
            foreach (var tile in tiles)
            {
                text = FolderConfigWriter.AppendTile(text, (System.Text.Json.Nodes.JsonObject)tile.DeepClone(), replacePreview: false, preferPreview: true);
            }
        }
        catch (InvalidOperationException)
        {
            if (!await new ConfirmDialog(_skins!.Current, Loc.T("Zamienić podgląd na dashboard?"),
                    Loc.T("{0} pokazuje teraz coś innego. Podgląd stanie się dashboardem z tymi skrótami; dotychczasowy podgląd zostanie po wejściu do folderu, jeśli nic tam nie ustawiono.", file), Loc.T("Zamień")).ShowAsync(this))
            {
                return true;
            }

            text = existing;
            foreach (var tile in tiles)
            {
                text = FolderConfigWriter.AppendTile(text, (System.Text.Json.Nodes.JsonObject)tile.DeepClone(), replacePreview: true, preferPreview: true);
            }
        }

        try
        {
            await Task.Run(() => WriteFolderConfigTextAsync(file, text!, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsAuthenticationRequiredException
            or VfsProviderNotFoundException or NotSupportedException or TimeoutException or OperationCanceledException)
        {
            Notify(Loc.T("Nie można zapisać {0}: {1}", file, ex.Message), isError: true);
            return true;
        }

        if (wasTrusted)
        {
            await Task.Run(() => _host!.FolderTrust.Trust(FolderConfig.Parse(folder.Path, text!)));
        }

        source.ActiveTab.Marked.Clear();
        if (preview.DashboardFolder?.Path == folder.Path)
        {
            await preview.ReloadCurrentAsync();
        }

        var message = Loc.P(tiles.Count, "{0} skrót do dashboardu „{1}”.|{0} skróty do dashboardu „{1}”.|{0} skrótów do dashboardu „{1}”.", folder.Name);
        var skipped = sources.Count - entries.Count;
        if (skipped > 0)
        {
            message += " " + Loc.P(skipped, "Pominięto {0} element, którego nie można otworzyć jako kafelka.|Pominięto {0} elementy, których nie można otworzyć jako kafelka.|Pominięto {0} elementów, których nie można otworzyć jako kafelka.");
        }

        Notify(message);
        return true;
    }

    private string? ManifestPreviewCardId(VfsEntry entry)
    {
        return _host!.PreviewPlugins.OfType<Core.Plugins.ExternalPreviewPlugin>()
            .Where(plugin => plugin.CanPreview(entry)
                && plugin.Manifest.Contributes?.Previews?.Any(preview =>
                    preview.Card && string.Equals(plugin.Id, plugin.Manifest.Id + "/" + preview.Id, StringComparison.OrdinalIgnoreCase)) == true)
            .OrderByDescending(plugin => plugin.Priority)
            .Select(plugin => plugin.Id)
            .FirstOrDefault();
    }

    private async Task EditDashboardTileAsync(
        Commander4K.Plugins.BuiltIn.Dashboard.DashboardPreview dashboard, int index, DashboardTileEdit edit)
    {
        if ((uint)index >= (uint)dashboard.Slots.Count)
        {
            Notify(Loc.T("Nie znaleziono kafelka dashboardu."), isError: true);
            return;
        }

        var tile = dashboard.Slots[index].Tile;
        if (edit == DashboardTileEdit.Remove
            && !await new ConfirmDialog(_skins!.Current, Loc.T("Usuń kafelek?"),
                Loc.T("Usunąć kafelek „{0}” z dashboardu?", tile.Title), Loc.T("Usuń")).ShowAsync(this))
        {
            return;
        }

        var file = FolderConfig.ChildPath(dashboard.Config.Folder, FolderConfig.FileName);
        try
        {
            var existing = await Task.Run(() => ReadFolderConfigTextAsync(file, CancellationToken.None));
            if (existing is null)
            {
                Notify(Loc.T("Nie znaleziono pliku {0}.", file), isError: true);
                return;
            }

            var parsed = FolderConfig.Parse(dashboard.Config.Folder, existing);
            if (parsed.Error is { } configError)
            {
                Notify(Loc.T("{0}: {1}", file, configError), isError: true);
                return;
            }

            if (FolderConfigWriter.HasComments(existing)
                && !await new ConfirmDialog(_skins!.Current, Loc.T("Zapisać c4k.dir?"),
                    Loc.T("{0} ma komentarze; zapis zmian kafelka je usunie.", file), Loc.T("Zapisz")).ShowAsync(this))
            {
                return;
            }

            var wasTrusted = await Task.Run(() => _host!.FolderTrust.IsTrusted(parsed));
            var updated = edit switch
            {
                DashboardTileEdit.Remove => FolderConfigWriter.RemoveTile(existing, dashboard.Section, index, tile.Title),
                DashboardTileEdit.MoveUp => FolderConfigWriter.MoveTile(existing, dashboard.Section, index, -1, tile.Title),
                DashboardTileEdit.MoveDown => FolderConfigWriter.MoveTile(existing, dashboard.Section, index, 1, tile.Title),
                _ => throw new ArgumentOutOfRangeException(nameof(edit)),
            };
            await Task.Run(() => WriteFolderConfigTextAsync(file, updated, CancellationToken.None));
            if (wasTrusted)
            {
                await Task.Run(() => _host!.FolderTrust.Trust(FolderConfig.Parse(dashboard.Config.Folder, updated)));
            }

            _host!.FolderSuggestions.Forget(dashboard.Config.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsAuthenticationRequiredException
            or VfsProviderNotFoundException or NotSupportedException or TimeoutException or OperationCanceledException
            or InvalidOperationException or ArgumentException)
        {
            Notify(Loc.T("Nie można zmienić kafelka: {0}", ex.Message), isError: true);
        }
    }

    private bool CanCreateDashboardTile(VfsEntry entry) =>
        entry.Kind is not (VfsEntryKind.ParentLink or VfsEntryKind.Action)
        && (Core.Ai.AiInstallInfo.IsWebUrl(entry.Path)
            || entry.Kind is not VfsEntryKind.Service && _host!.Router.TryResolve(entry.Path, out _));

    private async Task<string?> ReadFolderConfigTextAsync(string file, CancellationToken cancellationToken)
    {
        var entry = await _host!.Router.GetEntryAsync(file, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        if (entry is not { Kind: VfsEntryKind.File } fileEntry || fileEntry.Size > 256 * 1024)
        {
            throw new IOException(Loc.T("Plik {0} nie jest prawidłowym plikiem c4k.dir.", file));
        }

        await using var stream = await _host.Router.OpenReadAsync(file, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[4096];
        var content = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (content.Length + read > 256 * 1024)
            {
                throw new IOException(Loc.T("Plik {0} przekracza dozwolony rozmiar c4k.dir.", file));
            }

            content.Append(buffer, 0, read);
        }

        var text = content.ToString();
        if (Encoding.UTF8.GetByteCount(text) > 256 * 1024)
        {
            throw new IOException(Loc.T("Plik {0} przekracza dozwolony rozmiar c4k.dir.", file));
        }

        return text;
    }

    private async Task WriteFolderConfigTextAsync(string file, string text, CancellationToken cancellationToken)
    {
        await using var stream = await _host!.Router.OpenWriteAsync(file, overwrite: true, cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    /// <summary>F5 onto a plugin panel: ask it for receivers, keep the ones that fit, let the user pick, run the job.</summary>
    /// <param name="source">The file panel the files come from, captured when F5 was pressed.</param>
    /// <param name="target">The panel showing <paramref name="panel"/>.</param>
    private async Task SendToPanelAsync(FilePanelView source, FilePanelView target, PluginPanelView panel)
    {
        var session = panel.Session;
        if (!session.CanReceive)
        {
            Notify(DoesNotReceive(panel.Title));
            return;
        }

        var sources = source.ActiveTab.MarkedOrCurrent();
        var items = sources.Select(ToTransferItem).ToList();
        if (items.Count == 0)
        {
            return; // TransferMatching.WhyNone would give a misleading reason for nothing.
        }

        // The panel can be closed (session disposed) or the source can switch to a mode while we wait.
        bool StillThere() => target.Mode == panel && source.Mode is null;

        var offered = await AskPanelAsync(panel.Title, ct => session.GetReceiversAsync(items, ct));
        if (offered is null || !Unchanged(StillThere()))
        {
            return;
        }

        // Ids pick the receiver after the dialog, so a duplicate id (a plugin bug) keeps only its first receiver.
        var fitting = offered.Where(receiver => Core.Plugins.TransferMatching.Fits(receiver, items)).DistinctBy(r => r.Id, StringComparer.Ordinal).ToList();
        if (fitting.Count == 0)
        {
            Notify(Core.Plugins.TransferMatching.WhyNone(offered, items) ?? DoesNotReceive(panel.Title));
            return;
        }

        var choice = await new PanelTransferDialog(
            _skins!.Current,
            Loc.T("Wyślij do „{0}”", panel.Title),
            Loc.T("Wyślij {0} do:", DescribeSelection(sources)),
            [.. fitting.Select(r => (r.Id, r.Title, r.Description))],
            target: null).ShowAsync(this);
        if (choice is null || fitting.FirstOrDefault(r => r.Id == choice.OptionId) is not { } receiver || !Unchanged(StillThere()))
        {
            return;
        }

        var job = new PanelReceiveJob(session, panel.Title, receiver, items);
        RunJob(job, queue: null, refreshBoth: false, handle =>
        {
            // The plugin's answer only exists when the call returned; a failed or cancelled call is reported by RunJob.
            if (job.Result is { } result)
            {
                var report = new StringBuilder();
                if (receiver.ReturnsMessage && !string.IsNullOrWhiteSpace(result.Text))
                {
                    report.AppendLine(result.Text);
                }

                var failed = result.Results?.Where(r => !r.Ok).ToList() ?? [];
                if (failed.Count > 0)
                {
                    if (report.Length > 0)
                    {
                        report.AppendLine();
                    }

                    report.AppendLine(Loc.T("Wtyczka nie przyjęła:"));
                    foreach (var item in failed)
                    {
                        report.Append("  ").AppendLine(Loc.T("{0}: {1}", item.Path, item.Error ?? Loc.T("nieznany błąd")));
                    }
                }

                if (report.Length > 0)
                {
                    _ = ShowTextWindowAsync(panel.Title + " — " + receiver.Title, report.ToString().TrimEnd());
                }
            }

            // Like after a panel action; only while the panel is still open (closing it disposed the session).
            if (_left.Mode == panel || _right.Mode == panel)
            {
                panel.Reload(quiet: true);
            }
        });
        source.ActiveTab.Marked.Clear();
    }

    /// <summary>F5 in a plugin panel that provides files: pick a provider and a target folder, then write what it returns.</summary>
    /// <param name="source">The panel showing <paramref name="panel"/>, captured when F5 was pressed.</param>
    /// <param name="target">The panel whose folder receives the files.</param>
    private async Task ProvideFromPanelAsync(FilePanelView source, FilePanelView target, PluginPanelView panel)
    {
        if (!ShowsFolder(target))
        {
            Notify(DoesNotReceive(target.Mode?.Title ?? string.Empty));
            return;
        }

        var rowIds = panel.SelectedRowIds;
        if (rowIds.Count == 0)
        {
            Notify(Loc.T("Panel „{0}” nie ma wierszy do skopiowania.", panel.Title));
            return;
        }

        // While we wait the target may turn into a mode that hides its folder, or the panel may close.
        bool StillThere() => source.Mode == panel && ShowsFolder(target);

        var session = panel.Session;
        var asked = await AskPanelAsync(panel.Title, ct => session.GetProvidersAsync(rowIds, ct));
        if (asked is null || !Unchanged(StillThere()))
        {
            return;
        }

        // Ids pick the provider after the dialog, so a duplicate id (a plugin bug) keeps only its first provider.
        var providers = asked.DistinctBy(p => p.Id, StringComparer.Ordinal).ToList();

        if (providers.Count == 0)
        {
            Notify(Loc.T("Ta wtyczka nie wydaje plików."));
            return;
        }

        var choice = await new PanelTransferDialog(
            _skins!.Current,
            Loc.T("Kopiuj z „{0}”", panel.Title),
            Loc.T("Kopiuj {0} z panelu „{1}”:", Loc.P(rowIds.Count, "{0} wiersz|{0} wiersze|{0} wierszy"), panel.Title),
            [.. providers.Select(p => (p.Id, p.Title, p.Description))],
            target.ActiveTab.Path).ShowAsync(this);
        if (choice?.Target is not { } folder || providers.FirstOrDefault(p => p.Id == choice.OptionId) is not { } provider || !Unchanged(StillThere()))
        {
            return;
        }

        // Downloads of url files go through the plugin's own host: gated by its "net" permission and audited.
        var host = (Core.Plugins.ScopedPluginHost)_host!.HostFor(panel.Plugin.Manifest);
        var job = new PanelProvideJob(session, provider, rowIds, _host.Router, folder, choice.Overwrite, host.OpenHttpReadAsync, host.Audit);
        RunJob(job, queue: null, refreshBoth: true, handle =>
        {
            // Also after a failure or cancel: the files that went wrong before it are still worth knowing (RunJob reports the failure itself).
            var report = new StringBuilder();
            if (handle.State == JobState.Completed && !string.IsNullOrWhiteSpace(job.Result?.Text))
            {
                report.AppendLine(job.Result.Text);
            }

            if (job.Errors.Count > 0)
            {
                if (report.Length > 0)
                {
                    report.AppendLine();
                }

                if (handle.State != JobState.Completed)
                {
                    report.AppendLine(handle.State == JobState.Canceled ? Loc.T("Przerwano kopiowanie.") : Loc.T("Kopiowanie nie powiodło się."));
                }

                report.AppendLine(Loc.T("Nie zapisano:"));
                foreach (var error in job.Errors)
                {
                    report.Append("  ").AppendLine(error);
                }
            }

            if (report.Length > 0)
            {
                _ = ShowTextWindowAsync(panel.Title + " — " + provider.Title, report.ToString().TrimEnd());
            }
        });
    }

    private bool _panelTransferPending;

    /// <summary>One panel transfer question (and its dialog) at a time: a second F5 meanwhile would ask twice and could send the files twice.</summary>
    private async Task GuardPanelTransferAsync(Func<Task> transfer)
    {
        if (_panelTransferPending)
        {
            Notify(Loc.T("Poczekaj — wtyczka jeszcze odpowiada."));
            return;
        }

        _panelTransferPending = true;
        try
        {
            await transfer();
        }
        finally
        {
            _panelTransferPending = false;
        }
    }

    /// <summary>Returns <paramref name="unchanged"/>, with a message when the panels changed during a question or dialog.</summary>
    private bool Unchanged(bool unchanged)
    {
        if (!unchanged)
        {
            Notify(Loc.T("Panele zmieniły się w międzyczasie — przerwano."));
        }

        return unchanged;
    }

    /// <summary>
    /// Asks a plugin panel for its receivers or providers on the thread pool, with the 5 s limit a waiting dialog
    /// allows; failures become a message and <c>null</c>.
    /// </summary>
    private async Task<IReadOnlyList<T>?> AskPanelAsync<T>(string title, Func<CancellationToken, Task<IReadOnlyList<T>>> ask)
    {
        var asking = Loc.T("Pytam wtyczkę „{0}”…", title);
        Notify(asking);
        using var timeout = new CancellationTokenSource(PanelQuestionTimeout);
        try
        {
            // The executor turns a synchronous throw from the session into a faulted task (never out of a Task.Run lambda).
            var answer = await _host!.Executor.RunAsync(ask, timeout.Token, PanelQuestionTimeout);
            if (LastNotification == asking)
            {
                _notification.Text = string.Empty;
            }

            return answer;
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && timeout.IsCancellationRequested))
        {
            Notify(Loc.T("Panel „{0}” nie odpowiedział w ciągu 5 s.", title), isError: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Includes a cancellation that comes from the plugin itself rather than from our time limit.
            Notify(Loc.T("{0}: {1}", title, ex.Message), isError: true);
        }

        return null;
    }

    private static readonly TimeSpan PanelQuestionTimeout = TimeSpan.FromSeconds(5);

    private static TransferItem ToTransferItem(VfsEntry entry) => new(
        entry.Path,
        entry.Name,
        entry.IsContainer && entry.Kind != VfsEntryKind.Archive ? TransferItemKind.Folder : TransferItemKind.File,
        entry.Size);

    /// <summary>Alt+F5: the format follows the name typed (.zip, .tar, .tar.gz/.tgz); the archive goes to the other panel.</summary>
    private async ValueTask PackAsync(CommandInvocation _)
    {
        var sources = _active.ActiveTab.MarkedOrCurrent();
        if (sources.Count == 0)
        {
            return;
        }

        var router = _host!.Router;
        var defaultName = (sources.Count == 1 ? Path.GetFileNameWithoutExtension(sources[0].Name) : _active.ActiveTab.Title) + ".zip";
        var target = await new InputDialog(_skins!.Current, Loc.T("Spakuj"),
            Loc.T("Spakuj {0} do (rozszerzenie wybiera format: .zip, .tar, .tar.gz):", DescribeSelection(sources)),
            router.Combine(Other.ActiveTab.Path, defaultName)).ShowAsync(this);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var archives = _host.Archives;
        var name = target;
        RunJob(new VfsPackJob(router, (output, inputs, progress, ct) => archives.PackAsync(output, inputs, progress, ct, name), sources, target), queue: null, refreshBoth: true);
        _active.ActiveTab.Marked.Clear();
    }

    /// <summary>Alt+F9: extracts the whole archive into a folder named after it in the other panel.</summary>
    private async ValueTask UnpackAsync(CommandInvocation _)
    {
        if (_active.ActiveTab.CurrentEntry is not { Kind: VfsEntryKind.File } archive || !ArchiveVfsPlugin.IsArchiveName(archive.Name))
        {
            Notify(Loc.T("Ustaw kursor na archiwum (.zip, .7z, .tar.gz, .rar…)."), isError: true);
            return;
        }

        var router = _host!.Router;
        var folderName = archive.Name[..^(ArchiveExtensionLength(archive.Name))];
        var target = await new InputDialog(_skins!.Current, Loc.T("Rozpakuj"), Loc.T("Rozpakuj „{0}” do:", archive.Name),
            router.Combine(Other.ActiveTab.Path, folderName)).ShowAsync(this);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var root = ArchiveVfsPlugin.RootPath(archive.Path);
        RunJob(new DelegateJob(Loc.T("Rozpakowywanie „{0}” → {1}", archive.Name, target), async (progress, ct) =>
        {
            var entries = new List<VfsEntry>();
            await foreach (var entry in router.EnumerateAsync(root, ct))
            {
                entries.Add(entry);
            }

            if (await router.GetEntryAsync(target, ct) is null)
            {
                await router.CreateDirectoryAsync(target, ct);
            }

            await new VfsTransferJob(router, entries, target, TransferMode.Copy, OverwritePolicy.Skip).ExecuteAsync(progress, ct);
        }), queue: null, refreshBoth: true);
    }

    private static int ArchiveExtensionLength(string name)
    {
        foreach (var extension in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension.Length;
            }
        }

        return Path.GetExtension(name).Length;
    }

    private async ValueTask MakeDirectoryAsync(CommandInvocation _)
    {
        var tab = _active.ActiveTab;
        var name = await new InputDialog(_skins!.Current, Loc.T("Nowy katalog"), Loc.T("Utwórz katalog w {0}:", tab.Path)).ShowAsync(this);
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

        if (_host!.Settings.ConfirmDelete && !await new ConfirmDialog(_skins!.Current, Loc.T("Usuń"), Loc.T("Usunąć {0}? Tej operacji nie można cofnąć.", DescribeSelection(targets)), Loc.T("Usuń")).ShowAsync(this))
        {
            return;
        }

        RunJob(new VfsDeleteJob(_host!.Router, targets), queue: null, refreshBoth: true);
        _active.ActiveTab.Marked.Clear();
    }

    /// <param name="finished">Runs on the UI thread once the job has ended, whatever its state.</param>
    private void RunJob(IJob job, string? queue, bool refreshBoth, Action<JobHandle>? finished = null)
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

                finished?.Invoke(handle);

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
        var window = new SearchWindow(_host!, _skins!.Current, panel.ActiveTab.Path, entry =>
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
        });
        FollowSkin(window, window.ApplySkin);
        window.Show(this);
    }

    private async ValueTask ShowDriveMenuAsync(FilePanelView panel)
    {
        SetActive(panel);
        var items = new List<PickerItem>();
        await foreach (var drive in _host!.Router.EnumerateAsync(LocalFileSystemPlugin.RootPath, CancellationToken.None))
        {
            items.Add(new PickerItem(drive.Name, drive.Description, drive.Path));
        }

        // Network locations right after the drives: the LAN, saved connections, Explorer's network locations and
        // network folders visited recently (frecency), each once.
        items.Add(new PickerItem(Loc.T("🖧 Sieć"), Loc.T("net:// – hosty w sieci LAN, udziały SMB, SFTP/FTP"), NetworkNeighborhoodVfsPlugin.RootPath));
        var network = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddNetwork(string label, string detail, string path)
        {
            if (network.Add(path.TrimEnd('\\', '/')))
            {
                items.Add(new PickerItem(label, detail, path, Loc.T("sieć lokalizacja")));
            }
        }

        foreach (var connection in _host.Settings.Connections)
        {
            AddNetwork("🔗 " + connection.Name, Loc.T("Zapisane połączenie · {0}", connection.Path), connection.Path);
        }

        var (locations, recent) = await Task.Run(async () =>
        {
            var explorer = NetworkLocations.Read();
            var visited = _host.Index is { } index ? await index.GetFrecentAsync(null, 200) : [];
            return (explorer, visited.Where(f => IsNetworkPath(f.Path)).Take(8).ToList());
        });
        foreach (var location in locations)
        {
            AddNetwork("📁 " + location.Name, Loc.T("Lokalizacja sieciowa Windows · {0}", location.Path), location.Path);
        }

        foreach (var visited in recent)
        {
            AddNetwork("🕘 " + visited.Path, Loc.T("Ostatnio odwiedzane w sieci"), visited.Path);
        }

        items.Add(new PickerItem(Loc.T("➕ Dodaj połączenie sieciowe…"), Loc.T("SMB, SFTP lub FTP — zostanie zapisane na tej liście"), AddConnectionChoice));
        items.Add(new PickerItem(Loc.T("Rejestr Windows"), "reg://", "reg://"));
        items.AddRange(_host.PanelPlugins.Select(p => new PickerItem("Panel: " + p.Title, p.Description, CommandIds.PanelPrefix + p.Id)));

        var choice = await new PickerDialog(_skins!.Current, Loc.T("Zmień dysk"), items).ShowAsync(this);
        if (choice is null)
        {
            return;
        }

        if (choice.Value.StartsWith(CommandIds.PanelPrefix, StringComparison.Ordinal))
        {
            await ExecuteAsync(choice.Value);
            return;
        }

        if (choice.Value == AddConnectionChoice)
        {
            await ExecuteAsync(CommandIds.AddConnection);
            return;
        }

        panel.CloseMode();
        await panel.NavigateAsync(choice.Value);
    }

    private const string AddConnectionChoice = "cmd:add-connection";

    /// <summary>UNC shares and remote servers (not local disks, the registry or archives on a local disk).</summary>
    private static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("net://", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a panel plugin in the opposite panel (like Total Commander's quick view), so the file list the user is in
    /// stays visible and remains the plugin's context; a panel already showing a plugin is reused.
    /// File-bound panels (log analysis) get the file under the cursor as their argument.
    /// </summary>
    private Task OpenPanelAsync(string pluginId, string? argument) => OpenPanelInAsync(pluginId, argument, null);

    /// <param name="target">Panel to show the plugin in; <c>null</c> = the usual mode target (see <see cref="ModeTarget"/>).</param>
    /// <param name="options">JSON from a folder's c4k.dir, passed to the plugin with the request.</param>
    private Task OpenPanelInAsync(string pluginId, string? argument, FilePanelView? target, string? options = null)
    {
        var host = _host!;
        if (host.FindPanel(pluginId) is not { } plugin)
        {
            Notify(Loc.T("Nie znaleziono panelu „{0}”.", pluginId), isError: true);
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
                Notify(Loc.T("{0}: ustaw kursor na pliku i spróbuj ponownie.", plugin.Title), isError: true);
                return Task.CompletedTask;
            }
        }

        var request = new PanelRequest(argument, null, options);
        var services = new PluginPanelServices(
            (title, message) => new ConfirmDialog(_skins!.Current, title, message).ShowAsync(this),
            ShowTextWindowAsync,
            Notify);
        target.ShowMode(new PluginPanelView(plugin, plugin.CreateSession(request, host.HostFor(plugin.Manifest)), request, _skins!.Current, services));
        SetActive(target);
        return Task.CompletedTask;
    }

    private async Task ChooseNewTabAsync(FilePanelView panel)
    {
        SetActive(panel);
        var items = new List<PickerItem>();
        await foreach (var drive in _host!.Router.EnumerateAsync(LocalFileSystemPlugin.RootPath, CancellationToken.None))
        {
            if (drive.Kind == VfsEntryKind.Drive)
            {
                items.Add(new PickerItem(drive.Name, drive.Description, drive.Path));
            }
        }

        items.Add(new PickerItem(string.Empty, null, "separator", IsSeparator: true));
        items.AddRange(_host.PanelPlugins.Select(plugin =>
            new PickerItem(plugin.Title, plugin.Description, CommandIds.PanelPrefix + plugin.Id, plugin.Manifest.Name)));

        var choice = await new PickerDialog(
            _skins!.Current,
            Loc.T("Nowa zakładka"),
            items,
            Loc.T("Wybierz dysk albo plugin panelu.")).ShowAsync(this);
        if (choice is null)
        {
            return;
        }

        if (choice.Value.StartsWith(CommandIds.PanelPrefix, StringComparison.Ordinal))
        {
            panel.AddPluginTab(panel.ActiveTab.Path, choice.Value[CommandIds.PanelPrefix.Length..]);
        }
        else
        {
            panel.AddTab(choice.Value);
        }
    }

    private void ShowPluginTab(FilePanelView panel, PanelTab tab)
    {
        var pluginId = panel.PluginTabId(tab);
        if (pluginId is null || _host!.FindPanel(pluginId) is not { } plugin)
        {
            Notify(Loc.T("Nie znaleziono panelu pluginu dla tej zakładki."), isError: true);
            panel.CancelPluginTab(tab);
            return;
        }

        var source = panel == _left ? _right : _left;
        var argument = source.ActiveTab.CurrentEntry is { Kind: VfsEntryKind.File } file ? file.Path
            : tab.CurrentEntry is { Kind: VfsEntryKind.File } tabFile ? tabFile.Path
            : null;
        if (plugin.RequiresArgument && argument is null)
        {
            Notify(Loc.T("{0}: ustaw kursor na pliku i spróbuj ponownie.", plugin.Title), isError: true);
            panel.CancelPluginTab(tab);
            return;
        }

        var request = new PanelRequest(argument, null);
        var services = new PluginPanelServices(
            (title, message) => new ConfirmDialog(_skins!.Current, title, message).ShowAsync(this),
            ShowTextWindowAsync,
            Notify);
        panel.SetPluginTabMode(tab, new PluginPanelView(
            plugin,
            plugin.CreateSession(request, _host.HostFor(plugin.Manifest)),
            request,
            _skins!.Current,
            services));
        SetActive(panel);
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

        target.ShowMode(CreatePreview(source));
        SetActive(source);
    }

    private void SetPanelMode(FilePanelView panel, bool preview)
    {
        if (!preview)
        {
            panel.CloseMode();
            SetActive(panel);
            return;
        }

        var source = panel == _left ? _right : _left;
        if (panel.Mode is not PreviewPanelView)
        {
            panel.ShowMode(CreatePreview(source));
        }

        SetActive(source);
    }

    /// <summary>F2: choose what the opposite panel shows — files, quick view or any plugin panel.</summary>
    private async ValueTask ShowPanelModesAsync(CommandInvocation _)
    {
        var host = _host!;
        var (target, _) = ModeTarget();
        var side = target == _left ? Loc.T("lewy") : Loc.T("prawy");
        var items = new List<PickerItem>
        {
            new(Loc.T("Pliki"), Loc.T("lista plików (zamyka tryb panelu)"), "files", "lista katalog"),
            new(Loc.T("Podgląd (Ctrl+Q)"), Loc.T("markdown z edycją, tekst, obrazy, rozmiary folderów — dla pliku pod kursorem"), "preview", "quick view markdown"),
            new(Loc.T("Przeglądarka (Ctrl+Alt+W)"), Loc.T("strony WWW, wyszukiwarki, ChatGPT / Gemini / Claude — logowanie jest zapamiętywane"), "browser", "www internet web ai"),
        };
        items.AddRange(host.PanelPlugins.Select(p => new PickerItem(p.Title, p.Description, CommandIds.PanelPrefix + p.Id, p.Manifest.Name)));

        var choice = await new PickerDialog(_skins!.Current, Loc.T("Tryb panelu ({0})", side), items, Loc.T("Co ma pokazywać {0} panel?", side)).ShowAsync(this);
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
        var window = new TextWindow(_skins!.Current, title, text);
        FollowSkin(window, window.ApplySkin);
        window.Show(this);
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

        var pluginMenu = new MenuItem { Header = Loc.T("_Wtyczki") };
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
            blocked > 0 ? Loc.T("Menedżer wtyczek… ({0} nieaktywnych)", blocked) : Loc.T("Menedżer wtyczek…")));

        _menu.Items.Clear();
        _menu.Items.Add(Group(Loc.T("_Plik"), [Item(CommandIds.Copy), Item(CommandIds.Move), Item(CommandIds.MakeDirectory), Item(CommandIds.Delete), new Separator(),
            Item(CommandIds.NewTab), Item(CommandIds.CloseTab), new Separator(), Item(CommandIds.Exit)]));
        _menu.Items.Add(Group(Loc.T("P_olecenia"), [Item(CommandIds.SearchAsync), Item(CommandIds.GoToFrecent), Item(CommandIds.CalculateDirSizes), Item(CommandIds.Compare),
            Item(CommandIds.OpenTerminal), Item(CommandIds.OpenInVsCode), Item(CommandIds.Refresh), Item(CommandIds.ToggleHidden), new Separator(), Item(CommandIds.AddConnection)]));
        _menu.Items.Add(Group(Loc.T("Pa_nele"), [Item(CommandIds.LeftDriveMenu), Item(CommandIds.RightDriveMenu), Item(CommandIds.OpenNetwork), Item(CommandIds.OpenRegistry), new Separator(),
            .. host.PanelPlugins.Select(p => Item(CommandIds.PanelPrefix + p.Id, p.Title))]));
        _menu.Items.Add(pluginMenu);
        _menu.Items.Add(Group(Loc.T("_Konfiguracja"), [Item(CommandIds.Settings), Item(CommandIds.SelectSkin), Item(CommandIds.CommandPalette)]));
        _menu.Items.Add(Group(Loc.T("Pomo_c"), [Item(CommandIds.Help), Item(CommandIds.CheckForUpdates), Item(CommandIds.About)]));
        if (_skins is not null)
        {
            ApplyMenuSkin(_skins.Current);
        }
    }

    /// <summary>Design system: the menu sits on the window background; the palette search box is a muted header-coloured field.</summary>
    private void ApplyMenuSkin(SkinBrushes b)
    {
        _menuBar.Background = b.IsModern ? b.WindowBackground : b.HeaderBackground;
        _menu.Background = Brushes.Transparent;
        _menu.Foreground = b.IsModern ? b.WindowForeground : b.HeaderForeground;
        _menu.FontFamily = b.FontFamily;
        _menu.FontSize = b.FontSize;
        _paletteButton.IsVisible = b.IsModern;
        _paletteButton.Background = b.HeaderBackground;
        _paletteButton.BorderBrush = b.PanelBorder;
        _paletteButton.BorderThickness = new Thickness(1);
        _paletteButton.CornerRadius = new CornerRadius(6);
        _paletteButton.Foreground = b.MutedForeground;
        if (_paletteButton.Content is Grid { Children: [_, _, Border shortcut] })
        {
            shortcut.BorderBrush = b.PanelBorder;
            shortcut.Background = b.PanelBackground;
            ((TextBlock)shortcut.Child!).Foreground = b.HeaderForeground;
        }
    }

    private async ValueTask GoToFrecentAsync(CommandInvocation _)
    {
        if (_host!.Index is not { } index)
        {
            Notify(Loc.T("Baza częstych lokalizacji jest niedostępna."), isError: true);
            return;
        }

        var frecent = await index.GetFrecentAsync(null, 60);
        var items = frecent.Select(f => new PickerItem(f.Path, $"{f.Visits}× · ostatnio {f.LastVisit.ToLocalTime():g}", f.Path)).ToList();
        var choice = await new PickerDialog(_skins!.Current, Loc.T("Częste lokalizacje (Ctrl+G)"), items, Loc.T("Najczęściej i najświeżej odwiedzane katalogi:")).ShowAsync(this);
        if (choice is not null)
        {
            await _active.NavigateAsync(choice.Value);
        }
    }

    private async ValueTask AddConnectionAsync(CommandInvocation _)
    {
        var result = await new ConnectionDialog(_skins!.Current, TestConnectionAsync).ShowAsync(this);
        if (result is null)
        {
            return;
        }

        var connection = result.Setting;
        _host!.Settings.Connections.Add(connection);
        _host.ApplySettings();
        await _host.SaveSettingsAsync();
        if (!await PrepareConnectionAsync(connection, result.Secret))
        {
            Notify(Loc.T("Zapisano „{0}”, ale serwer odrzucił login — spróbuj ponownie przy otwieraniu.", connection.Name), isError: true);
        }

        // Straight into the new connection, in the active file panel.
        var target = _active.Mode is null ? _active : Other;
        target.CloseMode();
        SetActive(target);
        await target.NavigateAsync(connection.Path);
    }

    private async Task ConfigureSftpConnectionAsync(FilePanelView source, string path)
    {
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase))
        {
            Notify(Loc.T("Nieprawidłowy adres SFTP: {0}", path), isError: true);
            return;
        }

        var initial = new ConnectionSetting
        {
            Name = uri.Host,
            Path = path,
            UserName = RemoteTransferVfsPlugin.UserNameOf(path),
        };
        var result = await new ConnectionDialog(_skins!.Current, TestConnectionAsync, initial).ShowAsync(this);
        if (result is null)
        {
            return;
        }

        var connection = result.Setting;
        var realm = RemoteTransferVfsPlugin.RealmFor(connection.Path);
        var existing = _host!.Settings.Connections.FindIndex(saved =>
            string.Equals(RemoteTransferVfsPlugin.RealmFor(saved.Path), realm, StringComparison.OrdinalIgnoreCase));
        if (existing < 0)
        {
            _host.Settings.Connections.Add(connection);
        }
        else
        {
            _host.Settings.Connections[existing] = connection;
        }

        _host.ApplySettings();
        await _host.SaveSettingsAsync();
        if (!await PrepareConnectionAsync(connection, result.Secret))
        {
            Notify(Loc.T("Zapisano „{0}”, ale serwer odrzucił login — spróbuj ponownie przy otwieraniu.", connection.Name), isError: true);
        }

        source.CloseMode();
        SetActive(source);
        await source.NavigateAsync(connection.Path);
    }

    private async Task AddLanHostAsync()
    {
        var host = await new LanHostDialog(_skins!.Current).ShowAsync(this);
        if (host is null)
        {
            return;
        }

        var hosts = _host!.Settings.LanHosts;
        var existing = hosts.FindIndex(saved => string.Equals(saved.Host, host.Host, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            host.Services.InsertRange(0, hosts[existing].Services.Where(old =>
                host.Services.All(service => !SameService(old, service))));
            hosts[existing] = host;
        }
        else
        {
            hosts.Add(host);
        }

        _host.ApplySettings();
        await _host.SaveSettingsAsync();
        Notify(host.Services.Count == 0
            ? Loc.T("Dodano host LAN: {0}.", host.Name)
            : Loc.T("Dodano usługę do hosta LAN: {0}.", host.Name));
        await ReloadLanPanelsAsync();
    }

    private async Task ConfigureLanScanAsync()
    {
        var host = _host!;
        var includePopularPorts = await new LanScanDialog(_skins!.Current, host.Settings.LanScanPopularPorts).ShowAsync(this);
        if (includePopularPorts is null)
        {
            return;
        }

        host.Settings.LanScanPopularPorts = includePopularPorts.Value;
        host.ApplySettings();
        await host.SaveSettingsAsync();
        await ReloadLanPanelsAsync();
    }

    private async Task ReloadLanPanelsAsync()
    {
        var tabs = new[] { _left, _right }
            .Where(panel => string.Equals(panel.ActiveTab.Path, NetworkNeighborhoodVfsPlugin.LanPath, StringComparison.OrdinalIgnoreCase))
            .Select(panel => panel.ActiveTab)
            .ToArray();
        foreach (var tab in tabs)
        {
            tab.SetBusy(NetworkNeighborhoodVfsPlugin.ScanLanPath, true);
        }

        try
        {
            await Task.WhenAll(tabs.Select(tab =>
                tab.NavigateAsync(NetworkNeighborhoodVfsPlugin.LanPath, tab.CurrentEntry?.Name)));
        }
        finally
        {
            foreach (var tab in tabs)
            {
                tab.SetBusy(NetworkNeighborhoodVfsPlugin.ScanLanPath, false);
            }
        }
    }

    private static bool SameService(LanHostServiceSetting left, LanHostServiceSetting right) =>
        left.Port == right.Port && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase);

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
        Notify(Loc.T("Skórka: {0}", next.DisplayName));
    }

    private async ValueTask ToggleHiddenAsync(CommandInvocation _)
    {
        _host!.Settings.ShowHidden = !_host.Settings.ShowHidden;
        Notify(_host.Settings.ShowHidden ? Loc.T("Pokazuję pliki ukryte.") : Loc.T("Ukrywam pliki ukryte i systemowe."));
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

        // A share with a saved login: connect with it first, ask only if that fails.
        if (VfsPath.GetScheme(ex.Path) is not ("sftp" or "ftp") && SavedLoginFor(realm) is { } saved && await TryConnectShareAsync(realm, saved.User, saved.Password))
        {
            await request.Tab.NavigateAsync(request.Tab.Path);
            return;
        }

        var isSsh = VfsPath.GetScheme(ex.Path) == "sftp";
        var credential = await new LoginDialog(_skins!.Current, realm, string.IsNullOrEmpty(user) ? null : user, allowKey: isSsh).ShowAsync(this);
        if (credential is null)
        {
            return;
        }

        try
        {
            if (VfsPath.GetScheme(ex.Path) is "sftp" or "ftp")
            {
                _host!.Remote.SetLogin(realm, credential.KeyFile is { } key
                    ? new RemoteLogin(credential.UserName, PrivateKeyFile: key, Passphrase: credential.Password.Length > 0 ? credential.Password : null)
                    : new RemoteLogin(credential.UserName, credential.Password));
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

    /// <summary>The saved Windows-share login whose path contains <paramref name="realm"/> (user + decrypted password).</summary>
    private (string User, string Password)? SavedLoginFor(string realm)
    {
        foreach (var connection in _host!.Settings.Connections)
        {
            if (connection.Auth == "password" && connection.UserName is { } user && SecretProtector.Unprotect(connection.Secret) is { } password
                && (realm.StartsWith(connection.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || connection.Path.StartsWith(realm.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                return (user, password);
            }
        }

        return null;
    }

    /// <summary>WNetAddConnection2 for a share, off the UI thread; <c>false</c> when the server refuses or is unreachable.</summary>
    private async Task<bool> TryConnectShareAsync(string path, string? user, string? password)
    {
        try
        {
            await _host!.Executor.RunAsync(
                _ =>
                {
                    _host.Network.Connect(path, user, password);
                    return true;
                },
                CancellationToken.None,
                TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or IOException)
        {
            return false;
        }
    }

    /// <summary>"Test" in the connection dialog: registers the login for this session and lists the start folder.</summary>
    private async Task<string?> TestConnectionAsync(ConnectionDialogResult result)
    {
        var setting = result.Setting;
        try
        {
            if (!await PrepareConnectionAsync(setting, result.Secret))
            {
                return Loc.T("Serwer odrzucił login albo jest nieosiągalny.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var reader = _host!.Router.OpenListing(setting.Path, timeout.Token);
            var count = 0;
            await foreach (var entry in reader.ReadAllAsync(timeout.Token))
            {
                if (++count >= 1)
                {
                    break;
                }
            }

            return null;
        }
        catch (VfsAuthenticationRequiredException)
        {
            return Loc.T("Serwer odrzucił login (sprawdź użytkownika, hasło albo klucz).");
        }
        catch (OperationCanceledException)
        {
            return Loc.T("Brak odpowiedzi w ciągu 20 s.");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Makes a connection usable now: SFTP/FTP logins go to the remote plugin, shares are connected.</summary>
    private async Task<bool> PrepareConnectionAsync(ConnectionSetting setting, string? secret)
    {
        if (VfsPath.GetScheme(setting.Path) is "sftp" or "ftp")
        {
            if (setting.Auth is not null)
            {
                _host!.Remote.SetLogin(setting.Path, setting.Auth == "key"
                    ? new RemoteLogin(setting.UserName ?? Environment.UserName, PrivateKeyFile: setting.KeyFile, Passphrase: secret)
                    : new RemoteLogin(setting.UserName ?? Environment.UserName, Password: secret ?? string.Empty));
            }

            return true;
        }

        return setting.Auth != "password" || await TryConnectShareAsync(setting.Path, setting.UserName, secret);
    }

    /// <summary>Mail password for an interactive account; called from background threads, the dialog runs on the UI thread.</summary>
    private Task<string?> AskMailPasswordAsync(string realm, string user) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var credential = await new LoginDialog(_skins!.Current, realm, user, lockUser: true).ShowAsync(this);
            return credential?.Password;
        });

    /// <summary>A mail server certificate that failed validation: trusted only after the user compares the fingerprint.</summary>
    private Task<bool> ConfirmMailCertificateAsync(string hostPort, string fingerprint) =>
        Dispatcher.UIThread.InvokeAsync(() => new ConfirmDialog(
            _skins!.Current,
            Loc.T("Certyfikat serwera poczty"),
            Loc.T("Serwer {0} przedstawił certyfikat, którego system nie uznaje za zaufany. Sprawdź odcisk SHA-256 z administratorem serwera, zanim mu zaufasz.\n\nSHA-256:\n{1}", hostPort, fingerprint),
            Loc.T("Zaufaj certyfikatowi")).ShowAsync(this));

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
        entries.Count == 1 ? Loc.T("„{0}”", entries[0].Name) : Loc.P(entries.Count, "{0} zaznaczony element|{0} zaznaczone elementy|{0} zaznaczonych elementów");
}
