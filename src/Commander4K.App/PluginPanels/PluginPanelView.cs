using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Input;
using Commander4K.App.Panels;
using Commander4K.App.Theming;
using Commander4K.Core.Configuration;
using Commander4K.Core.Input;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.App.PluginPanels;

/// <summary>Callbacks the panel needs from the window (dialogs, viewer); kept narrow so the view stays testable.</summary>
public sealed record PluginPanelServices(
    Func<string, string, Task<bool>> ConfirmAsync,
    Func<string, string, Task> ShowTextAsync,
    Action<string, bool> Notify);

/// <summary>
/// Renders an <see cref="IPanelPlugin"/> session inside a file panel: filter box, actions, the tree table, a details
/// pane for the row under the cursor and a status line. Loading always runs on the thread pool and can be cancelled.
/// </summary>
public sealed class PluginPanelView : ModeViewBase, IPanelModeView
{
    private readonly IPanelPlugin _plugin;
    private readonly IPanelSession _session;
    private readonly PanelRequest _request;
    private readonly PluginPanelServices _services;
    private readonly PluginTableCanvas _table = new();
    private readonly TextBox _filter;
    private readonly WrapPanel _actions;
    private readonly StackPanel _details = new() { Spacing = 14, Margin = new Thickness(14, 12) };
    private readonly Control _searchField;
    private readonly Grid _body = new();
    private readonly GridSplitter _splitter = new();
    private const int LongSectionItems = 25;
    private PanelDetails? _shownDetails;
    private string? _detailsMessage;
    private bool? _detailsAtSide;
    private readonly Border _header;
    private readonly Border _detailsBorder;
    private readonly DispatcherTimer _detailsDelay;
    private readonly DispatcherTimer? _autoRefresh;
    private readonly HashSet<string> _expanded = [];
    private PanelSnapshot? _snapshot;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _detailsLoading;
    private bool _firstLoad = true;
    private SkinBrushes _brushes;

    public IPanelPlugin Plugin { get; }

    public PluginPanelView(IPanelPlugin plugin, IPanelSession session, PanelRequest request, SkinBrushes brushes, PluginPanelServices services)
    {
        _plugin = plugin;
        Plugin = plugin;
        _session = session;
        _request = request;
        _services = services;
        _brushes = brushes;

        _filter = new TextBox().PlaceholderText(Loc.T("Filtruj… (pisz, aby zawęzić)"));
        _filter.TextChanged += (_, _) => Rebuild(keepCursorId: CurrentRow?.Row.Id);
        _filter.KeyDown += OnFilterKeyDown;
        _searchField = Ds.SearchField(brushes, _filter, 26).Width(240).VerticalAlignment(VerticalAlignment.Top);
        _actions = new WrapPanel().Orientation(Orientation.Horizontal);
        foreach (var action in session.Actions)
        {
            var a = action;
            var button = Ds.Button(brushes, action.Title, () => _ = ExecuteActionAsync(a), ButtonKind.Toolbar).Margin(0, 0, 6, 4);
            ToolTip.SetTip(button, action.Shortcut is { } key ? $"{action.Title} ({key})" : action.Title);
            _actions.Children.Add(button);
        }

        var refresh = Ds.ToolButton(brushes, "⟳", Loc.T("Odśwież (Ctrl+R)"), () => Reload());
        var close = Ds.ToolButton(brushes, "✕", Loc.T("Zamknij (Esc)"), () => CloseRequested?.Invoke());
        var right = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2).VerticalAlignment(VerticalAlignment.Top).Children(refresh, close);

        // Design system: a tool bar on the header colour with a rule below (the title is in the panel's path bar).
        _header = new Border().Padding(8, 6, 8, 2).Child(new Grid().Cols("Auto,*,Auto").Children(
            _searchField.Margin(0, 0, 10, 4),
            _actions.Col(1),
            right.Col(2)));

        _detailsBorder = new Border().Child(new ScrollViewer().Content(_details));

        Children.Add(_header);
        Children.Add(_body.Row(1));
        AttachChrome();
        _body.Children.Add(_table);
        _body.Children.Add(_splitter);
        _body.Children.Add(_detailsBorder);
        PlaceDetails(atSide: false);
        SizeChanged += (_, e) => PlaceDetails(e.NewSize.Width >= 960);

        _table.Activated += () => Activated?.Invoke();
        _table.RowClicked += index => MoveCursor(index);
        _table.RowDoubleClicked += index =>
        {
            MoveCursor(index);
            _ = ActivateCurrentAsync();
        };
        _table.ExpanderClicked += index =>
        {
            MoveCursor(index);
            ToggleExpanded();
        };
        _table.HeaderClicked += column =>
        {
            _table.SortDescending = _table.SortColumn == column && !_table.SortDescending;
            _table.SortColumn = column;
            Rebuild(CurrentRow?.Row.Id);
        };

        _detailsDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _detailsDelay.Tick += (_, _) =>
        {
            _detailsDelay.Stop();
            _ = LoadDetailsAsync();
        };

        if (session.RefreshInterval is { } interval)
        {
            _autoRefresh = new DispatcherTimer { Interval = interval };
            _autoRefresh.Tick += (_, _) =>
            {
                if (_loading is null && IsVisible)
                {
                    Reload(quiet: true);
                }
            };
        }

        ApplySkin(brushes);
        AttachedToVisualTree += (_, _) => _autoRefresh?.Start();
        DetachedFromVisualTree += (_, _) => _autoRefresh?.Stop();
        Reload();
    }

    public event Action? CloseRequested;

    public event Action? Activated;

    public string Title => _plugin.Title;

    public string? TabIcon => "⚙";

    public string PluginId => _plugin.Id;

    /// <summary>The plugin session, for sending files to the panel or taking files from it (F5).</summary>
    public IPanelSession Session => _session;

    public Core.Plugins.PanelModeKind Kind => Core.Plugins.PanelModeKind.Plugin;

    /// <summary>From the manifest (<c>provides</c>) or the built-in session.</summary>
    public bool CanProvide => _session.CanProvide;

    /// <summary>From the manifest (<c>accepts</c>) or the built-in session.</summary>
    public bool CanReceive => _session.CanReceive;

    /// <summary>Marked rows, or the row under the cursor — what an action that applies to rows would get.</summary>
    public IReadOnlyList<string> SelectedRowIds =>
        _table.Marked.Count > 0 ? [.. _table.Marked] : CurrentRow is { } row ? [row.Row.Id] : [];

    public bool IsActivePanel
    {
        set
        {
            _table.IsActivePanel = value;
            _table.InvalidateVisual();
        }
    }

    private VisibleRow? CurrentRow => _rowsCache.Count > 0 && _table.CursorIndex < _rowsCache.Count ? _rowsCache[_table.CursorIndex] : null;

    private IReadOnlyList<VisibleRow> _rowsCache = [];

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        _table.Brushes = brushes;
        _header.Background = brushes.HeaderBackground;
        _header.BorderBrush = brushes.PanelBorder;
        _header.BorderThickness = new Thickness(0, 0, 0, brushes.IsModern ? 1 : 0);
        _detailsBorder.Background = brushes.PanelBackground;
        _detailsBorder.BorderBrush = brushes.PanelBorder;
        _splitter.Background = brushes.PanelBorder;
        ApplyChromeSkin(brushes);
        Background = brushes.PanelBackground;
        foreach (var button in _header.GetLogicalDescendants().OfType<Button>())
        {
            Ds.Style(button, brushes, button.Content is "⟳" or "✕" ? ButtonKind.Ghost : ButtonKind.Toolbar);
        }

        RenderDetails();
        _table.InvalidateVisual();
    }

    /// <summary>The details pane: beside the table on a wide panel (the design), under it on a narrow one.</summary>
    private void PlaceDetails(bool atSide)
    {
        if (_detailsAtSide == atSide)
        {
            return;
        }

        _detailsAtSide = atSide;
        _body.RowDefinitions.Clear();
        _body.ColumnDefinitions.Clear();
        if (atSide)
        {
            _body.ColumnDefinitions = new ColumnDefinitions("*,1,360");
            Grid.SetRow(_table, 0); Grid.SetColumn(_table, 0);
            Grid.SetRow(_splitter, 0); Grid.SetColumn(_splitter, 1);
            Grid.SetRow(_detailsBorder, 0); Grid.SetColumn(_detailsBorder, 2);
            _splitter.ResizeDirection = GridResizeDirection.Columns;
        }
        else
        {
            _body.RowDefinitions = new RowDefinitions("2*,1,*");
            Grid.SetColumn(_table, 0); Grid.SetRow(_table, 0);
            Grid.SetColumn(_splitter, 0); Grid.SetRow(_splitter, 1);
            Grid.SetColumn(_detailsBorder, 0); Grid.SetRow(_detailsBorder, 2);
            _splitter.ResizeDirection = GridResizeDirection.Rows;
        }
    }

    /// <summary>Each section: an accent heading over a two-column grid (muted key, selectable value).</summary>
    private void RenderDetails()
    {
        _details.Children.Clear();
        var b = _brushes;
        if (_detailsMessage is { } message)
        {
            _details.Children.Add(Ds.Muted(b, message));
            return;
        }

        if (_shownDetails is null)
        {
            return;
        }

        foreach (var section in _shownDetails.Sections)
        {
            if (section.Items.Count > LongSectionItems)
            {
                // Hundreds of per-row controls (DLLs, threads) froze the UI on every cursor move; one text block is cheap.
                var text = new SelectableTextBlock
                {
                    Text = string.Join('\n', section.Items.Select(i => $"{i.Key}  {i.Value}")),
                    FontSize = 12,
                    Foreground = b.FileForeground,
                    TextWrapping = TextWrapping.Wrap,
                };
                _details.Children.Add(new StackPanel { Spacing = 8, Children = { Ds.SectionHeading(b, section.Title), text } });
                continue;
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 4 };
            var row = 0;
            foreach (var (key, value) in section.Items)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var keyText = new TextBlock { Text = key, FontSize = 12, Foreground = b.MutedForeground, MaxWidth = 140, TextWrapping = TextWrapping.Wrap };
                var valueText = new SelectableTextBlock { Text = value, FontSize = 12, Foreground = b.FileForeground, TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(keyText, row);
                Grid.SetRow(valueText, row);
                Grid.SetColumn(valueText, 1);
                grid.Children.Add(keyText);
                grid.Children.Add(valueText);
                row++;
            }

            _details.Children.Add(new StackPanel { Spacing = 8, Children = { Ds.SectionHeading(b, section.Title), grid } });
        }
    }

    /// <summary>Keys while the panel is active; returns <c>false</c> for keys left to the global keymap.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var page = _table.VisibleRowCount;
        switch (e.Key)
        {
            case Key.Up: MoveCursor(_table.CursorIndex - 1); return true;
            case Key.Down: MoveCursor(_table.CursorIndex + 1); return true;
            case Key.PageUp: MoveCursor(_table.CursorIndex - page); return true;
            case Key.PageDown: MoveCursor(_table.CursorIndex + page); return true;
            case Key.Home: MoveCursor(0); return true;
            case Key.End: MoveCursor(_rowsCache.Count - 1); return true;
            case Key.Right when CurrentRow is { HasChildren: true, Expanded: false }:
                ToggleExpanded();
                return true;
            case Key.Left:
                CollapseOrGoToParent();
                return true;
            case Key.Insert:
            case Key.Space when e.KeyModifiers == Avalonia.Input.KeyModifiers.None:
                ToggleMark();
                return true;
        }

        if (KeyMapping.ToStroke(e) is { } stroke)
        {
            var text = stroke.ToString();
            var action = _session.Actions.FirstOrDefault(a => a.Shortcut is { } s && SafeParse(s) == text);
            if (action is not null)
            {
                _ = ExecuteActionAsync(action);
                return true;
            }

            // A panel that provides files leaves F5 to the keymap (cm_Copy, or whatever the user bound it to); Ctrl+R
            // always refreshes. An action bound to F5 has already won above.
            if (text == "Ctrl+R" || text == "F5" && !_session.CanProvide && !_session.Actions.Any(a => a.Shortcut == "F5"))
            {
                Reload();
                return true;
            }

            if (text == "ENTER")
            {
                _ = ActivateCurrentAsync();
                return true;
            }

            if (text is "ESCAPE" or "BACKSPACE")
            {
                if (_loading is not null && text == "ESCAPE")
                {
                    _loading.Cancel();
                }
                else if (!string.IsNullOrEmpty(_filter.Text))
                {
                    _filter.Text = string.Empty;
                }
                else
                {
                    CloseRequested?.Invoke();
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>Typing on the panel narrows it (like the command line for file panels).</summary>
    public Control View => this;

    public void AppendText(string text)
    {
        _filter.Text += text;
        _filter.CaretIndex = _filter.Text.Length;
        _filter.Focus();
    }

    public async ValueTask DisposeModeAsync()
    {
        _loading?.Cancel();
        _detailsLoading?.Cancel();
        _autoRefresh?.Stop();
        await _session.DisposeAsync();
    }

    /// <summary>Loads the panel again (after an action or after files were sent to it).</summary>
    public void Reload(bool quiet = false)
    {
        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;
        if (!quiet)
        {
            SetStatus(Loc.T("Ładowanie… (Esc – anuluj)"), busy: true);
        }

        var progress = new Progress<string>(message => SetStatus(message, busy: true));
        var request = _request with { Filter = _filter.Text };
        _ = Task.Run(() => _session.LoadAsync(request, progress, cts.Token), cts.Token).ContinueWith(
            task => Dispatcher.UIThread.Post(() =>
            {
                if (_loading != cts)
                {
                    return;
                }

                _loading = null;
                if (task.IsCanceled || (task.Exception?.InnerException is OperationCanceledException))
                {
                    SetStatus(Loc.T("Przerwano."));
                    return;
                }

                if (task.Exception is { } error)
                {
                    ShowError("⚠ " + error.InnerException?.Message);
                    return;
                }

                Apply(task.Result, quiet);
            }),
            TaskScheduler.Default);
    }

    private void Apply(PanelSnapshot snapshot, bool quiet)
    {
        var cursorId = CurrentRow?.Row.Id;
        _snapshot = snapshot;
        if (_firstLoad)
        {
            _firstLoad = false;
            foreach (var row in snapshot.Rows.Where(r => r.ExpandedByDefault))
            {
                _expanded.Add(row.Id);
            }
        }

        _table.Marked.IntersectWith(snapshot.Rows.Select(r => r.Id));
        Rebuild(cursorId);
        SetStatus(snapshot.Status ?? $"{snapshot.Rows.Count} pozycji");

        // The timer refresh must not rebuild (and re-query) the details of the row that is already shown.
        if (!(quiet && _shownDetails is not null && cursorId is not null && CurrentRow?.Row.Id == cursorId))
        {
            ScheduleDetails();
        }
    }

    /// <summary>Flattens the tree: filter (matches plus their ancestors, all expanded), sort within siblings, expand state.</summary>
    private void Rebuild(string? keepCursorId)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var byId = snapshot.Rows.ToDictionary(r => r.Id);
        var children = snapshot.Rows
            .Where(r => r.ParentId is not null && byId.ContainsKey(r.ParentId))
            .GroupBy(r => r.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var roots = snapshot.Rows.Where(r => r.ParentId is null || !byId.ContainsKey(r.ParentId)).ToList();

        HashSet<string>? visibleIds = null;
        var filter = _filter.Text?.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            visibleIds = [];
            foreach (var row in SettingsCatalog.Search(snapshot.Rows, filter, r => r.Cells.FirstOrDefault() ?? "", r => r.Cells.Skip(1)))
            {
                for (var current = row; current is not null; current = current.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent : null)
                {
                    if (!visibleIds.Add(current.Id))
                    {
                        break;
                    }
                }
            }
        }

        var flat = new List<VisibleRow>();
        void Add(PanelRow row, int depth)
        {
            if (visibleIds is not null && !visibleIds.Contains(row.Id))
            {
                return;
            }

            var hasChildren = children.TryGetValue(row.Id, out var kids);
            var expanded = hasChildren && (visibleIds is not null || _expanded.Contains(row.Id));
            flat.Add(new VisibleRow(row, depth, hasChildren, expanded));
            if (expanded)
            {
                foreach (var child in Sort(kids!))
                {
                    Add(child, depth + 1);
                }
            }
        }

        foreach (var root in Sort(roots))
        {
            Add(root, 0);
        }

        _rowsCache = flat;
        var cursor = keepCursorId is null ? _table.CursorIndex : flat.FindIndex(r => r.Row.Id == keepCursorId);
        _table.CursorIndex = cursor < 0 ? Math.Min(_table.CursorIndex, Math.Max(0, flat.Count - 1)) : cursor;
        _table.SetContent(snapshot.Columns, flat);
    }

    private IEnumerable<PanelRow> Sort(List<PanelRow> rows)
    {
        var column = _table.SortColumn;
        if (column < 0)
        {
            return rows;
        }

        IOrderedEnumerable<PanelRow> ordered = rows.All(r => r.SortKeys is { } k && k.Count > column && k[column] is not null)
            ? rows.OrderBy(r => r.SortKeys![column])
            : rows.OrderBy(r => r.Cells.ElementAtOrDefault(column), StringComparer.CurrentCultureIgnoreCase);
        return _table.SortDescending ? ordered.Reverse() : ordered;
    }

    private void MoveCursor(int index)
    {
        _table.CursorIndex = Math.Clamp(index, 0, Math.Max(0, _rowsCache.Count - 1));
        _table.EnsureCursorVisible();
        ScheduleDetails();
    }

    private void ToggleExpanded()
    {
        if (CurrentRow is { HasChildren: true } row && !_expanded.Remove(row.Row.Id))
        {
            _expanded.Add(row.Row.Id);
        }

        Rebuild(CurrentRow?.Row.Id);
    }

    private void CollapseOrGoToParent()
    {
        if (CurrentRow is not { } row)
        {
            return;
        }

        if (row.Expanded)
        {
            _expanded.Remove(row.Row.Id);
            Rebuild(row.Row.Id);
        }
        else if (row.Row.ParentId is { } parentId)
        {
            MoveCursor(_rowsCache.ToList().FindIndex(r => r.Row.Id == parentId));
        }
    }

    private void ToggleMark()
    {
        if (CurrentRow is { } row && !_table.Marked.Remove(row.Row.Id))
        {
            _table.Marked.Add(row.Row.Id);
        }

        MoveCursor(_table.CursorIndex + 1);
    }

    /// <summary>Enter: the plugin's Enter action, otherwise expand/collapse.</summary>
    private async Task ActivateCurrentAsync()
    {
        if (_session.Actions.FirstOrDefault(a => a.Shortcut == "Enter") is { } action && CurrentRow is { HasChildren: false })
        {
            await ExecuteActionAsync(action);
        }
        else
        {
            ToggleExpanded();
        }
    }

    private async Task ExecuteActionAsync(PanelAction action)
    {
        var ids = SelectedRowIds;
        if (action.AppliesToRows && ids.Count == 0)
        {
            return;
        }

        if (action.RequiresConfirmation)
        {
            var names = ids.Select(id => _snapshot?.Rows.FirstOrDefault(r => r.Id == id)?.Cells.FirstOrDefault() ?? id).Take(10);
            if (!await _services.ConfirmAsync(action.Title, $"{action.Title}: {string.Join(", ", names)}{(ids.Count > 10 ? $" i {ids.Count - 10} innych" : "")}?"))
            {
                return;
            }
        }

        try
        {
            var result = await Task.Run(() => _session.ExecuteActionAsync(action.Id, ids, CancellationToken.None));
            if (!string.IsNullOrEmpty(result))
            {
                await _services.ShowTextAsync($"{_plugin.Title} — {action.Title}", result);
            }
        }
        catch (Exception ex)
        {
            _services.Notify($"{action.Title}: {ex.Message}", true);
        }

        _table.Marked.Clear();
        Reload(quiet: true);
    }

    private void ScheduleDetails()
    {
        _detailsDelay.Stop();
        _detailsDelay.Start();
    }

    private async Task LoadDetailsAsync()
    {
        _detailsLoading?.Cancel();
        if (CurrentRow is not { } row)
        {
            ShowDetails(null, null);
            return;
        }

        var cts = new CancellationTokenSource();
        _detailsLoading = cts;
        try
        {
            var details = await Task.Run(() => _session.GetDetailsAsync(row.Row.Id, cts.Token), cts.Token);
            if (_detailsLoading != cts)
            {
                return;
            }

            ShowDetails(details, null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowDetails(null, "⚠ " + ex.Message);
        }
    }

    private void ShowDetails(PanelDetails? details, string? message)
    {
        _shownDetails = details;
        _detailsMessage = message;
        RenderDetails();
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Escape && string.IsNullOrEmpty(_filter.Text))
            {
                (TopLevel.GetTopLevel(this) as InputElement)?.Focus();
            }

            e.Handled = HandleKey(e);
        }
    }

    private static string? SafeParse(string sequence)
    {
        try
        {
            return KeyStroke.Parse(sequence).ToString();
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
