namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>
/// How to sign in to an SFTP/FTP server: a password, a private key file (with an optional passphrase), or both (the
/// server picks). Without either, SFTP tries the user's default keys in <c>~/.ssh</c>, like the <c>ssh</c> client.
/// </summary>
public sealed record RemoteLogin(string UserName, string? Password = null, string? PrivateKeyFile = null, string? Passphrase = null)
{
    /// <summary>
    /// Key files that exist for the current user, in the order <c>ssh</c> tries them: the <c>IdentityFile</c>s of
    /// <paramref name="host"/> in <c>~/.ssh/config</c>, then the default OpenSSH names.
    /// </summary>
    public static IReadOnlyList<string> DefaultKeyFiles(string? host = null)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var standard = new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(name => Path.Combine(folder, name)).Where(File.Exists);
        return [.. (host is null ? [] : SshConfig.For(host).IdentityFiles).Concat(standard).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The user to sign in as when the path names none: <c>User</c> from <c>~/.ssh/config</c>, else the Windows user.</summary>
    public static string DefaultUser(string host) => SshConfig.For(host).User ?? Environment.UserName;
}
