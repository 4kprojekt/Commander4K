using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Composition;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;

namespace Commander4K.App.Dialogs;

/// <summary>
/// Adds a UI language without code: copies a prompt (with the English file as reference) for any AI chat, opens
/// <c>config/i18n</c> for the answer and checks the files there (placeholders, plural forms, missing entries).
/// </summary>
public sealed class TranslationDialog : DialogWindow<bool>
{
    private readonly AppHost _host;
    private readonly TextBox _code;
    private readonly TextBlock _languageName;
    private readonly SelectableTextBlock _report;

    public TranslationDialog(AppHost host, SkinBrushes brushes) : base(Loc.T("Przetłumacz interfejs z AI"), brushes, 640)
    {
        _host = host;
        var suggestion = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is var windows && windows is not ("pl" or "en") ? windows : "de";
        _code = new TextBox().Text(suggestion).Width(90);
        _languageName = new TextBlock().VerticalAlignment(VerticalAlignment.Center).Margin(8, 0, 0, 0);
        _code.TextChanged += (_, _) => _languageName.Text = AppHost.LanguageName(Code);
        _languageName.Text = AppHost.LanguageName(Code);
        _report = new SelectableTextBlock().TextWrapping(TextWrapping.Wrap).FontFamily(FontFamily.Parse("Cascadia Mono, Consolas")).FontSize(12);

        SetBody(
            Label(Loc.T("Obecny język: {0}. Teksty interfejsu są w plikach JSON — jeden plik na język. Brakujące teksty pokazują się w oryginale, więc niepełne tłumaczenie niczego nie psuje.",
                AppHost.LanguageName(Loc.Language))),
            Label(Loc.T("1. Wpisz kod języka (np. de, uk, es, fr, cs) i skopiuj prompt.\n2. Wklej go w Claude.ai, ChatGPT lub Gemini — odpowiedzią jest gotowy plik JSON.\n3. Zapisz go jako config/i18n/<kod>.json, sprawdź poniżej i wybierz język w ustawieniach (F1 → „Język”), potem uruchom aplikację ponownie.")),
            new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Margin(0, 8).Children(
                new TextBlock().Text(Loc.T("Kod języka:")).VerticalAlignment(VerticalAlignment.Center),
                _code,
                _languageName),
            new WrapPanel().Children(
                Action(Loc.T("📋 Kopiuj prompt dla AI"), CopyPromptAsync),
                Action(Loc.T("📂 Folder tłumaczeń"), () =>
                {
                    SkinPickerDialog.OpenFolder(Folder);
                    return Task.CompletedTask;
                }),
                Action(Loc.T("✔ Sprawdź pliki"), () =>
                {
                    _report.Text = TranslationKit.Report(_host.Config.ConfigDirectory);
                    return Task.CompletedTask;
                })),
            new ScrollViewer().MaxHeight(220).Content(_report),
            ButtonRow((Loc.T("Zamknij"), () => Complete(true), true)));
        _report.Text = TranslationKit.Report(_host.Config.ConfigDirectory);
    }

    private string Code => (_code.Text ?? string.Empty).Trim().ToLowerInvariant();

    private string Folder => Path.Combine(_host.Config.ConfigDirectory, Loc.FolderName);

    private async Task CopyPromptAsync()
    {
        if (Code.Length is < 2 or > 10)
        {
            _report.Text = Loc.T("Wpisz kod języka, np. de albo uk.");
            return;
        }

        await SkinPickerDialog.CopyToClipboardAsync(this, TranslationKit.PromptFor(Code, AppHost.LanguageName(Code)));
        Directory.CreateDirectory(Folder);
        _report.Text = Loc.T("Skopiowano prompt ({0}). Odpowiedź AI zapisz jako {1}.", AppHost.LanguageName(Code), Path.Combine(Folder, Code + ".json"));
    }

    private static Button Action(string text, Func<Task> onClick) =>
        new Button().Content(text).Margin(0, 0, 6, 6).OnClick(async _ => await onClick());
}
