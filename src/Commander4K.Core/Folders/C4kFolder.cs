using System.Text;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Folders;

public enum C4kLayout
{
    /// <summary>No <c>*.c4k</c> files: an ordinary folder.</summary>
    None,

    /// <summary>One file: its view fills the panel.</summary>
    Single,

    /// <summary>Several files: a dashboard of tiles.</summary>
    Dashboard,
}

/// <param name="Skipped">Files beyond <see cref="C4kReader.MaxItems"/>.</param>
public sealed record C4kFolder(string Folder, IReadOnlyList<C4kItem> Items, int Skipped)
{
    public C4kLayout Layout => Items.Count switch { 0 => C4kLayout.None, 1 => C4kLayout.Single, _ => C4kLayout.Dashboard };

    public C4kItem? Single => Items.Count == 1 ? Items[0] : null;

    /// <summary>The single item's error.</summary>
    public string? Error => Single?.Error;
}

public static class C4kReader
{
    public const string Extension = ".c4k";

    public const int MaxItems = 50;

    public const long MaxBytes = 256 * 1024;

    /// <summary>A file, not hidden (so not the <c>.thumbnail.c4k</c> cache folder), small enough to read.</summary>
    public static bool IsItemFile(VfsEntry entry) =>
        entry.Kind == VfsEntryKind.File && entry.Size <= MaxBytes
        && entry.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && !entry.Name.StartsWith('.');

    public static C4kFolder Build(string folder, IEnumerable<(string FileName, string Json)> files)
    {
        var sorted = files.OrderBy(f => f.FileName, NaturalComparer.Instance).ToList();
        return new C4kFolder(folder, [.. sorted.Take(MaxItems).Select(f => C4kItem.Parse(folder, f.FileName, f.Json))], Math.Max(0, sorted.Count - MaxItems));
    }

    /// <returns><c>null</c> when there are no item files, on timeout, or on any failure but the caller's cancellation.</returns>
    public static async Task<C4kFolder?> ReadAsync(Vfs.VfsRouter router, string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var names = new List<string>();
            await foreach (var entry in router.OpenListing(folder, limit.Token).ReadAllAsync(limit.Token).ConfigureAwait(false))
            {
                if (IsItemFile(entry))
                {
                    names.Add(entry.Name);
                }
            }

            if (names.Count == 0)
            {
                return null;
            }

            // Only the first MaxItems by name are read; the rest are counted as skipped.
            var files = new List<(string, string)>();
            foreach (var name in names.OrderBy(n => n, NaturalComparer.Instance).Take(MaxItems))
            {
                await using var stream = await router.OpenReadAsync(C4kJson.ChildPath(folder, name), limit.Token).ConfigureAwait(false);
                using var text = new StreamReader(stream, Encoding.UTF8);
                files.Add((name, await text.ReadToEndAsync(limit.Token).ConfigureAwait(false)));
            }

            return Build(folder, files) with { Skipped = names.Count - files.Count };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Digit runs compare as numbers, everything else ignoring case.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsAsciiDigit(y[j]))
                    {
                        j++;
                    }

                    var a = x.AsSpan(si, i - si).TrimStart('0');
                    var b = y.AsSpan(sj, j - sj).TrimStart('0');
                    var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.CompareTo(b, StringComparison.Ordinal);
                    if (c != 0)
                    {
                        return c;
                    }
                }
                else
                {
                    var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0)
                    {
                        return c;
                    }

                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
