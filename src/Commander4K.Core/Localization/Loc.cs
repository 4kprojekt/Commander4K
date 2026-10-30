using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Commander4K.Core.Localization;

/// <summary>
/// UI translations, gettext style: the Polish text in the code is the key, and a language file maps it to the
/// translation (<c>{"Kopiuj": "Copy"}</c>). Polish needs no file; anything missing from a file falls back to the
/// Polish original, so a partial translation never breaks the UI. Built-in files are embedded
/// (<c>Localization/i18n/*.json</c>); <c>config/i18n/&lt;code&gt;.json</c> adds languages or overrides entries.
/// Plurals are one entry whose forms are separated by <c>|</c> (Polish: one|few|many, English: one|other).
/// </summary>
public static class Loc
{
    public const string SourceLanguage = "pl";
    public const string Auto = "auto";
    public const string FolderName = "i18n";

    private static FrozenDictionary<string, string> _strings = FrozenDictionary<string, string>.Empty;

    /// <summary>Two-letter code of the active language (<c>pl</c> until <see cref="Initialize"/> runs).</summary>
    public static string Language { get; private set; } = SourceLanguage;

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(SourceLanguage);

    public static bool IsInitialized { get; private set; }

    /// <summary>Translates <paramref name="source"/> (Polish); unknown texts come back unchanged.</summary>
    public static string T(string source) => _strings.TryGetValue(source, out var translated) && translated.Length > 0 ? translated : source;

    /// <summary>Translates a composite format string and fills it; a broken translation falls back to the original.</summary>
    public static string T(string source, params object?[] args)
    {
        var format = T(source);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException) when (!ReferenceEquals(format, source))
        {
            return string.Format(CultureInfo.CurrentCulture, source, args);
        }
    }

    /// <summary>
    /// Plural: <paramref name="forms"/> is the Polish entry "one|few|many" (e.g. <c>"{0} plik|{0} pliki|{0} plików"</c>);
    /// the translation lists its own language's forms. <c>{0}</c> is <paramref name="count"/>, further arguments follow.
    /// </summary>
    public static string P(long count, string forms, params object?[] args)
    {
        var translated = T(forms);
        var parts = translated.Split('|');
        var language = ReferenceEquals(translated, forms) ? SourceLanguage : Language;
        var form = parts[Math.Min(PluralIndex(language, count), parts.Length - 1)];
        object?[] all = [count, .. args];
        try
        {
            return string.Format(CultureInfo.CurrentCulture, form, all);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, forms.Split('|')[Math.Min(PluralIndex(SourceLanguage, count), 2)], all);
        }
    }

    /// <summary>CLDR plural category index for the languages that need more than one|other.</summary>
    public static int PluralIndex(string language, long count)
    {
        var n = Math.Abs(count);
        switch (language)
        {
            case "pl":
                if (n == 1)
                {
                    return 0;
                }

                return n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? 1 : 2;
            case "cs" or "sk":
                return n == 1 ? 0 : n is >= 2 and <= 4 ? 1 : 2;
            case "uk" or "ru" or "be" or "hr" or "sr" or "bs":
                return n % 10 == 1 && n % 100 != 11 ? 0 : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? 1 : 2;
            case "ja" or "zh" or "ko" or "vi" or "th" or "id":
                return 0;
            case "fr" or "pt":
                return n <= 1 ? 0 : 1;
            default:
                return n == 1 ? 0 : 1;
        }
    }

    /// <summary>
    /// Picks the language (<paramref name="setting"/> code, or <see cref="Auto"/> = Windows display language when a
    /// translation exists, else English) and loads its entries: embedded first, then <paramref name="configDirectory"/>/i18n.
    /// </summary>
    public static void Initialize(string? setting, string? configDirectory)
    {
        var available = AvailableLanguages(configDirectory);
        var language = setting is null or "" or Auto
            ? (available.Contains(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : "en")
            : setting.ToLowerInvariant();
        Use(language, LoadEntries(language, configDirectory));
    }

    /// <summary>Activates entries directly (tests, live preview of a generated file).</summary>
    public static void Use(string language, IReadOnlyDictionary<string, string> entries)
    {
        Language = language;
        _strings = language == SourceLanguage && entries.Count == 0
            ? FrozenDictionary<string, string>.Empty
            : entries.ToFrozenDictionary(StringComparer.Ordinal);
        try
        {
            Culture = CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            Culture = CultureInfo.InvariantCulture;
        }

        IsInitialized = true;
    }

    /// <summary>Codes with a built-in or user file, plus Polish.</summary>
    public static IReadOnlyList<string> AvailableLanguages(string? configDirectory)
    {
        var codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { SourceLanguage };
        foreach (var name in typeof(Loc).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            codes.Add(Path.GetFileNameWithoutExtension(name[ResourcePrefix.Length..]));
        }

        if (configDirectory is not null && Directory.Exists(Path.Combine(configDirectory, FolderName)))
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(configDirectory, FolderName), "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                if (code.Length is >= 2 and <= 10 && !code.StartsWith('_'))
                {
                    codes.Add(code.ToLowerInvariant());
                }
            }
        }

        return [.. codes];
    }

    /// <summary>The embedded file of a language (the English one is the reference for new translations).</summary>
    public static string? BuiltInFile(string language)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream(ResourcePrefix + language + ".json");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Marks comment entries (file sections, notes); keys may start with "_" (menu access keys like "_Plik").</summary>
    public const string CommentPrefix = "_//";

    /// <summary>Parses a language file: a flat JSON object of strings (keys starting with <see cref="CommentPrefix"/> are comments).</summary>
    public static Dictionary<string, string> Parse(string json)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.StartsWith(CommentPrefix, StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.String)
            {
                entries[property.Name] = property.Value.GetString()!;
            }
        }

        return entries;
    }

    private const string ResourcePrefix = "i18n/";

    private static Dictionary<string, string> LoadEntries(string language, string? configDirectory)
    {
        var entries = BuiltInFile(language) is { } builtIn ? Parse(builtIn) : new Dictionary<string, string>(StringComparer.Ordinal);
        var user = configDirectory is null ? null : Path.Combine(configDirectory, FolderName, language + ".json");
        if (user is not null && File.Exists(user))
        {
            try
            {
                foreach (var (key, value) in Parse(File.ReadAllText(user)))
                {
                    entries[key] = value;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"Language file {user} ignored: {ex.Message}");
            }
        }

        return entries;
    }

    /// <summary>Reads only <c>"language"</c> from settings.json, for start-up before the rest of the configuration.</summary>
    public static string? ReadLanguageSetting(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "language", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
