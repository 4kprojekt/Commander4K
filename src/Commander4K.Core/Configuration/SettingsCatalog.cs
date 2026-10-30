using System.Globalization;
using System.Text;

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
    public const string Appearance = "Wygląd";
    public const string Panels = "Panele";
    public const string Network = "Sieć";
    public const string Updates = "Aktualizacje";

    public static IReadOnlyList<SettingDescriptor> Create(Func<IReadOnlyList<SettingChoice>> skinChoices) =>
    [
        new("skin", Appearance, "Skórka", "Kolorystyka i styl całej aplikacji (Norton Commander, Midnight Commander, nowoczesna…).",
            SettingKind.Choice, s => s.Skin, (s, v) => s.Skin = (string)v!, ["motyw", "kolory", "theme", "wygląd"])
        { Choices = skinChoices },

        new("showHidden", Panels, "Pokazuj pliki ukryte i systemowe", "Gdy wyłączone, pliki z atrybutem ukryty/systemowy nie pojawiają się w panelach (Ctrl+H).",
            SettingKind.Toggle, s => s.ShowHidden, (s, v) => s.ShowHidden = (bool)v!, ["ukryte", "hidden", "systemowe", "atrybuty"]),
        new("confirmDelete", Panels, "Pytaj przed usunięciem", "Okno potwierdzenia przed usunięciem plików i katalogów (F8).",
            SettingKind.Toggle, s => s.ConfirmDelete, (s, v) => s.ConfirmDelete = (bool)v!, ["kasowanie", "delete", "potwierdzenie"]),
        new("leftPath", Panels, "Folder startowy lewego panelu", "Ścieżka otwierana przy starcie; zapisywana automatycznie przy zamknięciu.",
            SettingKind.Text, s => s.LeftPath ?? string.Empty, (s, v) => s.LeftPath = NullIfEmpty(v), ["start", "ścieżka", "katalog"]),
        new("rightPath", Panels, "Folder startowy prawego panelu", "Ścieżka otwierana przy starcie; zapisywana automatycznie przy zamknięciu.",
            SettingKind.Text, s => s.RightPath ?? string.Empty, (s, v) => s.RightPath = NullIfEmpty(v), ["start", "ścieżka", "katalog"]),

        new("lanDiscovery", Network, "Wykrywaj hosty w sieci LAN", "Przy otwarciu net:// skanuje lokalną podsieć (porty 445, 22, 21) i pokazuje znalezione maszyny.",
            SettingKind.Toggle, s => s.LanDiscovery, (s, v) => s.LanDiscovery = (bool)v!, ["skan", "lan", "smb", "ssh", "otoczenie"]),

        new("checkForUpdates", Updates, "Sprawdzaj aktualizacje automatycznie", "Raz na dobę sprawdza wydania na GitHubie i informuje o nowej wersji.",
            SettingKind.Toggle, s => s.CheckForUpdates, (s, v) => s.CheckForUpdates = (bool)v!, ["update", "wersja", "nowa"]),
        new("includePrereleases", Updates, "Wersje testowe (pre-release)", "Proponuje także wydania oznaczone jako testowe.",
            SettingKind.Toggle, s => s.IncludePrereleases, (s, v) => s.IncludePrereleases = (bool)v!, ["beta", "preview", "testowe"]),
        new("updateRepository", Updates, "Źródło aktualizacji (GitHub)", "Repozytorium owner/nazwa, z którego pobierane są wydania.",
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
