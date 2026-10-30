using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Theming;

namespace Commander4K.App.Dialogs;

/// <summary>Modal dialog painted from the active skin; Escape cancels with <c>default</c>.</summary>
public abstract class DialogWindow<TResult> : Window
{
    protected DialogWindow(string title, SkinBrushes brushes, double width = 480)
    {
        Brushes = brushes;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        Background = brushes.WindowBackground;
        Foreground = brushes.FileForeground;
    }

    protected SkinBrushes Brushes { get; }

    public Task<TResult> ShowAsync(Window owner) => ShowDialog<TResult>(owner);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            Close(default(TResult));
            e.Handled = true;
        }
    }

    protected void Complete(TResult result) => Close(result);

    protected static TextBlock Label(string text) =>
        new TextBlock().Text(text).TextWrapping(TextWrapping.Wrap).Margin(0, 0, 0, 6);

    protected StackPanel ButtonRow(params (string Text, Action OnClick, bool IsDefault)[] buttons)
    {
        var row = new StackPanel()
            .Orientation(Orientation.Horizontal)
            .HorizontalAlignment(HorizontalAlignment.Right)
            .Spacing(8)
            .Margin(0, 14, 0, 0);
        foreach (var (text, onClick, isDefault) in buttons)
        {
            var button = new Button().Content(text).MinWidth(90).IsDefault(isDefault).OnClick(_ => onClick());
            if (isDefault)
            {
                button.Background = Brushes.Accent;
                button.Foreground = Brushes.CursorForeground;
            }

            row.Children.Add(button);
        }

        return row;
    }

    protected void SetBody(params Control[] children) =>
        Content = new StackPanel().Margin(18).Children(children);
}

public sealed class InputDialog : DialogWindow<string?>
{
    private readonly TextBox _input;

    public InputDialog(SkinBrushes brushes, string title, string prompt, string initial = "") : base(title, brushes)
    {
        _input = new TextBox().Text(initial);
        SetBody(Label(prompt), _input, ButtonRow(("OK", Accept, true), ("Anuluj", () => Complete(null), false)));
        Opened += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    private void Accept()
    {
        var text = _input.Text?.Trim();
        Complete(string.IsNullOrEmpty(text) ? null : text);
    }
}

public sealed class ConfirmDialog : DialogWindow<bool>
{
    public ConfirmDialog(SkinBrushes brushes, string title, string message, string confirmText = "Tak") : base(title, brushes)
    {
        SetBody(Label(message), ButtonRow((confirmText, () => Complete(true), true), ("Anuluj", () => Complete(false), false)));
    }
}

public sealed class MessageDialog : DialogWindow<bool>
{
    public MessageDialog(SkinBrushes brushes, string title, string message, bool monospace = false) : base(title, brushes, monospace ? 640 : 480)
    {
        var text = new SelectableTextBlock().Text(message).TextWrapping(TextWrapping.Wrap);
        if (monospace)
        {
            text.FontFamily = FontFamily.Parse("Cascadia Mono, Consolas");
        }

        SetBody(new ScrollViewer().MaxHeight(520).Content(text), ButtonRow(("OK", () => Complete(true), true)));
    }
}

public sealed record TransferRequest(string Target, Core.Jobs.OverwritePolicy Overwrite, bool Queue);

/// <summary>F5 / F6: target path, overwrite policy, and Total Commander's "F2 = queue" option.</summary>
public sealed class TransferDialog : DialogWindow<TransferRequest?>
{
    private readonly TextBox _target;
    private readonly CheckBox _overwrite;

    public TransferDialog(SkinBrushes brushes, bool move, string description, string target)
        : base(move ? "Przenieś" : "Kopiuj", brushes, 560)
    {
        _target = new TextBox().Text(target);
        _overwrite = new CheckBox().Content("Nadpisuj istniejące pliki");
        SetBody(
            Label($"{(move ? "Przenieś" : "Kopiuj")} {description} do:"),
            _target,
            _overwrite.Margin(0, 8, 0, 0),
            ButtonRow(
                ("OK", () => Accept(queue: false), true),
                ("Do kolejki (F2)", () => Accept(queue: true), false),
                ("Anuluj", () => Complete(null), false)));
        Opened += (_, _) => _target.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            Accept(queue: true);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void Accept(bool queue)
    {
        var target = _target.Text?.Trim();
        if (!string.IsNullOrEmpty(target))
        {
            Complete(new TransferRequest(target, _overwrite.IsChecked == true ? Core.Jobs.OverwritePolicy.Overwrite : Core.Jobs.OverwritePolicy.Skip, queue));
        }
    }
}

public sealed class LoginDialog : DialogWindow<System.Net.NetworkCredential?>
{
    private readonly TextBox _user;
    private readonly TextBox _password;

    public LoginDialog(SkinBrushes brushes, string realm, string? userName = null) : base("Logowanie", brushes)
    {
        _user = new TextBox().Text(userName ?? string.Empty).PlaceholderText("użytkownik (np. DOMENA\\jan)");
        _password = new TextBox().PasswordChar('•').PlaceholderText("hasło");
        SetBody(
            Label($"„{realm}” wymaga zalogowania."),
            _user,
            _password.Margin(0, 6, 0, 0),
            ButtonRow(("Zaloguj", Accept, true), ("Anuluj", () => Complete(null), false)));
        Opened += (_, _) => (string.IsNullOrEmpty(_user.Text) ? _user : _password).Focus();
    }

    private void Accept()
    {
        if (!string.IsNullOrWhiteSpace(_user.Text))
        {
            Complete(new System.Net.NetworkCredential(_user.Text.Trim(), _password.Text ?? string.Empty));
        }
    }
}

public sealed class HostKeyDialog : DialogWindow<bool>
{
    public HostKeyDialog(SkinBrushes brushes, string host, int port, string fingerprint, bool mismatch)
        : base(mismatch ? "UWAGA: zmieniony klucz hosta" : "Nieznany host SSH", brushes, 560)
    {
        var message = mismatch
            ? $"Serwer {host}:{port} przedstawił INNY klucz niż poprzednio. Może to oznaczać atak typu man-in-the-middle " +
              "albo reinstalację serwera. Połącz się tylko, jeśli wiesz, dlaczego klucz się zmienił."
            : $"Łączysz się z {host}:{port} po raz pierwszy. Sprawdź odcisk klucza z administratorem serwera, zanim mu zaufasz.";
        var print = new SelectableTextBlock().Text("SHA256:" + fingerprint).FontFamily(FontFamily.Parse("Cascadia Mono, Consolas")).Margin(0, 4, 0, 0);
        if (mismatch)
        {
            print.Foreground = brushes.Error;
        }

        SetBody(Label(message), print,
            ButtonRow((mismatch ? "Zaufaj nowemu kluczowi" : "Zaufaj i połącz", () => Complete(true), !mismatch), ("Anuluj", () => Complete(false), mismatch)));
    }
}

/// <param name="Keywords">Extra searchable text that is not displayed (command ids, synonyms).</param>
public sealed record PickerItem(string Label, string? Detail, string Value, string? Keywords = null);

/// <summary>Filterable list (drive menu, frecency jump list, skin list).</summary>
public sealed class PickerDialog : DialogWindow<PickerItem?>
{
    private readonly IReadOnlyList<PickerItem> _items;
    private readonly ListBox _list;
    private readonly TextBox _filter;

    public PickerDialog(SkinBrushes brushes, string title, IReadOnlyList<PickerItem> items, string? hint = null) : base(title, brushes, 560)
    {
        _items = items;
        _filter = new TextBox().PlaceholderText("Filtruj…");
        _filter.TextChanged += (_, _) => ApplyFilter();
        _list = new ListBox().MaxHeight(420).Margin(0, 6, 0, 0);
        _list.DoubleTapped += (_, _) => Accept();
        SetBody(
            hint is null ? new Panel() : Label(hint),
            _filter,
            _list,
            ButtonRow(("OK", Accept, true), ("Anuluj", () => Complete(null), false)));
        ApplyFilter();
        Opened += (_, _) => _filter.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && _filter.IsFocused && _list.ItemCount > 0)
        {
            _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _list.ItemCount - 1);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ApplyFilter()
    {
        // Word-based, case- and accent-insensitive: "skor norton" finds "Skórka: Norton Commander".
        var visible = Core.Configuration.SettingsCatalog.Search(
            _items, _filter.Text, i => i.Label, i => [i.Detail ?? string.Empty, i.Keywords ?? string.Empty]);
        _list.ItemsSource = visible.Select(i => new ListBoxItem
        {
            Tag = i,
            Content = new StackPanel().Children(
                new TextBlock().Text(i.Label).FontWeight(FontWeight.SemiBold),
                new TextBlock().Text(i.Detail ?? string.Empty).FontSize(11).Opacity(0.7).IsVisible(i.Detail is not null)),
        }).ToList();
        _list.SelectedIndex = visible.Count > 0 ? 0 : -1;
    }

    private void Accept()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is PickerItem item)
        {
            Complete(item);
        }
    }
}

public sealed class AddConnectionDialog : DialogWindow<Core.Configuration.ConnectionSetting?>
{
    private readonly TextBox _name;
    private readonly TextBox _path;

    public AddConnectionDialog(SkinBrushes brushes) : base("Dodaj połączenie sieciowe", brushes, 520)
    {
        _name = new TextBox().PlaceholderText("Nazwa, np. NAS – filmy");
        _path = new TextBox().PlaceholderText(@"\\nas\media   lub   sftp://jan@serwer/   lub   ftp://serwer/");
        SetBody(
            Label("Nazwa:"), _name,
            Label("Adres:").Margin(0, 10, 0, 6), _path,
            ButtonRow(("Dodaj", Accept, true), ("Anuluj", () => Complete(null), false)));
        Opened += (_, _) => _name.Focus();
    }

    private void Accept()
    {
        var path = _path.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(_name.Text) ? path : _name.Text.Trim();
        Complete(new Core.Configuration.ConnectionSetting { Name = name, Path = path });
    }
}
