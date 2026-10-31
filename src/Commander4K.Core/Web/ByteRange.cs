using System.Globalization;

namespace Commander4K.Core.Web;

/// <summary>An inclusive byte range of a resource, from an HTTP <c>Range</c> header.</summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    /// <summary>Parses a single <c>bytes=a-b</c>, <c>a-</c> or <c>-n</c> range; null for a missing, malformed, multi-range or unsatisfiable header.</summary>
    public static ByteRange? Parse(string? header, long length)
    {
        const string prefix = "bytes=";
        if (header is null || length <= 0 || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spec = header.AsSpan(prefix.Length).Trim();
        if (spec.Contains(','))
        {
            return null;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.IsEmpty)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return null;
            }

            return new ByteRange(Math.Max(0, length - suffix), length - 1);
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start >= length)
        {
            return null;
        }

        if (last.IsEmpty)
        {
            return new ByteRange(start, length - 1);
        }

        if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var end) || end < start)
        {
            return null;
        }

        return new ByteRange(start, Math.Min(end, length - 1));
    }
}
