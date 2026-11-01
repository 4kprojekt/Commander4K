using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Commander4K.App.Theming;
using Commander4K.Core.Configuration;
using Commander4K.Core.Localization;
using Commander4K.Core.Security;

namespace Commander4K.App.Dialogs;

/// <param name="Secret">The password or passphrase in plain text for this session (the setting keeps it only encrypted, and only when remembered).</param>
public sealed record ConnectionDialogResult(ConnectionSetting Setting, string? Secret);

/// <summary>
/// A network connection: SFTP/SSH (password, private key + passphrase, or the default <c>~/.ssh</c> keys), FTP, or a
/// Windows share. "Test" tries it before saving; the result opens in the panel right away.
/// </summary>
public sealed class ConnectionDialog : DialogWindow<ConnectionDialogResult?>
{
    private const int Sftp = 0, Ftp = 1, Smb = 2;
    private const int AuthPassword = 0, AuthKey = 1; // 2 = the default keys from ~/.ssh.

    private readonly Func<ConnectionDialogResult, Task<string?>> _test;
    private readonly ComboBox _type;
    private readonly TextBox _name;
    private readonly TextBox _host;
    private readonly TextBox _port;
    private readonly TextBox _user;
    private readonly TextBox _folder;
    private readonly ComboBox _auth;
    private readonly TextBox _password;
    private readonly TextBox _keyFile;
    private readonly TextBox _passphrase;
    private readonly CheckBox _remember;
    private readonly TextBlock _status;
    private readonly StackPanel _keyRow;
    private readonly StackPanel _hostRow;
    private readonly TextBlock _hostLabel;
    private readonly StackPanel _portRow;

    /// <param name="test">Tries the connection; returns an error message or <c>null</c> when it works.</param>
    public ConnectionDialog(SkinBrushes brushes, Func<ConnectionDialogResult, Task<string?>> test, ConnectionSetting? initialSetting = null) : base(Loc.T("Dodaj połączenie sieciowe"), brushes, 620)
    {
        _test = test;
        _type = new ComboBox().HorizontalAlignment(HorizontalAlignment.Stretch)
            .ItemsSource(new[] { "SFTP / SSH", "FTP", Loc.T("Udział Windows (SMB)") });
        _type.SelectedIndex = Sftp;
        _name = new TextBox().PlaceholderText(Loc.T("np. Serwer domowy"));
        _host = new TextBox().PlaceholderText(Loc.T("adres lub nazwa, np. 192.168.1.10 albo nas.lan"));
        _port = new TextBox().Width(90).Text("22");
        _user = new TextBox().Text(Environment.UserName);
        _folder = new TextBox().PlaceholderText(Loc.T("/ (katalog domowy na serwerze, np. /home/jan)"));
        _auth = new ComboBox().HorizontalAlignment(HorizontalAlignment.Stretch);
        _password = new TextBox().PasswordChar('•').PlaceholderText(Loc.T("hasło"));
        var defaultKey = Plugins.BuiltIn.Remote.RemoteLogin.DefaultKeyFiles().FirstOrDefault();
        _keyFile = new TextBox().Text(defaultKey ?? string.Empty).PlaceholderText(@"C:\Users\…\.ssh\id_ed25519");
        _passphrase = new TextBox().PasswordChar('•').PlaceholderText(Loc.T("hasło klucza (jeśli jest)"));
        _remember = new CheckBox().Content(Loc.T("Zapamiętaj hasło (zaszyfrowane dla tego konta Windows)")).IsChecked(true);
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12);
        _hostLabel = Label(Loc.T("Serwer:"));

        _hostRow = new StackPanel().Children(_hostLabel, new Grid().Cols("*,Auto").Children(
            _host,
            _portRow = new StackPanel().Col(1).Orientation(Orientation.Horizontal).Margin(8, 0, 0, 0).Children(
                new TextBlock().Text(Loc.T("Port:")).VerticalAlignment(VerticalAlignment.Center).Margin(0, 0, 6, 0), _port)));
        _keyRow = new StackPanel().Spacing(4).Children(
            Label(Loc.T("Plik klucza prywatnego:")),
            new Grid().Cols("*,Auto").Children(_keyFile, new Button().Col(1).Content(Loc.T("Wybierz…")).Margin(6, 0, 0, 0).OnClick(async click => await BrowseKeyAsync())),
            _passphrase);

        _type.SelectionChanged += (_, _) => UpdateFields(typeChanged: true);
        _auth.SelectionChanged += (_, _) => UpdateFields(typeChanged: false);
        HeaderGlyph = "🖧";
        HeaderGlyphBrush = brushes.NetworkForeground;
        FooterLeading = Ds.Button(brushes, Loc.T("Testuj"), () => _ = TestAsync());
        SetBody(
            Label(Loc.T("Rodzaj:")), _type,
            Label(Loc.T("Nazwa na liście:")).Margin(0, 8, 0, 6), _name,
            _hostRow.Margin(0, 8, 0, 0),
            Label(Loc.T("Użytkownik:")).Margin(0, 8, 0, 6), _user,
            Label(Loc.T("Logowanie:")).Margin(0, 8, 0, 6), _auth,
            _password.Margin(0, 6, 0, 0),
            _keyRow.Margin(0, 6, 0, 0),
            _remember.Margin(0, 6, 0, 0),
            Label(Loc.T("Folder startowy:")).Margin(0, 8, 0, 6), _folder,
            _status.Margin(0, 8, 0, 0),
            ButtonRow(
                (Loc.T("Anuluj"), () => Complete(null), false),
                (Loc.T("Zapisz i otwórz"), Accept, true)));
        UpdateFields(typeChanged: true);
        if (initialSetting is not null)
        {
            ApplyInitialSetting(initialSetting);
        }

        Opened += (_, _) => _host.Focus();
    }

    private void ApplyInitialSetting(ConnectionSetting setting)
    {
        if (!Uri.TryCreate(setting.Path, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _type.SelectedIndex = Sftp;
        UpdateFields(typeChanged: true);
        _name.Text = setting.Name;
        _host.Text = uri.Host;
        _port.Text = (uri.Port is >= 1 and <= 65535 ? uri.Port : 22).ToString();
        _user.Text = setting.UserName ?? (uri.UserInfo.Length == 0 ? Environment.UserName : Uri.UnescapeDataString(uri.UserInfo));
        _folder.Text = uri.AbsolutePath;
        if (setting.Auth == "password")
        {
            _auth.SelectedIndex = AuthPassword;
        }
        else if (setting.Auth == "key")
        {
            _auth.SelectedIndex = AuthKey;
            _keyFile.Text = setting.KeyFile ?? string.Empty;
        }

        UpdateFields(typeChanged: false);
    }

    private void UpdateFields(bool typeChanged)
    {
        var type = _type.SelectedIndex;
        if (typeChanged)
        {
            var auth = type switch
            {
                Sftp => new[] { Loc.T("Hasło"), Loc.T("Klucz prywatny (SSH)"), Loc.T("Domyślne klucze z ~/.ssh, a w razie potrzeby zapytaj") },
                Ftp => [Loc.T("Hasło"), Loc.T("Anonimowo")],
                _ => [Loc.T("Hasło"), Loc.T("Bieżące konto Windows")],
            };
            _auth.ItemsSource = auth;
            _auth.SelectedIndex = type == Sftp && Plugins.BuiltIn.Remote.RemoteLogin.DefaultKeyFiles().Count > 0 ? AuthKey : AuthPassword;
            _port.Text = type == Ftp ? "21" : "22";
            _hostLabel.Text = type == Smb ? Loc.T("Ścieżka udziału (np. \\\\nas\\filmy):") : Loc.T("Serwer:");
        }

        var method = _auth.SelectedIndex;
        _portRow.IsVisible = type != Smb;
        _folder.IsEnabled = type != Smb;
        _user.IsEnabled = !(type == Ftp && method == 1) && !(type == Smb && method == 1);
        _password.IsVisible = method == AuthPassword;
        _keyRow.IsVisible = type == Sftp && method == AuthKey;
        _remember.IsVisible = method == AuthPassword || (type == Sftp && method == AuthKey);
    }

    private async Task BrowseKeyAsync()
    {
        var sshFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var start = Directory.Exists(sshFolder) ? await StorageProvider.TryGetFolderFromPathAsync(sshFolder) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Klucz prywatny SSH"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _keyFile.Text = path;
        }
    }

    /// <summary>The connection as entered, or an error message for the status line.</summary>
    private (ConnectionDialogResult? Result, string? Error) Build()
    {
        var type = _type.SelectedIndex;
        var method = _auth.SelectedIndex;
        var host = _host.Text?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            return (null, type == Smb ? Loc.T("Podaj ścieżkę udziału.") : Loc.T("Podaj adres serwera."));
        }

        var user = _user.IsEnabled ? _user.Text?.Trim() : null;
        string path;
        if (type == Smb)
        {
            path = host.StartsWith(@"\\", StringComparison.Ordinal) ? host : @"\\" + host.TrimStart('\\', '/');
        }
        else
        {
            if (!int.TryParse(_port.Text, out var port) || port is < 1 or > 65535)
            {
                return (null, Loc.T("Port musi być liczbą od 1 do 65535."));
            }

            var scheme = type == Sftp ? "sftp" : "ftp";
            var folder = _folder.Text?.Trim() is { Length: > 0 } f ? (f.StartsWith('/') ? f : "/" + f) : "/";
            var defaultPort = type == Sftp ? 22 : 21;
            var userPart = string.IsNullOrEmpty(user) ? string.Empty : Uri.EscapeDataString(user) + "@";
            path = $"{scheme}://{userPart}{host}{(port == defaultPort ? string.Empty : ":" + port)}{folder}";
        }

        var auth = method switch
        {
            AuthPassword => "password",
            AuthKey when type == Sftp => "key",
            _ => null, // Default keys from ~/.ssh: nothing to store.
        };
        if (auth == "key" && string.IsNullOrWhiteSpace(_keyFile.Text))
        {
            return (null, Loc.T("Wskaż plik klucza prywatnego."));
        }

        var secret = auth switch
        {
            "password" => _password.Text ?? string.Empty,
            "key" => string.IsNullOrEmpty(_passphrase.Text) ? null : _passphrase.Text,
            _ => null,
        };
        var setting = new ConnectionSetting
        {
            Name = _name.Text?.Trim() is { Length: > 0 } name ? name : (string.IsNullOrEmpty(user) || type == Smb ? host : $"{user}@{host}"),
            Path = path,
            Auth = auth,
            UserName = string.IsNullOrEmpty(user) ? null : user,
            KeyFile = auth == "key" ? _keyFile.Text!.Trim() : null,
            Secret = secret is not null && _remember.IsChecked == true ? SecretProtector.Protect(secret) : null,
        };
        return (new ConnectionDialogResult(setting, secret), null);
    }

    private async Task TestAsync()
    {
        var (result, error) = Build();
        if (result is null)
        {
            ShowStatus(error!, isError: true);
            return;
        }

        ShowStatus(Loc.T("Łączenie z {0}…", result.Setting.Path), isError: false);
        var failure = await _test(result);
        ShowStatus(failure is null ? Loc.T("✓ Połączenie działa.") : "✕ " + failure, isError: failure is not null);
    }

    private void Accept()
    {
        var (result, error) = Build();
        if (result is null)
        {
            ShowStatus(error!, isError: true);
            return;
        }

        Complete(result);
    }

    private void ShowStatus(string text, bool isError)
    {
        _status.Text = text;
        _status.Foreground = isError ? Brushes.Error : Brushes.Success;
    }
}
