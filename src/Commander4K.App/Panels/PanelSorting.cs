using Commander4K.PluginSdk;

namespace Commander4K.App.Panels;

public enum SortColumn : byte
{
    Name,
    Extension,
    Size,
    Date,
}

public readonly record struct SortOrder(SortColumn Column, bool Descending)
{
    public static SortOrder Default => new(SortColumn.Name, false);

    public SortOrder Toggle(SortColumn column) => Column == column ? this with { Descending = !Descending } : new(column, false);
}

/// <summary>Containers before files (Total Commander order), then by the chosen column.</summary>
public static class PanelSorting
{
    public static void Sort(List<VfsEntry> entries, SortOrder order, IReadOnlyDictionary<string, long>? directorySizes = null) =>
        entries.Sort((a, b) => Compare(a, b, order, directorySizes));

    private static int Compare(VfsEntry a, VfsEntry b, SortOrder order, IReadOnlyDictionary<string, long>? sizes)
    {
        var group = Rank(a).CompareTo(Rank(b));
        if (group != 0)
        {
            return group;
        }

        var result = order.Column switch
        {
            SortColumn.Extension => string.Compare(Path.GetExtension(a.Name), Path.GetExtension(b.Name), StringComparison.OrdinalIgnoreCase),
            SortColumn.Size => SizeOf(a, sizes).CompareTo(SizeOf(b, sizes)),
            SortColumn.Date => a.LastWriteTime.CompareTo(b.LastWriteTime),
            _ => 0,
        };
        if (result == 0)
        {
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        }

        if (result == 0)
        {
            result = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        }

        if (result == 0)
        {
            result = string.Compare(a.Path, b.Path, StringComparison.Ordinal);
        }

        return order.Descending ? -result : result;
    }

    /// <summary>Groups, in order: "..", actions, drives, folders, shares, hosts, files ("This PC" mixes all of them).</summary>
    private static int Rank(VfsEntry entry) => entry.Kind switch
    {
        VfsEntryKind.ParentLink => 0,
        VfsEntryKind.Action => 1,
        VfsEntryKind.Drive => 2,
        VfsEntryKind.Share => 4,
        VfsEntryKind.Host => 5,
        _ when entry.IsContainer => 3,
        _ => 6,
    };

    private static long SizeOf(VfsEntry entry, IReadOnlyDictionary<string, long>? sizes) =>
        sizes is not null && sizes.TryGetValue(entry.Path, out var size) ? size : entry.Size;
}
