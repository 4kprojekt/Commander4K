using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.App.Dialogs;
using Commander4K.App.Theming;
using Commander4K.Core.Localization;
using Commander4K.Core.Mcp;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.App.Mcp;

/// <summary>
/// Connects Claude Code (or another MCP client): switch the server on, pick the port, copy the <c>claude mcp add</c>
/// command or a <c>.mcp.json</c> entry, replace the token, choose the allowed folders, see the last calls and revoke
/// "yes for this session" answers. Non-modal; follows the skin through <c>MainWindow.FollowSkin</c>.
/// </summary>
public sealed class McpConnectWindow : ToolWindow
{
    private readonly AppHost _host;
    private readonly McpHost _mcp;
    private readonly Func<string?> _activeFolder;
    private ToggleSwitch _enabled = null!;
    private TextBox _port = null!;
    private TextBlock _status = null!;
    private SelectableTextBlock _command = null!;
    private TextBlock _where = null!;
    private int _client; // Kept across ApplySkin rebuilds.
    private StackPanel _roots = null!;
    private StackPanel _recent = null!;
    private bool _refreshPending;

    public McpConnectWindow(AppHost host, McpHost mcp, SkinBrushes brushes, Func<string?> activeFolder)
        : base(Loc.T("Połącz Claude Code — Commander4K"), brushes, 820, 780, 560)
    {
        _host = host;
        _mcp = mcp;
        _activeFolder = activeFolder;
        Build();
        mcp.Changed += ScheduleRefresh;
        Closed += (sender, args) => mcp.Changed -= ScheduleRefresh;
    }

    protected override Control BuildLayout()
    {
        var b = Brushes;
        var settings = _host.Settings.Mcp;

        _enabled = new ToggleSwitch().IsChecked(settings.Enabled).OnContent(Loc.T("Wł.")).OffContent(Loc.T("Wył."));
        _enabled.IsCheckedChanged += (sender, args) => _ = SetEnabledAsync(_enabled.IsChecked == true);
        _port = new TextBox().Width(100).Text(settings.Port.ToString(CultureInfo.InvariantCulture));
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).Foreground(b.FileForeground);
        _command = Code();
        _where = Muted(string.Empty);
        _roots = new StackPanel().Spacing(4);
        _recent = new StackPanel().Spacing(2);

        var list = new StackPanel().Spacing(10).Margin(Ds.WindowBodyPadding).Children(
            Heading(Loc.T("Serwer")),
            new StackPanel().Orientation(Orientation.Horizontal).Spacing(12).Children(
                new TextBlock().Text(Loc.T("Serwer MCP")).VerticalAlignment(VerticalAlignment.Center),
                _enabled,
                new TextBlock().Text(Loc.T("Port")).VerticalAlignment(VerticalAlignment.Center).Margin(12, 0, 0, 0),
                _port,
                SmallButton(Loc.T("Zastosuj port"), () => _ = ApplyPortAsync())),
            _status,
            Heading(Loc.T("Podłącz agenta")).Margin(0, 8, 0, 0),
            Muted(Loc.T("Wybierz klienta MCP i wklej tekst tam, gdzie wskazuje opis. Token daje dostęp do aplikacji — nie udostępniaj go. Po zmianie portu lub tokenu podłącz agenta ponownie.")),
            Ds.Segmented(b, [.. _mcp.ClientConfigs().Select(c => c.Name)], _client, index =>
            {
                _client = index;
                Refresh();
            }),
            _where,
            Box(_command),
            new StackPanel().Orientation(Orientation.Horizontal).Spacing(8).Children(
                SmallButton(Loc.T("Kopiuj"), () => _ = CopyAsync(CurrentClient().Text)),
                SmallButton(Loc.T("Nowy token"), () => _mcp.NewToken())),
            Heading(Loc.T("Foldery dostępne dla AI")).Margin(0, 8, 0, 0),
            Muted(Loc.T("Narzędzia plikowe działają tylko w tych folderach i ich podfolderach. Do folderów wtyczek, config/, data/ i folderu programu MCP nie ma dostępu (widzi tylko nazwy plików).")),
            _roots,
            SmallButton(Loc.T("Dodaj folder z aktywnego panelu"), () => _ = AddActiveFolderAsync()),
            Heading(Loc.T("Ostatnie wywołania")).Margin(0, 8, 0, 0),
            Muted(Loc.T("Nazwy klientów podają same klienty. Pełny dziennik: data/logs/plugins-RRRRMMDD.log.")),
            _recent,
            SmallButton(Loc.T("Cofnij zgody z tej sesji"), () => _mcp.RevokeSessionGrants()));

        var footer = Ds.Footer(b, null, Ds.Button(b, Loc.T("Zamknij"), Close, ButtonKind.Primary));
        var frame = Frame(Loc.T("Połącz Claude Code"),
            Loc.T("Claude Code łączy się z Commander4K przez MCP na 127.0.0.1 i może przeglądać pliki w wybranych folderach, pisać i instalować wtyczki oraz sterować panelami. O ryzykowne akcje aplikacja zawsze pyta."),
            Scroll(list), footer);
        Refresh();
        return frame;
    }

    private void ScheduleRefresh()
    {
        // Calls can arrive in bursts; one refresh per dispatcher pass is enough.
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var b = Brushes;
        var settings = _host.Settings.Mcp;
        _enabled.IsChecked = settings.Enabled; // Changed elsewhere (settings window); equal values are ignored.
        if (!_port.IsFocused)
        {
            _port.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        }
        _status.Text = _mcp.StatusText();
        _status.Foreground = _mcp.Status is McpStartResult.PortBusy or McpStartResult.Failed ? b.Error : b.FileForeground;
        var client = CurrentClient();
        _where.Text = client.Where;
        _command.Text = client.Text;

        _roots.Children.Clear();
        if (settings.AllowedRoots.Count == 0)
        {
            _roots.Children.Add(Muted(Loc.T("Brak — AI nie ma dostępu do żadnych plików.")));
        }

        foreach (var root in settings.AllowedRoots)
        {
            var path = root;
            _roots.Children.Add(new Grid().Cols("*,Auto").Children(
                new TextBlock().Text(path).VerticalAlignment(VerticalAlignment.Center).TextTrimming(TextTrimming.CharacterEllipsis).Foreground(b.FileForeground),
                SmallButton(Loc.T("Usuń"), () => _ = RemoveRootAsync(path)).Col(1)));
        }

        _recent.Children.Clear();
        var recent = _mcp.Recent;
        if (recent.Count == 0)
        {
            _recent.Children.Add(Muted(Loc.T("Jeszcze nic.")));
        }

        foreach (var call in recent.Reverse())
        {
            _recent.Children.Add(new Grid().Cols("Auto,Auto,Auto,*,Auto").Children(
                Cell(call.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), 0, b.MutedForeground),
                Cell(call.Client, 1, b.MutedForeground),
                Cell(call.Tool, 2, b.FileForeground),
                Cell(call.Summary, 3, b.MutedForeground).TextTrimming(TextTrimming.CharacterEllipsis),
                Cell(Outcome(call.Outcome), 4, call.Outcome == AuditOutcome.Allowed ? b.MutedForeground : b.Error)));
        }
    }

    private McpClientConfig CurrentClient() => _mcp.ClientConfigs()[_client];

    private static string Outcome(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Allowed => Loc.T("wykonano"),
        AuditOutcome.Denied => Loc.T("odmowa"),
        AuditOutcome.Failed => Loc.T("błąd"),
        _ => Loc.T("informacja"),
    };

    private async Task SetEnabledAsync(bool enabled)
    {
        if (_host.Settings.Mcp.Enabled == enabled)
        {
            return;
        }

        _host.Settings.Mcp.Enabled = enabled;
        await SaveAndApplyAsync();
    }

    private async Task ApplyPortAsync()
    {
        if (!int.TryParse(_port.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            _status.Text = Loc.T("Port musi być liczbą od 1024 do 65535.");
            _status.Foreground = Brushes.Error;
            return;
        }

        _host.Settings.Mcp.Port = port;
        await SaveAndApplyAsync();
    }

    private async Task AddActiveFolderAsync()
    {
        var folder = _activeFolder();
        if (!IsRealFolder(folder))
        {
            _status.Text = Loc.T("Otwórz w aktywnym panelu folder na dysku, w udziale sieciowym albo na serwerze SFTP/FTP.");
            _status.Foreground = Brushes.Error;
            return;
        }

        if (_host.Settings.Mcp.AllowedRoots.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // A new list, never an in-place edit: the server reads a snapshot taken by ApplySettingsAsync.
        _host.Settings.Mcp.AllowedRoots = [.. _host.Settings.Mcp.AllowedRoots, folder];
        await SaveAndApplyAsync();
    }

    /// <summary>
    /// Only real folders can be allowed: disk paths, <c>\\server\share</c>, <c>sftp://</c> and <c>ftp://</c>. Virtual
    /// lists (This PC <c>local://</c>, the network <c>net://</c>, a bare <c>\\server</c>), the registry and archives are
    /// not offered here (the setting "Foldery dostępne dla MCP" still takes any path).
    /// </summary>
    private static bool IsRealFolder([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        return VfsPath.GetScheme(folder) switch
        {
            VfsPath.FileScheme => !folder.Contains("://", StringComparison.Ordinal) && Path.IsPathFullyQualified(folder),
            VfsPath.SmbScheme => folder.TrimStart('\\').TrimEnd('\\').Contains('\\'),
            "sftp" or "ftp" => true,
            _ => false,
        };
    }

    private async Task RemoveRootAsync(string root)
    {
        _host.Settings.Mcp.AllowedRoots = [.. _host.Settings.Mcp.AllowedRoots.Where(r => !string.Equals(r, root, StringComparison.Ordinal))];
        await SaveAndApplyAsync();
    }

    private async Task SaveAndApplyAsync()
    {
        try
        {
            await _mcp.ApplySettingsAsync();
            await _host.SaveSettingsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = Loc.T("Nie udało się zapisać ustawień: {0}", ex.Message);
            _status.Foreground = Brushes.Error;
        }
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private SelectableTextBlock Code() => Ds.Code(Brushes);

    private Border Box(Control content) => Ds.Box(Brushes, content);

    private TextBlock Heading(string text) => Ds.SectionHeading(Brushes, text);

    private TextBlock Muted(string text) => Ds.Muted(Brushes, text);

    private static TextBlock Cell(string text, int column, IBrush brush) =>
        new TextBlock().Text(text).Col(column).FontSize(12).Margin(0, 0, 10, 0).Foreground(brush);

    private Button SmallButton(string text, Action onClick) => Ds.Button(Brushes, text, onClick, ButtonKind.Toolbar);
}
