using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Browser;
using Commander4K.App.Composition;
using Commander4K.App.Panels;
using Commander4K.App.Theming;
using Commander4K.Core.Input;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Microsoft.Web.WebView2.Core;
using CoreModifiers = Commander4K.Core.Input.KeyModifiers;

namespace Commander4K.App.Editor;

/// <summary>
/// Panel mode that edits one <c>.c4k</c> file in Monaco (WebView2, served from a virtual host that is the only place
/// the page may go). The file is read once; the text is kept here (refreshed from the page's replies) so a page
/// created again when the view returns to the window gets the current text. It cannot run headless; verify in a real window.
/// </summary>
public sealed class C4kEditorView : DockPanel, IPanelModeView, ITitledModeView
{
    private const string VirtualHost = "c4k-editor.local";
    private const string MonacoVersion = "0.52.2";
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkTab = 0x09, VkF2 = 0x71, VkF11 = 0x7A;

    private readonly AppHost _app;
    private readonly string _schemaJson;
    private readonly Action<string, bool> _notify;
    private readonly Func<KeyStroke, bool> _runShortcut;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly WebView2Host _web;
    private readonly Border _toolbar;
    private readonly TextBlock _name;
    private readonly TextBlock _status;
    private readonly Button _format;
    private readonly Button _save;
    private readonly Button _close;
    private readonly Border _message;
    private readonly TextBlock _messageText;
    private readonly CancellationTokenSource _closed = new();
    private readonly Dictionary<int, TaskCompletionSource<string>> _pending = [];
    private readonly DispatcherTimer _debounce;
    private readonly string _fileName;
    private SkinBrushes _brushes;
    private string? _text;
    private string? _onDisk;
    private bool _readOnly;
    private bool _pageReady;
    private bool _active;
    private bool _dirty;
    private bool _discard;
    private bool _disposed;
    private bool _saving;
    private bool _closing;
    private int _problems;
    private int _nextId;

    public C4kEditorView(AppHost app, string filePath, string schemaJson, SkinBrushes brushes,
        Action<string, bool> notify, Func<KeyStroke, bool> runShortcut, Func<string, string, Task<bool>> confirm)
    {
        _app = app;
        FilePath = filePath;
        _schemaJson = schemaJson;
        _brushes = brushes;
        _notify = notify;
        _runShortcut = runShortcut;
        _confirm = confirm;
        _fileName = Path.GetFileName(filePath.TrimEnd('/', '\\'));

        _name = new TextBlock().Text(_fileName).VerticalAlignment(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis);
        _status = new TextBlock().FontSize(11).VerticalAlignment(VerticalAlignment.Center).Margin(12, 0);
        _format = Ds.Button(brushes, Loc.T("Formatuj"), () => Post(new JsonObject { ["t"] = "format" }), ButtonKind.Toolbar);
        _save = Ds.Button(brushes, Loc.T("Zapisz (Ctrl+S)"), () => _ = SaveFromPageAsync(), ButtonKind.Primary, enabled: false);
        _close = Ds.ToolButton(brushes, "✕", Loc.T("Zamknij edytor"), () => _ = RequestCloseAsync());
        foreach (var button in new[] { _format, _save, _close })
        {
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Margin = new Thickness(4, 0, 0, 0);
        }

        _toolbar = new Border().Child(new Grid().Cols("*,Auto,Auto,Auto,Auto")
            .Children(_name.Col(0), _status.Col(1), _format.Col(2), _save.Col(3), _close.Col(4)));
        _toolbar.PointerPressed += (_, _) => Activated?.Invoke();
        _messageText = new TextBlock().TextWrapping(TextWrapping.Wrap).MaxWidth(520);
        _message = new Border().IsVisible(false).Padding(24).Child(_messageText.VerticalAlignment(VerticalAlignment.Center));

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RequestTextAsync(); // stores the reply in _text
        };

        _web = new WebView2Host(Path.Combine(app.DataDirectory, "data", "webview2"));
        _web.Ready += controller => _ = StartPageAsync(controller);
        _web.Failed += error => ShowMessage(error is WebView2RuntimeNotFoundException
            ? Loc.T("Brak środowiska Microsoft Edge WebView2. Zainstaluj je: winget install Microsoft.EdgeWebView2Runtime")
            : Loc.T("Nie udało się uruchomić edytora: {0}", error.Message));

        SetDock(_toolbar, Avalonia.Controls.Dock.Top);
        Children.Add(_toolbar);
        Children.Add(new Grid().Children(_web, _message));
        ApplySkin(brushes);
        UpdateStatus();
    }

    public string FilePath { get; }

    /// <summary>Unsaved edits: the caller must not replace this mode without asking.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Raised on the UI thread after a successful save, with the file path.</summary>
    public event Action<string>? Saved;

    public string Title => _dirty ? _fileName + " ●" : _fileName;

    public string? TabIcon => "{ }";

    public Control View => this;

    public bool IsActivePanel
    {
        set
        {
            if (value && !_active)
            {
                FocusEditor();
            }
            else if (!value && _active)
            {
                _web.ReleaseFocus();
            }

            _active = value;
        }
    }

    public event Action? CloseRequested;

    public event Action? Activated;

    public event Action? TitleChanged;

    private CoreWebView2? Core => _web.Controller?.CoreWebView2;

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        var modern = brushes.IsModern;
        Background = brushes.PanelBackground;
        _toolbar.Background = modern ? brushes.HeaderBackground : brushes.StatusBackground;
        _toolbar.BorderBrush = brushes.PanelBorder;
        _toolbar.BorderThickness = new Thickness(0, 0, 0, modern ? 1 : 0);
        _toolbar.Padding = new Thickness(modern ? 12 : 6, 4, modern ? 8 : 4, 4);
        _toolbar.MinHeight = modern ? 36 : 0;
        _name.Foreground = brushes.FileForeground;
        _name.FontFamily = brushes.FontFamily;
        _name.FontSize = brushes.FontSize;
        Ds.Style(_format, brushes, ButtonKind.Toolbar);
        Ds.Style(_save, brushes, ButtonKind.Primary);
        Ds.Style(_close, brushes, ButtonKind.Ghost);
        _message.Background = brushes.PanelBackground;
        _messageText.Foreground = brushes.MutedForeground;
        _messageText.FontSize = brushes.FontSize;
        UpdateStatus();
        SendTheme();
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return false;
        }

        _ = RequestCloseAsync();
        return true;
    }

    public void AppendText(string text)
    {
    }

    public ValueTask DisposeModeAsync()
    {
        // Replaced or closed from outside (another mode, the tab, the app) with unsaved text: nobody was asked, so it is
        // saved rather than lost. Only the editor's own close asks, and may discard.
        if (_dirty && !_discard && !_readOnly && !_disposed && _text is { } unsaved && unsaved != _onDisk)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var stream = await _app.Router.OpenWriteAsync(FilePath, overwrite: true, CancellationToken.None);
                    await stream.WriteAsync(new UTF8Encoding(false).GetBytes(unsaved));
                    Dispatcher.UIThread.Post(() => _notify(Loc.T("Zapisano zmiany w {0}.", _fileName), false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Dispatcher.UIThread.Post(() => _notify(Loc.T("Nie udało się zapisać pliku: {0}", ex.Message), true));
                }
            });
        }

        _disposed = true;
        _debounce.Stop();
        _closed.Cancel();
        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetCanceled();
        }

        return ValueTask.CompletedTask;
    }

    private async Task StartPageAsync(CoreWebView2Controller controller)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var folder = await Task.Run(ExtractMonaco);
            if (_disposed)
            {
                return;
            }

            var core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(VirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnMessage;
            controller.GotFocus += (_, _) => Activated?.Invoke();
            controller.AcceleratorKeyPressed += OnAcceleratorKey;
            _pageReady = false; // a page created again must say 'ready' before it gets messages
            core.Navigate($"https://{VirtualHost}/editor.html");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage(Loc.T("Nie udało się uruchomić edytora: {0}", ex.Message));
        }
    }

    /// <summary>Writes the embedded <c>monaco/…</c> resources under data/monaco/&lt;version&gt;, keeping the sub-folders.</summary>
    private string ExtractMonaco()
    {
        var folder = Path.Combine(_app.DataDirectory, "data", "monaco", MonacoVersion);
        var assembly = typeof(C4kEditorView).Assembly;
        const string prefix = "monaco/";
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var relative = resource[prefix.Length..];
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var file = File.Create(target);
            stream.CopyTo(file);
        }

        return folder;
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            Handle(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Not one of the page's messages: nothing to do with it.
        }
    }

    private void Handle(JsonElement message)
    {
        switch (message.GetProperty("t").GetString())
        {
            case "ready":
                _pageReady = true;
                _ = OpenAsync();
                break;
            case "change":
                SetDirty(message.TryGetProperty("dirty", out var dirty) && dirty.GetBoolean());
                _debounce.Stop();
                _debounce.Start();
                break;
            case "problems":
                _problems = message.GetProperty("count").GetInt32();
                UpdateStatus();
                break;
            case "save":
                _ = SaveAsync(message.GetProperty("text").GetString() ?? string.Empty);
                break;
            case "close":
                _ = RequestCloseAsync();
                break;
            case "text":
                var text = message.GetProperty("text").GetString() ?? string.Empty;
                _text = text;
                if (_pending.Remove(message.GetProperty("id").GetInt32(), out var waiting))
                {
                    waiting.TrySetResult(text);
                }

                break;
        }
    }

    /// <summary>The first 'ready' reads the file; a re-created page gets the text kept in memory.</summary>
    private async Task OpenAsync()
    {
        if (_text is null)
        {
            try
            {
                var (text, readOnly) = await Task.Run(ReadFileAsync);
                _text = _onDisk = text;
                _readOnly = readOnly;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _notify(Loc.T("Nie udało się odczytać pliku: {0}", ex.Message), true);
                ShowMessage(Loc.T("Nie udało się odczytać pliku: {0}", ex.Message));
                return;
            }
        }

        if (_disposed)
        {
            return;
        }

        UpdateStatus();
        var schema = JsonNode.Parse(_schemaJson);
        Post(new JsonObject
        {
            ["t"] = "open",
            ["text"] = _text,
            ["uri"] = "file:///" + Uri.EscapeDataString(_fileName),
            ["schema"] = schema,
            ["readOnly"] = _readOnly,
            ["theme"] = ThemeJson(),
            ["fontSize"] = Math.Round(_brushes.FontSize),
            ["fontFamily"] = _brushes.FontFamily.Name,
        });
        if (_active)
        {
            FocusEditor();
        }
    }

    private async Task<(string Text, bool ReadOnly)> ReadFileAsync()
    {
        await using var stream = await _app.Router.OpenReadAsync(FilePath, _closed.Token);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var text = await reader.ReadToEndAsync(_closed.Token);
        var readOnly = !_app.Router.TryResolve(FilePath, out var provider) || (provider.Capabilities & VfsCapabilities.Write) == 0;
        return (text, readOnly);
    }

    private async Task SaveFromPageAsync()
    {
        if (await RequestTextAsync() is { } text)
        {
            await SaveAsync(text);
        }
    }

    private async Task SaveAsync(string text)
    {
        if (_saving || _readOnly || _disposed)
        {
            return;
        }

        _text = text;
        if (text == _onDisk)
        {
            SetDirty(false);
            Post(new JsonObject { ["t"] = "saved", ["text"] = text });
            return;
        }

        _saving = true;
        try
        {
            await Task.Run(async () =>
            {
                await using var stream = await _app.Router.OpenWriteAsync(FilePath, overwrite: true, CancellationToken.None);
                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text));
            });
            _onDisk = text;
            SetDirty(false);
            Post(new JsonObject { ["t"] = "saved", ["text"] = text });
            Saved?.Invoke(FilePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notify(Loc.T("Nie udało się zapisać pliku: {0}", ex.Message), true);
            _status.Text = Loc.T("Nie udało się zapisać pliku: {0}", ex.Message);
            _status.Foreground = _brushes.Error;
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>Asks the page for its text; stores it in <c>_text</c>. <c>null</c> when the page is gone or silent for 5 s.</summary>
    private async Task<string?> RequestTextAsync()
    {
        if (!_pageReady || _disposed)
        {
            return _text;
        }

        var id = ++_nextId;
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        Post(new JsonObject { ["t"] = "get", ["id"] = id });
        try
        {
            return await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _pending.Remove(id);
            return null;
        }
    }

    private async Task RequestCloseAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (_dirty && !await _confirm(Loc.T("Zamknąć edytor?"), Loc.T("Zmiany w {0} nie zostały zapisane.", _fileName)))
            {
                FocusEditor();
                return;
            }

            _discard = true;
            CloseRequested?.Invoke();
        }
        finally
        {
            _closing = false;
        }
    }

    private void SetDirty(bool dirty)
    {
        if (_dirty == dirty)
        {
            return;
        }

        _dirty = dirty;
        UpdateStatus();
        TitleChanged?.Invoke();
    }

    private void UpdateStatus()
    {
        _save.IsEnabled = _dirty && !_readOnly;
        _status.Foreground = _brushes.MutedForeground;
        _status.Text = _problems > 0 ? Loc.T("Problemy: {0}", _problems)
            : _dirty ? Loc.T("Niezapisane zmiany")
            : Loc.T("Zapisano");
    }

    private void Post(JsonObject message)
    {
        if (_pageReady && !_disposed && Core is { } core)
        {
            try
            {
                core.PostWebMessageAsJson(message.ToJsonString());
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
            {
                // The browser closed under the editor (the view left the window, the app is closing).
            }
        }
    }

    private void FocusEditor()
    {
        _web.FocusPage();
        Post(new JsonObject { ["t"] = "focus" });
    }

    private JsonObject ThemeJson()
    {
        static string Hex(IBrush brush) => brush is ISolidColorBrush solid ? $"#{solid.Color.R:x2}{solid.Color.G:x2}{solid.Color.B:x2}" : "#808080";
        var background = _brushes.PanelBackground.Color;
        var accent = Hex(_brushes.Accent);
        return new JsonObject
        {
            ["dark"] = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255 < 0.5,
            ["background"] = Hex(_brushes.PanelBackground),
            ["foreground"] = Hex(_brushes.FileForeground),
            ["accent"] = accent,
            ["muted"] = Hex(_brushes.MutedForeground),
            ["surface"] = Hex(_brushes.Surface2),
            ["selected"] = Hex(_brushes.SelectedBackground ?? _brushes.Surface2),
            ["selection"] = accent + "55",
        };
    }

    private void SendTheme() => Post(new JsonObject
    {
        ["t"] = "theme",
        ["theme"] = ThemeJson(),
        ["fontSize"] = Math.Round(_brushes.FontSize),
        ["fontFamily"] = _brushes.FontFamily.Name,
    });

    private void ShowMessage(string text)
    {
        _messageText.Text = text;
        _message.IsVisible = true;
        _web.IsVisible = false; // the native window would cover the message otherwise
    }

    /// <summary>Only Ctrl+Shift letters, Ctrl+Tab and F2…F11 leave the editor. Ctrl+Alt is AltGr (ą, ę…) and stays.</summary>
    private void OnAcceleratorKey(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown))
        {
            return;
        }

        var ctrl = WebView2Host.IsKeyDown(VkControl);
        var alt = WebView2Host.IsKeyDown(VkMenu);
        if (ctrl && alt)
        {
            return;
        }

        var vk = (int)e.VirtualKey;
        var shift = WebView2Host.IsKeyDown(VkShift);
        var modifiers = (ctrl ? CoreModifiers.Ctrl : CoreModifiers.None) | (shift ? CoreModifiers.Shift : CoreModifiers.None) | (alt ? CoreModifiers.Alt : CoreModifiers.None);
        if (vk is >= VkF2 and <= VkF11)
        {
            e.Handled = _runShortcut(new KeyStroke("F" + (vk - VkF2 + 2), modifiers));
        }
        else if (ctrl && vk == VkTab && !shift)
        {
            e.Handled = _runShortcut(new KeyStroke(nameof(Key.Tab), CoreModifiers.Ctrl)) || _runShortcut(new KeyStroke(nameof(Key.Tab), CoreModifiers.None));
        }
        else if (ctrl && shift && vk is >= 'A' and <= 'Z')
        {
            e.Handled = _runShortcut(new KeyStroke(((char)vk).ToString(), CoreModifiers.Ctrl | CoreModifiers.Shift));
        }
    }
}
