using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;

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
        Background = brushes.IsModern ? brushes.Surface2 : brushes.WindowBackground;
        Foreground = brushes.WindowForeground;
        FontFamily = brushes.FontFamily;
        FontSize = brushes.FontSize;
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

    private const string FooterTag = "dialog-footer";

    /// <summary>Right-aligned buttons (design system: 30 px, min 88 px; the default one filled with the accent).</summary>
    protected StackPanel ButtonRow(params (string Text, Action OnClick, bool IsDefault)[] buttons)
    {
        var row = new StackPanel()
            .Orientation(Orientation.Horizontal)
            .HorizontalAlignment(HorizontalAlignment.Right)
            .Spacing(8)
            .Tag(FooterTag);
        foreach (var (text, onClick, isDefault) in buttons)
        {
            var button = new Button().Content(text).MinWidth(88).Height(30).HorizontalContentAlignment(HorizontalAlignment.Center)
                .VerticalContentAlignment(VerticalAlignment.Center).IsDefault(isDefault).OnClick(_ => onClick());
            if (isDefault)
            {
                button.Background = Brushes.Accent;
                button.BorderBrush = Brushes.Accent;
                button.Foreground = Avalonia.Media.Brushes.White;
                button.FontWeight = FontWeight.SemiBold;
            }

            row.Children.Add(button);
        }

        return row;
    }

    /// <summary>Body with 16/18 px padding; a trailing <see cref="ButtonRow"/> becomes the footer band (header colour, top rule).</summary>
    protected void SetBody(params Control[] children)
    {
        var footer = children.Length > 0 && children[^1] is StackPanel { Tag: FooterTag } row ? row : null;
        var body = new StackPanel().Margin(18, 16).Spacing(Brushes.IsModern ? 6 : 0)
            .Children(footer is null ? children : children[..^1]);
        if (footer is null)
        {
            Content = body;
            return;
        }

        var band = new Border()
            .Background(Brushes.IsModern ? Brushes.HeaderBackground : Avalonia.Media.Brushes.Transparent)
            .BorderBrush(Brushes.PanelBorder)
            .BorderThickness(new Thickness(0, Brushes.IsModern ? 1 : 0, 0, 0))
            .Padding(18, 12)
            .Child(footer);
        DockPanel.SetDock(band, Dock.Bottom);
        Content = new DockPanel().Children(band, body);
    }
}

public sealed class InputDialog : DialogWindow<string?>
{
    private readonly TextBox _input;

    public InputDialog(SkinBrushes brushes, string title, string prompt, string initial = "") : base(title, brushes)
    {
        _input = new TextBox().Text(initial);
        SetBody(Label(prompt), _input, ButtonRow(("OK", Accept, true), (Loc.T("Anuluj"), () => Complete(null), false)));
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
    /// <param name="cancelIsDefault">For dialogs that open unprompted (an MCP client asks): Cancel is the default and
    /// focused button, so an Enter or Space meant for the panel does not confirm.</param>
    public ConfirmDialog(SkinBrushes brushes, string title, string message, string? confirmText = null, bool cancelIsDefault = false) : base(title, brushes)
    {
        var row = ButtonRow((confirmText ?? Loc.T("Tak"), () => Complete(true), !cancelIsDefault), (Loc.T("Anuluj"), () => Complete(false), cancelIsDefault));
        SetBody(Label(message), row);
        CancelButton = row.Children.OfType<Button>().ElementAt(1);
        if (cancelIsDefault)
        {
            Opened += (sender, args) => CancelButton.Focus();
        }
    }

    public Button CancelButton { get; }
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
        : base(move ? Loc.T("Przenieś") : Loc.T("Kopiuj"), brushes, 560)
    {
        _target = new TextBox().Text(target);
        _overwrite = new CheckBox().Content(Loc.T("Nadpisuj istniejące pliki"));
        SetBody(
            Label($"{(move ? Loc.T("Przenieś") : Loc.T("Kopiuj"))} {description} do:"),
            _target,
            _overwrite.Margin(0, 8, 0, 0),
            ButtonRow(
                ("OK", () => Accept(queue: false), true),
                (Loc.T("Do kolejki (F2)"), () => Accept(queue: true), false),
                (Loc.T("Anuluj"), () => Complete(null), false)));
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

/// <param name="KeyFile">SSH private key file (the password is then its passphrase); <c>null</c> = password login.</param>
public sealed record LoginResult(string UserName, string Password, string? KeyFile);

public sealed class LoginDialog : DialogWindow<LoginResult?>
{
    private readonly TextBox _user;
    private readonly TextBox _password;
    private readonly CheckBox? _useKey;
    private readonly TextBox? _keyFile;

    /// <param name="allowKey">SSH: offer a private key file instead of a password.</param>
    /// <param name="lockUser">The user name is fixed (mail accounts); only the password is asked.</param>
    public LoginDialog(SkinBrushes brushes, string realm, string? userName = null, bool allowKey = false, bool lockUser = false) : base(Loc.T("Logowanie"), brushes)
    {
        _user = new TextBox().Text(userName ?? string.Empty).PlaceholderText(Loc.T("użytkownik (np. DOMENA\\jan)"));
        _user.IsEnabled = !lockUser;
        _password = new TextBox().PasswordChar('•').PlaceholderText(Loc.T("hasło"));
        var body = new List<Control> { Label(Loc.T("„{0}” wymaga zalogowania.", realm)), _user, _password.Margin(0, 6, 0, 0) };
        if (allowKey)
        {
            _keyFile = new TextBox().IsEnabled(false).Text(Plugins.BuiltIn.Remote.RemoteLogin.DefaultKeyFiles().FirstOrDefault() ?? string.Empty)
                .PlaceholderText(@"C:\Users\…\.ssh\id_ed25519");
            _useKey = new CheckBox().Content(Loc.T("Zaloguj kluczem prywatnym (hasło powyżej = hasło klucza)"));
            _useKey.IsCheckedChanged += (_, _) => _keyFile.IsEnabled = _useKey.IsChecked == true;
            body.Add(_useKey.Margin(0, 8, 0, 0));
            body.Add(_keyFile.Margin(0, 4, 0, 0));
        }

        body.Add(ButtonRow((Loc.T("Zaloguj"), Accept, true), (Loc.T("Anuluj"), () => Complete(null), false)));
        SetBody([.. body]);
        Opened += (_, _) => (string.IsNullOrEmpty(_user.Text) ? _user : _password).Focus();
    }

    private void Accept()
    {
        if (!string.IsNullOrWhiteSpace(_user.Text))
        {
            var key = _useKey?.IsChecked == true && !string.IsNullOrWhiteSpace(_keyFile?.Text) ? _keyFile!.Text!.Trim() : null;
            Complete(new LoginResult(_user.Text.Trim(), _password.Text ?? string.Empty, key));
        }
    }
}

public sealed class HostKeyDialog : DialogWindow<bool>
{
    public HostKeyDialog(SkinBrushes brushes, string host, int port, string fingerprint, bool mismatch)
        : base(mismatch ? "UWAGA: zmieniony klucz hosta" : Loc.T("Nieznany host SSH"), brushes, 560)
    {
        var message = mismatch
            ? Loc.T("Serwer {0}:{1} przedstawił INNY klucz niż poprzednio. Może to oznaczać atak typu man-in-the-middle ", host, port) +
              Loc.T("albo reinstalację serwera. Połącz się tylko, jeśli wiesz, dlaczego klucz się zmienił.")
            : Loc.T("Łączysz się z {0}:{1} po raz pierwszy. Sprawdź odcisk klucza z administratorem serwera, zanim mu zaufasz.", host, port);
        var print = new SelectableTextBlock().Text("SHA256:" + fingerprint).FontFamily(FontFamily.Parse("Cascadia Mono, Consolas")).Margin(0, 4, 0, 0);
        if (mismatch)
        {
            print.Foreground = brushes.Error;
        }

        SetBody(Label(message), print,
            ButtonRow((mismatch ? Loc.T("Zaufaj nowemu kluczowi") : Loc.T("Zaufaj i połącz"), () => Complete(true), !mismatch), (Loc.T("Anuluj"), () => Complete(false), mismatch)));
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
        _filter = new TextBox().PlaceholderText(Loc.T("Filtruj…"));
        _filter.TextChanged += (_, _) => ApplyFilter();
        _list = new ListBox().MaxHeight(420).Margin(0, 6, 0, 0);
        _list.DoubleTapped += (_, _) => Accept();
        SetBody(
            hint is null ? new Panel() : Label(hint),
            _filter,
            _list,
            ButtonRow(("OK", Accept, true), (Loc.T("Anuluj"), () => Complete(null), false)));
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
