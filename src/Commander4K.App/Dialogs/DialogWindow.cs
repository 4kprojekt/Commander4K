using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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

    /// <summary>Tints the header band (design system: warningBackground for a warning, errorBackground for danger).</summary>
    protected IBrush? HeaderBackground { get; set; }

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
        if (CreateHeaderBand() is { } headerBand)
        {
            DockPanel.SetDock(headerBand, Dock.Top);
            dock.Children.Add(headerBand);
        }

        if (footer is not null)
        {
            // The row itself goes into the band, so callers can still reach its buttons (default/focus handling).
            var band = Ds.Footer(Brushes, FooterLeading, footer);
            DockPanel.SetDock(band, Dock.Bottom);
            dock.Children.Add(band);
        }

        dock.Children.Add(body);

        SetFramedContent(dock);
    }

    /// <summary>
    /// The 44 px header band with the title, the optional glyph, ✕ and drag-to-move — modern skins only (retro skins
    /// keep the system title bar, so this returns <c>null</c>).
    /// </summary>
    protected Control? CreateHeaderBand()
    {
        if (!Brushes.IsModern)
        {
            return null;
        }

        var header = Ds.DialogHeader(Brushes, Title ?? string.Empty, HeaderGlyph, HeaderGlyphBrush);
        var close = Ds.Button(Brushes, "✕", () => Close(default(TResult)), ButtonKind.Ghost);
        close.Width = 28;
        close.Height = 28;
        close.Padding = new Thickness(0);
        close.IsTabStop = false;
        ToolTip.SetTip(close, Loc.T("Zamknij (Esc)"));
        var bar = new DockPanel { Background = HeaderBackground ?? Avalonia.Media.Brushes.Transparent };
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
        return band;
    }

    /// <summary>Sets the window content inside the design's 1 px frame (modern skins).</summary>
    protected void SetFramedContent(Control content)
    {
        // Without the system title bar Windows draws no visible edge: the design system's 1 px panelBorder frame
        // keeps the dialog from floating over what is behind it.
        Content = Brushes.IsModern
            ? new Border { BorderBrush = Brushes.PanelBorder, BorderThickness = new Thickness(1), Child = content }
            : content;
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
    /// <param name="error">Why the dialog is shown again (a refused password), as an error notice above the fields.</param>
    public LoginDialog(SkinBrushes brushes, string realm, string? userName = null, bool allowKey = false, bool lockUser = false, string? error = null)
        : base(Loc.T("Logowanie"), brushes)
    {
        _user = new TextBox().Text(userName ?? string.Empty).PlaceholderText(Loc.T("użytkownik (np. DOMENA\\jan)"));
        _user.IsEnabled = !lockUser;
        _password = new TextBox().PasswordChar('•').PlaceholderText(Loc.T("hasło"));
        var body = new List<Control> { Message(Loc.T("„{0}” wymaga zalogowania.", realm.Replace("://@", "://", StringComparison.Ordinal))), Label(Loc.T("Użytkownik")), _user, Label(Loc.T("Hasło")).Margin(0, 6, 0, 4), _password };
        if (error is not null)
        {
            body.Insert(0, Ds.Notice(brushes, error, NoticeKind.Error).Margin(0, 0, 0, 6));
        }

        if (allowKey)
        {
            _keyFile = new TextBox().IsEnabled(false).Text(Plugins.BuiltIn.Remote.RemoteLogin.DefaultKeyFiles().FirstOrDefault() ?? string.Empty)
                .PlaceholderText(@"C:\Users\…\.ssh\id_ed25519");
            _useKey = new CheckBox().Content(Loc.T("Zaloguj kluczem prywatnym (hasło powyżej = hasło klucza)"));
            _useKey.IsCheckedChanged += (_, _) => _keyFile.IsEnabled = _useKey.IsChecked == true;
            var browse = new Button().Col(1).Content(Loc.T("Wybierz…")).Margin(6, 0, 0, 0).IsEnabled(false);
            browse.Click += async (_, _) => _keyFile.Text = await SshKeyPicker.PickAsync(this) ?? _keyFile.Text;
            _useKey.IsCheckedChanged += (_, _) => browse.IsEnabled = _useKey.IsChecked == true;
            body.Add(_useKey.Margin(0, 8, 0, 0));
            body.Add(new Grid().Cols("*,Auto").Margin(0, 4, 0, 0).Children(_keyFile, browse));
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

/// <summary>The system file picker for an SSH private key, starting in <c>~/.ssh</c>.</summary>
internal static class SshKeyPicker
{
    public static async Task<string?> PickAsync(Window owner)
    {
        var sshFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var start = Directory.Exists(sshFolder) ? await owner.StorageProvider.TryGetFolderFromPathAsync(sshFolder) : null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Klucz prywatny SSH"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
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
        HeaderBackground = mismatch ? brushes.ErrorBackground : brushes.WarningBackground;

        // A changed key: Cancel is the one default action and trusting is a plain button (one filled button per dialog).
        SetBody(Message(message), Ds.Box(brushes, print),
            ButtonRow(
                (mismatch ? Loc.T("Zaufaj nowemu kluczowi") : Loc.T("Anuluj"), () => Complete(mismatch), ButtonKind.Secondary),
                (mismatch ? Loc.T("Anuluj") : Loc.T("Zaufaj i połącz"), () => Complete(!mismatch), ButtonKind.Primary)));
    }
}

/// <param name="Keywords">Extra searchable text that is not displayed (command ids, synonyms).</param>
/// <param name="Group">Section the item is listed under (design: "POLECENIA", "USTAWIENIA"); consecutive items share it.</param>
/// <param name="Icon">16 px outline icon as path data on a 16-unit grid (<see cref="PickerIcons"/>), drawn in the muted colour.</param>
public sealed record PickerItem(string Label, string? Detail, string Value, string? Keywords = null, bool IsSeparator = false, string? Group = null, string? Icon = null);

/// <summary>Row icons of the command palette (design "Paleta komend"): 16-unit outlines, 1.25 px stroke.</summary>
public static class PickerIcons
{
    public const string Command = "M3 4.5l3 3.5-3 3.5 M8 12h5";
    public const string Setting = "M5.75 8a2.25 2.25 0 1 0 4.5 0a2.25 2.25 0 1 0 -4.5 0 M8 2v1.5 M8 12.5V14 M2 8h1.5 M12.5 8H14 M3.75 3.75l1.05 1.05 M11.2 11.2l1.05 1.05 M3.75 12.25l1.05-1.05 M11.2 4.8l1.05-1.05";
    public const string Panel = "M2 13.5h12 M3.5 11V7.5 M6.5 11V3.5 M9.5 11V6 M12.5 11V8.5";
    public const string Files = "M1.75 4.25c0-.83.67-1.5 1.5-1.5h3.1l1.4 1.5h5c.83 0 1.5.67 1.5 1.5v6.5c0 .83-.67 1.5-1.5 1.5H3.25c-.83 0-1.5-.67-1.5-1.5z";
    public const string Preview = "M1.5 8s2.5-4.5 6.5-4.5S14.5 8 14.5 8s-2.5 4.5-6.5 4.5S1.5 8 1.5 8z M6 8a2 2 0 1 0 4 0a2 2 0 1 0 -4 0";
    public const string Web = "M1.5 8a6.5 6.5 0 1 0 13 0a6.5 6.5 0 1 0 -13 0 M1.75 8h12.5 M8 1.75c-2 2-2 10.5 0 12.5 M8 1.75c2 2 2 10.5 0 12.5";
    public const string Skin = "M8 1.75a6.25 6.25 0 1 0 0 12.5c.9 0 1.25-.6 1.25-1.2 0-.8-.75-1.05-.75-1.8 0-.7.55-1.25 1.25-1.25h1.5a3 3 0 0 0 3-3C14.25 4.3 11.45 1.75 8 1.75z M5 7h.01 M7.25 4.75h.01 M10.25 5h.01";
}

/// <summary>
/// Filterable list in the design's "Paleta komend" layout (drive menu, frecency jump list, panel modes, the command
/// palette): a search field, group labels, 30 px rows with the matched text in bold accent and the shortcut or detail
/// muted on the right, the cursor row filled with <c>cursorBackground</c>, and a hint bar with "N z M" instead of
/// OK/Cancel buttons. ↑↓/PgUp/PgDn/Home/End move, Enter or a click picks, Esc cancels.
/// </summary>
public sealed class PickerDialog : DialogWindow<PickerItem?>
{
    /// <summary>Details longer than this go under the label instead of to the right of it.</summary>
    private const int InlineDetailLength = 34;

    private readonly IReadOnlyList<PickerItem> _items;
    private readonly int _total;
    private readonly TextBox _filter;
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _count;
    private readonly List<Row> _visible = [];
    private int _selected = -1;

    /// <param name="hint">Shown as the field's placeholder (and, in a titled picker, as a line above it).</param>
    /// <param name="shortcut">Key cap in the search field (the palette's "Ctrl+Shift+P").</param>
    /// <param name="showHeader">False for the command palette, which in the design is only the field and the list.</param>
    public PickerDialog(SkinBrushes brushes, string title, IReadOnlyList<PickerItem> items, string? hint = null, string? shortcut = null, bool showHeader = true)
        : base(title, brushes, 560)
    {
        _items = items;
        _total = items.Count(i => !i.IsSeparator);
        _filter = new TextBox { PlaceholderText = showHeader || hint is null ? Loc.T("Filtruj…") : hint, FontSize = 14 };
        _filter.TextChanged += (_, _) => ApplyFilter();
        _scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = 420,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        _count = new TextBlock { FontSize = 11, Foreground = Brushes.MutedForeground, VerticalAlignment = VerticalAlignment.Center };

        var search = Ds.SearchField(Brushes, _filter, 36, showHeader ? "⌕" : ">_", shortcut is null ? null : Ds.Kbd(Brushes, shortcut));
        var top = new StackPanel { Margin = new Thickness(10, 10, 10, 6), Spacing = 8 };
        if (showHeader && hint is not null)
        {
            top.Children.Add(Ds.Muted(Brushes, hint).Margin(2, 0, 0, 0));
        }

        top.Children.Add(search);

        var hints = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        hints.Children.Add(HintPair("↑↓", Loc.T("wybór")));
        hints.Children.Add(HintPair("Enter", Loc.T("wybierz")));
        hints.Children.Add(HintPair("Esc", Loc.T("zamknij")));
        var hintBar = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right);
        hintBar.Children.Add(_count);
        hintBar.Children.Add(hints);
        var hintBand = new Border
        {
            BorderBrush = Brushes.PanelBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 9),
            Margin = new Thickness(0, 6, 0, 0),
            Child = hintBar,
        };

        var dock = new DockPanel();
        if (showHeader && CreateHeaderBand() is { } header)
        {
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
        }

        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        DockPanel.SetDock(hintBand, Dock.Bottom);
        dock.Children.Add(hintBand);
        dock.Children.Add(_scroll);
        SetFramedContent(dock);

        ApplyFilter();
        Opened += (_, _) => _filter.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Down => 1,
            Key.Up => -1,
            Key.PageDown => 10,
            Key.PageUp => -10,
            _ => 0,
        };
        if (step != 0 || (e.Key is Key.Home or Key.End && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            Select(e.Key switch
            {
                Key.Home => 0,
                Key.End => _visible.Count - 1,
                _ => Math.Clamp(_selected + step, 0, _visible.Count - 1),
            });
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private StackPanel HintPair(string keys, string text)
    {
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        pair.Children.Add(Ds.Kbd(Brushes, keys));
        pair.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = Brushes.MutedForeground, VerticalAlignment = VerticalAlignment.Center });
        return pair;
    }

    private void ApplyFilter()
    {
        // Word-based, case- and accent-insensitive: "skor norton" finds "Skórka: Norton Commander". Ranked within
        // each group; groups (and the parts a separator divides) keep their order.
        var query = _filter.Text ?? string.Empty;
        var words = Core.Configuration.SettingsCatalog.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sections = new List<(string? Group, bool AfterSeparator, List<PickerItem> Items)>();
        foreach (var item in _items)
        {
            if (item.IsSeparator)
            {
                sections.Add((null, true, []));
            }
            else if (sections.Count == 0 || (item.Group is not null && sections[^1].Group != item.Group))
            {
                sections.Add((item.Group, false, [item]));
            }
            else
            {
                sections[^1].Items.Add(item);
            }
        }

        _rows.Children.Clear();
        _visible.Clear();
        var anyBefore = false;
        foreach (var (group, afterSeparator, sectionItems) in sections)
        {
            var matches = Core.Configuration.SettingsCatalog.Search(
                sectionItems, query, i => i.Label, i => [i.Detail ?? string.Empty, i.Keywords ?? string.Empty]);
            if (matches.Count == 0)
            {
                continue;
            }

            if (group is not null)
            {
                _rows.Children.Add(new TextBlock
                {
                    Text = group.ToUpper(Loc.Culture),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    LetterSpacing = 0.66,
                    Foreground = Brushes.MutedForeground,
                    Margin = new Thickness(16, anyBefore ? 8 : 2, 16, 4),
                });
            }
            else if (afterSeparator && anyBefore)
            {
                _rows.Children.Add(new Border { Height = 1, Background = Brushes.PanelBorder, Margin = new Thickness(16, 5) });
            }

            foreach (var item in matches)
            {
                var row = CreateRow(item, words);
                _visible.Add(row);
                _rows.Children.Add(row.Border);
            }

            anyBefore = true;
        }

        _count.Text = Loc.T("{0} z {1}", _visible.Count, _total);
        _selected = -1;
        Select(_visible.Count > 0 ? 0 : -1);
    }

    private Row CreateRow(PickerItem item, string[] words)
    {
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var highlights = new List<Avalonia.Controls.Documents.Run>();
        foreach (var (text, matched) in Highlight(item.Label, words))
        {
            var run = new Avalonia.Controls.Documents.Run(text);
            if (matched)
            {
                run.FontWeight = FontWeight.Bold;
                highlights.Add(run);
            }

            label.Inlines!.Add(run);
        }

        Avalonia.Controls.Shapes.Path? icon = item.Icon is { } data
            ? new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Width = 16,
                Height = 16,
                StrokeThickness = 1.25,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            }
            : null;
        var inline = item.Detail is { Length: > 0 and <= InlineDetailLength };
        var detail = item.Detail is { Length: > 0 }
            ? new TextBlock
            {
                Text = item.Detail,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = inline ? new Thickness(12, 0, 0, 0) : new Thickness(0, 1, 0, 0),
            }
            : null;

        Control content;
        if (detail is null)
        {
            content = label;
        }
        else if (inline)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(detail, 1);
            grid.Children.Add(label);
            grid.Children.Add(detail);
            content = grid;
        }
        else
        {
            content = new StackPanel { Children = { label, detail } };
        }

        if (icon is not null)
        {
            var withIcon = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            withIcon.Children.Add(icon);
            withIcon.Children.Add(content);
            content = withIcon;
        }

        var border = new Border
        {
            MinHeight = 30,
            Margin = new Thickness(6, 0),
            Padding = new Thickness(10, detail is not null && !inline ? 5 : 0),
            CornerRadius = new CornerRadius(Brushes.IsModern ? 4 : 0),
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };
        var row = new Row(item, border, label, detail, highlights, icon);
        border.PointerEntered += (_, _) => Paint(row, hover: true);
        border.PointerExited += (_, _) => Paint(row, hover: false);
        border.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                Complete(item);
            }
        };
        Paint(row, hover: false);
        return row;
    }

    private void Select(int index)
    {
        var previous = _selected;
        _selected = index;
        if (previous >= 0 && previous < _visible.Count)
        {
            Paint(_visible[previous], hover: false);
        }

        if (index >= 0 && index < _visible.Count)
        {
            Paint(_visible[index], hover: false);
            _visible[index].Border.BringIntoView();
        }
    }

    private void Paint(Row row, bool hover)
    {
        var selected = _selected >= 0 && _selected < _visible.Count && ReferenceEquals(_visible[_selected], row);
        row.Border.Background = selected ? Brushes.CursorBackground
            : hover ? Brushes.HoverBackground
            : Avalonia.Media.Brushes.Transparent;
        row.Label.Foreground = selected ? Brushes.CursorForeground : Brushes.FileForeground;
        foreach (var run in row.Highlights)
        {
            run.Foreground = selected ? Brushes.CursorForeground : Brushes.Accent;
        }

        if (row.Detail is not null)
        {
            row.Detail.Foreground = selected ? Brushes.CursorForeground : Brushes.MutedForeground;
        }

        if (row.Icon is not null)
        {
            row.Icon.Stroke = selected ? Brushes.CursorForeground : Brushes.MutedForeground;
        }
    }

    private void Accept()
    {
        if (_selected >= 0 && _selected < _visible.Count)
        {
            Complete(_visible[_selected].Item);
        }
    }

    /// <summary>Splits <paramref name="text"/> into runs, marking where a query word occurs (case- and accent-insensitive).</summary>
    internal static IEnumerable<(string Text, bool Matched)> Highlight(string text, IReadOnlyList<string> words)
    {
        if (words.Count == 0 || text.Length == 0)
        {
            return [(text, false)];
        }

        // One folded character per source character, so indexes line up with the original text.
        var folded = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            // A lone surrogate half (emoji) is invalid input for Unicode normalization.
            var one = char.IsSurrogate(text[i]) ? text[i].ToString() : Core.Configuration.SettingsCatalog.Normalize(text[i].ToString());
            folded[i] = one.Length == 1 ? one[0] : char.ToLowerInvariant(text[i]);
        }

        var haystack = new string(folded);
        var marked = new bool[text.Length];
        foreach (var word in words)
        {
            var at = haystack.IndexOf(word, StringComparison.Ordinal);
            if (at >= 0)
            {
                Array.Fill(marked, true, at, word.Length);
            }
        }

        var runs = new List<(string, bool)>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || marked[i] != marked[start])
            {
                runs.Add((text[start..i], marked[start]));
                start = i;
            }
        }

        return runs;
    }

    private sealed record Row(PickerItem Item, Border Border, TextBlock Label, TextBlock? Detail, List<Avalonia.Controls.Documents.Run> Highlights, Avalonia.Controls.Shapes.Path? Icon);
}
