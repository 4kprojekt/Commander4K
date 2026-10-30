using System.Globalization;
using System.Text;
using Commander4K.Core.Localization;

namespace Commander4K.Core.Configuration;

public enum SettingKind : byte
{
    Toggle,
    Text,
    Choice,
}

public sealed record SettingChoice(string Value, string Label);

/// <summary>
/// One user-facing setting: what the settings window renders and what search matches against. Getters/setters
/// operate on <see cref="AppSettings"/>, which stays the single source of truth persisted as JSON.
/// </summary>
public sealed record SettingDescriptor(
    string Key,
    string Category,
    string Title,
    string Description,
    SettingKind Kind,
    Func<AppSettings, object?> Get,
    Action<AppSettings, object?> Set,
    IReadOnlyList<string>? Keywords = null)
{
    /// <summary>Options for <see cref="SettingKind.Choice"/>; resolved lazily because some depend on runtime data (skins).</summary>
    public Func<IReadOnlyList<SettingChoice>>? Choices { get; init; }

    /// <summary>Settings whose effect needs a restart say so in the UI.</summary>
    public bool RequiresRestart { get; init; }
}

/// <summary>Registry of all settings plus accent- and case-insensitive search ("zrodlo" finds "Źródło").</summary>
public static class SettingsCatalog
{
    // Category names are display texts (translated), so they cannot be constants.
    public static string Appearance => Loc.T("Wygląd");
    public static string Panels => Loc.T("Panele");
    public static string Palettes => Loc.T("Palety");
    public static string Columns => Loc.T("Kolumny");
    public static string Network => Loc.T("Sieć");
    public static string Preview => Loc.T("Podgląd");
    public static string Updates => Loc.T("Aktualizacje");
    public static string Browser => Loc.T("Przeglądarka");
    public static string Language => Loc.T("Język");

    public static IReadOnlyList<SettingDescriptor> Create(Func<IReadOnlyList<SettingChoice>> skinChoices, Func<IReadOnlyList<SettingChoice>>? languageChoices = null) =>
    [
        new("language", Language, Loc.T("Język interfejsu"),
            Loc.T("„Automatycznie” wybiera język Windows, jeśli jest tłumaczenie (inaczej angielski). Własne tłumaczenia: pliki config/i18n/<kod>.json — komenda „Przetłumacz interfejs z AI” przygotowuje prompt."),
            SettingKind.Choice, s => s.Language, (s, v) => s.Language = (string)v!, ["language", "jezyk", "tlumaczenie", "translation", "english", "polski"])
        {
            Choices = languageChoices ?? (() => [new(Loc.Auto, Loc.T("Automatycznie")), new("pl", "Polski"), new("en", "English")]),
            RequiresRestart = true,
        },

        new("skin", Appearance, Loc.T("Skórka"), Loc.T("Kolorystyka i styl całej aplikacji (Norton Commander, Midnight Commander, nowoczesna…)."),
            SettingKind.Choice, s => s.Skin, (s, v) => s.Skin = (string)v!, ["motyw", "kolory", "theme", Loc.T("wygląd")])
        { Choices = skinChoices },

        new("showHidden", Panels, Loc.T("Pokazuj pliki ukryte i systemowe"), Loc.T("Gdy wyłączone, pliki z atrybutem ukryty/systemowy nie pojawiają się w panelach (Ctrl+H)."),
            SettingKind.Toggle, s => s.ShowHidden, (s, v) => s.ShowHidden = (bool)v!, ["ukryte", "hidden", "systemowe", "atrybuty"]),
        new("confirmDelete", Panels, Loc.T("Pytaj przed usunięciem"), Loc.T("Okno potwierdzenia przed usunięciem plików i katalogów (F8)."),
            SettingKind.Toggle, s => s.ConfirmDelete, (s, v) => s.ConfirmDelete = (bool)v!, ["kasowanie", "delete", "potwierdzenie"]),
        new("leftPath", Panels, Loc.T("Folder startowy lewego panelu"), Loc.T("Ścieżka otwierana przy starcie; zapisywana automatycznie przy zamknięciu."),
            SettingKind.Text, s => s.LeftPath ?? string.Empty, (s, v) => s.LeftPath = NullIfEmpty(v), ["start", Loc.T("ścieżka"), "katalog"]),
        new("rightPath", Panels, Loc.T("Folder startowy prawego panelu"), Loc.T("Ścieżka otwierana przy starcie; zapisywana automatycznie przy zamknięciu."),
            SettingKind.Text, s => s.RightPath ?? string.Empty, (s, v) => s.RightPath = NullIfEmpty(v), ["start", Loc.T("ścieżka"), "katalog"]),

        new("shellContextMenu", Panels, Loc.T("Prawy przycisk: menu Windows"),
            Loc.T("Prawy przycisk myszy pokazuje menu kontekstowe Eksploratora (Otwórz za pomocą, Wyślij do, Właściwości…). Wyłączone: prawy przycisk zaznacza pliki jak w Total Commanderze. Z klawiatury: Shift+F10 lub klawisz menu."),
            SettingKind.Toggle, s => s.ShellContextMenu, (s, v) => s.ShellContextMenu = (bool)v!, ["kontekstowe", "context", "explorer", "eksplorator", "mysz", "mouse", "prawy"]),

        new("showPalettes", Palettes, Loc.T("Pokazuj palety"), Loc.T("Palety z szybkimi akcjami, ulubionymi folderami, zapisanymi widokami i palety wtyczek (Ctrl+B). Położenie każdej palety ustawisz poniżej."),
            SettingKind.Toggle, s => s.ShowPalettes, (s, v) => s.ShowPalettes = (bool)v!, [Loc.T("paleta"), "pasek", "przyciski", "ulubione", "widoki", "toolbar"]),

        new("markdownEdit", Preview, Loc.T("Markdown: od razu edycja"), Loc.T("Podgląd (Ctrl+Q) otwiera pliki .md w edycji z podglądem na żywo obok, zamiast samego widoku sformatowanego (F4 przełącza)."),
            SettingKind.Toggle, s => s.MarkdownEditByDefault, (s, v) => s.MarkdownEditByDefault = (bool)v!, ["markdown", "md", "edytor", Loc.T("podgląd"), "quick view"]),

        new("lanDiscovery", Network, Loc.T("Wykrywaj hosty w sieci LAN"), Loc.T("Przy otwarciu net:// skanuje lokalną podsieć (porty 445, 22, 21) i pokazuje znalezione maszyny."),
            SettingKind.Toggle, s => s.LanDiscovery, (s, v) => s.LanDiscovery = (bool)v!, ["skan", "lan", "smb", "ssh", "otoczenie"]),

        new("searchEngine", Browser, Loc.T("Wyszukiwarka w przeglądarce"), Loc.T("Co dzieje się z tekstem wpisanym w pasek adresu wewnętrznej przeglądarki (Ctrl+Alt+W), gdy nie jest adresem."),
            SettingKind.Choice, s => s.SearchEngine, (s, v) => s.SearchEngine = (string)v!, ["google", "bing", "duckduckgo", "szukaj", "www", "internet"])
        { Choices = () => [.. Web.SearchEngines.All.Select(e => new SettingChoice(e.Id, e.Name))] },
        new("assistantEngine", Browser, Loc.T("Asystent AI do szukania rozwiązań"), Loc.T("Kogo pyta „Szukaj rozwiązania” (menedżer wtyczek) i komenda „Zapytaj AI” (Ctrl+Alt+A). Zaloguj się raz w przeglądarce — sesja zostaje zapamiętana."),
            SettingKind.Choice, s => s.AssistantEngine, (s, v) => s.AssistantEngine = (string)v!, ["ai", "chatgpt", "gemini", "claude", "perplexity", "asystent", "pomoc"])
        { Choices = () => [.. Web.SearchEngines.All.OrderBy(e => !e.IsAssistant).Select(e => new SettingChoice(e.Id, e.IsAssistant ? e.Name : e.Name + Loc.T(" (wyszukiwarka)")))] },

        new("checkForUpdates", Updates, Loc.T("Sprawdzaj aktualizacje automatycznie"), Loc.T("Raz na dobę sprawdza wydania na GitHubie i informuje o nowej wersji."),
            SettingKind.Toggle, s => s.CheckForUpdates, (s, v) => s.CheckForUpdates = (bool)v!, ["update", "wersja", "nowa"]),
        new("includePrereleases", Updates, Loc.T("Wersje testowe (pre-release)"), Loc.T("Proponuje także wydania oznaczone jako testowe."),
            SettingKind.Toggle, s => s.IncludePrereleases, (s, v) => s.IncludePrereleases = (bool)v!, ["beta", "preview", "testowe"]),
        new("updateRepository", Updates, Loc.T("Źródło aktualizacji (GitHub)"), Loc.T("Repozytorium owner/nazwa, z którego pobierane są wydania."),
            SettingKind.Text, s => s.UpdateRepository, (s, v) => s.UpdateRepository = NullIfEmpty(v) ?? AppSettings.DefaultUpdateRepository,
            ["github", "repozytorium", "release"]),
    ];

    /// <summary>
    /// Ranks entries whose title, category, description or keywords contain every word of the query.
    /// Title hits rank first. An empty query returns everything in catalogue order.
    /// </summary>
    public static IReadOnlyList<T> Search<T>(IEnumerable<T> items, string? query, Func<T, string> title, Func<T, IEnumerable<string>> otherText)
    {
        var words = Normalize(query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [.. items];
        }

        return [.. items
            .Select((item, order) =>
            {
                var titleText = Normalize(title(item));
                var all = titleText + " " + Normalize(string.Join(' ', otherText(item)));
                if (!words.All(all.Contains))
                {
                    return (item, order, score: -1);
                }

                var score = words.Count(titleText.Contains) * 10 + (titleText.StartsWith(words[0], StringComparison.Ordinal) ? 5 : 0);
                return (item, order, score);
            })
            .Where(x => x.score >= 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.order)
            .Select(x => x.item)];
    }

    public static IReadOnlyList<SettingDescriptor> Search(IReadOnlyList<SettingDescriptor> settings, string? query) =>
        Search(settings, query, s => s.Title, s => [s.Category, s.Description, s.Key, .. s.Keywords ?? []]);

    /// <summary>Lower case without diacritics; Polish ł has no decomposition, so it is mapped explicitly.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace('ł', 'l').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string? NullIfEmpty(object? value) => value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
