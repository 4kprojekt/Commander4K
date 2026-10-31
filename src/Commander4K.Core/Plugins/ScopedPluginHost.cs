using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Localization;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>What the window provides to plugins: context and UI. Implemented by the app, marshalled to the UI thread.</summary>
public interface IPluginUi
{
    string? ActivePath { get; }

    string? OtherPath { get; }

    IReadOnlyList<VfsEntry> SelectedEntries { get; }

    Task NavigateAsync(string path, string? focusName);

    void Notify(string message, bool isError);

    Task ShowTextAsync(string title, string text);

    Task<bool> ConfirmAsync(string title, string message);

    Task<string?> PromptAsync(string title, string message, string initial);

    Task EditFileAsync(string path);

    Task OpenPanelAsync(string panelPluginId, string? argument);

    Task OpenUrlAsync(string url);

    Task SearchWebAsync(string query, string? engine);

    Task CopyTextAsync(string text);
}

/// <summary>
/// The <see cref="IPluginHost"/> one plugin sees. Gated operations check the plugin's manifest permissions; every call
/// — allowed, denied or failed — is written to the <see cref="AuditLog"/> with the plugin's id. Elevated program runs
/// additionally require the user's confirmation every time.
/// </summary>
/// <param name="processes">Process list for <c>process.list</c>; the app passes one with parent PIDs and image paths.</param>
public sealed class ScopedPluginHost(PluginManifest manifest, IPluginUi ui, VfsRouter router, HttpClient http, AuditLog audit,
    Func<IReadOnlyList<ProcessSnapshot>>? processes = null) : IPluginHost
{
    private const int MaxHttpBytes = 5 * 1024 * 1024;

    private readonly ReadGrants _grants = new();

    public PluginManifest Manifest { get; } = manifest;

    public string? ActivePath => ui.ActivePath;

    public string? OtherPath => ui.OtherPath;

    public IReadOnlyList<VfsEntry> SelectedEntries => ui.SelectedEntries;

    public Task NavigateAsync(string path, string? focusName = null)
    {
        audit.Record(Manifest.Id, "ui.navigate", path, AuditOutcome.Info);
        return ui.NavigateAsync(path, focusName);
    }

    public void Notify(string message, bool isError = false) => ui.Notify(message, isError);

    public Task ShowTextAsync(string title, string text) => ui.ShowTextAsync(title, text);

    public Task<bool> ConfirmAsync(string title, string message) => ui.ConfirmAsync(title, message);

    public Task<string?> PromptAsync(string title, string message, string initial = "") => ui.PromptAsync(title, message, initial);

    public Task EditFileAsync(string path)
    {
        audit.Record(Manifest.Id, "ui.edit", path, AuditOutcome.Info);
        return ui.EditFileAsync(path);
    }

    public Task OpenPanelAsync(string panelPluginId, string? argument = null) => ui.OpenPanelAsync(panelPluginId, argument);

    /// <remarks>Not gated: the page opens visibly in the app's browser, not inside the plugin. Only http(s) is allowed.</remarks>
    public Task OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            audit.Record(Manifest.Id, "ui.openUrl", url, AuditOutcome.Denied);
            throw new ArgumentException(Loc.T("Dozwolone są tylko adresy http i https."), nameof(url));
        }

        audit.Record(Manifest.Id, "ui.openUrl", uri.ToString(), AuditOutcome.Info);
        return ui.OpenUrlAsync(uri.ToString());
    }

    public Task SearchWebAsync(string query, string? engine = null)
    {
        audit.Record(Manifest.Id, "ui.search", $"{engine ?? "default"}: {query}", AuditOutcome.Info);
        return ui.SearchWebAsync(query, engine);
    }

    public void Log(string message) => audit.Record(Manifest.Id, "log", message, AuditOutcome.Info);

    public Task CopyTextAsync(string text)
    {
        audit.Record(Manifest.Id, "ui.copy", Loc.T("{0} znaków", text.Length), AuditOutcome.Info);
        return ui.CopyTextAsync(text);
    }

    /// <summary>
    /// Lets the plugin read exactly these files (and folder subtrees) without <c>fs.read</c> until the result is
    /// disposed. Not part of the protocol: the app grants it around a call that hands the plugin the user's files.
    /// </summary>
    public IDisposable GrantRead(IReadOnlyList<TransferItem> items)
    {
        var grant = _grants.Grant(items);
        audit.Record(Manifest.Id, "fs.grant", $"{items.Count} paths", AuditOutcome.Info);
        return new GrantScope(grant, () => audit.Record(Manifest.Id, "fs.grant.end", string.Empty, AuditOutcome.Info));
    }

    public Task<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileRead, "fs.stat", path, () => router.GetEntryAsync(path, cancellationToken));

    public Task WriteBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileWrite, "fs.write", Loc.T("{0} ({1} B)", path, content.Length), async () =>
        {
            await using var stream = await router.OpenWriteAsync(path, overwrite: true, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileWrite, "fs.mkdir", path, async () =>
        {
            await router.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileWrite, "fs.delete", recursive ? path + Loc.T(" (z zawartością)") : path, async () =>
        {
            await router.DeleteAsync(path, recursive, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileWrite, "fs.move", $"{sourcePath} → {destinationPath}", async () =>
        {
            await router.MoveAsync(sourcePath, destinationPath, overwrite, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task<IReadOnlyList<ProcessSnapshot>> ListProcessesAsync(CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.ProcessRead, "process.list", string.Empty, () => Task.Run(processes ?? (() => SystemSnapshots.Processes()), cancellationToken));

    public Task<SystemSnapshot> GetSystemInfoAsync(CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.SystemInfo, "system.info", string.Empty, () => Task.Run(SystemSnapshots.System, cancellationToken));

    public Task<HttpResponseData> HttpRequestAsync(HttpRequestSpec request, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.Network, "net.request", $"{request.Method.ToUpperInvariant()} {request.Url}", async () =>
        {
            if (request.Url.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException(Loc.T("Dozwolone są tylko adresy http i https."), nameof(request));
            }

            if ((request.Body is not null ? 1 : 0) + (request.BodyFile is not null ? 1 : 0) + (request.Part is not null ? 1 : 0) > 1)
            {
                throw new ArgumentException("Use only one of body, bodyFile and multipart.", nameof(request));
            }

            using var message = new HttpRequestMessage(new HttpMethod(request.Method.ToUpperInvariant()), request.Url);
            if (request.Body is not null)
            {
                message.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType ?? "application/json");
            }
            else if (request.BodyFile is { } bodyFile)
            {
                // Streamed through the router (disk, share, SFTP, archive), never loaded into memory. The read is gated
                // like fs.read, so it works with fs.read or while the file is granted (panel.receive).
                var content = new StreamContent(await OpenReadAsync(bodyFile, cancellationToken).ConfigureAwait(false));
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType ?? "application/octet-stream");
                message.Content = content;
            }
            else if (request.Part is { } part)
            {
                var form = new MultipartFormDataContent();
                message.Content = form;
                var content = new StreamContent(await OpenReadAsync(part.Path, cancellationToken).ConfigureAwait(false));
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType ?? "application/octet-stream");
                form.Add(content, part.Field, part.FileName ?? FileNameOf(part.Path));
            }

            foreach (var (name, value) in request.Headers ?? new Dictionary<string, string>())
            {
                if (!message.Headers.TryAddWithoutValidation(name, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var headers = response.Headers.Concat(response.Content.Headers)
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
            var body = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            return new HttpResponseData((int)response.StatusCode, headers, body);
        });

    public Task<IReadOnlyList<VfsEntry>> ListAsync(string path, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileRead, "fs.list", path, async () =>
        {
            var entries = new List<VfsEntry>();
            await foreach (var entry in router.EnumerateAsync(path, cancellationToken).ConfigureAwait(false))
            {
                entries.Add(entry);
            }

            return (IReadOnlyList<VfsEntry>)entries;
        });

    public Task<DirectorySizeInfo> CalculateSizeAsync(string path, IProgress<DirectorySizeInfo>? progress = null, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileRead, "fs.size", path, async () =>
        {
            var forward = progress is null ? null : new Progress<DirectorySize>(s => progress.Report(new DirectorySizeInfo(s.Bytes, s.Files, s.Directories, s.InaccessibleDirectories)));
            var size = await new DirectorySizeCalculator(router).CalculateAsync(path, forward, cancellationToken).ConfigureAwait(false);
            return new DirectorySizeInfo(size.Bytes, size.Files, size.Directories, size.InaccessibleDirectories);
        });

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileRead, "fs.read", path, () => router.OpenReadAsync(path, cancellationToken));

    public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.FileWrite, "fs.write", Loc.T("{0} ({1} znaków)", path, content.Length), async () =>
        {
            await using var stream = await router.OpenWriteAsync(path, overwrite: true, cancellationToken).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task KillProcessAsync(int processId, bool entireTree, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.ProcessControl, "process.kill", $"PID {processId}{(entireTree ? Loc.T(" (z podprocesami)") : "")}", () =>
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireTree);
            return Task.FromResult(true);
        });

    public Task<ProcessRunResult> RunProcessAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.ShellRun, request.Elevated ? "shell.run (admin)" : "shell.run", DescribeCommand(request), async () =>
        {
            if (request.Elevated
                && !await ui.ConfirmAsync(Loc.T("Uprawnienia administratora"), Loc.T("Wtyczka „{0}” chce uruchomić jako administrator:\n\n{1}", Manifest.Name, DescribeCommand(request))).ConfigureAwait(false))
            {
                throw new OperationCanceledException(Loc.T("Odmówiono uruchomienia jako administrator."));
            }

            return await ProcessRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        });

    public Task<string> HttpGetTextAsync(Uri url, CancellationToken cancellationToken = default) =>
        Gate(PluginPermissions.Network, "net.get", url.ToString(), async () =>
        {
            if (url.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException(Loc.T("Dozwolone są tylko adresy http i https."), nameof(url));
            }

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
        });

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (limited.Length + read > MaxHttpBytes)
            {
                throw new InvalidDataException(Loc.T("Odpowiedź przekracza 5 MB."));
            }

            limited.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(limited.GetBuffer(), 0, (int)limited.Length);
    }

    private async Task<T> Gate<T>(string permission, string operation, string detail, Func<Task<T>> action)
    {
        if (!Manifest.Allows(permission))
        {
            if (permission == PluginPermissions.FileRead && _grants.Covers(detail))
            {
                detail += " (granted)";
            }
            else
            {
                audit.Record(Manifest.Id, operation, detail, AuditOutcome.Denied);
                throw new PluginPermissionException(Manifest.Id, permission);
            }
        }

        try
        {
            var result = await action().ConfigureAwait(false);
            audit.Record(Manifest.Id, operation, detail);
            return result;
        }
        catch (Exception ex) when (ex is not PluginPermissionException)
        {
            audit.Record(Manifest.Id, operation, $"{detail} → {ex.GetType().Name}: {ex.Message}", AuditOutcome.Failed);
            throw;
        }
    }

    private sealed class GrantScope(IDisposable grant, Action ended) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                grant.Dispose();
                ended();
            }
        }
    }

    /// <summary>Last segment of any VFS path (<c>C:\a\b.txt</c>, <c>sftp://h/a/b.txt</c>, <c>zip://x.zip::/b.txt</c>).</summary>
    private static string FileNameOf(string path) =>
        Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : "file";

    private static string DescribeCommand(ProcessRunRequest request) =>
        request.FileName + " " + string.Join(' ', request.Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
}
