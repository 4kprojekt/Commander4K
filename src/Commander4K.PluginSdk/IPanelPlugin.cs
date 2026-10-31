namespace Commander4K.PluginSdk;

public enum RowSeverity : byte
{
    Normal,
    Muted,
    Highlight,
    Warning,
    Error,
}

/// <summary>One row of a plugin panel. Rows form a tree through <see cref="ParentId"/> (child processes, threads, groups).</summary>
/// <param name="Cells">Display text per <see cref="PanelSnapshot.Columns"/> entry, in the same order.</param>
/// <param name="SortKeys">Optional numeric sort key per column (null entries sort by text).</param>
public sealed record PanelRow(
    string Id,
    string? ParentId,
    IReadOnlyList<string> Cells,
    RowSeverity Severity = RowSeverity.Normal,
    IReadOnlyList<long?>? SortKeys = null,
    bool ExpandedByDefault = false);

/// <summary>Complete content of a plugin panel at one moment; it replaces the previous snapshot (cursor and expanded rows are kept by id).</summary>
public sealed record PanelSnapshot(
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<PanelRow> Rows,
    string? Status = null);

public sealed record PanelDetailSection(string Title, IReadOnlyList<KeyValuePair<string, string>> Items);

/// <summary>Information shown under the table for the row under the cursor.</summary>
public sealed record PanelDetails(IReadOnlyList<PanelDetailSection> Sections);

/// <param name="Shortcut">Key handled while the panel has focus, e.g. <c>Delete</c> or <c>Enter</c>.</param>
/// <param name="RequiresConfirmation">Destructive actions (kill a process) are confirmed by the host first.</param>
public sealed record PanelAction(string Id, string Title, string? Shortcut = null, bool RequiresConfirmation = false, bool AppliesToRows = true);

/// <param name="Argument">What the panel was opened for, e.g. a log file path; the host passes the file under the cursor.</param>
/// <param name="Filter">Text typed into the panel's filter box.</param>
/// <param name="Options">JSON object from the folder's <c>c4k.dir</c> (<c>"options"</c>) when the panel was opened by entering that folder.</param>
public sealed record PanelRequest(string? Argument, string? Filter, string? Options = null);

/// <summary>
/// A panel mode backed by a plugin (system information, task manager, log analysis...). The plugin only provides data;
/// the host renders it in the active skin with tree navigation, sorting, a details pane, a filter box and actions.
/// One <see cref="IPanelSession"/> is created per opened panel and disposed when the panel closes.
/// </summary>
public interface IPanelPlugin : IPlugin
{
    /// <summary>Short title for menus and the panel header.</summary>
    string Title { get; }

    string Description { get; }

    /// <summary>When true, the panel needs an argument (a file); the host passes the entry under the cursor.</summary>
    bool RequiresArgument => false;

    IPanelSession CreateSession(PanelRequest request, IPluginHost host);
}

public enum TransferItemKind : byte
{
    File,
    Folder,
}

/// <summary>One file or folder sent from a file panel to a plugin panel.</summary>
public sealed record TransferItem(string Path, string Name, TransferItemKind Kind, long Size);

/// <summary>A way a plugin panel can receive the offered items (e.g. "Add to library").</summary>
/// <param name="Extensions">Extensions, file names or <c>*</c>/<c>?</c> patterns every offered file must match; <c>null</c> = any.</param>
/// <param name="Multiple">Accepts more than one item at once.</param>
/// <param name="Folders">Accepts folders.</param>
/// <param name="ReturnsMessage">The result carries text to show to the user.</param>
public sealed record PanelReceiver(
    string Id,
    string Title,
    string? Description = null,
    IReadOnlyList<string>? Extensions = null,
    bool Multiple = false,
    bool Folders = false,
    bool ReturnsMessage = false);

/// <summary>A way a plugin panel can provide the rows under selection as files.</summary>
public sealed record PanelProvider(string Id, string Title, string? Description = null);

public sealed record TransferItemResult(string Path, bool Ok, string? Error = null);

/// <param name="Text">Message for the user, or <c>null</c>.</param>
public sealed record ReceiveResult(string? Text, IReadOnlyList<TransferItemResult>? Results = null);

/// <summary>A file a plugin provides: exactly one of <paramref name="Text"/>, <paramref name="Data"/> or <paramref name="Url"/>.</summary>
/// <param name="Error">Why this file cannot be written (too large, no content, bad url…), for the user; the content is then empty.</param>
public sealed record ProvidedFile(string Name, string? Text = null, byte[]? Data = null, Uri? Url = null, string? Error = null);

public sealed record ProvideResult(IReadOnlyList<ProvidedFile> Files, string? Text = null);

public readonly record struct TransferProgress(int Done, int Total, long Bytes = 0, string? Text = null);

public interface IPanelSession : IAsyncDisposable
{
    /// <summary>Re-loads automatically at this interval while visible (task manager); <c>null</c> = manual refresh only.</summary>
    TimeSpan? RefreshInterval { get; }

    IReadOnlyList<PanelAction> Actions { get; }

    /// <summary>Called on a background thread; long work should honour the token (Esc cancels it).</summary>
    Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken);

    Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken);

    /// <returns>Text to show to the user, or <c>null</c>. The host reloads the panel afterwards.</returns>
    Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken);

    /// <summary>The panel can receive files sent from a file panel.</summary>
    bool CanReceive => false;

    /// <summary>The panel can provide files for a file panel.</summary>
    bool CanProvide => false;

    Task<IReadOnlyList<PanelReceiver>> GetReceiversAsync(IReadOnlyList<TransferItem> items, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PanelReceiver>>([]);

    Task<ReceiveResult> ReceiveAsync(string receiverId, IReadOnlyList<TransferItem> items, IProgress<TransferProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PanelProvider>> GetProvidersAsync(IReadOnlyList<string> rowIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PanelProvider>>([]);

    Task<ProvideResult> ProvideAsync(string providerId, IReadOnlyList<string> rowIds, IProgress<TransferProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();
}
