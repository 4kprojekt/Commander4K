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
public sealed record PanelRequest(string? Argument, string? Filter);

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
}
