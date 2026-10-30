using System.Diagnostics;
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
}

/// <summary>
/// The <see cref="IPluginHost"/> one plugin sees. Gated operations check the plugin's manifest permissions; every call
/// — allowed, denied or failed — is written to the <see cref="AuditLog"/> with the plugin's id. Elevated program runs
/// additionally require the user's confirmation every time.
/// </summary>
public sealed class ScopedPluginHost(PluginManifest manifest, IPluginUi ui, VfsRouter router, HttpClient http, AuditLog audit) : IPluginHost
{
    private const int MaxHttpBytes = 5 * 1024 * 1024;

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
        });

    private async Task<T> Gate<T>(string permission, string operation, string detail, Func<Task<T>> action)
    {
        if (!Manifest.Allows(permission))
        {
            audit.Record(Manifest.Id, operation, detail, AuditOutcome.Denied);
            throw new PluginPermissionException(Manifest.Id, permission);
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

    private static string DescribeCommand(ProcessRunRequest request) =>
        request.FileName + " " + string.Join(' ', request.Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
}
