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
    /// <summary>The 26 px status bar of tool windows (design system: <c>statusBackground</c>, a rule above).</summary>
    internal static Border StatusBar(SkinBrushes brushes, Control content) =>
        new Border().MinHeight(26).Background(brushes.StatusBackground).BorderBrush(brushes.PanelBorder)
            .BorderThickness(new Avalonia.Thickness(0, 1, 0, 0)).Child(content);

    private readonly CancellationTokenSource _loading = new();

    public ViewerWindow(AppHost host, SkinBrushes brushes, VfsEntry entry)
    {
        Title = Loc.T("Podgląd – {0}", entry.Name);
        Width = 960;
        Height = 680;
        Ds.ApplySurface(this, brushes, WindowSurface.Panel);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var status = new TextBlock().Text(Loc.T("Wczytywanie…")).Margin(8, 4).FontSize(12).Foreground(brushes.StatusForeground);
        var busy = new WaveDots { Foreground = brushes.StatusForeground, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        var text = new TextBox()
            .IsReadOnly(true)
            .AcceptsReturn(true)
            .TextWrapping(TextWrapping.NoWrap)
            .FontFamily(brushes.MonospaceFamily)
            .Background(brushes.PanelBackground)
            .Foreground(brushes.FileForeground)
            .BorderThickness(new Avalonia.Thickness(0));
        Content = new Grid().Rows("*,Auto").Children(
            new ScrollViewer().Row(0).HorizontalScrollBarVisibility(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto).Content(text),
            StatusBar(brushes, new Grid().Cols("Auto,*").Children(busy, status.Col(1))).Row(1));

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

                (text.Text, status.Text) = content switch
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
                status.Text = "⚠ " + ex.Message;
                status.Foreground = brushes.Error;
            }
            finally
            {
                busy.IsVisible = false;
            }
        };
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
    public TextWindow(SkinBrushes brushes, string title, string text)
    {
        Title = title;
        Width = 960;
        Height = 640;
        Ds.ApplySurface(this, brushes, WindowSurface.Panel);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var box = new TextBox()
            .Text(text)
            .IsReadOnly(true)
            .AcceptsReturn(true)
            .TextWrapping(TextWrapping.NoWrap)
            .FontFamily(brushes.MonospaceFamily)
            .Background(brushes.PanelBackground)
            .Foreground(brushes.FileForeground)
            .BorderThickness(new Avalonia.Thickness(0));
        Content = new Grid().Rows("*,Auto").Children(
            new ScrollViewer().Row(0).HorizontalScrollBarVisibility(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto).Content(box),
            Ds.Footer(brushes, null,
                Ds.Button(brushes, Loc.T("Kopiuj"), () => _ = DesktopShell.CopyToClipboardAsync(this, text)),
                Ds.Button(brushes, Loc.T("Zamknij"), Close, ButtonKind.Primary)).Row(1));
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
    private readonly SkinBrushes _brushes;
    private readonly Action<VfsEntry> _goTo;
    private readonly TextBox _root;
    private readonly TextBox _pattern;
    private readonly TextBox _text;
    private readonly CheckBox _caseSensitive;
    private readonly Button _start;
    private readonly TextBlock _status;
    private readonly ObservableCollection<SearchResultItem> _results = [];
    private readonly ListBox _list;
    private CancellationTokenSource? _running;

    public SearchWindow(AppHost host, SkinBrushes brushes, string rootPath, Action<VfsEntry> goTo)
    {
        _host = host;
        _brushes = brushes;
        _goTo = goTo;
        Title = Loc.T("Szukaj (Alt+F7)");
        Width = 820;
        Height = 620;
        Ds.ApplySurface(this, brushes, WindowSurface.Panel);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _root = new TextBox().Text(rootPath);
        _pattern = new TextBox().Text("*").PlaceholderText(Loc.T("Nazwa, np. *.log"));
        _text = new TextBox().PlaceholderText(Loc.T("Zawiera tekst (opcjonalnie)"));
        _caseSensitive = new CheckBox().Content(Loc.T("Rozróżniaj wielkość liter"));
        _start = Ds.Button(brushes, Loc.T("Szukaj"), () => _ = ToggleAsync(), ButtonKind.Primary).IsDefault(true);
        _start.MinWidth = Ds.ButtonMinWidth;
        _status = new TextBlock().Margin(12, 4).FontSize(12).Foreground(brushes.StatusForeground).TextTrimming(TextTrimming.CharacterEllipsis);
        _list = new ListBox().ItemsSource(_results);
        _list.DoubleTapped += (_, _) => GoToSelected();

        DockPanel.SetDock(_start, Dock.Right);
        // Design system (tool windows): the form is a tool bar on headerBackground, results on the panel, status bar below.
        var form = new Border().Background(brushes.IsModern ? brushes.HeaderBackground : brushes.WindowBackground).BorderBrush(brushes.PanelBorder)
            .BorderThickness(new Avalonia.Thickness(0, 0, 0, 1)).Padding(14, 12).Child(new Grid().Rows("Auto,Auto,Auto,Auto").Children(
                Row(0, Loc.T("W:"), _root),
                Row(1, Loc.T("Nazwa:"), _pattern),
                Row(2, Loc.T("Tekst:"), _text),
                new DockPanel().Row(3).Margin(96, 2, 0, 0).Children(_start, _caseSensitive.VerticalAlignment(VerticalAlignment.Center))));
        _list.Background = brushes.PanelBackground;
        _list.BorderThickness = new Avalonia.Thickness(0);
        Content = new Grid().Rows("Auto,*,Auto").Children(form, _list.Row(1), ViewerWindow.StatusBar(brushes, _status).Row(2));
        Opened += (_, _) => _pattern.Focus();
        Closed += (_, _) => _running?.Cancel();
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

    private Grid Row(int row, string label, Control input) =>
        new Grid().Row(row).Cols("96,*").Margin(0, 0, 0, 6).Children(
            Ds.FieldLabel(_brushes, label).Margin(0).VerticalAlignment(VerticalAlignment.Center),
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
