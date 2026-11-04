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

/// <summary>What entering a folder shows: always the files, the folder's own view when its c4k.dir has one, or always a view.</summary>
public enum FolderViewMode
{
    Files,
    Auto,
    View,
}

/// <summary>One side of the dual-pane layout: tab strip, path bar, owner-drawn list and status line.</summary>
public sealed class FilePanelView : ViewBase
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;
    private readonly List<PanelTab> _tabs = [];
    private readonly Dictionary<PanelTab, string> _pluginTabIds = [];
    private readonly Dictionary<PanelTab, IPanelModeView> _pluginTabModes = [];

    /// <summary>Tabs whose view belongs to their folder (plugin manager, settings, skins): Pliki/Podgląd switches between them.</summary>
    private readonly HashSet<PanelTab> _folderViewTabs = [];

    private readonly Stack<(IPanelModeView View, bool IsFolderView)> _returnModes = [];

    /// <summary>The mode on screen is the view of the panel's folder (a c4k.dir panel, a built-in folder view).</summary>
    private bool _modeIsFolderView;

    private readonly HashSet<IPanelModeView> _wiredModes = [];
    private int _activeIndex;
    private bool _isActive;
    private string? _tabsKey;
    private bool _statusScheduled;

    private Border _tabBorder = null!;
    private StackPanel _tabStrip = null!;
    private Border _pathBorder = null!;
    private TextBox _pathBox = null!;
    /// <summary>The Pliki · Auto · Widok switch on the path bar (rebuilt when the mode or the skin changes).</summary>
    private ContentControl _modeSwitch = null!;
    private Border _statusBorder = null!;
    private Decorator _emptyFolderSetup = null!;
    private SkinBrushes? _emptyFolderSkin;
    private Panel _content = null!;
    private readonly BusyOverlay _loadingCard = new();
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
    /// <param name="folderView">The view belongs to the folder the panel is in: the Pliki/Podgląd switch then shows
    /// the folder's files (and closes the view) instead of switching to the quick preview.</param>
    public void ShowMode(IPanelModeView view, bool folderView = false)
    {
        ShowSuggestion(null);
        DisposeReturnModes();
        CloseMode();
        _modeIsFolderView = folderView;
        AttachMode(view);
        UpdateModeButtons();
    }

    /// <summary>Shows a temporary mode over the current one; closing it restores the current mode.</summary>
    public void ShowModeReturningToCurrent(IPanelModeView view)
    {
        ShowSuggestion(null);
        if (Mode is { } current)
        {
            _content.Children.Remove(current.View);
            _returnModes.Push((current, _modeIsFolderView));
        }

        _modeIsFolderView = false;
        AttachMode(view);
        UpdateModeButtons();
    }

    /// <summary>Key of the tab that holds the quick view.</summary>
    public const string QuickViewKey = "quickview";

    /// <summary>The quick view tab is on screen.</summary>
    public bool QuickViewShown => _pluginTabIds.GetValueOrDefault(ActiveTab) == QuickViewKey && Mode is PreviewPanelView;

    /// <summary>Removes the quick view tab, whichever tab is selected.</summary>
    public void CloseQuickViewTab()
    {
        var tab = _tabs.FirstOrDefault(t => _pluginTabIds.GetValueOrDefault(t) == QuickViewKey);
        if (tab is null || _tabs.Count == 1)
        {
            return;
        }

        SelectTab(_tabs.IndexOf(tab));
        CloseActiveTab();
    }

    /// <summary>A new tab on <paramref name="path"/> showing <paramref name="view"/>, the folder's own view.</summary>
    /// <param name="key">Identifies the view, so opening it again selects this tab (<see cref="SelectFolderViewTab"/>).</param>
    public void AddFolderViewTab(string path, string key, IPanelModeView view)
    {
        var tab = CreateTab(path);
        _pluginTabIds.Add(tab, key);
        _folderViewTabs.Add(tab);
        _pluginTabModes[tab] = view;
        SelectTab(_tabs.Count - 1);
        _ = tab.NavigateAsync(path);
    }

    /// <summary>Selects the tab showing the folder view <paramref name="key"/> and returns its view; <c>null</c> when there is none.</summary>
    public IPanelModeView? SelectFolderViewTab(string key)
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (_folderViewTabs.Contains(_tabs[i]) && _pluginTabIds.GetValueOrDefault(_tabs[i]) == key && _pluginTabModes.TryGetValue(_tabs[i], out var view))
            {
                SelectTab(i);
                if (Mode != view)
                {
                    ActivatePluginTabMode(_tabs[i], view);
                }

                return view;
            }
        }

        return null;
    }

    /// <summary>The view on screen belongs to the folder: the switch toggles between it and the files.</summary>
    private bool IsFolderView =>
        Mode is not null && (_modeIsFolderView || (_folderViewTabs.Contains(ActiveTab) && _pluginTabModes.GetValueOrDefault(ActiveTab) == Mode));

    private void AttachMode(IPanelModeView view)
    {
        Mode = view;
        view.IsActivePanel = _isActive;
        if (_wiredModes.Add(view))
        {
            view.Activated += () => Activated?.Invoke(this);
            view.CloseRequested += () => OnModeCloseRequested(view);
        }

        _content.Children.Add(view.View);
        Canvas.IsVisible = false;
        _statusBorder.IsVisible = false;
        _pathBox.Text = (view.TabIcon ?? "⚙") + " " + view.Title;
        _pathBox.IsReadOnly = true;
        UpdateModeButtons();
        if (view is ITitledModeView titled)
        {
            titled.TitleChanged += () =>
            {
                if (Mode == view)
                {
                    _pathBox.Text = (view.TabIcon ?? "⚙") + " " + view.Title;
                }

                if (TabsKey() != _tabsKey)
                {
                    RebuildTabs();
                }
            };
        }
    }

    /// <summary>Esc or a close button in a mode: a folder tab's view gives way to the files, any other mode closes.</summary>
    private void OnModeCloseRequested(IPanelModeView view)
    {
        if (Mode != view)
        {
            return;
        }

        if (_folderViewTabs.Contains(ActiveTab) && _pluginTabModes.GetValueOrDefault(ActiveTab) == view)
        {
            if (_pluginTabIds.GetValueOrDefault(ActiveTab) == QuickViewKey && _tabs.Count > 1)
            {
                CloseActiveTab();
                return;
            }

            DetachPluginTabMode();
            return;
        }

        if (_returnModes.TryPop(out var previous))
        {
            CloseCurrentMode(view);
            _modeIsFolderView = previous.IsFolderView;
            AttachMode(previous.View);
            UpdateModeButtons();
            return;
        }

        CloseMode();
    }

    public void CloseMode()
    {
        if (Mode is not { } view)
        {
            DisposeReturnModes();
            return;
        }

        DisposeReturnModes();
        CloseCurrentMode(view);
        Canvas.IsVisible = true;
        _statusBorder.IsVisible = true;
        _pathBox.IsReadOnly = false;
        _pathBox.Text = ActiveTab.Path;
        UpdateModeButtons();
    }

    private void CloseCurrentMode(IPanelModeView view)
    {
        _wiredModes.Remove(view);
        _modeIsFolderView = false;
        Mode = null;
        _content.Children.Remove(view.View);
        var pluginTab = _pluginTabModes.FirstOrDefault(pair => pair.Value == view).Key;
        if (pluginTab is not null)
        {
            _pluginTabModes.Remove(pluginTab);
            _pluginTabIds.Remove(pluginTab);
            _folderViewTabs.Remove(pluginTab);
            RebuildTabs();
        }

        _ = view.DisposeModeAsync();
    }

    private void DisposeReturnModes()
    {
        while (_returnModes.TryPop(out var previous))
        {
            _wiredModes.Remove(previous.View);
            _ = previous.View.DisposeModeAsync();
        }
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
        mode.ApplySkin(_skins.Current); // the skin may have changed while another tab was on screen
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

    public event Action<FilePanelView>? TabSelected;

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

    /// <summary>Configure the current empty folder as a special folder.</summary>
    public event Action<string>? ConfigureEmptyFolderRequested;

    private Button _locationButton = null!;

    public event Action<AuthenticationRequest>? AuthenticationRequired;

    protected override object Build()
    {
        var root = CreateLayout();
        _pathBox.KeyDown += OnPathKeyDown;
        _pathBox.GotFocus += (_, _) => Activated?.Invoke(this);
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
                            new ContentControl().Ref(out _modeSwitch).Col(2).Focusable(false)
                                .VerticalAlignment(VerticalAlignment.Center).Margin(6, 0),
                            // One click to switch this panel elsewhere: drives, This PC, network, saved and new connections.
                            new Button().Focusable(false).Ref(out _locationButton).Col(5).Content("▾").Padding(10, 0).MinWidth(32)
                                .VerticalAlignment(VerticalAlignment.Stretch).VerticalContentAlignment(VerticalAlignment.Center)
                                .BorderThickness(new Thickness(0)).Background(Brushes.Transparent)
                                .OnClick(click => LocationMenuRequested?.Invoke(this)))),
                    new Panel().Ref(out _content).Row(2).Children(
                        Canvas,
                        _loadingCard,
                        new Decorator().Ref(out _emptyFolderSetup).IsVisible(false)),
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
        _folderViewTabs.Remove(tab);
        _wiredModes.RemoveWhere(m => !_pluginTabModes.ContainsValue(m) && m != Mode);
        tab.Detach();
        tab.Changed -= OnAnyTabChanged;
        _tabs.RemoveAt(_activeIndex);
        _activeIndex = Math.Min(_activeIndex, _tabs.Count - 1); // SelectTab reads ActiveTab first
        SelectTab(_activeIndex);
    }

    public void SelectTab(int index)
    {
        if (_folderViewTabs.Contains(ActiveTab) && Mode == _pluginTabModes.GetValueOrDefault(ActiveTab))
        {
            DetachPluginTabMode();
        }
        else if (Mode is PreviewPanelView { IsPinned: true } || _modeIsFolderView)
        {
            CloseMode();
        }

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

        TabSelected?.Invoke(this);
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
                if (FolderEntering is not null)
                {
                    FolderEntering(entry);
                }
                else
                {
                    _ = ActiveTab.NavigateAsync(entry.Path, ActiveTab.Title);
                }

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
        UpdateLoadingCard();
        UpdateStatus();
        // Changed fires on every cursor move and streamed batch: rebuild the tab buttons only when they would differ.
        if (_tabStrip.Children.Count > 0 && TabsKey() != _tabsKey)
        {
            RebuildTabs();
        }
    }

    /// <summary>The same loading card the plugin modes show, over the list while a folder is being read.</summary>
    private void UpdateLoadingCard()
    {
        var tab = ActiveTab;
        var count = Math.Max(0, tab.Entries.Count - (tab.Entries.Count > 0 && tab.Entries[0].Kind == VfsEntryKind.ParentLink ? 1 : 0));
        _loadingCard.Set(tab.IsLoading && Mode is null, Loc.T("Wczytywanie… {0:N0}   [Esc – Anuluj]", count));
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
        if (_emptyFolderSetup.IsVisible && _emptyFolderSkin != _skins.Current)
        {
            _emptyFolderSkin = _skins.Current;
            _emptyFolderSetup.Child = EmptyFolderCard(_skins.Current, () => ConfigureEmptyFolderRequested?.Invoke(ActiveTab.Path));
        }
    }

    /// <summary>The empty-folder card (also shown by the quick view): what the folder should show, saved in c4k.dir.</summary>
    internal static Border EmptyFolderCard(SkinBrushes brushes, Action configure) => Ds.EmptyState(
        brushes,
        Loc.T("Pusty folder"),
        Loc.T("Ten folder jest pusty. Wybierz, co ma pokazywać — ustawienia zostaną zapisane w pliku c4k.dir."),
        Loc.T("Skonfiguruj jako specjalny folder…"),
        configure);

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
        tab.FolderViewOrigin is { Length: > 0 } origin ? origin
        : _pluginTabModes.TryGetValue(tab, out var view) ? view.Title
        : _pluginTabIds.TryGetValue(tab, out var pluginId) ? _host.FindPanel(pluginId)?.Title ?? tab.Title
        : tab.Title;

    private string TabIcon(PanelTab tab) => _pluginTabModes.GetValueOrDefault(tab)?.TabIcon ?? "📁";

    private static string TabToolTip(PanelTab tab) =>
        tab.FolderViewPlugin is { Length: > 0 } plugin ? plugin + "\n" + tab.Path : tab.Path;

    private string TabsKey() => _activeIndex + "|" + _skins.Current.Skin.Id + "|" + string.Join('\n', _tabs.Select(tab => TabTitle(tab) + "\0" + tab.FolderViewPlugin + "\0" + tab.Path));

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
            var tab = _tabs[i];
            var button = new Button().Focusable(false)
                .Content(TabIcon(tab) + " " + TabTitle(tab))
                .Padding(10, 2)
                .Margin(0, 0, 2, 0)
                .CornerRadius(new CornerRadius(b.Skin.Chrome.CornerRadius, b.Skin.Chrome.CornerRadius, 0, 0))
                .Background(selected ? b.CursorBackground : b.HeaderBackground)
                .Foreground(selected ? b.CursorForeground : b.HeaderForeground)
                .FontFamily(b.FontFamily)
                .FontSize(b.FontSize - 1)
                .OnClick(_ => SelectTab(index));
            ToolTip.SetTip(button, TabToolTip(tab));
            _tabStrip.Children.Add(button);
        }

        _tabStrip.Children.Add(new Button().Focusable(false).Content("+").Width(24).Height(24).Padding(0).FontSize(14)
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
                new TextBlock().Text(TabIcon(_tabs[i])).FontSize(small).Foreground(selected ? b.DirectoryForeground : b.MutedForeground).VerticalAlignment(VerticalAlignment.Center),
                title);
            if (selected && _tabs.Count > 1)
            {
                var close = new Button().Focusable(false).Content("✕").Padding(3, 0).MinHeight(0).FontSize(10).Background(Brushes.Transparent).BorderThickness(new Thickness(0))
                    .Foreground(b.MutedForeground).VerticalAlignment(VerticalAlignment.Center).OnClick(_ => CloseActiveTab());
                ToolTip.SetTip(close, Loc.T("Zamknij zakładkę (Ctrl+W)"));
                content.Children.Add(close);
            }

            var button = new Button().Focusable(false)
                .Content(content)
                .Height(24)
                .Padding(8, 0)
                .CornerRadius(new CornerRadius(4))
                .BorderThickness(new Thickness(1))
                .BorderBrush(selected ? b.PanelBorder : Brushes.Transparent)
                .Background(selected ? b.PanelBackground : Brushes.Transparent)
                .OnClick(_ => SelectTab(index));
            ToolTip.SetTip(button, TabToolTip(_tabs[i]));
            _tabStrip.Children.Add(button);
        }

        var add = new Button().Focusable(false).Content("+").Width(24).Height(24).Padding(0).FontSize(14).HorizontalContentAlignment(HorizontalAlignment.Center)
            .VerticalContentAlignment(VerticalAlignment.Center).Background(Brushes.Transparent).BorderThickness(new Thickness(0))
            .Foreground(b.MutedForeground).OnClick(_ => NewTabRequested?.Invoke());
        ToolTip.SetTip(add, Loc.T("Nowa zakładka (Ctrl+T)"));
        _tabStrip.Children.Add(add);
    }

    private void ApplySkin(SkinBrushes b)
    {
        Canvas.Brushes = b;
        _loadingCard.ApplySkin(b);
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
        ToolTip.SetTip(_modeSwitch, Loc.T("Pliki: zawsze lista plików · Auto: widok z c4k.dir folderu lub nadrzędnego, a bez niego pliki · Widok: zawsze widok folderu"));

        UpdateModeButtons();
        ApplyChrome();
        RebuildTabs();
    }

    /// <summary>Raised after the mode over the list may have changed (shown, closed, another tab's view).</summary>
    public event Action? ModeChanged;

    private void UpdateModeButtons()
    {
        ModeChanged?.Invoke();
        if (_modeSwitch is null)
        {
            return;
        }

        // A mode that is not a folder's view (a plugin panel opened on its own, the browser) has no files to switch to.
        var showingPreview = Mode is PreviewPanelView || IsFolderView;
        _modeSwitch.IsVisible = Mode is null || showingPreview || _folderViewTabs.Contains(ActiveTab);

        // A plugin's own folder view has its "Pliki (Esc)" button: the path bar and the switch add nothing there.
        _pathBorder.IsVisible = Mode is not PreviewPanelView { IsPinnedFolder: true };

        // A folder tab (plugin manager, settings, skins) is its view or its files; elsewhere the panel's folder mode.
        var folderTab = _folderViewTabs.Contains(ActiveTab);
        var selected = folderTab ? (Mode is not null ? 2 : 0) : (int)FolderMode;
        var segmented = Ds.Segmented(_skins.Current, [Loc.T("Pliki"), Loc.T("Auto"), Loc.T("Widok")], selected, OnFolderModePicked, onToolbar: true);
        if (segmented.Child is Panel buttons)
        {
            foreach (var button in buttons.Children.OfType<Button>())
            {
                button.Focusable = false; // panel chrome never takes the keyboard from the list
            }
        }

        _modeSwitch.Content = segmented;
    }

    /// <summary>What entering a folder shows in this panel (Pliki · Auto · Widok on the path bar).</summary>
    public FolderViewMode FolderMode
    {
        get => ActiveTab.FolderMode;
        set
        {
            ActiveTab.FolderMode = value;
            UpdateModeButtons();
        }
    }

    /// <summary>Pliki · Auto · Widok was clicked; the window applies it to the folder on screen.</summary>
    public event Action<FilePanelView>? FolderModeRequested;

    private void OnFolderModePicked(int index)
    {
        var mode = (FolderViewMode)index;
        if (_folderViewTabs.Contains(ActiveTab) && _pluginTabModes.TryGetValue(ActiveTab, out var tabView))
        {
            // A folder tab: Pliki = the folder's files, Auto/Widok = its view (plugin manager, settings, skins).
            if (mode == FolderViewMode.Files)
            {
                DetachPluginTabMode();
            }
            else
            {
                ActivatePluginTabMode(ActiveTab, tabView);
            }

            return;
        }

        FolderMode = mode;
        FolderModeRequested?.Invoke(this);
        UpdateModeButtons();
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
