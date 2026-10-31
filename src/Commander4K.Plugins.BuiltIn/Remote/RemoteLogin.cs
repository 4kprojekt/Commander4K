namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>
/// How to sign in to an SFTP/FTP server: a password, a private key file (with an optional passphrase), or both (the
/// server picks). Without either, SFTP tries the user's default keys in <c>~/.ssh</c>, like the <c>ssh</c> client.
/// </summary>
public sealed record RemoteLogin(string UserName, string? Password = null, string? PrivateKeyFile = null, string? Passphrase = null)
{
    /// <summary>Default OpenSSH key files that exist for the current user, in the order <c>ssh</c> tries them.</summary>
    public static IReadOnlyList<string> DefaultKeyFiles()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return [.. new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(name => Path.Combine(folder, name)).Where(File.Exists)];
    }
}
