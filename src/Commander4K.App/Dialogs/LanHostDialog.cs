using Avalonia.Controls;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Commander4K.App.Theming;
using Commander4K.Core.Configuration;
using Commander4K.Core.Localization;

namespace Commander4K.App.Dialogs;

public sealed class LanHostDialog : DialogWindow<LanHostSetting?>
{
    private readonly TextBox _name;
    private readonly TextBox _host;
    private readonly TextBox _serviceName;
    private readonly TextBox _servicePort;
    private readonly ComboBox _serviceScheme;
    private readonly TextBlock _status;

    public LanHostDialog(SkinBrushes brushes) : base(Loc.T("Dodaj host LAN"), brushes, 520)
    {
        _name = new TextBox().PlaceholderText(Loc.T("np. MikroTik"));
        _host = new TextBox().PlaceholderText(Loc.T("adres IP lub nazwa, np. 192.168.1.1 albo mikrotik.lan"));
        _serviceName = new TextBox().PlaceholderText(Loc.T("np. WWW"));
        _servicePort = new TextBox().Text("80").Width(100);
        _serviceScheme = new ComboBox().ItemsSource(new[] { "http", "https" }).SelectedIndex(0).Width(100);
        _serviceScheme.SelectionChanged += (_, _) =>
        {
            if (_servicePort.Text is "80" or "443")
            {
                _servicePort.Text = _serviceScheme.SelectedIndex == 1 ? "443" : "80";
            }
        };
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).FontSize(12);

        SetBody(
            Label(Loc.T("Nazwa:")), _name,
            Label(Loc.T("Adres hosta:")).Margin(0, 8, 0, 6), _host,
            Label(Loc.T("Usługa (opcjonalnie):")).Margin(0, 8, 0, 6),
            new Grid().Cols("*,Auto,Auto").Children(
                _serviceName,
                _serviceScheme.Col(1).Margin(6, 0, 0, 0),
                _servicePort.Col(2).Margin(6, 0, 0, 0)),
            new TextBlock().Text(Loc.T("Podaj usługę i port, aby dodać np. WWW do tego hosta. Skan popularnych portów ustawisz osobno w „Skanuj adresy IP”."))
                .TextWrapping(TextWrapping.Wrap).Margin(0, 8, 0, 0),
            _status.Margin(0, 8, 0, 0),
            ButtonRow(
                (Loc.T("Dodaj"), Accept, true),
                (Loc.T("Anuluj"), () => Complete(null), false)));
        Opened += (_, _) => _host.Focus();
    }

    private void Accept()
    {
        var host = _host.Text?.Trim() ?? string.Empty;
        if (!IsValidHost(host))
        {
            _status.Text = Loc.T("Podaj adres IPv4 albo nazwę hosta bez protokołu, portu i ścieżki.");
            _status.Foreground = Brushes.Error;
            return;
        }

        var name = _name.Text?.Trim();
        var serviceName = _serviceName.Text?.Trim();
        var services = new List<LanHostServiceSetting>();
        if (!string.IsNullOrEmpty(serviceName))
        {
            if (!int.TryParse(_servicePort.Text, out var port) || port is < 1 or > 65535)
            {
                _status.Text = Loc.T("Port usługi musi być liczbą od 1 do 65535.");
                _status.Foreground = Brushes.Error;
                return;
            }

            services.Add(new LanHostServiceSetting
            {
                Name = serviceName,
                Port = port,
                Scheme = _serviceScheme.SelectedIndex == 1 ? "https" : "http",
            });
        }

        Complete(new LanHostSetting
        {
            Name = string.IsNullOrEmpty(name) ? host : name,
            Host = host,
            Services = services,
        });
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', ':', '@']) >= 0)
        {
            return false;
        }

        return System.Net.IPAddress.TryParse(host, out var address)
            ? address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            : Uri.CheckHostName(host) == UriHostNameType.Dns;
    }
}
