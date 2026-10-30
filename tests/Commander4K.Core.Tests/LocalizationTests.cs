using System.Text.RegularExpressions;
using Commander4K.Core.Localization;

namespace Commander4K.Core.Tests;

/// <summary>Keeps the language files in step with the code. Runs in a collection of its own: <see cref="Loc"/> is global.</summary>
[Collection(nameof(LocalizationTests))]
public sealed partial class LocalizationTests : IDisposable
{
    public void Dispose() => Loc.Use(Loc.SourceLanguage, new Dictionary<string, string>());

    [Fact]
    public void English_file_covers_exactly_the_texts_used_in_code()
    {
        var used = KeysInSource();
        var english = Loc.Parse(Loc.BuiltInFile("en")!);

        Assert.True(used.Count > 500, $"only {used.Count} keys found — is the source scan broken?");
        Assert.Empty(used.Where(k => !english.ContainsKey(k)).Order());   // Texts the English file is missing.
        Assert.Empty(english.Keys.Where(k => !used.Contains(k)).Order()); // Stale entries (text changed in code).
    }

    [Fact]
    public void English_file_has_matching_placeholders_and_plural_forms()
    {
        Assert.Empty(TranslationKit.Check(Loc.BuiltInFile("en")!, "en").Select(i => $"{i.Key}: {i.Problem}"));
    }

    [Theory]
    [InlineData(1, "1 plik")]
    [InlineData(3, "3 pliki")]
    [InlineData(5, "5 plików")]
    [InlineData(22, "22 pliki")]
    [InlineData(12, "12 plików")]
    public void Polish_plurals_follow_cldr(long count, string expected)
    {
        Loc.Use(Loc.SourceLanguage, new Dictionary<string, string>());
        Assert.Equal(expected, Loc.P(count, "{0} plik|{0} pliki|{0} plików"));
    }

    [Fact]
    public void Translation_falls_back_to_the_original_and_survives_broken_entries()
    {
        Loc.Use("en", new Dictionary<string, string>
        {
            ["Kopiuj"] = "Copy",
            ["{0} plik|{0} pliki|{0} plików"] = "{0} file|{0} files",
            ["Usunąć {0}?"] = "Delete {0} {1}?", // Broken: needs an argument that does not exist.
        });

        Assert.Equal("Copy", Loc.T("Kopiuj"));
        Assert.Equal("Wklej", Loc.T("Wklej"));
        Assert.Equal("1 file", Loc.P(1, "{0} plik|{0} pliki|{0} plików"));
        Assert.Equal("5 files", Loc.P(5, "{0} plik|{0} pliki|{0} plików"));
        Assert.Equal("Usunąć a.txt?", Loc.T("Usunąć {0}?", "a.txt"));
    }

    [Fact]
    public void Checker_reports_placeholder_and_plural_mistakes()
    {
        var issues = TranslationKit.Check("""
            { "Kopiuj": "Kopieren", "Usunąć {0}? Tej operacji nie można cofnąć.": "Löschen?", "{0} element|{0} elementy|{0} elementów": "{0} Element", "Nie ma takiego klucza": "x" }
            """, "de");

        Assert.Contains(issues, i => i.Key.StartsWith("Usunąć", StringComparison.Ordinal) && i.Problem.Contains("{0}", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Key.StartsWith("{0} element", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Key == "Nie ma takiego klucza");
        Assert.Equal(2, TranslationKit.PluralForms("de"));
        Assert.Equal(3, TranslationKit.PluralForms("uk"));
        Assert.Contains("\"Kopiuj\": \"Copy\"", TranslationKit.PromptFor("de", "Deutsch"), StringComparison.Ordinal);
    }

    /// <summary>First string-literal argument of every Loc.T / second of every Loc.P call in src (regular literals only).</summary>
    private static HashSet<string> KeysInSource()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Commander4K.sln")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match match in LocCall().Matches(File.ReadAllText(file)))
            {
                keys.Add(Unescape(match.Groups["text"].Value));
            }
        }

        // Permission descriptions come from the SDK (which cannot reference Loc) and are translated where shown.
        foreach (var permission in Commander4K.PluginSdk.PluginPermissions.All)
        {
            keys.Add(Commander4K.PluginSdk.PluginPermissions.Describe(permission));
        }

        return keys;
    }

    private static string Unescape(string literal) => Regex.Unescape(literal.Replace("\\\"", "\"", StringComparison.Ordinal));

    [GeneratedRegex("""Loc\.(?:T\(|P\([^,()]*(?:\([^()]*\))?[^,()]*,\s*)"(?<text>(?:[^"\\]|\\.)*)"(?=\s*[,)])""")]
    private static partial Regex LocCall();
}
