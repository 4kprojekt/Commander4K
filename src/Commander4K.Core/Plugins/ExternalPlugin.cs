using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Commander4K.Core.Updates;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>
/// One out-of-process plugin (Node, Python, PowerShell, .NET, exe) speaking the Commander4K plugin protocol over
/// stdio. Started lazily on first use and restarted after a crash. Everything the plugin asks of the app goes through
/// its <see cref="ScopedPluginHost"/>: permission check plus audit log entry for every call.
/// </summary>
public sealed class ExternalPlugin : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly DiscoveredPlugin _discovered;
    private readonly RuntimeInfo _runtime;
    private readonly ScopedPluginHost _host;
    private readonly AuditLog _audit;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private JsonRpcConnection? _connection;

    public ExternalPlugin(DiscoveredPlugin discovered, RuntimeInfo runtime, ScopedPluginHost host, AuditLog audit)
    {
        _discovered = discovered;
        _runtime = runtime;
        _host = host;
        _audit = audit;
    }

    public PluginManifest Manifest => _discovered.Manifest!;

    public string Folder => _discovered.Folder;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Calls the plugin; starts it first if needed. Cancelling sends <c>$/cancelRequest</c>.</summary>
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var connection = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            return await connection.RequestAsync(method, parameters, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Wtyczka „{Manifest.Name}” nie odpowiedziała na „{method}” w ciągu {(timeout ?? DefaultTimeout).TotalSeconds:0} s.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await connection.RequestAsync("shutdown", null, quick.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonRpcException)
            {
            }

            // Kill first: a pending pipe read only ends when the process (and its stdout) goes away.
            StopProcess();
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        StopProcess();
        _startGate.Dispose();
    }

    private async Task<JsonRpcConnection> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null && IsRunning && !_connection.Completion.IsCompleted)
            {
                return _connection;
            }

            StopProcess();
            var info = RuntimeCatalog.BuildStartInfo(_runtime, _discovered.EntryPath, Folder);
            _audit.Record(Manifest.Id, "start", $"{info.FileName} {string.Join(' ', info.ArgumentList)}", AuditOutcome.Info);
            var process = Process.Start(info) ?? throw new InvalidOperationException($"Nie udało się uruchomić wtyczki {Manifest.Name}.");
            _process = process;
            _ = PumpStandardErrorAsync(process);

            var connection = new JsonRpcConnection(process.StandardOutput, process.StandardInput, HandleRequestAsync);
            connection.Start();
            _connection = connection;

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(20));
            await connection.RequestAsync("initialize", new
            {
                protocol = ManifestReader.ProtocolVersion,
                app = new { name = "Commander4K", version = AppVersion.Current.ToString() },
                locale = CultureInfo.CurrentUICulture.Name,
                permissions = Manifest.Permissions,
            }, handshake.Token).ConfigureAwait(false);
            return connection;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Requests from the plugin to the app. Each maps to one <see cref="IPluginHost"/> call.</summary>
    private async Task<object?> HandleRequestAsync(string method, JsonElement? p, CancellationToken cancellationToken)
    {
        string Str(string name) => p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ArgumentException($"Brak parametru „{name}”.");
        string? OptStr(string name) => p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string name) => p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.True;

        switch (method)
        {
            case "context.get":
                return Context(_host);
            case "log.write":
                _host.Log(Str("message"));
                return null;
            case "ui.notify":
                _host.Notify(Str("message"), Bool("error"));
                return null;
            case "ui.showText":
                await _host.ShowTextAsync(Str("title"), Str("text")).ConfigureAwait(false);
                return null;
            case "ui.confirm":
                return await _host.ConfirmAsync(Str("title"), Str("message")).ConfigureAwait(false);
            case "ui.prompt":
                return await _host.PromptAsync(Str("title"), Str("message"), OptStr("initial") ?? string.Empty).ConfigureAwait(false);
            case "ui.navigate":
                await _host.NavigateAsync(Str("path"), OptStr("focus")).ConfigureAwait(false);
                return null;
            case "fs.list":
                return (await _host.ListAsync(Str("path"), cancellationToken).ConfigureAwait(false)).Select(EntryDto).ToList();
            case "fs.readText":
                var maxBytes = p?.TryGetProperty("maxBytes", out var m) == true && m.TryGetInt32(out var limit) ? Math.Clamp(limit, 1, 16 * 1024 * 1024) : 1024 * 1024;
                await using (var stream = await _host.OpenReadAsync(Str("path"), cancellationToken).ConfigureAwait(false))
                {
                    var buffer = new byte[maxBytes];
                    var read = await stream.ReadAtLeastAsync(buffer, maxBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                    return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                }

            case "fs.writeText":
                await _host.WriteTextAsync(Str("path"), Str("content"), cancellationToken).ConfigureAwait(false);
                return null;
            case "process.kill":
                var pid = p?.TryGetProperty("pid", out var pidElement) == true ? pidElement.GetInt32() : throw new ArgumentException("Brak parametru „pid”.");
                await _host.KillProcessAsync(pid, Bool("tree"), cancellationToken).ConfigureAwait(false);
                return null;
            case "shell.run":
                var arguments = p?.TryGetProperty("args", out var a) == true ? a.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : [];
                var result = await _host.RunProcessAsync(new ProcessRunRequest(Str("file"), arguments, OptStr("cwd"), Bool("elevated")), cancellationToken).ConfigureAwait(false);
                return new { exitCode = result.ExitCode, output = result.Output, error = result.Error };
            case "net.get":
                return await _host.HttpGetTextAsync(new Uri(Str("url")), cancellationToken).ConfigureAwait(false);
            default:
                _audit.Record(Manifest.Id, method, "nieznana metoda", AuditOutcome.Denied);
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Nieznana metoda „{method}”.");
        }
    }

    /// <summary>What the user is looking at, sent with every command/panel call so plugins rarely need context.get.</summary>
    public static object Context(IPluginHost host) => new
    {
        activePath = host.ActivePath,
        otherPath = host.OtherPath,
        selection = host.SelectedEntries.Select(EntryDto).ToList(),
    };

    public static object EntryDto(VfsEntry entry) => new
    {
        name = entry.Name,
        path = entry.Path,
        kind = entry.Kind.ToString().ToLowerInvariant(),
        size = entry.Size,
        modified = entry.LastWriteTime == default ? null : entry.LastWriteTime.ToString("O", CultureInfo.InvariantCulture),
        hidden = (entry.Flags & VfsEntryFlags.Hidden) != 0,
    };

    private async Task PumpStandardErrorAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _audit.Record(Manifest.Id, "stderr", line, AuditOutcome.Info);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void StopProcess()
    {
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            _process = null;
        }
    }
}
