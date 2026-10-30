using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Network;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>
/// SFTP (SSH.NET) and FTP/FTPS (FluentFTP) servers. Sessions are pooled per user/host/port and kept alive, so
/// navigating a remote tree does not reconnect on every directory. Skeleton: listing, reading, mkdir and delete.
/// </summary>
public sealed class RemoteTransferVfsPlugin : IVfsProviderPlugin, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IRemoteSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NetworkCredential> _credentials = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "builtin.remote";

    public string DisplayName => "SFTP / FTP";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.remote", "SFTP / FTP", "Serwery SFTP i FTP.", PluginPermissions.Network, PluginPermissions.FileRead, PluginPermissions.FileWrite);

    public IReadOnlyList<string> Schemes { get; } = [RemoteEndpoint.Sftp, RemoteEndpoint.Ftp];

    public VfsCapabilities Capabilities => VfsCapabilities.Read | VfsCapabilities.CreateDirectory | VfsCapabilities.Delete;

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Decides whether to trust an SSH host key. Defaults to accepting every key, which is vulnerable to
    /// man-in-the-middle attacks; the UI must replace it with a known-hosts check before production use.
    /// </summary>
    public HostKeyValidator HostKeyValidator { get; set; } = static (_, _, _) => true;

    /// <summary>Stores credentials for a realm reported by <see cref="VfsAuthenticationRequiredException.Realm"/>.</summary>
    public void SetCredentials(string realm, NetworkCredential credential) => _credentials[realm] = credential;

    public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var session = await GetSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await foreach (var entry in session.ListAsync(endpoint, cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    public ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var name = endpoint.RemotePath.TrimEnd('/');
        name = name.Length == 0 ? endpoint.Host : name[(name.LastIndexOf('/') + 1)..];
        // Kind is a best guess without a round trip; listings carry the authoritative kind.
        return ValueTask.FromResult<VfsEntry?>(new VfsEntry(name, path, VfsEntryKind.Directory));
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

    /// <remarks>Recursive deletion of non-empty remote directories is not implemented yet.</remarks>
    public async ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var endpoint = RemoteEndpoint.Parse(path);
        var session = await GetSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await session.DeleteAsync(endpoint.RemotePath, isDirectory: recursive, cancellationToken).ConfigureAwait(false);
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
        var credential = endpoint.Password is not null
            ? new NetworkCredential(endpoint.UserName, endpoint.Password)
            : _credentials.GetValueOrDefault(endpoint.SessionKey);

        if (credential is null)
        {
            if (endpoint.Scheme == RemoteEndpoint.Ftp && endpoint.UserName is null)
            {
                credential = new NetworkCredential("anonymous", "anonymous@");
            }
            else
            {
                return Task.FromException<IRemoteSession>(
                    new VfsAuthenticationRequiredException(endpoint.ToPath(endpoint.RemotePath), endpoint.SessionKey));
            }
        }

        return endpoint.Scheme == RemoteEndpoint.Sftp
            ? Connect(SftpSession.ConnectAsync(endpoint, credential, HostKeyValidator, KeepAliveInterval, CancellationToken.None))
            : Connect(FtpSession.ConnectAsync(endpoint, credential, CancellationToken.None));

        static async Task<IRemoteSession> Connect<T>(Task<T> connecting) where T : IRemoteSession => await connecting.ConfigureAwait(false);
    }
}
