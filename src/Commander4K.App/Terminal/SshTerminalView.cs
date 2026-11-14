using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
using Commander4K.Plugins.BuiltIn.Remote;
using Microsoft.Web.WebView2.Core;
using CoreModifiers = Commander4K.Core.Input.KeyModifiers;

namespace Commander4K.App.Terminal;

/// <summary>
/// Panel mode with an SSH terminal: xterm.js (in WebView2, served from a virtual host that is the only place the page
/// may go) draws what an <see cref="SshShell"/> sends, so colours, cursor addressing and full-screen programs (mc,
/// vim, htop) work as in any terminal. Every key belongs to the shell except Ctrl+Shift combos (the app's keymap;
/// C and V copy and paste) and Ctrl+Tab (the other panel). It cannot run headless; verify in a real window.
/// </summary>
public sealed class SshTerminalView : DockPanel, IPanelModeView
{
    private const string VirtualHost = "c4k-terminal.local";
    private const string XtermVersion = "6.0.0";
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkTab = 0x09;

    private readonly AppHost _app;
    private readonly string _path;
    private readonly Action<string, bool> _notify;
    private readonly Func<KeyStroke, bool> _runShortcut;
    private readonly WebView2Host _web;
    private readonly Border _toolbar;
    private readonly TextBlock _title;
    private readonly TextBlock _hint;
    private readonly Button _close;
    private readonly Border _message;
    private readonly TextBlock _messageText;
    private readonly CancellationTokenSource _closed = new();
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private SkinBrushes _brushes;
    private SshShell? _shell;
    private bool _pageReady;
    private bool _connectStarted;
    private bool _active;
    private bool _disposed;

    /// <param name="path">The <c>sftp://</c> folder the shell starts in; its server and login are the session's.</param>
    public SshTerminalView(AppHost app, string path, SkinBrushes brushes, Action<string, bool> notify, Func<KeyStroke, bool> runShortcut)
    {
        _app = app;
        _path = path;
        _brushes = brushes;
        _notify = notify;
        _runShortcut = runShortcut;
        Server = RemoteTransferVfsPlugin.RealmFor(path);
        Title = "SSH · " + Server[(Server.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimStart('@');

        _title = new TextBlock().Text(Title).VerticalAlignment(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis);
        _hint = new TextBlock().Text(Loc.T("Ctrl+Shift+C / V — kopiuj / wklej · Ctrl+Tab — drugi panel")).FontSize(11)
            .VerticalAlignment(VerticalAlignment.Center).Margin(12, 0);
        _close = Ds.ToolButton(brushes, "✕", Loc.T("Zamknij terminal"), () => CloseRequested?.Invoke());
        _close.VerticalAlignment = VerticalAlignment.Center;
        _toolbar = new Border().Child(new Grid().Cols("*,Auto,Auto").Children(_title.Col(0), _hint.Col(1), _close.Col(2)));
        _toolbar.PointerPressed += (_, _) => Activated?.Invoke();
        _messageText = new TextBlock().TextWrapping(TextWrapping.Wrap).MaxWidth(520);
        _message = new Border().IsVisible(false).Padding(24).Child(_messageText.VerticalAlignment(VerticalAlignment.Center));

        _web = new WebView2Host(Path.Combine(app.DataDirectory, "data", "webview2"));
        _web.Ready += controller => _ = StartPageAsync(controller);
        _web.Failed += error => ShowMessage(error is WebView2RuntimeNotFoundException
            ? Loc.T("Brak środowiska Microsoft Edge WebView2. Zainstaluj je: winget install Microsoft.EdgeWebView2Runtime")
            : Loc.T("Nie udało się uruchomić terminala: {0}", error.Message));

        SetDock(_toolbar, Avalonia.Controls.Dock.Top);
        Children.Add(_toolbar);
        Children.Add(new Grid().Children(_web, _message));
        ApplySkin(brushes);
    }

    /// <summary>The session key of the server (<c>sftp://user@host:port</c>): one terminal per server is reused.</summary>
    public string Server { get; }

    public string Title { get; }

    public string? TabIcon => ">_";

    public Control View => this;

    public bool IsActivePanel
    {
        set
        {
            if (value && !_active)
            {
                FocusTerminal(); // typing in the active panel goes to the shell
            }
            else if (!value && _active)
            {
                _web.ReleaseFocus(); // Ctrl+Tab: the keys must follow to the other panel
            }

            _active = value;
        }
    }

    public event Action? CloseRequested;

    public event Action? Activated;

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
        _title.Foreground = brushes.FileForeground;
        _title.FontFamily = brushes.FontFamily;
        _title.FontSize = brushes.FontSize;
        _hint.Foreground = brushes.MutedForeground;
        Ds.Style(_close, brushes, ButtonKind.Ghost);
        _message.Background = brushes.PanelBackground;
        _messageText.Foreground = brushes.MutedForeground;
        _messageText.FontSize = brushes.FontSize;
        SendTheme();
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return false;
        }

        FocusTerminal(); // Esc belongs to the shell (vim); the ✕ button or "exit" closes the terminal
        return true;
    }

    public void AppendText(string text)
    {
        Send(text);
        FocusTerminal();
    }

    public ValueTask DisposeModeAsync()
    {
        _disposed = true;
        _closed.Cancel();
        _input.Writer.TryComplete();
        return _shell?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private async Task StartPageAsync(CoreWebView2Controller controller)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var folder = await Task.Run(() => WebView2Host.ExtractAssets(
                Path.Combine(_app.DataDirectory, "data", "xterm", XtermVersion), "xterm/", "terminal.html", "xterm.js", "xterm.css", "addon-fit.js"));
            if (_disposed)
            {
                return;
            }

            var core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; // F5, Ctrl+R, Ctrl+F… are the shell's keys here
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(VirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnMessage;
            controller.GotFocus += (_, _) => Activated?.Invoke();
            controller.AcceleratorKeyPressed += OnAcceleratorKey;
            core.Navigate($"https://{VirtualHost}/terminal.html");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage(Loc.T("Nie udało się uruchomić terminala: {0}", ex.Message));
        }
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
        string? Text() => message.TryGetProperty("d", out var d) ? d.GetString() : null;
        switch (message.GetProperty("t").GetString())
        {
            case "ready":
                _pageReady = true;
                SendTheme();
                if (_active)
                {
                    FocusTerminal();
                }

                // The page is new again when the view was taken out of the window and put back: the session goes on
                // (the old screen is gone), only its size is sent again.
                var (cols, lines) = (message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                if (!_connectStarted)
                {
                    _connectStarted = true;
                    _ = ConnectAsync(cols, lines);
                }
                else if (_shell is { } running)
                {
                    _ = Task.Run(() => running.Resize(cols, lines));
                }

                break;
            case "in" when Text() is { } typed:
                Send(typed);
                break;
            case "bin" when Text() is { } binary:
                _input.Writer.TryWrite(Encoding.Latin1.GetBytes(binary)); // mouse reports: one byte per char
                break;
            case "size" when _shell is { } shell:
                var (columns, rows) = (message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                _ = Task.Run(() => shell.Resize(columns, rows));
                break;
            case "copy" when Text() is { Length: > 0 } selection:
                _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(selection);
                break;
            case "paste":
                _ = PasteAsync();
                break;
        }
    }

    private async Task ConnectAsync(int columns, int rows)
    {
        Print(Loc.T("Łączenie z {0}…", Title[6..]) + "\r\n");
        SshShell shell;
        try
        {
            // The login lookup reads ~/.ssh/config: off the UI thread with the connection.
            shell = await Task.Run(() => _app.Remote.OpenShellAsync(_path, columns, rows, _closed.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Print("\x1b[31m" + Loc.T("Nie udało się połączyć: {0}", ex.Message) + "\x1b[0m\r\n");
            return;
        }

        if (_disposed)
        {
            await shell.DisposeAsync();
            return;
        }

        _shell = shell;
        _ = Task.Run(() => PumpInputAsync(shell));
        new Thread(() => PumpOutput(shell)) { IsBackground = true, Name = "SSH terminal" }.Start();

        // The shell starts in the home folder: go to the folder on screen. The leading space keeps it out of bash's history.
        var slash = _path.IndexOf('/', _path.IndexOf("://", StringComparison.Ordinal) + 3);
        if (slash >= 0 && _path[slash..] is { Length: > 1 } folder)
        {
            Send(" cd -- '" + folder.Replace("'", @"'\''", StringComparison.Ordinal) + "'\r");
        }
    }

    /// <summary>Keys go to the server one after another, off the UI thread (a write waits for the network).</summary>
    private async Task PumpInputAsync(SshShell shell)
    {
        try
        {
            await foreach (var data in _input.Reader.ReadAllAsync(_closed.Token))
            {
                shell.Write(data);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The blocking read on its own thread; each chunk waits for the UI thread, so a flood cannot pile up.</summary>
    private void PumpOutput(SshShell shell)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            for (int read; (read = shell.Read(buffer)) > 0;)
            {
                var chunk = Convert.ToBase64String(buffer, 0, read);
                Dispatcher.UIThread.Invoke(() => Post(new JsonObject { ["t"] = "out", ["d"] = chunk }));
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    _notify(Loc.T("Sesja SSH zakończona."), false);
                    CloseRequested?.Invoke();
                }
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // The window closed under the terminal.
        }
    }

    private void Send(string text) => _input.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    private void Print(string text) => Post(new JsonObject { ["t"] = "out", ["d"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });

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
                // The browser closed under the terminal (the view left the window, the app is closing).
            }
        }
    }

    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
        {
            Post(new JsonObject { ["t"] = "paste", ["d"] = text });
        }
    }

    private void FocusTerminal()
    {
        _web.FocusPage();
        Post(new JsonObject { ["t"] = "focus" });
    }

    private void SendTheme()
    {
        static string Hex(IBrush brush) => brush is ISolidColorBrush solid ? $"#{solid.Color.R:x2}{solid.Color.G:x2}{solid.Color.B:x2}" : "#808080";
        Post(new JsonObject
        {
            ["t"] = "theme",
            ["fontSize"] = Math.Round(_brushes.FontSize),
            ["theme"] = new JsonObject
            {
                ["background"] = Hex(_brushes.PanelBackground),
                ["foreground"] = Hex(_brushes.FileForeground),
                ["cursor"] = Hex(_brushes.Accent),
                ["selectionBackground"] = Hex(_brushes.Accent) + "66",
            },
        });
    }

    private void ShowMessage(string text)
    {
        _messageText.Text = text;
        _message.IsVisible = true;
        _web.IsVisible = false; // the native window would cover the message otherwise
    }

    /// <summary>Keys pressed in the terminal: only Ctrl+Shift combos and Ctrl+Tab leave it. Ctrl+Alt is AltGr (ą, ę…) and stays.</summary>
    private void OnAcceleratorKey(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)
            || !WebView2Host.IsKeyDown(VkControl) || WebView2Host.IsKeyDown(VkMenu))
        {
            return;
        }

        var vk = (int)e.VirtualKey;
        var shift = WebView2Host.IsKeyDown(VkShift);
        if (vk == VkTab && !shift)
        {
            e.Handled = _runShortcut(new KeyStroke(nameof(Key.Tab), CoreModifiers.Ctrl)) || _runShortcut(new KeyStroke(nameof(Key.Tab), CoreModifiers.None));
        }
        else if (shift && vk == 'C')
        {
            e.Handled = true;
            Post(new JsonObject { ["t"] = "copy" });
        }
        else if (shift && vk == 'V')
        {
            e.Handled = true;
            _ = PasteAsync();
        }
        else if (shift && vk is >= 'A' and <= 'Z')
        {
            e.Handled = _runShortcut(new KeyStroke(((char)vk).ToString(), CoreModifiers.Ctrl | CoreModifiers.Shift));
        }
    }
}
