using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;

namespace Commander4K.App.Dialogs;

/// <summary>
/// A plugin's window with several fields (<c>ui.dialog</c>, <see cref="DialogRequest"/>), built from the design
/// system's parts: section headings, field labels, a segmented switch for modes, file pickers, a notice for the
/// plugin's message and the error under each field. Fields with <see cref="DialogField.VisibleWhen"/> follow the
/// others as they change; required fields and number ranges are checked before the window closes.
/// </summary>
public sealed class PluginDialog : DialogWindow<IReadOnlyDictionary<string, string>?>
{
    private readonly List<Row> _rows = [];

    public PluginDialog(SkinBrushes brushes, DialogRequest request) : base(request.Title, brushes, 540)
    {
        // The right margin keeps the overlay scroll bar of a long window off the fields.
        var body = new StackPanel().Spacing(10).Margin(0, 0, 10, 0);
        if (request.Description is { Length: > 0 } description)
        {
            body.Children.Add(Message(description));
        }

        if (request.Message is { Length: > 0 } message)
        {
            body.Children.Add(Ds.Notice(brushes, message, NoticeKind.Error));
        }

        foreach (var field in request.Fields)
        {
            var row = CreateRow(field);
            if (request.Errors?.TryGetValue(field.Id, out var error) == true)
            {
                row.ShowError(error);
            }

            _rows.Add(row);
            body.Children.Add(row.Panel);
        }

        UpdateVisibility();
        SetBody(
            new ScrollViewer { Content = body, MaxHeight = 600, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            ButtonRow((Loc.T("Anuluj"), () => Complete(null), false), (request.Submit is { Length: > 0 } submit ? submit : "OK", Accept, true)));
        Opened += (_, _) => (_rows.FirstOrDefault(r => r.HasError && r.Panel.IsVisible) ?? _rows.FirstOrDefault(r => r.Focusable is not null && r.Panel.IsVisible))?.Focusable?.Focus();
    }

    private Row CreateRow(DialogField field)
    {
        var row = new Row(field);
        if (field.Type == DialogFieldType.Heading)
        {
            row.Panel.Children.Add(Brushes.IsModern ? Ds.SectionHeading(Brushes, field.Label ?? string.Empty).Margin(0, 6, 0, 0) : Label(field.Label ?? string.Empty));
            return row;
        }

        if (field.Type != DialogFieldType.Checkbox && field.Label is { Length: > 0 } label)
        {
            row.Panel.Children.Add(Label(label));
        }

        switch (field.Type)
        {
            case DialogFieldType.Checkbox:
                var check = new CheckBox { Content = field.Label, IsChecked = field.Value == "true", Foreground = Brushes.IsModern ? Brushes.FileForeground : Brushes.WindowForeground };
                check.IsCheckedChanged += (_, _) => UpdateVisibility();
                row.Read = () => check.IsChecked == true ? "true" : "false";
                row.Focusable = check;
                row.Panel.Children.Add(check);
                break;
            case DialogFieldType.Select:
                var options = field.Options!;
                var combo = new ComboBox { ItemsSource = options.Select(o => o.Label).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.SelectedIndex = Math.Max(0, IndexOf(options, field.Value));
                combo.SelectionChanged += (_, _) => UpdateVisibility();
                row.Read = () => options[Math.Max(0, combo.SelectedIndex)].Value;
                row.Focusable = combo;
                row.Panel.Children.Add(combo);
                break;
            case DialogFieldType.Segmented:
                var choices = field.Options!;
                var selected = Math.Max(0, IndexOf(choices, field.Value));
                var segmented = Ds.Segmented(Brushes, [.. choices.Select(o => o.Label)], selected, index =>
                {
                    selected = index;
                    UpdateVisibility();
                });
                segmented.HorizontalAlignment = HorizontalAlignment.Left;
                row.Read = () => choices[selected].Value;
                row.Panel.Children.Add(segmented);
                break;
            case DialogFieldType.File or DialogFieldType.Folder:
                var path = TextBox(field);
                var browse = Ds.Button(Brushes, Loc.T("Wybierz…"), () => _ = BrowseAsync(field, path)).Col(1).Margin(6, 0, 0, 0);
                row.Read = () => path.Text?.Trim() ?? string.Empty;
                row.Focusable = path;
                row.Panel.Children.Add(new Grid().Cols("*,Auto").Children(path, browse));
                break;
            default:
                var box = TextBox(field);
                row.Read = field.Type switch
                {
                    DialogFieldType.Password or DialogFieldType.Multiline => () => box.Text ?? string.Empty,
                    DialogFieldType.Number => () => box.Text?.Trim().Replace(',', '.') ?? string.Empty,
                    _ => () => box.Text?.Trim() ?? string.Empty,
                };
                row.Focusable = box;
                if (field.Button is { Length: > 0 } buttonLabel)
                {
                    var button = Ds.Button(Brushes, buttonLabel, () => PressButton(field.Id)).Col(1).Margin(6, 0, 0, 0);
                    row.Panel.Children.Add(new Grid().Cols("*,Auto").Children(box, button));
                }
                else
                {
                    row.Panel.Children.Add(box);
                }

                break;
        }

        if (field.Help is { Length: > 0 } help)
        {
            row.Panel.Children.Add(Ds.Muted(Brushes, help).Margin(0, 4, 0, 0));
        }

        row.Error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Error, IsVisible = false, Margin = new(0, 4, 0, 0) };
        row.Panel.Children.Add(row.Error);
        return row;
    }

    private TextBox TextBox(DialogField field)
    {
        var box = new TextBox { Text = field.Value ?? string.Empty, PlaceholderText = field.Placeholder };
        switch (field.Type)
        {
            case DialogFieldType.Password:
                box.PasswordChar = '•';
                break;
            case DialogFieldType.Multiline:
                box.AcceptsReturn = true;
                box.TextWrapping = TextWrapping.Wrap;
                box.Height = 96;
                break;
            case DialogFieldType.Number:
                box.Width = 140;
                box.HorizontalAlignment = HorizontalAlignment.Left;
                break;
        }

        box.TextChanged += (_, _) => UpdateVisibility();
        return box;
    }

    private async Task BrowseAsync(DialogField field, TextBox target)
    {
        var current = target.Text?.Trim() is { Length: > 0 } text ? (field.Type == DialogFieldType.Folder ? text : Path.GetDirectoryName(text)) : null;
        var start = current is not null && Directory.Exists(current) ? await StorageProvider.TryGetFolderFromPathAsync(current) : null;
        string? picked;
        if (field.Type == DialogFieldType.Folder)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = field.Label, SuggestedStartLocation = start });
            picked = folders.FirstOrDefault()?.TryGetLocalPath();
        }
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = field.Label, AllowMultiple = false, SuggestedStartLocation = start });
            picked = files.FirstOrDefault()?.TryGetLocalPath();
        }

        if (picked is not null)
        {
            target.Text = picked;
        }
    }

    private void UpdateVisibility()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var values = _rows.Where(r => r.Read is not null).ToDictionary(r => r.Field.Id, r => r.Read!(), StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            row.Panel.IsVisible = row.Field.VisibleWhen is not { } conditions
                || conditions.All(c => values.TryGetValue(c.Key, out var value) && c.Value.Contains(value, StringComparer.Ordinal));
        }
    }

    private void Accept()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        Row? firstInvalid = null;
        foreach (var row in _rows.Where(r => r.Read is not null && r.Panel.IsVisible))
        {
            var value = row.Read!();
            var error = Check(row.Field, value);
            row.ShowError(error);
            firstInvalid ??= error is null ? null : row;
            answers[row.Field.Id] = value;
        }

        if (firstInvalid is not null)
        {
            firstInvalid.Focusable?.Focus();
            return;
        }

        Complete(answers);
    }

    /// <summary>A field's button: every value as typed, unchecked, plus which button it was; the plugin shows the dialog again.</summary>
    private void PressButton(string fieldId)
    {
        var answers = _rows.Where(r => r.Read is not null).ToDictionary(r => r.Field.Id, r => r.Read!(), StringComparer.Ordinal);
        answers[DialogAnswers.ButtonKey] = fieldId;
        Complete(answers);
    }

    /// <summary>The app's own checks: required fields and number ranges. Anything else the plugin checks and reports.</summary>
    private static string? Check(DialogField field, string value)
    {
        if (field.Required && (field.Type == DialogFieldType.Checkbox ? value != "true" : string.IsNullOrWhiteSpace(value)))
        {
            return field.Type == DialogFieldType.Checkbox ? Loc.T("Zaznacz to pole.") : Loc.T("To pole jest wymagane.");
        }

        if (field.Type != DialogFieldType.Number || value.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return Loc.T("Wpisz liczbę.");
        }

        return (field.Min is { } min && number < min) || (field.Max is { } max && number > max)
            ? Loc.T("Liczba musi być z zakresu {0}–{1}.", field.Min?.ToString(CultureInfo.CurrentCulture) ?? "…", field.Max?.ToString(CultureInfo.CurrentCulture) ?? "…")
            : null;
    }

    private static int IndexOf(IReadOnlyList<DialogOption> options, string? value)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Value == value)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class Row(DialogField field)
    {
        public DialogField Field { get; } = field;

        public StackPanel Panel { get; } = new();

        /// <summary>The field's current value; <c>null</c> for headings.</summary>
        public Func<string>? Read { get; set; }

        public Control? Focusable { get; set; }

        public TextBlock? Error { get; set; }

        public bool HasError => Error?.IsVisible == true;

        public void ShowError(string? text)
        {
            if (Error is not null)
            {
                Error.Text = text;
                Error.IsVisible = text is not null;
            }
        }
    }
}
