using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;

namespace Commander4K.App.Dialogs;

/// <summary>What the user picked for a new API card.</summary>
/// <param name="Refresh"><c>null</c> = read once; otherwise a <c>c4k.dir</c> interval such as <c>10s</c>.</param>
public sealed record ApiCardChoice(string Title, string? Refresh, IReadOnlyList<ApiField> Fields);

/// <summary>
/// The fields of a sample API response with their current values: tick the ones the card shows, say how (value,
/// chart, status) and under which label. A search box narrows long responses.
/// </summary>
public sealed class ApiFieldsDialog : DialogWindow<ApiCardChoice?>
{
    private readonly List<Row> _rows = [];
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBox _title;
    private readonly ComboBox _refresh;

    public ApiFieldsDialog(SkinBrushes brushes, string defaultTitle, string summary, IReadOnlyList<(string Path, string Preview)> fields)
        : base(Loc.T("Dodaj kartę API"), brushes, 760)
    {
        _title = new TextBox().Text(defaultTitle);
        _refresh = new ComboBox { ItemsSource = new[] { Loc.T("bez odświeżania"), "10s", "1m", "5m" }, SelectedIndex = 1, MinWidth = 160 };
        var search = new TextBox { PlaceholderText = Loc.T("Szukaj pola…") };
        search.TextChanged += (_, _) => Filter(search.Text);

        foreach (var (path, preview) in fields)
        {
            var row = new Row(path, preview, brushes);
            _rows.Add(row);
            _list.Children.Add(row.View);
        }

        SetBody(
            Label(summary),
            new Grid().Cols("*,Auto").Children(
                new StackPanel().Spacing(2).Children(Label(Loc.T("Tytuł karty:")), _title),
                new StackPanel().Col(1).Spacing(2).Margin(12, 0, 0, 0).Children(Label(Loc.T("Odświeżanie:")), _refresh)),
            Label(fields.Count == 0 ? Loc.T("Odpowiedź nie jest JSON-em — karta pokaże jej początek.") : Loc.T("Pola odpowiedzi (zaznacz te, które ma pokazać karta):")),
            search,
            new ScrollViewer { MaxHeight = 380, Content = _list },
            ButtonRow((Loc.T("Anuluj"), () => Complete(null), false), (Loc.T("Dodaj kartę"), Accept, true)));
    }

    private void Filter(string? text)
    {
        foreach (var row in _rows)
        {
            row.View.IsVisible = string.IsNullOrWhiteSpace(text) || row.Path.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Accept()
    {
        var title = _title.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            _title.Focus();
            return;
        }

        var refresh = _refresh.SelectedIndex <= 0 ? null : _refresh.SelectedItem as string;
        Complete(new ApiCardChoice(title, refresh, [.. _rows.Where(r => r.Selected).Select(r => r.Field())]));
    }

    private sealed class Row
    {
        private readonly CheckBox _pick;
        private readonly ComboBox _kind;
        private readonly TextBox _label;

        public Row(string path, string preview, SkinBrushes brushes)
        {
            Path = path;
            _pick = new CheckBox { Content = new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis } };
            _kind = new ComboBox { ItemsSource = new[] { Loc.T("wartość"), Loc.T("wykres"), Loc.T("status") }, SelectedIndex = 0, MinWidth = 110 };
            var last = path.LastIndexOfAny(['.', ']']);
            _label = new TextBox { Text = last < 0 ? path : path[(last + 1)..] is { Length: > 0 } tail ? tail : path, MinWidth = 140 };
            var value = new TextBlock { Text = preview, Foreground = brushes.MutedForeground, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
            View = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,1.4*,Auto,Auto") };
            View.Children.Add(_pick);
            Grid.SetColumn(value, 1);
            View.Children.Add(value);
            Grid.SetColumn(_kind, 2);
            View.Children.Add(_kind);
            Grid.SetColumn(_label, 3);
            _label.Margin = new Thickness(6, 0, 0, 0);
            View.Children.Add(_label);
        }

        public string Path { get; }

        public Grid View { get; }

        public bool Selected => _pick.IsChecked == true;

        public ApiField Field() => new(Path, _label.Text?.Trim() is { Length: > 0 } label ? label : null,
            _kind.SelectedIndex switch { 1 => ApiFieldKind.Chart, 2 => ApiFieldKind.Status, _ => ApiFieldKind.Value }, null, []);
    }
}
