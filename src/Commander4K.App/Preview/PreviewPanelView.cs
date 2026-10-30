using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Panels;
using Commander4K.App.Theming;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Viewers;

namespace Commander4K.App.Preview;

/// <summary>
/// Quick view of the entry under the cursor in the source panel. The best <see cref="IPreviewPlugin"/> renders it; a
/// new cursor position or folder cancels the running load at once. Markdown and text can be edited (F4) and saved
/// (Ctrl+S) through the plugin's host, which checks fs.write and audits the write.
/// </summary>
public sealed class PreviewPanelView : Grid, IPanelModeView
{
    private const int MaxFolderRows = 300;

    private readonly AppHost _app;
    private readonly FilePanelView _source;
    private readonly Action<string, bool> _notify;
    private PanelTab? _watchedTab;
    private CancellationTokenSource? _debounce;
    private bool _renderScheduled;
    private readonly Border _header;
    private readonly TextBlock _title;
    private readonly Button _editButton;
    private readonly Button _saveButton;
    private readonly ContentControl _content = new();
    private readonly TextBlock _status;
    private SkinBrushes _brushes;
    private string? _loadedKey;
    private CancellationTokenSource? _loading;
    private PreviewContent? _shown;
    private PreviewContent? _pendingRender;
    private VfsEntry? _entry;
    private IPreviewPlugin? _plugin;
    private bool _editing;
    private bool _dirty;
    private bool _escapeArmed;
    private string _savedText = string.Empty;
    private TextBox? _editor;
    private ScrollViewer? _livePreview;
    private DispatcherTimer? _liveDelay;

    public PreviewPanelView(AppHost app, FilePanelView source, SkinBrushes brushes, Action<string, bool> notify)
    {
        _app = app;
        _source = source;
        _brushes = brushes;
        _notify = notify;

        _title = new TextBlock().FontWeight(FontWeight.SemiBold).VerticalAlignment(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis);
        _editButton = new Button().Content("Edytuj (F4)").Padding(8, 2).FontSize(12).IsVisible(false).OnClick(_ => ToggleEdit());
        _saveButton = new Button().Content("Zapisz (Ctrl+S)").Padding(8, 2).FontSize(12).Margin(4, 0, 0, 0).IsVisible(false).OnClick(async _ => await SaveAsync());
        _header = new Border().Padding(8, 4).Child(new Grid().Cols("*,Auto,Auto,Auto").Children(
            _title,
            _editButton.Col(1),
            _saveButton.Col(2),
            new Button().Col(3).Content("Zamknij (Esc)").Padding(8, 2).FontSize(12).Margin(4, 0, 0, 0).OnClick(_ => CloseRequested?.Invoke())));
        _status = new TextBlock().Margin(8, 2).TextTrimming(TextTrimming.CharacterEllipsis);

        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        Children.Add(_header);
        Children.Add(_content.Row(1));
        Children.Add(_status.Row(2));
        PointerPressed += (_, _) => Activated?.Invoke();

        // Event-driven: the source tab reports cursor moves, navigation and loading; switching tabs rebinds.
        _source.ActiveTabChanged += Rebind;
        Rebind();
        ApplySkin(brushes);
    }

    public event Action? CloseRequested;

    public event Action? Activated;

    public string Title => "Podgląd";

    public Control View => this;

    public bool IsActivePanel
    {
        set { }
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        Background = brushes.PanelBackground;
        _header.Background = brushes.HeaderBackground;
        _title.Foreground = brushes.HeaderForeground;
        _status.Foreground = brushes.StatusForeground;
        if (_shown is { } shown)
        {
            Show(shown);
        }
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.F4 && CanEdit)
        {
            ToggleEdit();
            return true;
        }

        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _dirty)
        {
            _ = SaveAsync();
            return true;
        }

        if (e.Key == Key.Escape || (e.Key == Key.Back && _editor is not { IsFocused: true }))
        {
            if (_dirty && !_escapeArmed)
            {
                _escapeArmed = true;
                _notify("Niezapisane zmiany: Ctrl+S zapisuje, ponowne Esc je odrzuca.", false);
                return true;
            }

            CloseRequested?.Invoke();
            return true;
        }

        return false;
    }

    public void AppendText(string text)
    {
        // Typing goes to the editor when editing; otherwise the preview ignores it.
        if (_editor is { IsReadOnly: false } editor)
        {
            editor.Focus();
        }
    }

    public ValueTask DisposeModeAsync()
    {
        _loading?.Cancel();
        _debounce?.Cancel();
        _source.ActiveTabChanged -= Rebind;
        if (_watchedTab is not null)
        {
            _watchedTab.Changed -= OnSourceChanged;
        }

        return ValueTask.CompletedTask;
    }

    private bool CanEdit => _shown is MarkdownPreview { CanEdit: true } or TextPreview { CanEdit: true };

    private void Rebind()
    {
        if (_watchedTab is not null)
        {
            _watchedTab.Changed -= OnSourceChanged;
        }

        _watchedTab = _source.ActiveTab;
        _watchedTab.Changed += OnSourceChanged;
        OnSourceChanged();
    }

    private void OnSourceChanged()
    {
        if (_dirty)
        {
            return; // Never throw away edits because the cursor moved.
        }

        var tab = _source.ActiveTab;
        var entry = tab.CurrentEntry;
        var key = $"{tab.Path}|{entry?.Path}";
        if (key == _loadedKey || tab.IsLoading)
        {
            return;
        }

        // The previous target is no longer interesting: stop it now, start the new one once the cursor rests
        // briefly, so scrolling quickly through a list does not start a load per row.
        _loading?.Cancel();
        _debounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _debounce = debounce;
        _ = LoadAfterPauseAsync(key, entry, tab, debounce.Token);
    }

    private async Task LoadAfterPauseAsync(string key, VfsEntry? entry, PanelTab tab, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _loadedKey = key;
        var target = entry is { Kind: VfsEntryKind.ParentLink }
            ? new VfsEntry(tab.Title, tab.Path, VfsEntryKind.Directory)
            : entry;
        await LoadAsync(target);
    }

    /// <summary>Folder sizes may report many times a second; render at most every 150 ms.</summary>
    private async Task RenderSoonAsync()
    {
        await Task.Delay(150);
        _renderScheduled = false;
        if (_pendingRender is { } pending)
        {
            _pendingRender = null;
            Show(pending);
        }
    }

    private async Task LoadAsync(VfsEntry? entry)
    {
        _loading?.Cancel();
        _editing = false;
        _entry = entry;
        if (entry is not { } target)
        {
            Show(new MessagePreview("Brak elementu pod kursorem."));
            return;
        }

        var plugin = _app.PreviewPlugins.Where(p => p.CanPreview(target)).OrderByDescending(p => p.Priority).FirstOrDefault();
        _plugin = plugin;
        _title.Text = target.Name;
        if (plugin is null)
        {
            Show(new MessagePreview($"Brak podglądu dla „{target.Name}”."));
            return;
        }

        var cts = new CancellationTokenSource();
        _loading = cts;
        _status.Text = $"⏳ {plugin.DisplayName}: wczytywanie…";
        var host = _app.HostFor(plugin.Manifest);
        var progress = new Progress<PreviewContent>(partial =>
        {
            if (_loading == cts)
            {
                _pendingRender = partial;
                if (!_renderScheduled)
                {
                    _renderScheduled = true;
                    _ = RenderSoonAsync();
                }
            }
        });
        try
        {
            var result = await Task.Run(() => plugin.LoadAsync(target, host, progress, cts.Token), cts.Token);

            // Files on read-only locations (inside archives, net://) cannot be saved back, so they are view-only.
            if (!_app.Router.TryResolve(target.Path, out var provider) || (provider.Capabilities & VfsCapabilities.Write) == 0)
            {
                result = result switch
                {
                    MarkdownPreview markdown => markdown with { CanEdit = false },
                    TextPreview text => text with { CanEdit = false },
                    _ => result,
                };
            }

            if (_loading == cts)
            {
                _pendingRender = null;
                // "Edit right away" applies once per loaded file; F4 still switches back to the formatted view.
                _editing = result is MarkdownPreview { CanEdit: true } && _app.Settings.MarkdownEditByDefault;
                Show(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_loading == cts)
            {
                Show(new MessagePreview($"⚠ {ex.Message}"));
            }
        }
    }

    private void Show(PreviewContent content)
    {
        _shown = content;
        _editor = null;
        _livePreview = null;
        var editable = content is MarkdownPreview { CanEdit: true } or TextPreview { CanEdit: true };
        _editButton.IsVisible = editable;
        _editButton.Content = _editing ? "Podgląd (F4)" : "Edytuj (F4)";
        _saveButton.IsVisible = _editing;
        _status.Text = _plugin is null ? string.Empty : $"{_plugin.DisplayName} · {_entry?.Path}";

        _content.Content = content switch
        {
            MarkdownPreview markdown when _editing => MarkdownEditor(markdown.Markdown),
            MarkdownPreview markdown => Scroll(MarkdownRenderer.Render(markdown.Markdown, _brushes)),
            TextPreview text => TextView(text),
            ImagePreview image => ImageView(image),
            HexPreview hex => Monospace(TextHexViewerPlugin.FormatHex(hex.Data.Span), $"Hex · pokazano {hex.Data.Length:N0} z {hex.TotalLength:N0} B"),
            FolderPreview folder => FolderView(folder),
            MessagePreview message => new TextBlock { Text = message.Message, Foreground = _brushes.HiddenForeground, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap },
            _ => null,
        };
    }


    private void ToggleEdit()
    {
        if (!CanEdit)
        {
            return;
        }

        if (_editing && _editor is { } editor)
        {
            // Keep the edited text when going back to the formatted view.
            _shown = _shown switch
            {
                MarkdownPreview m => m with { Markdown = editor.Text ?? string.Empty },
                TextPreview t => t with { Text = editor.Text ?? string.Empty },
                var other => other,
            };
        }

        _editing = !_editing;
        Show(_shown!);
        _editor?.Focus();
    }

    private async Task SaveAsync()
    {
        if (_entry is not { } entry || _plugin is null || _editor is null)
        {
            return;
        }

        try
        {
            await _app.HostFor(_plugin.Manifest).WriteTextAsync(entry.Path, _editor.Text ?? string.Empty);
            _savedText = _editor.Text ?? string.Empty;
            _dirty = false;
            _escapeArmed = false;
            _saveButton.Content = "Zapisz (Ctrl+S)";
            _notify($"Zapisano {entry.Name}.", false);
        }
        catch (Exception ex)
        {
            _notify($"Nie zapisano: {ex.Message}", true);
        }
    }

    private Control MarkdownEditor(string markdown)
    {
        var editor = CreateEditor(markdown);
        _livePreview = Scroll(MarkdownRenderer.Render(markdown, _brushes));
        _liveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _liveDelay.Tick += (_, _) =>
        {
            _liveDelay.Stop();
            if (_livePreview is not null)
            {
                _livePreview.Content = MarkdownRenderer.Render(editor.Text ?? string.Empty, _brushes);
            }
        };
        editor.TextChanged += (_, _) =>
        {
            _liveDelay.Stop();
            _liveDelay.Start();
        };

        var grid = new Grid().Cols("*,4,*");
        grid.Children.Add(editor);
        grid.Children.Add(new GridSplitter().Col(1).Background(_brushes.PanelBorder));
        grid.Children.Add(_livePreview.Col(2));
        return grid;
    }

    private Control TextView(TextPreview text)
    {
        if (_editing)
        {
            return CreateEditor(text.Text);
        }

        return Monospace(text.Text, text.Truncated ? "Pokazano początek pliku (2 MB)." : null);
    }

    private TextBox CreateEditor(string text)
    {
        var editor = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = FontFamily.Parse("Cascadia Mono, Consolas, Courier New"),
            FontSize = _brushes.FontSize,
            Background = _brushes.PanelBackground,
            Foreground = _brushes.FileForeground,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10),
        };
        // Dirty means "differs from what was loaded or last saved", not "a TextChanged happened" (Avalonia raises
        // it when the text is first assigned).
        _savedText = text;
        editor.TextChanged += (_, _) =>
        {
            _dirty = !string.Equals(editor.Text ?? string.Empty, _savedText, StringComparison.Ordinal);
            _escapeArmed = false;
            _saveButton.Content = _dirty ? "● Zapisz (Ctrl+S)" : "Zapisz (Ctrl+S)";
        };
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _ = SaveAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F4)
            {
                ToggleEdit();
                e.Handled = true;
            }
        };
        _editor = editor;
        return editor;
    }

    private Control Monospace(string text, string? note)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = FontFamily.Parse("Cascadia Mono, Consolas, Courier New"),
            FontSize = _brushes.FontSize - 1,
            Background = _brushes.PanelBackground,
            Foreground = _brushes.FileForeground,
            BorderThickness = new Thickness(0),
        };
        if (note is not null)
        {
            _status.Text = note;
        }

        return new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = box };
    }

    private Control ImageView(ImagePreview image)
    {
        try
        {
            using var stream = new MemoryStream(image.EncodedImage.ToArray());
            var bitmap = new Bitmap(stream);
            _status.Text = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} px · {_entry?.Path}";
            return new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new TextBlock { Text = "Nie można odczytać obrazu: " + ex.Message, Foreground = _brushes.Error, Margin = new Thickness(16) };
        }
    }

    private Control FolderView(FolderPreview folder)
    {
        var known = folder.Items.Where(i => i.Size is not null).Sum(i => i.Size!.Value);
        var max = Math.Max(1, folder.Items.Max(i => i.Size ?? 0));
        var ordered = folder.Items.OrderByDescending(i => i.Size ?? -1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var pending = folder.Items.Count(i => i.Size is null);

        var list = new StackPanel { Spacing = 1, Margin = new Thickness(12, 4) };
        list.Children.Add(new TextBlock
        {
            Text = $"Razem: {FileViewportCanvas.FormatSize(known)}" + (folder.Complete ? string.Empty : $"  (liczenie… {pending} folderów)") +
                $"   ·   {folder.Files:N0} plików, {folder.Folders:N0} folderów",
            FontSize = _brushes.FontSize + 2,
            FontWeight = FontWeight.Bold,
            Foreground = _brushes.DirectoryForeground,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (var item in ordered.Take(MaxFolderRows))
        {
            var fraction = (item.Size ?? 0) / (double)max;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,Auto,3*"), Height = _brushes.RowHeight };
            row.Children.Add(new TextBlock
            {
                Text = (item.IsDirectory ? "▸ " : "  ") + item.Name,
                Foreground = item.IsDirectory ? _brushes.DirectoryForeground : _brushes.FileForeground,
                FontWeight = item.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var size = new TextBlock
            {
                Text = item.Size is { } bytes ? FileViewportCanvas.FormatSize(bytes) : "…",
                Foreground = _brushes.FileForeground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0),
                MinWidth = 80,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            var bar = new Border
            {
                Background = item.IsDirectory ? _brushes.Accent : _brushes.Skeleton,
                Height = Math.Max(4, _brushes.RowHeight * 0.45),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var barHost = new Border { Child = bar };
            barHost.SizeChanged += (_, e) => bar.Width = Math.Max(1, e.NewSize.Width * fraction);
            Grid.SetColumn(barHost, 2);
            row.Children.Add(barHost);
            list.Children.Add(row);
        }

        if (ordered.Count > MaxFolderRows)
        {
            list.Children.Add(new TextBlock { Text = $"… i {ordered.Count - MaxFolderRows} mniejszych", Foreground = _brushes.HiddenForeground, Margin = new Thickness(0, 6) });
        }

        _status.Text = folder.Complete ? $"Gotowe · {folder.Path}" : $"⏳ Liczenie rozmiarów… · {folder.Path}";
        return Scroll(list);
    }

    private static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
}
