using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using FluentFTP;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>One pooled, authenticated connection to a remote server.</summary>
internal interface IRemoteSession : IAsyncDisposable
{
    bool IsConnected { get; }

    IAsyncEnumerable<VfsEntry> ListAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken);

    /// <summary>Kind, size and date of one path, or <c>null</c> when it does not exist.</summary>
    Task<VfsEntry?> GetEntryAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>The upload completes when the returned stream is disposed.</summary>
    Task<Stream> OpenWriteAsync(string remotePath, bool overwrite, CancellationToken cancellationToken);

    Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken);

    /// <summary>Deletes a file, or a directory with everything in it.</summary>
    Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken);

    Task MoveAsync(string sourcePath, string destinationPath, bool isDirectory, bool overwrite, CancellationToken cancellationToken);
}

/// <param name="HostKeyFingerprint">SHA-256 fingerprint presented by the server.</param>
public delegate bool HostKeyValidator(string host, int port, string HostKeyFingerprint);

internal sealed class SftpSession(SftpClient client) : IRemoteSession
{
    /// <summary>
    /// Connects with every method the login allows — private key(s), password, and keyboard-interactive answered with
    /// the password — so the server can pick the one it accepts. A refused login becomes
    /// <see cref="VfsAuthenticationRequiredException"/> (the app then asks for credentials).
    /// </summary>
    public static async Task<SftpSession> ConnectAsync(
        RemoteEndpoint endpoint,
        RemoteLogin login,
        HostKeyValidator hostKeyValidator,
        TimeSpan keepAlive,
        CancellationToken cancellationToken)
    {
        var info = new ConnectionInfo(endpoint.Host, endpoint.Port, login.UserName, [.. AuthenticationMethods(login)]);
        var client = new SftpClient(info) { KeepAliveInterval = keepAlive };
        client.HostKeyReceived += (_, e) => e.CanTrust = hostKeyValidator(endpoint.Host, endpoint.Port, e.FingerPrintSHA256);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new SftpSession(client);
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new VfsAuthenticationRequiredException(endpoint.ToPath(endpoint.RemotePath), endpoint.SessionKey, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static IEnumerable<AuthenticationMethod> AuthenticationMethods(RemoteLogin login)
    {
        // An explicit key file, or else the default ~/.ssh keys when there is no password either. Keys that need a
        // passphrase we do not have are skipped instead of failing the whole connection.
        var keyFiles = login.PrivateKeyFile is { Length: > 0 } file ? [file]
            : login.Password is null ? RemoteLogin.DefaultKeyFiles() : [];
        var keys = new List<IPrivateKeySource>();
        foreach (var path in keyFiles)
        {
            try
            {
                keys.Add(string.IsNullOrEmpty(login.Passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, login.Passphrase));
            }
            catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (login.PrivateKeyFile is not null)
                {
                    throw new IOException($"The key '{path}' cannot be read: {ex.Message}", ex);
                }
            }
        }

        if (keys.Count > 0)
        {
            yield return new PrivateKeyAuthenticationMethod(login.UserName, [.. keys]);
        }

        if (login.Password is { } password)
        {
            yield return new PasswordAuthenticationMethod(login.UserName, password);
            var interactive = new KeyboardInteractiveAuthenticationMethod(login.UserName);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            yield return interactive;
        }

        if (keys.Count == 0 && login.Password is null)
        {
            yield return new NoneAuthenticationMethod(login.UserName); // Lets the server reject it, which asks the user.
        }
    }

    public bool IsConnected => client.IsConnected;

    public async IAsyncEnumerable<VfsEntry> ListAsync(RemoteEndpoint endpoint, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var file in client.ListDirectoryAsync(endpoint.RemotePath, cancellationToken).ConfigureAwait(false))
        {
            if (file.Name is not ("." or ".."))
            {
                yield return ToEntry(endpoint, file);
            }
        }
    }

    public async Task<VfsEntry?> GetEntryAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            return ToEntry(endpoint, await client.GetAsync(endpoint.RemotePath, cancellationToken).ConfigureAwait(false));
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken) =>
        await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);

    public async Task<Stream> OpenWriteAsync(string remotePath, bool overwrite, CancellationToken cancellationToken) =>
        await client.OpenAsync(remotePath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, cancellationToken).ConfigureAwait(false);

    public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken) =>
        client.CreateDirectoryAsync(remotePath, cancellationToken);

    /// <summary>SFTP only removes empty directories: delete files on the way down, directories deepest first.</summary>
    public async Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken)
    {
        if (!isDirectory)
        {
            await client.DeleteFileAsync(remotePath, cancellationToken).ConfigureAwait(false);
            return;
        }

        var directories = new List<string>();
        var pending = new Stack<string>([remotePath]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            directories.Add(directory);
            await foreach (var item in client.ListDirectoryAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                if (item.Name is "." or "..")
                {
                    continue;
                }

                if (item.IsDirectory && !item.IsSymbolicLink)
                {
                    pending.Push(item.FullName);
                }
                else
                {
                    await client.DeleteFileAsync(item.FullName, cancellationToken).ConfigureAwait(false); // Links are removed, not followed.
                }
            }
        }

        for (var i = directories.Count - 1; i >= 0; i--)
        {
            await client.DeleteDirectoryAsync(directories[i], cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, bool isDirectory, bool overwrite, CancellationToken cancellationToken)
    {
        if (await client.ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false))
        {
            if (!overwrite || isDirectory)
            {
                throw new IOException(Loc.T("„{0}” już istnieje.", destinationPath));
            }

            await client.DeleteFileAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        }

        await client.RenameFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static VfsEntry ToEntry(RemoteEndpoint endpoint, ISftpFile file)
    {
        var name = file.FullName.TrimEnd('/') is { Length: > 0 } full ? full[(full.LastIndexOf('/') + 1)..] : endpoint.Host;
        return new VfsEntry(
            name,
            endpoint.ToPath(file.FullName),
            file.IsDirectory ? VfsEntryKind.Directory : VfsEntryKind.File,
            file.IsDirectory ? -1 : file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            (file.IsSymbolicLink ? VfsEntryFlags.ReparsePoint : VfsEntryFlags.None)
                | (name.StartsWith('.') ? VfsEntryFlags.Hidden : VfsEntryFlags.None));
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
        var items = await Gated(() => client.GetListing(endpoint.RemotePath, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            yield return ToEntry(endpoint, item);
        }
    }

    /// <summary>MLST when the server has it; otherwise existence checks plus SIZE/MDTM.</summary>
    public Task<VfsEntry?> GetEntryAsync(RemoteEndpoint endpoint, CancellationToken cancellationToken) =>
        Gated(async () =>
        {
            var path = endpoint.RemotePath;
            if (path.TrimEnd('/').Length == 0)
            {
                return new VfsEntry(endpoint.Host, endpoint.ToPath("/"), VfsEntryKind.Directory);
            }

            if (await client.GetObjectInfo(path, true, cancellationToken).ConfigureAwait(false) is { } info)
            {
                return ToEntry(endpoint, info);
            }

            var name = path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];
            if (await client.DirectoryExists(path, cancellationToken).ConfigureAwait(false))
            {
                return new VfsEntry(name, endpoint.ToPath(path), VfsEntryKind.Directory);
            }

            if (!await client.FileExists(path, cancellationToken).ConfigureAwait(false))
            {
                return (VfsEntry?)null;
            }

            var size = await client.GetFileSize(path, -1, cancellationToken).ConfigureAwait(false);
            var modified = await client.GetModifiedTime(path, cancellationToken).ConfigureAwait(false);
            return new VfsEntry(name, endpoint.ToPath(path), VfsEntryKind.File, size,
                modified == DateTime.MinValue ? default : new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)));
        }, cancellationToken);

    /// <summary>
    /// Transfers use FluentFTP's high-level <c>DownloadStream</c>/<c>UploadStream</c>, which own the data-connection
    /// reply handling (hand-rolled OpenRead/OpenWrite + GetReply desynchronised the control connection after other
    /// commands and reported uploads complete too early). A pipe connects them to the caller's stream; the session
    /// stays locked until the transfer ends.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pipe = new Pipe(TransferPipe);
        var abort = new CancellationTokenSource();
        var transfer = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                await using var sink = pipe.Writer.AsStream(leaveOpen: true);
                if (!await client.DownloadStream(sink, remotePath, 0, null, abort.Token, 0).ConfigureAwait(false))
                {
                    throw new IOException(Loc.T("Nie udało się pobrać „{0}”.", remotePath));
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(error).ConfigureAwait(false);
                _gate.Release();
            }
        }, CancellationToken.None);
        return new PipedTransferStream(pipe.Reader.AsStream(), transfer, abort, writable: false);
    }

    public async Task<Stream> OpenWriteAsync(string remotePath, bool overwrite, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!overwrite && await client.FileExists(remotePath, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException(Loc.T("„{0}” już istnieje.", remotePath));
            }
        }
        catch
        {
            _gate.Release();
            throw;
        }

        var pipe = new Pipe(TransferPipe);
        var abort = new CancellationTokenSource();
        var transfer = Task.Run(async () =>
        {
            try
            {
                await using var source = pipe.Reader.AsStream(leaveOpen: true);
                var status = await client.UploadStream(source, remotePath, FtpRemoteExists.Overwrite, false, null, abort.Token).ConfigureAwait(false);
                if (status != FtpStatus.Success)
                {
                    throw new IOException(Loc.T("Serwer FTP nie przyjął „{0}” ({1}).", remotePath, status));
                }

                await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await pipe.Reader.CompleteAsync(ex).ConfigureAwait(false); // Unblocks the writer.
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }, CancellationToken.None);
        return new PipedTransferStream(pipe.Writer.AsStream(), transfer, abort, writable: true);
    }

    private static readonly PipeOptions TransferPipe = new(pauseWriterThreshold: 4 * 1024 * 1024, resumeWriterThreshold: 2 * 1024 * 1024);

    /// <remarks>FluentFTP reports some refusals (no permission outside the home folder) as <c>false</c>, not an exception.</remarks>
    public Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken) =>
        Gated(async () =>
        {
            if (!await client.CreateDirectory(remotePath, cancellationToken).ConfigureAwait(false)
                && !await client.DirectoryExists(remotePath, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException(Loc.T("Serwer FTP nie utworzył folderu „{0}” (brak uprawnień?).", remotePath));
            }
        }, cancellationToken);

    /// <remarks>FluentFTP's DeleteDirectory removes the contents recursively.</remarks>
    public Task DeleteAsync(string remotePath, bool isDirectory, CancellationToken cancellationToken) =>
        Gated(() => isDirectory ? client.DeleteDirectory(remotePath, cancellationToken) : client.DeleteFile(remotePath, cancellationToken), cancellationToken);

    public Task MoveAsync(string sourcePath, string destinationPath, bool isDirectory, bool overwrite, CancellationToken cancellationToken) =>
        Gated(async () =>
        {
            var policy = overwrite && !isDirectory ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip;
            var moved = isDirectory
                ? await client.MoveDirectory(sourcePath, destinationPath, policy, cancellationToken).ConfigureAwait(false)
                : await client.MoveFile(sourcePath, destinationPath, policy, cancellationToken).ConfigureAwait(false);
            if (!moved)
            {
                throw new IOException(Loc.T("„{0}” już istnieje.", destinationPath));
            }
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private static VfsEntry ToEntry(RemoteEndpoint endpoint, FtpListItem item)
    {
        var isDirectory = item.Type == FtpObjectType.Directory;
        return new VfsEntry(
            item.Name,
            endpoint.ToPath(item.FullName),
            isDirectory ? VfsEntryKind.Directory : VfsEntryKind.File,
            isDirectory ? -1 : item.Size,
            item.Modified == DateTime.MinValue ? default : new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc)),
            (item.Type == FtpObjectType.Link ? VfsEntryFlags.ReparsePoint : VfsEntryFlags.None)
                | (item.Name.StartsWith('.') ? VfsEntryFlags.Hidden : VfsEntryFlags.None));
    }

    private async Task<T> Gated<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task Gated(Func<Task> action, CancellationToken cancellationToken) =>
        Gated(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// The caller's end of a transfer pipe. Disposing an upload stream finishes the data and waits for the server to
    /// confirm (errors surface here); disposing a download early aborts it.
    /// </summary>
    private sealed class PipedTransferStream(Stream inner, Task transfer, CancellationTokenSource abort, bool writable) : Stream
    {
        private int _closed;

        public override bool CanRead => !writable && inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => writable;

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

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private async ValueTask CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            if (writable)
            {
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false); // Completes the pipe: end of the uploaded data.
                }
                catch (Exception) when (transfer.IsFaulted)
                {
                    // The upload already failed; its exception is the meaningful one.
                }

                await transfer.ConfigureAwait(false);
            }
            else
            {
                if (!transfer.IsCompleted)
                {
                    await abort.CancelAsync().ConfigureAwait(false);
                }

                await inner.DisposeAsync().ConfigureAwait(false);
                await transfer.ConfigureAwait(false); // Never throws: download errors go to the reader.
            }

            abort.Dispose();
        }
    }
}
