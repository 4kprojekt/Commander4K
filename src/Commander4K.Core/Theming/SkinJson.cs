using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Commander4K.Core.Configuration;
using Commander4K.Core.Localization;
using Commander4K.Core.Mcp;

namespace Commander4K.Core.Theming;

/// <summary>Outcome of <see cref="SkinJson.Read"/>.</summary>
/// <param name="Error">Every problem found, one per line, for the user; <c>null</c> when <paramref name="Skin"/> was read.</param>
/// <param name="Notes">What was repaired or ignored on the way (renamed keys, short colours, unknown fields).</param>
public sealed record SkinReadResult(Skin? Skin, string? Error, IReadOnlyList<string> Notes);

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
        var result = Read(text, fixId);
        skin = result.Skin;
        error = result.Error ?? "";
        return skin is not null;
    }

    /// <summary>
    /// Reads a skin the way another assistant tends to write it: the JSON anywhere in the text, keys in any case or
    /// with <c>_</c>/<c>-</c> (<c>Window_Background</c>), <c>colors</c> for <c>palette</c>, colours as <c>#RGB</c>,
    /// <c>RRGGBB</c> or <c>rgb()</c>/<c>rgba()</c>, numbers and booleans in quotes, a missing <c>typography</c>.
    /// Anything else is reported in Polish with its place in the file, all problems at once, never as a serializer message.
    /// </summary>
    public static SkinReadResult Read(string? text, bool fixId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SkinReadResult(null, Loc.T("Wklej JSON skórki."), []);
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(Extract(text), documentOptions: Lenient) is not JsonObject parsed)
            {
                return new SkinReadResult(null, Loc.T("✗ Skórka musi być obiektem JSON { … }."), []);
            }

            root = parsed;
        }
        catch (JsonException ex)
        {
            return new SkinReadResult(null, Loc.T("✗ To nie jest poprawny JSON (wiersz {0}, znak {1}). Sprawdź przecinki, cudzysłowy wokół nazw i nawiasy.",
                (ex.LineNumber ?? 0) + 1, (ex.BytePositionInLine ?? 0) + 1), []);
        }

        var errors = new List<string>();
        var notes = new List<string>();
        Rename(root, "colors", "palette", notes);
        Rename(root, "colours", "palette", notes);
        Rename(root, "name", "displayName", notes);
        Rename(root, "font", "typography", notes);
        if (root["typography"] is null)
        {
            root["typography"] = new JsonObject { ["fontFamily"] = DefaultFont };
            notes.Add(Loc.T("Brak „typography” — użyto czcionki {0}.", DefaultFont));
        }

        if (root["displayName"] is null && root["id"] is JsonValue id)
        {
            root["displayName"] = id.ToString();
        }

        if (root["id"] is null && fixId)
        {
            root["id"] = NormalizeId(root["displayName"]?.ToString());
        }

        Normalize(root, ConfigJsonContext.Default.Skin, string.Empty, errors, notes);
        if (errors.Count > 0)
        {
            return new SkinReadResult(null, Loc.T("✗ Skórka ma błędy:") + "\n• " + string.Join("\n• ", errors), notes);
        }

        Skin skin;
        try
        {
            skin = root.Deserialize(ConfigJsonContext.Default.Skin) ?? throw new JsonException();
        }
        catch (JsonException ex)
        {
            // Normalize checks every key the model has; this is a last line for anything it does not know.
            return new SkinReadResult(null, Loc.T("✗ Skórka ma błędy:") + "\n• " + Loc.T("{0}: zła wartość.", ex.Path?.TrimStart('$', '.') ?? "?"), notes);
        }

        if (!McpConfigTools.IsSkinId(skin.Id))
        {
            if (!fixId)
            {
                return new SkinReadResult(null, Loc.T("✗ Identyfikator „{0}” musi składać się z małych liter, cyfr i myślników (np. „solarized-dark”).", skin.Id), notes);
            }

            skin = skin with { Id = NormalizeId(string.IsNullOrWhiteSpace(skin.Id) ? skin.DisplayName : skin.Id) };
        }

        return new SkinReadResult(skin, null, notes);
    }

    private const string DefaultFont = "Segoe UI, Arial, sans-serif";

    /// <summary>Checks and repairs one object against the model's properties (names, value kinds), recursing into nested objects.</summary>
    private static void Normalize(JsonObject node, JsonTypeInfo info, string path, List<string> errors, List<string> notes)
    {
        var properties = info.Properties.ToDictionary(p => Loose(p.Name), StringComparer.Ordinal);
        foreach (var (name, value) in node.ToList())
        {
            if (name == "$schema")
            {
                continue;
            }

            if (!properties.TryGetValue(Loose(name), out var property))
            {
                node.Remove(name);
                var similar = properties.Values.Select(p => p.Name).Where(p => node[p] is null).OrderBy(p => Distance(p, name)).FirstOrDefault();
                notes.Add(similar is not null && Distance(similar, name) <= 3
                    ? Loc.T("Pominięto nieznane pole „{0}” (czy chodziło o „{1}”?).", path + name, similar)
                    : Loc.T("Pominięto nieznane pole „{0}”.", path + name));
                continue;
            }

            if (name != property.Name)
            {
                node.Remove(name);
                node[property.Name] = value;
            }

            var where = path + property.Name;
            if (value is null)
            {
                if (property.IsRequired)
                {
                    errors.Add(Loc.T("{0}: wartość nie może być pusta.", where));
                }
                else
                {
                    node.Remove(property.Name);
                }

                continue;
            }

            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type == typeof(RgbColor))
            {
                if (Color(value) is { } color)
                {
                    node[property.Name] = color;
                }
                else
                {
                    errors.Add(Loc.T("{0}: „{1}” nie jest kolorem; użyj #RRGGBB (np. #1E1E2E) albo #AARRGGBB.", where, Shown(value)));
                }
            }
            else if (type == typeof(string))
            {
                if (value is JsonValue text && text.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
                {
                    var shown = text.ToString();
                    node[property.Name] = property.Name == "themeVariant" ? ThemeVariant(shown, where, errors) : shown;
                }
                else
                {
                    errors.Add(Loc.T("{0}: oczekiwano tekstu.", where));
                }
            }
            else if (type == typeof(double))
            {
                if (value is JsonValue number && (number.TryGetValue<double>(out var parsed)
                    || (number.TryGetValue<string>(out var quoted) && double.TryParse(quoted.Replace(',', '.').Replace("px", "", StringComparison.OrdinalIgnoreCase).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))))
                {
                    node[property.Name] = parsed;
                }
                else
                {
                    errors.Add(Loc.T("{0}: „{1}” nie jest liczbą.", where, Shown(value)));
                }
            }
            else if (type == typeof(bool))
            {
                if (value is JsonValue flag && (flag.TryGetValue<bool>(out var parsed) || (flag.TryGetValue<string>(out var quoted) && bool.TryParse(quoted, out parsed))))
                {
                    node[property.Name] = parsed;
                }
                else
                {
                    errors.Add(Loc.T("{0}: oczekiwano true albo false.", where));
                }
            }
            else if (type.IsEnum)
            {
                var match = value is JsonValue choice && choice.TryGetValue<string>(out var picked)
                    ? Enum.GetNames(type).FirstOrDefault(n => n.Equals(picked, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (match is not null)
                {
                    node[property.Name] = match;
                }
                else
                {
                    errors.Add(Loc.T("{0}: „{1}” nie jest dozwolone; możliwe: {2}.", where, Shown(value), string.Join(", ", Enum.GetNames(type))));
                }
            }
            else if (value is JsonObject nested && ConfigJsonContext.Default.GetTypeInfo(type) is { } nestedInfo)
            {
                Normalize(nested, nestedInfo, where + ".", errors, notes);
            }
            else
            {
                errors.Add(Loc.T("{0}: oczekiwano obiektu {{ … }}.", where));
            }
        }

        var missing = info.Properties.Where(p => p.IsRequired && node[p.Name] is null).Select(p => path + p.Name).ToList();
        if (missing.Count > 0)
        {
            errors.Add(Loc.T("brakuje wymaganych pól: {0}", string.Join(", ", missing)));
        }
    }

    private static void Rename(JsonObject root, string from, string to, List<string> notes)
    {
        if (root[from] is { } value && root[to] is null)
        {
            root.Remove(from);
            root[to] = value;
            notes.Add(Loc.T("Pole „{0}” odczytano jako „{1}”.", from, to));
        }
    }

    private static string ThemeVariant(string value, string where, List<string> errors)
    {
        if (value.Equals("Dark", StringComparison.OrdinalIgnoreCase) || value.Equals("Light", StringComparison.OrdinalIgnoreCase))
        {
            return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
        }

        errors.Add(Loc.T("{0}: „{1}” nie jest dozwolone; możliwe: Dark, Light.", where, value));
        return value;
    }

    /// <summary><c>#RRGGBB</c>/<c>#AARRGGBB</c> as written, or repaired from <c>#RGB</c>, hex without <c>#</c>, <c>rgb()</c>, <c>rgba()</c>.</summary>
    internal static string? Color(JsonNode value)
    {
        if (value is not JsonValue text || !text.TryGetValue<string>(out var raw))
        {
            return null;
        }

        var color = raw.Trim();
        if (color.Length is 3 or 6 or 8 && color.All(char.IsAsciiHexDigit))
        {
            color = "#" + color;
        }

        if (color.Length == 4 && color[0] == '#' && color[1..].All(char.IsAsciiHexDigit))
        {
            color = string.Concat("#", color[1], color[1], color[2], color[2], color[3], color[3]);
        }

        if (RgbColor.TryParse(color, out var parsed))
        {
            return parsed.ToString();
        }

        var open = color.IndexOf('(');
        if (open > 0 && color.EndsWith(')') && color[..open].Trim().ToLowerInvariant() is "rgb" or "rgba")
        {
            var parts = color[(open + 1)..^1].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length is 3 or 4 && parts.Take(3).All(p => byte.TryParse(p, inv, out _)))
            {
                var alpha = 1.0;
                if (parts.Length == 4)
                {
                    var percent = parts[3].EndsWith('%');
                    if (!double.TryParse(percent ? parts[3][..^1] : parts[3], System.Globalization.NumberStyles.Float, inv, out alpha))
                    {
                        return null;
                    }

                    alpha = percent ? alpha / 100 : alpha;
                }

                return new RgbColor(byte.Parse(parts[0], inv), byte.Parse(parts[1], inv), byte.Parse(parts[2], inv), (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255)).ToString();
            }
        }

        return null;
    }

    private static string Shown(JsonNode value)
    {
        var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        return text.Length > 40 ? text[..40] + "…" : text;
    }

    /// <summary>A key compared without case, <c>_</c>, <c>-</c> and spaces.</summary>
    private static string Loose(string name) =>
        string.Concat(name.Where(c => c is not ('_' or '-' or ' '))).ToLowerInvariant();

    private static int Distance(string a, string b)
    {
        a = Loose(a);
        b = Loose(b);
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }

        return row[b.Length];
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
