using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Declarative;
using Avalonia.Media;
using Avalonia.Threading;
using Commander4K.App.Composition;
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
public sealed class McpConnectWindow : Window
{
    private readonly AppHost _host;
    private readonly McpHost _mcp;
    private readonly Func<string?> _activeFolder;
    private SkinBrushes _brushes;
    private ScrollViewer _scroll = null!;
    private ToggleSwitch _enabled = null!;
    private TextBox _port = null!;
    private TextBlock _status = null!;
    private SelectableTextBlock _command = null!;
    private SelectableTextBlock _snippet = null!;
    private StackPanel _roots = null!;
    private StackPanel _recent = null!;
    private bool _refreshPending;

    public McpConnectWindow(AppHost host, McpHost mcp, SkinBrushes brushes, Func<string?> activeFolder)
    {
        _host = host;
        _mcp = mcp;
        _brushes = brushes;
        _activeFolder = activeFolder;
        Title = Loc.T("Połącz Claude Code — Commander4K");
        Width = 820;
        Height = 780;
        MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildLayout();
        mcp.Changed += ScheduleRefresh;
        Closed += (sender, args) => mcp.Changed -= ScheduleRefresh;
    }

    public void ApplySkin(SkinBrushes brushes)
    {
        _brushes = brushes;
        var offset = _scroll.Offset;
        BuildLayout();
        Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    private void BuildLayout()
    {
        var b = _brushes;
        Background = b.PanelBackground;
        Foreground = b.WindowForeground;
        FontFamily = b.FontFamily;
        FontSize = b.FontSize;
        var settings = _host.Settings.Mcp;

        _enabled = new ToggleSwitch().IsChecked(settings.Enabled).OnContent(Loc.T("Wł.")).OffContent(Loc.T("Wył."));
        _enabled.IsCheckedChanged += (sender, args) => _ = SetEnabledAsync(_enabled.IsChecked == true);
        _port = new TextBox().Width(100).Text(settings.Port.ToString(CultureInfo.InvariantCulture));
        _status = new TextBlock().TextWrapping(TextWrapping.Wrap).Foreground(b.FileForeground);
        _command = Code();
        _snippet = Code();
        _roots = new StackPanel().Spacing(4);
        _recent = new StackPanel().Spacing(2);

        var list = new StackPanel().Spacing(10).Margin(24, 16, 24, 24).Children(
            Heading(Loc.T("Serwer")),
            new StackPanel().Orientation(Orientation.Horizontal).Spacing(12).Children(
                new TextBlock().Text(Loc.T("Serwer MCP")).VerticalAlignment(VerticalAlignment.Center),
                _enabled,
                new TextBlock().Text(Loc.T("Port")).VerticalAlignment(VerticalAlignment.Center).Margin(12, 0, 0, 0),
                _port,
                SmallButton(Loc.T("Zastosuj port"), () => _ = ApplyPortAsync())),
            _status,
            Heading(Loc.T("Polecenie dla Claude Code")).Margin(0, 8, 0, 0),
            Muted(Loc.T("Uruchom je raz w terminalu; potem „claude mcp list” pokazuje commander4k. Token daje dostęp do aplikacji — nie udostępniaj go.")),
            Box(_command),
            new StackPanel().Orientation(Orientation.Horizontal).Spacing(8).Children(
                SmallButton(Loc.T("Kopiuj polecenie"), () => _ = CopyAsync(_mcp.ClaudeCommand())),
                SmallButton(Loc.T("Nowy token"), () => _mcp.NewToken())),
            Heading(Loc.T("Wpis do .mcp.json")).Margin(0, 8, 0, 0),
            Muted(Loc.T("Dla projektu, który ma łączyć się z Commander4K bez polecenia „claude mcp add”.")),
            Box(_snippet),
            SmallButton(Loc.T("Kopiuj wpis"), () => _ = CopyAsync(_mcp.McpJsonSnippet())),
            Heading(Loc.T("Foldery dostępne dla AI")).Margin(0, 8, 0, 0),
            Muted(Loc.T("Narzędzia plikowe działają tylko w tych folderach i ich podfolderach. Foldery wtyczek, config/ i data/ są chronione przed zapisem.")),
            _roots,
            SmallButton(Loc.T("Dodaj folder z aktywnego panelu"), () => _ = AddActiveFolderAsync()),
            Heading(Loc.T("Ostatnie wywołania")).Margin(0, 8, 0, 0),
            Muted(Loc.T("Nazwy klientów podają same klienty. Pełny dziennik: data/logs/plugins-RRRRMMDD.log.")),
            _recent,
            SmallButton(Loc.T("Cofnij zgody z tej sesji"), () => _mcp.RevokeSessionGrants()));

        _scroll = new ScrollViewer().Content(list);
        var top = new Border().BorderBrush(b.PanelBorder).BorderThickness(new Thickness(0, 0, 0, 1)).Padding(24, 14).Child(new StackPanel().Spacing(4).Children(
            new TextBlock().Text(Loc.T("Połącz Claude Code")).FontSize(18).FontWeight(FontWeight.SemiBold).Foreground(b.FileForeground),
            Muted(Loc.T("Claude Code łączy się z Commander4K przez MCP na 127.0.0.1 i może przeglądać pliki w wybranych folderach, pisać i instalować wtyczki oraz sterować panelami. O ryzykowne akcje aplikacja zawsze pyta."))));
        var footer = new Border().Background(b.HeaderBackground).BorderBrush(b.PanelBorder).BorderThickness(new Thickness(0, 1, 0, 0))
            .Padding(24, 10).Child(new Button().Content(Loc.T("Zamknij")).Height(30).HorizontalAlignment(HorizontalAlignment.Right)
                .Background(b.Accent).BorderBrush(b.Accent).Foreground(Brushes.White).FontWeight(FontWeight.SemiBold).OnClick(click => Close()));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel().Children(top, footer, _scroll);
        Refresh();
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
        var b = _brushes;
        var settings = _host.Settings.Mcp;
        _enabled.IsChecked = settings.Enabled; // Changed elsewhere (settings window); equal values are ignored.
        _status.Text = _mcp.StatusText();
        _status.Foreground = _mcp.Status is McpStartResult.PortBusy or McpStartResult.Failed ? b.Error : b.FileForeground;
        _command.Text = _mcp.ClaudeCommand();
        _snippet.Text = _mcp.McpJsonSnippet();

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
            _status.Foreground = _brushes.Error;
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
            _status.Foreground = _brushes.Error;
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
            _status.Foreground = _brushes.Error;
        }
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private SelectableTextBlock Code() =>
        new SelectableTextBlock().TextWrapping(TextWrapping.Wrap).FontFamily(_brushes.MonospaceFamily).FontSize(12).Foreground(_brushes.FileForeground);

    private Border Box(Control content) =>
        new Border().Background(_brushes.WindowBackground).BorderBrush(_brushes.PanelBorder).BorderThickness(new Thickness(1))
            .CornerRadius(new CornerRadius(_brushes.IsModern ? 6 : 0)).Padding(10, 8).Child(content);

    private TextBlock Heading(string text) =>
        new TextBlock().Text(text.ToUpper(Loc.Culture)).FontSize(11).FontWeight(FontWeight.SemiBold).Foreground(_brushes.Accent);

    private TextBlock Muted(string text) =>
        new TextBlock().Text(text).FontSize(12).TextWrapping(TextWrapping.Wrap).Foreground(_brushes.MutedForeground);

    private static TextBlock Cell(string text, int column, IBrush brush) =>
        new TextBlock().Text(text).Col(column).FontSize(12).Margin(0, 0, 10, 0).Foreground(brush);

    private static Button SmallButton(string text, Action onClick) =>
        new Button().Content(text).Height(28).Padding(10, 0).VerticalContentAlignment(VerticalAlignment.Center).OnClick(click => onClick());
}
