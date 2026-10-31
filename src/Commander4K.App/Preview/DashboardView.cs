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
/// A <c>c4k.dir</c> dashboard: plain tiles look as before, card tiles become live cards read by a
/// <see cref="CardRefreshLoop"/> while the view is shown. Cards that sign in or change something wait for the user's
/// trust in the file. <see cref="StopAsync"/> must be called when the view goes away.
/// </summary>
public sealed class DashboardView : ScrollViewer
{
    private const double SmallWidth = 260;
    private const double WideWidth = 532;

    private readonly AppHost _app;
    private readonly DashboardPreview _dashboard;
    private readonly Func<PreviewTile, Task> _open;
    private readonly Func<PreviewTile, double, Control> _shortcut;
    private readonly Func<FolderConfig, Task<bool>>? _askTrust;
    private readonly Action<string, bool> _notify;
    private readonly ConcurrentDictionary<int, CardSource> _sources = new();
    private readonly Dictionary<int, CardView> _cards = [];
    private readonly Dictionary<int, string> _targets = [];
    private readonly Dictionary<string, Bitmap?> _thumbnails = new(StringComparer.Ordinal);
    private readonly FolderSizeCache? _sizes;
    private CardRefreshLoop? _loop;
    private SkinBrushes _brushes;
    private volatile bool _trusted;
    private bool _stopped;

    /// <param name="shortcut">Draws a plain tile (the preview's usual tile look) at the given width.</param>
    /// <param name="askTrust">Asks the user to trust the <c>c4k.dir</c>; <c>null</c> = such cards stay locked.</param>
    /// <param name="notify">Status-bar message (text, is error) for card actions that fail.</param>
    public DashboardView(AppHost app, DashboardPreview dashboard, SkinBrushes brushes, Func<PreviewTile, Task> open,
        Func<PreviewTile, double, Control> shortcut, Func<FolderConfig, Task<bool>>? askTrust, Action<string, bool> notify)
    {
        _notify = notify;
        _app = app;
        _dashboard = dashboard;
        _brushes = brushes;
        _open = open;
        _shortcut = shortcut;
        _askTrust = askTrust;
        _sizes = app.BuiltInPreviews.OfType<FolderPreviewPlugin>().FirstOrDefault()?.Sizes;
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        foreach (var slot in dashboard.Slots.Where(s => s.Tile.Card is not null))
        {
            _cards[slot.Index] = new CardView(this, slot);
            if (slot.Tile.Open is { } open2)
            {
                _targets[slot.Index] = dashboard.Config.Resolve(open2);
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

        foreach (var slot in _dashboard.Slots.Where(s => string.Equals(s.Tile.Card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase)))
        {
            if (_targets.TryGetValue(slot.Index, out var target))
            {
                await _app.SshMonitor.ReleaseAsync(target);
            }
        }
    }

    private void Build()
    {
        var stack = new StackPanel { Margin = new Thickness(12, 8), Spacing = 6 };
        if (_dashboard.Title.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = _dashboard.Title, FontSize = _brushes.FontSize + 4, FontWeight = FontWeight.Bold, Foreground = _brushes.DirectoryForeground });
        }

        if (_dashboard.Description is { Length: > 0 } description)
        {
            stack.Children.Add(new TextBlock { Text = description, Foreground = _brushes.MutedForeground, TextWrapping = TextWrapping.Wrap });
        }

        var wrap = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
        foreach (var slot in _dashboard.Slots)
        {
            wrap.Children.Add(_cards.TryGetValue(slot.Index, out var card) ? card.Render() : _shortcut(slot.Shortcut, SmallWidth));
        }

        stack.Children.Add(wrap);
        Content = stack;
    }

    /// <summary>
    /// Every card resolves its tile inside its own read: the cards resolve side by side, each within the loop's time
    /// limit, so a tile pointing at a hung share holds back only its own card. Cancellation and timeouts stay in the loop.
    /// </summary>
    private void Start()
    {
        var resolver = _app.CreateCardResolver();
        var config = _dashboard.Config;
        var loop = new CardRefreshLoop();
        loop.Updated += state => Dispatcher.UIThread.Post(() =>
        {
            if (!_stopped && _cards.TryGetValue(state.Index, out var card))
            {
                card.Update(state);
            }
        });

        foreach (var slot in _dashboard.Slots.Where(s => s.Tile.Card is not null))
        {
            var tile = slot.Tile;
            var index = slot.Index;
            CardSource? source = null; // one read at a time per card (the loop guarantees it), so no lock is needed
            loop.Add(slot.Index, async (previous, ct) =>
            {
                source ??= await resolver.ResolveAsync(config, tile, ct).ConfigureAwait(false)
                    ?? new CardSource((_, _) => Task.FromResult(new PreviewCard(null, Loc.T("Ten kafelek nie jest kartą."), "⚠", CardStatus.Error, [], [])), false, null);
                _sources[index] = source; // the UI thread reads it for c4k:plugin/ buttons
                if (source.Unavailable is { } why)
                {
                    return Locked(why, unlock: false);
                }

                if (source.NeedsTrust && !(_trusted || (_trusted = _app.FolderTrust.IsTrusted(config))))
                {
                    return Locked(Loc.T("Ta karta łączy się z serwerem albo może coś na nim zmienić. Zezwól na ten plik c4k.dir, żeby działała."), unlock: true);
                }

                return await source.Load(previous, ct).ConfigureAwait(false);
            }, tile.Refresh);
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
                if (_askTrust is not null && await _askTrust(_dashboard.Config))
                {
                    _trusted = true;
                    foreach (var card in _cards.Keys)
                    {
                        _loop?.RefreshNow(card);
                    }
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
                return Bitmap.DecodeToWidth(stream, 112);
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

        public Control Render()
        {
            // The frame outlives a rebuild (a skin change): take it out of the old grid before it joins the new one.
            if (_frame.Parent is Panel old)
            {
                old.Children.Remove(_frame);
            }

            var brushes = owner._brushes;
            _frame.Width = slot.Tile.Size == TileSize.Wide ? WideWidth : SmallWidth;
            _frame.Margin = new Thickness(6);
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
            var body = new StackPanel { Spacing = 4, Margin = new Thickness(10, 8) };

            var heading = new DockPanel();
            var spinner = new TextBlock { Text = _state.Loading ? "⏳" : string.Empty, Foreground = brushes.MutedForeground, Margin = new Thickness(4, 0, 0, 0) };
            DockPanel.SetDock(spinner, Dock.Right);
            heading.Children.Add(spinner);
            heading.Children.Add(new TextBlock
            {
                Text = $"{card?.Icon ?? slot.Shortcut.Icon ?? "▪"}  {card?.Title ?? slot.Tile.Title}",
                FontWeight = FontWeight.SemiBold,
                Foreground = brushes.FileForeground,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            body.Children.Add(heading);

            if ((card?.Subtitle ?? slot.Tile.Subtitle) is { Length: > 0 } subtitle)
            {
                body.Children.Add(new TextBlock { Text = subtitle, FontSize = brushes.FontSize - 1, Foreground = brushes.MutedForeground, TextWrapping = TextWrapping.Wrap, MaxLines = 3 });
            }

            if (card is not null)
            {
                for (var i = 0; i < card.Items.Count; i++)
                {
                    body.Children.Add(Item(card.Items[i], i, brushes));
                }
            }

            if (_state.Error is { } error)
            {
                body.Children.Add(new TextBlock { Text = "⚠ " + error, Foreground = brushes.Error, TextWrapping = TextWrapping.Wrap, MaxLines = 3 });
            }

            var actions = new WrapPanel { Margin = new Thickness(-3, 2, 0, 0) };
            IEnumerable<CardAction> buttons = card?.Actions ?? [];
            if (_state.Error is not null && !buttons.Any(a => a.Target == CardActions.Refresh))
            {
                buttons = buttons.Append(new CardAction(Loc.T("Odśwież"), CardActions.Refresh));
            }

            foreach (var action in buttons)
            {
                var button = new Button { Content = action.Label, Padding = new Thickness(8, 2), Margin = new Thickness(3), FontSize = brushes.FontSize - 1 };
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

        private Control Item(CardItem item, int position, SkinBrushes brushes) => item switch
        {
            CardValue value => Row(value.Label, value.Value, StatusBrush(value.Status, brushes) ?? brushes.FileForeground, brushes),
            CardProgress progress => Bar(progress, brushes),
            CardText text => new TextBlock { Text = text.Text, Foreground = brushes.FileForeground, TextWrapping = TextWrapping.Wrap, MaxLines = 4 },
            CardChart chart => new ChartControl(brushes) { Chart = chart.Chart, Height = 80, Margin = new Thickness(0, 2) },
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
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            for (var i = 0; i < thumbnails.Images.Count; i++)
            {
                var image = new Image { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
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
