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
    public static string Columns => Loc.T("Kolumny");
    public static string Network => Loc.T("Sieć");
    public static string Preview => Loc.T("Podgląd");
    public static string Updates => Loc.T("Aktualizacje");
    public static string Browser => Loc.T("Przeglądarka");
    public static string Language => Loc.T("Język");
    public static string Ai => Loc.T("Sztuczna inteligencja");
    public static string Mcp => Loc.T("MCP (AI z zewnątrz)");

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

        new("splashScreen", Appearance, Loc.T("Animacja przy starcie"),
            Loc.T("Przy uruchomieniu pokazuje animowany ekran startowy, dopóki aplikacja się nie wczyta (Esc lub kliknięcie go pomija). Tę samą animację można obejrzeć w „O programie”."),
            SettingKind.Toggle, s => s.SplashScreen, (s, v) => s.SplashScreen = (bool)v!, ["splash", "start", "startup", "animacja", "animation", "ekran startowy", "intro"]),

        new("density", Appearance, Loc.T("Gęstość listy"), Loc.T("Kompaktowa: 12 px, wiersz 18 px · domyślna: wg skórki · wygodna: 14 px, wiersz 24 px."),
            SettingKind.Choice, s => s.Density, (s, v) => s.Density = (string)v!, ["density", "gestosc", "wiersz", "rozmiar", "czcionka", "kompaktowa", "wygodna"])
        {
            Choices = () => [new("compact", Loc.T("Kompaktowa")), new("default", Loc.T("Domyślna")), new("comfortable", Loc.T("Wygodna"))],
        },

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
        new("folderSuggestions", Panels, Loc.T("Podpowiedzi widoku folderu"),
            Loc.T("Gdy kursor zatrzyma się na folderze, którego zawartość pasuje do podglądu (np. zdjęcia → galeria), pokazuje propozycję przełączenia. Akceptacja zapisuje c4k.dir."),
            SettingKind.Toggle, s => s.FolderSuggestions, (s, v) => s.FolderSuggestions = (bool)v!, ["podpowiedzi", "sugestie", "galeria", "widok folderu", "suggestions", "gallery", "c4k.dir"]),
        new("columnWidths", Columns, Loc.T("Szerokości kolumn"),
            Loc.T("Przeciągnij krawędź kolumny w nagłówku listy plików, aby zmienić jej szerokość; podwójne kliknięcie krawędzi przywraca domyślną. Tutaj zapisane szerokości (np. size=96, date=140); puste pole przywraca wszystkie domyślne."),
            SettingKind.Text, s => FormatColumnWidths(s.ColumnWidths), (s, v) => ParseColumnWidths(v as string, s.ColumnWidths),
            ["szerokosc", "width", "kolumna", "column", "rozmiar", "data", "naglowek", "header", "resize"]),

        new("markdownEdit", Preview, Loc.T("Markdown: od razu edycja"), Loc.T("Podgląd (Ctrl+Q) otwiera pliki .md w edycji z podglądem na żywo obok, zamiast samego widoku sformatowanego (F4 przełącza)."),
            SettingKind.Toggle, s => s.MarkdownEditByDefault, (s, v) => s.MarkdownEditByDefault = (bool)v!, ["markdown", "md", "edytor", Loc.T("podgląd"), "quick view"]),
        new("syntaxHighlighting", Preview, Loc.T("Kolorowanie składni w podglądzie"), Loc.T("Podgląd (Ctrl+Q) koloruje pliki tekstowe gramatykami TextMate (jak VS Code): kod, JSON, YAML, konfiguracje i inne. Wtyczka podglądu przypisana do pliku ma pierwszeństwo. Pliki dłuższe niż kilka tysięcy linii pokazywane są dalej bez kolorów."),
            SettingKind.Toggle, s => s.SyntaxHighlighting, (s, v) => s.SyntaxHighlighting = (bool)v!, ["kolory", "skladnia", "syntax", "highlight", "textmate", "kod", "code", "podswietlanie", Loc.T("podgląd")]),
        new("folderConfigs", Preview, Loc.T("Pliki c4k.dir w folderach"), Loc.T("Plik c4k.dir w folderze decyduje, co pokazuje jego podgląd i co się dzieje po wejściu: galeria, kafelki, notatki, wtyczka (np. gry Steam), połączenie SSH z kluczem albo skrypt (uruchamiany dopiero po Twojej zgodzie). Shift+Enter wchodzi zawsze do zwykłej listy plików."),
            SettingKind.Toggle, s => s.FolderConfigs, (s, v) => s.FolderConfigs = (bool)v!, ["c4k.dir", "folder", "widok", "galeria", "dashboard", "kafelki", "steam", "skrypt", "ssh"]),
        new("pluginBackground", Preview, Loc.T("Wtyczki mogą działać w tle"),
            Loc.T("Zatwierdzone wtyczki z uprawnieniem „praca w tle” (np. poczta) są wywoływane co jakiś czas także wtedy, gdy ich panel jest zamknięty. Wyłączone: nic nie działa w tle, wtyczki odświeżają się tylko po otwarciu."),
            SettingKind.Toggle, s => s.PluginBackground, (s, v) => s.PluginBackground = (bool)v!, ["tło", "synchronizacja", "poczta", "wtyczki", "background", "sync", "mail"]),
        new("pluginCatalogUrl", Preview, Loc.T("Katalog oficjalnych wtyczek (adres)"), Loc.T("Plik catalog.json pobierany przy starcie z repozytorium projektu: wtyczki do instalacji jednym kliknięciem i do użycia w plikach c4k.dir."),
            SettingKind.Text, s => s.PluginCatalogUrl, (s, v) => s.PluginCatalogUrl = NullIfEmpty(v) ?? AppSettings.DefaultPluginCatalogUrl,
            ["wtyczki", "katalog", "pluginy", "repozytorium", "pobierz", "catalog"]),

        new("lanDiscovery", Network, Loc.T("Wykrywaj hosty w sieci LAN"), Loc.T("Przy otwarciu sekcji LAN skanuje porty SMB/SSH/FTP oraz odczytuje ogłoszenia DNS-SD mDNS usług HTTP, API, SMB i innych."),
            SettingKind.Toggle, s => s.LanDiscovery, (s, v) => s.LanDiscovery = (bool)v!, ["skan", "lan", "smb", "ssh", "otoczenie"]),
        new("lanScanPopularPorts", Network, Loc.T("Skan LAN: popularne porty"), Loc.T("Włącza sprawdzanie typowych portów WWW, Winbox, API MikroTik i innych w lokalnych podsieciach IPv4 (maksymalnie /24). Możesz też zmienić tę opcję przy akcji „Skanuj adresy IP”."),
            SettingKind.Toggle, s => s.LanScanPopularPorts, (s, v) => s.LanScanPopularPorts = (bool)v!, ["lan", "porty", "skan", "mikrotik", "winbox", "uslugi"]),

        new("searchEngine", Browser, Loc.T("Wyszukiwarka w przeglądarce"), Loc.T("Co dzieje się z tekstem wpisanym w pasek adresu wewnętrznej przeglądarki (Ctrl+Alt+W), gdy nie jest adresem."),
            SettingKind.Choice, s => s.SearchEngine, (s, v) => s.SearchEngine = (string)v!, ["google", "bing", "duckduckgo", "szukaj", "www", "internet"])
        { Choices = () => [.. Web.SearchEngines.All.Select(e => new SettingChoice(e.Id, e.Name))] },
        new("assistantEngine", Browser, Loc.T("Asystent AI do szukania rozwiązań"), Loc.T("Kogo pyta „Szukaj rozwiązania” (menedżer wtyczek) i wyszukiwania asystenta z wtyczek. Zaloguj się raz w przeglądarce — sesja zostaje zapamiętana."),
            SettingKind.Choice, s => s.AssistantEngine, (s, v) => s.AssistantEngine = (string)v!, ["ai", "chatgpt", "gemini", "claude", "perplexity", "asystent", "pomoc"])
        { Choices = () => [.. Web.SearchEngines.All.OrderBy(e => !e.IsAssistant).Select(e => new SettingChoice(e.Id, e.IsAssistant ? e.Name : e.Name + Loc.T(" (wyszukiwarka)")))] },

        new("aiInApp", Ai, Loc.T("Odpowiedzi AI w aplikacji"), Loc.T("Pytania o plik i prośby o wtyczkę z podglądu odpowiadają w oknie czatu przez zalogowanych dostawców (OpenRouter, Groq, Cerebras…), przechodząc do następnego, gdy któryś wyczerpie limit. Wyłączone lub bez logowania: otwiera okno Claude Code (pytanie trafia do schowka)."),
            SettingKind.Toggle, s => s.AiInApp, (s, v) => s.AiInApp = (bool)v!, ["ai", "czat", "chat", "openrouter", "groq", "cerebras", "gemini", "mistral", "dostawcy", "darmowe", "rotacja", "limit"]),
        new("aiCatalogUrl", Ai, Loc.T("Lista dostawców AI (adres)"), Loc.T("Plik ai-providers.json pobierany przy starcie z repozytorium projektu, więc nowe darmowe modele pojawiają się bez aktualizacji programu. Własne wpisy: config/ai/providers.json."),
            SettingKind.Text, s => s.AiCatalogUrl, (s, v) => s.AiCatalogUrl = NullIfEmpty(v) ?? AppSettings.DefaultAiCatalogUrl,
            ["ai", "dostawcy", "lista", "repozytorium", "modele", "providers"]),

        new("mcp.enabled", Mcp, Loc.T("Serwer MCP dla Claude Code"), Loc.T("Udostępnia aplikację przez Model Context Protocol na lokalnym porcie (tylko 127.0.0.1, z tokenem), żeby Claude Code mógł przeglądać pliki, pisać wtyczki i sterować panelami. Dostęp ogranicza lista folderów poniżej, a każde narzędzie ma własny tryb: zezwalaj, pytaj albo zablokuj."),
            SettingKind.Toggle, s => s.Mcp.Enabled, (s, v) => s.Mcp.Enabled = (bool)v!, McpKeywords),
        new("mcp.port", Mcp, Loc.T("Port serwera MCP"), Loc.T("Port na 127.0.0.1 (1024–65535). Po zmianie podłącz Claude Code ponownie."),
            SettingKind.Text, s => s.Mcp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (s, v) =>
            {
                if (v is string text && int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1024 and <= 65535)
                {
                    s.Mcp.Port = port;
                }
            }, McpKeywords),
        new("mcp.roots", Mcp, Loc.T("Foldery dostępne dla MCP"), Loc.T("Foldery (dowolna ścieżka: dysk, udział sieciowy, sftp://…), do których AI może zaglądać i w których może pisać; kilka oddziel średnikiem. Pusta lista: żaden."),
            SettingKind.Text, s => string.Join(';', s.Mcp.AllowedRoots),
            (s, v) => s.Mcp.AllowedRoots = [.. (v as string ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)],
            McpKeywords),

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

    private static readonly string[] McpKeywords = ["mcp", "claude", "claude code", "ai", "serwer", "port", "foldery", "dostęp"];

    private static string? NullIfEmpty(object? value) => value is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    /// <summary><c>key=width</c> pairs separated by commas, as shown in the settings window.</summary>
    public static string FormatColumnWidths(IReadOnlyDictionary<string, double> widths) =>
        string.Join(", ", widths.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase).Select(w => w.Key + "=" + w.Value.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Replaces <paramref name="widths"/> in place (the panels hold that dictionary); malformed pairs are skipped.</summary>
    public static void ParseColumnWidths(string? text, IDictionary<string, double> widths)
    {
        widths.Clear();
        foreach (var pair in (text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts is [{ Length: > 0 } key, var value] && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) && width > 0)
            {
                widths[key] = width;
            }
        }
    }
}
