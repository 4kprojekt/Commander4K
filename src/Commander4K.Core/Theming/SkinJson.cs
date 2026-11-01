using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Commander4K.Core.Configuration;
using Commander4K.Core.Localization;
using Commander4K.Core.Mcp;

namespace Commander4K.Core.Theming;

/// <summary>Skin (de)serialisation with the same options as <c>config/skins/*.json</c>.</summary>
public static class SkinJson
{
    private const int MaxCandidates = 64;

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static string Serialize(Skin skin) => JsonSerializer.Serialize(skin, ConfigJsonContext.Default.Skin);

    /// <exception cref="JsonException">Malformed JSON, a bad colour or a missing required property.</exception>
    public static Skin Deserialize(string json) =>
        JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Skin) ?? throw new JsonException("Empty skin document.");

    /// <summary>
    /// The skin JSON out of a whole pasted chat answer: the first balanced <c>{…}</c> that is a JSON object with a
    /// <c>palette</c> (so prose, ```json fences, a lead-in or a closing remark are dropped), else the first JSON
    /// object, else the outermost braces. Curly quotes from a rich-text copy are tolerated.
    /// </summary>
    public static string Extract(string text)
    {
        text = text.Trim().TrimStart('\uFEFF');
        string? firstObject = null;
        foreach (var variant in Variants(text))
        {
            var attempts = 0;
            for (var start = variant.IndexOf('{'); start >= 0 && attempts < MaxCandidates; start = variant.IndexOf('{', start + 1), attempts++)
            {
                var end = MatchingBrace(variant, start);
                if (end < 0)
                {
                    continue;
                }

                var candidate = variant[start..(end + 1)];
                try
                {
                    using var document = JsonDocument.Parse(candidate, Lenient);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (document.RootElement.EnumerateObject().Any(p => p.Name.Equals("palette", StringComparison.OrdinalIgnoreCase)))
                    {
                        return candidate;
                    }

                    firstObject ??= candidate;
                }
                catch (JsonException)
                {
                    // Not JSON (a brace in prose): try the next one.
                }
            }
        }

        if (firstObject is not null)
        {
            return firstObject;
        }

        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        return open >= 0 && close > open ? text[open..(close + 1)] : text;
    }

    /// <summary>Finds the skin in <paramref name="text"/> (see <see cref="Extract"/>) and parses it.</summary>
    /// <param name="fixId">An id that is not lower-case kebab-case is repaired (<see cref="NormalizeId"/>) instead of refused.</param>
    public static bool TryRead(string? text, bool fixId, [NotNullWhen(true)] out Skin? skin, out string error)
    {
        skin = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = Loc.T("Wklej JSON skórki.");
            return false;
        }

        Skin parsed;
        try
        {
            parsed = Deserialize(Extract(text));
        }
        catch (JsonException ex)
        {
            error = Loc.T("✗ JSON niezgodny ze schematem: {0}", ex.Message);
            return false;
        }

        if (!McpConfigTools.IsSkinId(parsed.Id))
        {
            if (!fixId)
            {
                error = Loc.T("✗ Identyfikator „{0}” musi składać się z małych liter, cyfr i myślników (np. „solarized-dark”).", parsed.Id);
                return false;
            }

            parsed = parsed with { Id = NormalizeId(string.IsNullOrWhiteSpace(parsed.Id) ? parsed.DisplayName : parsed.Id) };
        }

        skin = parsed;
        error = "";
        return true;
    }

    /// <summary>Lower-case letters, digits and single hyphens, accents dropped; "my-skin" when nothing usable is left.</summary>
    public static string NormalizeId(string? text)
    {
        var builder = new StringBuilder();
        foreach (var c in (text ?? "").ToLowerInvariant().Replace('\u0142', 'l').Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var id = builder.ToString().Trim('-');
        id = id.Length > 64 ? id[..64].TrimEnd('-') : id;
        return McpConfigTools.IsSkinId(id) ? id : "my-skin";
    }

    private static IEnumerable<string> Variants(string text)
    {
        yield return text;
        if (text.AsSpan().IndexOfAny('\u201C', '\u201D') >= 0)
        {
            yield return text.Replace('\u201C', '"').Replace('\u201D', '"');
        }
    }

    /// <summary>Index of the brace closing the one at <paramref name="start"/>, ignoring braces inside JSON strings; -1 if unbalanced.</summary>
    private static int MatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
            }
        }

        return -1;
    }
}
