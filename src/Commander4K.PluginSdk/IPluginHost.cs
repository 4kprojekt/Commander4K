namespace Commander4K.PluginSdk;

/// <param name="Elevated">Run as administrator in a visible window (output is not captured); the host always asks the user first.</param>
public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    bool Elevated = false,
    TimeSpan? Timeout = null);

public sealed record ProcessRunResult(int ExitCode, string Output, string Error);

/// <summary>One running process (<see cref="IPluginHost.ListProcessesAsync"/>).</summary>
public sealed record ProcessSnapshot(int Id, int? ParentId, string Name, string? Path, long WorkingSetBytes, DateTimeOffset? StartTime);

public sealed record DriveSnapshot(string Name, string Type, string? Format, string? Label, long? TotalBytes, long? FreeBytes);

/// <summary>Basic facts about the machine (<see cref="IPluginHost.GetSystemInfoAsync"/>).</summary>
public sealed record SystemSnapshot(
    string OsDescription,
    string OsVersion,
    string Architecture,
    string MachineName,
    string UserName,
    int ProcessorCount,
    long TotalMemoryBytes,
    IReadOnlyList<DriveSnapshot> Drives);

/// <param name="Method">GET, POST, PUT, PATCH, DELETE or HEAD.</param>
/// <param name="Body">Request body as text (JSON, form data…), sent with <paramref name="ContentType"/>.</param>
public sealed record HttpRequestSpec(string Method, Uri Url, IReadOnlyDictionary<string, string>? Headers = null, string? Body = null, string? ContentType = null);

/// <summary>Any status is returned (no exception for 4xx/5xx); the body is text, at most 5 MB.</summary>
public sealed record HttpResponseData(int Status, IReadOnlyDictionary<string, string> Headers, string Body);

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

    /// <summary>
    /// Short message in the status area. With <paramref name="target"/> the message is clickable; the target is handled
    /// like a tile target (a path, an http(s) URL or a <c>c4k:</c> link).
    /// </summary>
    void Notify(string message, bool isError = false, string? target = null);

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

    /// <summary>Opens an http(s) page in the internal browser, in the panel next to the file list.</summary>
    Task OpenUrlAsync(string url);

    /// <summary>
    /// Searches the web or asks an AI assistant in the internal browser. <paramref name="engine"/>: <c>null</c> = the
    /// user's search engine, <c>"assistant"</c> = the user's AI assistant, or an id (<c>google</c>, <c>chatgpt</c>,
    /// <c>gemini</c>, <c>claude</c>, <c>perplexity</c>, <c>bing</c>, <c>duckduckgo</c>).
    /// </summary>
    Task SearchWebAsync(string query, string? engine = null);

    /// <summary>Writes to the plugin's section of the audit log.</summary>
    void Log(string message);

    /// <summary>Puts text on the clipboard (writing only; plugins cannot read the clipboard).</summary>
    Task CopyTextAsync(string text);

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

    /// <summary>Any HTTP method with headers and a text body; returns status, headers and body. Requires <see cref="PluginPermissions.Network"/>.</summary>
    Task<HttpResponseData> HttpRequestAsync(HttpRequestSpec request, CancellationToken cancellationToken = default);

    /// <summary>Entry details, or <c>null</c> when the path does not exist. Requires <see cref="PluginPermissions.FileRead"/>.</summary>
    Task<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a binary file. Requires <see cref="PluginPermissions.FileWrite"/>.</summary>
    Task WriteBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Creates a folder (and missing parents). Requires <see cref="PluginPermissions.FileWrite"/>.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file, or a folder (with <paramref name="recursive"/> its content too). Requires <see cref="PluginPermissions.FileWrite"/>.</summary>
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default);

    /// <summary>Moves or renames a file or folder. Requires <see cref="PluginPermissions.FileWrite"/>.</summary>
    Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);

    /// <summary>Running processes. Requires <see cref="PluginPermissions.ProcessRead"/>.</summary>
    Task<IReadOnlyList<ProcessSnapshot>> ListProcessesAsync(CancellationToken cancellationToken = default);

    /// <summary>OS, machine, memory and drives. Requires <see cref="PluginPermissions.SystemInfo"/>.</summary>
    Task<SystemSnapshot> GetSystemInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Mail through the app (IMAP/SMTP, parsing, composing). Every member requires <see cref="PluginPermissions.Mail"/>.</summary>
    IMailHost Mail { get; }
}
