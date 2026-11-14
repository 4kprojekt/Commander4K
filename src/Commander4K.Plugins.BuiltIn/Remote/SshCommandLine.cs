using System.Globalization;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>Arguments for OpenSSH's <c>ssh</c> that open an interactive shell in the folder of an <c>sftp://</c> path.</summary>
public static class SshCommandLine
{
    /// <param name="login">The saved login of the session: its key file and user are passed on; a password never is (ssh asks).</param>
    /// <returns><c>null</c> when <paramref name="sftpPath"/> is not an sftp path.</returns>
    public static IReadOnlyList<string>? For(string sftpPath, RemoteLogin? login)
    {
        RemoteEndpoint endpoint;
        try
        {
            endpoint = RemoteEndpoint.Parse(sftpPath);
        }
        catch (FormatException)
        {
            return null;
        }

        // A host starting with '-' would be read by ssh as an option.
        // Windows Terminal splits its command line on ';': no terminal for a host, user or key file with one.
        var user = endpoint.UserName ?? (login?.UserName is { Length: > 0 } name ? name : null);
        if (endpoint.Scheme != RemoteEndpoint.Sftp || endpoint.Host.StartsWith('-')
            || (endpoint.Host + user + login?.PrivateKeyFile).AsSpan().ContainsAny(';', '"'))
        {
            return null;
        }

        var args = new List<string> { "-t", "-p", endpoint.Port.ToString(CultureInfo.InvariantCulture) };
        if (login?.PrivateKeyFile is { Length: > 0 } key)
        {
            args.Add("-i");
            args.Add(key);
        }

        if (user is not null)
        {
            args.Add("-l");
            args.Add(user);
        }

        args.Add("--");
        args.Add(endpoint.Host);

        // shortcut: Windows Terminal splits its command line on ';' and Windows quoting mangles '"', so a folder with
        // either starts in the home folder; upgrade when an embedded terminal talks to the server directly.
        var folder = endpoint.RemotePath;
        if (folder is not "/" && !folder.AsSpan().ContainsAny(';', '"'))
        {
            // No ';' in the command either: "(cd || cd) && exec" falls back to the home folder when the cd fails. Plain
            // $SHELL so fish reads it too; shortcut: a Windows OpenSSH server (cmd) cannot, upgrade if someone needs it.
            args.Add("cd -- '" + folder.Replace("'", @"'\''", StringComparison.Ordinal) + "' || cd && exec $SHELL -l");
        }

        return args;
    }
}
