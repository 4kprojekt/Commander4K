using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Theming;
using Commander4K.Core.Dashboard;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Dashboard;
using Commander4K.Plugins.BuiltIn.Previews;

namespace Commander4K.App.Preview;

/// <summary>
/// A dashboard of a folder's <c>.c4k</c> items: plain tiles look as before, card tiles become live cards read by a
/// <see cref="CardRefreshLoop"/> while the view is shown. Cards that sign in or change something wait for the user's
/// trust in their own file. <see cref="StopAsync"/> must be called when the view goes away.
/// </summary>
public sealed class DashboardView : ScrollViewer
{
    /// <summary>Narrowest column; the grid adds columns as the panel widens and stretches them to fill it.</summary>
    private const double UnitWidth = 240;

    /// <summary>Lowest row; cards taller than this make their row grow instead of being cut.</summary>
    private const double UnitHeight = 136;

    private const double TileGap = 12;
    private const double SideMargin = 12;

    private readonly AppHost _app;
    private readonly DashboardPreview _dashboard;
    private readonly Func<PreviewTile, Task> _open;
    private readonly Func<PreviewTile, double, Control> _shortcut;
    private readonly Func<int, Task>? _resizeTile;
    private readonly Func<C4kItem, Task<bool>>? _askTrust;
    private readonly Action<string, bool> _notify;
    private readonly ConcurrentDictionary<int, CardSource> _sources = new();
    private readonly Dictionary<int, CardView> _cards = [];
    private readonly Dictionary<int, string> _targets = [];
    private readonly List<Control> _tiles = [];
    private Grid? _grid;
    private int _columns;
    private readonly Dictionary<string, Bitmap?> _thumbnails = new(StringComparer.Ordinal);
    private readonly FolderSizeCache? _sizes;
    private CardRefreshLoop? _loop;
    private SkinBrushes _brushes;
    private bool _stopped;

    /// <param name="shortcut">Draws a plain tile (the preview's usual tile look) at the given width.</param>
    /// <param name="askTrust">Asks the user to trust one item's <c>.c4k</c> file; <c>null</c> = such cards stay locked.</param>
    /// <param name="notify">Status-bar message (text, is error) for card actions that fail.</param>
    public DashboardView(AppHost app, DashboardPreview dashboard, SkinBrushes brushes, Func<PreviewTile, Task> open,
        Func<PreviewTile, double, Control> shortcut, Func<C4kItem, Task<bool>>? askTrust,
        Action<string, bool> notify, Func<int, Task>? resizeTile = null)
    {
        _notify = notify;
        _app = app;
        _dashboard = dashboard;
        _brushes = brushes;
        _open = open;
        _shortcut = shortcut;
        _resizeTile = resizeTile;
        _askTrust = askTrust;
        _sizes = app.BuiltInPreviews.OfType<FolderPreviewPlugin>().FirstOrDefault()?.Sizes;
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        SizeChanged += (_, _) => LayoutTiles();
        foreach (var slot in dashboard.Slots.Where(s => s.Item.Card is not null))
        {
            _cards[slot.Index] = new CardView(this, slot);
            if (slot.Item.Open is { } open2)
            {
                _targets[slot.Index] = slot.Item.Resolve(open2);
            }
        }

        Build();
        if (_sizes is not null)
        {
            _sizes.Completed += OnSizeCompleted;
        }

        Start();
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        Build();
    }

    /// <summary>Stops every read and timer and releases SSH sessions; nothing updates the view afterwards.</summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        if (_sizes is not null)
        {
            _sizes.Completed -= OnSizeCompleted;
        }

        if (_loop is { } loop)
        {
            await loop.DisposeAsync();
        }

        foreach (var slot in _dashboard.Slots.Where(s => string.Equals(s.Item.Card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase)))
        {
            if (_targets.TryGetValue(slot.Index, out var target))
            {
                await _app.SshMonitor.ReleaseAsync(target);
            }
        }
    }

    private void Build()
    {
        var stack = new StackPanel { Margin = new Thickness(SideMargin, 10), Spacing = 6 };
        if (_dashboard.Title.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = _dashboard.Title, FontSize = _brushes.FontSize + 5, FontWeight = FontWeight.SemiBold, Foreground = _brushes.FileForeground });
        }

        if (_dashboard.Description is { Length: > 0 } description)
        {
            stack.Children.Add(new TextBlock { Text = description, FontSize = Math.Max(11, _brushes.FontSize - 1), Foreground = _brushes.MutedForeground, TextWrapping = TextWrapping.Wrap });
        }

        _grid = new Grid { ColumnSpacing = TileGap, RowSpacing = TileGap, Margin = new Thickness(0, 6, 0, 0) };
        _tiles.Clear();
        foreach (var slot in _dashboard.Slots)
        {
            var (_, rows) = TileSizes.Span(slot.Item.Size);
            var minHeight = UnitHeight * rows + TileGap * (rows - 1);
            Control tile;
            if (_cards.TryGetValue(slot.Index, out var card))
            {
                tile = card.Render(minHeight);
            }
            else
            {
                tile = _shortcut(slot.Shortcut, UnitWidth);
                tile.Width = double.NaN;
                tile.MinHeight = minHeight;
                tile.HorizontalAlignment = HorizontalAlignment.Stretch;
                tile.VerticalAlignment = VerticalAlignment.Stretch;
                if (tile is ContentControl content)
                {
                    content.VerticalContentAlignment = VerticalAlignment.Top;
                }
            }

            _tiles.Add(WithCommands(tile, slot));
        }

        _grid.Children.AddRange(_tiles);
        stack.Children.Add(_grid);
        Content = stack;
        _columns = 0;
        LayoutTiles();
    }

    /// <summary>Places the tiles again when the number of columns that fit the panel changes.</summary>
    private void LayoutTiles()
    {
        if (_grid is null || _tiles.Count == 0)
        {
            return;
        }

        var width = Bounds.Width > 0 ? Bounds.Width - SideMargin * 2 - 12 : UnitWidth * 3;
        var columns = TileGridLayout.ColumnsFor(width, UnitWidth, TileGap);
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        var placements = TileGridLayout.Place([.. _dashboard.Slots.Select(s => s.Item.Size)], columns);
        _grid.ColumnDefinitions.Clear();
        for (var i = 0; i < columns; i++)
        {
            _grid.ColumnDefinitions.Add(new Avalonia.Controls.ColumnDefinition(1, GridUnitType.Star));
        }

        _grid.RowDefinitions.Clear();
        for (var row = 0; row < TileGridLayout.RowCount(placements); row++)
        {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto) { MinHeight = UnitHeight });
        }

        for (var i = 0; i < _tiles.Count; i++)
        {
            var place = placements[i];
            Grid.SetColumn(_tiles[i], place.Column);
            Grid.SetRow(_tiles[i], place.Row);
            Grid.SetColumnSpan(_tiles[i], place.ColumnSpan);
            Grid.SetRowSpan(_tiles[i], place.RowSpan);
        }
    }

    /// <summary>
    /// The tile with its ⤢ resize button over its top-right corner, shown while the pointer is on the tile. Order is the
    /// file names and removing a tile is deleting its file, so there is nothing else to edit here.
    /// </summary>
    private Control WithCommands(Control tile, DashboardSlot slot)
    {
        var next = TileSizes.Next(slot.Item.Size);
        var resize = ResizeButton(Loc.T("Rozmiar kafelka: {0} → {1}", SizeName(slot.Item.Size), SizeName(next)), slot.Index);
        resize.HorizontalAlignment = HorizontalAlignment.Right;
        resize.VerticalAlignment = VerticalAlignment.Top;
        resize.Margin = new Thickness(0, 6, 6, 0);
        resize.IsVisible = false;
        var cell = new Panel { Background = Avalonia.Media.Brushes.Transparent };
        cell.Children.Add(tile);
        cell.Children.Add(resize);
        if (_resizeTile is not null)
        {
            cell.PointerEntered += (_, _) => resize.IsVisible = true;
            cell.PointerExited += (_, _) => resize.IsVisible = false;
        }

        return cell;
    }

    /// <summary>Display name of a tile size (columns × rows).</summary>
    public static string SizeName(TileSize size)
    {
        var (columns, rows) = TileSizes.Span(size);
        return columns == 0 ? Loc.T("cała szerokość") : $"{columns}×{rows}";
    }

    private Button ResizeButton(string description, int index)
    {
        var button = new Button
        {
            Content = "⤢",
            Padding = new Thickness(0),
            Width = 24,
            Height = 22,
            MinWidth = 24,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = _resizeTile is not null,
            Background = _brushes.Surface2,
            Foreground = _brushes.HeaderForeground,
            BorderBrush = _brushes.PanelBorder,
            CornerRadius = new CornerRadius(_brushes.ControlRadius),
            FontSize = Math.Max(10, _brushes.FontSize - 1),
        };
        ToolTip.SetTip(button, description);
        button.Click += async (_, args) =>
        {
            args.Handled = true;
            if (_resizeTile is not null)
            {
                await _resizeTile(index);
            }
        };
        return button;
    }

    /// <summary>
    /// Every card resolves its tile inside its own read: the cards resolve side by side, each within the loop's time
    /// limit, so a tile pointing at a hung share holds back only its own card. Cancellation and timeouts stay in the loop.
    /// </summary>
    private void Start()
    {
        var resolver = _app.CreateCardResolver();
        var loop = new CardRefreshLoop();
        loop.Updated += state => Dispatcher.UIThread.Post(() =>
        {
            if (!_stopped && _cards.TryGetValue(state.Index, out var card))
            {
                card.Update(state);
            }
        });

        foreach (var slot in _dashboard.Slots.Where(s => s.Item.Card is not null))
        {
            var tile = slot.Item;
            var index = slot.Index;
            if (string.Equals(tile.Card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase) && _targets.TryGetValue(slot.Index, out var server))
            {
                _app.SshMonitor.Retain(server); // released in StopAsync; another dashboard on the same server keeps the session
            }

            CardSource? source = null; // one read at a time per card (the loop guarantees it), so no lock is needed
            loop.Add(slot.Index, async (previous, ct) =>
            {
                source ??= await resolver.ResolveAsync(tile, ct).ConfigureAwait(false)
                    ?? new CardSource((_, _) => Task.FromResult(new PreviewCard(null, Loc.T("Ten kafelek nie jest kartą."), "⚠", CardStatus.Error, [], [])), false, null);
                _sources[index] = source; // the UI thread reads it for c4k:plugin/ buttons
                if (source.Unavailable is { } why)
                {
                    return Locked(why, unlock: false);
                }

                // Trust is per file: one card of the dashboard can be locked while the others run.
                if (source.NeedsTrust && !_app.FolderTrust.IsTrusted(tile))
                {
                    return Locked(Loc.T("Ta karta łączy się z serwerem albo może coś na nim zmienić. Zezwól na plik {0}, żeby działała.", Path.GetFileName(tile.FilePath)), unlock: true);
                }

                return await source.Load(previous, ct).ConfigureAwait(false);
            }, tile.Refresh ?? WidgetCards.DefaultRefresh(tile.Card));
        }

        if (!_stopped)
        {
            _loop = loop;
            loop.Start();
        }
    }

    private static PreviewCard Locked(string message, bool unlock) =>
        new(null, message, "🔒", CardStatus.Warning, [], unlock ? [new CardAction(Loc.T("Zezwól"), TrustAction)] : []);

    private const string TrustAction = "c4k:trust";

    private void OnSizeCompleted(string path)
    {
        foreach (var (index, target) in _targets)
        {
            if (string.Equals(target.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                _loop?.RefreshNow(index);
            }
        }
    }

    private async Task RunActionAsync(int index, CardAction action)
    {
        switch (action.Target)
        {
            case CardActions.Refresh:
                _loop?.RefreshNow(index);
                break;
            case TrustAction:
                if (_askTrust is not null && _dashboard.Slots.FirstOrDefault(s => s.Index == index) is { } trusted && await _askTrust(trusted.Item))
                {
                    _loop?.RefreshNow(index);
                }

                break;
            case var target when PreviewActionTargets.TryParse(target, out var name, out var arguments):
                await RunPluginActionAsync(index, name, arguments);
                break;
            default:
                await _open(new PreviewTile(action.Label, null, null, null, action.Target));
                break;
        }
    }

    /// <summary>A <c>c4k:plugin/…</c> card button: the preview that drew the card runs it, then the card is read again.</summary>
    private async Task RunPluginActionAsync(int index, string name, IReadOnlyDictionary<string, string> arguments)
    {
        if (!_sources.TryGetValue(index, out var source) || source.Invoke is not { } invoke)
        {
            _notify(Loc.T("Ta karta nie obsługuje akcji „{0}”.", name), true);
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await Task.Run(() => invoke(name, arguments, timeout.Token), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _notify(Loc.T("Akcja „{0}” nie skończyła się w ciągu minuty.", name), true);
        }
        catch (Exception ex)
        {
            _notify(Loc.T("Akcja „{0}”: {1}", name, ex.Message), true);
        }

        if (!_stopped)
        {
            _loop?.RefreshNow(index);
        }
    }

    private async Task DecodeAsync(string key, ReadOnlyMemory<byte> data, Image image)
    {
        if (_thumbnails.TryGetValue(key, out var cached))
        {
            image.Source = cached;
            return;
        }

        _thumbnails[key] = null;
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(data.ToArray());
                return Bitmap.DecodeToWidth(stream, 192);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or IOException)
            {
                return null;
            }
        });
        _thumbnails[key] = bitmap;
        image.Source = bitmap;
    }

    /// <summary>One card: its frame stays, its content is drawn again on every state change.</summary>
    private sealed class CardView(DashboardView owner, DashboardSlot slot)
    {
        private readonly Border _frame = new();
        private CardState _state = new(slot.Index, null, true, null);

        /// <summary>Rows the tile spans: a taller card shows more lines, a bigger chart and larger thumbnails.</summary>
        private int Rows => TileSizes.Span(slot.Item.Size).Rows;

        private bool IsWide => TileSizes.Span(slot.Item.Size).Columns is 0 or >= 2;

        public Control Render(double minHeight)
        {
            // The frame outlives a rebuild (a skin change): take it out of the old grid before it joins the new one.
            if (_frame.Parent is Panel old)
            {
                old.Children.Remove(_frame);
            }

            var brushes = owner._brushes;
            _frame.MinHeight = minHeight;
            _frame.HorizontalAlignment = HorizontalAlignment.Stretch;
            _frame.VerticalAlignment = VerticalAlignment.Stretch;
            _frame.Background = brushes.PanelBackground;
            _frame.BorderBrush = brushes.PanelBorder;
            _frame.BorderThickness = new Thickness(1);
            _frame.CornerRadius = new CornerRadius(brushes.IsModern ? 8 : 0);
            _frame.ClipToBounds = true;
            _frame.Child = Content(brushes);
            return _frame;
        }

        public void Update(CardState state)
        {
            _state = state;
            if (_frame.Parent is not null)
            {
                _frame.Child = Content(owner._brushes);
            }
        }

        private Control Content(SkinBrushes brushes)
        {
            var card = _state.Card;
            var status = _state.Error is not null ? CardStatus.Error : card?.Status ?? CardStatus.None;
            var body = new StackPanel { Spacing = 4, Margin = new Thickness(12, 10) };
            var lines = 4 * Rows;

            var heading = new DockPanel();
            var spinner = new TextBlock { Text = _state.Loading ? "⏳" : string.Empty, Foreground = brushes.MutedForeground, Margin = new Thickness(4, 0, 0, 0) };
            DockPanel.SetDock(spinner, Dock.Right);
            heading.Children.Add(spinner);
            var isClock = string.Equals(slot.Item.Card, WidgetCards.Clock, StringComparison.OrdinalIgnoreCase) && card?.Title is not null;
            heading.Children.Add(new TextBlock
            {
                Text = isClock ? $"{card?.Icon ?? "▪"}  {slot.Item.Title}" : $"{card?.Icon ?? slot.Shortcut.Icon ?? "▪"}  {card?.Title ?? slot.Item.Title}",
                FontWeight = FontWeight.SemiBold,
                Foreground = brushes.FileForeground,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            body.Children.Add(heading);
            if (isClock)
            {
                // The time is the card: as large as the tile allows.
                body.Children.Add(new TextBlock
                {
                    Text = card!.Title,
                    FontSize = brushes.FontSize * (Rows > 1 ? 4.2 : IsWide ? 3.2 : 2.6),
                    FontWeight = FontWeight.Light,
                    Foreground = brushes.FileForeground,
                    Margin = new Thickness(0, 0, 0, 2),
                });
            }

            if ((card?.Subtitle ?? slot.Item.Description) is { Length: > 0 } subtitle)
            {
                body.Children.Add(new TextBlock { Text = subtitle, FontSize = brushes.FontSize - 1, Foreground = brushes.MutedForeground, TextWrapping = TextWrapping.Wrap, MaxLines = 2 * Rows + 1 });
            }

            if (card is { Items: [CardValue kpi] })
            {
                // One value is a KPI: the number large, its label under it.
                body.Children.Add(new TextBlock
                {
                    Text = kpi.Value,
                    FontSize = brushes.FontSize * (Rows > 1 ? 3.6 : 2.4),
                    FontWeight = FontWeight.SemiBold,
                    Foreground = StatusBrush(kpi.Status, brushes) ?? brushes.FileForeground,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 4, 0, 0),
                });
                body.Children.Add(new TextBlock { Text = kpi.Label, FontSize = Math.Max(11, brushes.FontSize - 1), Foreground = brushes.MutedForeground, TextTrimming = TextTrimming.CharacterEllipsis });
            }
            else if (card is not null)
            {
                for (var i = 0; i < card.Items.Count; i++)
                {
                    body.Children.Add(Item(card.Items[i], i, lines, brushes));
                }
            }

            if (_state.Error is { } error)
            {
                body.Children.Add(new TextBlock { Text = "⚠ " + error, Foreground = brushes.Error, TextWrapping = TextWrapping.Wrap, MaxLines = 3 });
            }

            var actions = new WrapPanel { Margin = new Thickness(-3, 4, 0, 0) };
            IEnumerable<CardAction> buttons = card?.Actions ?? [];
            if (_state.Error is not null && !buttons.Any(a => a.Target == CardActions.Refresh))
            {
                buttons = buttons.Append(new CardAction(Loc.T("Odśwież"), CardActions.Refresh));
            }

            foreach (var action in buttons)
            {
                var button = new Button
                {
                    Content = action.Label,
                    Height = 26,
                    Padding = new Thickness(10, 0),
                    Margin = new Thickness(3),
                    FontSize = brushes.FontSize - 1,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = brushes.Surface2,
                    BorderBrush = brushes.PanelBorder,
                    Foreground = brushes.FileForeground,
                    CornerRadius = new CornerRadius(brushes.ControlRadius),
                };
                button.Click += async (_, e) =>
                {
                    e.Handled = true;
                    await owner.RunActionAsync(slot.Index, action);
                };
                actions.Children.Add(button);
            }

            if (actions.Children.Count > 0)
            {
                body.Children.Add(actions);
            }

            // A strip on the left edge carries the status colour; the rest of the card is a click on the tile.
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*"), Background = brushes.PanelBackground, Cursor = slot.Shortcut.Target is null ? null : new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            grid.Children.Add(new Border { Background = StatusBrush(status, brushes) ?? brushes.PanelBackground });
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            if (slot.Shortcut.Target is not null)
            {
                ToolTip.SetTip(grid, slot.Shortcut.Target);
                grid.PointerReleased += async (_, e) =>
                {
                    if (!e.Handled)
                    {
                        await owner._open(slot.Shortcut);
                    }
                };
            }

            return grid;
        }

        private Control Item(CardItem item, int position, int lines, SkinBrushes brushes) => item switch
        {
            CardValue value => Row(value.Label, value.Value, StatusBrush(value.Status, brushes) ?? brushes.FileForeground, brushes),
            CardProgress progress => Bar(progress, brushes),
            CardText text => new TextBlock { Text = text.Text, Foreground = brushes.FileForeground, TextWrapping = TextWrapping.Wrap, MaxLines = lines, TextTrimming = TextTrimming.CharacterEllipsis },
            CardChart chart => new ChartControl(brushes) { Chart = chart.Chart, Height = 80 + 96 * (Rows - 1), Margin = new Thickness(0, 2) },
            CardThumbnails thumbnails => Thumbnails(thumbnails, position, brushes),
            _ => new Panel(),
        };

        private static Control Row(string label, string value, IBrush foreground, SkinBrushes brushes)
        {
            var row = new DockPanel();
            var text = new TextBlock { Text = value, Foreground = foreground, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(text, Dock.Right);
            row.Children.Add(text);
            row.Children.Add(new TextBlock { Text = label, Foreground = brushes.MutedForeground, TextTrimming = TextTrimming.CharacterEllipsis });
            return row;
        }

        private static Control Bar(CardProgress progress, SkinBrushes brushes)
        {
            var fraction = Math.Clamp(progress.Fraction, 0, 1);
            var fill = new Grid { ColumnDefinitions = new ColumnDefinitions($"{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}*,{(1 - fraction).ToString(System.Globalization.CultureInfo.InvariantCulture)}*"), Height = 6 };
            fill.Children.Add(new Border { Background = fraction >= 0.9 ? brushes.Warning : brushes.Progress, CornerRadius = new CornerRadius(brushes.ControlRadius) });
            var track = new Border { Background = brushes.Surface2, CornerRadius = new CornerRadius(brushes.ControlRadius), Child = fill };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(Row(progress.Label, progress.Text ?? $"{fraction:P0}", brushes.FileForeground, brushes));
            stack.Children.Add(track);
            return stack;
        }

        private Control Thumbnails(CardThumbnails thumbnails, int position, SkinBrushes brushes)
        {
            var row = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
            for (var i = 0; i < thumbnails.Images.Count; i++)
            {
                var side = Rows > 1 ? 96 : 56;
                var image = new Image { Width = side, Height = side, Stretch = Stretch.UniformToFill };
                _ = owner.DecodeAsync($"{slot.Index}|{position}|{i}|{thumbnails.Images[i].Length}", thumbnails.Images[i], image);
                row.Children.Add(new Border { Background = brushes.Skeleton, CornerRadius = new CornerRadius(brushes.ControlRadius), ClipToBounds = true, Child = image });
            }

            return row;
        }

        private static IBrush? StatusBrush(CardStatus status, SkinBrushes brushes) => status switch
        {
            CardStatus.Ok => brushes.Success,
            CardStatus.Warning => brushes.Warning,
            CardStatus.Error => brushes.Error,
            _ => null,
        };
    }
}
