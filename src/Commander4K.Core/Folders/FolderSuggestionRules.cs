using Commander4K.PluginSdk;

namespace Commander4K.Core.Folders;

/// <summary>The manifest's <c>suggest</c> rule, checked by the host on a folder snapshot (no plugin process involved).</summary>
public static class FolderSuggestionRules
{
    /// <returns>The number of matching files, or <c>null</c> when the rule fails. A truncated snapshot is judged on what was read.</returns>
    public static int? Match(PreviewSuggestRule rule, FolderSnapshot folder)
    {
        foreach (var name in rule.Contains ?? [])
        {
            if (!folder.Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        if (rule.Files is not { Count: > 0 } patterns)
        {
            return rule.Contains is { Count: > 0 } ? 0 : null;
        }

        var files = folder.Entries.Where(e => e.Kind == VfsEntryKind.File).ToList();
        var count = files.Count(f => patterns.Any(p => MatchesPattern(p, f.Name)));
        return count >= Math.Max(1, rule.MinCount) && count >= rule.MinShare * files.Count ? count : null;
    }

    /// <summary>
    /// <c>.csv</c> = extension; anything else is a file name or a pattern with <c>*</c>/<c>?</c> (<c>package.json</c>,
    /// <c>requirements*.txt</c>, <c>*.openapi.json</c>).
    /// </summary>
    public static bool MatchesPattern(string pattern, string name) =>
        pattern.StartsWith('.') && pattern.IndexOfAny(['*', '?']) < 0
            ? string.Equals(Path.GetExtension(name), pattern, StringComparison.OrdinalIgnoreCase)
            : pattern != "/" && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    /// <summary>The message in <paramref name="language"/>, else English, else the first one, else <paramref name="fallbackTitle"/>.</summary>
    public static string MessageFor(PreviewSuggestRule rule, int count, string language, string fallbackTitle)
    {
        if (rule.Message is not { Count: > 0 } messages)
        {
            return fallbackTitle;
        }

        var text = messages.TryGetValue(language, out var own) ? own
            : messages.TryGetValue("en", out var english) ? english
            : messages.Values.First();
        return text.Replace("{count}", count.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);
    }
}
