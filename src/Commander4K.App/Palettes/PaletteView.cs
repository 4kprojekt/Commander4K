using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.App.Palettes;

/// <summary>
/// Renders one <see cref="IPalettePlugin"/>: a header with the palette's actions and a button per item, laid out for
/// its dock (horizontal at the bottom, vertical at the sides, compact icon buttons between the panels). Items load
/// on a background thread; plugin failures are reported, never thrown at the UI.
/// </summary>
public sealed class PaletteView : Border
{
    private const double BetweenWidth = 82;
    private const double SideWidth = 190;

    private readonly IPalettePlugin _palette;
    private readonly IPluginHost _host;
    private readonly Action<string, bool> _notify;
    private readonly Panel _items;
    private readonly Panel _header;
    private readonly Action _itemsChanged;
    private SkinBrushes _brushes;
    private int _generation;

    private const double GripSize = 5;

    private readonly Border _grip;
    private Point? _dragStart;
    private double _dragStartSize;

    /// <param name="size">Width (side docks) or height (bottom) the user dragged it to last time; <c>null</c> = default.</param>
    /// <param name="resized">Called once a drag ends, with the new width or height.</param>
    /// <param name="close">Called by the ✕ button (the window hides this palette).</param>
    public PaletteView(IPalettePlugin palette, IPluginHost host, PaletteDock dock, SkinBrushes brushes, Action<string, bool> notify,
        double? size = null, Action<double>? resized = null, Action? close = null)
    {
        _palette = palette;
        _host = host;
        Dock = dock;
        _brushes = brushes;
        _notify = notify;
        _grip = new Border { Background = Brushes.Transparent };

        var vertical = dock != PaletteDock.Bottom;
        _items = vertical ? new StackPanel { Spacing = 2 } : new WrapPanel { Orientation = Orientation.Horizontal };
        // Between the panels there is no room for title and buttons on one line, so they wrap.
        // Design system: 30 px header, title on the left, 22 px icon buttons on the right. Between the panels there is
        // no room for that, so title and buttons wrap.
        _header = dock == PaletteDock.BetweenPanels ? new WrapPanel { Margin = new Thickness(0, 0, 0, 2) } : new DockPanel { Height = 30, LastChildFill = true };
        var title = new TextBlock
        {
            Text = palette.Title,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = dock == PaletteDock.BetweenPanels ? BetweenWidth - 10 : double.PositiveInfinity,
        };
        var buttons = new List<Button>();
        foreach (var action in palette.Actions.Where(a => !a.ForItem))
        {
            var a = action;
            var button = SmallButton(action.Icon ?? action.Title, action.Title);
            button.Click += async (_, _) => await RunActionAsync(a.Id, null);
            buttons.Add(button);
        }

        if (close is not null)
        {
            var closeButton = SmallButton("✕", Loc.T("Zamknij paletę (przywrócisz ją w ustawieniach: Palety)"));
            closeButton.Click += (_, _) => close();
            buttons.Add(closeButton);
        }

        if (_header is DockPanel)
        {
            for (var i = buttons.Count - 1; i >= 0; i--)
            {
                DockPanel.SetDock(buttons[i], Avalonia.Controls.Dock.Right);
                _header.Children.Add(buttons[i]);
            }

            _header.Children.Add(title);
        }
        else
        {
            _header.Children.Add(title);
            foreach (var button in buttons)
            {
                _header.Children.Add(button);
            }
        }

        var layout = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(_header, Avalonia.Controls.Dock.Top);
        layout.Children.Add(_header);
        layout.Children.Add(vertical || size is not null
            ? new ScrollViewer { Content = _items, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
            : _items);

        // A grip on the edge facing the panels: drag to widen (sides) or heighten (bottom).
        var horizontal = dock != PaletteDock.Bottom;
        var root = new Grid();
        if (horizontal)
        {
            _grip.Width = GripSize;
            _grip.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast);
            root.ColumnDefinitions = dock == PaletteDock.Right ? new ColumnDefinitions($"{GripSize},*") : new ColumnDefinitions($"*,{GripSize}");
            Grid.SetColumn(layout, dock == PaletteDock.Right ? 1 : 0);
            Grid.SetColumn(_grip, dock == PaletteDock.Right ? 0 : 1);
        }
        else
        {
            _grip.Height = GripSize;
            _grip.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth);
            root.RowDefinitions = new RowDefinitions($"{GripSize},*");
            Grid.SetRow(layout, 1);
        }

        ToolTip.SetTip(_grip, Loc.T("Przeciągnij, aby zmienić rozmiar"));
        root.Children.Add(layout);
        root.Children.Add(_grip);
        Child = root;
        if (horizontal)
        {
            Width = size ?? (dock == PaletteDock.BetweenPanels ? BetweenWidth : SideWidth);
        }
        else if (size is not null)
        {
            Height = size.Value;
        }

        _grip.PointerPressed += (_, e) =>
        {
            _dragStart = e.GetPosition(TopLevel.GetTopLevel(this));
            _dragStartSize = horizontal ? Bounds.Width : Bounds.Height;
            e.Pointer.Capture(_grip);
            e.Handled = true;
        };
        _grip.PointerMoved += (_, e) =>
        {
            if (_dragStart is not { } start)
            {
                return;
            }

            var point = e.GetPosition(TopLevel.GetTopLevel(this));
            var delta = horizontal ? point.X - start.X : point.Y - start.Y;
            var grows = dock is PaletteDock.Left or PaletteDock.BetweenPanels ? delta : -delta; // Right and bottom grow towards the panels.
            var value = Math.Clamp(_dragStartSize + grows, horizontal ? 56 : 40, horizontal ? 640 : 480);
            if (horizontal)
            {
                Width = value;
            }
            else
            {
                Height = value;
            }
        };
        _grip.PointerReleased += (_, e) =>
        {
            if (_dragStart is null)
            {
                return;
            }

            _dragStart = null;
            e.Pointer.Capture(null);
            resized?.Invoke(horizontal ? Width : Height);
        };

        _itemsChanged = () => Dispatcher.UIThread.Post(Refresh);
        palette.ItemsChanged += _itemsChanged;
        ApplySkin(brushes); // Also loads the items.
    }

    public PaletteDock Dock { get; }

    public IPalettePlugin Palette => _palette;

    /// <summary>Items depend on the current folder (external palettes get it as context), so reload after navigation.</summary>
    public bool FollowsNavigation => _palette is INavigationAwarePalette or ExternalPalettePlugin;

    /// <summary>Unsubscribes from the palette; call when the view is thrown away (dock change, plugin reload).</summary>
    public void Detach()
    {
        _palette.ItemsChanged -= _itemsChanged;
        _generation++;
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        // Design system: a card (5 px corners) on the status background; the title in the file colour.
        Background = brushes.StatusBackground;
        BorderBrush = brushes.PanelBorder;
        _grip.Background = brushes.PanelBorder; // A visible line, so the handle can be found.
        _grip.Opacity = 0.35;
        BorderThickness = brushes.IsModern || Dock != PaletteDock.Bottom ? new Thickness(1) : new Thickness(0, 1, 0, 0);
        CornerRadius = new CornerRadius(brushes.IsModern ? 5 : 0);
        ClipToBounds = true;
        foreach (var text in _header.Children.OfType<TextBlock>())
        {
            text.Foreground = brushes.IsModern ? brushes.FileForeground : brushes.HeaderForeground;
            text.FontSize = 12;
        }

        foreach (var button in _header.Children.OfType<Button>())
        {
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Foreground = brushes.MutedForeground;
        }

        Refresh();
    }

    public void Refresh()
    {
        var generation = ++_generation;
        _ = Task.Run(() => _palette.GetItemsAsync(_host, CancellationToken.None)).ContinueWith(
            task => Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                _items.Children.Clear();
                if (task.Exception is { } error)
                {
                    _items.Children.Add(new TextBlock { Text = "⚠ " + error.InnerException?.Message, Foreground = _brushes.Error, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
                    return;
                }

                foreach (var item in task.Result)
                {
                    _items.Children.Add(ItemButton(item));
                }

                if (task.Result.Count == 0)
                {
                    var hint = _palette.Actions.FirstOrDefault(a => !a.ForItem);
                    _items.Children.Add(new TextBlock
                    {
                        Text = hint is null ? Loc.T("(pusto)") : Loc.T("(pusto — „{0}” {1})", hint.Icon ?? hint.Title, hint.Title.ToLowerInvariant()),
                        Foreground = _brushes.HiddenForeground,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4),
                    });
                }
            }),
            TaskScheduler.Default);
    }

    private Button ItemButton(PaletteItem item)
    {
        // Design system: flat rows (28 px, accent icon, hover from the theme); between the panels 58 px tiles, icon above label.
        var compact = Dock == PaletteDock.BetweenPanels;
        var modern = _brushes.IsModern;
        var icon = new TextBlock { Text = item.Icon ?? "▸", FontSize = compact ? 16 : 13, Foreground = _brushes.Accent, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock
        {
            Text = item.Title,
            FontSize = compact ? 11 : 12.5,
            Foreground = _brushes.FileForeground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = compact ? TextAlignment.Center : TextAlignment.Left,
            HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = compact
            ? (Control)new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { icon, label } }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, label } };

        var button = new Button
        {
            Content = content,
            Height = compact ? 58 : 28,
            Padding = compact ? new Thickness(4, 0) : new Thickness(8, 0),
            Margin = Dock == PaletteDock.Bottom ? new Thickness(0, 0, 2, 0) : new Thickness(0, 1),
            HorizontalAlignment = Dock == PaletteDock.Bottom ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
            HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = modern ? Brushes.Transparent : _brushes.PanelBackground,
            BorderBrush = _brushes.PanelBorder,
            BorderThickness = new Thickness(modern ? 0 : 1),
            CornerRadius = new CornerRadius(modern ? 4 : 0),
        };
        ToolTip.SetTip(button, item.Tooltip ?? item.Title);
        button.Click += async (_, _) => await RunAsync(() => _palette.InvokeAsync(item.Id, _host, CancellationToken.None));

        var itemActions = _palette.Actions.Where(a => a.ForItem).ToList();
        if (itemActions.Count > 0)
        {
            var menu = new ContextMenu();
            foreach (var action in itemActions)
            {
                var a = action;
                var entry = new MenuItem { Header = $"{action.Icon} {action.Title}".Trim() };
                entry.Click += async (_, _) => await RunActionAsync(a.Id, item.Id);
                menu.Items.Add(entry);
            }

            button.ContextMenu = menu;
        }

        return button;
    }

    private Button SmallButton(string text, string tip)
    {
        var button = new Button
        {
            Content = text,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>Actions usually change the items; external palettes have no change event, so reload after each one.</summary>
    private async Task RunActionAsync(string actionId, string? itemId)
    {
        await RunAsync(() => _palette.InvokeActionAsync(actionId, itemId, _host, CancellationToken.None));
        if (_palette is ExternalPalettePlugin)
        {
            Refresh();
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _notify($"{_palette.Title}: {ex.Message}", true);
        }
    }
}
