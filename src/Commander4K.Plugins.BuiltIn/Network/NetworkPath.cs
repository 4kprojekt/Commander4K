using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Network;

internal enum NetworkPathLevel : byte
{
    Root,
    Host,
    Share,
}

/// <summary>
/// Normalised view of <c>net://</c>, <c>smb://</c> and UNC paths. Host level is <c>net://HOST</c> (or a bare
/// <c>\\HOST</c>); anything with a share component becomes a plain UNC path, which Windows can open directly.
/// </summary>
internal readonly record struct NetworkPath(NetworkPathLevel Level, string Host, string Unc)
{
    public const string RootPath = "net://";
    public const string AddConnectionPath = "net://?add";

    public static NetworkPath Parse(string path)
    {
        string rest;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            rest = path[8..];
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            rest = path[2..];
        }
        else
        {
            rest = VfsPath.StripScheme(path);
        }

        rest = rest.Replace('/', '\\').Trim('\\');
        if (rest.Length == 0)
        {
            return new NetworkPath(NetworkPathLevel.Root, string.Empty, string.Empty);
        }

        var separator = rest.IndexOf('\\');
        if (separator < 0)
        {
            return new NetworkPath(NetworkPathLevel.Host, rest, @"\\" + rest);
        }

        return new NetworkPath(NetworkPathLevel.Share, rest[..separator], @"\\" + rest);
    }

    public static string HostPath(string host) => RootPath + host;

    /// <summary><c>\\server\share</c> of a share-level path, the unit credentials are granted for.</summary>
    public string ShareRoot
    {
        get
        {
            var afterHost = Unc.IndexOf('\\', 2 + Host.Length + 1);
            return afterHost < 0 ? Unc : Unc[..afterHost];
        }
    }
}
