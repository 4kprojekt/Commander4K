using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Dialogs;
using Commander4K.App.Preview;
using Commander4K.App.Theming;
using Commander4K.Core.Ai;
using Commander4K.Core.Localization;

namespace Commander4K.App.Ai;

/// <summary>
/// Sign in to AI providers (browser login, pasted key, environment variable or GitHub CLI), choose their order and
/// model, test them, and see which one is waiting for its limit. Changes are saved at once to <c>config/ai</c>.
/// </summary>
public sealed class AiProvidersWindow : Window
{
    private readonly AppHost _host;
    private readonly Dictionary<string, string> _testResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _showKeyBox = new(StringComparer.OrdinalIgnoreCase);
    private SkinBrushes _brushes;
    private StackPanel _list = null!;
    private TextBlock _source = null!;
    private ScrollViewer _scroll = null!;
    /// <summary>Local servers found on this computer: provider id → number of models (0 = running without models); missing = not answering.</summary>
    private readonly Dictionary<string, int> _detected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _installOpen = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _login;
    private bool _rebuildPending;
    private bool _detecting;

    public AiProvidersWindow(AppHost host, SkinBrushes brushes)
    {
        _host = host;
        _brushes = brushes;
        Title = Loc.T("Dostawcy AI — Commander4K");
        Width = 860;
        Height = 760;
        MinWidth = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildLayout();
        _ = DetectLocalAsync();
        host.Ai.StateChanged += ScheduleRebuild;
        Closed += (_, _) =>
        {
            host.Ai.StateChanged -= ScheduleRebuild;
            _login?.Cancel();
        };
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        var offset = _scroll.Offset;
        BuildLayout();
        Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    private void BuildLayout()
    {
        var b = _brushes;
        WindowSkin.Apply(this, b, panelSurface: true);

        _list = new StackPanel().Spacing(10).Margin(24, 16, 24, 24);
        _scroll = new ScrollViewer().Content(_list);
        _source = new TextBlock().FontSize(11).Foreground(b.MutedForeground).VerticalAlignment(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis);

        var top = new Border().BorderBrush(b.PanelBorder).BorderThickness(new Thickness(0, 0, 0, 1)).Padding(24, 14).Child(new StackPanel().Spacing(4).Children(
            new TextBlock().Text(Loc.T("Dostawcy AI")).FontSize(18).FontWeight(FontWeight.SemiBold).Foreground(b.FileForeground),
            new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12).Foreground(b.MutedForeground).Text(
                Loc.T("Zaloguj się do kilku darmowych usług — gdy jedna wyczerpie limit albo zablokuje dostęp, odpowiada następna w kolejności. Klucze są zapisywane w config/ai/credentials.json, zaszyfrowane dla bieżącego konta Windows."))));
        var buttons = new StackPanel().Orientation(Orientation.Horizontal).Spacing(8).Children(
            new Button().Content(Loc.T("Odśwież listę")).Height(30).OnClick(async _ => await RefreshCatalogAsync()),
            new Button().Content(Loc.T("📂 Folder config/ai")).Height(30).OnClick(_ =>
                SkinPickerDialog.OpenFolder(Path.GetDirectoryName(_host.AiCredentials.FilePath)!)),
            WindowSkin.ApplyAccent(new Button().Content(Loc.T("Zamknij")).Height(30), b).OnClick(_ => Close()));
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new Border().Background(b.HeaderBackground).BorderBrush(b.PanelBorder).BorderThickness(new Thickness(0, 1, 0, 0))
            .Padding(24, 10).Child(new DockPanel().Children(buttons, _source));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel().Children(top, footer, _scroll);
        Rebuild();
    }

    private void ScheduleRebuild()
    {
        // Router events come from worker threads and in bursts (one per tried model).
        if (_rebuildPending)
        {
            return;
        }

        _rebuildPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildPending = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        var catalog = _host.AiCatalog;
        _source.Text = Loc.T("Lista: {0} · {1} dostawców · aktualizacja {2}", catalog.Source, catalog.Providers.Count, catalog.Updated ?? "?");
        _list.Children.Clear();
        var states = _host.Ai.States();
        var signedIn = states.Where(s => s.Status != AiProviderStatus.NotSignedIn).ToList();
        if (signedIn.Count > 0)
        {
            _list.Children.Add(Heading(Loc.T("Kolejność rotacji")));
            for (var i = 0; i < signedIn.Count; i++)
            {
                _list.Children.Add(Card(signedIn[i], i, signedIn));
            }
        }

        var available = states.Where(s => s.Status == AiProviderStatus.NotSignedIn).ToList();
        if (available.Count > 0)
        {
            _list.Children.Add(Heading(Loc.T("Dostępni dostawcy")).Margin(0, signedIn.Count > 0 ? 12 : 0, 0, 0));
            foreach (var state in available)
            {
                _list.Children.Add(Card(state, -1, signedIn));
            }
        }
    }

    private TextBlock Heading(string text) =>
        new TextBlock().Text(text.ToUpper(Loc.Culture)).FontSize(11).FontWeight(FontWeight.SemiBold).Foreground(_brushes.Accent);

    private Border Card(AiProviderState state, int position, IReadOnlyList<AiProviderState> signedIn)
    {
        var b = _brushes;
        var provider = state.Provider;
        var (statusText, statusBrush) = state.Status switch
        {
            AiProviderStatus.Ready => (Loc.T("gotowy"), b.Success),
            AiProviderStatus.Waiting => (Loc.T("limit — wraca o {0:HH:mm}", state.Until!.Value.ToLocalTime()), b.Warning),
            AiProviderStatus.InvalidKey => (Loc.T("klucz odrzucony"), b.Error),
            AiProviderStatus.Disabled => (Loc.T("wyłączony"), b.MutedForeground),
            _ => (Loc.T("niezalogowany"), b.MutedForeground),
        };
        var tier = provider.Tier switch
        {
            "credits" => Loc.T("darmowy kredyt"),
            "local" => Loc.T("lokalny"),
            _ => Loc.T("darmowy"),
        };

        var header = new WrapPanel().Children(
            new TextBlock().Text(position >= 0 ? $"{position + 1}. {provider.Name}" : provider.Name).FontWeight(FontWeight.SemiBold).FontSize(b.FontSize + 1)
                .Foreground(b.FileForeground).VerticalAlignment(VerticalAlignment.Center).Margin(0, 0, 10, 0),
            Chip(tier, b.Accent),
            Chip("● " + statusText, statusBrush));
        if (provider.Tier == "local")
        {
            header.Children.Add(_detecting && !_detected.ContainsKey(provider.Id) ? Chip(Loc.T("sprawdzam…"), b.MutedForeground)
                : _detected.TryGetValue(provider.Id, out var count)
                    ? Chip(count > 0 ? Loc.P(count, "działa · {0} model|działa · {0} modele|działa · {0} modeli") : Loc.T("działa, brak pobranych modeli"), count > 0 ? b.Success : b.Warning)
                    : Chip(Loc.T("nie działa na tym komputerze"), b.MutedForeground));
        }
        if (state.KeySource is { } source)
        {
            header.Children.Add(Chip(source switch
            {
                "oauth" => Loc.T("zalogowano przez przeglądarkę"),
                "gh" => "GitHub CLI",
                var env when env.StartsWith("env:", StringComparison.Ordinal) => Loc.T("zmienna {0}", env[4..]),
                _ => Loc.T("klucz"),
            }, b.MutedForeground));
        }

        var body = new StackPanel().Spacing(6).Children(header);
        if (provider.Note is { Length: > 0 } note)
        {
            body.Children.Add(new TextBlock().Text(note).FontSize(12).Foreground(b.MutedForeground).TextWrapping(TextWrapping.Wrap));
        }

        var model = _host.AiCredentials.Get(provider.Id)?.Model;
        if (state.Status != AiProviderStatus.NotSignedIn)
        {
            body.Children.Add(new TextBlock().FontSize(12).Foreground(b.MutedForeground).TextWrapping(TextWrapping.Wrap).Text(
                Loc.T("Model: {0}", model ?? (provider.Models.Count > 0 ? string.Join(", ", provider.Models.Take(3)) : Loc.T("pierwszy dostępny")))));
        }

        if (state.LastError is { Length: > 0 } error && state.Status != AiProviderStatus.Ready)
        {
            body.Children.Add(new SelectableTextBlock().Text(error).FontSize(12).Foreground(state.Status == AiProviderStatus.InvalidKey ? b.Error : b.Warning).TextWrapping(TextWrapping.Wrap));
        }

        if (_testResults.TryGetValue(provider.Id, out var test))
        {
            body.Children.Add(new SelectableTextBlock().Text(test).FontSize(12).Foreground(b.FileForeground).TextWrapping(TextWrapping.Wrap));
        }

        body.Children.Add(Actions(state, position, signedIn));
        if (provider.Install is { } install)
        {
            body.Children.Add(InstallSection(provider, install, state));
        }

        return new Border().CornerRadius(new CornerRadius(6)).BorderBrush(b.PanelBorder).BorderThickness(new Thickness(1))
            .Background(b.WindowBackground).Padding(16, 12).Child(body);
    }

    private Control Actions(AiProviderState state, int position, IReadOnlyList<AiProviderState> signedIn)
    {
        var provider = state.Provider;
        var row = new WrapPanel().Margin(0, 4, 0, 0);
        void Add(Control control) => row.Children.Add(control.Margin(0, 0, 8, 6));

        if (state.Status == AiProviderStatus.NotSignedIn)
        {
            if (provider.Auth == AiAuthKinds.None)
            {
                Add(Primary(Loc.T("Włącz"), () => _ = Task.Run(() => _host.AiCredentials.Update(provider.Id, c => (c ?? new AiCredential()) with { Enabled = true, Source = "local" }))
                    .ContinueWith(_ => TestAsync(provider), TaskScheduler.FromCurrentSynchronizationContext())));
                Add(Button(Loc.T("Strona ↗"), () => OpenWeb(provider.KeyUrl), AiInstallInfo.IsWebUrl(provider.KeyUrl)));
                return row;
            }

            if (provider.Auth == AiAuthKinds.OpenRouterOAuth)
            {
                Add(Primary(_login is null ? Loc.T("Zaloguj przez przeglądarkę") : Loc.T("Czekam na logowanie… (anuluj)"), () => _ = LoginOpenRouterAsync(provider)));
            }

            if (provider.Auth != AiAuthKinds.OpenRouterOAuth || _showKeyBox.Contains(provider.Id))
            {
                var key = new TextBox().Width(300).PasswordChar('•').PlaceholderText(Loc.T("wklej klucz API"));
                void Save()
                {
                    if (!string.IsNullOrWhiteSpace(key.Text))
                    {
                        var text = key.Text;
                        _ = Task.Run(() => _host.AiCredentials.SaveKey(provider.Id, text, "pasted")).ContinueWith(_ => TestAsync(provider), TaskScheduler.FromCurrentSynchronizationContext());
                    }
                }

                key.KeyDown += (_, e) =>
                {
                    if (e.Key == Avalonia.Input.Key.Enter)
                    {
                        Save();
                        e.Handled = true;
                    }
                };
                Add(key);
                Add(provider.Auth == AiAuthKinds.OpenRouterOAuth ? Button(Loc.T("Zapisz"), Save) : Primary(Loc.T("Zapisz"), Save));
            }
            else
            {
                Add(Button(Loc.T("Wklej klucz"), () =>
                {
                    _showKeyBox.Add(provider.Id);
                    Rebuild();
                }));
            }

            Add(Button(Loc.T("Pobierz klucz ↗"), () => OpenWeb(provider.KeyUrl), AiInstallInfo.IsWebUrl(provider.KeyUrl)));
            if (provider.GitHubCli)
            {
                row.Children.Add(new TextBlock().Text(Loc.T("albo: gh auth login (GitHub CLI) i uruchom ponownie")).FontSize(11)
                    .Foreground(_brushes.MutedForeground).VerticalAlignment(VerticalAlignment.Center));
            }

            return row;
        }

        Add(Primary(Loc.T("Testuj"), () => _ = TestAsync(provider)));
        Add(Button(Loc.T("Model…"), () => _ = ChooseModelAsync(provider)));
        Add(Button("▲", () => Move(signedIn, position, -1), position > 0));
        Add(Button("▼", () => Move(signedIn, position, +1), position < signedIn.Count - 1));
        var enabled = state.Status != AiProviderStatus.Disabled;
        Add(Button(enabled ? Loc.T("Wyłącz") : Loc.T("Włącz"), () =>
            Store(() => _host.AiCredentials.Update(provider.Id, c => (c ?? new AiCredential()) with { Enabled = !enabled }))));
        if (state.Status is AiProviderStatus.Waiting or AiProviderStatus.InvalidKey)
        {
            Add(Button(Loc.T("Spróbuj teraz"), () => _host.Ai.Reset(provider.Id)));
        }

        var stored = _host.AiCredentials.Get(provider.Id) is not null;
        Add(Button(provider.Auth == AiAuthKinds.None ? Loc.T("Usuń") : Loc.T("Wyloguj"), () =>
        {
            _testResults.Remove(provider.Id);
            Store(() => _host.AiCredentials.Remove(provider.Id));
        }, stored));
        if (!stored || state.KeySource is "gh" || state.KeySource?.StartsWith("env:", StringComparison.Ordinal) == true)
        {
            row.Children.Add(new TextBlock().Text(Loc.T("klucz spoza aplikacji — wylogowanie: usuń zmienną lub gh auth logout")).FontSize(11)
                .Foreground(_brushes.MutedForeground).VerticalAlignment(VerticalAlignment.Center));
        }

        return row;
    }

    private void Move(IReadOnlyList<AiProviderState> signedIn, int position, int delta)
    {
        var order = signedIn.Select(s => s.Provider.Id).ToList();
        (order[position], order[position + delta]) = (order[position + delta], order[position]);
        Store(() =>
        {
            for (var i = 0; i < order.Count; i++)
            {
                var priority = i;
                _host.AiCredentials.Update(order[i], c => (c ?? new AiCredential()) with { Priority = priority });
            }
        });
    }

    private async Task TestAsync(AiProviderInfo provider)
    {
        _testResults[provider.Id] = Loc.T("Testowanie…");
        Rebuild();
        try
        {
            var answer = await Task.Run(() => _host.Ai.CompleteAsync(
                [AiMessage.User("Reply with one short sentence confirming you work, in the language of this word: " + Loc.T("Działa"))],
                onlyProvider: provider.Id));
            _testResults[provider.Id] = Loc.T("✓ {0} odpowiada w {1:0.0} s: {2}", answer.Model, answer.Duration.TotalSeconds, answer.Text.Trim());
        }
        catch (AiUnavailableException ex)
        {
            _testResults[provider.Id] = "✕ " + string.Join("\n", ex.Attempts.Select(a => $"{a.Model}: {a.Message}").DefaultIfEmpty(ex.Message)) +
                (provider.Install is not null ? "\n" + Loc.T("Serwer nie odpowiada albo nie ma pobranego modelu? Zobacz „Jak zainstalować”.") : string.Empty);
        }

        if (provider.Tier == "local")
        {
            _ = DetectLocalAsync();
        }

        Rebuild();
    }

    private async Task ChooseModelAsync(AiProviderInfo provider)
    {
        var models = await Task.Run(() => _host.Ai.AvailableModelsAsync(provider, CancellationToken.None));
        var current = _host.AiCredentials.Get(provider.Id)?.Model;
        var items = new List<PickerItem> { new(Loc.T("Automatycznie (kolejność z listy dostawców)"), null, string.Empty) };
        items.AddRange(models.Select(m => new PickerItem(m, m == current ? Loc.T("wybrany") : provider.Models.Contains(m) ? Loc.T("polecany") : null, m)));
        var choice = await new PickerDialog(_brushes, Loc.T("Model — {0}", provider.Name), items, Loc.T("Model próbowany jako pierwszy (pozostałe zostają zapasowe):")).ShowAsync(this);
        if (choice is not null)
        {
            Store(() => _host.AiCredentials.Update(provider.Id, c => (c ?? new AiCredential()) with { Model = choice.Value.Length == 0 ? null : choice.Value }));
        }
    }

    private async Task LoginOpenRouterAsync(AiProviderInfo provider)
    {
        if (_login is not null)
        {
            _login.Cancel();
            return;
        }

        _login = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Rebuild();
        try
        {
            var key = await Task.Run(() => AiLogin.OpenRouterAsync(_host.Http, url =>
            {
                AboutDialog.OpenUrl(url);
                return Task.CompletedTask;
            }, _login.Token));
            await Task.Run(() => _host.AiCredentials.SaveKey(provider.Id, key, "oauth"));
            Activate();
            _ = TestAsync(provider);
        }
        catch (OperationCanceledException)
        {
            _testResults[provider.Id] = Loc.T("Logowanie przerwane.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {
            _testResults[provider.Id] = "✕ " + ex.Message;
        }
        finally
        {
            _login.Dispose();
            _login = null;
            Rebuild();
        }
    }

    private async Task RefreshCatalogAsync()
    {
        _source.Text = Loc.T("Pobieranie listy…");
        var error = await Task.Run(() => _host.AiCatalog.RefreshAsync(_host.Settings.AiCatalogUrl));
        Rebuild();
        if (error is not null)
        {
            _source.Text = Loc.T("Nie udało się pobrać listy: {0}", error);
        }
    }

    /// <summary>
    /// "How to install" for local providers: Markdown steps from the provider list, a winget button and the download
    /// page. Open by default while the server is not running and the provider is not enabled yet.
    /// </summary>
    private Control InstallSection(AiProviderInfo provider, AiInstallInfo install, AiProviderState state)
    {
        var b = _brushes;
        var open = _installOpen.TryGetValue(provider.Id, out var chosen) ? chosen
            : !_detecting && !_detected.ContainsKey(provider.Id) && state.Status == AiProviderStatus.NotSignedIn;
        var toggle = new Button().Content((open ? "▾ " : "▸ ") + Loc.T("Jak zainstalować")).Height(28).Padding(8, 0)
            .Background(Avalonia.Media.Brushes.Transparent).BorderThickness(new Thickness(0)).Foreground(b.Accent)
            .OnClick(_ =>
            {
                _installOpen[provider.Id] = !open;
                Rebuild();
            });
        var section = new StackPanel().Spacing(6).Children(toggle);
        if (!open)
        {
            return section;
        }

        if (install.LocalizedSteps is { Length: > 0 } steps)
        {
            section.Children.Add(new Border().CornerRadius(new CornerRadius(5)).BorderBrush(b.PanelBorder).BorderThickness(new Thickness(1))
                .Background(b.PanelBackground).Child(MarkdownRenderer.Render(steps, b)));
        }

        var buttons = new WrapPanel();
        if (install.SafeWingetId is { } id)
        {
            buttons.Children.Add(Primary(Loc.T("Zainstaluj (winget)"), () => RunWinget(provider, id)).Margin(0, 0, 8, 6));
        }

        if (AiInstallInfo.IsWebUrl(install.DownloadUrl))
        {
            buttons.Children.Add(Button(Loc.T("Pobierz instalator ↗"), () => OpenWeb(install.DownloadUrl)).Margin(0, 0, 8, 6));
        }

        buttons.Children.Add(Button(Loc.T("Sprawdź ponownie"), () => _ = DetectLocalAsync(), !_detecting).Margin(0, 0, 8, 6));
        section.Children.Add(buttons);
        return section;
    }

    /// <summary>Asks each local provider for its models (2 s each, in parallel) to show whether it runs here.</summary>
    private async Task DetectLocalAsync()
    {
        _detecting = true;
        Rebuild();
        var client = new OpenAiCompatibleClient(_host.Http);
        var local = _host.AiCatalog.Providers.Where(p => p.Tier == "local").ToList();
        var results = await Task.WhenAll(local.Select(provider => Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                return (provider.Id, Count: (int?)(await client.ListModelsAsync(provider, null, freeOnly: false, timeout.Token)).Count);
            }
            catch (Exception ex) when (ex is AiProviderException or OperationCanceledException)
            {
                return (provider.Id, Count: (int?)null);
            }
        })));
        _detected.Clear();
        foreach (var (providerId, count) in results)
        {
            if (count is { } models)
            {
                _detected[providerId] = models;
            }
        }

        _detecting = false;
        Rebuild();
    }

    private void RunWinget(AiProviderInfo provider, string packageId)
    {
        try
        {
            // A visible console: winget shows its progress and may ask for consent (UAC) itself.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -NoExit -Command winget install --id {packageId} -e --accept-source-agreements --accept-package-agreements")
            {
                UseShellExecute = true,
            });
            _testResults[provider.Id] = Loc.T("Instalacja uruchomiona w oknie PowerShell. Gdy się skończy, kliknij „Sprawdź ponownie”.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _testResults[provider.Id] = "✕ " + ex.Message;
        }

        Rebuild();
    }

    /// <summary>Opens only http(s) links: the provider list comes from the internet and must not launch files.</summary>
    private static void OpenWeb(string? url)
    {
        if (AiInstallInfo.IsWebUrl(url))
        {
            AboutDialog.OpenUrl(url!);
        }
    }

    /// <summary>Credential writes touch the disk, so they run off the UI thread; the store's Changed event rebuilds the list.</summary>
    private static void Store(Action write) => _ = Task.Run(write);

    private Border Chip(string text, IBrush foreground) =>
        new Border().CornerRadius(new CornerRadius(10)).BorderBrush(_brushes.PanelBorder).BorderThickness(new Thickness(1)).Padding(8, 1)
            .Margin(0, 0, 6, 0).VerticalAlignment(VerticalAlignment.Center)
            .Child(new TextBlock().Text(text).FontSize(11).Foreground(foreground));

    private static Button Button(string text, Action onClick, bool enabled = true) =>
        new Button().Content(text).Height(30).IsEnabled(enabled).VerticalContentAlignment(VerticalAlignment.Center).OnClick(_ => onClick());

    private Button Primary(string text, Action onClick) =>
        WindowSkin.ApplyAccent(Button(text, onClick), _brushes);
}
