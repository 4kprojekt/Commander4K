using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;

namespace Commander4K.App.Dialogs;

/// <summary>
/// Modal dialog painted from the active skin, with the design system's anatomy (see <see cref="Ds"/>): a 44 px header
/// with an optional glyph and a close button, the body (16/18 px padding, 12 px between groups) and the footer band
/// with the buttons to the right. Modern skins draw the header themselves (the window keeps only its border, and the
/// header drags it); retro skins keep the system title bar and their flat look. Escape cancels with <c>default</c>.
/// </summary>
public abstract class DialogWindow<TResult> : Window
{
    private const string FooterTag = "dialog-footer";

    protected DialogWindow(string title, SkinBrushes brushes, double width = 480)
    {
        Brushes = brushes;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        Ds.ApplySurface(this, brushes, WindowSurface.Raised);
        if (brushes.IsModern)
        {
            WindowDecorations = WindowDecorations.BorderOnly;
        }
    }

    protected SkinBrushes Brushes { get; }

    /// <summary>Glyph in the header (set before <see cref="SetBody"/>); e.g. "⚠" with <see cref="SkinBrushes.Warning"/>.</summary>
    protected string? HeaderGlyph { get; set; }

    protected IBrush? HeaderGlyphBrush { get; set; }

    /// <summary>A control on the left of the footer (a secondary action such as "Do kolejki", or a status).</summary>
    protected Control? FooterLeading { get; set; }

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

    /// <summary>Text above a field: 12/600 label on modern skins, plain text on retro ones.</summary>
    protected TextBlock Label(string text) =>
        Brushes.IsModern ? Ds.FieldLabel(Brushes, text) : new TextBlock().Text(text).TextWrapping(TextWrapping.Wrap).Margin(0, 0, 0, 6);

    /// <summary>Running text of the dialog (a question, an explanation).</summary>
    protected TextBlock Message(string text) =>
        new TextBlock().Text(text).TextWrapping(TextWrapping.Wrap).Foreground(Brushes.IsModern ? Brushes.FileForeground : Brushes.WindowForeground).Margin(0, 0, 0, 6);

    /// <summary>Right-aligned buttons; the default one is the primary (accent) button.</summary>
    protected StackPanel ButtonRow(params (string Text, Action OnClick, bool IsDefault)[] buttons) =>
        ButtonRow([.. buttons.Select(b => (b.Text, b.OnClick, b.IsDefault ? ButtonKind.Primary : ButtonKind.Secondary))]);

    /// <summary>
    /// Right-aligned buttons in explicit roles; a <see cref="ButtonKind.Primary"/> or <see cref="ButtonKind.Danger"/>
    /// one is the default (Enter) and is moved to the right end, as the design system orders them (Cancel, then the action).
    /// </summary>
    protected StackPanel ButtonRow(params (string Text, Action OnClick, ButtonKind Kind)[] buttons)
    {
        buttons = [.. buttons.Where(b => b.Kind is not (ButtonKind.Primary or ButtonKind.Danger)), .. buttons.Where(b => b.Kind is ButtonKind.Primary or ButtonKind.Danger)];
        var row = new StackPanel()
            .Orientation(Orientation.Horizontal)
            .HorizontalAlignment(HorizontalAlignment.Right)
            .Spacing(8)
            .Tag(FooterTag);
        foreach (var (text, onClick, kind) in buttons)
        {
            var button = Ds.Button(Brushes, text, onClick, kind);
            button.MinWidth = Ds.ButtonMinWidth;
            button.IsDefault = kind is ButtonKind.Primary or ButtonKind.Danger;
            row.Children.Add(button);
        }

        return row;
    }

    /// <summary>A footer row of buttons the dialog keeps references to (made with <see cref="Ds.Button"/>), in the given order.</summary>
    protected StackPanel FooterRow(params Button[] buttons)
    {
        var row = new StackPanel().Orientation(Orientation.Horizontal).HorizontalAlignment(HorizontalAlignment.Right).Spacing(8).Tag(FooterTag);
        foreach (var button in buttons)
        {
            button.MinWidth = Math.Max(button.MinWidth, Ds.ButtonMinWidth);
            row.Children.Add(button);
        }

        return row;
    }

    /// <summary>Header, body and — when the last child is a <see cref="ButtonRow"/> — the footer band.</summary>
    protected void SetBody(params Control[] children)
    {
        var footer = children.Length > 0 && children[^1] is StackPanel { Tag: FooterTag } row ? row : null;
        var body = new StackPanel().Margin(Ds.BodyPadding).Spacing(Brushes.IsModern ? 6 : 0)
            .Children(footer is null ? children : children[..^1]);
        var dock = new DockPanel();
        if (Brushes.IsModern)
        {
            var header = Ds.DialogHeader(Brushes, Title ?? string.Empty, HeaderGlyph, HeaderGlyphBrush);
            var close = Ds.Button(Brushes, "✕", () => Close(default(TResult)), ButtonKind.Ghost);
            close.Width = 28;
            close.Height = 28;
            close.Padding = new Thickness(0);
            close.IsTabStop = false;
            ToolTip.SetTip(close, Loc.T("Zamknij (Esc)"));
            var bar = new DockPanel { Background = Avalonia.Media.Brushes.Transparent };
            DockPanel.SetDock(close, Dock.Right);
            close.Margin = new Thickness(0, 0, 8, 0);
            close.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(close);
            header.BorderThickness = new Thickness(0);
            bar.Children.Add(header);
            var band = new Border { BorderBrush = Brushes.PanelBorder, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
            band.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(band).Properties.IsLeftButtonPressed)
                {
                    BeginMoveDrag(e);
                }
            };
            DockPanel.SetDock(band, Dock.Top);
            dock.Children.Add(band);
        }

        if (footer is not null)
        {
            // The row itself goes into the band, so callers can still reach its buttons (default/focus handling).
            var band = Ds.Footer(Brushes, FooterLeading, footer);
            DockPanel.SetDock(band, Dock.Bottom);
            dock.Children.Add(band);
        }

        dock.Children.Add(body);
        Content = dock;
    }
}

public sealed class InputDialog : DialogWindow<string?>
{
    private readonly TextBox _input;

    public InputDialog(SkinBrushes brushes, string title, string prompt, string initial = "") : base(title, brushes)
    {
        _input = new TextBox().Text(initial);
        SetBody(Label(prompt), _input, ButtonRow((Loc.T("Anuluj"), () => Complete(null), false), ("OK", Accept, true)));
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
    /// <param name="danger">The confirmation deletes or overwrites something: a red button and a warning glyph.</param>
    public ConfirmDialog(SkinBrushes brushes, string title, string message, string? confirmText = null, bool cancelIsDefault = false, bool danger = false)
        : base(title, brushes)
    {
        if (danger)
        {
            HeaderGlyph = "⚠";
            HeaderGlyphBrush = brushes.Error;
        }

        var cancelEntry = (Loc.T("Anuluj"), (Action)(() => Complete(false)), cancelIsDefault ? ButtonKind.Primary : ButtonKind.Secondary);
        var confirmEntry = (confirmText ?? Loc.T("Tak"), (Action)(() => Complete(true)),
            cancelIsDefault ? ButtonKind.Secondary : danger ? ButtonKind.Danger : ButtonKind.Primary);

        // The default (Enter) button is always the rightmost one.
        var row = cancelIsDefault ? ButtonRow(confirmEntry, cancelEntry) : ButtonRow(cancelEntry, confirmEntry);
        CancelButton = row.Children.OfType<Button>().ElementAt(cancelIsDefault ? 1 : 0);
        SetBody(Message(message), row);
        if (cancelIsDefault)
        {
            Opened += (sender, args) => CancelButton.Focus();
        }
    }

    public Button CancelButton { get; }
}

public enum LinkOpenTarget : byte
{
    SystemBrowser,
    ActivePanel,
}

/// <summary>Asks where to open an address or a folder: the system (browser / Explorer) or the active panel.</summary>
public sealed class LinkOpenDialog : DialogWindow<LinkOpenTarget?>
{
    public LinkOpenDialog(SkinBrushes brushes, string url, bool folder = false)
        : base(folder ? Loc.T("Otwórz folder") : Loc.T("Otwórz adres"), brushes)
    {
        SetBody(
            Label(folder ? Loc.T("Gdzie otworzyć ten folder?") : Loc.T("Gdzie otworzyć ten adres?")),
            Label(url),
            ButtonRow(
                (folder ? Loc.T("Eksplorator Windows") : Loc.T("Przeglądarka systemowa"), () => Complete(LinkOpenTarget.SystemBrowser), false),
                (Loc.T("Aktywny panel"), () => Complete(LinkOpenTarget.ActivePanel), true),
                (Loc.T("Anuluj"), () => Complete(null), false)));
    }
}

/// <summary>The one way the app opens a folder: always the same question, then Explorer or the active panel.</summary>
public static class FolderOpener
{
    /// <summary>Set by the main window: navigates its active panel to a path.</summary>
    public static Action<string>? OpenInPanel { get; set; }

    public static async Task OpenAsync(Window owner, SkinBrushes brushes, string path, bool createIfMissing = true)
    {
        if (createIfMissing)
        {
            Directory.CreateDirectory(path);
        }

        switch (await new LinkOpenDialog(brushes, path, folder: true).ShowAsync(owner))
        {
            case LinkOpenTarget.SystemBrowser when Directory.Exists(path):
                using (Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false }))
                {
                }

                break;
            case LinkOpenTarget.ActivePanel:
                OpenInPanel?.Invoke(path);
                break;
        }
    }
}

public sealed class MessageDialog : DialogWindow<bool>
{
    public MessageDialog(SkinBrushes brushes, string title, string message, bool monospace = false) : base(title, brushes, monospace ? 640 : 480)
    {
        var text = new SelectableTextBlock().Text(message).TextWrapping(TextWrapping.Wrap);
        if (monospace)
        {
            text.FontFamily = brushes.MonospaceFamily;
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
        HeaderGlyph = move ? "➜" : "⧉";
        FooterLeading = Ds.Button(brushes, Loc.T("Do kolejki (F2)"), () => Accept(queue: true));
        SetBody(
            Label($"{(move ? Loc.T("Przenieś") : Loc.T("Kopiuj"))} {description} do:"),
            _target,
            _overwrite.Margin(0, 8, 0, 0),
            ButtonRow(
                (Loc.T("Anuluj"), () => Complete(null), false),
                (move ? Loc.T("Przenieś") : Loc.T("Kopiuj"), () => Accept(queue: false), true)));
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
        var body = new List<Control> { Message(Loc.T("„{0}” wymaga zalogowania.", realm)), Label(Loc.T("Użytkownik")), _user, Label(Loc.T("Hasło")).Margin(0, 6, 0, 4), _password };
        if (allowKey)
        {
            _keyFile = new TextBox().IsEnabled(false).Text(Plugins.BuiltIn.Remote.RemoteLogin.DefaultKeyFiles().FirstOrDefault() ?? string.Empty)
                .PlaceholderText(@"C:\Users\…\.ssh\id_ed25519");
            _useKey = new CheckBox().Content(Loc.T("Zaloguj kluczem prywatnym (hasło powyżej = hasło klucza)"));
            _useKey.IsCheckedChanged += (_, _) => _keyFile.IsEnabled = _useKey.IsChecked == true;
            body.Add(_useKey.Margin(0, 8, 0, 0));
            body.Add(_keyFile.Margin(0, 4, 0, 0));
        }

        HeaderGlyph = "🔑";
        body.Add(ButtonRow((Loc.T("Anuluj"), () => Complete(null), false), (Loc.T("Zaloguj"), Accept, true)));
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
        : base(mismatch ? Loc.T("UWAGA: zmieniony klucz hosta") : Loc.T("Nieznany host SSH"), brushes, 560)
    {
        var message = mismatch
            ? Loc.T("Serwer {0}:{1} przedstawił INNY klucz niż poprzednio. Może to oznaczać atak typu man-in-the-middle ", host, port) +
              Loc.T("albo reinstalację serwera. Połącz się tylko, jeśli wiesz, dlaczego klucz się zmienił.")
            : Loc.T("Łączysz się z {0}:{1} po raz pierwszy. Sprawdź odcisk klucza z administratorem serwera, zanim mu zaufasz.", host, port);
        var print = Ds.Code(brushes, "SHA256:" + fingerprint);
        if (mismatch)
        {
            print.Foreground = brushes.Error;
        }

        HeaderGlyph = mismatch ? "⚠" : "🔑";
        HeaderGlyphBrush = mismatch ? brushes.Error : brushes.Warning;
        SetBody(Message(message), Ds.Box(brushes, print),
            ButtonRow(
                (Loc.T("Anuluj"), () => Complete(false), mismatch ? ButtonKind.Primary : ButtonKind.Secondary),
                (mismatch ? Loc.T("Zaufaj nowemu kluczowi") : Loc.T("Zaufaj i połącz"), () => Complete(true), mismatch ? ButtonKind.Danger : ButtonKind.Primary)));
    }
}

/// <param name="Keywords">Extra searchable text that is not displayed (command ids, synonyms).</param>
public sealed record PickerItem(string Label, string? Detail, string Value, string? Keywords = null, bool IsSeparator = false);

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
        _list = new ListBox().MaxHeight(420).Background(Brushes.PanelBackground).BorderThickness(new Thickness(0));
        _list.DoubleTapped += (_, _) => Accept();
        var listCard = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Background = Brushes.PanelBackground,
            BorderBrush = Brushes.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Brushes.IsModern ? 6 : 0),
            ClipToBounds = true,
            Child = _list,
        };
        SetBody(
            hint is null ? new Panel() : Label(hint),
            Ds.SearchField(Brushes, _filter),
            listCard,
            ButtonRow((Loc.T("Anuluj"), () => Complete(null), false), ("OK", Accept, true)));
        ApplyFilter();
        Opened += (_, _) => _filter.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && _filter.IsFocused && _list.ItemCount > 0)
        {
            _list.SelectedIndex = NextSelectableIndex(_list.SelectedIndex, e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ApplyFilter()
    {
        // Word-based, case- and accent-insensitive: "skor norton" finds "Skórka: Norton Commander".
        var candidates = _items.Where(item => !item.IsSeparator).ToArray();
        var matches = Core.Configuration.SettingsCatalog.Search(
            candidates, _filter.Text, i => i.Label, i => [i.Detail ?? string.Empty, i.Keywords ?? string.Empty]);
        var matched = matches.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        var sourceItems = _items.ToArray();
        var separatorIndex = Array.FindIndex(sourceItems, item => item.IsSeparator);
        var visible = new List<PickerItem>();
        var beforeSeparator = false;
        var afterSeparator = false;
        for (var i = 0; i < sourceItems.Length; i++)
        {
            var item = sourceItems[i];
            if (item.IsSeparator)
            {
                separatorIndex = i;
                continue;
            }

            if (!matched.Contains(item.Value))
            {
                continue;
            }

            visible.Add(item);
            if (separatorIndex < 0 || i < separatorIndex)
            {
                beforeSeparator = true;
            }
            else
            {
                afterSeparator = true;
            }
        }

        if (separatorIndex >= 0 && beforeSeparator && afterSeparator)
        {
            var split = visible.FindIndex(item => Array.FindIndex(sourceItems, source => source.Value == item.Value) > separatorIndex);
            if (split >= 0)
            {
                visible.Insert(split, sourceItems[separatorIndex]);
            }
        }

        _list.ItemsSource = visible.Select(i => new ListBoxItem
        {
            Tag = i,
            IsHitTestVisible = !i.IsSeparator,
            Height = i.IsSeparator ? 9 : double.NaN,
            Content = i.IsSeparator ? new Separator().Margin(4, 3) : new StackPanel().Children(
                new TextBlock().Text(i.Label).FontWeight(FontWeight.SemiBold),
                new TextBlock().Text(i.Detail ?? string.Empty).FontSize(11).Foreground(Brushes.MutedForeground).IsVisible(i.Detail is not null)),
        }).ToList();
        _list.SelectedIndex = visible.FindIndex(item => !item.IsSeparator);
    }

    private void Accept()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is PickerItem { IsSeparator: false } item)
        {
            Complete(item);
        }
    }

    private int NextSelectableIndex(int current, int direction)
    {
        var index = current;
        do
        {
            index += direction;
        }
        while (index >= 0 && index < _list.ItemCount
            && (_list.Items[index] as ListBoxItem)?.Tag is PickerItem { IsSeparator: true });

        return Math.Clamp(index, 0, Math.Max(0, _list.ItemCount - 1));
    }
}
