using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Composition;
using Commander4K.App.Theming;
using Commander4K.PluginSdk;

namespace Commander4K.App.Panels;

public sealed record AuthenticationRequest(PanelTab Tab, VfsAuthenticationRequiredException Exception);

/// <summary>One side of the dual-pane layout: tab strip, path bar, owner-drawn list and status line.</summary>
public sealed class FilePanelView : ViewBase
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;
    private readonly List<PanelTab> _tabs = [];
    private int _activeIndex;
    private bool _isActive;

    private Border _tabBorder = null!;
    private StackPanel _tabStrip = null!;
    private Border _pathBorder = null!;
    private TextBox _pathBox = null!;
    private Border _statusBorder = null!;
    private TextBlock _status = null!;

    public FilePanelView(AppHost host, SkinManager skins, string initialPath)
    {
        _host = host;
        _skins = skins;
        Canvas.Activated += () => Activated?.Invoke(this);
        Canvas.RowActivated += Open;
        Canvas.HeaderClicked += column => ActiveTab.SetSort(ActiveTab.Sort.Toggle(column));
        AddTab(initialPath);
        skins.Changed += ApplySkin;
    }

    public FileViewportCanvas Canvas { get; } = new();

    public PanelTab ActiveTab => _tabs[_activeIndex];

    public IReadOnlyList<PanelTab> Tabs => _tabs;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            Canvas.IsActivePanel = value;
            ApplyChrome();
        }
    }

    public event Action<FilePanelView>? Activated;

    // Single-parameter events: the declarative source generator mis-generates extensions for multi-parameter ones.

    /// <summary>Enter / double-click on a file (as opposed to a container).</summary>
    public event Action<VfsEntry>? FileActivated;

    /// <summary>Enter on the "+ Add network connection" row.</summary>
    public event Action<FilePanelView>? AddConnectionRequested;

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

    private Grid CreateLayout() =>
        new Grid()
            .Rows("Auto,Auto,*,Auto")
            .Children(
                new Border()
                    .Ref(out _tabBorder)
                    .Row(0)
                    .Child(new StackPanel().Ref(out _tabStrip).Orientation(Orientation.Horizontal)),
                new Border()
                    .Ref(out _pathBorder)
                    .Row(1)
                    .Padding(2)
                    .Child(new TextBox().Ref(out _pathBox).BorderThickness(new Thickness(0)).MinHeight(24).Padding(6, 2)),
                Canvas.Row(2),
                new Border()
                    .Ref(out _statusBorder)
                    .Row(3)
                    .Padding(6, 2)
                    .Child(new TextBlock().Ref(out _status).TextTrimming(TextTrimming.CharacterEllipsis)));

    public Task NavigateAsync(string path, string? focusName = null) => ActiveTab.NavigateAsync(path, focusName);

    public void FocusPathBar()
    {
        _pathBox.Focus();
        _pathBox.SelectAll();
    }

    public void AddTab(string path)
    {
        var tab = new PanelTab(_host, path);
        tab.Changed += OnAnyTabChanged;
        tab.AuthenticationRequired += (t, ex) => AuthenticationRequired?.Invoke(new AuthenticationRequest(t, ex));
        _tabs.Add(tab);
        SelectTab(_tabs.Count - 1);
        _ = tab.NavigateAsync(path);
    }

    public void CloseActiveTab()
    {
        if (_tabs.Count == 1)
        {
            return;
        }

        var tab = ActiveTab;
        tab.Detach();
        tab.Changed -= OnAnyTabChanged;
        _tabs.RemoveAt(_activeIndex);
        SelectTab(Math.Min(_activeIndex, _tabs.Count - 1));
    }

    public void SelectTab(int index)
    {
        _activeIndex = Math.Clamp(index, 0, _tabs.Count - 1);
        Canvas.Tab = ActiveTab;
        if (_tabStrip is not null)
        {
            RebuildTabs();
            OnTabChanged();
        }
    }

    /// <summary>Enter: opens containers in place, reports files to the window.</summary>
    public void Open(VfsEntry entry)
    {
        switch (entry.Kind)
        {
            case VfsEntryKind.ParentLink:
                _ = ActiveTab.NavigateAsync(entry.Path, ActiveTab.Title);
                break;
            case VfsEntryKind.Action:
                AddConnectionRequested?.Invoke(this);
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
        if (!_pathBox.IsFocused)
        {
            _pathBox.Text = tab.Path;
        }

        _status.Text = StatusText(tab);
        _status.Foreground = tab.Error is null ? _skins.Current.StatusForeground : _skins.Current.Error;
        if (_tabStrip.Children.Count > 0)
        {
            RebuildTabs();
        }
    }

    private string StatusText(PanelTab tab)
    {
        if (tab.Error is { } error)
        {
            return "⚠ " + error;
        }

        if (tab.Status is { } status)
        {
            return status;
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

        var text = $"{files:N0} plików ({FileViewportCanvas.FormatSize(bytes)}), {containers:N0} katalogów";
        return tab.Marked.Count > 0
            ? $"Zaznaczono {tab.Marked.Count:N0} ({FileViewportCanvas.FormatSize(markedBytes)}) · {text}"
            : text;
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
        _tabStrip.Children.Clear();
        _tabBorder.IsVisible = _tabs.Count > 1;
        if (_tabs.Count <= 1)
        {
            return;
        }

        var b = _skins.Current;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var index = i;
            var selected = i == _activeIndex;
            _tabStrip.Children.Add(new Button()
                .Content(_tabs[i].Title)
                .Padding(10, 2)
                .Margin(0, 0, 2, 0)
                .CornerRadius(new CornerRadius(b.Skin.Chrome.CornerRadius, b.Skin.Chrome.CornerRadius, 0, 0))
                .Background(selected ? b.CursorBackground : b.HeaderBackground)
                .Foreground(selected ? b.CursorForeground : b.HeaderForeground)
                .FontFamily(b.FontFamily)
                .FontSize(b.FontSize - 1)
                .OnClick(_ => SelectTab(index)));
        }
    }

    private void ApplySkin(SkinBrushes b)
    {
        Canvas.Brushes = b;
        if (_pathBox is null)
        {
            return;
        }

        _tabBorder.Background = b.WindowBackground;
        _statusBorder.Background = b.StatusBackground;
        _status.Foreground = b.StatusForeground;
        _status.FontFamily = b.FontFamily;
        _status.FontSize = b.FontSize - 1;
        _pathBox.FontFamily = b.FontFamily;
        _pathBox.FontSize = b.FontSize;
        ApplyChrome();
        RebuildTabs();
    }

    private void ApplyChrome()
    {
        if (_pathBox is null)
        {
            return;
        }

        var b = _skins.Current;
        _pathBorder.Background = _isActive ? b.CursorBackground : b.HeaderBackground;
        _pathBox.Background = _isActive ? b.CursorBackground : b.HeaderBackground;
        _pathBox.Foreground = _isActive ? b.CursorForeground : b.HeaderForeground;
    }
}
