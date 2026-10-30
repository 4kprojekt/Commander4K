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
public sealed class PluginPanelView : Grid, IPanelModeView
{
    private readonly IPanelPlugin _plugin;
    private readonly IPanelSession _session;
    private readonly PanelRequest _request;
    private readonly PluginPanelServices _services;
    private readonly PluginTableCanvas _table = new();
    private readonly TextBox _filter;
    private readonly WrapPanel _actions;
    private readonly SelectableTextBlock _details;
    private readonly TextBlock _status;
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

        _filter = new TextBox().PlaceholderText(Loc.T("Filtruj… (pisz, aby zawęzić)")).MinWidth(220).Padding(6, 2);
        _filter.TextChanged += (_, _) => Rebuild(keepCursorId: CurrentRow?.Row.Id);
        _filter.KeyDown += OnFilterKeyDown;
        _actions = new WrapPanel().Orientation(Orientation.Horizontal);
        foreach (var action in session.Actions)
        {
            var a = action;
            _actions.Children.Add(new Button()
                .Content(action.Shortcut is { } key ? $"{action.Title} ({key})" : action.Title)
                .Padding(8, 2).Margin(0, 0, 4, 4).FontSize(12)
                .OnClick(async _ => await ExecuteActionAsync(a)));
        }

        _actions.Children.Add(new Button().Content(Loc.T("Odśwież (Ctrl+R)")).Padding(8, 2).FontSize(12).OnClick(_ => Reload()));
        _actions.Children.Add(new Button().Content(Loc.T("Zamknij (Esc)")).Padding(8, 2).Margin(4, 0, 0, 0).FontSize(12).OnClick(_ => CloseRequested?.Invoke()));

        // The title is already in the panel's path bar; the header holds the filter and the actions only.
        _filter.Width = 220;
        _filter.Height = 28;
        _filter.VerticalAlignment = VerticalAlignment.Top;
        _header = new Border().Padding(6, 4).Child(new Grid().Cols("Auto,*").Children(
            _filter.Margin(0, 0, 10, 0),
            _actions.Col(1)));

        _details = new SelectableTextBlock().TextWrapping(TextWrapping.Wrap).Margin(8, 6);
        _detailsBorder = new Border().Child(new ScrollViewer().Content(_details));
        _status = new TextBlock().Margin(6, 2).TextTrimming(TextTrimming.CharacterEllipsis);

        RowDefinitions = new RowDefinitions("Auto,2*,4,*,Auto");
        Children.Add(_header);
        Children.Add(_table.Row(1));
        Children.Add(new GridSplitter().Row(2).ResizeDirection(GridResizeDirection.Rows));
        Children.Add(_detailsBorder.Row(3));
        Children.Add(_status.Row(4));

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

    public string PluginId => _plugin.Id;

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
        _detailsBorder.Background = brushes.PanelBackground;
        _details.Foreground = brushes.FileForeground;
        _details.FontFamily = brushes.FontFamily;
        _details.FontSize = brushes.FontSize - 1;
        _status.Foreground = brushes.StatusForeground;
        Background = brushes.StatusBackground;
        foreach (var text in _header.GetLogicalDescendants().OfType<TextBlock>())
        {
            text.Foreground = brushes.HeaderForeground;
        }

        _table.InvalidateVisual();
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

            if (text == "Ctrl+R" || text == "F5" && !_session.Actions.Any(a => a.Shortcut == "F5"))
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

    private void Reload(bool quiet = false)
    {
        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;
        if (!quiet)
        {
            _status.Text = Loc.T("⏳ Ładowanie… (Esc – anuluj)");
        }

        var progress = new Progress<string>(message => _status.Text = "⏳ " + message);
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
                    _status.Text = Loc.T("Przerwano.");
                    return;
                }

                if (task.Exception is { } error)
                {
                    _status.Text = "⚠ " + error.InnerException?.Message;
                    _status.Foreground = _brushes.Error;
                    return;
                }

                _status.Foreground = _brushes.StatusForeground;
                Apply(task.Result);
            }),
            TaskScheduler.Default);
    }

    private void Apply(PanelSnapshot snapshot)
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
        _status.Text = snapshot.Status ?? $"{snapshot.Rows.Count} pozycji";
        ScheduleDetails();
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
        var ids = _table.Marked.Count > 0 ? _table.Marked.ToList() : CurrentRow is { } row ? [row.Row.Id] : [];
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
            _details.Text = string.Empty;
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

            _details.Text = Format(details);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _details.Text = "⚠ " + ex.Message;
        }
    }

    private static string Format(PanelDetails? details)
    {
        if (details is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var section in details.Sections)
        {
            text.Append("■ ").AppendLine(section.Title);
            var width = Math.Min(28, section.Items.Select(i => i.Key.Length).DefaultIfEmpty(0).Max());
            foreach (var (key, value) in section.Items)
            {
                text.Append("   ").Append(key.PadRight(width)).Append("  ").AppendLine(value);
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd();
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
