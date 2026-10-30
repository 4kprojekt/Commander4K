using System.Globalization;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>
/// Parsed <c>sftp://[user[:password]@]host[:port]/path</c>. Parsed by hand rather than with <see cref="Uri"/> so that
/// '#', '?', '%' and spaces in remote file names survive unchanged.
/// </summary>
internal readonly record struct RemoteEndpoint(string Scheme, string? UserName, string? Password, string Host, int Port, string RemotePath)
{
    public const string Sftp = "sftp";
    public const string Ftp = "ftp";

    /// <summary>Pool key and credential realm: one session per user, host and port.</summary>
    public string SessionKey => $"{Scheme}://{UserName}@{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Authority as written back into entry paths (never includes the password).</summary>
    public string Authority
    {
        get
        {
            var user = UserName is null ? string.Empty : UserName + "@";
            var port = Port == DefaultPort(Scheme) ? string.Empty : ":" + Port.ToString(CultureInfo.InvariantCulture);
            return user + Host + port;
        }
    }

    public string ToPath(string remotePath) => $"{Scheme}://{Authority}{(remotePath.StartsWith('/') ? remotePath : "/" + remotePath)}";

    public static RemoteEndpoint Parse(string path)
    {
        var schemeEnd = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw new FormatException($"'{path}' is not a remote path.");
        }

        var scheme = path[..schemeEnd].ToLowerInvariant();
        var rest = path[(schemeEnd + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var remotePath = slash < 0 ? "/" : rest[slash..];

        string? user = null, password = null;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userInfo = authority[..at];
            authority = authority[(at + 1)..];
            var colon = userInfo.IndexOf(':');
            user = Uri.UnescapeDataString(colon < 0 ? userInfo : userInfo[..colon]);
            password = colon < 0 ? null : Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        }

        var port = DefaultPort(scheme);
        var portSeparator = authority.LastIndexOf(':');
        if (portSeparator > 0 && !authority.EndsWith(']'))
        {
            port = int.Parse(authority[(portSeparator + 1)..], CultureInfo.InvariantCulture);
            authority = authority[..portSeparator];
        }

        if (authority.Length == 0)
        {
            throw new FormatException($"'{path}' has no host.");
        }

        return new RemoteEndpoint(scheme, string.IsNullOrEmpty(user) ? null : user, password, authority, port, remotePath);
    }

    public static int DefaultPort(string scheme) => scheme == Sftp ? 22 : 21;
}
