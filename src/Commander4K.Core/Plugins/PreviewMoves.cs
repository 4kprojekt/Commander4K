using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>
/// Decides whether the host may follow a preview result's <c>path</c> (a renamed or new entry): the same scheme, a
/// normalised file path, and a place inside the third ancestor of the current entry (<c>Poczta\Firma\Odebrane\x.eml</c>
/// may move anywhere under <c>Poczta\</c>). Anything else is ignored and the view keeps its entry.
/// </summary>
public static class PreviewMoves
{
    private const int Levels = 3;

    public static bool IsAcceptable(string fromPath, string toPath)
    {
        if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath) || toPath.EndsWith(Path.DirectorySeparatorChar) || toPath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        var scheme = VfsPath.GetScheme(fromPath);
        if (!string.Equals(scheme, VfsPath.GetScheme(toPath), StringComparison.Ordinal))
        {
            return false;
        }

        if (scheme == VfsPath.FileScheme)
        {
            if (!Path.IsPathFullyQualified(toPath) || !Path.IsPathFullyQualified(fromPath))
            {
                return false;
            }

            fromPath = Path.GetFullPath(fromPath);
            toPath = Path.GetFullPath(toPath);
        }

        var to = Segments(toPath);
        if (to.Contains("..") || to.Contains("."))
        {
            return false;
        }

        var from = Segments(fromPath);
        if (from.Count == 0)
        {
            return false;
        }

        // A message in a month folder (Odebrane\2026\11\x.eml) counts from its mail folder, as if it lay directly in it.
        var levels = from.Count >= 3 && IsDigits(from[^2], 2) && IsDigits(from[^3], 4) ? Levels + 2 : Levels;
        var root = from.Take(Math.Max(1, from.Count - levels)).ToList();
        return to.Count > root.Count && root.Zip(to).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDigits(string segment, int length) => segment.Length == length && segment.All(char.IsAsciiDigit);

    private static List<string> Segments(string path) => [.. path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)];
}
