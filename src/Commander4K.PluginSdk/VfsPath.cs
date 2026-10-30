namespace Commander4K.PluginSdk;

public static class VfsPath
{
    public const string SchemeSeparator = "://";

    /// <summary>Pseudo-scheme for plain Windows paths such as <c>C:\Users</c>.</summary>
    public const string FileScheme = "file";

    /// <summary>Pseudo-scheme for UNC paths such as <c>\\server\share</c>.</summary>
    public const string SmbScheme = "smb";

    /// <summary>
    /// Returns the routing scheme of a path: the explicit <c>scheme://</c> prefix, <see cref="SmbScheme"/> for UNC
    /// paths, <see cref="FileScheme"/> for drive paths, or <c>null</c> when the path is not recognised.
    /// </summary>
    public static string? GetScheme(ReadOnlySpan<char> path)
    {
        var separator = path.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (separator > 0)
        {
            return path[..separator].ToString().ToLowerInvariant();
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return SmbScheme;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return IsDrivePath(path[4..]) ? FileScheme : null;
        }

        if (path.Length > 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return SmbScheme;
        }

        return IsDrivePath(path) ? FileScheme : null;
    }

    /// <summary>Strips a <c>scheme://</c> prefix, returning the remainder (or the input when there is none).</summary>
    public static string StripScheme(string path)
    {
        var separator = path.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        return separator > 0 ? path[(separator + SchemeSeparator.Length)..] : path;
    }

    private static bool IsDrivePath(ReadOnlySpan<char> path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
