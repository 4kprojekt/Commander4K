using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Commander4K.Core.Localization;
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

    private readonly PluginStorage? _storage;

    /// <param name="storage">Backs <c>storage.get/set</c>; without it those calls fail with "not available".</param>
    public ExternalPlugin(DiscoveredPlugin discovered, RuntimeInfo runtime, ScopedPluginHost host, AuditLog audit, PluginStorage? storage = null)
    {
        _discovered = discovered;
        _runtime = runtime;
        _host = host;
        _audit = audit;
        _storage = storage;
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
            throw new TimeoutException(Loc.T("Wtyczka „{0}” nie odpowiedziała na „{1}” w ciągu {2:0} s.", Manifest.Name, method, (timeout ?? DefaultTimeout).TotalSeconds));
        }
    }

    /// <summary>Raised when the plugin asks for a tick now (<c>background.wake</c>); the argument is the plugin id.</summary>
    public event Action<string>? WakeRequested;

    /// <summary><c>background.tick</c>; true when the plugin answered <c>{ "more": true }</c> (it has more work, tick again soon).</summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("background.tick", null, cancellationToken, BackgroundTickLoop.TickTimeout).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("more", out var more) && more.ValueKind == JsonValueKind.True;
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
            var process = Process.Start(info) ?? throw new InvalidOperationException(Loc.T("Nie udało się uruchomić wtyczki {0}.", Manifest.Name));
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
            : throw new ArgumentException(Loc.T("Brak parametru „{0}”.", name));
        string? OptStr(string name) => p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string name) => p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.True;
        long Long(string name, long fallback) => p?.TryGetProperty(name, out var v) == true && v.TryGetInt64(out var number) ? number : fallback;

        if (MailRpc.Handles(method))
        {
            return await MailRpc.HandleAsync(method, p, _host.Mail, cancellationToken).ConfigureAwait(false);
        }

        switch (method)
        {
            case "context.get":
                return Context(_host);
            case "background.wake":
                if (!Manifest.Allows(PluginPermissions.Background))
                {
                    _audit.Record(Manifest.Id, "background.wake", string.Empty, AuditOutcome.Denied);
                    throw new PluginPermissionException(Manifest.Id, PluginPermissions.Background);
                }

                _audit.Record(Manifest.Id, "background.wake", string.Empty);
                WakeRequested?.Invoke(Manifest.Id);
                return null;
            case "log.write":
                _host.Log(Str("message"));
                return null;
            case "ui.notify":
                _host.Notify(Str("message"), Bool("error"), OptStr("target"));
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
            case "ui.openUrl":
                await _host.OpenUrlAsync(Str("url")).ConfigureAwait(false);
                return null;
            case "ui.search":
                await _host.SearchWebAsync(Str("query"), OptStr("engine")).ConfigureAwait(false);
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
                var pid = p?.TryGetProperty("pid", out var pidElement) == true ? pidElement.GetInt32() : throw new ArgumentException(Loc.T("Brak parametru „pid”."));
                await _host.KillProcessAsync(pid, Bool("tree"), cancellationToken).ConfigureAwait(false);
                return null;
            case "shell.run":
                var arguments = p?.TryGetProperty("args", out var a) == true ? a.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : [];
                var result = await _host.RunProcessAsync(new ProcessRunRequest(Str("file"), arguments, OptStr("cwd"), Bool("elevated")), cancellationToken).ConfigureAwait(false);
                return new { exitCode = result.ExitCode, output = result.Output, error = result.Error };
            case "net.get":
                return await _host.HttpGetTextAsync(new Uri(Str("url")), cancellationToken).ConfigureAwait(false);
            case "net.request":
                var headers = p?.TryGetProperty("headers", out var h) == true && h.ValueKind == JsonValueKind.Object
                    ? h.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ToString())
                    : null;
                var response = await _host.HttpRequestAsync(
                    new HttpRequestSpec(OptStr("method") ?? "GET", new Uri(Str("url")), headers, OptStr("body"), OptStr("contentType")), cancellationToken).ConfigureAwait(false);
                return new { status = response.Status, headers = response.Headers, body = response.Body };
            case "storage.get":
                return (_storage ?? throw new InvalidOperationException("Plugin storage is not available.")).Get(Manifest.Id, Str("key"));
            case "storage.set":
                var value = p?.TryGetProperty("value", out var v) == true && v.ValueKind != JsonValueKind.Null ? System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText()) : null;
                (_storage ?? throw new InvalidOperationException("Plugin storage is not available.")).Set(Manifest.Id, Str("key"), value);
                _audit.Record(Manifest.Id, "storage.set", Str("key"), AuditOutcome.Info);
                return null;
            case "storage.keys":
                return _storage?.Keys(Manifest.Id) ?? [];
            case "ui.copyText":
                await _host.CopyTextAsync(Str("text")).ConfigureAwait(false);
                return null;
            case "fs.stat":
                return await _host.GetEntryAsync(Str("path"), cancellationToken).ConfigureAwait(false) is { } found ? EntryDto(found) : null;
            case "fs.readBytes":
                var offset = Long("offset", 0);
                var length = (int)Math.Clamp(Long("length", 64 * 1024), 0, 16 * 1024 * 1024);
                await using (var stream = await _host.OpenReadAsync(Str("path"), cancellationToken).ConfigureAwait(false))
                {
                    await SkipAsync(stream, offset, cancellationToken).ConfigureAwait(false);
                    var buffer = new byte[length];
                    var read = await stream.ReadAtLeastAsync(buffer, length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                    return new { data = Convert.ToBase64String(buffer, 0, read), bytesRead = read, eof = read < length };
                }

            case "fs.writeBytes":
                await _host.WriteBytesAsync(Str("path"), Convert.FromBase64String(Str("data")), cancellationToken).ConfigureAwait(false);
                return null;
            case "fs.mkdir":
                await _host.CreateDirectoryAsync(Str("path"), cancellationToken).ConfigureAwait(false);
                return null;
            case "fs.delete":
                await _host.DeleteAsync(Str("path"), Bool("recursive"), cancellationToken).ConfigureAwait(false);
                return null;
            case "fs.move":
                await _host.MoveAsync(Str("from"), Str("to"), Bool("overwrite"), cancellationToken).ConfigureAwait(false);
                return null;
            case "process.list":
                return (await _host.ListProcessesAsync(cancellationToken).ConfigureAwait(false)).Select(x => new
                {
                    pid = x.Id,
                    parentPid = x.ParentId,
                    name = x.Name,
                    path = x.Path,
                    memoryBytes = x.WorkingSetBytes,
                    started = x.StartTime?.ToString("O", CultureInfo.InvariantCulture),
                }).ToList();
            case "system.info":
                var system = await _host.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false);
                return new
                {
                    os = system.OsDescription,
                    osVersion = system.OsVersion,
                    architecture = system.Architecture,
                    machine = system.MachineName,
                    user = system.UserName,
                    processors = system.ProcessorCount,
                    memoryBytes = system.TotalMemoryBytes,
                    drives = system.Drives.Select(d => new { name = d.Name, type = d.Type, format = d.Format, label = d.Label, totalBytes = d.TotalBytes, freeBytes = d.FreeBytes }).ToList(),
                };
            default:
                _audit.Record(Manifest.Id, method, "nieznana metoda", AuditOutcome.Denied);
                throw new JsonRpcException(JsonRpcException.MethodNotFound, Loc.T("Nieznana metoda „{0}”.", method));
        }
    }

    /// <summary>Moves a read stream to <paramref name="offset"/>: seeks when possible, otherwise reads and discards (SFTP, archives).</summary>
    private static async Task SkipAsync(Stream stream, long offset, CancellationToken cancellationToken)
    {
        if (offset <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            return;
        }

        var buffer = new byte[81920];
        while (offset > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            offset -= read;
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
