using Commander4K.Core.Mail;
using Commander4K.Core.Security;
using Commander4K.PluginSdk;
using MailService = Commander4K.Core.Mail.MailService;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Commander4K.Plugins.Tests;

/// <summary>
/// Runs against a real IMAP/SMTP server when <c>C4K_TEST_IMAP</c> (e.g. <c>imaps://test:test@127.0.0.1:3993</c>) and
/// <c>C4K_TEST_SMTP</c> (e.g. <c>smtps://127.0.0.1:3465</c>) are set — docker <c>greenmail/standalone</c> with
/// <c>-Dgreenmail.setup.test.all -Dgreenmail.users=test:test@localhost</c>. Skipped otherwise. The self-signed certificate
/// is trusted through the confirmation callback; <c>imaps</c>/<c>smtps</c> mark the test ports as implicit TLS.
/// </summary>
public sealed class MailServerTests : IDisposable
{
    private const string Address = "test@localhost";

    private readonly string _dir = Directory.CreateTempSubdirectory("c4k-mailsrv-").FullName;
    private int _confirmations;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public Task Full_cycle_against_real_server() => RunCycleAsync(useCondStore: true);

    [Fact]
    public Task Sync_without_condstore_uses_known_uids() => RunCycleAsync(useCondStore: false);

    private async Task RunCycleAsync(bool useCondStore)
    {
        var imapUrl = Environment.GetEnvironmentVariable("C4K_TEST_IMAP");
        var smtpUrl = Environment.GetEnvironmentVariable("C4K_TEST_SMTP");
        Assert.SkipWhen(string.IsNullOrEmpty(imapUrl) || string.IsNullOrEmpty(smtpUrl), "C4K_TEST_IMAP / C4K_TEST_SMTP are not set");

        var imap = new Uri(imapUrl!);
        var smtp = new Uri(smtpUrl!);
        var login = imap.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(login[0]);
        var password = Uri.UnescapeDataString(login[1]);
        var implicitTls = new HashSet<int>();
        if (imap.Scheme == "imaps") implicitTls.Add(imap.Port);
        if (smtp.Scheme == "smtps") implicitTls.Add(smtp.Port);

        var credentials = new MailCredentials(new SecretStore(Path.Combine(_dir, "secrets.json")), (_, _) => Task.FromResult<string?>(password));
        var certificates = new MailCertificateStore(Path.Combine(_dir, "mail_certificates.json"), (_, _) =>
        {
            Interlocked.Increment(ref _confirmations);
            return Task.FromResult(true);
        });
        var options = new MailServiceOptions { UseCondStore = useCondStore, ImplicitTlsPorts = implicitTls };
        await using var service = new MailService(credentials, certificates, options: options);
        var account = new MailAccount($"{imap.Host}:{imap.Port}", $"{smtp.Host}:{smtp.Port}", user);
        var since = DateTimeOffset.Now.AddDays(-1);

        // Baseline: what the shared server already holds.
        var baseline = await service.SyncAsync("t", account, new MailSyncRequest("INBOX", 0, 0, 0, since, []), Token);
        var lastUid = baseline.New.Select(h => h.Uid).DefaultIfEmpty(0u).Max();

        // 1. Three messages over plain SMTP.
        var tag = Guid.NewGuid().ToString("N")[..8];
        await SendRawAsync(smtp, user, password, Enumerable.Range(1, 3).Select(i => $"{tag} message {i}"));

        // 2. Folders: INBOX with at least three messages.
        var folders = await WaitForAsync<IReadOnlyList<MailFolderInfo>>(() => service.FoldersAsync("t", account, Token), f => f.Any(x => x.Special == MailSpecialFolder.Inbox && x.Count >= baseline.New.Count + 3));
        var inbox = folders.Single(x => x.Special == MailSpecialFolder.Inbox);
        Assert.Equal("INBOX", inbox.ServerName, ignoreCase: true);
        Assert.NotEqual(0u, inbox.UidValidity);
        Assert.True(_confirmations >= 1);

        // 3. A first sync after the baseline: exactly the three new ones.
        var first = await service.SyncAsync("t", account, new MailSyncRequest("INBOX", inbox.UidValidity, lastUid, baseline.ModSeq, since, []), Token);
        Assert.False(first.UidValidityChanged);
        Assert.Equal(3, first.New.Count);
        Assert.All(first.New, h =>
        {
            Assert.StartsWith(tag, h.Subject);
            Assert.Equal(MailFileNames.For(h.Date, h.From, h.Subject, h.Uid), h.FileName);
            Assert.Equal(MailFlags.None, h.Flags & MailFlags.Seen);
        });
        var uids = first.New.Select(h => h.Uid).Order().ToList();

        // 4. Fetch writes a parseable message and does not mark it read.
        using (var raw = new MemoryStream())
        {
            var written = await service.FetchAsync("t", account, "INBOX", uids[0], raw, Token);
            Assert.Equal(raw.Length, written);
            raw.Position = 0;
            Assert.Equal($"{tag} message 1", (await MimeMessage.LoadAsync(raw, Token)).Subject);
        }

        // 5. +Seen shows up as a flag change.
        await service.SetFlagsAsync("t", account, "INBOX", [uids[0]], MailFlags.Seen, MailFlags.None, Token);
        var second = await service.SyncAsync("t", account, new MailSyncRequest("INBOX", inbox.UidValidity, uids[^1], first.ModSeq, since, uids), Token);
        Assert.Empty(second.New);
        Assert.Empty(second.Removed);
        Assert.Contains(second.FlagChanges, c => c.Uid == uids[0] && c.Flags.HasFlag(MailFlags.Seen));
        Assert.DoesNotContain(second.FlagChanges, c => c.Uid != uids[0] && c.Flags.HasFlag(MailFlags.Seen));

        // 6. Move to Archive: the UID is reported as removed from INBOX.
        await EnsureFolderAsync(imap, implicitTls.Contains(imap.Port), user, password, "Archive");
        await service.MoveAsync("t", account, "INBOX", [uids[1]], "Archive", Token);
        var third = await service.SyncAsync("t", account, new MailSyncRequest("INBOX", inbox.UidValidity, uids[^1], second.ModSeq, since, uids), Token);
        Assert.Equal([uids[1]], third.Removed);
        Assert.Empty(third.New);
        Assert.Contains((await service.FoldersAsync("t", account, Token)), f => f.ServerName == "Archive" && f.Special == MailSpecialFolder.Archive && f.Count >= 1);

        // 7. Send through the service: Message-ID back, a \Seen copy in Sent.
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(Address));
        message.To.Add(MailboxAddress.Parse(Address));
        message.Subject = $"{tag} sent";
        message.Body = new TextPart("plain") { Text = "Hello from Commander4K." };
        message.Headers.Remove(HeaderId.MessageId);
        using var draft = new MemoryStream();
        await message.WriteToAsync(draft, Token);
        draft.Position = 0;
        var messageId = await service.SendAsync("t", account, draft, Token);
        Assert.False(string.IsNullOrWhiteSpace(messageId));

        var sent = (await service.FoldersAsync("t", account, Token)).Single(f => f.Special == MailSpecialFolder.Sent);
        var sentSync = await service.SyncAsync("t", account, new MailSyncRequest(sent.ServerName, 0, 0, 0, since, []), Token);
        var copy = Assert.Single(sentSync.New, h => h.Subject == $"{tag} sent");
        Assert.True(copy.Flags.HasFlag(MailFlags.Seen));

        // Delivered to the inbox as well.
        await WaitForAsync<MailSyncResult>(
            () => service.SyncAsync("t", account, new MailSyncRequest("INBOX", inbox.UidValidity, uids[^1], 0, since, []), Token),
            r => r.New.Any(h => h.Subject == $"{tag} sent"));
    }

    [Fact]
    public async Task Send_succeeds_when_sent_copy_fails()
    {
        var imapUrl = Environment.GetEnvironmentVariable("C4K_TEST_IMAP");
        var smtpUrl = Environment.GetEnvironmentVariable("C4K_TEST_SMTP");
        Assert.SkipWhen(string.IsNullOrEmpty(imapUrl) || string.IsNullOrEmpty(smtpUrl), "C4K_TEST_IMAP / C4K_TEST_SMTP are not set");

        var imap = new Uri(imapUrl!);
        var smtp = new Uri(smtpUrl!);
        var login = imap.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(login[0]);
        var password = Uri.UnescapeDataString(login[1]);
        var credentials = new MailCredentials(new SecretStore(Path.Combine(_dir, "secrets.json")), (_, _) => Task.FromResult<string?>(password));
        var certificates = new MailCertificateStore(Path.Combine(_dir, "mail_certificates.json"), (_, _) => Task.FromResult(true));
        var warnings = new List<string>();
        var implicitTls = smtp.Scheme == "smtps" ? new HashSet<int> { smtp.Port } : new HashSet<int>();
        await using var service = new MailService(credentials, certificates, options: new MailServiceOptions { ImplicitTlsPorts = implicitTls }, warning: w =>
        {
            lock (warnings)
            {
                warnings.Add(w);
            }
        });

        // GreenMail's plain IMAP port offers no STARTTLS: the copy to Sent fails with NotSupportedException after delivery.
        var account = new MailAccount($"{imap.Host}:3143", $"{smtp.Host}:{smtp.Port}", user);
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(Address));
        message.To.Add(MailboxAddress.Parse(Address));
        message.Subject = "copy fails " + Guid.NewGuid().ToString("N")[..8];
        message.Body = new TextPart("plain") { Text = "Delivered anyway." };
        using var draft = new MemoryStream();
        await message.WriteToAsync(draft, Token);
        draft.Position = 0;

        var messageId = await service.SendAsync("t", account, draft, Token);

        Assert.Equal(message.MessageId, messageId);
        var warning = Assert.Single(warnings);
        Assert.Contains(messageId, warning);
        Assert.Contains("copy to Sent failed", warning);
        Assert.Contains(nameof(NotSupportedException), warning);
    }

    private static async Task SendRawAsync(Uri smtp, string user, string password, IEnumerable<string> subjects)
    {
        using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync(smtp.Host, smtp.Port, smtp.Scheme == "smtps" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, Token);
        await client.AuthenticateAsync(user, password, Token);
        foreach (var subject in subjects)
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse("sender@localhost"));
            message.To.Add(MailboxAddress.Parse(Address));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = subject };
            await client.SendAsync(message, Token);
        }

        await client.DisconnectAsync(true, Token);
    }

    private static async Task EnsureFolderAsync(Uri imap, bool implicitTls, string user, string password, string name)
    {
        using var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync(imap.Host, imap.Port, implicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, Token);
        await client.AuthenticateAsync(user, password, Token);
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var existing = await root.GetSubfoldersAsync(false, Token);
        if (!existing.Any(f => f.Name == name))
        {
            await root.CreateAsync(name, true, Token);
        }

        await client.DisconnectAsync(true, Token);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTime.UtcNow > until)
            {
                Assert.True(done(value), "condition not reached within 15 s");
                return value;
            }

            await Task.Delay(250, Token);
        }
    }
}
