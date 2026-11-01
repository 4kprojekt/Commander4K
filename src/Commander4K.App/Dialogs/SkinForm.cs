using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;

namespace Commander4K.App.Dialogs;

/// <summary>
/// Edit form generated from the skin's JSON Schema: enums become combo boxes, booleans check boxes, numbers and strings
/// text boxes, colours a swatch plus a hex box. The data stays a <see cref="JsonObject"/>, so the form never needs to
/// know the model; an empty optional field is simply left out.
/// </summary>
public sealed class SkinForm
{
    private sealed record Field(string[] Path, Func<JsonNode?> Read, Action<JsonNode?> Write);

    private readonly List<Field> _fields = [];
    private readonly Queue<Action> _work = new();
    private readonly Action _changed;
    private readonly IBrush _swatchBorder;
    private IReadOnlyList<string> _fontFamilies = [];
    private JsonObject _data = [];
    private bool _loading;

    /// <summary>
    /// Creates the section frames only; the field controls are queued and built by <see cref="BuildAsync"/> in small
    /// batches, so a window showing the form appears at once and stays responsive while it fills.
    /// </summary>
    public SkinForm(JsonNode schema, IBrush swatchBorder, Action changed)
    {
        _changed = changed;
        _swatchBorder = swatchBorder;
        var root = schema.AsObject();
        var properties = root["properties"]?.AsObject() ?? [];
        var required = RequiredOf(root);

        var general = new StackPanel { Spacing = 6 };
        foreach (var (name, node) in properties.Where(p => !IsObject(p.Value)))
        {
            _work.Enqueue(() => general.Children.Add(AddField([name], name, node!, required.Contains(name))));
        }

        Root.Children.Add(Section(Loc.T("Ogólne"), general));
        foreach (var (name, node) in properties.Where(p => IsObject(p.Value)))
        {
            Root.Children.Add(Section(SectionTitle(name), ObjectBody(name, node!.AsObject())));
        }
    }

    public StackPanel Root { get; } = new() { Spacing = 16 };

    /// <summary>True once every field control exists, so <see cref="ToJson"/> is complete.</summary>
    public bool IsBuilt => _work.Count == 0;

    public async Task BuildAsync()
    {
        _fontFamilies = await Task.Run(() =>
        {
            var families = FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return families.Length > 0
                ? families
                : throw new InvalidOperationException("No installed font families were found.");
        });

        while (_work.Count > 0)
        {
            for (var i = 0; i < 6 && _work.Count > 0; i++)
            {
                _work.Dequeue()();
            }

            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        }
    }

    /// <summary>Current form content; empty optional fields are omitted, so the object matches the schema.</summary>
    public JsonObject ToJson()
    {
        var result = new JsonObject();
        foreach (var field in _fields)
        {
            if (field.Read() is not { } value)
            {
                continue;
            }

            var target = result;
            foreach (var key in field.Path[..^1])
            {
                if (target[key] is not JsonObject child)
                {
                    child = [];
                    target[key] = child;
                }

                target = child;
            }

            target[field.Path[^1]] = value;
        }

        return result;
    }

    /// <summary>Fills the form; fields that are not built yet take their value from the same data when they appear.</summary>
    public void Load(JsonObject data)
    {
        _data = data;
        _loading = true;
        try
        {
            foreach (var field in _fields)
            {
                field.Write(ValueAt(field.Path));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private JsonNode? ValueAt(string[] path)
    {
        JsonNode? node = _data;
        foreach (var key in path)
        {
            node = node is JsonObject obj ? obj[key] : null;
        }

        return node;
    }

    private StackPanel ObjectBody(string name, JsonObject schema)
    {
        var properties = schema["properties"]?.AsObject() ?? [];
        var required = RequiredOf(schema);
        var body = new StackPanel { Spacing = 6 };
        var optional = new StackPanel { Spacing = 6 };
        foreach (var (key, node) in properties)
        {
            var isRequired = required.Contains(key);
            var target = isRequired ? body : optional;
            _work.Enqueue(() => target.Children.Add(AddField([name, key], key, node!, isRequired)));
        }

        if (properties.Any(p => !required.Contains(p.Key)))
        {
            var expander = new Expander
            {
                Header = Loc.T("Opcjonalne (puste = wyliczone z pozostałych)"),
                Margin = new Thickness(0, 6, 0, 0),
                Content = optional,
            };
            _work.Enqueue(() => body.Children.Add(expander));
        }

        return body;
    }

    private Control AddField(string[] path, string name, JsonNode schema, bool required)
    {
        var description = schema["description"]?.GetValue<string>();
        var (editor, read, write) = CreateEditor(path, schema, required);
        _fields.Add(new Field(path, read, write));
        _loading = true;
        write(ValueAt(path));
        _loading = false;
        _loading = true;
        write(ValueAt(path));
        _loading = false;
        var label = new TextBlock
        {
            Text = Humanize(name) + (required ? " *" : ""),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(label, string.IsNullOrEmpty(description) ? name : name + " — " + description);
        return new Grid().Cols("210,*").Children(label, editor.Col(1));
    }

    private (Control Editor, Func<JsonNode?> Read, Action<JsonNode?> Write) CreateEditor(string[] path, JsonNode schema, bool required)
    {
        if (path.Length == 2 && path[0] == "typography" && path[1] == "fontFamily")
        {
            return CreateFontFamilyEditor(required);
        }

        if (schema["enum"] is JsonArray options)
        {
            var values = options.Select(o => o?.ToString() ?? "").ToList();
            if (!required)
            {
                values.Insert(0, "");
            }

            var combo = new ComboBox { ItemsSource = values, MinWidth = 160 };
            combo.SelectionChanged += (_, _) => Notify();
            return (combo,
                () => combo.SelectedItem is string { Length: > 0 } s ? JsonValue.Create(s) : null,
                n => combo.SelectedItem = values.Contains(n?.ToString() ?? "") ? n?.ToString() ?? "" : values.FirstOrDefault());
        }

        var type = TypeOf(schema);
        if (type == "boolean")
        {
            var box = new CheckBox();
            box.IsCheckedChanged += (_, _) => Notify();
            return (box, () => JsonValue.Create(box.IsChecked == true),
                n => box.IsChecked = n is JsonValue v && v.TryGetValue(out bool b) && b);
        }

        var text = new TextBox { MinWidth = 160 };
        text.TextChanged += (_, _) => Notify();
        string Read() => text.Text?.Trim() ?? "";

        if (type is "number" or "integer")
        {
            return (text,
                () => Read() is { Length: > 0 } s
                    ? double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : JsonValue.Create(s)
                    : null,
                n => text.Text = n is JsonValue v && v.TryGetValue(out double d) ? d.ToString(CultureInfo.InvariantCulture) : "");
        }

        if (IsColor(schema))
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = _swatchBorder,
                Margin = new Thickness(0, 0, 8, 0),
            };
            text.TextChanged += (_, _) =>
                swatch.Background = Color.TryParse(Read(), out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
            text.Width = 130;
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            row.Children.Add(swatch);
            row.Children.Add(text);
            return (row, () => Read() is { Length: > 0 } s ? JsonValue.Create(s) : null,
                n => text.Text = n is JsonValue v && v.TryGetValue(out string? s) ? s : "");
        }

        return (text, () => Read() is { Length: > 0 } s ? JsonValue.Create(s) : required ? JsonValue.Create("") : null,
            n => text.Text = n is JsonValue v && v.TryGetValue(out string? s) ? s : "");
    }

    private (Control Editor, Func<JsonNode?> Read, Action<JsonNode?> Write) CreateFontFamilyEditor(bool required)
    {
        var values = _fontFamilies.ToList();
        var combo = new ComboBox { ItemsSource = values, MinWidth = 160 };
        combo.SelectionChanged += (_, _) => Notify();

        void Write(JsonNode? node)
        {
            var value = node is JsonValue json && json.TryGetValue(out string? family) ? family : "";
            if (value.Length > 0 && !values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
                combo.ItemsSource = values.ToArray();
            }

            combo.SelectedItem = value.Length > 0 ? value : null;
        }

        return (combo,
            () => combo.SelectedItem is string { Length: > 0 } family ? JsonValue.Create(family) : required ? JsonValue.Create("") : null,
            Write);
    }

    private void Notify()
    {
        if (!_loading)
        {
            _changed();
        }
    }

    private static Control Section(string title, Control body) =>
        new StackPanel { Spacing = 8 }.Children(
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14 },
            body);

    private static string SectionTitle(string name) => name switch
    {
        "palette" => Loc.T("Kolory"),
        "typography" => Loc.T("Typografia"),
        _ => Humanize(name),
    };

    private static HashSet<string> RequiredOf(JsonObject schema) =>
        schema["required"] is JsonArray array ? array.Select(n => n?.ToString() ?? "").ToHashSet() : [];

    private static bool IsObject(JsonNode? node) => node?["properties"] is JsonObject;

    private static bool IsColor(JsonNode schema) =>
        schema["pattern"] is not null || (schema["description"]?.ToString().Contains("#RRGGBB", StringComparison.Ordinal) ?? false);

    private static string? TypeOf(JsonNode schema) => schema["type"] switch
    {
        JsonArray array => array.Select(n => n?.ToString()).FirstOrDefault(t => t != "null"),
        { } node => node.ToString(),
        _ => null,
    };

    /// <summary>"panelBackground" becomes "Panel background".</summary>
    private static string Humanize(string name)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i == 0)
            {
                builder.Append(char.ToUpperInvariant(c));
            }
            else if (char.IsUpper(c))
            {
                builder.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
