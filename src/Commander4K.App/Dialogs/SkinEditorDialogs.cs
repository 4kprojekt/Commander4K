using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Theming;

namespace Commander4K.App.Dialogs;

/// <summary>
/// "Create with AI" without an API key: the user describes the skin, the dialog copies a prompt (idea + schema + a strict
/// "JSON only" contract), the user pastes it into any assistant and pastes the answer back here. The answer is trimmed
/// to its JSON object and validated; a valid skin completes the dialog.
/// </summary>
public sealed class SkinAiPromptDialog : DialogWindow<Skin?>
{
    private readonly SkinManager _skins;
    private readonly TextBox _description;
    private readonly TextBox _answer;
    private readonly TextBlock _status;
    private readonly StackPanel _step2;

    public SkinAiPromptDialog(SkinManager skins) : base(Loc.T("Stwórz nową skórkę z AI"), skins.Current, 640)
    {
        _skins = skins;
        _description = new TextBox().AcceptsReturn(true).TextWrapping(TextWrapping.Wrap).Height(90)
            .PlaceholderText(Loc.T("np. „cyberpunk: neonowy róż i cyjan na granatowym tle” albo „sepia jak stary terminal”"));
        _answer = new TextBox().AcceptsReturn(true).TextWrapping(TextWrapping.NoWrap).Height(150)
            .FontFamily(Brushes.MonospaceFamily).FontSize(12)
            .PlaceholderText(Loc.T("Wklej tutaj odpowiedź asystenta — wystarczy cały tekst, JSON zostanie wykryty…"));
        _answer.TextChanged += (_, _) => Check();
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12);
        _step2 = new StackPanel().Spacing(6).IsVisible(false).Children(
            new TextBlock().TextWrapping(TextWrapping.Wrap).FontWeight(FontWeight.SemiBold)
                .Text(Loc.T("Prompt jest w schowku. Wklej go do ulubionego asystenta (Gemini, ChatGPT, Claude…), a jego odpowiedź wklej poniżej.")),
            _answer,
            new Button().Content(Loc.T("📋 Wklej ze schowka")).OnClick(async _ => await PasteAsync()),
            _status);

        SetBody(
            Label(Loc.T("Opisz, jak ma wyglądać skórka:")),
            _description,
            new Button().Content(Loc.T("📋 Przygotuj i skopiuj prompt")).Margin(0, 6, 0, 0).OnClick(async _ => await PrepareAsync()),
            _step2,
            ButtonRow((Loc.T("Anuluj"), () => Complete(null), false)));
        _skins.Changed += ApplySkin;
        Closed += (_, _) => _skins.Changed -= ApplySkin;
        Opened += (_, _) => _description.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Complete(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ApplySkin(SkinBrushes brushes) => WindowSkin.Apply(this, brushes, raisedSurface: true);

    private async Task PrepareAsync()
    {
        await SkinPickerDialog.CopyToClipboardAsync(this, SkinPrompt.ForJsonOnly(_description.Text));
        _step2.IsVisible = true;
        SetStatus(Loc.T("Czekam na odpowiedź…"), Brushes.MutedForeground);
        _answer.Focus();
    }

    private async Task PasteAsync()
    {
        if (Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
        {
            _answer.Text = text;
        }
        else
        {
            SetStatus(Loc.T("Schowek nie zawiera tekstu."), Brushes.Error);
        }
    }

    private void Check()
    {
        if (string.IsNullOrWhiteSpace(_answer.Text))
        {
            return;
        }

        if (SkinJson.TryRead(_answer.Text, fixId: true, out var skin, out var error))
        {
            _skins.Preview(skin);
            Complete(skin);
        }
        else
        {
            SetStatus(error, Brushes.Error);
        }
    }

    private void SetStatus(string text, IBrush foreground)
    {
        _status.Text = text;
        _status.Foreground = foreground;
    }
}

/// <summary>
/// Skin editor: a form generated from the skin JSON Schema with a live preview. A skin can be picked, copied as the base
/// of a new one or pasted from the clipboard (JSON found inside any text); saving writes it into config/skins.
/// </summary>
public sealed class SkinEditorDialog : DialogWindow<Skin?>
{
    private readonly AppHost _host;
    private readonly SkinManager _skins;
    private readonly Skin _original;
    private readonly ComboBox _source;
    private readonly SkinForm _form;
    private readonly TextBlock _status;
    private bool _switching;
    private bool _ready;
    private Skin? _pending;
    private readonly DispatcherTimer _previewTimer;
    private readonly WaveDots _busy;
    private readonly StackPanel _loadingRow;
    private readonly ScrollViewer _scroll;
    private Skin? _valid;

    public SkinEditorDialog(AppHost host, SkinManager skins, Skin? start = null, bool asDraft = false) : base(Loc.T("Edytor skórki"), skins.Current, 760)
    {
        _host = host;
        _skins = skins;
        _original = skins.Current.Skin;
        start ??= _original;
        if (asDraft)
        {
            start = start with { Id = UniqueId(start.Id) };
        }

        var items = host.Skins.Skins.Select(s => new ComboBoxItem { Content = s.DisplayName + " (" + s.Id + ")", Tag = s }).ToList();
        if (asDraft || items.All(i => !((Skin)i.Tag!).Id.Equals(start.Id, StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(0, new ComboBoxItem { Content = Loc.T("(nowa) {0}", start.DisplayName), Tag = start });
        }

        _source = new ComboBox().MinWidth(260).ItemsSource(items);
        _source.SelectionChanged += (_, _) =>
        {
            if (_ready && !_switching && SelectedSkin is { } skin)
            {
                LoadSkin(skin);
            }
        };
        _form = new SkinForm(SkinSchema.Create(), Brushes.PanelBorder, () => ScheduleRevalidate());
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).Margin(0, 8, 0, 0);
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, (_, _) =>
        {
            _previewTimer!.Stop();
            Revalidate(preview: true);
        });
        _busy = new WaveDots { Foreground = Brushes.FileForeground, DotSize = 5, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _loadingRow = new StackPanel().Orientation(Orientation.Horizontal).Margin(0, 14).Children(
            _busy,
            new TextBlock().Text(Loc.T("Budowanie formularza…")).Foreground(Brushes.MutedForeground).VerticalAlignment(VerticalAlignment.Center));
        _scroll = new ScrollViewer().MaxHeight(460).Padding(0, 0, 12, 0).IsVisible(false).Content(_form.Root);

        SetBody(
            new WrapPanel().Children(
                Label(Loc.T("Skórka:")).VerticalAlignment(VerticalAlignment.Center).Margin(0, 0, 8, 6),
                _source.Margin(0, 0, 6, 6),
                Action(Loc.T("➕ Nowa na podstawie"), NewFromSelected),
                Action(Loc.T("📋 Wklej ze schowka"), PasteAsync),
                Action(Loc.T("📂 Otwórz folder skórek"), () => FolderOpener.OpenAsync(this, Brushes, host.Config.SkinsDirectory))),
            _loadingRow,
            _scroll,
            _status,
            ButtonRow((Loc.T("💾 Zapisz i użyj"), () => _ = SaveAsync(), true), (Loc.T("Zamknij"), Discard, false)));

        _switching = true;
        _source.SelectedItem = items.FirstOrDefault(i => ((Skin)i.Tag!).Id.Equals(start.Id, StringComparison.OrdinalIgnoreCase));
        _switching = false;
        _pending = start;
        _form.Load(JsonNode.Parse(SkinJson.Serialize(start))!.AsObject());
        _skins.Changed += ApplySkin;
        Closed += (_, _) =>
        {
            _skins.Changed -= ApplySkin;
            _previewTimer.Stop();
        };
        Opened += async (_, _) => await BuildFormAsync();
    }

    /// <summary>The window is already on screen (wave dots) while the controls are created in small batches.</summary>
    private async Task BuildFormAsync()
    {
        await _form.BuildAsync();
        _loadingRow.IsVisible = false;
        _scroll.IsVisible = true;
        _ready = true;
        if (_pending is { } skin)
        {
            _pending = null;
            LoadSkin(skin);
        }
    }

    private void ScheduleRevalidate()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
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

    private void ApplySkin(SkinBrushes brushes) => WindowSkin.Apply(this, brushes, raisedSurface: true);

    private static Button Action(string text, Action onClick) =>
        new Button().Content(text).Margin(0, 0, 6, 6).OnClick(_ => onClick());

    private static Button Action(string text, Func<Task> onClick) =>
        new Button().Content(text).Margin(0, 0, 6, 6).OnClick(async _ => await onClick());

    private void LoadSkin(Skin skin)
    {
        _form.Load(JsonNode.Parse(SkinJson.Serialize(skin))!.AsObject());
        if (_ready)
        {
            Revalidate(preview: true);
        }
        else
        {
            _pending = skin;
        }
    }

    private void NewFromSelected()
    {
        var skin = _valid ?? SelectedSkin;
        if (skin is null)
        {
            return;
        }

        LoadSkin(skin with { Id = UniqueId(skin.Id + "-wlasna"), DisplayName = skin.DisplayName + Loc.T(" (kopia)") });
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

    private async Task PasteAsync()
    {
        if (Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } text)
        {
            SetStatus(Loc.T("Schowek nie zawiera tekstu."), Brushes.Error);
            return;
        }

        if (SkinJson.TryRead(text, fixId: true, out var skin, out var error))
        {
            LoadSkin(skin);
        }
        else
        {
            SetStatus(error, Brushes.Error);
        }
    }

    /// <summary>Builds the skin from the form; on success shows contrast warnings and, with <paramref name="preview"/>, paints it live.</summary>
    private void Revalidate(bool preview)
    {
        if (!_ready)
        {
            return;
        }

        _previewTimer.Stop();
        if (!SkinJson.TryRead(_form.ToJson().ToJsonString(), fixId: false, out var skin, out var error))
        {
            _valid = null;
            SetStatus(error, Brushes.Error);
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

    private async Task SaveAsync()
    {
        Revalidate(preview: false);
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
            SetStatus(Loc.T("Nie można zapisać {0}: {1}", path, ex.Message), Brushes.Error);
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

    private void SetStatus(string text, IBrush foreground)
    {
        _status.Text = text;
        _status.Foreground = foreground;
    }
}
