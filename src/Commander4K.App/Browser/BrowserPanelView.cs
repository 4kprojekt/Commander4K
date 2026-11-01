using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Ai;
using Commander4K.App.Composition;
using Commander4K.App.Panels;
using Commander4K.App.Theming;
using Commander4K.Core.Input;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;
using Commander4K.Core.Web;
using Commander4K.PluginSdk;
using Microsoft.Web.WebView2.Core;
using CoreModifiers = Commander4K.Core.Input.KeyModifiers;

namespace Commander4K.App.Browser;

/// <summary>Identity of the built-in browser in the plugin manager and the audit log (every top-level navigation is logged).</summary>
public sealed class BrowserPlugin : IPreviewPlugin
{
    public const string PluginId = "builtin.browser";

    public static BrowserPlugin Instance { get; } = new();

    public string Id => PluginId;

    public string DisplayName => "Browser";

    public PluginManifest Manifest { get; } = new(PluginId, Loc.T("Przeglądarka"), "1.0.0", "4Kprojekt",
        Loc.T("Panel przeglądarki (Microsoft Edge WebView2): strony, wyszukiwarki i asystenci AI. Logowania są zapamiętywane w profilu aplikacji."),
        [PluginPermissions.Network]);

    public int Priority => int.MaxValue;

    public bool CanPreview(VfsEntry entry) =>
        Uri.TryCreate(entry.Path, UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

    public Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken) =>
        Task.FromResult<PreviewContent>(new BrowserPagePreview(entry.Path));
}

public sealed record BrowserPagePreview(string Url) : PreviewContent;

/// <summary>
/// Panel mode with a WebView2 browser: address bar that also searches, back/forward/reload, a search-engine picker
/// and "ask AI". Plugins open pages here through <see cref="IPluginHost.OpenUrlAsync"/> and
/// <see cref="IPluginHost.SearchWebAsync"/>. App shortcuts (F-keys, Ctrl+Shift/Alt combos) keep working while the
/// page has focus: WebView2 reports them as accelerator keys and they are passed to the keymap.
/// </summary>
public sealed class BrowserPanelView : DockPanel, IPanelModeView, ITitledModeView
{
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkF1 = 0x70, VkF5 = 0x74, VkF6 = 0x75, VkF12 = 0x7B, VkF24 = 0x87;

    private readonly AppHost _app;
    private readonly IPluginHost _host;
    private readonly Action<string, bool> _notify;
    private readonly Func<KeyStroke, bool> _runShortcut;
    private readonly WebView2Host _web;
    private readonly TextBox _address;
    private readonly ComboBox _engine;
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _reload;
    private readonly Button _external;
    private readonly Button _close;
    private readonly Border _statusBar;
    private readonly Border _toolbar;
    private readonly Border _message;
    private readonly TextBlock _messageText;
    private readonly TextBlock _status;
    private string? _pending;
    private bool _disposed;

    public BrowserPanelView(AppHost app, IPluginHost host, string? url, SkinBrushes brushes, Action<string, bool> notify, Func<KeyStroke, bool> runShortcut)
    {
        _app = app;
        _host = host;
        _notify = notify;
        _runShortcut = runShortcut;
        _pending = url ?? "https://www.google.com/";

        _back = Ds.ToolButton(brushes, "←", Loc.T("Wstecz (Alt+←)"), () => Core?.GoBack());
        _forward = Ds.ToolButton(brushes, "→", Loc.T("Dalej (Alt+→)"), () => Core?.GoForward());
        _reload = Ds.ToolButton(brushes, "⟳", Loc.T("Odśwież (F5)"), () => Core?.Reload());
        _external = Ds.ToolButton(brushes, "↗", Loc.T("Otwórz w przeglądarce systemowej"), OpenExternally);
        _close = Ds.ToolButton(brushes, "✕", Loc.T("Zamknij przeglądarkę (Esc)"), () => CloseRequested?.Invoke());
        _address = new TextBox().PlaceholderText(Loc.T("Adres albo szukane słowa (Ctrl+L)")).Padding(10, 0).MinHeight(26).Height(26).VerticalContentAlignment(VerticalAlignment.Center);
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                NavigateFromAddress();
                e.Handled = true;
            }
        };
        _engine = new ComboBox().Width(130).Height(26).Margin(6, 0, 0, 0).VerticalContentAlignment(VerticalAlignment.Center).ItemsSource(SearchEngines.All.Select(e => e.Name).ToList());
        _engine.SelectedIndex = IndexOf(app.Settings.SearchEngine);
        _engine.SelectionChanged += async (_, _) =>
        {
            if (_engine.SelectedIndex >= 0 && SearchEngines.All[_engine.SelectedIndex].Id != _app.Settings.SearchEngine)
            {
                _app.Settings.SearchEngine = SearchEngines.All[_engine.SelectedIndex].Id;
                await _app.SaveSettingsAsync();
            }
        };
        ToolTip.SetTip(_engine, Loc.T("Wyszukiwarka dla tekstu wpisanego w pasek adresu"));

        var toolbar = new Grid().Cols("Auto,Auto,Auto,*,Auto,Auto,Auto").Children(
            _back.Col(0),
            _forward.Col(1),
            _reload.Col(2),
            _address.Col(3).Margin(6, 0, 0, 0),
            _engine.Col(4),
            _external.Col(5).Margin(6, 0, 0, 0),
            _close.Col(6).Margin(2, 0, 0, 0));
        foreach (var button in new[] { _back, _forward, _reload }.Concat([_external, _close]))
        {
            button.VerticalAlignment = VerticalAlignment.Center;
        }

        _address.VerticalAlignment = _engine.VerticalAlignment = VerticalAlignment.Center;
        _toolbar = new Border().Child(toolbar);
        _toolbar.PointerPressed += (_, _) => Activated?.Invoke();

        _status = new TextBlock().FontSize(11).VerticalAlignment(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis);
        _statusBar = new Border().Child(_status);
        _messageText = new TextBlock().TextWrapping(TextWrapping.Wrap).MaxWidth(520);
        _message = new Border().IsVisible(false).Padding(24).Child(new StackPanel().Spacing(12).VerticalAlignment(VerticalAlignment.Center).Children(
            _messageText,
            new Button().Content(Loc.T("Otwórz w przeglądarce systemowej")).OnClick(_ => OpenExternally())));

        _web = new WebView2Host(Path.Combine(app.DataDirectory, "data", "webview2"));
        _web.Ready += OnReady;
        _web.Failed += OnFailed;

        SetDock(_toolbar, Avalonia.Controls.Dock.Top);
        SetDock(_statusBar, Avalonia.Controls.Dock.Bottom);
        Children.Add(_toolbar);
        Children.Add(_statusBar);
        Children.Add(new Grid().Children(_web, _message));

        if (WebView2Host.RuntimeVersion() is null)
        {
            ShowMessage(Loc.T("Brak środowiska Microsoft Edge WebView2, którego używa wbudowana przeglądarka.\n\nZainstaluj je poleceniem:\n    winget install Microsoft.EdgeWebView2Runtime\nalbo ze strony Microsoftu, a potem otwórz przeglądarkę ponownie."));
        }

        ApplySkin(brushes);
    }

    public string Title => "🌐 " + (Core?.DocumentTitle is { Length: > 0 } title ? title : CurrentUrl ?? Loc.T("Przeglądarka"));

    public Control View => this;

    public bool IsActivePanel { private get; set; }

    /// <summary>The page shown (or about to be shown); saved views store it.</summary>
    public string? CurrentUrl => Core?.Source is { Length: > 0 } source && source != "about:blank" ? source : _pending;

    private CoreWebView2? Core => _web.Controller?.CoreWebView2;

    /// <summary>For tests and tooling (screenshots of the page, scripted checks).</summary>
    public WebView2Host WebHost => _web;

    public event Action? CloseRequested;

    public event Action? Activated;

    public event Action? TitleChanged;

    public void Navigate(string url)
    {
        _address.Text = url;
        if (Core is { } core)
        {
            core.Navigate(url);
        }
        else
        {
            _pending = url;
        }
    }

    /// <summary>
    /// Searches with <paramref name="engine"/>. Assistants that take no query in the address (Gemini), and prompts too
    /// long for one, go to the clipboard with the chat opened (see <see cref="AskWithPromptAsync"/>).
    /// </summary>
    public async Task SearchAsync(string query, SearchEngine engine)
    {
        if (engine.IsAssistant && (!engine.AcceptsQuery || query.Length > SearchEngine.MaxQueryLength))
        {
            await AskWithPromptAsync(query, engine);
            return;
        }

        Navigate(engine.BuildUrl(query));
    }

    /// <summary>
    /// Opens the assistant's chat and puts <paramref name="prompt"/> on the clipboard; the user pastes it (Ctrl+V) on the page.
    /// </summary>
    public async Task AskWithPromptAsync(string prompt, SearchEngine engine)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(prompt);
        }

        var onAssistant = CurrentUrl is { } current && Uri.TryCreate(current, UriKind.Absolute, out var page)
            && Uri.TryCreate(engine.ChatUrl, UriKind.Absolute, out var chat) && page.Host == chat.Host;
        if (!onAssistant)
        {
            Navigate(engine.ChatUrl);
        }

        _notify(Loc.T("Pytanie jest w schowku — wklej je na stronie (Ctrl+V)."), false);
    }

    private SkinBrushes? _brushes;

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        var modern = brushes.IsModern;
        Background = brushes.PanelBackground;

        // Design system: toolbar like the path bar (32 px, header colour, rule below), status line like the panel status (26 px, rule above).
        _toolbar.Background = modern ? brushes.HeaderBackground : brushes.StatusBackground;
        _toolbar.BorderBrush = brushes.PanelBorder;
        _toolbar.BorderThickness = new Thickness(0, 0, 0, modern ? 1 : 0);
        _toolbar.Padding = new Thickness(modern ? 8 : 4, modern ? 4 : 4);
        _toolbar.MinHeight = modern ? 36 : 0;
        foreach (var button in new[] { _back, _forward, _reload, _external, _close })
        {
            Ds.Style(button, brushes, ButtonKind.Ghost);
        }

        _address.CornerRadius = new CornerRadius(modern ? 6 : 0);
        _address.Background = brushes.PanelBackground;
        _address.FontFamily = brushes.FontFamily;
        _address.FontSize = brushes.FontSize;
        _engine.CornerRadius = new CornerRadius(modern ? 6 : 0);
        _engine.FontSize = Math.Max(12, brushes.FontSize - 1);

        _statusBar.Background = brushes.StatusBackground;
        _statusBar.BorderBrush = brushes.PanelBorder;
        _statusBar.BorderThickness = new Thickness(0, modern ? 1 : 0, 0, 0);
        _statusBar.Height = modern ? 26 : double.NaN;
        _statusBar.Padding = modern ? new Thickness(12, 0) : new Thickness(6, 2);
        _status.Foreground = brushes.StatusForeground;
        _status.FontFamily = brushes.FontFamily;
        _status.FontSize = modern ? Math.Max(11, brushes.FontSize - 1) : 11;

        _message.Background = brushes.PanelBackground;
        _messageText.Foreground = brushes.MutedForeground;
        _messageText.FontSize = brushes.FontSize;
    }

    public bool HandleKey(KeyEventArgs e)
    {
        var ctrl = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) != 0;
        var alt = (e.KeyModifiers & Avalonia.Input.KeyModifiers.Alt) != 0;
        switch (e.Key)
        {
            case Key.L when ctrl:
            case Key.F6:
                FocusAddress();
                return true;
            case Key.F5:
                Core?.Reload();
                return true;
            case Key.Left when alt:
                Core?.GoBack();
                return true;
            case Key.Right when alt:
                Core?.GoForward();
                return true;
            case Key.Escape when _address.IsFocused:
                _address.Text = CurrentUrl;
                _web.FocusPage();
                return true;
            case Key.Escape:
                CloseRequested?.Invoke();
                return true;
            default:
                return false;
        }
    }

    public void AppendText(string text)
    {
        _address.Text = text;
        FocusAddress();
        _address.CaretIndex = _address.Text?.Length ?? 0;
    }

    public ValueTask DisposeModeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private void OnReady(CoreWebView2Controller controller)
    {
        if (_disposed)
        {
            return;
        }

        var core = controller.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) =>
        {
            _status.Text = Loc.T("Ładowanie… ") + e.Uri;
            if (!e.IsRedirected)
            {
                _host.Log("navigate " + e.Uri);
            }
        };
        core.NavigationCompleted += (_, e) =>
        {
            _status.Text = e.IsSuccess ? string.Empty : Loc.T("Nie udało się otworzyć strony ({0}).", e.WebErrorStatus);
            UpdateButtons();
        };
        core.SourceChanged += (_, _) =>
        {
            if (!_address.IsFocused)
            {
                _address.Text = core.Source;
            }

            UpdateButtons();
            TitleChanged?.Invoke();
        };
        core.DocumentTitleChanged += (_, _) => TitleChanged?.Invoke();
        core.HistoryChanged += (_, _) => UpdateButtons();

        // target=_blank links open here; real pop-ups (sign-in windows with a size or position) keep their own window.
        core.NewWindowRequested += (_, e) =>
        {
            if (!e.WindowFeatures.HasPosition && !e.WindowFeatures.HasSize)
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            }
        };
        controller.GotFocus += (_, _) => Activated?.Invoke();
        controller.AcceleratorKeyPressed += OnAcceleratorKey;

        var url = _pending ?? "about:blank";
        _pending = null;
        _address.Text = url;
        core.Navigate(url);
    }

    private void OnFailed(Exception error) =>
        ShowMessage(error is WebView2RuntimeNotFoundException
            ? Loc.T("Brak środowiska Microsoft Edge WebView2. Zainstaluj je: winget install Microsoft.EdgeWebView2Runtime")
            : Loc.T("Nie udało się uruchomić przeglądarki: ") + error.Message);

    private void ShowMessage(string text)
    {
        _messageText.Text = text;
        _message.IsVisible = true;
        _web.IsVisible = false; // The native window would cover the message otherwise.
    }

    /// <summary>Keys pressed inside the page: browser shortcuts stay with the page, app shortcuts go to the keymap.</summary>
    private void OnAcceleratorKey(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown))
        {
            return;
        }

        var vk = (int)e.VirtualKey;
        var ctrl = WebView2Host.IsKeyDown(VkControl);
        var alt = WebView2Host.IsKeyDown(VkMenu);
        var shift = WebView2Host.IsKeyDown(VkShift);
        if ((ctrl && vk == 'L') || vk == VkF6)
        {
            e.Handled = true;
            FocusAddress();
            return;
        }

        var functionKey = vk is >= VkF1 and <= VkF24 && vk is not (VkF5 or VkF12); // F5 reloads, F12 opens DevTools.
        var appCombo = ctrl && (shift || alt || vk is 'B' or 'Q' or 'D' or 0xBC);
        if (!(functionKey || appCombo) || KeyName(vk) is not { } key)
        {
            return;
        }

        var modifiers = (ctrl ? CoreModifiers.Ctrl : 0) | (alt ? CoreModifiers.Alt : 0) | (shift ? CoreModifiers.Shift : 0);
        e.Handled = _runShortcut(new KeyStroke(key, modifiers));
    }

    /// <summary>Avalonia <see cref="Key"/> names for the virtual keys forwarded to the keymap.</summary>
    private static string? KeyName(int vk) => vk switch
    {
        >= VkF1 and <= VkF24 => "F" + (vk - VkF1 + 1),
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => "D" + (char)vk,
        0xBC => nameof(Key.OemComma),
        _ => null,
    };

    private void NavigateFromAddress()
    {
        var engine = SearchEngines.Get(_app.Settings.SearchEngine);
        if (SearchEngines.ResolveAddress(_address.Text, engine) is { } url)
        {
            Navigate(url);
            _web.FocusPage();
        }
    }

    private void OpenExternally()
    {
        if (CurrentUrl is { } url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.AboutDialog.OpenUrl(url);
        }
    }

    private void FocusAddress()
    {
        _address.Focus();
        _address.SelectAll();
    }

    private void UpdateButtons()
    {
        _back.IsEnabled = Core?.CanGoBack == true;
        _forward.IsEnabled = Core?.CanGoForward == true;
    }

    private static int IndexOf(string id) =>
        Math.Max(0, SearchEngines.All.ToList().FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)));
}
