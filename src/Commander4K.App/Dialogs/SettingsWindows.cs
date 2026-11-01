using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Panels;
using Commander4K.App.Preview;
using Commander4K.App.Splash;
using Commander4K.App.Theming;
using Commander4K.Core.Configuration;
using Commander4K.Core.Input;
using Commander4K.Core.Localization;
using Commander4K.Core.Updates;

namespace Commander4K.App.Dialogs;

/// <summary>
/// One searchable page for everything configurable: settings, key bindings and the raw JSON files. Changes are
/// saved immediately; JSON stays the source of truth, so hand edits keep working.
/// </summary>
public sealed class SettingsWindow : ToolWindow
{
    private static string KeysCategory => Loc.T("Skróty klawiszowe");
    private static string FilesCategory => Loc.T("Pliki konfiguracyjne");

    private readonly AppHost _host;
    private readonly Action<string> _onChanged;
    private readonly Action<string> _openInPanel;
    private readonly Func<Task> _checkForUpdates;
    private TextBox _search = null!;
    private StackPanel _content = null!;
    private StackPanel _nav = null!;
    private TextBlock _keyStatus = null!;

    public SettingsWindow(AppHost host, SkinBrushes brushes, Action<string> onChanged,     Func<Task> checkForUpdates, Action<string> openInPanel, string? query = null)
        : base(Loc.T("Ustawienia — Commander4K"), brushes, 1000, 700, 640)
    {
        _host = host;
        _onChanged = onChanged;
            _openInPanel = openInPanel;
        _checkForUpdates = checkForUpdates;
        _initialQuery = query;
        Build();

        Opened += (_, _) =>
        {
            _search.Focus();
            _search.CaretIndex = _search.Text?.Length ?? 0;
        };
    }

    private readonly string? _initialQuery;
    private ScrollViewer _scroll = null!;

    protected override Control BuildLayout()
    {
        var brushes = Brushes;
        var query = _search?.Text ?? _initialQuery;

        _search = new TextBox().Text(query ?? string.Empty)
            .PlaceholderText(Loc.T("Szukaj ustawień, skrótów, plików… (np. „ukryte”, „F5”, „aktualizacje”)"));
        var searchField = Ds.SearchField(brushes, _search).MaxWidth(560).MinWidth(420);
        _search.TextChanged += (_, _) => Rebuild();
        _content = new StackPanel().Spacing(22).Margin(24, 20, 24, 24);
        _nav = new StackPanel().Spacing(2);
        _keyStatus = new TextBlock().TextWrapping(TextWrapping.Wrap).Foreground(brushes.Error).IsVisible(false).Margin(24, 10, 24, 0);

        // Design system: top bar (title + search), section list on the left, cards on the right, footer with actions.
        var top = new Border().BorderBrush(brushes.PanelBorder).BorderThickness(new Thickness(0, 0, 0, 1)).Padding(Ds.WindowHeaderPadding).Child(
            new Grid().Cols("Auto,*").Children(
                new TextBlock().Text(Loc.T("Ustawienia")).FontSize(18).FontWeight(FontWeight.SemiBold).Foreground(brushes.FileForeground)
                    .VerticalAlignment(VerticalAlignment.Center).Margin(0, 0, 24, 0).MinWidth(160),
                searchField.Col(1).HorizontalAlignment(HorizontalAlignment.Left)));
        var version = new TextBlock().Text(Loc.T("Wersja {0} · zmiany zapisywane od razu w config/settings.json", AppVersion.Display)).FontSize(11)
            .Foreground(brushes.MutedForeground).TextWrapping(TextWrapping.Wrap).Margin(10, 8, 10, 0);
        DockPanel.SetDock(version, Dock.Bottom);
        var nav = new Border().Width(230).Background(brushes.HeaderBackground).BorderBrush(brushes.PanelBorder)
            .BorderThickness(new Thickness(0, 0, 1, 0)).Padding(10, 12).Child(new DockPanel().Children(
                version,
                new ScrollViewer().Content(_nav)));
        var footer = Ds.Footer(brushes,
            new TextBlock().Text(Loc.T("Esc czyści wyszukiwanie, drugie Esc zamyka · Tab przechodzi między kontrolkami")).FontSize(12)
                .Foreground(brushes.MutedForeground).TextTrimming(TextTrimming.CharacterEllipsis),
            Ds.Button(brushes, Loc.T("Sprawdź aktualizacje"), () => _ = _checkForUpdates()),
            Ds.Button(brushes, Loc.T("O programie"), () => _ = new AboutDialog(brushes).ShowAsync(this)),
            Ds.Button(brushes, Loc.T("Zamknij"), Close, ButtonKind.Primary));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(nav, Dock.Left);
        DockPanel.SetDock(_keyStatus, Dock.Top);
        var content = new DockPanel().Children(top, footer, nav, _keyStatus, _scroll = Scroll(_content));
        Rebuild();
        return content;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_search.Text))
            {
                _search.Text = string.Empty;
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
    }

    private void Rebuild()
    {
        var query = _search.Text;
        _content.Children.Clear();
        _nav.Children.Clear();

        foreach (var group in SettingsCatalog.Search(_host.SettingsCatalog, query).GroupBy(s => s.Category))
        {
            Section(group.Key, [.. group.Select(setting =>
                Row(setting.Title, setting.Description + (setting.RequiresRestart ? Loc.T(" (wymaga ponownego uruchomienia)") : ""), Editor(setting)))]);
        }

        var commands = SettingsCatalog.Search(
            _host.ListCommands().OrderBy(c => c.Description, StringComparer.CurrentCulture).ToList(),
            query,
            c => c.Description,
            c => [KeysCategory, Loc.T("skrót klawisz"), c.Id, BindingsText(c.Id)]);
        if (commands.Count > 0)
        {
            Section(KeysCategory, [.. commands.Select(command => Row(command.Description, command.Id, BindingEditor(command.Id)))],
                Loc.T("Kilka skrótów oddziel średnikiem, akord spacją (np. „F5; Ctrl+K Ctrl+C”). Pusty = brak skrótu."));
        }

        if (SettingsCatalog.Search([FilesCategory], query, s => s, _ => ["json", "folder", "konfiguracja", "keymap", "tools", Loc.T("narzędzia"), Loc.T("skórki"), "plik"]).Count > 0)
        {
            Section(FilesCategory, [new Border().Padding(16, 12).Child(new WrapPanel().Children(
                FileButton(Loc.T("📂 Folder konfiguracji"), () => SkinPickerDialog.OpenFolder(_host.Config.ConfigDirectory)),
                FileButton("⌨ keymap.json", () => _ = _host.Tools.EditAsync(_host.Config.KeymapPath)),
                FileButton(Loc.T("🛠 tools.json (edytor, diff, terminal)"), () => _ = _host.Tools.EditAsync(_host.Config.ToolsPath)),
                FileButton(Loc.T("🎨 Folder skórek"), () => SkinPickerDialog.OpenFolder(_host.Config.SkinsDirectory))))]);
        }

        if (_content.Children.Count == 0)
        {
            _content.Children.Add(new TextBlock().Text(Loc.T("Nic nie pasuje do wyszukiwania.")).Foreground(Brushes.MutedForeground).Margin(0, 20));
        }
    }

    /// <summary>
    /// One section: an 11 px uppercase accent heading and a card (6 px corners) whose rows are separated by rules; plus
    /// an entry in the section list on the left that scrolls to it.
    /// </summary>
    private void Section(string title, IReadOnlyList<Control> rows, string? note = null)
    {
        var card = new StackPanel();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0 && rows[i] is Border row)
            {
                row.BorderBrush = Brushes.PanelBorder;
                row.BorderThickness = new Thickness(0, 1, 0, 0);
            }

            card.Children.Add(rows[i]);
        }

        var heading = Ds.SectionHeading(Brushes, title)
            .Margin(0, 0, 0, 8);
        var section = new StackPanel().Children(heading);
        if (note is not null)
        {
            section.Children.Add(Ds.Muted(Brushes, note).Margin(0, 0, 0, 8));
        }

        section.Children.Add(Ds.Box(Brushes, card, new Thickness(0)).ClipToBounds(true));
        _content.Children.Add(section);

        var navButton = new Button().Height(32).Padding(10, 0).HorizontalAlignment(HorizontalAlignment.Stretch)
            .HorizontalContentAlignment(HorizontalAlignment.Left).VerticalContentAlignment(VerticalAlignment.Center)
            .Background(Avalonia.Media.Brushes.Transparent).BorderThickness(new Thickness(0))
            .Foreground(Brushes.WindowForeground).Content(new TextBlock().Text(title).TextTrimming(TextTrimming.CharacterEllipsis))
            .OnClick(_ => ScrollToSection(section));
        _nav.Children.Add(navButton);
    }

    private void ScrollToSection(Control section)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (section.TranslatePoint(default, _content) is { } origin)
            {
                _scroll.Offset = new Vector(_scroll.Offset.X, Math.Max(0, origin.Y - 16));
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Design system: min. 60 px, 10/16 px padding, semibold title, muted description, the control on the right.</summary>
    private Border Row(string title, string description, Control editor) =>
        new Border()
            .Padding(16, 10)
            .MinHeight(60)
            .Child(new Grid().Cols("*,Auto").Children(
                new StackPanel().VerticalAlignment(VerticalAlignment.Center).Children(
                    new TextBlock().Text(title).FontWeight(FontWeight.SemiBold).Foreground(Brushes.FileForeground).TextWrapping(TextWrapping.Wrap),
                    new TextBlock().Text(description).FontSize(12).Foreground(Brushes.MutedForeground).TextWrapping(TextWrapping.Wrap).Margin(0, 2, 0, 0)),
                editor.Col(1).Margin(16, 0, 0, 0).VerticalAlignment(VerticalAlignment.Center)));

    private Control Editor(SettingDescriptor setting)
    {
        switch (setting.Kind)
        {
            case SettingKind.Toggle:
                var toggle = new ToggleSwitch().IsChecked((bool?)setting.Get(_host.Settings)).OnContent(Loc.T("Wł.")).OffContent(Loc.T("Wył."));
                toggle.IsCheckedChanged += (_, _) => Commit(setting, toggle.IsChecked == true);
                return toggle;

            case SettingKind.Choice:
                var choices = setting.Choices?.Invoke() ?? [];
                var combo = new ComboBox().Width(260).ItemsSource(choices.Select(c => c.Label).ToList());
                combo.SelectedIndex = choices.ToList().FindIndex(c => Equals(c.Value, setting.Get(_host.Settings)));
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0)
                    {
                        Commit(setting, choices[combo.SelectedIndex].Value);
                    }
                };
                return combo;

            default:
                var text = new TextBox().Width(300).Text(setting.Get(_host.Settings) as string ?? string.Empty);
                text.LostFocus += (_, _) => Commit(setting, text.Text);
                text.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        Commit(setting, text.Text);
                        e.Handled = true;
                    }
                };
                if (setting.Key.Equals("pluginCatalogUrl", StringComparison.OrdinalIgnoreCase))
                {
                    var open = new Button().Width(34).Height(30).Content("↗")
                        .OnClick(async _ => await OpenCatalogUrlAsync(text.Text));
                    ToolTip.SetTip(open, Loc.T("Otwórz w przeglądarce systemowej"));
                    open.IsEnabled = Core.Ai.AiInstallInfo.IsWebUrl(text.Text);
                    text.TextChanged += (_, _) => open.IsEnabled = Core.Ai.AiInstallInfo.IsWebUrl(text.Text);
                    return new Grid().Cols("*,Auto").Children(text, open.Col(1).Margin(6, 0, 0, 0));
                }

                return text;
        }
    }

    private async Task OpenCatalogUrlAsync(string? url)
    {
        if (!Core.Ai.AiInstallInfo.IsWebUrl(url))
        {
            return;
        }

        var target = await new LinkOpenDialog(Brushes, url!).ShowAsync(this);
        if (target == LinkOpenTarget.SystemBrowser)
        {
            AboutDialog.OpenUrl(url!);
        }
        else if (target == LinkOpenTarget.ActivePanel)
        {
            _openInPanel(url!);
        }
    }

    private void Commit(SettingDescriptor setting, object? value)
    {
        if (Equals(setting.Get(_host.Settings), value))
        {
            return;
        }

        setting.Set(_host.Settings, value);
        _host.ApplySettings();
        _ = _host.SaveSettingsAsync();
        _onChanged(setting.Key);
    }

    private string BindingsText(string commandId) =>
        string.Join("; ", _host.KeyBindings.Where(b => string.Equals(b.Value, commandId, StringComparison.OrdinalIgnoreCase)).Select(b => b.Key));

    private TextBox BindingEditor(string commandId)
    {
        var box = new TextBox().Width(220).Text(BindingsText(commandId)).FontFamily(Brushes.MonospaceFamily);
        async Task CommitAsync()
        {
            var requested = (box.Text ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (string.Join("; ", requested) == BindingsText(commandId))
            {
                return;
            }

            var bindings = _host.KeyBindings
                .Where(b => !string.Equals(b.Value, commandId, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(b => b.Key, b => b.Value, StringComparer.OrdinalIgnoreCase);
            var conflicts = new List<string>();
            try
            {
                foreach (var sequence in requested)
                {
                    var canonical = string.Join(' ', sequence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => KeyStroke.Parse(s).ToString()));
                    foreach (var clash in bindings.Where(b => string.Equals(Canonical(b.Key), canonical, StringComparison.Ordinal)).ToList())
                    {
                        conflicts.Add(Loc.T("{0} był przypisany do „{1}”", canonical, clash.Value));
                        bindings.Remove(clash.Key);
                    }

                    bindings[canonical] = commandId;
                }

                await _host.SaveKeyBindingsAsync(bindings);
                _keyStatus.IsVisible = conflicts.Count > 0;
                _keyStatus.Foreground = Brushes.SelectedForeground;
                _keyStatus.Text = conflicts.Count > 0 ? Loc.T("Przeniesiono skróty: ") + string.Join("; ", conflicts) + "." : string.Empty;
                box.Text = BindingsText(commandId);
            }
            catch (FormatException ex)
            {
                _keyStatus.IsVisible = true;
                _keyStatus.Foreground = Brushes.Error;
                _keyStatus.Text = Loc.T("Niepoprawny skrót: {0}", ex.Message);
            }
        }

        box.LostFocus += async (_, _) => await CommitAsync();
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await CommitAsync();
            }
        };
        return box;
    }

    private static string Canonical(string sequence)
    {
        try
        {
            return string.Join(' ', sequence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => KeyStroke.Parse(s).ToString()));
        }
        catch (FormatException)
        {
            return sequence;
        }
    }

    private static Button FileButton(string text, Action onClick) =>
        new Button().Content(text).Margin(0, 0, 8, 8).OnClick(_ => onClick());
}

public sealed class AboutDialog : DialogWindow<bool>
{
    public const string RepositoryUrl = "https://github.com/4kprojekt/Commander4K";

    public AboutDialog(SkinBrushes brushes) : base(Loc.T("O programie"), brushes, 520)
    {
        HeaderGlyph = "▦";
        FooterLeading = Ds.Button(brushes, Loc.T("Animacja startowa"), () => _ = new SplashWindow(brushes, SplashMode.Demo).ShowDialog(this));
        SetBody(
            new TextBlock().Text("Commander4K").FontSize(20).FontWeight(FontWeight.Bold).Foreground(brushes.FileForeground),
            new SelectableTextBlock().FontSize(12).Foreground(brushes.MutedForeground).Text(
                $"Wersja {AppVersion.Display}\n" +
                $".NET {Environment.Version} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.OSDescription}"),
            Message(Loc.T("Dwupanelowy menedżer plików dla Windows 11 inspirowany Norton, Midnight i Total Commanderem.")).Margin(0, 12, 0, 6),
            Message(Loc.T("Oprogramowanie open source na licencji MIT. © 2026 4Kprojekt.")),
            Ds.Notice(brushes, Loc.T("🤖 Projekt jest tworzony z pomocą sztucznej inteligencji (Claude Code). Kod generowany przez AI może zawierać błędy — zgłaszaj je na GitHubie.")),
            ButtonRow(
                ("GitHub", () => OpenUrl(RepositoryUrl), false),
                ("OK", () => Complete(true), true)));
    }

    public static void OpenUrl(string url)
    {
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}

/// <summary>Shows a release and, for the published single-file build, downloads, verifies and installs it.</summary>
public sealed class UpdateDialog : DialogWindow<bool>
{
    private readonly AppHost _host;
    private readonly UpdateInfo _update;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly Button _install;
    private CancellationTokenSource? _download;

    public UpdateDialog(AppHost host, SkinBrushes brushes, UpdateInfo update) : base(Loc.T("Dostępna aktualizacja"), brushes, 620)
    {
        _host = host;
        _update = update;
        var kind = UpdateService.DetectInstallKind(Environment.ProcessPath);
        var canInstall = kind != InstallKind.Development && update.AssetUrl is not null;
        _progress = new ProgressBar().Minimum(0).Maximum(1).IsVisible(false).Margin(0, 8, 0, 0).Foreground(brushes.Progress);
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).Margin(0, 6, 0, 0).Text(canInstall
            ? (kind == InstallKind.Msi
                ? Loc.T("Instalator MSI zostanie pobrany z GitHuba, sprawdzony sumą SHA-256 i uruchomiony (Windows może poprosić o zgodę administratora).")
                : Loc.T("Wersja przenośna: archiwum ZIP zostanie pobrane z GitHuba, sprawdzone sumą SHA-256 i podmienione."))
            : Loc.T("Ta kopia nie jest pojedynczym plikiem z wydania (np. uruchomiona przez dotnet run), więc pobierz nową wersję ze strony wydania."));
        _install = Ds.Button(brushes, Loc.T("Pobierz i zainstaluj"), async () => await InstallAsync(), ButtonKind.Primary).IsVisible(canInstall);
        FooterLeading = Ds.Button(brushes, Loc.T("Strona wydania"), () => AboutDialog.OpenUrl(update.PageUrl));

        SetBody(
            new TextBlock().Text($"{update.Name}{(update.IsPrerelease ? Loc.T("  (wersja testowa)") : "")}").FontSize(17).FontWeight(FontWeight.SemiBold).Foreground(brushes.FileForeground),
            Label($"Zainstalowana: {AppVersion.Display} → nowa: {update.Version}"),
            new ScrollViewer().MaxHeight(260).Margin(0, 6, 0, 0).Content(
                MarkdownRenderer.Render(string.IsNullOrWhiteSpace(update.Notes) ? Loc.T("(brak opisu zmian)") : update.Notes, brushes)),
            _progress,
            _status,
            FooterRow(
                Ds.Button(brushes, Loc.T("Pomiń tę wersję"), async () =>
                {
                    host.Settings.SkippedVersion = update.Version.ToString();
                    await host.SaveSettingsAsync();
                    Complete(false);
                }),
                Ds.Button(brushes, Loc.T("Później"), Close, canInstall ? ButtonKind.Secondary : ButtonKind.Primary),
                _install));
        Closed += (_, _) => _download?.Cancel();
    }

    private async Task InstallAsync()
    {
        var current = Environment.ProcessPath!;
        _install.IsEnabled = false;
        _progress.IsVisible = true;
        _download = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                _progress.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
                _status.Text = Loc.T("Pobieranie… {0} z {1}", FileViewportCanvas.FormatSize(p.Done), FileViewportCanvas.FormatSize(p.Total));
            });
            var staging = Path.Combine(_host.DataDirectory, "data", "update");
            var downloaded = await _host.Updates.DownloadAsync(_update, staging, progress, _download.Token);
            _status.Text = Loc.T("Instalowanie…");
            if (UpdateService.DetectInstallKind(current) == InstallKind.Msi)
            {
                // The MSI replaces files in Program Files itself; it needs this process gone, so start it and exit.
                UpdateService.StartMsiInstall(downloaded);
            }
            else
            {
                await Task.Run(() => UpdateService.Install(UpdateService.ExtractExecutable(downloaded, staging), current));
                _status.Text = Loc.T("Zainstalowano {0}. Uruchamianie nowej wersji…", _update.Version);
                using (Process.Start(new ProcessStartInfo(current) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(current)! }))
                {
                }
            }

            Complete(true);
            Dispatcher.UIThread.Post(() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        }
        catch (OperationCanceledException)
        {
            _status.Text = Loc.T("Przerwano pobieranie.");
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            _status.Foreground = Brushes.Error;
            _status.Text = "⚠ " + ex.Message;
        }
        finally
        {
            _install.IsEnabled = true;
        }
    }
}
