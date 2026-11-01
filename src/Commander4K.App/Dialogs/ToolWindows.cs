using System.Collections.ObjectModel;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Composition;
using Commander4K.App.Panels;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Streaming;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Viewers;

namespace Commander4K.App.Dialogs;

/// <summary>F3: text or hex view, loaded entirely in the background.</summary>
public sealed class ViewerWindow : Window
{
    private readonly CancellationTokenSource _loading = new();
    private SkinBrushes _brushes;
    private TextBlock _status = null!;
    private WaveDots _busy = null!;
    private TextBox _text = null!;
    private Border _statusBar = null!;

    public ViewerWindow(AppHost host, SkinBrushes brushes, VfsEntry entry)
    {
        _brushes = brushes;
        Title = Loc.T("Podgląd – {0}", entry.Name);
        Width = 960;
        Height = 680;
        WindowSkin.Apply(this, brushes, panelSurface: true);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _status = new TextBlock().Text(Loc.T("Wczytywanie…")).Margin(10, 0).VerticalAlignment(VerticalAlignment.Center).Foreground(brushes.StatusForeground);
        _busy = new WaveDots { Foreground = brushes.StatusForeground, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        _text = new TextBox()
            .IsReadOnly(true)
            .AcceptsReturn(true)
            .TextWrapping(TextWrapping.NoWrap)
            .FontFamily(brushes.MonospaceFamily)
            .Background(brushes.CodeBackground)
            .Foreground(brushes.FileForeground)
            .Padding(12, 8)
            .BorderThickness(new Avalonia.Thickness(0));
        _statusBar = new Border().Background(brushes.StatusBackground)
            .Child(new Grid().Cols("Auto,*").Children(_busy, _status.Col(1)));
        StyleStatusBar(brushes);
        Content = new Grid().Rows("*,Auto").Children(
            new ScrollViewer().Row(0).HorizontalScrollBarVisibility(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto).Content(_text),
            _statusBar.Row(1));

        Closed += (_, _) => _loading.Cancel();
        Opened += async (_, _) =>
        {
            try
            {
                var token = _loading.Token;
                var content = await Task.Run(async () =>
                {
                    await using var stream = await host.Router.OpenReadAsync(entry.Path, token);
                    return await host.Viewer.LoadAsync(entry, stream, token);
                }, token);

                (_text.Text, _status.Text) = content switch
                {
                    TextViewerContent t => (t.Text, Loc.T("Tekst · {0}", FileViewportCanvas.FormatSize(entry.Size))),
                    HexViewerContent h => (await Task.Run(() => TextHexViewerPlugin.FormatHex(h.Data.Span), token),
                        Loc.T("Hex · pokazano {0} z {1}", FileViewportCanvas.FormatSize(h.Data.Length), FileViewportCanvas.FormatSize(h.TotalLength))),
                    _ => (string.Empty, Loc.T("Nieobsługiwany typ podglądu")),
                };
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _status.Text = "⚠ " + ex.Message;
                _status.Foreground = _brushes.Error;
            }
            finally
            {
                _busy.IsVisible = false;
            }
        };
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        WindowSkin.Apply(this, brushes, panelSurface: true);
        _text.Background = brushes.CodeBackground;
        _text.Foreground = brushes.FileForeground;
        _statusBar.Background = brushes.StatusBackground;
        _status.Foreground = brushes.StatusForeground;
        _busy.Foreground = brushes.StatusForeground;
        _brushes = brushes;
        StyleStatusBar(brushes);
    }

    private void StyleStatusBar(SkinBrushes b)
    {
        _statusBar.MinHeight = b.IsModern ? 26 : 0;
        _statusBar.BorderBrush = b.PanelBorder;
        _statusBar.BorderThickness = b.IsModern ? new Avalonia.Thickness(0, 1, 0, 0) : default;
        _status.FontSize = b.IsModern ? 11 : b.FontSize;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Escape or Key.F3 or Key.F10)
        {
            Close();
            e.Handled = true;
        }
    }

}

/// <summary>Read-only text output (plugin reports, PowerShell results) with copy to clipboard.</summary>
public sealed class TextWindow : Window
{
    private readonly string _text;
    private readonly TextBox _box;
    private readonly Border _card = new();
    private readonly Border _footer = new();
    private readonly Button _copy;
    private readonly Button _close;

    public TextWindow(SkinBrushes brushes, string title, string text)
    {
        _text = text;
        Title = title;
        Width = 960;
        Height = 640;
        WindowSkin.Apply(this, brushes, panelSurface: true);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _box = new TextBox()
            .Text(text)
            .IsReadOnly(true)
            .AcceptsReturn(true)
            .TextWrapping(TextWrapping.NoWrap)
            .FontFamily(brushes.MonospaceFamily)
            .Background(Brushes.Transparent)
            .Foreground(brushes.FileForeground)
            .Padding(12, 8)
            .BorderThickness(new Avalonia.Thickness(0));
        _copy = new Button().Content(Loc.T("Kopiuj")).OnClick(async _ => await SkinPickerDialog.CopyToClipboardAsync(this, _text));
        _close = new Button().Content(Loc.T("Zamknij")).OnClick(_ => Close());
        _card.Child = new ScrollViewer().HorizontalScrollBarVisibility(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto).Content(_box);
        _footer.Child = new StackPanel().Orientation(Orientation.Horizontal).HorizontalAlignment(HorizontalAlignment.Right).Spacing(8).Children(_copy, _close);
        Content = new Grid().Rows("*,Auto").Children(_card.Row(0), _footer.Row(1));
        StyleParts(brushes);
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        WindowSkin.Apply(this, brushes, panelSurface: true);
        _box.Foreground = brushes.FileForeground;
        StyleParts(brushes);
    }

    private void StyleParts(SkinBrushes b)
    {
        var modern = b.IsModern;
        _card.Background = b.CodeBackground;
        _card.BorderBrush = b.PanelBorder;
        _card.BorderThickness = new Avalonia.Thickness(modern ? 1 : 0);
        _card.CornerRadius = new Avalonia.CornerRadius(modern ? 6 : 0);
        _card.Margin = modern ? new Avalonia.Thickness(12, 12, 12, 0) : default;
        _card.ClipToBounds = true;
        _footer.Background = b.HeaderBackground;
        _footer.BorderBrush = b.PanelBorder;
        _footer.BorderThickness = new Avalonia.Thickness(0, 1, 0, 0);
        _footer.Padding = new Avalonia.Thickness(12, 10);
        _footer.Margin = new Avalonia.Thickness(0, modern ? 12 : 0, 0, 0);
        foreach (var button in new[] { _copy, _close })
        {
            button.MinWidth = 88;
            button.Height = 30;
        }

        WindowSkin.ApplyAccent(_close, b);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Escape or Key.F3 or Key.F10)
        {
            Close();
            e.Handled = true;
        }
    }
}

/// <summary>Alt+F7: parallel search with results streamed into the list; Esc stops instantly.</summary>
public sealed class SearchWindow : Window
{
    private readonly AppHost _host;
    private SkinBrushes _brushes;
    private readonly Action<VfsEntry> _goTo;
    private readonly TextBox _root;
    private readonly TextBox _pattern;
    private readonly TextBox _text;
    private readonly CheckBox _caseSensitive;
    private readonly Button _start;
    private readonly TextBlock _status;
    private readonly ObservableCollection<SearchResultItem> _results = [];
    private readonly ListBox _list;
    private readonly Border _form = new();
    private readonly Border _listCard = new();
    private readonly Border _footer = new();
    private CancellationTokenSource? _running;

    public SearchWindow(AppHost host, SkinBrushes brushes, string rootPath, Action<VfsEntry> goTo)
    {
        _host = host;
        _brushes = brushes;
        _goTo = goTo;
        Title = Loc.T("Szukaj (Alt+F7)");
        Width = 820;
        Height = 620;
        WindowSkin.Apply(this, brushes);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _root = new TextBox().Text(rootPath);
        _pattern = new TextBox().Text("*").PlaceholderText(Loc.T("Nazwa, np. *.log"));
        _text = new TextBox().PlaceholderText(Loc.T("Zawiera tekst (opcjonalnie)"));
        _caseSensitive = new CheckBox().Content(Loc.T("Rozróżniaj wielkość liter"));
        _start = new Button().Content(Loc.T("Szukaj")).IsDefault(true).OnClick(async _ => await ToggleAsync());
        _status = new TextBlock().VerticalAlignment(VerticalAlignment.Center).Foreground(brushes.MutedForeground);
        _list = new ListBox().ItemsSource(_results);
        _list.DoubleTapped += (_, _) => GoToSelected();
        _listCard.Child = _list;
        _form.Child = new Grid().Rows("Auto,Auto,Auto").Children(
            Row(0, Loc.T("W:"), _root),
            Row(1, Loc.T("Nazwa:"), Ds.SearchField(brushes, _pattern)),
            Row(2, Loc.T("Tekst:"), _text));
        _footer.Child = new Grid().Cols("*,Auto,Auto").Children(
            _status,
            _caseSensitive.Col(1).Margin(0, 0, 16, 0),
            _start.Col(2));
        Content = new Grid().Rows("Auto,*,Auto").Children(_form.Row(0), _listCard.Row(1), _footer.Row(2));
        StyleParts(brushes);
        Opened += (_, _) => _pattern.Focus();
        Closed += (_, _) => _running?.Cancel();
    }

    private void StyleParts(SkinBrushes b)
    {
        var modern = b.IsModern;
        _form.Padding = new Avalonia.Thickness(14, 14, 14, 8);
        _listCard.Background = b.PanelBackground;
        _listCard.BorderBrush = b.PanelBorder;
        _listCard.BorderThickness = new Avalonia.Thickness(1);
        _listCard.CornerRadius = new Avalonia.CornerRadius(modern ? 6 : 0);
        _listCard.Margin = new Avalonia.Thickness(14, 0);
        _listCard.ClipToBounds = true;
        _list.Background = Brushes.Transparent;
        _list.BorderThickness = default;
        _footer.Background = b.HeaderBackground;
        _footer.BorderBrush = b.PanelBorder;
        _footer.BorderThickness = new Avalonia.Thickness(0, 1, 0, 0);
        _footer.Padding = new Avalonia.Thickness(14, 10);
        _footer.Margin = new Avalonia.Thickness(0, 14, 0, 0);
        _start.MinWidth = 96;
        _start.Height = 30;
        _status.Foreground = b.MutedForeground;
        WindowSkin.ApplyAccent(_start, b);
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        WindowSkin.Apply(this, brushes);
        StyleParts(brushes);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (_running is not null)
            {
                _running.Cancel();
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _list.IsFocused)
        {
            GoToSelected();
            e.Handled = true;
        }
    }

    private static Grid Row(int row, string label, Control input) =>
        new Grid().Row(row).Cols("70,*").Margin(0, 0, 0, 8).Children(
            new TextBlock().Text(label).FontSize(12).VerticalAlignment(VerticalAlignment.Center).Opacity(0.75),
            input.Col(1));

    private async Task ToggleAsync()
    {
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }

        var root = _root.Text?.Trim() ?? string.Empty;
        var provider = _host.FindSearchProvider(root);
        if (provider is null)
        {
            _status.Text = Loc.T("⚠ Wyszukiwanie nie jest dostępne dla tej lokalizacji.");
            return;
        }

        var query = new SearchQuery(
            root,
            string.IsNullOrWhiteSpace(_pattern.Text) ? "*" : _pattern.Text.Trim(),
            string.IsNullOrEmpty(_text.Text) ? null : _text.Text,
            _caseSensitive.IsChecked == true);

        _results.Clear();
        _status.Foreground = _brushes.MutedForeground;
        _running = new CancellationTokenSource();
        var token = _running.Token;
        _start.Content = Loc.T("Stop (Esc)");
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var channel = Channel.CreateUnbounded<SearchHit>(new UnboundedChannelOptions { SingleReader = true });
            var producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var hit in _host.Executor.StreamAsync(provider.SearchAsync(query, token), token))
                    {
                        channel.Writer.TryWrite(hit);
                    }

                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);

            await foreach (var batch in channel.Reader.ReadBatchesAsync(TimeSpan.FromMilliseconds(50), 2048, token))
            {
                foreach (var hit in batch)
                {
                    _results.Add(new SearchResultItem(hit));
                }

                _status.Text = Loc.T("Szukanie… {0:N0} wyników", _results.Count);
            }

            await producer;
            _status.Text = Loc.T("Gotowe: {0:N0} wyników w {1:0.0} s", _results.Count, started.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            _status.Text = Loc.T("Zatrzymano: {0:N0} wyników", _results.Count);
        }
        catch (Exception ex)
        {
            _status.Text = "⚠ " + ex.Message;
            _status.Foreground = _brushes.Error;
        }
        finally
        {
            _running?.Dispose();
            _running = null;
            _start.Content = Loc.T("Szukaj");
        }
    }

    private void GoToSelected()
    {
        if (_list.SelectedItem is SearchResultItem item)
        {
            _goTo(item.Hit.Entry);
        }
    }

    private sealed record SearchResultItem(SearchHit Hit)
    {
        public override string ToString() =>
            Hit.Snippet is { Length: > 0 } snippet ? $"{Hit.Entry.Path}    — {snippet}"
            : Hit.Offset is long offset ? $"{Hit.Entry.Path}    (offset {offset:N0})"
            : Hit.Entry.Path;
    }
}
