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

    private MailCredentials Credentials(SecretStore secrets, string? answer = "pw") => new(secrets, (_, _, _) =>
    {
        Interlocked.Increment(ref _prompts);
        return Task.FromResult(answer);
    });

    private readonly List<bool> _refusedFlags = [];

    /// <summary>Answers the prompts in order; <c>null</c> = the user cancelled.</summary>
    private MailCredentials Scripted(SecretStore secrets, params string?[] answers)
    {
        var queue = new Queue<string?>(answers);
        return new MailCredentials(secrets, (_, _, refused) =>
        {
            Interlocked.Increment(ref _prompts);
            lock (_refusedFlags)
            {
                _refusedFlags.Add(refused);
            }

            return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : null);
        });
    }

    /// <summary>The fake server's self-signed certificate is confirmed; its port counts as implicit TLS.</summary>
    private MailService Service(MailCredentials credentials, FakeImapServer server) => new(
        credentials,
        new MailCertificateStore(Path.Combine(_dir, "mail_certificates.json"), (_, _) => Task.FromResult(true)),
        options: new MailServiceOptions { OperationTimeout = TimeSpan.FromSeconds(10), ImplicitTlsPorts = new HashSet<int> { server.Port } });

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
        await using var server = await FakeImapServer.StartAsync(acceptedPassword: "pw");
        var secrets = Secrets();
        var credentials = Scripted(secrets, "pw", "pw");
        var account = new MailAccount($"127.0.0.1:{server.Port}", "smtp.example.com:587", "jan@example.com");
        Assert.Equal("mail:a:jan@example.com@127.0.0.1", MailCredentials.ImapSecretName("a", account));
        Assert.Equal("mail-smtp:a:jan@example.com@smtp.example.com", MailCredentials.SmtpSecretName("a", account));

        // Typed but not yet accepted by a server: nothing stored.
        Assert.Equal("pw", await credentials.GetImapPasswordAsync("a", account, afterRefusal: false, Token));
        Assert.False(secrets.Exists(MailCredentials.ImapSecretName("a", account)));
        Assert.False(secrets.Exists(MailCredentials.SmtpSecretName("a", account)));

        await using var service = Service(credentials, server);
        var folders = await service.FoldersAsync("a", account, Token);

        Assert.Contains(folders, f => f.Special == MailSpecialFolder.Inbox);
        Assert.Equal("pw", secrets.Get(MailCredentials.ImapSecretName("a", account), "127.0.0.1"));
        Assert.Equal("pw", secrets.Get(MailCredentials.SmtpSecretName("a", account), "smtp.example.com"));
        Assert.Null(secrets.Get(MailCredentials.ImapSecretName("a", account), "smtp.example.com"));

        // Stored now: no further prompt for either server.
        Assert.Equal("pw", await credentials.GetSmtpPasswordAsync("a", account, afterRefusal: false, Token));
        Assert.Equal(2, _prompts);
    }

    [Fact]
    public async Task Refused_or_cancelled_login_stores_nothing()
    {
        await using var server = await FakeImapServer.StartAsync(acceptedPassword: "right");
        var secrets = Secrets();
        var account = new MailAccount($"127.0.0.1:{server.Port}", "smtp.example.com:587", "jan");

        // A typo, then the re-prompt is cancelled.
        await using (var service = Service(Scripted(secrets, "typo", null), server))
        {
            await Assert.ThrowsAsync<MailAuthenticationRequiredException>(() => service.FoldersAsync("a", account, Token));
        }

        Assert.Equal(2, _prompts);
        Assert.False(secrets.Exists(MailCredentials.ImapSecretName("a", account)));
        Assert.False(secrets.Exists(MailCredentials.SmtpSecretName("a", account)));

        // Three typos: refused every time up to the limit, still nothing stored, and a background call afterwards never prompts.
        await using (var service = Service(Scripted(secrets, "typo", "typo2", "typo3"), server))
        {
            await Assert.ThrowsAsync<MailAuthenticationRequiredException>(() => service.FoldersAsync("a", account, Token));
            Assert.False(secrets.Exists(MailCredentials.ImapSecretName("a", account)));
            await Assert.ThrowsAsync<MailAuthenticationRequiredException>(() => service.FoldersAsync("a", account with { Interactive = false }, Token));
        }

        Assert.Equal(2 + MailService.LoginAttempts, _prompts);
        Assert.Equal(4, server.RefusedLogins); // typo, then typo, typo2, typo3; the background call never reached the server.
        Assert.Equal([false, true, false, true, true], _refusedFlags); // each re-prompt says the password was refused

        // A typo, then the right one: only the accepted password is stored.
        await using (var service = Service(Scripted(secrets, "typo", "right"), server))
        {
            await service.FoldersAsync("a", account, Token);
        }

        Assert.Equal("right", secrets.Get(MailCredentials.ImapSecretName("a", account), "127.0.0.1"));
        Assert.Equal("right", secrets.Get(MailCredentials.SmtpSecretName("a", account), "smtp.example.com"));
    }

    [Fact]
    public async Task Imap_login_keeps_a_different_smtp_secret()
    {
        await using var server = await FakeImapServer.StartAsync(acceptedPassword: "imap-pw");
        var secrets = Secrets();
        var account = new MailAccount($"127.0.0.1:{server.Port}", "smtp.example.com:587", "jan");
        secrets.Set(MailCredentials.SmtpSecretName("a", account), "smtp-pw", "smtp.example.com");

        await using (var service = Service(Scripted(secrets, "imap-pw"), server))
        {
            await service.FoldersAsync("a", account, Token);
        }

        Assert.Equal("imap-pw", secrets.Get(MailCredentials.ImapSecretName("a", account), "127.0.0.1"));
        Assert.Equal("smtp-pw", secrets.Get(MailCredentials.SmtpSecretName("a", account), "smtp.example.com"));
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
