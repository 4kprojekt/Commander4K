using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Commander4K.Core.Localization;

/// <summary>One problem found in a language file.</summary>
public sealed record TranslationIssue(string Key, string Problem);

/// <summary>
/// Everything an MCP client needs to add a language without touching code (get_translation_kit, translate_ui): the
/// instructions with the English file as the reference, and a checker for the entries that come back.
/// </summary>
public static partial class TranslationKit
{
    /// <summary>Prompt asking an AI to produce <c>config/i18n/&lt;code&gt;.json</c> for <paramref name="languageCode"/>.</summary>
    public static string PromptFor(string languageCode, string languageName)
    {
        var reference = Loc.BuiltInFile("en") ?? "{}";
        var forms = PluralForms(languageCode);
        return $$"""
            Translate the user interface of Commander4K (a dual-pane file manager for Windows) into {{languageName}} (language code "{{languageCode}}").

            Below is the English language file. It is a flat JSON object: each KEY is the original Polish UI text, each VALUE is its English translation.
            Produce the same JSON object with every value translated into {{languageName}}:
            - Copy every key exactly as it is (keys are the Polish originals and are used for lookup). Do not add, drop or merge keys.
            - Keys starting with "_//" are section comments: keep them unchanged. Other keys may start with "_" (it marks a menu access key, e.g. "_Plik" → "_File"): put the "_" before a suitable letter of the translation.
            - Keep placeholders exactly: {0}, {1}, {0:N0}, {1:0} … (you may move them within the sentence).
            - Plural entries contain forms separated by "|" (the English value has 2 forms: one|other). For {{languageName}} write exactly {{forms}} form(s) in CLDR order (one|few|many|other as the language uses them), each with its own {0}.
            - Keep keyboard shortcuts (Ctrl+S, F4, Alt+F7, Esc …), file names (keymap.json, tools.json), command ids, URLs, product names (Commander4K, Claude, Gemini, ChatGPT, PowerShell, WebView2, Git, SFTP …) and emoji unchanged.
            - Use the quotation marks of {{languageName}} instead of „ ” where natural. Keep the style short, as in desktop menus and dialogs. Use the informal or formal register the way Windows does in {{languageName}}.
            - Translate from the meaning of the Polish key; the English value is there to help.

            Answer with one ```json code block containing the complete file and nothing else.
            The user will save it as config/i18n/{{languageCode}}.json next to Commander4K and choose the language in the settings (F1 → "Language").

            ```json
            {{reference.Trim()}}
            ```
            """;
    }

    /// <summary>
    /// Compares a language file with the reference (the English file): unreadable JSON, missing or unknown keys,
    /// placeholders that differ from the key, and plural entries with the wrong number of forms.
    /// </summary>
    public static IReadOnlyList<TranslationIssue> Check(string json, string languageCode)
    {
        var issues = new List<TranslationIssue>();
        Dictionary<string, string> entries;
        try
        {
            entries = Loc.Parse(json);
        }
        catch (JsonException ex)
        {
            return [new TranslationIssue("(plik)", Loc.T("Niepoprawny JSON: {0}", ex.Message))];
        }

        var reference = Loc.BuiltInFile("en") is { } en ? Loc.Parse(en) : [];
        foreach (var key in reference.Keys.Where(k => !entries.ContainsKey(k)))
        {
            issues.Add(new TranslationIssue(key, Loc.T("brak tłumaczenia (zostanie oryginał)")));
        }

        foreach (var (key, value) in entries)
        {
            if (reference.Count > 0 && !reference.ContainsKey(key))
            {
                issues.Add(new TranslationIssue(key, Loc.T("klucz nie występuje w aplikacji (literówka w kluczu?)")));
                continue;
            }

            var isPlural = reference.TryGetValue(key, out var english) && english.Contains('|', StringComparison.Ordinal) && key.Contains('|', StringComparison.Ordinal);
            if (isPlural)
            {
                var expected = PluralForms(languageCode);
                var actual = value.Split('|').Length;
                if (actual != expected)
                {
                    issues.Add(new TranslationIssue(key, Loc.T("liczba form mnogich: {0}, oczekiwano {1}", actual, expected)));
                }

                foreach (var form in value.Split('|').Where(f => !f.Contains("{0", StringComparison.Ordinal)))
                {
                    issues.Add(new TranslationIssue(key, Loc.T("forma „{0}” nie zawiera {{0}}", form)));
                }

                continue;
            }

            var wanted = Placeholders(key);
            var got = Placeholders(value);
            if (!wanted.SetEquals(got))
            {
                issues.Add(new TranslationIssue(key, Loc.T("inne miejsca na wartości: {0} zamiast {1}", string.Join(" ", got.Order()), string.Join(" ", wanted.Order()))));
            }
        }

        return issues;
    }

    /// <summary>How many plural forms <see cref="Loc.PluralIndex"/> uses for a language.</summary>
    public static int PluralForms(string languageCode)
    {
        var max = 0;
        foreach (var n in new long[] { 0, 1, 2, 3, 5, 11, 12, 21, 22, 25, 101, 102, 111 })
        {
            max = Math.Max(max, Loc.PluralIndex(languageCode, n));
        }

        return max + 1;
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal).Replace("}}", string.Empty, StringComparison.Ordinal))
            .Select(m => "{" + m.Groups[1].Value + "}")];

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
