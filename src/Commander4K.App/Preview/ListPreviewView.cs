using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.App.Preview;

/// <summary>
/// A plugin's <see cref="ListPreview"/> drawn as the design "Tryb panelu — klient poczty" list: tabs, a search field with
/// filters and the plugin's actions, group headings and two-line rows. Search and filters work on the rows the plugin
/// sent; selecting a row is reported (the panel's cursor follows, and with it the preview), Enter or a double click opens it.
/// </summary>
internal sealed class ListPreviewView : DockPanel
{
    private readonly ListPreview _list;
    private readonly SkinBrushes _b;
    private readonly Action<ListPreviewItem> _selected;
    private readonly Action<ListPreviewItem> _opened;
    private readonly Action<ListPreviewItem>? _revealed;
    private readonly TextBox _search;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly List<(ListPreviewItem Item, Border Row)> _visible = [];
    private readonly double _rowHeight;
    private readonly Action<string>? _searchPlugin;
    private readonly Func<PreviewAction, Control> _actionButton;
    private Avalonia.Threading.DispatcherTimer? _searchDelay;
    private int _filter;
    private string? _selectedPath;

    /// <summary>What the next list of the same folder takes over: the typed search (its focus too), the filter and the scroll.</summary>
    public sealed record Carry(TextBox Search, bool SearchFocused, int Filter, Vector Scroll);

    /// <param name="actionButton">Builds a button for one of the plugin's actions (the host binds its shortcut).</param>
    /// <param name="searchPlugin">Runs the plugin's search (<see cref="ListPreview.SearchTarget"/>) with the typed text.</param>
    /// <param name="carry">The previous list of the same folder (another page, search results): its field, filter and scroll stay.</param>
    public ListPreviewView(ListPreview list, IReadOnlyList<PreviewAction> actions, SkinBrushes brushes, string? selectedPath,
        Action<ListPreviewItem> selected, Action<ListPreviewItem> opened, Func<PreviewAction, Control> actionButton,
        Action<string>? searchPlugin = null, Carry? carry = null, Action<ListPreviewItem>? revealed = null)
    {
        _revealed = revealed;
        _list = list;
        _b = brushes;
        _selected = selected;
        _opened = opened;
        _selectedPath = selectedPath;
        _actionButton = actionButton;
        _searchPlugin = list.SearchTarget is null ? null : searchPlugin;
        _filter = carry?.Filter ?? 0;
        _rowHeight = Math.Round(56 * brushes.FontSize / 13);

        if (list.Tabs is { Count: > 0 } tabs)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            foreach (var tab in tabs)
            {
                strip.Children.Add(Tab(tab, actionButton));
            }

            var tabBar = new Border
            {
                Height = 32,
                Padding = new Thickness(4, 0),
                Background = brushes.HeaderBackground,
                BorderBrush = brushes.PanelBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            };
            SetDock(tabBar, Dock.Top);
            Children.Add(tabBar);
        }

        _search = carry?.Search ?? new TextBox { FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, Text = list.Query ?? string.Empty };
        _search.PlaceholderText = list.SearchPlaceholder ?? Loc.T("Szukaj");
        _search.TextChanged += OnSearchChanged;
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var searchField = Ds.SearchField(brushes, _search, 26, "⌕", Ds.Kbd(brushes, "Ctrl+F"));
        searchField.MinWidth = 180;
        searchField.Width = 260;
        searchField.Margin = new Thickness(0, 2, 6, 2);
        bar.Children.Add(searchField);
        if (list.Filters is { Count: > 1 } filters)
        {
            _filter = Math.Clamp(_filter, 0, filters.Count - 1);
            var segmented = Ds.Segmented(brushes, [.. filters.Select(f => f.Label)], _filter, index =>
            {
                _filter = index;
                Rebuild(keepScroll: false);
            }, onToolbar: true);
            segmented.Margin = new Thickness(0, 2, 6, 2);
            bar.Children.Add(segmented);
        }

        foreach (var action in actions)
        {
            var button = actionButton(action);
            button.Margin = new Thickness(0, 2, 6, 2);
            bar.Children.Add(button);
        }

        var barBorder = new Border
        {
            MinHeight = 38,
            Padding = new Thickness(8, 4),
            BorderBrush = brushes.PanelBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };
        SetDock(barBorder, Dock.Top);
        Children.Add(barBorder);

        _scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 2, 0, 6) };
        Children.Add(_scroll);
        Rebuild(keepScroll: false);

        if (carry is { } previous)
        {
            // Another page or the answer to a search: the user stays where they were, still typing.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _scroll.Offset = previous.Scroll;
                if (previous.SearchFocused)
                {
                    _search.Focus();
                    _search.CaretIndex = _search.Text?.Length ?? 0;
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        // Typed on while the plugin was answering: ask again for what the field holds now.
        if (_searchPlugin is not null && (_search.Text ?? string.Empty).Trim() != (list.Query ?? string.Empty).Trim())
        {
            ScheduleSearch();
        }
    }

    /// <summary>Hands the search field, filter and scroll to the next list of the same folder; this view lets go of the field.</summary>
    public Carry Release()
    {
        _searchDelay?.Stop();
        _search.TextChanged -= OnSearchChanged;
        var focused = _search.IsFocused;
        if (_search.Parent is Panel parent)
        {
            parent.Children.Remove(_search);
        }

        return new Carry(_search, focused, _filter, _scroll.Offset);
    }

    public string Folder => _list.Folder;

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        Rebuild(keepScroll: false); // the rows on screen narrow down at once
        if (_searchPlugin is not null)
        {
            ScheduleSearch();
        }
    }

    /// <summary>The plugin searches everything (other pages, other folders) once typing pauses for 350 ms.</summary>
    private void ScheduleSearch()
    {
        _searchDelay?.Stop();
        _searchDelay = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay?.Stop();
            _searchPlugin?.Invoke((_search.Text ?? string.Empty).Trim());
        };
        _searchDelay.Start();
    }

    /// <summary>The row selected now (after search and filters), or <c>null</c>.</summary>
    public ListPreviewItem? Selected => _visible.FirstOrDefault(v => Same(v.Item.Path, _selectedPath)).Item;

    public void FocusSearch() => _search.Focus();

    /// <summary>↑↓, PgUp/PgDn, Home/End move the selection; Enter opens. Other keys are not the list's.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                FocusSearch();
                return true;
            }

            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control && _revealed is { } reveal && Selected is { } current)
            {
                reveal(current);
                return true;
            }

            return false;
        }

        var page = Math.Max(1, (int)(_scroll.Viewport.Height / (_rowHeight + 1)) - 1);
        var index = _visible.FindIndex(v => Same(v.Item.Path, _selectedPath));
        int? target = e.Key switch
        {
            Key.Down => index + 1,
            Key.Up => index - 1,
            Key.PageDown => index + page,
            Key.PageUp => index - page,
            Key.Home => 0,
            Key.End => _visible.Count - 1,
            _ => null,
        };
        if (target is { } next && _visible.Count > 0)
        {
            Select(_visible[Math.Clamp(next, 0, _visible.Count - 1)].Item);
            return true;
        }

        if (e.Key == Key.Enter && Selected is { } item)
        {
            _opened(item);
            return true;
        }

        return false;
    }

    private Control Tab(ListPreviewTab tab, Func<PreviewAction, Control> actionButton)
    {
        var fg = tab.Selected ? _b.WindowForeground : _b.MutedForeground;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (PreviewIcons.Find(tab.Icon) is { } icon)
        {
            content.Children.Add(PreviewIcons.Create(icon, tab.Selected ? _b.Accent : fg));
        }

        content.Children.Add(new TextBlock { Text = tab.Label, FontSize = 12, Foreground = fg, FontWeight = tab.Selected ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
        if (tab.Count is > 0 and var count)
        {
            content.Children.Add(new Border
            {
                Background = _b.Accent,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = _b.AccentForeground, LineHeight = 16 },
            });
        }

        if (tab.Selected)
        {
            return new Border
            {
                Height = 24,
                Padding = new Thickness(8, 0),
                CornerRadius = new CornerRadius(4),
                BorderBrush = _b.PanelBorder,
                BorderThickness = new Thickness(1),
                Background = _b.PanelBackground,
                VerticalAlignment = VerticalAlignment.Center,
                Child = content,
            };
        }

        // Another tab runs like the plugin's action, so the plugin answers with that list.
        var button = actionButton(new PreviewAction(tab.Label, tab.Target));
        if (button is Button b)
        {
            b.Content = content;
            Ds.Style(b, _b, ButtonKind.Ghost);
            b.Height = 24;
            b.Padding = new Thickness(8, 0);
        }

        return button;
    }

    private bool Passes(ListPreviewItem item)
    {
        if (_list.Filters is { Count: > 1 } filters && filters[Math.Clamp(_filter, 0, filters.Count - 1)].Tag is { Length: > 0 } tag
            && item.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        var query = _search.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return new[] { item.Title, item.Subtitle, item.Snippet }.Any(text => text is not null && compare.IndexOf(text, query, options) >= 0);
    }

    private void Rebuild(bool keepScroll)
    {
        _rows.Children.Clear();
        _visible.Clear();
        var shown = _list.Items.Where(Passes).ToList();
        var groups = shown.GroupBy(i => i.Group ?? string.Empty).ToList();
        foreach (var group in groups)
        {
            if (group.Key.Length > 0)
            {
                _rows.Children.Add(GroupHeading(group.Key, group.Count()));
            }

            foreach (var item in group)
            {
                var row = Row(item, Same(item.Path, _selectedPath));
                _visible.Add((item, row));
                _rows.Children.Add(row);
            }
        }

        if (shown.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = _list.Items.Count == 0 ? _list.Empty ?? Loc.T("Brak elementów.") : Loc.T("Nic nie pasuje do wyszukiwania ani filtra."),
                Foreground = _b.MutedForeground,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24, 48),
            });
        }

        if (_list.More is { } more && shown.Count > 0)
        {
            var button = _actionButton(more);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.Margin = new Thickness(0, 10, 0, 6);
            _rows.Children.Add(button);
        }

        if (!keepScroll)
        {
            _scroll.Offset = default;
        }

        // The selection stays where it was when it is still listed; otherwise the first row is selected.
        if (Selected is null && _visible.Count > 0)
        {
            Select(_visible[0].Item);
        }
    }

    public void Select(ListPreviewItem item)
    {
        var changed = !Same(item.Path, _selectedPath);
        _selectedPath = item.Path;
        foreach (var (shownItem, row) in _visible)
        {
            Paint(row, shownItem, Same(shownItem.Path, item.Path));
        }

        if (_visible.FirstOrDefault(v => Same(v.Item.Path, item.Path)).Row is { } selectedRow)
        {
            selectedRow.BringIntoView();
        }

        if (changed)
        {
            _selected(item);
        }
    }

    private Control GroupHeading(string label, int count)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        panel.Children.Add(new TextBlock { Text = label.ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = _b.HeaderForeground });
        panel.Children.Add(new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 11, Foreground = _b.MutedForeground });
        return new Border { Height = 28, Padding = new Thickness(22, 0, 14, 5), Child = panel };
    }

    private Border Row(ListPreviewItem item, bool selected)
    {
        var row = new Border
        {
            Height = _rowHeight,
            Margin = new Thickness(4, 0, 4, 1),
            Padding = new Thickness(24, 0, 12, 0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                Select(item);
            }
        };
        row.DoubleTapped += (_, _) => _opened(item);
        if (_revealed is { } reveal)
        {
            var menuItem = new MenuItem { Header = Loc.T("Pokaż w folderze (Ctrl+Enter)") };
            menuItem.Click += (_, _) => reveal(item);
            row.ContextMenu = new ContextMenu { ItemsSource = new[] { menuItem } };
        }

        Paint(row, item, selected);
        return row;
    }

    /// <summary>Draws a row's content for its state; the cursor row uses the cursor colours like the file list.</summary>
    private void Paint(Border row, ListPreviewItem item, bool selected)
    {
        var unread = Has(item, "unread");
        var fg = selected ? _b.CursorForeground : _b.FileForeground;
        IBrush meta = selected ? new SolidColorBrush(_b.CursorForeground.Color, 0.82) : _b.MutedForeground;
        row.Background = selected ? _b.CursorBackground : Brushes.Transparent;

        var first = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), VerticalAlignment = VerticalAlignment.Center };
        first.Children.Add(new TextBlock
        {
            Text = item.Title,
            Foreground = fg,
            FontWeight = unread ? FontWeight.Bold : FontWeight.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (Has(item, "attachment"))
        {
            var clip = PreviewIcons.Create(PreviewIcons.Attachment, meta);
            clip.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(clip, 1);
            first.Children.Add(clip);
        }

        if (Has(item, "flagged"))
        {
            var flag = PreviewIcons.Create(PreviewIcons.Flag, selected ? _b.CursorForeground : _b.SelectedForeground);
            flag.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(flag, 2);
            first.Children.Add(flag);
        }

        if (item.Meta is { Length: > 0 } time)
        {
            var when = new TextBlock { Text = time, FontSize = 12, Foreground = meta, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(when, 3);
            first.Children.Add(when);
        }

        var second = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (item.Subtitle is { Length: > 0 } subtitle)
        {
            second.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = fg,
                FontSize = _b.FontSize - 0.5,
                FontWeight = unread ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }

        if (item.Snippet is { Length: > 0 } snippet)
        {
            var text = new TextBlock { Text = snippet, Foreground = meta, FontSize = _b.FontSize - 0.5, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1);
            second.Children.Add(text);
        }

        var lines = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(first);
        lines.Children.Add(second);

        var body = new Panel();
        body.Children.Add(lines);
        if (unread)
        {
            body.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = selected ? _b.CursorForeground : _b.Accent,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-15, Math.Round(_rowHeight / 2 - 9), 0, 0),
            });
        }

        row.Child = body;
    }

    private static bool Has(ListPreviewItem item, string tag) => item.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) == true;

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
