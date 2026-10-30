using System.Net;
using System.Runtime.CompilerServices;
using Commander4K.PluginSdk;
using FluentFTP;
using Renci.SshNet;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>One pooled, authenticated connection to a remote server.</summary>
internal interface IRemoteSession : IAsyncDisposable
{
    bool IsConnected { get; }

    IAsyncEnumerable<VfsEntry> ListAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken);

    Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken);

    Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken);
}

/// <param name="HostKeyFingerprint">SHA-256 fingerprint presented by the server.</param>
public delegate bool HostKeyValidator(string host, int port, string HostKeyFingerprint);

internal sealed class SftpSession(SftpClient client) : IRemoteSession
{
    public static async Task<SftpSession> ConnectAsync(
        RemoteEndpoint endpoint,
        NetworkCredential credential,
        HostKeyValidator hostKeyValidator,
        TimeSpan keepAlive,
        CancellationToken cancellationToken)
    {
        var info = new ConnectionInfo(endpoint.Host, endpoint.Port, credential.UserName,
            new PasswordAuthenticationMethod(credential.UserName, credential.Password));
        var client = new SftpClient(info) { KeepAliveInterval = keepAlive };
        client.HostKeyReceived += (_, e) => e.CanTrust = hostKeyValidator(endpoint.Host, endpoint.Port, e.FingerPrintSHA256);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new SftpSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public bool IsConnected => client.IsConnected;

    public async IAsyncEnumerable<VfsEntry> ListAsync(RemoteEndpoint endpoint, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var file in client.ListDirectoryAsync(endpoint.RemotePath, cancellationToken).ConfigureAwait(false))
        {
            if (file.Name is "." or "..")
            {
                continue;
            }

            yield return new VfsEntry(
                file.Name,
                endpoint.ToPath(file.FullName),
                file.IsDirectory ? VfsEntryKind.Directory : VfsEntryKind.File,
                file.IsDirectory ? -1 : file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                (file.IsSymbolicLink ? VfsEntryFlags.ReparsePoint : VfsEntryFlags.None)
                    | (file.Name.StartsWith('.') ? VfsEntryFlags.Hidden : VfsEntryFlags.None));
        }
    }

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken) =>
        await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);

    public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken) =>
        client.CreateDirectoryAsync(remotePath, cancellationToken);

    public Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken) =>
        isDirectory
            ? client.DeleteDirectoryAsync(remotePath, cancellationToken)
            : client.DeleteFileAsync(remotePath, cancellationToken);

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>FTP control connections handle one command at a time, so operations are serialised per session.</summary>
internal sealed class FtpSession(AsyncFtpClient client) : IRemoteSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static async Task<FtpSession> ConnectAsync(RemoteEndpoint endpoint, NetworkCredential credential, CancellationToken cancellationToken)
    {
        var client = new AsyncFtpClient(endpoint.Host, credential.UserName, credential.Password, endpoint.Port);
        client.Config.EncryptionMode = FtpEncryptionMode.Auto;
        client.Config.SocketKeepAlive = true;
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return new FtpSession(client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool IsConnected => client.IsConnected;

    public async IAsyncEnumerable<VfsEntry> ListAsync(RemoteEndpoint endpoint, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FtpListItem[] items;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            items = await client.GetListing(endpoint.RemotePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var item in items)
        {
            var isDirectory = item.Type == FtpObjectType.Directory;
            yield return new VfsEntry(
                item.Name,
                endpoint.ToPath(item.FullName),
                isDirectory ? VfsEntryKind.Directory : VfsEntryKind.File,
                isDirectory ? -1 : item.Size,
                item.Modified == DateTime.MinValue ? default : new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc)),
                item.Type == FtpObjectType.Link ? VfsEntryFlags.ReparsePoint : VfsEntryFlags.None);
        }
    }

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken)
    {
        // The data stream holds the control connection until disposed; release the gate only then.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await client.OpenRead(remotePath, FtpDataType.Binary, 0, true, cancellationToken).ConfigureAwait(false);
            return new GateReleasingStream(stream, _gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.CreateDirectory(remotePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isDirectory)
            {
                await client.DeleteDirectory(remotePath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFile(remotePath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private sealed class GateReleasingStream(Stream inner, SemaphoreSlim gate) : Stream
    {
        private int _released;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                Release();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            Release();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
