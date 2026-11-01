using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Connections;
using Commander4K.App.Preview;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.App.Panels;

public sealed record AuthenticationRequest(PanelTab Tab, VfsAuthenticationRequiredException Exception);

public sealed record PanelModeRequest(FilePanelView Panel, bool Preview);

/// <summary>One side of the dual-pane layout: tab strip, path bar, owner-drawn list and status line.</summary>
public sealed class FilePanelView : ViewBase
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;
    private readonly List<PanelTab> _tabs = [];
    private readonly Dictionary<PanelTab, string> _pluginTabIds = [];
    private readonly Dictionary<PanelTab, IPanelModeView> _pluginTabModes = [];
    private int _activeIndex;
    private bool _isActive;
    private string? _tabsKey;
    private bool _statusScheduled;
    private bool _updatingModeSwitch;

    private Border _tabBorder = null!;
    private StackPanel _tabStrip = null!;
    private Border _pathBorder = null!;
    private TextBox _pathBox = null!;
    private TextBlock _filesModeLabel = null!;
    private ToggleSwitch _modeSwitch = null!;
    private TextBlock _previewModeLabel = null!;
    private Border _statusBorder = null!;
    private Border _emptyFolderSetup = null!;
    private TextBlock _emptyFolderMessage = null!;
    private Button _emptyFolderConfigure = null!;
    private Panel _content = null!;
    private TextBlock _status = null!;
    private TextBlock _statusMarked = null!;
    private StackPanel _statusMarkedBox = null!;
    private Border _statusMarkedEdge = null!;
    private Border _card = null!;
    private Border _accentEdge = null!;

    public FilePanelView(AppHost host, SkinManager skins, string initialPath)
    {
        _host = host;
        _skins = skins;
        Canvas.Activated += () =>
        {
            // The canvas is not focusable: a click must take focus away from the command line or path bar,
            // otherwise cursor keys keep going to that text box.
            LeavePathBar();
            Activated?.Invoke(this);
        };
        Canvas.RowActivated += Open;
        Canvas.HeaderClicked += column => ActiveTab.SetSort(ActiveTab.Sort.Toggle(column));
        Canvas.ColumnWidths = host.Settings.ColumnWidths;
        Canvas.RightClicked += entry =>
        {
            LeavePathBar();
            Activated?.Invoke(this);
            if (_host.Settings.ShellContextMenu)
            {
                ContextMenuRequested?.Invoke(entry);
            }
            else
            {
                ActiveTab.ToggleMark(entry); // Total Commander: right click marks.
            }
        };
        AddTab(initialPath);
        skins.Changed += ApplySkin;
    }

    public FileViewportCanvas Canvas { get; } = new();

    /// <summary>Mode shown over the file list (plugin panel, preview…), or <c>null</c> in file mode.</summary>
    public IPanelModeView? Mode { get; private set; }

    /// <summary>The folder-view hint shown over the list, or <c>null</c>.</summary>
    public FolderSuggestionCard? Suggestion { get; private set; }

    /// <summary>Shows <paramref name="card"/> in the list's corner (replacing any other); <c>null</c> hides it.</summary>
    public void ShowSuggestion(FolderSuggestionCard? card)
    {
        if (Suggestion is { } old)
        {
            _content.Children.Remove(old);
        }

        Suggestion = card;
        if (card is not null)
        {
            _content.Children.Add(card);
        }
    }

    /// <summary>Replaces the file list with a mode until <see cref="CloseMode"/> (Esc / Backspace).</summary>
    public void ShowMode(IPanelModeView view)
    {
        ShowSuggestion(null);
        CloseMode();
        AttachMode(view);
        UpdateModeButtons();
    }

    private void AttachMode(IPanelModeView view)
    {
        Mode = view;
        view.IsActivePanel = _isActive;
        view.Activated += () => Activated?.Invoke(this);
        view.CloseRequested += CloseMode;
        _content.Children.Add(view.View);
        Canvas.IsVisible = false;
        _statusBorder.IsVisible = false;
        _pathBox.Text = "⚙ " + view.Title;
        _pathBox.IsReadOnly = true;
        UpdateModeButtons();
        if (view is ITitledModeView titled)
        {
            titled.TitleChanged += () =>
            {
                if (Mode == view)
                {
                    _pathBox.Text = "⚙ " + view.Title;
                }
            };
        }
    }

    public void CloseMode()
    {
        if (Mode is not { } view)
        {
            return;
        }

        Mode = null;
        _content.Children.Remove(view.View);
        var pluginTab = _pluginTabModes.FirstOrDefault(pair => pair.Value == view).Key;
        if (pluginTab is not null)
        {
            _pluginTabModes.Remove(pluginTab);
            _pluginTabIds.Remove(pluginTab);
            RebuildTabs();
        }

        _ = view.DisposeModeAsync();
        Canvas.IsVisible = true;
        _statusBorder.IsVisible = true;
        _pathBox.IsReadOnly = false;
        _pathBox.Text = ActiveTab.Path;
        UpdateModeButtons();
    }

    private void DetachPluginTabMode()
    {
        if (Mode is null || !_pluginTabModes.Values.Contains(Mode))
        {
            return;
        }

        _content.Children.Remove(Mode.View);
        Mode = null;
        Canvas.IsVisible = true;
        _statusBorder.IsVisible = true;
        _pathBox.IsReadOnly = false;
        _pathBox.Text = ActiveTab.Path;
        UpdateModeButtons();
    }

    private void ActivatePluginTabMode(PanelTab tab, IPanelModeView mode)
    {
        if (Mode == mode)
        {
            return;
        }

        DetachPluginTabMode();
        if (Mode is not null)
        {
            CloseMode();
        }

        ShowSuggestion(null);
        AttachMode(mode);
    }

    public PanelTab ActiveTab => _tabs[_activeIndex];

    public IReadOnlyList<PanelTab> Tabs => _tabs;

    public string? PluginTabId(PanelTab tab) => _pluginTabIds.GetValueOrDefault(tab);

    public void CancelPluginTab(PanelTab tab)
    {
        if (!_pluginTabIds.ContainsKey(tab))
        {
            return;
        }

        if (tab == ActiveTab && _tabs.Count > 1)
        {
            CloseActiveTab();
            return;
        }

        _pluginTabIds.Remove(tab);
        if (_pluginTabModes.Remove(tab, out var mode))
        {
            if (Mode == mode)
            {
                CloseMode();
            }
            else
            {
                _ = mode.DisposeModeAsync();
            }
        }

        RebuildTabs();
        OnTabChanged();
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            Canvas.IsActivePanel = value;
            if (Mode is not null)
            {
                Mode.IsActivePanel = value;
            }
            ApplyChrome();
        }
    }

    public event Action<FilePanelView>? Activated;

    /// <summary>Raised when another tab becomes the active one (the quick preview follows it).</summary>
    public event Action? ActiveTabChanged;

    public event Action? NewTabRequested;

    public event Action<PanelTab>? PluginTabSelected;

    // Single-parameter events: the declarative source generator mis-generates extensions for multi-parameter ones.

    /// <summary>Enter / double-click on a file (as opposed to a container).</summary>
    public event Action<VfsEntry>? FileActivated;

    /// <summary>Right click with the Windows context menu enabled; the window shows the menu for this entry (or the marked set).</summary>
    public event Action<VfsEntry>? ContextMenuRequested;

    /// <summary>Enter on a folder, drive or share; the window decides (a <c>c4k.dir</c> may open something else) and calls <see cref="NavigateAsync"/>.</summary>
    public event Action<VfsEntry>? FolderEntering;

    /// <summary>Enter on the "+ Add network connection" row.</summary>
    public event Action<VfsEntry>? ActionRequested;

    /// <summary>The ▾ button on the path bar: the window shows the location menu (drives, This PC, network, connections).</summary>
    public event Action<FilePanelView>? LocationMenuRequested;

    /// <summary>The panel's files/preview switch was toggled.</summary>
    public event Action<PanelModeRequest>? ModeRequested;

    /// <summary>Configure the current empty folder as a special folder.</summary>
    public event Action<string>? ConfigureEmptyFolderRequested;

    private Button _locationButton = null!;

    public event Action<AuthenticationRequest>? AuthenticationRequired;

    protected override object Build()
    {
        var root = CreateLayout();
        _pathBox.KeyDown += OnPathKeyDown;
        _pathBox.GotFocus += (_, _) => Activated?.Invoke(this);
        _modeSwitch.IsCheckedChanged += (_, _) =>
        {
            if (!_updatingModeSwitch)
            {
                ModeRequested?.Invoke(new(this, Preview: _modeSwitch.IsChecked == true));
            }
        };
        ApplySkin(_skins.Current);
        RebuildTabs();
        OnTabChanged();
        return root;
    }

    /// <summary>
    /// Design system: a card (6 px corners, 1 px border) with a 32 px tab bar, a 32 px path bar (active panel:
    /// tinted with a 3 px accent edge), the list, and a 26 px status bar with the marked set on the right.
    /// </summary>
    private Border CreateLayout() =>
        new Border()
            .Ref(out _card)
            .ClipToBounds(true)
            .Child(new Grid()
                .Rows("Auto,Auto,*,Auto")
                .Children(
                    new Border()
                        .Ref(out _tabBorder)
                        .Row(0)
                        .Child(new StackPanel().Ref(out _tabStrip).Orientation(Orientation.Horizontal).Spacing(2).VerticalAlignment(VerticalAlignment.Center)),
                    new Border()
                        .Ref(out _pathBorder)
                        .Row(1)
                        .Child(new Grid().Cols("Auto,*,Auto,Auto,Auto,Auto").Children(
                            new Border().Ref(out _accentEdge).Width(3).Col(0),
                            new TextBox().Ref(out _pathBox).Col(1).BorderThickness(new Thickness(0)).MinHeight(24).Padding(7, 2)
                                .VerticalContentAlignment(VerticalAlignment.Center),
                            new TextBlock().Ref(out _filesModeLabel).Col(2).Text(Loc.T("Pliki"))
                                .VerticalAlignment(VerticalAlignment.Center).Margin(6, 0),
                            new ToggleSwitch().Ref(out _modeSwitch).Col(3).Width(42).MinHeight(24)
                                .HorizontalAlignment(HorizontalAlignment.Center)
                                .VerticalAlignment(VerticalAlignment.Center),
                            new TextBlock().Ref(out _previewModeLabel).Col(4).Text(Loc.T("Podgląd"))
                                .VerticalAlignment(VerticalAlignment.Center).Margin(6, 0),
                            // One click to switch this panel elsewhere: drives, This PC, network, saved and new connections.
                            new Button().Ref(out _locationButton).Col(5).Content("▾").Padding(10, 0).MinWidth(32)
                                .VerticalAlignment(VerticalAlignment.Stretch).VerticalContentAlignment(VerticalAlignment.Center)
                                .BorderThickness(new Thickness(0)).Background(Brushes.Transparent)
                                .OnClick(click => LocationMenuRequested?.Invoke(this)))),
                    new Panel().Ref(out _content).Row(2).Children(
                        Canvas,
                        new Border().Ref(out _emptyFolderSetup).IsVisible(false)
                            .HorizontalAlignment(HorizontalAlignment.Center).VerticalAlignment(VerticalAlignment.Center)
                            .MaxWidth(480).Margin(24).Padding(24)
                            .Background(_skins.Current.PanelBackground).BorderBrush(_skins.Current.PanelBorder)
                            .BorderThickness(1).CornerRadius(new CornerRadius(_skins.Current.IsModern ? 8 : 0))
                            .Child(new StackPanel().Spacing(14).Children(
                                new TextBlock().Ref(out _emptyFolderMessage).TextWrapping(TextWrapping.Wrap)
                                    .TextAlignment(TextAlignment.Center).HorizontalAlignment(HorizontalAlignment.Stretch),
                                new Button().Ref(out _emptyFolderConfigure).Content(Loc.T("Skonfiguruj jako specjalny folder"))
                                    .HorizontalAlignment(HorizontalAlignment.Center).Padding(16, 8)
                                    .OnClick(_ => ConfigureEmptyFolderRequested?.Invoke(ActiveTab.Path))))),
                    new Border()
                        .Ref(out _statusBorder)
                        .Row(3)
                        .Child(new Grid().Cols("*,Auto").Children(
                            new TextBlock().Ref(out _status).TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center),
                            new StackPanel().Ref(out _statusMarkedBox).Col(1).Orientation(Orientation.Horizontal).Spacing(6).IsVisible(false)
                                .VerticalAlignment(VerticalAlignment.Center).Children(
                                    new Border().Ref(out _statusMarkedEdge).Width(3).Height(12).CornerRadius(new CornerRadius(2)),
                                    new TextBlock().Ref(out _statusMarked).FontWeight(FontWeight.SemiBold))))));

    public Task NavigateAsync(string path, string? focusName = null) => ActiveTab.NavigateAsync(path, focusName);

    private IReadOnlyList<ActiveColumn> _columns = [];

    /// <summary>Plugin columns shown in every tab of this panel (from the settings).</summary>
    public IReadOnlyList<ActiveColumn> Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            foreach (var tab in _tabs)
            {
                tab.SetColumns(value);
            }

            Canvas.InvalidateVisual();
        }
    }

    public void AddTab(string path)
    {
        var tab = CreateTab(path);
        SelectTab(_tabs.Count - 1);
        _ = tab.NavigateAsync(path);
    }

    public void AddPluginTab(string path, string pluginId)
    {
        var tab = CreateTab(path);
        _pluginTabIds.Add(tab, pluginId);
        SelectTab(_tabs.Count - 1);
        _ = tab.NavigateAsync(path);
    }

    public void SetPluginTabMode(PanelTab tab, IPanelModeView mode)
    {
        if (!_pluginTabIds.ContainsKey(tab))
        {
            _ = mode.DisposeModeAsync();
            return;
        }

        _pluginTabModes[tab] = mode;
        if (tab == ActiveTab)
        {
            ActivatePluginTabMode(tab, mode);
        }
    }

    private PanelTab CreateTab(string path)
    {
        var tab = new PanelTab(_host, path);
        tab.ColumnData.SetColumns(_columns);
        tab.Changed += OnAnyTabChanged;
        tab.SizesUpdated += () => OnSizesUpdated(tab);
        tab.AuthenticationRequired += (t, ex) => AuthenticationRequired?.Invoke(new AuthenticationRequest(t, ex));
        _tabs.Add(tab);
        return tab;
    }

    public void CloseActiveTab()
    {
        if (_tabs.Count == 1)
        {
            return;
        }

        var tab = ActiveTab;
        if (_pluginTabModes.Remove(tab, out var mode))
        {
            if (Mode == mode)
            {
                CloseMode();
            }
            else
            {
                _ = mode.DisposeModeAsync();
            }
        }

        _pluginTabIds.Remove(tab);
        tab.Detach();
        tab.Changed -= OnAnyTabChanged;
        _tabs.RemoveAt(_activeIndex);
        SelectTab(Math.Min(_activeIndex, _tabs.Count - 1));
    }

    public void SelectTab(int index)
    {
        DetachPluginTabMode();
        _activeIndex = Math.Clamp(index, 0, _tabs.Count - 1);
        Canvas.Tab = ActiveTab;
        ActiveTabChanged?.Invoke();
        if (_pluginTabIds.ContainsKey(ActiveTab))
        {
            if (_pluginTabModes.TryGetValue(ActiveTab, out var mode))
            {
                ActivatePluginTabMode(ActiveTab, mode);
            }
            else
            {
                PluginTabSelected?.Invoke(ActiveTab);
            }
        }

        if (_tabStrip is not null)
        {
            RebuildTabs();
            OnTabChanged();
        }
    }

    /// <summary>Enter: opens containers in place, reports files to the window.</summary>
    public void Open(VfsEntry entry)
    {
        if (PreviewPluginResolver.Resolve(_host.PreviewPlugins, entry) is SftpConnectionPreviewPlugin)
        {
            FileActivated?.Invoke(entry);
            return;
        }

        switch (entry.Kind)
        {
            case VfsEntryKind.ParentLink:
                _ = ActiveTab.NavigateAsync(entry.Path, ActiveTab.Title);
                break;
            case VfsEntryKind.Action:
                ActionRequested?.Invoke(entry);
                break;
            case VfsEntryKind.Service:
                break;
            case VfsEntryKind.Directory or VfsEntryKind.Drive or VfsEntryKind.Share when FolderEntering is not null:
                FolderEntering(entry); // The window checks the folder's c4k.dir first.
                break;
            case var _ when entry.IsContainer:
                _ = ActiveTab.NavigateAsync(entry.Path);
                break;
            default:
                FileActivated?.Invoke(entry);
                break;
        }
    }

    public void GoToParent()
    {
        var parent = ActiveTab.Entries.Count > 0 && ActiveTab.Entries[0].Kind == VfsEntryKind.ParentLink ? ActiveTab.Entries[0] : (VfsEntry?)null;
        if (parent is { } p)
        {
            Open(p);
        }
    }

    /// <summary>Cursor keys; returns <c>false</c> for keys the panel does not handle.</summary>
    public bool HandleNavigationKey(Key key, KeyModifiers modifiers)
    {
        var tab = ActiveTab;
        var page = Canvas.VisibleRows;
        int? target = key switch
        {
            Key.Up => tab.Cursor - 1,
            Key.Down => tab.Cursor + 1,
            Key.PageUp => tab.Cursor - page,
            Key.PageDown => tab.Cursor + page,
            Key.Home => 0,
            Key.End => tab.Entries.Count - 1,
            _ => null,
        };
        if (target is null)
        {
            return false;
        }

        if ((modifiers & KeyModifiers.Shift) != 0 && tab.CurrentEntry is { } current)
        {
            tab.ToggleMark(current);
        }

        tab.MoveCursor(target.Value);
        Canvas.EnsureCursorVisible();
        return true;
    }

    private void OnAnyTabChanged()
    {
        if (_tabStrip is not null)
        {
            OnTabChanged();
        }
    }

    private void OnTabChanged()
    {
        var tab = ActiveTab;
        if (!_pathBox.IsFocused && Mode is null && _pathBox.Text != tab.Path)
        {
            _pathBox.Text = tab.Path;
        }

        UpdateEmptyFolderSetup();
        UpdateStatus();
        // Changed fires on every cursor move and streamed batch: rebuild the tab buttons only when they would differ.
        if (_tabStrip.Children.Count > 0 && TabsKey() != _tabsKey)
        {
            RebuildTabs();
        }
    }

    private void UpdateEmptyFolderSetup()
    {
        if (_emptyFolderSetup is null)
        {
            return;
        }

        var tab = ActiveTab;
        var isDiskOrShare = VfsPath.GetScheme(tab.Path) is VfsPath.FileScheme or VfsPath.SmbScheme
            && !tab.Path.Contains("://", StringComparison.Ordinal);
        var empty = !tab.IsLoading && tab.Error is null && tab.Entries.All(entry =>
            entry.Kind is VfsEntryKind.ParentLink or VfsEntryKind.Action);
        _emptyFolderSetup.IsVisible = isDiskOrShare && empty && Mode is null && !_pluginTabIds.ContainsKey(tab);
        _emptyFolderMessage.Text = Loc.T("Ten folder jest pusty. Wybierz, co ma pokazywać — ustawienia zostaną zapisane w pliku c4k.dir.");
        var brushes = _skins.Current;
        _emptyFolderSetup.Background = brushes.PanelBackground;
        _emptyFolderSetup.BorderBrush = brushes.PanelBorder;
        Ds.Style(_emptyFolderConfigure, brushes, ButtonKind.Primary);
        _emptyFolderMessage.Foreground = brushes.MutedForeground;
    }

    private void UpdateStatus()
    {
        var tab = ActiveTab;
        var (main, marked) = StatusText(tab);
        _status.Text = main;
        _status.Foreground = tab.Error is null ? _skins.Current.StatusForeground : _skins.Current.Error;
        _statusMarked.Text = marked ?? string.Empty;
        _statusMarkedBox.IsVisible = marked is not null;
    }

    /// <summary>Running folder sizes: the list repaints itself; the totals in the status line follow at most 4× a second.</summary>
    private void OnSizesUpdated(PanelTab tab)
    {
        if (tab != ActiveTab || _statusScheduled || _tabStrip is null)
        {
            return;
        }

        _statusScheduled = true;
        DispatcherTimer.RunOnce(() =>
        {
            _statusScheduled = false;
            UpdateStatus();
        }, TimeSpan.FromMilliseconds(250), DispatcherPriority.Background);
    }

    private string TabTitle(PanelTab tab) =>
        _pluginTabIds.TryGetValue(tab, out var pluginId) ? _host.FindPanel(pluginId)?.Title ?? tab.Title : tab.Title;

    private string TabsKey() => _activeIndex + "|" + _skins.Current.Skin.Id + "|" + string.Join('\n', _tabs.Select(TabTitle));

    /// <summary>Counts on the left; the marked set (if any) on the right, in the marking colour.</summary>
    private (string Main, string? Marked) StatusText(PanelTab tab)
    {
        if (tab.Error is { } error)
        {
            return ("⚠ " + error, null);
        }

        if (tab.Status is { } status)
        {
            return (status, null);
        }

        int files = 0, containers = 0;
        long bytes = 0, markedBytes = 0;
        foreach (var entry in tab.Entries)
        {
            if (entry.Kind is VfsEntryKind.ParentLink or VfsEntryKind.Action)
            {
                continue;
            }

            var size = entry.IsContainer ? tab.DirectorySizes.GetValueOrDefault(entry.Path, 0) : Math.Max(0, entry.Size);
            if (entry.IsContainer)
            {
                containers++;
            }
            else
            {
                files++;
                bytes += size;
            }

            if (tab.Marked.Contains(entry.Path))
            {
                markedBytes += size;
            }
        }

        var text = Loc.P(files, "{0:N0} plik ({1})|{0:N0} pliki ({1})|{0:N0} plików ({1})", FileViewportCanvas.FormatSize(bytes))
            + ", " + Loc.P(containers, "{0:N0} katalog|{0:N0} katalogi|{0:N0} katalogów");
        return (text, tab.Marked.Count > 0 ? Loc.T("Zaznaczono {0:N0} · {1}", tab.Marked.Count, FileViewportCanvas.FormatSize(markedBytes)) : null);
    }

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var path = _pathBox.Text?.Trim();
                if (!string.IsNullOrEmpty(path))
                {
                    _ = ActiveTab.NavigateAsync(path);
                }

                LeavePathBar();
                e.Handled = true;
                break;
            case Key.Escape:
                _pathBox.Text = ActiveTab.Path;
                LeavePathBar();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Hands keyboard focus back to the window, where the keymap lives.</summary>
    private void LeavePathBar() => (TopLevel.GetTopLevel(this) as InputElement)?.Focus();

    private void RebuildTabs()
    {
        _tabsKey = TabsKey();
        _tabStrip.Children.Clear();
        var b = _skins.Current;
        if (b.IsModern)
        {
            RebuildModernTabs(b);
            return;
        }

        _tabBorder.IsVisible = true;

        for (var i = 0; i < _tabs.Count; i++)
        {
            var index = i;
            var selected = i == _activeIndex;
            _tabStrip.Children.Add(new Button()
                .Content(TabTitle(_tabs[i]))
                .Padding(10, 2)
                .Margin(0, 0, 2, 0)
                .CornerRadius(new CornerRadius(b.Skin.Chrome.CornerRadius, b.Skin.Chrome.CornerRadius, 0, 0))
                .Background(selected ? b.CursorBackground : b.HeaderBackground)
                .Foreground(selected ? b.CursorForeground : b.HeaderForeground)
                .FontFamily(b.FontFamily)
                .FontSize(b.FontSize - 1)
                .OnClick(_ => SelectTab(index)));
        }

        _tabStrip.Children.Add(new Button().Content("+").Width(24).Height(24).Padding(0).FontSize(14)
            .HorizontalContentAlignment(HorizontalAlignment.Center).VerticalContentAlignment(VerticalAlignment.Center)
            .Background(Brushes.Transparent).BorderThickness(new Thickness(0)).Foreground(b.MutedForeground)
            .OnClick(_ => NewTabRequested?.Invoke()));
    }

    /// <summary>Design system: always-visible tab bar; the current tab raised with a close button, the rest muted, "+" adds one.</summary>
    private void RebuildModernTabs(SkinBrushes b)
    {
        _tabBorder.IsVisible = true;
        var small = Math.Max(11, b.FontSize - 1);
        for (var i = 0; i < _tabs.Count; i++)
        {
            var index = i;
            var selected = i == _activeIndex;
            var title = new TextBlock().Text(TabTitle(_tabs[i])).FontSize(small).MaxWidth(180).TextTrimming(TextTrimming.CharacterEllipsis)
                .FontWeight(selected ? FontWeight.SemiBold : FontWeight.Normal).VerticalAlignment(VerticalAlignment.Center)
                .Foreground(selected ? b.WindowForeground : b.MutedForeground);
            var content = new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                new TextBlock().Text("▱").FontSize(small).Foreground(selected ? b.DirectoryForeground : b.MutedForeground).VerticalAlignment(VerticalAlignment.Center),
                title);
            if (selected && _tabs.Count > 1)
            {
                var close = new Button().Content("✕").Padding(3, 0).MinHeight(0).FontSize(10).Background(Brushes.Transparent).BorderThickness(new Thickness(0))
                    .Foreground(b.MutedForeground).VerticalAlignment(VerticalAlignment.Center).OnClick(_ => CloseActiveTab());
                ToolTip.SetTip(close, Loc.T("Zamknij zakładkę (Ctrl+W)"));
                content.Children.Add(close);
            }

            _tabStrip.Children.Add(new Button()
                .Content(content)
                .Height(24)
                .Padding(8, 0)
                .CornerRadius(new CornerRadius(4))
                .BorderThickness(new Thickness(1))
                .BorderBrush(selected ? b.PanelBorder : Brushes.Transparent)
                .Background(selected ? b.PanelBackground : Brushes.Transparent)
                .OnClick(_ => SelectTab(index)));
        }

        var add = new Button().Content("+").Width(24).Height(24).Padding(0).FontSize(14).HorizontalContentAlignment(HorizontalAlignment.Center)
            .VerticalContentAlignment(VerticalAlignment.Center).Background(Brushes.Transparent).BorderThickness(new Thickness(0))
            .Foreground(b.MutedForeground).OnClick(_ => NewTabRequested?.Invoke());
        ToolTip.SetTip(add, Loc.T("Nowa zakładka (Ctrl+T)"));
        _tabStrip.Children.Add(add);
    }

    private void ApplySkin(SkinBrushes b)
    {
        Canvas.Brushes = b;
        Mode?.ApplySkin(b);
        if (_pathBox is null)
        {
            return;
        }

        var modern = b.IsModern;
        _card.Background = b.PanelBackground;
        _card.BorderBrush = b.PanelBorder;
        _card.BorderThickness = new Thickness(modern ? 1 : 0);
        _card.CornerRadius = new CornerRadius(modern ? 6 : 0);
        _tabBorder.Background = modern ? b.HeaderBackground : b.WindowBackground;
        _tabBorder.BorderBrush = b.PanelBorder;
        _tabBorder.BorderThickness = new Thickness(0, 0, 0, modern ? 1 : 0);
        _tabBorder.Height = modern ? 32 : double.NaN;
        _tabBorder.Padding = new Thickness(modern ? 4 : 0, 0);
        _pathBorder.BorderBrush = b.PanelBorder;
        _pathBorder.BorderThickness = new Thickness(0, 0, 0, modern ? 1 : 0);
        _pathBorder.Height = modern ? 32 : double.NaN;
        _pathBorder.Padding = new Thickness(modern ? 0 : 2);
        _statusBorder.Background = b.StatusBackground;
        _statusBorder.BorderBrush = b.PanelBorder;
        _statusBorder.BorderThickness = new Thickness(0, modern ? 1 : 0, 0, 0);
        _statusBorder.Height = modern ? 26 : double.NaN;
        _statusBorder.Padding = modern ? new Thickness(12, 0) : new Thickness(6, 2);
        _status.Foreground = b.StatusForeground;
        _status.FontFamily = b.FontFamily;
        _status.FontSize = modern ? Math.Max(11, b.FontSize - 1) : b.FontSize - 1;
        _statusMarked.Foreground = b.SelectedForeground;
        _statusMarked.FontFamily = b.FontFamily;
        _statusMarked.FontSize = _status.FontSize;
        _statusMarkedEdge.Background = b.SelectedForeground;
        UpdateEmptyFolderSetup();
        _pathBox.FontFamily = b.FontFamily;
        _pathBox.FontSize = b.FontSize;
        _pathBox.FontWeight = modern ? FontWeight.SemiBold : FontWeight.Normal;
        _locationButton.Foreground = b.HeaderForeground;
        _locationButton.FontSize = b.FontSize;
        ToolTip.SetTip(_locationButton, Loc.T("Zmień lokalizację panelu: dyski, Ten komputer, sieć, połączenia SSH/FTP (Alt+F1 / Alt+F2)"));
        _modeSwitch.FontFamily = b.FontFamily;
        _modeSwitch.FontSize = modern ? Math.Max(12, b.FontSize) : b.FontSize;
        ToolTip.SetTip(_modeSwitch, Loc.T("Przełącz między listą plików a podglądem"));
        _filesModeLabel.FontFamily = b.FontFamily;
        _previewModeLabel.FontFamily = b.FontFamily;
        _filesModeLabel.FontSize = _previewModeLabel.FontSize = modern ? Math.Max(11, b.FontSize - 1) : b.FontSize;

        UpdateModeButtons();
        ApplyChrome();
        RebuildTabs();
    }

    private void UpdateModeButtons()
    {
        if (_modeSwitch is null)
        {
            return;
        }

        var brushes = _skins.Current;
        var showingPreview = Mode is PreviewPanelView;
        _updatingModeSwitch = true;
        _modeSwitch.IsChecked = showingPreview;
        _updatingModeSwitch = false;
        _filesModeLabel.Foreground = showingPreview ? brushes.MutedForeground : brushes.HeaderForeground;
        _previewModeLabel.Foreground = showingPreview ? brushes.HeaderForeground : brushes.MutedForeground;
        _filesModeLabel.FontWeight = showingPreview ? FontWeight.Normal : FontWeight.SemiBold;
        _previewModeLabel.FontWeight = showingPreview ? FontWeight.SemiBold : FontWeight.Normal;
    }

    /// <summary>Active panel: modern skins tint the path bar and show the accent edge; retro skins use the cursor colours.</summary>
    private void ApplyChrome()
    {
        if (_pathBox is null)
        {
            return;
        }

        var b = _skins.Current;
        var (background, foreground) = b.IsModern
            ? (_isActive ? b.ActiveHeaderBackground : b.HeaderBackground, _isActive ? b.ActiveHeaderForeground : b.HeaderForeground)
            : (_isActive ? b.CursorBackground : b.HeaderBackground, _isActive ? b.CursorForeground : b.HeaderForeground);
        _pathBorder.Background = background;
        _pathBox.Background = Brushes.Transparent;
        _pathBox.Foreground = foreground;
        _accentEdge.Background = b.Accent;
        _accentEdge.IsVisible = b.IsModern && _isActive;
    }
}
