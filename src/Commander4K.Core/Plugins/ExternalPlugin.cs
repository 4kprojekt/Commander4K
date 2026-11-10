using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Commander4K.Core.Execution;
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

    private readonly ConcurrentDictionary<string, IProgress<TransferProgress>> _jobs = new(StringComparer.Ordinal);

    // Time limits of the calls in flight; they stand still while the plugin waits for the user (see UserWait).
    private readonly Dictionary<CancellationTokenSource, TimeSpan> _limits = [];
    private int _userWaits;

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

    /// <summary>
    /// Calls the plugin; starts it first if needed. Cancelling sends <c>$/cancelRequest</c>. Safe to call from the UI
    /// thread: starting the process, building the JSON and writing to the pipe all happen on the pool.
    /// </summary>
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        await CallerThread.LeaveUi();
        var connection = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_limits)
        {
            _limits[limit] = timeout ?? DefaultTimeout;
            if (_userWaits == 0)
            {
                limit.CancelAfter(timeout ?? DefaultTimeout);
            }
        }

        try
        {
            return await connection.RequestAsync(method, parameters, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.T("Wtyczka „{0}” nie odpowiedziała na „{1}” w ciągu {2:0} s.", Manifest.Name, method, (timeout ?? DefaultTimeout).TotalSeconds));
        }
        finally
        {
            lock (_limits)
            {
                _limits.Remove(limit);
            }
        }
    }

    /// <summary>
    /// While the plugin waits for the user (a dialog, a confirmation, a mail password), the limits of its calls stand
    /// still; when the last wait ends they start again from the full time. Otherwise a user who takes a minute to fill
    /// in a setup window would see the command fail with a time-out.
    /// </summary>
    private Waiting UserWait()
    {
        lock (_limits)
        {
            if (_userWaits++ == 0)
            {
                foreach (var limit in _limits.Keys)
                {
                    limit.CancelAfter(Timeout.InfiniteTimeSpan);
                }
            }
        }

        return new Waiting(this);
    }

    private void EndUserWait()
    {
        lock (_limits)
        {
            if (--_userWaits == 0)
            {
                foreach (var (limit, time) in _limits)
                {
                    limit.CancelAfter(time);
                }
            }
        }
    }

    private readonly struct Waiting(ExternalPlugin plugin) : IDisposable
    {
        public void Dispose() => plugin.EndUserWait();
    }

    /// <summary>Raised when the plugin asks for a tick now (<c>background.wake</c>); the argument is the plugin id.</summary>
    public event Action<string>? WakeRequested;

    /// <summary><c>background.tick</c>; true when the plugin answered <c>{ "more": true }</c> (it has more work, tick again soon).</summary>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("background.tick", null, cancellationToken, BackgroundTickLoop.TickTimeout).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("more", out var more) && more.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Routes <c>job.progress</c> notifications carrying <paramref name="jobId"/> to <paramref name="progress"/> until
    /// the result is disposed. The id travels in the params of <c>panel.receive</c>/<c>panel.provide</c>.
    /// </summary>
    public IDisposable RegisterProgress(string jobId, IProgress<TransferProgress> progress)
    {
        _jobs[jobId] = progress;
        return new JobRegistration(_jobs, jobId, progress);
    }

    /// <summary>
    /// Lets this plugin read <paramref name="items"/> (folders: everything below) until the result is disposed. The
    /// grant goes to the host this plugin's requests are served with, so it always applies to them.
    /// </summary>
    public IDisposable GrantRead(IReadOnlyList<TransferItem> items) => _host.GrantRead(items);

    private sealed class JobRegistration(ConcurrentDictionary<string, IProgress<TransferProgress>> jobs, string jobId, IProgress<TransferProgress> progress) : IDisposable
    {
        public void Dispose() => jobs.TryRemove(new KeyValuePair<string, IProgress<TransferProgress>>(jobId, progress));
    }

    public async ValueTask DisposeAsync()
    {
        await CallerThread.LeaveUi(); // killing a process tree is slow
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
            await StopProcessAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        await StopProcessAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }

    /// <summary>
    /// Kills the process and waits (up to 2 s) until it is gone: <see cref="Process.Kill(bool)"/> returns before the
    /// process ends, and until then it still holds its working folder, so deleting or replacing the plugin failed.
    /// </summary>
    private async Task StopProcessAsync()
    {
        if (_process is not { } process)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
        }

        process.Dispose();
        _process = null;
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

            await StopProcessAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Requests from the plugin to the app. Each maps to one <see cref="IPluginHost"/> call (gated and audited there),
    /// except <c>background.wake</c>, which is checked and audited here. A new method
    /// also goes into the three SDK clients and docs/plugins.md; ProtocolConsistencyTests fails otherwise.
    /// </summary>
    private async Task<object?> HandleRequestAsync(string method, JsonElement? p, CancellationToken cancellationToken)
    {
        string Str(string name) => p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ArgumentException(Loc.T("Brak parametru „{0}”.", name));
        string? OptStr(string name) => p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string name) => p?.TryGetProperty(name, out var v) == true && v.ValueKind == JsonValueKind.True;
        long Long(string name, long fallback) => p?.TryGetProperty(name, out var v) == true && v.TryGetInt64(out var number) ? number : fallback;
        int Int(string name) => (int)Math.Clamp(Long(name, 0), 0, int.MaxValue);

        if (MailRpc.Handles(method))
        {
            // An interactive mail call may show the password or certificate dialog; discovery (≤ 20 s) runs while the user waits for it.
            using var wait = method == "mail.discover" || (p?.TryGetProperty("account", out var account) == true && account.ValueKind == JsonValueKind.Object
                && !(account.TryGetProperty("interactive", out var interactive) && interactive.ValueKind == JsonValueKind.False))
                ? UserWait() : default(Waiting?);
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
                using (UserWait())
                {
                    return await _host.ConfirmAsync(Str("title"), Str("message")).ConfigureAwait(false);
                }

            case "ui.prompt":
                using (UserWait())
                {
                    return await _host.PromptAsync(Str("title"), Str("message"), OptStr("initial") ?? string.Empty).ConfigureAwait(false);
                }

            case "ui.dialog":
                var dialog = DialogRpc.Parse(p ?? default);
                using (UserWait())
                {
                    return DialogRpc.Answer(dialog, await _host.ShowDialogAsync(dialog).ConfigureAwait(false));
                }

            case "ui.navigate":
                await _host.NavigateAsync(Str("path"), OptStr("focus")).ConfigureAwait(false);
                return null;
            case "ui.openUrl":
                await _host.OpenUrlAsync(Str("url")).ConfigureAwait(false);
                return null;
            case "ui.openFolder":
                await _host.OpenFolderAsync(Str("path")).ConfigureAwait(false);
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
                    return System.Text.Encoding.UTF8.GetString(buffer, 0, read < maxBytes ? read : Utf8Boundary(buffer, read));
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
                var part = p?.TryGetProperty("multipart", out var mp) == true && mp.ValueKind == JsonValueKind.Object
                    ? new HttpFilePart(
                        mp.TryGetProperty("field", out var field) && field.GetString() is { Length: > 0 } fieldName ? fieldName : throw new ArgumentException(Loc.T("Brak parametru „{0}”.", "multipart.field")),
                        mp.TryGetProperty("path", out var partPath) && partPath.GetString() is { Length: > 0 } partFile ? partFile : throw new ArgumentException(Loc.T("Brak parametru „{0}”.", "multipart.path")),
                        mp.TryGetProperty("fileName", out var partName) && partName.ValueKind == JsonValueKind.String ? partName.GetString() : null)
                    : null;
                var response = await _host.HttpRequestAsync(
                    new HttpRequestSpec(OptStr("method") ?? "GET", new Uri(Str("url")), headers, OptStr("body"), OptStr("contentType"), OptStr("bodyFile"), part),
                    cancellationToken).ConfigureAwait(false);
                return new { status = response.Status, headers = response.Headers, body = response.Body };
            case "job.progress":
                // A notification (no answer): progress of a panel.receive/panel.provide call. Unknown ids are ignored.
                if (OptStr("jobId") is { } jobId && _jobs.TryGetValue(jobId, out var job))
                {
                    job.Report(new TransferProgress(Int("done"), Int("total"), Math.Max(0, Long("bytes", 0)), OptStr("text")));
                }

                return null;
            case "storage.get":
                return await _host.StorageGetAsync(Str("key")).ConfigureAwait(false);
            case "storage.set":
                var value = p?.TryGetProperty("value", out var v) == true && v.ValueKind != JsonValueKind.Null ? System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText()) : null;
                await _host.StorageSetAsync(Str("key"), value).ConfigureAwait(false);
                return null;
            case "storage.keys":
                return await _host.StorageKeysAsync().ConfigureAwait(false);
            case "ui.editFile":
                await _host.EditFileAsync(Str("path")).ConfigureAwait(false);
                return null;
            case "ui.openPanel":
                await _host.OpenPanelAsync(Str("panel"), OptStr("argument")).ConfigureAwait(false);
                return null;
            case "fs.size":
                var size = await _host.CalculateSizeAsync(Str("path"), null, cancellationToken).ConfigureAwait(false);
                return new { bytes = size.Bytes, files = size.Files, folders = size.Folders, inaccessible = size.Inaccessible };
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
                await _host.CreateDirectoryAsync(Str("path"), Bool("hidden"), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Where to cut <paramref name="count"/> bytes of UTF-8 so that a character split by the read limit is left out
    /// (it would decode as U+FFFD at the end of the text).
    /// </summary>
    public static int Utf8Boundary(ReadOnlySpan<byte> bytes, int count)
    {
        var lead = count - 1;
        while (lead >= 0 && count - lead < 4 && (bytes[lead] & 0xC0) == 0x80)
        {
            lead--;
        }

        if (lead < 0)
        {
            return count;
        }

        var needed = bytes[lead] >= 0xF0 ? 4 : bytes[lead] >= 0xE0 ? 3 : bytes[lead] >= 0xC0 ? 2 : 1;
        return needed > count - lead ? lead : count;
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
}
