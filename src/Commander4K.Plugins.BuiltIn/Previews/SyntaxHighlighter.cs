
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TmRegistry = TextMateSharp.Registry.Registry;
using TextMateSharp.Themes;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>A piece of text with the colour (0xRRGGBB) a TextMate theme gives it (<c>null</c> = the panel's own foreground).</summary>
public readonly record struct HighlightedSpan(string Text, uint? Rgb);

/// <summary>
/// Colours text with the TextMate grammars VS Code uses (TextMateSharp.Grammars), one theme per light/dark
/// variant. Slow (loading a grammar, tokenizing), so callers run it off the UI thread.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Lines past this are shown plain: a <c>SelectableTextBlock</c> with ten thousands of runs crawls.</summary>
    public const int MaxLines = 3000;

    private const int MaxLineLength = 2000;

    private static readonly Dictionary<string, string> NamedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = ".dockerfile",
        ["Makefile"] = ".mk",
        ["CMakeLists.txt"] = ".cmake",
        ["Gemfile"] = ".rb",
        ["Rakefile"] = ".rb",
        [".bashrc"] = ".sh",
        [".zshrc"] = ".sh",
        [".gitignore"] = ".ignore",
        [".editorconfig"] = ".ini",
    };

    private static readonly Lock Gate = new();
    private static readonly Dictionary<ThemeName, (RegistryOptions Options, TmRegistry Registry, Theme Theme)> Themes = [];

    /// <returns><c>null</c> when the file name has no known syntax or the text should stay plain.</returns>
    public static IReadOnlyList<HighlightedSpan>? Highlight(string text, string? fileName, bool dark, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(fileName) || text.Length == 0)
        {
            return null;
        }

        var extension = NamedFiles.TryGetValue(fileName, out var named) ? named : Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        lock (Gate)
        {
            var themeName = dark ? ThemeName.DarkPlus : ThemeName.LightPlus;
            if (!Themes.TryGetValue(themeName, out var entry))
            {
                var options = new RegistryOptions(themeName);
                var registry = new TmRegistry(options);
                entry = (options, registry, registry.GetTheme());
                Themes[themeName] = entry;
            }

            var language = entry.Options.GetLanguageByExtension(extension);
            if (language is null)
            {
                return null;
            }

            var grammar = entry.Registry.LoadGrammar(entry.Options.GetScopeByLanguageId(language.Id));
            if (grammar is null)
            {
                return null;
            }

            var spans = new List<HighlightedSpan>();
            IStateStack? stack = null;
            var lineCount = 0;
            var position = 0;
            while (position < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = text.IndexOf('\n', position);
                var next = end < 0 ? text.Length : end + 1;
                var lineEnd = end < 0 ? text.Length : end;
                if (lineEnd > position && text[lineEnd - 1] == '\r')
                {
                    lineEnd--;
                }

                var line = text.AsSpan(position, lineEnd - position);
                if (++lineCount > MaxLines || line.Length > MaxLineLength)
                {
                    spans.Add(new HighlightedSpan(text[position..], null));
                    return spans;
                }

                var lineText = line.ToString();
                var result = grammar.TokenizeLine(lineText, stack, TimeSpan.FromMilliseconds(200));
                stack = result.RuleStack;
                var covered = 0;
                foreach (var token in result.Tokens)
                {
                    var start = Math.Min(token.StartIndex, lineText.Length);
                    var stop = Math.Min(token.EndIndex, lineText.Length);
                    if (stop <= start || start < covered)
                    {
                        continue;
                    }

                    if (start > covered)
                    {
                        spans.Add(new HighlightedSpan(lineText[covered..start], null));
                    }

                    spans.Add(new HighlightedSpan(lineText[start..stop], ColorOf(entry.Theme, token.Scopes)));
                    covered = stop;
                }

                // Remainder of the line plus its terminator (kept verbatim so selection and copy match the file).
                spans.Add(new HighlightedSpan(text[(position + covered)..next], null));
                position = next;
            }

            return spans;
        }
    }

    private static uint? ColorOf(Theme theme, List<string> scopes)
    {
        foreach (var rule in theme.Match(scopes))
        {
            var hex = rule.foreground > 0 ? theme.GetColor(rule.foreground) : null;
            if (hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            {
                return rgb;
            }
        }

        return null;
    }
}
