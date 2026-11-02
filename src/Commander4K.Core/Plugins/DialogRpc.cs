using System.Globalization;
using System.Text.Json;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>
/// The <c>ui.dialog</c> request of the plugin protocol: JSON in, <see cref="DialogRequest"/>, and the answers back as
/// JSON with their natural types (a number field answers a number, a checkbox <c>true</c>/<c>false</c>, the rest text).
/// </summary>
public static class DialogRpc
{
    private const int MaxFields = 60;

    public static DialogRequest Parse(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(Loc.T("Brak parametru „{0}”.", "fields"));
        }

        if (fields.GetArrayLength() > MaxFields)
        {
            throw new ArgumentException(Loc.T("Okno może mieć najwyżej {0} pól.", MaxFields));
        }

        return new DialogRequest(
            Text(p, "title") ?? throw new ArgumentException(Loc.T("Brak parametru „{0}”.", "title")),
            [.. fields.EnumerateArray().Select(ParseField)],
            Text(p, "description"),
            Text(p, "submit"),
            Text(p, "message"),
            p.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object
                ? errors.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.String).ToDictionary(e => e.Name, e => e.Value.GetString()!, StringComparer.Ordinal)
                : null);
    }

    /// <summary>The answers as the plugin receives them; <c>null</c> stays <c>null</c> (cancelled).</summary>
    public static Dictionary<string, object?>? Answer(DialogRequest request, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var answer = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in request.Fields)
        {
            if (!values.TryGetValue(field.Id, out var value))
            {
                continue;
            }

            answer[field.Id] = field.Type switch
            {
                DialogFieldType.Checkbox => value == "true",
                DialogFieldType.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null,
                _ => value,
            };
        }

        if (values.TryGetValue(DialogAnswers.ButtonKey, out var button))
        {
            answer[DialogAnswers.ButtonKey] = button;
        }

        return answer;
    }

    private static DialogField ParseField(JsonElement f)
    {
        var id = Text(f, "id") ?? string.Empty;
        var typeName = Text(f, "type") ?? "text";
        if (!Enum.TryParse<DialogFieldType>(typeName, ignoreCase: true, out var type) || int.TryParse(typeName, out _))
        {
            throw new ArgumentException(Loc.T("Nieznany rodzaj pola „{0}”.", typeName));
        }

        var options = f.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object
                ? new DialogOption(Value(x, "value") ?? string.Empty, Text(x, "label") ?? Value(x, "value") ?? string.Empty)
                : new DialogOption(AsText(x) ?? string.Empty, AsText(x) ?? string.Empty)).ToList()
            : null;
        if (type is DialogFieldType.Select or DialogFieldType.Segmented && options is not { Count: > 0 })
        {
            throw new ArgumentException(Loc.T("Pole „{0}” potrzebuje listy „options”.", id));
        }

        var visibleWhen = f.TryGetProperty("visibleWhen", out var w) && w.ValueKind == JsonValueKind.Object
            ? w.EnumerateObject().ToDictionary(
                e => e.Name,
                e => (IReadOnlyList<string>)(e.Value.ValueKind == JsonValueKind.Array ? [.. e.Value.EnumerateArray().Select(x => AsText(x) ?? string.Empty)] : [AsText(e.Value) ?? string.Empty]),
                StringComparer.Ordinal)
            : null;
        return new DialogField(id, type, Text(f, "label"), Value(f, "value"), Text(f, "placeholder"), Text(f, "help"),
            f.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
            options, visibleWhen, Number(f, "min"), Number(f, "max"), Text(f, "button"));
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Value(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? AsText(v) : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var number) ? number : null;

    /// <summary>A JSON value as the field text: numbers in invariant form, booleans as <c>true</c>/<c>false</c>.</summary>
    private static string? AsText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
