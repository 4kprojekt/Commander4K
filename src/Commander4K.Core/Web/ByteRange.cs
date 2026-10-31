using System.Globalization;

namespace Commander4K.Core.Web;

/// <summary>How to answer a request's <c>Range</c> header.</summary>
public enum ByteRangeKind : byte
{
    /// <summary>No header: 200 with the whole file.</summary>
    None,

    /// <summary>Malformed, another unit or several ranges: ignore the header, 200 with the whole file (RFC 9110).</summary>
    Ignore,

    /// <summary>416 with <c>Content-Range: bytes */length</c>.</summary>
    Unsatisfiable,

    /// <summary>206 with <c>Content-Range: bytes start-end/length</c>.</summary>
    Satisfiable,
}

public readonly record struct ByteRangeResult(ByteRangeKind Kind, ByteRange Range);

/// <summary>An inclusive byte range of a resource, from an HTTP <c>Range</c> header.</summary>
public readonly record struct ByteRange(long Start, long End)
{
    private static readonly ByteRangeResult NoRange = new(ByteRangeKind.None, default);
    private static readonly ByteRangeResult IgnoreRange = new(ByteRangeKind.Ignore, default);
    private static readonly ByteRangeResult Unsatisfiable = new(ByteRangeKind.Unsatisfiable, default);

    public long Length => End - Start + 1;

    /// <summary>Evaluates a single <c>bytes=a-b</c>, <c>a-</c> or <c>-n</c> range against a resource of <paramref name="length"/> bytes.</summary>
    public static ByteRangeResult Evaluate(string? header, long length)
    {
        if (header is null)
        {
            return NoRange;
        }

        const string prefix = "bytes=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return IgnoreRange;
        }

        var spec = header.AsSpan(prefix.Length);
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return IgnoreRange;
        }

        var first = spec[..dash];
        var last = spec[(dash + 1)..];
        if ((first.IsEmpty && last.IsEmpty) || !IsDigits(first) || !IsDigits(last))
        {
            return IgnoreRange;
        }

        if (first.IsEmpty)
        {
            // Suffix: the last n bytes; a number beyond long means "more than the file".
            var suffix = Parse(last);
            if (suffix < 0)
            {
                suffix = long.MaxValue;
            }

            if (suffix == 0 || length <= 0)
            {
                return Unsatisfiable;
            }

            return Satisfiable(Math.Max(0, length - suffix), length - 1);
        }

        var start = Parse(first);
        if (start < 0 || start >= length)
        {
            return Unsatisfiable;
        }

        if (last.IsEmpty)
        {
            return Satisfiable(start, length - 1);
        }

        var end = Parse(last);
        if (end >= 0 && end < start)
        {
            return IgnoreRange;
        }

        return Satisfiable(start, end < 0 ? length - 1 : Math.Min(end, length - 1));
    }

    private static ByteRangeResult Satisfiable(long start, long end) => new(ByteRangeKind.Satisfiable, new ByteRange(start, end));

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The value, or -1 when it does not fit a long (digits only).</summary>
    private static long Parse(ReadOnlySpan<char> digits) =>
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
}
