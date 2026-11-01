using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Dialogs;
using Commander4K.App.Preview;
using Commander4K.App.Theming;
using Commander4K.Core.Ai;
using Commander4K.Core.Localization;
using Commander4K.Core.Plugins;

namespace Commander4K.App.Ai;

/// <summary>
/// Chat with whichever signed-in provider answers (<see cref="AiRouter"/> rotates past limits). The model gets the
/// app state with every question and can call <see cref="AiChatTools"/> (state, folders, files with consent, plugin
/// guide). "Write a plugin" adds the plugin guide; answers that contain a plugin get an install button.
/// </summary>
public sealed class AiChatWindow : Window
{
    private const int MaxToolRounds = 6;

    private const string SystemPrompt =
        "You are the assistant built into Commander4K, a dual-pane file manager for Windows 11. " +
        "Answer in the language of the user's question, concisely, using Markdown. " +
        "For commands prefer PowerShell 7 and Windows tools. " +
        "The current app state is below; when the user refers to 'here', 'this file', the selection or the app, rely on it, " +
        "and call get_context for a fresh state, list_directory to look into folders and read_text_file to read a file (the user is asked first). " +
        "You cannot change files yourself. If the user wants a plugin, call get_plugin_guide first and follow it.";

    private readonly AiAppContext _context;
    private readonly Action _openProviders;
    private readonly Func<Task> _openPluginManager;
    private readonly AiChatTools _tools;
    private readonly List<AiMessage> _history = [];
    private readonly List<(AiMessage Message, AiAnswer? Answer)> _transcript = [];
    private SkinBrushes _brushes;
    private StackPanel _messages = null!;
    private ScrollViewer _scroll = null!;
    private TextBox _input = null!;
    private TextBlock _status = null!;
    private Button _send = null!;
    private Button _pluginButton = null!;
    private CancellationTokenSource? _request;
    private bool _pluginMode;
    private bool _pluginPreview;

    public AiChatWindow(AiAppContext context, SkinBrushes brushes, Action openProviders, Func<Task> openPluginManager)
    {
        _context = context;
        _brushes = brushes;
        _openProviders = openProviders;
        _openPluginManager = openPluginManager;
        _tools = new AiChatTools(context, AllowReadAsync);
        Title = Loc.T("Czat AI — Commander4K");
        Width = 760;
        Height = 720;
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildLayout();
        Opened += (_, _) => _input.Focus();
        Closed += (_, _) => _request?.Cancel();
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        var draft = _input.Text;
        BuildLayout();
        _input.Text = draft;
    }

    /// <summary>
    /// Starts a plugin-writing conversation: the guide joins the system prompt (with the preview example when
    /// <paramref name="preview"/>); <paramref name="wish"/> fills the input and, with <paramref name="send"/>, is sent.
    /// </summary>
    public void StartPlugin(string? wish = null, bool preview = false, bool send = false)
    {
        _pluginMode = true;
        _pluginPreview = preview;
        UpdatePluginButton();
        if (wish is not null)
        {
            _input.Text = wish;
        }
        else if (string.IsNullOrWhiteSpace(_input.Text))
        {
            _input.Text = Loc.T("Napisz wtyczkę, która ");
        }

        _input.Focus();
        _input.CaretIndex = _input.Text?.Length ?? 0;
        if (send)
        {
            _ = SendAsync();
        }
    }

    /// <summary>Sends a question prepared by the app (e.g. "what format is this file?").</summary>
    public void Ask(string question)
    {
        _input.Text = question;
        _ = SendAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            if (_request is not null)
            {
                _request.Cancel();
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
    }

    private void BuildLayout()
    {
        var b = _brushes;
        Ds.ApplySurface(this, b, WindowSurface.Panel);

        _messages = new StackPanel().Spacing(14).Margin(20, 16);
        _scroll = new ScrollViewer().Content(_messages);
        _status = new TextBlock().FontSize(11).Foreground(b.MutedForeground).TextTrimming(TextTrimming.CharacterEllipsis).VerticalAlignment(VerticalAlignment.Center);
        _input = new TextBox().AcceptsReturn(true).TextWrapping(TextWrapping.Wrap).MinHeight(64).MaxHeight(200)
            .PlaceholderText(Loc.T("Zadaj pytanie… (Enter wysyła, Shift+Enter nowa linia, Esc przerywa)"));
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _send = Ds.Button(b, Loc.T("Wyślij"), () => _ = SendAsync(), ButtonKind.Primary);
        _send.MinWidth = Ds.ButtonMinWidth;
        _pluginButton = Ds.Button(b, string.Empty, () =>
        {
            if (_pluginMode)
            {
                _pluginMode = false;
                _pluginPreview = false;
                UpdatePluginButton();
            }
            else
            {
                StartPlugin();
            }
        });
        UpdatePluginButton();
        ToolTip.SetTip(_pluginButton, Loc.T("Dołącza przewodnik po wtyczkach (docs/plugins.md) i przykład; gotową wtyczkę zainstalujesz przyciskiem pod odpowiedzią."));

        var headerButtons = new StackPanel().Orientation(Orientation.Horizontal).Spacing(8).Children(
            _pluginButton,
            Ds.Button(b, Loc.T("Nowa rozmowa"), () =>
            {
                _request?.Cancel();
                _history.Clear();
                _transcript.Clear();
                _pluginMode = false;
                UpdatePluginButton();
                Render();
            }),
            Ds.Button(b, Loc.T("Dostawcy…"), () => _openProviders()));
        var top = Ds.WindowHeader(b, Loc.T("Czat AI"), null, headerButtons);

        var sendRow = new DockPanel().Margin(0, 8, 0, 0);
        DockPanel.SetDock(_send, Dock.Right);
        sendRow.Children(_send, _status);
        var bottom = Ds.Band(b, new StackPanel().Children(_input, sendRow));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel().Children(top, bottom, _scroll);
        Render();
    }

    private void UpdatePluginButton()
    {
        _pluginButton.Content = _pluginMode ? Loc.T("🧩 Tryb wtyczki ✕") : Loc.T("🧩 Napisz wtyczkę");
        _pluginButton.Background = _pluginMode ? _brushes.HighlightBackground : null;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void Render()
    {
        var b = _brushes;
        _messages.Children.Clear();
        if (_transcript.Count == 0)
        {
            _messages.Children.Add(Intro());
        }

        foreach (var (message, answer) in _transcript)
        {
            if (message.Role == "user")
            {
                _messages.Children.Add(new Border().HorizontalAlignment(HorizontalAlignment.Right).MaxWidth(560).CornerRadius(new CornerRadius(8))
                    .Background(b.HighlightBackground).Padding(12, 8)
                    .Child(new SelectableTextBlock().Text(message.Content).TextWrapping(TextWrapping.Wrap).Foreground(b.FileForeground)));
                continue;
            }

            if (message.Role == "tool")
            {
                _messages.Children.Add(new TextBlock().Text(message.Content).FontSize(11).Foreground(b.MutedForeground).Margin(8, 0));
                continue;
            }

            _messages.Children.Add(AnswerCard(message.Content, answer));
        }

        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private Border AnswerCard(string text, AiAnswer? answer)
    {
        var b = _brushes;
        var footer = new DockPanel().Margin(16, 0, 16, 10);
        var buttons = new StackPanel().Orientation(Orientation.Horizontal).Spacing(6);
        var files = answer is null ? [] : PluginAuthoring.ParseFiles(text);
        if (files.Any(f => string.Equals(f.Path, ManifestReader.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            buttons.Children.Add(Ds.Style(Ds.Button(b, Loc.T("🧩 Zainstaluj wtyczkę"), () => { }, ButtonKind.Toolbar), b, ButtonKind.Primary)
                .OnClick(async click =>
                {
                    var result = await AiPluginInstaller.InstallAsync(this, _brushes, _context, files, _openPluginManager);
                    _status.Text = result.Message;
                    if (result.FixRequest is { } fix)
                    {
                        // The plugin bypasses the SDK or is invalid: the follow-up waits in the input for the user to send.
                        _input.Text = fix;
                        _input.Focus();
                        _input.CaretIndex = fix.Length;
                    }
                }));
        }

        buttons.Children.Add(new Button().Content(Loc.T("Kopiuj")).Height(26).FontSize(11).Padding(10, 0).Background(Avalonia.Media.Brushes.Transparent)
            .OnClick(async click =>
            {
                if (Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            }));
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children(buttons, new TextBlock().FontSize(11).Foreground(b.MutedForeground).TextWrapping(TextWrapping.Wrap).VerticalAlignment(VerticalAlignment.Center)
            .Text(answer is null ? string.Empty : Describe(answer)));
        return new Border().CornerRadius(new CornerRadius(8)).BorderBrush(b.PanelBorder).BorderThickness(new Thickness(1))
            .Background(b.WindowBackground).Child(new StackPanel().Children(MarkdownRenderer.Render(text, b), footer));
    }

    private Control Intro()
    {
        var b = _brushes;
        var host = _context.Host;
        var configured = host.Ai.IsConfigured;
        var panel = new StackPanel().Spacing(8).Margin(0, 20).Children(
            new TextBlock().Text(configured
                ? Loc.T("Pytania trafiają do zalogowanych dostawców po kolei — gdy któryś wyczerpie limit, odpowiada następny.")
                : Loc.T("Nie jesteś zalogowany do żadnego dostawcy AI. Zaloguj się (np. OpenRouter jednym kliknięciem), żeby rozmawiać tutaj za darmo."))
                .TextWrapping(TextWrapping.Wrap).Foreground(b.MutedForeground),
            new TextBlock().Text(Loc.T("AI widzi stan aplikacji: ścieżki obu paneli, zaznaczenie i listę wtyczek; sama może przeglądać foldery. Plik przeczyta dopiero po Twojej zgodzie."))
                .TextWrapping(TextWrapping.Wrap).FontSize(12).Foreground(b.MutedForeground));
        var ready = host.Ai.States().Where(s => s.Status is AiProviderStatus.Ready or AiProviderStatus.Waiting).Select(s => s.Provider.Name).ToList();
        if (ready.Count > 0)
        {
            panel.Children.Add(new TextBlock().Text(Loc.T("Kolejność: {0}", string.Join(" → ", ready))).FontSize(12).Foreground(b.MutedForeground).TextWrapping(TextWrapping.Wrap));
        }

        if (!configured)
        {
            panel.Children.Add(Ds.Button(b, Loc.T("Zaloguj do dostawców AI…"), () => _openProviders(), ButtonKind.Primary).HorizontalAlignment(HorizontalAlignment.Left));
        }

        return panel;
    }

    private static string Describe(AiAnswer answer)
    {
        var text = Loc.T("{0} · {1} · {2:0.0} s", answer.Provider.Name, answer.Model, answer.Duration.TotalSeconds);
        var failed = answer.Attempts.Count(a => a.Failure is not null);
        return failed > 0 ? text + Loc.P(failed, " · pominięto {0} niedostępny| · pominięto {0} niedostępne| · pominięto {0} niedostępnych") : text;
    }

    private async Task<bool> AllowReadAsync(string path) =>
        await Dispatcher.UIThread.InvokeAsync(() => new ConfirmDialog(_brushes, Loc.T("AI chce przeczytać plik"),
            Loc.T("Asystent prosi o treść pliku:\n{0}\n\nTreść zostanie wysłana do dostawcy AI, który odpowiada w tej rozmowie.", path), Loc.T("Zezwól")).ShowAsync(this));

    private async Task SendAsync()
    {
        var question = _input.Text?.Trim();
        if (string.IsNullOrEmpty(question) || _request is not null)
        {
            return;
        }

        _input.Text = string.Empty;
        var user = AiMessage.User(question);
        var historyStart = _history.Count;
        _history.Add(user);
        _transcript.Add((user, null));
        Render();

        _request = new CancellationTokenSource();
        _send.IsEnabled = false;
        _status.Foreground = _brushes.MutedForeground;
        var progress = new Progress<string>(text => _status.Text = text);
        try
        {
            var token = _request.Token;
            var system = SystemPrompt + "\n\n" + _context.Describe();
            if (_pluginMode)
            {
                system += "\n\nThe user wants a Commander4K plugin. Plugin guide and example:\n\n" + _context.PluginGuide() + "\n\n" + _context.Example(_pluginPreview) +
                    "\n\n" + PluginAuthoring.OutputFormat;
            }

            for (var round = 0; ; round++)
            {
                var messages = new List<AiMessage> { AiMessage.System(system) };
                messages.AddRange(_history);
                var tools = round < MaxToolRounds ? AiChatTools.Definitions : null;
                var answer = await Task.Run(() => _context.Host.Ai.CompleteAsync(messages, progress, cancellationToken: token, tools: tools), token);
                if (answer.ToolCalls.Count == 0)
                {
                    var reply = AiMessage.Assistant(answer.Text);
                    _history.Add(reply);
                    _transcript.Add((reply, answer));
                    break;
                }

                _history.Add(AiMessage.Assistant(answer.Text) with { ToolCalls = answer.ToolCalls });
                foreach (var call in answer.ToolCalls)
                {
                    _transcript.Add((new AiMessage("tool", AiChatTools.Describe(call)), null));
                    Render();
                    _status.Text = AiChatTools.Describe(call);
                    var result = await Task.Run(() => _tools.ExecuteAsync(call, token), token);
                    _history.Add(AiMessage.Tool(call, result));
                }
            }

            _status.Text = string.Empty;
            Render();
        }
        catch (OperationCanceledException)
        {
            _history.RemoveRange(historyStart, _history.Count - historyStart);
            _status.Text = Loc.T("Przerwano.");
        }
        catch (AiUnavailableException ex)
        {
            _history.RemoveRange(historyStart, _history.Count - historyStart);
            _status.Foreground = _brushes.Error;
            _status.Text = ex.Message;
            var details = string.Join("\n", ex.Attempts.Select(a => $"- **{a.ProviderId}** `{a.Model}`: {a.Message}"));
            _transcript.Add((AiMessage.Assistant("⚠ " + ex.Message + (details.Length > 0 ? "\n\n" + details : string.Empty)), null));
            Render();
        }
        finally
        {
            _request.Dispose();
            _request = null;
            _send.IsEnabled = true;
            _input.Focus();
        }
    }
}
