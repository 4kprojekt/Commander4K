using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Theming;
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

    public PaletteView(IPalettePlugin palette, IPluginHost host, PaletteDock dock, SkinBrushes brushes, Action<string, bool> notify)
    {
        _palette = palette;
        _host = host;
        Dock = dock;
        _brushes = brushes;
        _notify = notify;

        var vertical = dock != PaletteDock.Bottom;
        _items = vertical ? new StackPanel { Spacing = 2 } : new WrapPanel { Orientation = Orientation.Horizontal };
        // Between the panels there is no room for title and buttons on one line, so they wrap.
        _header = dock == PaletteDock.BetweenPanels ? new WrapPanel { Margin = new Thickness(0, 0, 0, 2) } : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var title = new TextBlock
        {
            Text = palette.Title,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = dock == PaletteDock.BetweenPanels ? BetweenWidth - 10 : double.PositiveInfinity,
        };
        _header.Children.Add(title);
        foreach (var action in palette.Actions.Where(a => !a.ForItem))
        {
            var a = action;
            var button = SmallButton(action.Icon ?? action.Title, action.Title);
            button.Click += async (_, _) => await RunActionAsync(a.Id, null);
            _header.Children.Add(button);
        }

        var layout = new DockPanel();
        DockPanel.SetDock(_header, Avalonia.Controls.Dock.Top);
        layout.Children.Add(_header);
        layout.Children.Add(vertical ? new ScrollViewer { Content = _items, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } : _items);
        Child = layout;
        Padding = new Thickness(4);
        Width = dock switch
        {
            PaletteDock.BetweenPanels => BetweenWidth,
            PaletteDock.Left or PaletteDock.Right => SideWidth,
            _ => double.NaN,
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
        Background = brushes.StatusBackground;
        BorderBrush = brushes.PanelBorder;
        BorderThickness = Dock switch
        {
            PaletteDock.Bottom => new Thickness(0, 1, 0, 0),
            _ => new Thickness(1),
        };
        foreach (var text in _header.Children.OfType<TextBlock>())
        {
            text.Foreground = brushes.HeaderForeground;
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
                        Text = hint is null ? "(pusto)" : $"(pusto — „{hint.Icon ?? hint.Title}” {hint.Title.ToLowerInvariant()})",
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
        var compact = Dock == PaletteDock.BetweenPanels;
        var icon = new TextBlock { Text = item.Icon ?? "▸", FontSize = compact ? 16 : 13, Foreground = _brushes.Accent, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock
        {
            Text = item.Title,
            FontSize = compact ? 10.5 : 12,
            Foreground = _brushes.FileForeground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = compact ? TextAlignment.Center : TextAlignment.Left,
            HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = compact
            ? (Control)new StackPanel { Children = { icon, label } }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, label } };

        var button = new Button
        {
            Content = content,
            Padding = compact ? new Thickness(2, 4) : new Thickness(8, 3),
            Margin = new Thickness(1),
            HorizontalAlignment = Dock == PaletteDock.Bottom ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
            HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            Background = _brushes.PanelBackground,
            BorderBrush = _brushes.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_brushes.Skin.Chrome.CornerRadius),
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
        var button = new Button { Content = text, Padding = new Thickness(6, 0), Margin = new Thickness(0, 0, 2, 0), FontSize = 12, MinHeight = 20 };
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
