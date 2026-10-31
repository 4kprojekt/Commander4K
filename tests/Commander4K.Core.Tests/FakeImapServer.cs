using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Commander4K.Core.Tests;

/// <summary>
/// A minimal IMAP server over implicit TLS (self-signed certificate) on loopback: enough for MailKit to log in and list
/// INBOX. LOGIN succeeds only with <c>acceptedPassword</c>; any other password gets <c>NO [AUTHENTICATIONFAILED]</c>.
/// </summary>
internal sealed class FakeImapServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate = CreateCertificate();
    private readonly string _acceptedPassword;
    private Task _loop = Task.CompletedTask;
    private int _refused;

    private FakeImapServer(string acceptedPassword) => _acceptedPassword = acceptedPassword;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int RefusedLogins => Volatile.Read(ref _refused);

    public static Task<FakeImapServer> StartAsync(string acceptedPassword)
    {
        var server = new FakeImapServer(acceptedPassword);
        server._listener.Start();
        server._loop = server.AcceptLoopAsync();
        return Task.FromResult(server);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _certificate.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        var sessions = new List<Task>();
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                sessions.Add(ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }

        await Task.WhenAll(sessions);
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var ssl = new SslStream(client.GetStream()))
            {
                await ssl.AuthenticateAsServerAsync(_certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
                using var reader = new StreamReader(ssl, Encoding.ASCII);
                await using var writer = new StreamWriter(ssl, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("* OK [CAPABILITY IMAP4rev1] fake ready");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var parts = line.Split(' ');
                    var tag = parts[0];
                    var command = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
                    switch (command)
                    {
                        case "CAPABILITY":
                            await writer.WriteLineAsync("* CAPABILITY IMAP4rev1");
                            await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
                            break;
                        case "LOGIN":
                            if (parts.Length >= 4 && parts[^1].Trim('"') == _acceptedPassword)
                            {
                                await writer.WriteLineAsync($"{tag} OK [CAPABILITY IMAP4rev1] LOGIN completed");
                            }
                            else
                            {
                                Interlocked.Increment(ref _refused);
                                await writer.WriteLineAsync($"{tag} NO [AUTHENTICATIONFAILED] invalid credentials");
                            }

                            break;
                        case "LIST":
                            if (line.EndsWith("\"\" \"\"", StringComparison.Ordinal))
                            {
                                await writer.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"");
                            }
                            else
                            {
                                await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" INBOX");
                            }

                            await writer.WriteLineAsync($"{tag} OK LIST completed");
                            break;
                        case "STATUS":
                            await writer.WriteLineAsync("* STATUS INBOX (MESSAGES 0 UNSEEN 0 UIDVALIDITY 1)");
                            await writer.WriteLineAsync($"{tag} OK STATUS completed");
                            break;
                        case "LOGOUT":
                            await writer.WriteLineAsync("* BYE logging out");
                            await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                            return;
                        default:
                            await writer.WriteLineAsync($"{tag} OK completed");
                            break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException
            or AuthenticationException)
        {
            // The client went away.
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SslStream on Windows needs a key it can persist: reload from PFX.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}
