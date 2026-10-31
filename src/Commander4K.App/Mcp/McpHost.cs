using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Commander4K.App.Composition;
using Commander4K.Core.Localization;
using Commander4K.Core.Mcp;
using Commander4K.Core.Updates;

namespace Commander4K.App.Mcp;

/// <summary>
/// Runs the MCP server for the window: one policy dispatcher for the app's lifetime (recent calls, session grants) and
/// a fresh <see cref="McpServer"/> + <see cref="McpHttpServer"/> per start, so a restart (port change, off/on) drops
/// every session and in-flight call. The dispatcher reads an immutable copy of the MCP settings that is swapped on
/// each <see cref="ApplySettingsAsync"/>, because request threads read it while the settings window edits the original
/// on the UI thread. Create and use it on the UI thread; <see cref="Changed"/> is raised there too.
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    private readonly AppHost _host;
    private readonly MainWindow _window;
    private readonly McpToolDispatcher _dispatcher;
    private readonly IReadOnlyList<McpPrompt> _prompts;
    private readonly IReadOnlyList<string> _protectedRoots;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile McpSettings _snapshot;
    private volatile string? _token;
    private McpServer? _server;
    private McpHttpServer? _http;
    private int _runningPort;
    private bool _disposed;

    public McpHost(AppHost host, MainWindow window)
    {
        _host = host;
        _window = window;
        _snapshot = Snapshot(host.Settings.Mcp);
        _protectedRoots = ProtectedRootsOf(host);
        IReadOnlyList<string> AllowedRoots() => _snapshot.AllowedRoots;
        IReadOnlyList<string> ProtectedRoots() => _protectedRoots;
        var tools = window.McpTools(AllowedRoots, ProtectedRoots);
        var readFile = tools.First(t => t.Definition.Name == "read_file").Definition;
        // Prompts that sample a file honour read_file's mode (the dispatcher exists by the time a prompt runs).
        _prompts = McpPrompts.Create(host, AllowedRoots, ProtectedRoots, () => _dispatcher!.EffectiveMode(readFile) == McpToolMode.Block);
        _dispatcher = new McpToolDispatcher(tools, () => _snapshot, ProtectedRoots, ConfirmAsync, host.Audit);
        _dispatcher.Called += () => Dispatcher.UIThread.Post(RaiseChanged);
    }

    /// <summary>Raised on the UI thread when the status, the sessions or the recent calls change.</summary>
    public event Action? Changed;

    /// <summary>Result of the last start; <c>null</c> while the server is off.</summary>
    public McpStartResult? Status { get; private set; }

    public string Url => $"http://127.0.0.1:{_host.Settings.Mcp.Port}/mcp";

    public int Sessions => _server?.SessionCount ?? 0;

    public IReadOnlyList<McpCallRecord> Recent => _dispatcher.Recent;

    public IReadOnlyList<string> ProtectedRoots => _protectedRoots;

    /// <summary>
    /// Folders no MCP tool may read or change (only list_directory and stat see names there): the plugin folders
    /// (install_plugin is the way in), the configuration (approvals, credentials, the MCP token, known hosts) and the
    /// data folder (audit logs, plugin storage, the browser profile with its sign-ins), and the folder of the
    /// executable itself (a DLL written next to Commander4K.exe would be loaded on the next start).
    /// </summary>
    public static IReadOnlyList<string> ProtectedRootsOf(AppHost host) =>
    [
        .. host.PluginRoots
            .Append(AppContext.BaseDirectory)
            .Append(host.Config.ConfigDirectory)
            .Append(Path.Combine(host.DataDirectory, "data"))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>One line for the badge's tooltip, the connect window and the Claude Code dialog.</summary>
    public string StatusText()
    {
        var port = _host.Settings.Mcp.Port;
        return Status switch
        {
            null => Loc.T("Serwer MCP jest wyłączony."),
            McpStartResult.PortBusy => Loc.T("Port {0} jest zajęty — wybierz inny port.", port),
            McpStartResult.Failed => Loc.T("Nie udało się uruchomić serwera MCP na porcie {0}.", port),
            _ when Sessions > 0 => Loc.P(Sessions, "Serwer MCP działa ({1}), połączony {0} klient.|Serwer MCP działa ({1}), połączeni {0} klienci.|Serwer MCP działa ({1}), połączonych {0} klientów.", Url),
            _ => Loc.T("Serwer MCP działa ({0}), czeka na Claude Code.", Url),
        };
    }

    /// <summary>
    /// Starts, stops or restarts the listener when <c>Enabled</c> or <c>Port</c> changed (and retries a port that was
    /// busy), creates a token when enabling without one, and swaps in the settings snapshot either way.
    /// </summary>
    public async Task ApplySettingsAsync()
    {
        var settings = _host.Settings.Mcp;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            if (settings.Enabled && string.IsNullOrEmpty(settings.ReadToken()))
            {
                settings.SetToken(McpSettings.NewToken());
                await _host.SaveSettingsAsync();
            }

            _snapshot = Snapshot(settings);
            _token = settings.Enabled ? settings.ReadToken() : null;
            if (settings.Enabled && _http is not null && _runningPort == settings.Port)
            {
                return;
            }

            await StopServerAsync();
            if (settings.Enabled)
            {
                StartServer(settings.Port);
            }
        }
        finally
        {
            _gate.Release();
            RaiseChanged();
        }
    }

    /// <summary>The command that registers this server with Claude Code.</summary>
    public string ClaudeCommand() =>
        $"claude mcp add --transport http commander4k {Url} --header \"Authorization: Bearer {CurrentToken()}\"";

    /// <summary>The same as an entry for a project's <c>.mcp.json</c>.</summary>
    public string McpJsonSnippet()
    {
        var json = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["commander4k"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = Url,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + CurrentToken() },
                },
            },
        };
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Replaces the token; requests with the old one are refused from now on.</summary>
    public async Task NewTokenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            var settings = _host.Settings.Mcp;
            var token = McpSettings.NewToken();
            settings.SetToken(token);
            _snapshot = Snapshot(settings);
            if (settings.Enabled)
            {
                _token = token;
            }

            RaiseChanged();
            await _host.SaveSettingsAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void NewToken() => _ = SaveQuietlyAsync(NewTokenAsync());

    public void RevokeSessionGrants()
    {
        _dispatcher.RevokeSessionGrants();
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _token = null;
            await StopServerAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task SaveQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning($"Saving the MCP token failed: {ex.Message}");
        }
    }

    private string CurrentToken() => _host.Settings.Mcp.ReadToken() ?? string.Empty;

    private void StartServer(int port)
    {
        var server = new McpServer(_dispatcher, () => _prompts, AppVersion.Current.ToString());
        var http = new McpHttpServer(server, () => _token);
        Status = http.Start(port);
        if (Status != McpStartResult.Started)
        {
            return;
        }

        server.SessionsChanged += OnSessionsChanged;
        (_server, _http, _runningPort) = (server, http, port);
    }

    /// <summary>Closing the listener cancels every in-flight call, which closes their confirmation dialogs as "No".</summary>
    private async Task StopServerAsync()
    {
        var (server, http) = (_server, _http);
        (_server, _http, _runningPort) = (null, null, 0);
        Status = null;
        if (server is not null)
        {
            server.SessionsChanged -= OnSessionsChanged;
        }

        if (http is not null)
        {
            await http.StopAsync();
        }

        // "Yes until the end of the session": sessions end with the server.
        _dispatcher.RevokeSessionGrants();
    }

    private void OnSessionsChanged() => Dispatcher.UIThread.Post(RaiseChanged);

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>
    /// The dispatcher's confirmation: shown on the UI thread over the main window (the dispatcher already allows one
    /// at a time). The call's cancellation (<c>notifications/cancelled</c>, client gone, server stopped) closes the
    /// dialog and answers "No".
    /// </summary>
    private Task<McpConsent> ConfirmAsync(McpConfirmation confirmation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(McpConsent.No);
        }

        var result = new TaskCompletionSource<McpConsent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => _ = ShowConfirmationAsync(confirmation, result, cancellationToken));
        return result.Task;
    }

    private async Task ShowConfirmationAsync(McpConfirmation confirmation, TaskCompletionSource<McpConsent> result, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                result.TrySetResult(McpConsent.No);
                return;
            }

            var dialog = new McpConfirmDialog(_window.McpBrushes, confirmation);
            using var registration = cancellationToken.Register(() =>
            {
                result.TrySetResult(McpConsent.No);
                Dispatcher.UIThread.Post(() => dialog.Close());
            });
            var consent = await dialog.ShowAsync(_window);
            result.TrySetResult(cancellationToken.IsCancellationRequested ? McpConsent.No : consent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No window to show it over (closing, headless): the action does not happen.
            System.Diagnostics.Trace.TraceWarning($"MCP confirmation failed: {ex.Message}");
            result.TrySetResult(McpConsent.No);
        }
    }

    /// <summary>A copy the request threads can read while the settings window edits the original.</summary>
    private static McpSettings Snapshot(McpSettings settings) => new()
    {
        Enabled = settings.Enabled,
        Port = settings.Port,
        AllowedRoots = [.. settings.AllowedRoots],
        ToolModes = new Dictionary<string, string>(settings.ToolModes, StringComparer.OrdinalIgnoreCase),
        Token = settings.Token,
    };
}
