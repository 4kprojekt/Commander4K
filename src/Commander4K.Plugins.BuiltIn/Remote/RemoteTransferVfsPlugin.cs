using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Network;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>
/// SFTP (SSH.NET) and FTP/FTPS (FluentFTP) servers. Sessions are pooled per user/host/port and kept alive, so
/// navigating a remote tree does not reconnect on every directory. Listing, stat, reading, uploading, mkdir,
/// server-side move and recursive delete.
/// </summary>
public sealed class RemoteTransferVfsPlugin : IVfsProviderPlugin, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IRemoteSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RemoteLogin> _logins = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "builtin.remote";

    public string DisplayName => "SFTP / FTP";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.remote", "SFTP / FTP", Loc.T("Serwery SFTP i FTP."), PluginPermissions.Network, PluginPermissions.FileRead, PluginPermissions.FileWrite);

    public IReadOnlyList<string> Schemes { get; } = [RemoteEndpoint.Sftp, RemoteEndpoint.Ftp];

    public VfsCapabilities Capabilities =>
        VfsCapabilities.Read | VfsCapabilities.Write | VfsCapabilities.CreateDirectory | VfsCapabilities.Delete | VfsCapabilities.Move;

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Decides whether to trust an SSH host key. Defaults to accepting every key, which is vulnerable to
    /// man-in-the-middle attacks; the UI must replace it with a known-hosts check before production use.
    /// </summary>
    public HostKeyValidator HostKeyValidator { get; set; } = static (_, _, _) => true;

    /// <summary>Stores credentials for a realm reported by <see cref="VfsAuthenticationRequiredException.Realm"/>.</summary>
    public void SetCredentials(string realm, NetworkCredential credential) => SetLogin(realm, new RemoteLogin(credential.UserName, credential.Password));

    /// <summary>
    /// Stores how to sign in for <paramref name="realm"/> (a session key reported by the exception) or for any
    /// <c>sftp://</c>/<c>ftp://</c> path (saved connections). The pooled session for it is dropped, so the next
    /// navigation signs in with the new login.
    /// </summary>
    public void SetLogin(string realmOrPath, RemoteLogin login)
    {
        var key = RealmFor(realmOrPath);
        _logins[key] = login;
        if (_sessions.TryRemove(key, out var stale) && stale.IsValueCreated && stale.Value.IsCompletedSuccessfully)
        {
            _ = stale.Value.Result.DisposeAsync().AsTask();
        }
    }

    /// <summary>
    /// How an SFTP session for <paramref name="path"/> would sign in: a password in the path, a saved login, or the
    /// default <c>~/.ssh</c> keys as the given or Windows user; <c>null</c> when there is nothing to try.
    /// </summary>
    public RemoteLogin? LoginFor(string path)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        return endpoint.Password is not null ? new RemoteLogin(endpoint.UserName ?? string.Empty, endpoint.Password)
            : _logins.GetValueOrDefault(endpoint.SessionKey)
            ?? (RemoteLogin.DefaultKeyFiles().Count > 0 ? new RemoteLogin(endpoint.UserName ?? Environment.UserName) : null);
    }

    /// <summary>The user written in a remote path (<c>sftp://me@host/…</c>); <c>null</c> when it has none or is not one.</summary>
    public static string? UserNameOf(string path)
    {
        try
        {
            return RemoteEndpoint.Parse(path).UserName;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The session key of a path (user, host and port), or the realm itself when it already is one.</summary>
    public static string RealmFor(string realmOrPath)
    {
        try
        {
            return RemoteEndpoint.Parse(realmOrPath).SessionKey;
        }
        catch (FormatException)
        {
            return realmOrPath;
        }
    }

    public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var session = await GetSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await foreach (var entry in session.ListAsync(endpoint, cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <summary>One round trip (stat); <c>null</c> when the path does not exist — copy jobs rely on that to create folders.</summary>
    public async ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var session = await GetSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return await session.GetEntryAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The server root goes up to the host in the network neighbourhood.</summary>
    public string? GetParentPath(string path)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var remote = endpoint.RemotePath.TrimEnd('/');
        if (remote.Length == 0)
        {
            return NetworkPath.HostPath(endpoint.Host);
        }

        var slash = remote.LastIndexOf('/');
        return endpoint.ToPath(slash <= 0 ? "/" : remote[..slash]);
    }

    public string Combine(string directoryPath, string name)
    {
        var endpoint = RemoteEndpoint.Parse(directoryPath);
        return endpoint.ToPath(endpoint.RemotePath.TrimEnd('/') + "/" + name);
    }

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var session = await GetSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return await session.OpenReadAsync(endpoint.RemotePath, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var session = await GetSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await session.CreateDirectoryAsync(endpoint.RemotePath, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var session = await GetSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return await session.OpenWriteAsync(endpoint.RemotePath, overwrite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Files are deleted directly; directories with their whole contents, but only when <paramref name="recursive"/>
    /// is set. Symbolic links are removed, never followed. A path that no longer exists is not an error.
    /// </summary>
    public async ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var session = await GetSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (await session.GetEntryAsync(endpoint, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return;
        }

        var isDirectory = entry.Kind == VfsEntryKind.Directory && (entry.Flags & VfsEntryFlags.ReparsePoint) == 0;
        if (isDirectory && !recursive)
        {
            await foreach (var _ in session.ListAsync(endpoint, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException(Loc.T("Folder „{0}” nie jest pusty.", entry.Name));
            }
        }

        await session.DeleteAsync(endpoint.RemotePath, isDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Server-side rename within one connection; anything else is left to copy + delete.</summary>
    public async ValueTask MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var source = RemoteEndpoint.Parse(sourcePath);
        var destination = RemoteEndpoint.Parse(destinationPath);
        if (!string.Equals(source.SessionKey, destination.SessionKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(Loc.T("Przenoszenie między serwerami odbywa się przez kopiowanie."));
        }

        var session = await GetSessionAsync(source, cancellationToken).ConfigureAwait(false);
        var entry = await session.GetEntryAsync(source, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException(Loc.T("Nie ma „{0}”.", sourcePath), sourcePath);
        await session.MoveAsync(source.RemotePath, destination.RemotePath, entry.Kind == VfsEntryKind.Directory, overwrite, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _sessions.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                await lazy.Value.Result.DisposeAsync().ConfigureAwait(false);
            }
        }

        _sessions.Clear();
    }

    private async Task<IRemoteSession> GetSessionAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken)
    {
        var key = endpoint.SessionKey;
        while (true)
        {
            var lazy = _sessions.GetOrAdd(key, _ => new Lazy<Task<IRemoteSession>>(() => ConnectAsync(endpoint)));
            try
            {
                var session = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (session.IsConnected)
                {
                    return session;
                }

                // Dropped by the server: evict and reconnect.
                if (_sessions.TryRemove(new KeyValuePair<string, Lazy<Task<IRemoteSession>>>(key, lazy)))
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _sessions.TryRemove(new KeyValuePair<string, Lazy<Task<IRemoteSession>>>(key, lazy));
                throw;
            }
        }
    }

    // Connection setup is deliberately not tied to the first caller's token: a cancelled navigation should not
    // poison the pooled session that the next navigation will reuse.
    private Task<IRemoteSession> ConnectAsync(RemoteEndpoint endpoint)
    {
        var login = endpoint.Password is not null
            ? new RemoteLogin(endpoint.UserName ?? string.Empty, endpoint.Password)
            : _logins.GetValueOrDefault(endpoint.SessionKey);

        if (login is null)
        {
            if (endpoint.Scheme == RemoteEndpoint.Ftp && endpoint.UserName is null)
            {
                login = new RemoteLogin("anonymous", "anonymous@");
            }
            else if (endpoint.Scheme == RemoteEndpoint.Sftp && RemoteLogin.DefaultKeyFiles().Count > 0)
            {
                // Like ssh: try the default keys (as the given user, else the Windows user); a refusal asks for a login.
                login = new RemoteLogin(endpoint.UserName ?? Environment.UserName);
            }
            else
            {
                return Task.FromException<IRemoteSession>(
                    new VfsAuthenticationRequiredException(endpoint.ToPath(endpoint.RemotePath), endpoint.SessionKey));
            }
        }

        if (endpoint.Scheme == RemoteEndpoint.Sftp)
        {
            return Connect(SftpSession.ConnectAsync(endpoint, login, HostKeyValidator, KeepAliveInterval, CancellationToken.None));
        }

        var credential = new NetworkCredential(login.UserName, login.Password ?? string.Empty);
        return Connect(FtpSession.ConnectAsync(endpoint, credential, CancellationToken.None));

        static async Task<IRemoteSession> Connect<T>(Task<T> connecting) where T : IRemoteSession => await connecting.ConfigureAwait(false);
    }
}
