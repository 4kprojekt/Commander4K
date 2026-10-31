using System.Text.Json;
using System.Text.RegularExpressions;

namespace Commander4K.Plugins.BuiltIn.Dashboard;

/// <summary>Paths into JSON responses as <c>c4k.dir</c> writes them: <c>a.b</c>, <c>items[0].name</c>, <c>$</c> for the whole value.</summary>
public static partial class JsonFieldPath
{
    public static bool TryGet(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (path is "$" or "")
        {
            return true;
        }

        foreach (Match step in Step().Matches(path))
        {
            if (step.Groups[1].Success)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(step.Groups[1].Value, out value))
                {
                    return false;
                }
            }
            else
            {
                var index = int.Parse(step.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength())
                {
                    return false;
                }

                value = value[index];
            }
        }

        return true;
    }

    /// <summary>
    /// Every leaf of <paramref name="root"/> with its current value, for picking fields. Arrays are shown by their first
    /// element (<c>[0]</c>); an empty array or object is a leaf itself.
    /// </summary>
    public static IReadOnlyList<(string Path, string Preview)> Flatten(JsonElement root, int max = 500)
    {
        var fields = new List<(string, string)>();
        Walk(root, string.Empty, fields, max);
        return fields;
    }

    /// <summary>A value as one line of text: strings without quotes, objects and arrays shortened.</summary>
    public static string Text(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => "—",
            _ => value.GetRawText(),
        };
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 80 ? text : text[..80] + "…";
    }

    private static void Walk(JsonElement value, string path, List<(string, string)> fields, int max)
    {
        if (fields.Count >= max)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object when value.EnumerateObject().Any():
                foreach (var property in value.EnumerateObject())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, fields, max);
                }

                break;
            case JsonValueKind.Array when value.GetArrayLength() > 0:
                Walk(value[0], path + "[0]", fields, max);
                break;
            default:
                fields.Add((path.Length == 0 ? "$" : path, Text(value)));
                break;
        }
    }

    [GeneratedRegex(@"([^.\[\]]+)|\[(\d+)\]")]
    private static partial Regex Step();
}
