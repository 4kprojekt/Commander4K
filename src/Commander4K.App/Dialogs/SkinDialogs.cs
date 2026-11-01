using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Commander4K.App.Composition;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Theming;

namespace Commander4K.App.Dialogs;

/// <summary>Skin list with live preview; Cancel restores the skin that was active before.</summary>
public sealed class SkinPickerDialog : DialogWindow<bool>
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;
    private readonly Skin _original;
    private readonly ListBox _list;
    private readonly TextBlock _warnings;
    private readonly TextBlock _info;

    public SkinPickerDialog(AppHost host, SkinManager skins) : base(Loc.T("Skórki"), skins.Current, 640)
    {
        _host = host;
        _skins = skins;
        _original = skins.Current.Skin;
        _list = new ListBox().Height(260);
        _list.SelectionChanged += (_, _) => PreviewSelected();
        _warnings = new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12).Margin(0, 6, 0, 0);
        _info = new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12).Opacity(0.75).Margin(0, 6, 0, 0)
            .Text(Loc.T("Własne skórki: pliki JSON w folderze {0} (schemat: {1}).", host.Config.SkinsDirectory, SkinSchema.FileName));

        SetBody(
            Label(Loc.T("Wybierz skórkę — podgląd działa na żywo:")),
            _list,
            _warnings,
            new WrapPanel().Margin(0, 10, 0, 0).Children(
                Action(Loc.T("✨ Generuj z AI…"), GenerateWithAiAsync),
                Action(Loc.T("📋 Kopiuj prompt dla Claude"), CopyPromptAsync),
                Action(Loc.T("✏ Edytor JSON (wklej, sprawdź, nowa na podstawie)…"), EditJsonAsync),
                Action(Loc.T("📂 Otwórz folder skórek"), () =>
                {
                    OpenFolder(host.Config.SkinsDirectory);
                    return Task.CompletedTask;
                }),
                Action(Loc.T("🔄 Przeładuj"), ReloadAsync)),
            _info,
            ButtonRow(("OK", Accept, true), (Loc.T("Anuluj"), Cancel, false)));
        Fill(_original.Id);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private Skin? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Skin;

    private Button Action(string text, Func<Task> onClick) =>
        new Button().Content(text).Margin(0, 0, 6, 6).OnClick(async _ => await onClick());

    private void Fill(string selectId)
    {
        var builtIn = BuiltInSkins.All.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = _host.Skins.Skins.Select(skin => new ListBoxItem
        {
            Tag = skin,
            Content = new StackPanel().Orientation(Orientation.Horizontal).Spacing(8).Children(
                Swatch(skin),
                new TextBlock().Text(skin.DisplayName).VerticalAlignment(VerticalAlignment.Center),
                new TextBlock().Text(builtIn.Contains(skin.Id) ? "" : Loc.T("(własna)")).Opacity(0.6).VerticalAlignment(VerticalAlignment.Center)),
        }).ToList();
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(i => string.Equals(((Skin)i.Tag!).Id, selectId, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();

        if (_host.Skins.Errors.Count > 0)
        {
            _info.Text += "\nNie wczytano: " + string.Join("; ", _host.Skins.Errors.Select(e => $"{Path.GetFileName(e.Path)} – {e.Error}"));
        }
    }

    /// <summary>Four colour chips: panel, directory, cursor, selection.</summary>
    private static StackPanel Swatch(Skin skin)
    {
        var p = skin.Palette;
        return new StackPanel().Orientation(Orientation.Horizontal).Children(
            [.. new[] { p.PanelBackground, p.DirectoryForeground, p.CursorBackground, p.SelectedForeground }
                .Select(c => (Control)new Border().Width(14).Height(14).Background(new SolidColorBrush(SkinBrushes.ToColor(c))))]);
    }

    private void PreviewSelected()
    {
        if (Selected is not { } skin)
        {
            return;
        }

        _skins.Preview(skin);
        var warnings = SkinValidator.Validate(skin);
        _warnings.Text = warnings.Count == 0 ? Loc.T("✓ Kontrast i czytelność w porządku.") : "⚠ " + string.Join("\n⚠ ", warnings);
    }

    private void Accept()
    {
        if (Selected is { } skin)
        {
            _skins.Apply(skin);
        }

        Complete(true);
    }

    private void Cancel()
    {
        _skins.Preview(_original);
        Complete(false);
    }

    private async Task GenerateWithAiAsync()
    {
        var generated = await new AiSkinDialog(_host, _skins).ShowAsync(this);
        Fill(generated?.Id ?? Selected?.Id ?? _original.Id);
    }

    private async Task CopyPromptAsync()
    {
        await CopyToClipboardAsync(this, SkinPrompt.ForChat(null));
        _info.Text = Loc.T("Skopiowano prompt ze schematem JSON. Wklej go w Claude.ai lub Claude Design, opisz swój pomysł, ") +
            Loc.T("a odpowiedź wklej w „Edytorze JSON” (sprawdzi ją i zapisze).");
    }

    private async Task EditJsonAsync()
    {
        var saved = await new SkinEditorDialog(_host, _skins, Selected).ShowAsync(this);
        Fill(saved?.Id ?? Selected?.Id ?? _original.Id);
        PreviewSelected();
    }

    private async Task ReloadAsync()
    {
        var selected = Selected?.Id;
        await _host.ReloadSkinsAsync();
        Fill(selected ?? _original.Id);
    }

    internal static async Task CopyToClipboardAsync(Window window, string text)
    {
        if (window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    internal static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }
}

/// <summary>"Describe it, Claude designs it": generates a skin, previews it live and saves it into config/skins.</summary>
public sealed class AiSkinDialog : DialogWindow<Skin?>
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;
    private readonly Skin _original;
    private readonly ClaudeSkinGenerator _generator = new();
    private readonly TextBox _description;
    private readonly TextBlock _status;
    private readonly WaveDots _busy = new() { IsVisible = false, Margin = new Thickness(0, 8, 8, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly Button _generate;
    private readonly Button _save;
    private CancellationTokenSource? _running;
    private Skin? _result;

    public AiSkinDialog(AppHost host, SkinManager skins) : base(Loc.T("Generuj skórkę z AI"), skins.Current, 600)
    {
        _host = host;
        _skins = skins;
        _original = skins.Current.Skin;
        _description = new TextBox()
            .AcceptsReturn(true)
            .TextWrapping(TextWrapping.Wrap)
            .Height(80)
            .PlaceholderText(Loc.T("np. „cyberpunk: neonowy róż i cyjan na granatowym tle” albo „sepia jak stary terminal”"));
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).Margin(0, 8, 0, 0)
            .Text(Loc.T("Model: {0}. Wymaga klucza ANTHROPIC_API_KEY (lub „ant auth login”). Generowanie trwa zwykle kilkanaście sekund i jest płatne wg cennika API.", ClaudeSkinGenerator.Model));
        _generate = new Button().Content(Loc.T("✨ Generuj")).OnClick(async _ => await GenerateAsync());
        _save = new Button().Content(Loc.T("💾 Zapisz skórkę")).IsEnabled(false).OnClick(async _ => await SaveAsync());

        SetBody(
            Label(Loc.T("Opisz wygląd skórki:")),
            _description,
            new StackPanel().Orientation(Orientation.Horizontal).Spacing(8).Margin(0, 8, 0, 0).Children(
                _generate,
                new Button().Content(Loc.T("📋 Kopiuj prompt (Claude.ai / Claude Design)")).OnClick(async _ => await CopyPromptAsync())),
            new Grid().Cols("Auto,*").Children(_busy, _status.Col(1)),
            new StackPanel()
                .Orientation(Orientation.Horizontal)
                .HorizontalAlignment(HorizontalAlignment.Right)
                .Spacing(8)
                .Margin(0, 14, 0, 0)
                .Children(_save, new Button().Content(Loc.T("Zamknij")).OnClick(_ => Discard())));
        Opened += (_, _) => _description.Focus();
        Closed += (_, _) => _running?.Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_running is not null)
            {
                _running.Cancel();
            }
            else
            {
                Discard();
            }

            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private async Task GenerateAsync()
    {
        var description = _description.Text?.Trim();
        if (string.IsNullOrEmpty(description) || _running is not null)
        {
            return;
        }

        _running = new CancellationTokenSource();
        _generate.IsEnabled = false;
        _save.IsEnabled = false;
        _status.Foreground = Brushes.FileForeground;
        _busy.Foreground = Brushes.FileForeground;
        SetStatus(Loc.T("Claude projektuje skórkę… (Esc – anuluj)"), busy: true);
        try
        {
            var result = await _generator.GenerateAsync(description, _running.Token);
            if (result.Skin is { } skin)
            {
                _result = skin;
                _skins.Preview(skin);
                var warnings = SkinValidator.Validate(skin);
                _status.Text = Loc.T("✓ „{0}” — podgląd jest widoczny w głównym oknie.", skin.DisplayName) +
                    (warnings.Count == 0 ? "" : "\n⚠ " + string.Join("\n⚠ ", warnings));
                _save.IsEnabled = true;
            }
            else
            {
                _status.Foreground = Brushes.Error;
                SetStatus("⚠ " + result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(Loc.T("Anulowano."));
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _generate.IsEnabled = true;
            _busy.IsVisible = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_result is not { } skin)
        {
            return;
        }

        if (BuiltInSkins.All.Any(b => string.Equals(b.Id, skin.Id, StringComparison.OrdinalIgnoreCase)))
        {
            skin = skin with { Id = skin.Id + "-ai" };
        }

        var path = Path.Combine(_host.Config.SkinsDirectory, skin.Id + ".json");
        await SkinCatalog.ExportAsync(skin, path);
        await _host.ReloadSkinsAsync();
        _skins.Apply(_host.Skins.Get(skin.Id));
        Complete(skin);
    }

    private async Task CopyPromptAsync()
    {
        await SkinPickerDialog.CopyToClipboardAsync(this, SkinPrompt.ForChat(_description.Text));
        _status.Text = (string.IsNullOrWhiteSpace(_description.Text) ? Loc.T("Skopiowano prompt. ") : Loc.T("Skopiowano prompt z Twoim opisem. ")) +
            Loc.T("Wklej go w Claude.ai lub Claude Design; gotowy plik JSON zapisz w {0}.", _host.Config.SkinsDirectory);
    }

    private void Discard()
    {
        _running?.Cancel();
        _skins.Preview(_original);
        Complete(null);
    }

    /// <summary>Status line text; <paramref name="busy"/> shows the wave dots in front of it.</summary>
    private void SetStatus(string? text, bool busy = false)
    {
        _status.Text = text;
        _busy.IsVisible = busy;
    }
}

/// <summary>
/// Skin JSON editor: open a skin (as is, or as the base of a new one), a file or the clipboard, validate it against the
/// model with a live preview, copy a prompt that carries the schema and this JSON, and save into config/skins.
/// </summary>
public sealed class SkinEditorDialog : DialogWindow<Skin?>
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;
    private readonly Skin _original;
    private readonly ComboBox _source;
    private readonly TextBox _json;
    private readonly TextBlock _status;
    private Skin? _valid;

    public SkinEditorDialog(AppHost host, SkinManager skins, Skin? start = null) : base(Loc.T("Edytor skórki (JSON)"), skins.Current, 760)
    {
        _host = host;
        _skins = skins;
        _original = skins.Current.Skin;
        var all = host.Skins.Skins;
        _source = new ComboBox().MinWidth(260)
            .ItemsSource(all.Select(s => new ComboBoxItem { Content = s.DisplayName + " (" + s.Id + ")", Tag = s }).ToList());
        _source.SelectedIndex = Math.Max(0, all.ToList().FindIndex(s => s.Id.Equals((start ?? _original).Id, StringComparison.OrdinalIgnoreCase)));
        _json = new TextBox()
            .AcceptsReturn(true)
            .TextWrapping(TextWrapping.NoWrap)
            .Height(380)
            .FontFamily(Brushes.MonospaceFamily)
            .FontSize(12)
            .PlaceholderText(Loc.T("Wklej tutaj JSON skórki…"));
        _json.TextChanged += (_, _) => Validate(preview: false);
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).Margin(0, 8, 0, 0);

        SetBody(
            new WrapPanel().Children(
                Label(Loc.T("Skórka:")).VerticalAlignment(VerticalAlignment.Center).Margin(0, 0, 8, 6),
                _source.Margin(0, 0, 6, 6),
                Action(Loc.T("📂 Otwórz"), () => Load(SelectedSkin, asNew: false)),
                Action(Loc.T("➕ Nowa na podstawie"), () => Load(SelectedSkin, asNew: true)),
                Action(Loc.T("📄 Z pliku…"), OpenFileAsync),
                Action(Loc.T("📋 Wklej ze schowka"), PasteAsync)),
            _json,
            new WrapPanel().Margin(0, 8, 0, 0).Children(
                Action(Loc.T("✔ Sprawdź i pokaż podgląd"), () => Validate(preview: true)),
                Action(Loc.T("📋 Kopiuj prompt dla Claude (ze schematem)"), CopyPromptAsync),
                Action(Loc.T("📂 Otwórz folder skórek"), () => SkinPickerDialog.OpenFolder(host.Config.SkinsDirectory))),
            _status,
            ButtonRow((Loc.T("💾 Zapisz i użyj"), () => _ = SaveAsync(), true), (Loc.T("Zamknij"), Discard, false)));
        Load(start ?? _original, asNew: false);
        Opened += (_, _) => _json.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Discard();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private Skin? SelectedSkin => (_source.SelectedItem as ComboBoxItem)?.Tag as Skin;

    private static Button Action(string text, Action onClick) =>
        new Button().Content(text).Margin(0, 0, 6, 6).OnClick(_ => onClick());

    private static Button Action(string text, Func<Task> onClick) =>
        new Button().Content(text).Margin(0, 0, 6, 6).OnClick(async _ => await onClick());

    private void Load(Skin? skin, bool asNew)
    {
        if (skin is null)
        {
            return;
        }

        if (asNew)
        {
            skin = skin with { Id = UniqueId(skin.Id + "-wlasna"), DisplayName = skin.DisplayName + Loc.T(" (kopia)") };
        }

        _json.Text = SkinJson.Serialize(skin);
        Validate(preview: true);
    }

    private string UniqueId(string id)
    {
        var candidate = id;
        for (var n = 2; _host.Skins.Skins.Any(s => s.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            candidate = id + "-" + n;
        }

        return candidate;
    }

    private async Task OpenFileAsync()
    {
        var start = Directory.Exists(_host.Config.SkinsDirectory) ? await StorageProvider.TryGetFolderFromPathAsync(_host.Config.SkinsDirectory) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Otwórz plik skórki"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            _json.Text = await File.ReadAllTextAsync(path);
            Validate(preview: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Loc.T("Nie można odczytać {0}: {1}", path, ex.Message));
        }
    }

    private async Task PasteAsync()
    {
        if (Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } text)
        {
            ShowError(Loc.T("Schowek nie zawiera tekstu."));
            return;
        }

        _json.Text = SkinJson.Extract(text);
        Validate(preview: true);
    }

    /// <summary>Parses the editor text; on success shows the contrast warnings and, with <paramref name="preview"/>, paints the skin live.</summary>
    private void Validate(bool preview)
    {
        _valid = null;
        var text = _json.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus(Loc.T("Wklej lub otwórz JSON skórki."), Brushes.FileForeground);
            return;
        }

        Skin skin;
        try
        {
            skin = SkinJson.Deserialize(text);
        }
        catch (JsonException ex)
        {
            ShowError(Loc.T("✗ JSON niezgodny ze schematem: {0}", ex.Message));
            return;
        }

        if (!Core.Mcp.McpConfigTools.IsSkinId(skin.Id))
        {
            ShowError(Loc.T("✗ Identyfikator „{0}” musi składać się z małych liter, cyfr i myślników (np. „solarized-dark”).", skin.Id));
            return;
        }

        _valid = skin;
        var warnings = SkinValidator.Validate(skin);
        SetStatus(
            Loc.T("✓ „{0}” ({1}) jest poprawna.", skin.DisplayName, skin.Id) +
            (warnings.Count == 0 ? " " + Loc.T("Kontrast i czytelność w porządku.") : "\n⚠ " + string.Join("\n⚠ ", warnings)),
            warnings.Count == 0 ? Brushes.FileForeground : Brushes.Warning);
        if (preview)
        {
            _skins.Preview(skin);
        }
    }

    private async Task CopyPromptAsync()
    {
        Validate(preview: false);
        await SkinPickerDialog.CopyToClipboardAsync(this, SkinPrompt.ForChat(null, _valid ?? SelectedSkin));
        SetStatus(Loc.T("Skopiowano prompt ze schematem JSON i bieżącą skórką. Wklej go w Claude.ai lub Claude Design, ") +
            Loc.T("a odpowiedź wklej tutaj przyciskiem „Wklej ze schowka”."), Brushes.FileForeground);
    }

    private async Task SaveAsync()
    {
        Validate(preview: false);
        if (_valid is not { } skin)
        {
            return;
        }

        var path = Path.Combine(_host.Config.SkinsDirectory, skin.Id + ".json");
        var builtIn = BuiltInSkins.All.Any(b => b.Id.Equals(skin.Id, StringComparison.OrdinalIgnoreCase));
        var question = builtIn
            ? Loc.T("„{0}” to wbudowana skórka. Zapisać własną wersję, która ją zastąpi?", skin.Id)
            : File.Exists(path) ? Loc.T("Plik {0} już istnieje. Zastąpić go?", path) : null;
        if (question is not null && !await new ConfirmDialog(Brushes, Loc.T("Zapisz skórkę"), question, Loc.T("Zapisz")).ShowAsync(this))
        {
            return;
        }

        try
        {
            await SkinCatalog.ExportAsync(skin, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Loc.T("Nie można zapisać {0}: {1}", path, ex.Message));
            return;
        }

        await _host.ReloadSkinsAsync();
        _skins.Apply(_host.Skins.Get(skin.Id));
        Complete(skin);
    }

    private void Discard()
    {
        _skins.Preview(_original);
        Complete(null);
    }

    private void ShowError(string text) => SetStatus(text, Brushes.Error);

    private void SetStatus(string text, IBrush foreground)
    {
        _status.Text = text;
        _status.Foreground = foreground;
    }
}
