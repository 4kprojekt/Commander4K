using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Mail;
using Commander4K.Core.Plugins;
using Commander4K.Core.Security;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;
using MimeKit;

namespace Commander4K.Plugins.Tests;

public sealed class ScopedMailHostTests : IAsyncLifetime
{
    private const string Subject = "Secret subject 4711";
    private const string Body = "Secret body text";

    private readonly string _dir = Directory.CreateTempSubdirectory("c4k-scopedmail-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly FakeBackend _backend = new();
    private readonly MailAccount _account = new("imap.example.org:993", "smtp.example.org:465", "anna");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private IMailHost Host(params string[] permissions) =>
        new ScopedPluginHost(new PluginManifest("test.mail", "Mail", "1.0.0", "tests", "t", permissions), new NullPluginUi(), _router, new HttpClient(), _audit, null, _backend).Mail;

    [Fact]
    public async Task Mail_requires_mail_permission()
    {
        var host = Host(PluginPermissions.FileWrite, PluginPermissions.FileRead);

        await Assert.ThrowsAsync<PluginPermissionException>(() => host.FoldersAsync(_account, Token));

        var entry = Assert.Single(_audit.Recent("test.mail"), e => e.Operation == "mail.folders");
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Equal(0, _backend.Calls);
    }

    [Fact]
    public async Task Fetch_requires_fs_write_for_target()
    {
        var host = Host(PluginPermissions.Mail);

        var ex = await Assert.ThrowsAsync<PluginPermissionException>(() => host.FetchAsync(_account, "INBOX", 1, Path.Combine(_dir, "a.eml"), Token));

        Assert.Equal(PluginPermissions.FileWrite, ex.Permission);
        Assert.Equal(0, _backend.Calls);
    }

    [Fact]
    public async Task Send_and_append_and_parse_require_fs_read()
    {
        var host = Host(PluginPermissions.Mail);
        var path = Path.Combine(_dir, "d.eml");

        await Assert.ThrowsAsync<PluginPermissionException>(() => host.SendAsync(_account, path, Token));
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.AppendAsync(_account, "Drafts", path, MailFlags.Draft, Token));
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.ParseAsync(path, Token));
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.CreateReplyAsync(path, MailReplyMode.Reply, "me@x.org", Token));
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.SaveAttachmentAsync(path, 0, Path.Combine(_dir, "t.bin"), Token));
    }

    [Fact]
    public async Task Audit_has_no_content_or_password()
    {
        var host = Host(PluginPermissions.Mail, PluginPermissions.FileRead);
        var draft = Path.Combine(_dir, "outgoing.eml");
        await File.WriteAllTextAsync(draft, $"Subject: {Subject}\r\n\r\n{Body}", Token);
        var account = _account with { User = "anna-user" };
        const string secret = "S3cr3t-Pa55-xyz";
        var secrets = new SecretStore(Path.Combine(_dir, "secrets.json"));
        secrets.Set(MailCredentials.SmtpSecretName("test.mail", account), secret, "smtp.example.org");
        var credentials = new MailCredentials(secrets, (_, _, _) => Task.FromResult<string?>(null));
        _backend.OnSend = async () => _backend.SeenPassword = await credentials.GetSmtpPasswordAsync("test.mail", account, afterRefusal: false, Token);

        var id = await host.SendAsync(account, draft, Token);

        Assert.Equal("<sent@test>", id);
        var entry = Assert.Single(_audit.Recent("test.mail"), e => e.Operation == "mail.send");
        Assert.Equal(AuditOutcome.Allowed, entry.Outcome);
        Assert.Equal(secret, _backend.SeenPassword); // the password really was in play on the host side
        Assert.Contains("anna-user", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("imap.example.org", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Body, entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Subject, entry.Detail, StringComparison.Ordinal);
        Assert.All(_audit.Recent("test.mail"), e => Assert.DoesNotContain(secret, e.ToString(), StringComparison.Ordinal));
        Assert.Equal(Body, _backend.SentBody);
    }

    [Fact]
    public async Task Compose_with_attachments_requires_fs_read_and_reads_nothing()
    {
        var host = Host(PluginPermissions.Mail, PluginPermissions.FileWrite);
        var attachment = Path.Combine(_dir, "secret.txt");
        await File.WriteAllTextAsync(attachment, "x", Token);
        var path = Path.Combine(_dir, "draft.eml");
        var draft = new MailDraft([new MailAddress(null, "b@example.org")], [], [], "s", "b", null, [], [attachment]);

        var ex = await Assert.ThrowsAsync<PluginPermissionException>(() => host.ComposeAsync(path, draft, Token));

        Assert.Equal(PluginPermissions.FileRead, ex.Permission);
        Assert.False(File.Exists(path));
        Assert.Contains(_audit.Recent("test.mail"), e => e.Operation == "mail.compose" && e.Outcome == AuditOutcome.Denied);
        Assert.DoesNotContain(_audit.Recent("test.mail"), e => e.Operation == "fs.read");
    }

    [Fact]
    public async Task Fetch_writes_through_router()
    {
        var host = Host(PluginPermissions.Mail, PluginPermissions.FileWrite);
        var target = Path.Combine(_dir, "m.eml");

        var written = await host.FetchAsync(_account, "INBOX", 7, target, Token);

        Assert.Equal(16, written);
        Assert.Equal("Subject: x\r\n\r\nhi", await File.ReadAllTextAsync(target, Token));
        Assert.Equal(["m.eml"], Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task Failed_fetch_leaves_no_partial_file()
    {
        var host = Host(PluginPermissions.Mail, PluginPermissions.FileWrite);
        _backend.FailFetch = true;

        await Assert.ThrowsAsync<IOException>(() => host.FetchAsync(_account, "INBOX", 7, Path.Combine(_dir, "m.eml"), Token));

        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Contains(_audit.Recent("test.mail"), e => e.Operation == "mail.fetch" && e.Outcome == AuditOutcome.Failed);
    }

    [Fact]
    public async Task Compose_then_parse_reply_and_save_attachment_roundtrip()
    {
        var host = Host(PluginPermissions.Mail, PluginPermissions.FileRead, PluginPermissions.FileWrite);
        var attachment = Path.Combine(_dir, "data.bin");
        await File.WriteAllBytesAsync(attachment, [1, 2, 3, 4], Token);
        var path = Path.Combine(_dir, "draft.eml");
        var draft = new MailDraft([new MailAddress("Bob", "bob@example.org")], [], [], "Hello", "Body line", null, [], [attachment], new MailAddress("Anna", "anna@example.org"));

        await host.ComposeAsync(path, draft, Token);
        var view = await host.ParseAsync(path, Token);

        Assert.Equal("Hello", view.Subject);
        Assert.Contains("Body line", view.BodyText, StringComparison.Ordinal);
        var saved = Assert.Single(view.Attachments);
        Assert.Equal("data.bin", saved.FileName);

        var target = Path.Combine(_dir, "restored.bin");
        await host.SaveAttachmentAsync(path, saved.Index, target, Token);
        var restored = await File.ReadAllBytesAsync(target, Token);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, restored);

        var reply = await host.CreateReplyAsync(path, MailReplyMode.Forward, "anna@example.org", Token);
        Assert.StartsWith("Fwd:", reply.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([path + "#0"], reply.AttachmentPaths);

        // A forward builds from the "<file>.eml#<n>" reference through the same gate.
        var forwarded = Path.Combine(_dir, "forward.eml");
        await host.ComposeAsync(forwarded, reply with { To = [new MailAddress(null, "c@example.org")] }, Token);
        Assert.Single((await host.ParseAsync(forwarded, Token)).Attachments);
    }

    [Fact]
    public async Task Append_and_move_reach_the_backend()
    {
        var host = Host(PluginPermissions.Mail, PluginPermissions.FileRead);
        var path = Path.Combine(_dir, "a.eml");
        await File.WriteAllTextAsync(path, "Subject: a\r\n\r\nb", Token);

        Assert.Equal(42u, await host.AppendAsync(_account, "Drafts", path, MailFlags.Draft | MailFlags.Seen, Token));
        await host.MoveAsync(_account, "INBOX", [1, 2, 3], "Archive", Token);

        var move = Assert.Single(_audit.Recent("test.mail"), e => e.Operation == "mail.move");
        Assert.Contains("3 uid", move.Detail, StringComparison.Ordinal);
        Assert.Equal(MailFlags.Draft | MailFlags.Seen, _backend.AppendFlags);
    }

    private sealed class FakeBackend : IMailBackend
    {
        public int Calls { get; private set; }

        public bool FailFetch { get; set; }

        public string? SentBody { get; private set; }

        public Func<Task>? OnSend { get; set; }

        public string? SeenPassword { get; set; }

        public MailFlags AppendFlags { get; private set; }

        public Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(string pluginId, MailAccount account, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<MailFolderInfo>>([]);
        }

        public Task<MailSyncResult> SyncAsync(string pluginId, MailAccount account, MailSyncRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new MailSyncResult(1, false, 0, [], [], []));
        }

        public async Task<long> FetchAsync(string pluginId, MailAccount account, string folder, uint uid, Stream destination, CancellationToken ct)
        {
            Calls++;
            var bytes = Encoding.ASCII.GetBytes("Subject: x\r\n\r\nhi");
            await destination.WriteAsync(bytes, ct);
            return FailFetch ? throw new IOException("dropped") : bytes.Length;
        }

        public Task SetFlagsAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task MoveAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task<uint?> AppendAsync(string pluginId, MailAccount account, string folder, Stream message, MailFlags flags, CancellationToken ct)
        {
            Calls++;
            AppendFlags = flags;
            return Task.FromResult<uint?>(42);
        }

        public async Task<string> SendAsync(string pluginId, MailAccount account, Stream message, CancellationToken ct)
        {
            Calls++;
            if (OnSend is not null)
            {
                await OnSend();
            }

            SentBody = (await MimeMessage.LoadAsync(message, ct)).TextBody?.Trim();
            return "<sent@test>";
        }
    }
}
