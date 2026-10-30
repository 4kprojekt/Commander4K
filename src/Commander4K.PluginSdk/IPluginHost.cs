namespace Commander4K.PluginSdk;

/// <param name="Elevated">Run as administrator in a visible window (output is not captured); the host always asks the user first.</param>
public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    bool Elevated = false,
    TimeSpan? Timeout = null);

public sealed record ProcessRunResult(int ExitCode, string Output, string Error);

/// <summary>Raised by the host when a plugin calls an API its manifest does not declare.</summary>
public sealed class PluginPermissionException(string pluginId, string permission)
    : UnauthorizedAccessException($"Wtyczka „{pluginId}” nie ma uprawnienia „{permission}” ({PluginPermissions.Describe(permission)}).")
{
    public string PluginId { get; } = pluginId;

    public string Permission { get; } = permission;
}

/// <summary>
/// The application API a plugin works through. Each plugin gets its own instance bound to its manifest: gated calls
/// throw <see cref="PluginPermissionException"/> when the permission is not declared, and every call is audited.
/// Members are safe to call from any thread.
/// </summary>
public interface IPluginHost
{
    // ---- context ----

    /// <summary>
    /// Location of the active file panel (any VFS path). When the active panel shows a plugin, this and
    /// <see cref="SelectedEntries"/> refer to the other panel — the file list the user is looking at.
    /// </summary>
    string? ActivePath { get; }

    /// <summary>Marked entries of the active file panel, or the entry under the cursor when nothing is marked.</summary>
    IReadOnlyList<VfsEntry> SelectedEntries { get; }

    /// <summary>Location of the other (inactive) file panel.</summary>
    string? OtherPath { get; }

    // ---- UI (no permission needed) ----

    /// <summary>Opens <paramref name="path"/> in the active file panel, placing the cursor on <paramref name="focusName"/>.</summary>
    Task NavigateAsync(string path, string? focusName = null);

    /// <summary>Short message in the status area.</summary>
    void Notify(string message, bool isError = false);

    /// <summary>Shows text in a read-only viewer window (reports, command output).</summary>
    Task ShowTextAsync(string title, string text);

    /// <summary>Asks the user to confirm; returns <c>false</c> when cancelled.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>Asks for a line of text; <c>null</c> when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string initial = "");

    /// <summary>Opens a file in the user's configured editor (tools.json).</summary>
    Task EditFileAsync(string path);

    /// <summary>Opens one of the host's panel plugins by <see cref="IPlugin.Id"/> in the active panel.</summary>
    Task OpenPanelAsync(string panelPluginId, string? argument = null);

    /// <summary>Writes to the plugin's section of the audit log.</summary>
    void Log(string message);

    // ---- gated ----

    /// <summary>Lists a folder on any VFS. Requires <see cref="PluginPermissions.FileRead"/>.</summary>
    Task<IReadOnlyList<VfsEntry>> ListAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Opens a file for reading on any VFS. Requires <see cref="PluginPermissions.FileRead"/>.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recursive size of a folder on any VFS, computed by the app on all cores (one audit entry per call). Requires
    /// <see cref="PluginPermissions.FileRead"/>.
    /// </summary>
    Task<DirectorySizeInfo> CalculateSizeAsync(string path, IProgress<DirectorySizeInfo>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a text file (UTF-8). Requires <see cref="PluginPermissions.FileWrite"/>.</summary>
    Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>Terminates a process (and optionally its children). Requires <see cref="PluginPermissions.ProcessControl"/>.</summary>
    Task KillProcessAsync(int processId, bool entireTree, CancellationToken cancellationToken = default);

    /// <summary>Runs a program and captures its output. Requires <see cref="PluginPermissions.ShellRun"/>.</summary>
    Task<ProcessRunResult> RunProcessAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>HTTP GET returning the body as text (at most 5 MB). Requires <see cref="PluginPermissions.Network"/>.</summary>
    Task<string> HttpGetTextAsync(Uri url, CancellationToken cancellationToken = default);
}
