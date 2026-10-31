using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Commander4K.Core.Mail;
using Commander4K.Core.Security;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class MailServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("c4k-mail-").FullName;
    private int _prompts;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SecretStore Secrets() => new(Path.Combine(_dir, "secrets.json"));

    private MailCredentials Credentials(SecretStore secrets, string? answer = "pw") => new(secrets, (_, _) =>
    {
        Interlocked.Increment(ref _prompts);
        return Task.FromResult(answer);
    });

    private MailCertificateStore Certificates() => new(Path.Combine(_dir, "mail_certificates.json"), (_, _) => Task.FromResult(false));

    [Fact]
    public async Task Background_call_without_password_throws_without_prompt()
    {
        await using var service = new MailService(Credentials(Secrets()), Certificates());
        var account = new MailAccount("imap.example.invalid:993", "smtp.example.invalid:465", "jan", Interactive: false);

        var ex = await Assert.ThrowsAsync<MailAuthenticationRequiredException>(() => service.FoldersAsync("a", account, Token));

        Assert.Equal("jan@imap.example.invalid", ex.Realm);
        Assert.Equal(0, _prompts);
    }

    [Fact]
    public async Task Secrets_are_bound_per_plugin_and_host()
    {
        var secrets = Secrets();
        var credentials = Credentials(secrets);
        var account = new MailAccount("imap.example.com:993", "smtp.example.com:465", "jan", Interactive: false);
        secrets.Set(MailCredentials.ImapSecretName("a", account), "secret", "imap.example.com");

        Assert.Equal("secret", await credentials.GetImapPasswordAsync("a", account, afterRefusal: false, Token));
        await Assert.ThrowsAsync<MailAuthenticationRequiredException>(() => credentials.GetImapPasswordAsync("b", account, false, Token));
        await Assert.ThrowsAsync<MailAuthenticationRequiredException>(
            () => credentials.GetImapPasswordAsync("a", account with { Imap = "other.example.com:993" }, false, Token));
        // A refusal never re-uses the stored password in the background.
        await Assert.ThrowsAsync<MailAuthenticationRequiredException>(() => credentials.GetImapPasswordAsync("a", account, afterRefusal: true, Token));
        Assert.Equal(0, _prompts);
    }

    [Fact]
    public async Task Interactive_login_stores_both_secrets()
    {
        var secrets = Secrets();
        var credentials = Credentials(secrets);
        var account = new MailAccount("imap.example.com:993", "smtp.example.com:587", "jan@example.com");

        Assert.Equal("pw", await credentials.GetImapPasswordAsync("a", account, afterRefusal: false, Token));

        Assert.Equal("mail:a:jan@example.com@imap.example.com", MailCredentials.ImapSecretName("a", account));
        Assert.Equal("mail-smtp:a:jan@example.com@smtp.example.com", MailCredentials.SmtpSecretName("a", account));
        Assert.Equal("pw", secrets.Get(MailCredentials.ImapSecretName("a", account), "imap.example.com"));
        Assert.Equal("pw", secrets.Get(MailCredentials.SmtpSecretName("a", account), "smtp.example.com"));
        Assert.Null(secrets.Get(MailCredentials.ImapSecretName("a", account), "smtp.example.com"));

        // Stored now: no second prompt.
        Assert.Equal("pw", await credentials.GetSmtpPasswordAsync("a", account, afterRefusal: false, Token));
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Cancelled_prompt_means_login_required()
    {
        var credentials = Credentials(Secrets(), answer: null);
        var account = new MailAccount("imap.example.com:993", null, "jan");

        await Assert.ThrowsAsync<MailAuthenticationRequiredException>(() => credentials.GetImapPasswordAsync("a", account, false, Token));
        Assert.Equal(1, _prompts);
    }

    [Fact]
    public async Task Certificate_trust_is_remembered()
    {
        var asked = new List<string>();
        var path = Path.Combine(_dir, "mail_certificates.json");
        var store = new MailCertificateStore(path, (hostPort, fingerprint) =>
        {
            lock (asked)
            {
                asked.Add($"{hostPort} {fingerprint}");
            }

            return Task.FromResult(true);
        });

        Assert.False(await store.ValidateAsync("Mail.Example.com", 993, "AA11", interactive: false));
        Assert.Empty(asked);
        Assert.True(await store.ValidateAsync("Mail.Example.com", 993, "AA11", interactive: true));
        Assert.True(await store.ValidateAsync("mail.example.com", 993, "AA11", interactive: true));
        Assert.True(await store.ValidateAsync("mail.example.com", 993, "AA11", interactive: false));
        Assert.Equal(["Mail.Example.com:993 AA11"], asked);

        // Persisted, and a different certificate asks again.
        var reloaded = new MailCertificateStore(path, (_, _) => Task.FromResult(false));
        Assert.True(reloaded.IsTrusted("mail.example.com", 993, "AA11"));
        Assert.False(reloaded.IsTrusted("mail.example.com", 143, "AA11"));
        Assert.True(await store.ValidateAsync("mail.example.com", 993, "BB22", interactive: true));
        Assert.Equal(2, asked.Count);
        Assert.False(await reloaded.ValidateAsync("mail.example.com", 993, "CC33", interactive: true));
        Assert.False(reloaded.IsTrusted("mail.example.com", 993, "CC33"));
    }

    [Fact]
    public async Task Operation_times_out_on_silent_server()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await listener.AcceptTcpClientAsync(Token));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Listener stopped.
            }
        }, Token);

        var secrets = Secrets();
        var account = new MailAccount($"127.0.0.1:{port}", null, "jan", Interactive: false);
        secrets.Set(MailCredentials.ImapSecretName("a", account), "pw", "127.0.0.1");
        await using var service = new MailService(Credentials(secrets), Certificates(), options: new MailServiceOptions { OperationTimeout = TimeSpan.FromSeconds(1) });

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => service.FoldersAsync("a", account, Token));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.False(Token.IsCancellationRequested);
        Assert.Equal(0, _prompts);
        listener.Stop();
        await acceptLoop;
        foreach (var client in accepted)
        {
            client.Dispose();
        }
    }
}
