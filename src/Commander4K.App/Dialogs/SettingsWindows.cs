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
public sealed class SettingsWindow : Window
{
    private static string KeysCategory => Loc.T("Skróty klawiszowe");
    private static string FilesCategory => Loc.T("Pliki konfiguracyjne");

    private readonly AppHost _host;
    private readonly SkinBrushes _brushes;
    private readonly Action<string> _onChanged;
    private readonly Func<Task> _checkForUpdates;
    private readonly TextBox _search;
    private readonly StackPanel _content;
    private readonly TextBlock _keyStatus;

    public SettingsWindow(AppHost host, SkinBrushes brushes, Action<string> onChanged, Func<Task> checkForUpdates, string? query = null)
    {
        _host = host;
        _brushes = brushes;
        _onChanged = onChanged;
        _checkForUpdates = checkForUpdates;
        Title = Loc.T("Ustawienia — Commander4K");
        Width = 820;
        Height = 700;
        MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = brushes.WindowBackground;
        Foreground = brushes.FileForeground;

        _search = new TextBox().Text(query ?? string.Empty).PlaceholderText(Loc.T("Szukaj ustawień, skrótów, plików… (np. „ukryte”, „F5”, „aktualizacje”)"));
        _search.TextChanged += (_, _) => Rebuild();
        _content = new StackPanel().Spacing(2);
        _keyStatus = new TextBlock().TextWrapping(TextWrapping.Wrap).Foreground(brushes.Error).IsVisible(false);

        Content = new Grid().Rows("Auto,Auto,*,Auto").Margin(16).Children(
            _search.Row(0),
            _keyStatus.Row(1).Margin(0, 6, 0, 0),
            new ScrollViewer().Row(2).Margin(0, 10, 0, 0).Content(_content),
            new DockPanel().Row(3).Margin(0, 10, 0, 0).Children(
                new TextBlock().Text($"Commander4K {AppVersion.Display}").Opacity(0.7).VerticalAlignment(VerticalAlignment.Center),
                new StackPanel().Orientation(Orientation.Horizontal).HorizontalAlignment(HorizontalAlignment.Right).Spacing(8).Children(
                    new Button().Content(Loc.T("Sprawdź aktualizacje")).OnClick(async _ => await _checkForUpdates()),
                    new Button().Content(Loc.T("O programie")).OnClick(async _ => await new AboutDialog(brushes).ShowAsync(this)),
                    new Button().Content(Loc.T("Zamknij")).OnClick(_ => Close()))));

        Opened += (_, _) =>
        {
            _search.Focus();
            _search.CaretIndex = _search.Text?.Length ?? 0;
        };
        Rebuild();
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

        foreach (var group in SettingsCatalog.Search(_host.SettingsCatalog, query).GroupBy(s => s.Category))
        {
            _content.Children.Add(Header(group.Key));
            foreach (var setting in group)
            {
                _content.Children.Add(Row(setting.Title, setting.Description + (setting.RequiresRestart ? Loc.T(" (wymaga ponownego uruchomienia)") : ""), Editor(setting)));
            }
        }

        var commands = SettingsCatalog.Search(
            _host.ListCommands().OrderBy(c => c.Description, StringComparer.CurrentCulture).ToList(),
            query,
            c => c.Description,
            c => [KeysCategory, Loc.T("skrót klawisz"), c.Id, BindingsText(c.Id)]);
        if (commands.Count > 0)
        {
            _content.Children.Add(Header(KeysCategory));
            _content.Children.Add(new TextBlock()
                .Text(Loc.T("Kilka skrótów oddziel średnikiem, akord spacją (np. „F5; Ctrl+K Ctrl+C”). Pusty = brak skrótu."))
                .FontSize(12).Opacity(0.7).Margin(0, 0, 0, 6));
            foreach (var command in commands)
            {
                _content.Children.Add(Row(command.Description, command.Id, BindingEditor(command.Id)));
            }
        }

        if (SettingsCatalog.Search([FilesCategory], query, s => s, _ => ["json", "folder", "konfiguracja", "keymap", "tools", Loc.T("narzędzia"), Loc.T("skórki"), "plik"]).Count > 0)
        {
            _content.Children.Add(Header(FilesCategory));
            _content.Children.Add(new WrapPanel().Children(
                FileButton(Loc.T("📂 Folder konfiguracji"), () => SkinPickerDialog.OpenFolder(_host.Config.ConfigDirectory)),
                FileButton("⌨ keymap.json", () => _ = _host.Tools.EditAsync(_host.Config.KeymapPath)),
                FileButton(Loc.T("🛠 tools.json (edytor, diff, terminal)"), () => _ = _host.Tools.EditAsync(_host.Config.ToolsPath)),
                FileButton(Loc.T("🎨 Folder skórek"), () => SkinPickerDialog.OpenFolder(_host.Config.SkinsDirectory))));
        }

        if (_content.Children.Count == 0)
        {
            _content.Children.Add(new TextBlock().Text(Loc.T("Nic nie pasuje do wyszukiwania.")).Opacity(0.7).Margin(0, 20));
        }
    }

    private TextBlock Header(string text) =>
        new TextBlock().Text(text).FontSize(16).FontWeight(FontWeight.SemiBold).Foreground(_brushes.Accent).Margin(0, 14, 0, 4);

    private Border Row(string title, string description, Control editor) =>
        new Border()
            .Padding(10, 8)
            .CornerRadius(new CornerRadius(4))
            .Background(_brushes.PanelBackground)
            .Margin(0, 0, 0, 4)
            .Child(new Grid().Cols("*,Auto").Children(
                new StackPanel().VerticalAlignment(VerticalAlignment.Center).Children(
                    new TextBlock().Text(title).FontWeight(FontWeight.SemiBold).TextWrapping(TextWrapping.Wrap),
                    new TextBlock().Text(description).FontSize(12).Opacity(0.7).TextWrapping(TextWrapping.Wrap)),
                editor.Col(1).Margin(12, 0, 0, 0).VerticalAlignment(VerticalAlignment.Center)));

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
                return text;
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
        var box = new TextBox().Width(220).Text(BindingsText(commandId)).FontFamily(FontFamily.Parse("Cascadia Mono, Consolas"));
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
                _keyStatus.Foreground = _brushes.SelectedForeground;
                _keyStatus.Text = conflicts.Count > 0 ? Loc.T("Przeniesiono skróty: ") + string.Join("; ", conflicts) + "." : string.Empty;
                box.Text = BindingsText(commandId);
            }
            catch (FormatException ex)
            {
                _keyStatus.IsVisible = true;
                _keyStatus.Foreground = _brushes.Error;
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
        SetBody(
            new TextBlock().Text("Commander4K").FontSize(22).FontWeight(FontWeight.Bold),
            new SelectableTextBlock().Text(
                $"Wersja {AppVersion.Display}\n" +
                $".NET {Environment.Version} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.OSDescription}"),
            Label(Loc.T("Dwupanelowy menedżer plików dla Windows 11 inspirowany Norton, Midnight i Total Commanderem.")).Margin(0, 12, 0, 6),
            Label(Loc.T("Oprogramowanie open source na licencji MIT. © 2026 4Kprojekt.")),
            Label(Loc.T("🤖 Projekt jest tworzony z pomocą sztucznej inteligencji (Claude Code). Kod generowany przez AI może zawierać błędy — zgłaszaj je na GitHubie.")),
            ButtonRow(("GitHub", () => OpenUrl(RepositoryUrl), false), ("OK", () => Complete(true), true)));
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
        _install = new Button().Content(Loc.T("Pobierz i zainstaluj")).IsVisible(canInstall).OnClick(async _ => await InstallAsync());

        SetBody(
            new TextBlock().Text($"{update.Name}{(update.IsPrerelease ? Loc.T("  (wersja testowa)") : "")}").FontSize(18).FontWeight(FontWeight.SemiBold),
            Label($"Zainstalowana: {AppVersion.Display} → nowa: {update.Version}"),
            new ScrollViewer().MaxHeight(260).Margin(0, 6, 0, 0).Content(
                new SelectableTextBlock().Text(string.IsNullOrWhiteSpace(update.Notes) ? Loc.T("(brak opisu zmian)") : update.Notes).TextWrapping(TextWrapping.Wrap)),
            _progress,
            _status,
            new StackPanel().Orientation(Orientation.Horizontal).HorizontalAlignment(HorizontalAlignment.Right).Spacing(8).Margin(0, 14, 0, 0).Children(
                _install,
                new Button().Content(Loc.T("Strona wydania")).OnClick(_ => AboutDialog.OpenUrl(update.PageUrl)),
                new Button().Content(Loc.T("Pomiń tę wersję")).OnClick(async _ =>
                {
                    host.Settings.SkippedVersion = update.Version.ToString();
                    await host.SaveSettingsAsync();
                    Complete(false);
                }),
                new Button().Content(Loc.T("Później")).OnClick(_ => Close())));
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
