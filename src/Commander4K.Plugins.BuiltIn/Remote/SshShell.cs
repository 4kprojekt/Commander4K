using Renci.SshNet;
using Renci.SshNet.Common;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>
/// An interactive shell on an SSH server (a pty of type <c>xterm-256color</c>): raw bytes in and out, for a terminal
/// emulator. Its own connection, separate from the pooled SFTP session, signed in the same way.
/// </summary>
public sealed class SshShell : IAsyncDisposable
{
    private readonly SshClient _client;
    private readonly ShellStream _stream;

    private SshShell(SshClient client, ShellStream stream)
    {
        _client = client;
        _stream = stream;
    }

    /// <summary>A refused login becomes <see cref="VfsAuthenticationRequiredException"/>, as for SFTP sessions.</summary>
    internal static async Task<SshShell> ConnectAsync(
        RemoteEndpoint endpoint, RemoteLogin login, HostKeyValidator hostKeyValidator, TimeSpan keepAlive, int columns, int rows, CancellationToken cancellationToken)
    {
        var info = new ConnectionInfo(endpoint.Host, endpoint.Port, login.UserName, [.. SftpSession.AuthenticationMethods(login, endpoint.Host)]);
        var client = new SshClient(info) { KeepAliveInterval = keepAlive };
        client.HostKeyReceived += (_, e) => e.CanTrust = hostKeyValidator(endpoint.Host, endpoint.Port, e.FingerPrintSHA256);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var stream = client.CreateShellStream("xterm-256color", (uint)Math.Max(columns, 2), (uint)Math.Max(rows, 2), 0, 0, 64 * 1024);
            return new SshShell(client, stream);
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

    /// <summary>Blocks until the server sends something; 0 (or a closed connection) means the shell ended. Call from a dedicated thread.</summary>
    public int Read(byte[] buffer)
    {
        try
        {
            return _stream.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
            return 0;
        }
    }

    /// <summary>Keys typed by the user; quietly ignored once the shell has ended.</summary>
    public void Write(byte[] data)
    {
        try
        {
            _stream.Write(data, 0, data.Length);
            _stream.Flush();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
        }
    }

    public void Resize(int columns, int rows)
    {
        try
        {
            _stream.ChangeWindowSize((uint)Math.Max(columns, 2), (uint)Math.Max(rows, 2), 0, 0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        // Disconnecting talks to the server: never on the caller's (UI) thread.
        return new ValueTask(Task.Run(() =>
        {
            try
            {
                _stream.Dispose();
                _client.Dispose();
            }
            catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException)
            {
            }
        }));
    }
}
