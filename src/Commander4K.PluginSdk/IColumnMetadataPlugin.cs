namespace Commander4K.PluginSdk;

public enum ColumnAlignment : byte
{
    Left,
    Right,
    Center,
}

public sealed record ColumnDefinition(string Id, string Header, ColumnAlignment Alignment = ColumnAlignment.Left, double DefaultWidth = 100);

/// <param name="SortKey">Numeric key for sorting; when <c>null</c> the column sorts by <paramref name="Text"/>.</param>
public readonly record struct ColumnValue(string Text, long? SortKey = null)
{
    public static ColumnValue Empty { get; } = new(string.Empty);
}

/// <summary>
/// Extra columns (Total Commander WDX equivalent) such as Git status, EXIF or hashes. Values are requested on
/// background threads after the listing is visible (rows in view first, several in parallel) and patched into rows
/// as they arrive; leaving the folder cancels the requests. Like every plugin, a column reads files and runs
/// programs only through <paramref name="host"/>, so its manifest permissions apply and calls are audited.
/// </summary>
public interface IColumnMetadataPlugin : IPlugin
{
    IReadOnlyList<ColumnDefinition> Columns { get; }

    bool Supports(in VfsEntry entry);

    ValueTask<ColumnValue> GetValueAsync(VfsEntry entry, string columnId, IPluginHost host, CancellationToken cancellationToken);
}
