using Commander4K.PluginSdk;

namespace Commander4K.Core.Mcp;

/// <summary>
/// Decides whether a VFS path lies inside one of the folders the user allowed for MCP. Paths are normalized by
/// text only (no disk or network access): <c>.</c> and <c>..</c> are resolved, and <c>..</c> never climbs above the
/// root of a path (drive, <c>\\server\share</c>, registry hive, remote host), so it cannot reach a different root.
/// Paths that are not recognised (relative, drive-relative, device paths, NTFS stream syntax such as
/// <c>name:stream</c> or <c>folder::$INDEX_ALLOCATION</c>) are never inside anything.
/// </summary>
public static class McpPathPolicy
{
    private static readonly string[] ArchiveSchemes = ["zip", "7z", "tar", "rar", "gz"];
    private static readonly string[] CaseInsensitiveSchemes = [VfsPath.FileScheme, VfsPath.SmbScheme, "reg", "net", "local"];

    /// <summary>The path in canonical form, or the input unchanged when it is not a recognised absolute path.</summary>
    public static string Normalize(string path) => TryNormalize(path) ?? path;

    public static bool IsAllowed(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (IsInside(path, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> is inside one of the protected folders or, with <paramref name="orAbove"/>
    /// (writing and recursive tools), when a protected folder is inside it.
    /// </summary>
    public static bool IsProtected(string path, IReadOnlyList<string> protectedRoots, bool orAbove = false) =>
        protectedRoots.Any(p => IsInside(path, p) || (orAbove && IsInside(p, path)));

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it.</summary>
    public static bool IsInside(string path, string root)
    {
        var p = TryNormalize(path);
        var r = TryNormalize(root);
        if (p is null || r is null)
        {
            return false;
        }

        var pathArchive = SplitArchive(p);
        var rootArchive = SplitArchive(r);
        if (rootArchive is { } ra)
        {
            // A root inside an archive only contains entries of that same archive.
            return pathArchive is { } pa
                && string.Equals(pa.Scheme, ra.Scheme, StringComparison.Ordinal)
                && string.Equals(pa.Container, ra.Container, ComparisonFor(pa.Container))
                && IsBelow(pa.Inner, ra.Inner, '/', StringComparison.Ordinal);
        }

        if (pathArchive is { } archive)
        {
            // An archive entry is judged by the archive file that holds it.
            p = archive.Container;
        }

        var scheme = VfsPath.GetScheme(p);
        if (!string.Equals(scheme, VfsPath.GetScheme(r), StringComparison.Ordinal))
        {
            return false;
        }

        return IsBelow(p, r, SeparatorFor(scheme!, p), ComparisonFor(p));
    }

    private static bool IsBelow(string path, string root, char separator, StringComparison comparison)
    {
        if (!path.StartsWith(root, comparison))
        {
            return false;
        }

        return path.Length == root.Length || root[^1] == separator || path[root.Length] == separator;
    }

    private static StringComparison ComparisonFor(string normalized)
    {
        var scheme = VfsPath.GetScheme(normalized);
        return scheme is not null && CaseInsensitiveSchemes.Contains(scheme) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private static char SeparatorFor(string scheme, string normalized) => scheme switch
    {
        VfsPath.FileScheme or VfsPath.SmbScheme or "reg" => '\\',
        _ => '/',
    };

    private static (string Scheme, string Container, string Inner)? SplitArchive(string normalized)
    {
        var scheme = VfsPath.GetScheme(normalized);
        if (scheme is null || !ArchiveSchemes.Contains(scheme))
        {
            return null;
        }

        var rest = normalized[(scheme.Length + VfsPath.SchemeSeparator.Length)..];
        var split = rest.IndexOf("::", StringComparison.Ordinal);
        return split < 0 ? (scheme, rest, "/") : (scheme, rest[..split], rest[(split + 2)..]);
    }

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var scheme = VfsPath.GetScheme(path);
        return scheme switch
        {
            null => null,
            VfsPath.FileScheme or VfsPath.SmbScheme => NormalizeWindows(path),
            "reg" => NormalizeRegistry(path),
            "sftp" or "ftp" => NormalizeRemote(scheme, path),
            _ when ArchiveSchemes.Contains(scheme) => NormalizeArchive(scheme, path),
            _ => NormalizeGeneric(scheme, path),
        };
    }

    private static string? NormalizeWindows(string path)
    {
        // Long-path prefixes are resolved by Windows without normalization, so strip them before resolving.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share is the root; GetFullPath keeps ".." below it.
            var parts = path[2..].Split('\\');
            if (parts.Length == 0 || parts[0] is "" or "." or ".." || (parts.Length > 1 && parts[1] is "." or ".."))
            {
                return null;
            }
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // Drive roots keep their separator (C:\); \\server\share has none.
        var root = Path.GetPathRoot(full) ?? string.Empty;

        // NTFS stream syntax (name:stream, folder::$INDEX_ALLOCATION) opens the real file or folder under a name that
        // is not textually inside the protected folders; ':' is never part of a plain Windows name, so refuse it.
        if (full.AsSpan(root.Length).Contains(':'))
        {
            return null;
        }

        var trimmed = full.TrimEnd('\\');
        return trimmed.Length < root.Length ? root : trimmed;
    }

    /// <summary><c>reg://HIVE\key\\ValueName</c>: the value name after the double backslash is kept as is.</summary>
    private static string? NormalizeRegistry(string path)
    {
        var rest = VfsPath.StripScheme(path);
        var value = rest.IndexOf(@"\\", StringComparison.Ordinal);
        var key = value < 0 ? rest : rest[..value];
        var parts = key.Split('\\');
        if (parts[0] is "" or "." or "..")
        {
            return null;
        }

        // The hive stays: ".." below it cannot reach another hive.
        var segments = Resolve(parts, keep: 1);

        var normalized = "reg://" + string.Join('\\', segments);
        return value < 0 ? normalized : normalized + rest[value..];
    }

    /// <summary><c>sftp://user@host:port/path</c>, parsed by hand so <c>#</c>, <c>?</c> and <c>%</c> stay literal.</summary>
    private static string? NormalizeRemote(string scheme, string path)
    {
        var rest = VfsPath.StripScheme(path);
        var slash = rest.IndexOfAny(['/', '\\']);
        var authority = slash < 0 ? rest : rest[..slash];
        if (authority.Length == 0)
        {
            return null;
        }

        // Windows-hosted servers also treat '\' as a separator; on Unix servers that only means false denials.
        var segments = slash < 0 ? [] : Resolve(rest[(slash + 1)..].Split('/', '\\'));
        return scheme + "://" + authority + "/" + string.Join('/', segments);
    }

    /// <summary><c>zip://&lt;container&gt;::/inner/path</c>: the first <c>::</c> splits; the container is any VFS path.</summary>
    private static string? NormalizeArchive(string scheme, string path)
    {
        var rest = VfsPath.StripScheme(path);
        var split = rest.IndexOf("::", StringComparison.Ordinal);
        var container = TryNormalize(split < 0 ? rest : rest[..split]);
        if (container is null)
        {
            return null;
        }

        var inner = split < 0 ? string.Empty : rest[(split + 2)..];
        return scheme + "://" + container + "::/" + string.Join('/', Resolve(inner.Split('/', '\\')));
    }

    private static string? NormalizeGeneric(string scheme, string path)
    {
        var segments = Resolve(VfsPath.StripScheme(path).Split('/', '\\'));
        return scheme + "://" + string.Join('/', segments);
    }

    /// <summary>Drops empty and <c>.</c> segments and applies <c>..</c>, never removing the first <paramref name="keep"/>.</summary>
    private static List<string> Resolve(string[] parts, int keep = 0)
    {
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (result.Count > keep)
                {
                    result.RemoveAt(result.Count - 1);
                }

                continue;
            }

            result.Add(part);
        }

        return result;
    }
}
