using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Commander4K.Core.Execution;
using Commander4K.Core.Mail;
using Commander4K.Core.Plugins;
using Commander4K.Core.Security;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MailService = Commander4K.Core.Mail.MailService;

namespace Commander4K.Plugins.Tests;

/// <summary>
/// The official mail plugin (<c>plugins-catalog/mail-powershell</c>) on the real PowerShell runtime: background sync
/// from IMAP into <c>.eml</c> folders. Offline tests use a fake mail backend; server tests need <c>C4K_TEST_IMAP</c> /
/// <c>C4K_TEST_SMTP</c> (GreenMail, see <see cref="MailServerTests"/>) and use a fresh mailbox user per test.
/// </summary>
public sealed class MailPluginTests : IAsyncLifetime
{
    private const string PluginId = "official.mail";
    private const string Unread = "● ";

    private readonly string _dir = Directory.CreateTempSubdirectory("c4k-mailplugin-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly RecordingUi _ui = new();
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly HttpClient _http = new();
    private readonly List<IAsyncDisposable> _owned = [];
    private readonly PluginStorage _storage;
    private IReadOnlyList<RuntimeInfo> _runtimes = [];

    public MailPluginTests() => _storage = new PluginStorage(Path.Combine(_dir, "plugin-data"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PluginFolder => Path.Combine(RepositoryRoot(), "plugins-catalog", "mail-powershell");

    public async ValueTask InitializeAsync() => _runtimes = await RuntimeCatalog.DetectAsync(Token);

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _owned.AsEnumerable().Reverse())
        {
            await item.DisposeAsync();
        }

        await _audit.DisposeAsync();
        _http.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- offline

    [Fact]
    public async Task Auth_failure_pauses_account_until_interactive_login()
    {
        var backend = new CountingBackend { ThrowAuth = true };
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);

        Assert.False(await plugin.TickAsync(Token));

        var state = ReadState(account);
        Assert.True(state["authRequired"]!.GetValue<bool>());
        Assert.Equal(1, backend.Calls);
        Assert.False(backend.LastAccount!.Interactive);

        backend.Calls = 0;
        for (var i = 0; i < 3; i++)
        {
            await plugin.TickAsync(Token);
        }

        Assert.Equal(0, backend.Calls);

        await AccountPreview(plugin).InvokeActionAsync(
            new PreviewActionRequest(Entry(account), null, "check", new Dictionary<string, string>(), null), Host(plugin), Token);

        Assert.Equal(1, backend.Calls);
        Assert.True(backend.LastAccount!.Interactive);
    }

    [Fact]
    public async Task Lock_held_by_other_machine_skips_sync()
    {
        var backend = new CountingBackend();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        WriteLock(account, "OTHER", DateTimeOffset.UtcNow.AddMinutes(-1));
        await RenderAsync(plugin, account);

        await plugin.TickAsync(Token);
        Assert.Equal(0, backend.Calls);

        WriteLock(account, "OTHER", DateTimeOffset.UtcNow.AddMinutes(-11));
        await plugin.TickAsync(Token);

        Assert.True(backend.Calls > 0);
        using var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(account, ".mail", "lock")));
        Assert.Equal(Environment.MachineName, written.RootElement.GetProperty("machine").GetString(), ignoreCase: true);
        Assert.True(new DirectoryInfo(Path.Combine(account, ".mail")).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.Equal("Odebrane", ReadState(account)["folders"]!["INBOX"]!["local"]!.GetValue<string>());
        Assert.True(Directory.Exists(Path.Combine(account, "Odebrane")));
    }

    [Fact]
    public async Task Folders_with_the_same_local_name_get_distinct_stable_folders()
    {
        var backend = new CountingBackend
        {
            Folders =
            [
                new MailFolderInfo("INBOX", "Odebrane", '/', MailSpecialFolder.Inbox, 1, 0, 0),
                new MailFolderInfo("Sent", "Wysłane", '/', MailSpecialFolder.Sent, 2, 0, 0),
                new MailFolderInfo("Sent Items", "Wysłane", '/', MailSpecialFolder.Sent, 3, 0, 0),
            ],
        };
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);

        await plugin.TickAsync(Token);
        backend.Folders = [.. backend.Folders.Reverse()];
        Age(account);
        await plugin.TickAsync(Token);

        var folders = ReadState(account)["folders"]!;
        Assert.Equal("Wysłane", folders["Sent"]!["local"]!.GetValue<string>());
        Assert.Equal("Wysłane (2)", folders["Sent Items"]!["local"]!.GetValue<string>());
        Assert.True(Directory.Exists(Path.Combine(account, "Wysłane (2)")));
    }

    // ---------------------------------------------------------------- server-backed

    [Fact]
    public async Task First_sync_is_batched_and_resumes()
    {
        var server = await MailboxAsync();
        await server.SendAsync(Enumerable.Range(1, 120).Select(i => $"Batch message {i:000}"));
        await server.WaitForCountAsync(120);
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User);
        await RenderAsync(plugin, account);

        Assert.True(await plugin.TickAsync(Token));
        var inbox = InboxFolder(account);
        var afterFirst = Directory.GetFiles(inbox, "*.eml").Length;
        Assert.InRange(afterFirst, 1, 50);
        Assert.NotNull(ReadState(account)["progress"]);

        // Between two ticks the plugin answers other requests at once.
        var watch = Stopwatch.StartNew();
        var card = await AccountPreview(plugin).LoadCardAsync(new PreviewCardRequest(Entry(account), null, null), Host(plugin), Token);
        watch.Stop();
        Assert.NotNull(card);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"preview.card took {watch.Elapsed}");

        var ticks = 1;
        while (await plugin.TickAsync(Token))
        {
            Assert.True(++ticks < 10, "the first sync does not finish");
        }

        var files = Directory.GetFiles(inbox, "*.eml").Select(Path.GetFileName).ToList();
        Assert.Equal(120, files.Count);
        Assert.All(files, f => Assert.StartsWith(Unread, f, StringComparison.Ordinal));
        var state = ReadState(account);
        Assert.Equal(await server.HighestUidAsync(), state["folders"]!["INBOX"]!["lastUid"]!.GetValue<long>());
        Assert.Null(state["progress"]);
        Assert.Null(state["error"]);
        Assert.NotNull(state["lastSuccess"]);
    }

    [Fact]
    public async Task Read_state_follows_server_flags()
    {
        var server = await MailboxAsync();
        await server.SendAsync(["Flag one", "Flag two"]);
        await server.WaitForCountAsync(2);
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User);
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var uid = (uint)await server.HighestUidAsync();
        Assert.Contains(Directory.GetFiles(inbox), f => Path.GetFileName(f).StartsWith(Unread, StringComparison.Ordinal) && f.EndsWith($"[uid {uid}].eml", StringComparison.Ordinal));

        await server.AddFlagsAsync(uid, MessageFlags.Seen | MessageFlags.Flagged);
        Age(account);
        await TickUntilDoneAsync(plugin);

        var file = Path.GetFileName(Assert.Single(Directory.GetFiles(inbox), f => f.EndsWith($"[uid {uid}].eml", StringComparison.Ordinal)));
        Assert.False(file.StartsWith(Unread, StringComparison.Ordinal), file);
        Assert.Single(Directory.GetFiles(inbox), f => Path.GetFileName(f).StartsWith(Unread, StringComparison.Ordinal));
        Assert.Contains($"INBOX/{uid}", ReadState(account)["flagged"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task Removed_on_server_removes_file_but_missing_file_is_refetched()
    {
        var server = await MailboxAsync();
        await server.SendAsync(["Keep one", "Keep two", "Remove me"]);
        await server.WaitForCountAsync(3);
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User);
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var uids = await server.UidsAsync();
        Assert.Equal(3, Directory.GetFiles(inbox, "*.eml").Length);

        await server.ExpungeAsync(uids[^1]);
        var local = Assert.Single(Directory.GetFiles(inbox), f => f.EndsWith($"[uid {uids[0]}].eml", StringComparison.Ordinal));
        File.Delete(local);
        Age(account);
        await TickUntilDoneAsync(plugin);

        var names = Directory.GetFiles(inbox, "*.eml").Select(Path.GetFileName).ToList();
        Assert.Equal(2, names.Count);
        Assert.DoesNotContain(names, n => n!.EndsWith($"[uid {uids[^1]}].eml", StringComparison.Ordinal));
        Assert.Contains(names, n => n!.EndsWith($"[uid {uids[0]}].eml", StringComparison.Ordinal));
        Assert.Contains(uids[0], await server.UidsAsync());
    }

    [Fact]
    public async Task Outside_since_is_kept()
    {
        var server = await MailboxAsync();
        await server.SendAsync(["Recent"]);
        await server.WaitForCountAsync(1);
        await server.AppendOldAsync("Old message", DateTimeOffset.Now.AddDays(-200));
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User, since: "1y");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        Assert.Equal(2, Directory.GetFiles(inbox, "*.eml").Length);

        WriteAccount("Test", server.ImapHostPort, server.User, since: "1m");
        Age(account);
        await TickUntilDoneAsync(plugin);
        Age(account);
        await TickUntilDoneAsync(plugin);

        Assert.Equal(2, Directory.GetFiles(inbox, "*.eml").Length);
        Assert.Contains(Directory.GetFiles(inbox), f => f.Contains("Old message", StringComparison.Ordinal));
    }

    [Fact]
    public async Task New_unread_mail_raises_one_notification_with_inbox_target()
    {
        var server = await MailboxAsync();
        await server.SendAsync(["First"]);
        await server.WaitForCountAsync(1);
        var plugin = Start(server.Service);
        var account = WriteAccount("Firma", server.ImapHostPort, server.User);
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        _ui.Notifications.Clear();

        await server.SendAsync(["Second", "Third"]);
        await server.WaitForCountAsync(3);
        Age(account);
        await TickUntilDoneAsync(plugin);

        var (message, target) = Assert.Single(_ui.Notifications);
        Assert.Equal("Firma: 2 nowe wiadomości", message);
        Assert.Equal(InboxFolder(account), target);
    }

    // ---------------------------------------------------------------- helpers

    private async Task TickUntilDoneAsync(ExternalPlugin plugin)
    {
        for (var i = 0; await plugin.TickAsync(Token); i++)
        {
            Assert.True(i < 10, "sync does not finish");
        }
    }

    private ExternalPlugin Start(IMailBackend backend)
    {
        var discovered = ManifestReader.Read(PluginFolder);
        Assert.True(discovered.IsValid, string.Join("; ", discovered.Errors));
        var missing = RuntimeCatalog.CheckRequirement(discovered.Manifest!.Runtime, _runtimes);
        Assert.SkipWhen(missing is not null, missing ?? string.Empty);
        var runtime = _runtimes.First(r => r.Type == discovered.Manifest.Runtime.Type);
        var plugin = new ExternalPlugin(discovered, runtime, new ScopedPluginHost(discovered.Manifest, _ui, _router, _http, _audit, null, backend), _audit, _storage);
        _owned.Add(plugin);
        return plugin;
    }

    private ScopedPluginHost Host(ExternalPlugin plugin) => new(plugin.Manifest, _ui, _router, _http, _audit);

    private static ExternalPreviewPlugin AccountPreview(ExternalPlugin plugin) =>
        new(plugin, plugin.Manifest.Contributes!.Previews!.Single(p => p.Id == "account"));

    private async Task RenderAsync(ExternalPlugin plugin, string account)
    {
        var content = await AccountPreview(plugin).LoadWithOptionsAsync(Entry(account), Host(plugin), null, pinned: false, Token);
        Assert.NotNull(content);
        Assert.Contains(account, _storage.Get(PluginId, "accounts")!.AsArray().Select(x => x!.GetValue<string>()));
    }

    private static VfsEntry Entry(string folder) => new(Path.GetFileName(folder), folder, VfsEntryKind.Directory, -1);

    private string WriteAccount(string name, string imap, string user, string? since = null)
    {
        var folder = Path.Combine(_dir, "Poczta", name);
        Directory.CreateDirectory(folder);
        var options = new JsonObject { ["imap"] = imap, ["smtp"] = null, ["user"] = user, ["refresh"] = "1m" };
        if (since is not null)
        {
            options["since"] = since;
        }

        var config = new JsonObject
        {
            ["preview"] = new JsonObject { ["plugin"] = PluginId, ["options"] = options },
            ["enter"] = PluginId,
        };
        File.WriteAllText(Path.Combine(folder, "c4k.dir"), "// account\n" + config.ToJsonString());
        return folder;
    }

    private static void WriteLock(string account, string machine, DateTimeOffset lastTick)
    {
        Directory.CreateDirectory(Path.Combine(account, ".mail"));
        File.WriteAllText(Path.Combine(account, ".mail", "lock"), new JsonObject { ["machine"] = machine, ["lastTick"] = lastTick.ToString("O") }.ToJsonString());
    }

    private static JsonNode ReadState(string account) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(account, ".mail", "state.json")))!;

    /// <summary>Makes the account due again (its last check an hour ago), as if <c>refresh</c> had passed.</summary>
    private static void Age(string account)
    {
        var state = ReadState(account);
        state["lastCheck"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        File.WriteAllText(Path.Combine(account, ".mail", "state.json"), state.ToJsonString());
    }

    private static string InboxFolder(string account) =>
        Path.Combine(account, ReadState(account)["folders"]!["INBOX"]!["local"]!.GetValue<string>());

    private async Task<Mailbox> MailboxAsync()
    {
        var imapUrl = Environment.GetEnvironmentVariable("C4K_TEST_IMAP");
        var smtpUrl = Environment.GetEnvironmentVariable("C4K_TEST_SMTP");
        Assert.SkipWhen(string.IsNullOrEmpty(imapUrl) || string.IsNullOrEmpty(smtpUrl), "C4K_TEST_IMAP / C4K_TEST_SMTP are not set");
        var mailbox = new Mailbox(new Uri(imapUrl!), new Uri(smtpUrl!), _dir);
        _owned.Add(mailbox);
        // GreenMail creates the user on first delivery (login = password = address).
        await mailbox.SendAsync(["Welcome"]);
        await mailbox.WaitForCountAsync(1);
        await mailbox.ExpungeAsync((uint)await mailbox.HighestUidAsync());
        await mailbox.PrimeAsync();
        return mailbox;
    }

    /// <summary>A fresh GreenMail user, a <see cref="MailService"/> that trusts it, and MailKit helpers for the server side.</summary>
    private sealed class Mailbox : IAsyncDisposable
    {
        private readonly Uri _imap;
        private readonly Uri _smtp;

        public Mailbox(Uri imap, Uri smtp, string dir)
        {
            _imap = imap;
            _smtp = smtp;
            User = $"u{Guid.NewGuid():N}"[..13] + "@localhost";
            var implicitTls = new HashSet<int>();
            if (imap.Scheme == "imaps") implicitTls.Add(imap.Port);
            if (smtp.Scheme == "smtps") implicitTls.Add(smtp.Port);
            var secrets = new SecretStore(Path.Combine(dir, $"secrets-{User}.json"));
            var credentials = new MailCredentials(secrets, (_, _) => Task.FromResult<string?>(User));
            var certificates = new MailCertificateStore(Path.Combine(dir, $"certs-{User}.json"), (_, _) => Task.FromResult(true));
            Service = new MailService(credentials, certificates, options: new MailServiceOptions { UseCondStore = false, ImplicitTlsPorts = implicitTls });
        }

        public string User { get; }

        public string ImapHostPort => $"{_imap.Host}:{_imap.Port}";

        public MailService Service { get; }

        public ValueTask DisposeAsync() => Service.DisposeAsync();

        /// <summary>One interactive call stores the password and trusts the certificate, as the user's first open would.</summary>
        public Task PrimeAsync() => Service.FoldersAsync(PluginId, new MailAccount(ImapHostPort, null, User), Token);

        public async Task SendAsync(IEnumerable<string> subjects)
        {
            using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
            await client.ConnectAsync(_smtp.Host, _smtp.Port, _smtp.Scheme == "smtps" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, Token);
            foreach (var subject in subjects)
            {
                var message = new MimeMessage();
                message.From.Add(MailboxAddress.Parse("sender@localhost"));
                message.To.Add(MailboxAddress.Parse(User));
                message.Subject = subject;
                message.Body = new TextPart("plain") { Text = subject };
                await client.SendAsync(message, Token);
            }

            await client.DisconnectAsync(true, Token);
        }

        public async Task AppendOldAsync(string subject, DateTimeOffset date)
        {
            var message = new MimeMessage { Subject = subject, Date = date, Body = new TextPart("plain") { Text = subject } };
            message.From.Add(MailboxAddress.Parse("sender@localhost"));
            message.To.Add(MailboxAddress.Parse(User));
            await WithInboxAsync(inbox => inbox.AppendAsync(new AppendRequest(message) { InternalDate = date }, Token));
        }

        public async Task WaitForCountAsync(int count)
        {
            for (var i = 0; i < 100; i++)
            {
                if ((await UidsAsync()).Count >= count)
                {
                    return;
                }

                await Task.Delay(100, Token);
            }

            Assert.Fail($"{count} messages did not arrive");
        }

        public async Task<List<uint>> UidsAsync() =>
            await WithInboxAsync(async inbox => (await inbox.SearchAsync(MailKit.Search.SearchQuery.All, Token)).Select(u => u.Id).Order().ToList());

        public async Task<long> HighestUidAsync() => (await UidsAsync()).DefaultIfEmpty(0u).Max();

        public Task AddFlagsAsync(uint uid, MessageFlags flags) =>
            WithInboxAsync(inbox => inbox.StoreAsync(new UniqueId(uid), new StoreFlagsRequest(StoreAction.Add, flags) { Silent = true }, Token));

        public Task ExpungeAsync(uint uid) => WithInboxAsync(async inbox =>
        {
            await inbox.StoreAsync(new UniqueId(uid), new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, Token);
            await inbox.ExpungeAsync(Token);
            return true;
        });

        private async Task<T> WithInboxAsync<T>(Func<IMailFolder, Task<T>> action)
        {
            using var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
            await client.ConnectAsync(_imap.Host, _imap.Port, _imap.Scheme == "imaps" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, Token);
            await client.AuthenticateAsync(User, User, Token);
            await client.Inbox.OpenAsync(FolderAccess.ReadWrite, Token);
            var result = await action(client.Inbox);
            await client.DisconnectAsync(true, Token);
            return result;
        }

        private Task WithInboxAsync(Func<IMailFolder, Task> action) => WithInboxAsync(async f =>
        {
            await action(f);
            return true;
        });
    }

    private sealed class CountingBackend : IMailBackend
    {
        public int Calls { get; set; }

        public bool ThrowAuth { get; set; }

        public MailAccount? LastAccount { get; private set; }

        public IReadOnlyList<MailFolderInfo> Folders { get; set; } = [new MailFolderInfo("INBOX", "Odebrane", '/', MailSpecialFolder.Inbox, 1, 0, 0)];

        private void Count(MailAccount account)
        {
            Calls++;
            LastAccount = account;
            if (ThrowAuth)
            {
                throw new MailAuthenticationRequiredException($"{account.User}@{account.Imap}");
            }
        }

        public Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(string pluginId, MailAccount account, CancellationToken ct)
        {
            Count(account);
            return Task.FromResult(Folders);
        }

        public Task<MailSyncResult> SyncAsync(string pluginId, MailAccount account, MailSyncRequest request, CancellationToken ct)
        {
            Count(account);
            var validity = Folders.FirstOrDefault(f => f.ServerName == request.Folder)?.UidValidity ?? 1;
            return Task.FromResult(new MailSyncResult(validity, false, 0, [], [], []));
        }

        public Task<long> FetchAsync(string pluginId, MailAccount account, string folder, uint uid, Stream destination, CancellationToken ct)
        {
            Count(account);
            return Task.FromResult(0L);
        }

        public Task SetFlagsAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken ct)
        {
            Count(account);
            return Task.CompletedTask;
        }

        public Task MoveAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct)
        {
            Count(account);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken ct)
        {
            Count(account);
            return Task.CompletedTask;
        }

        public Task<uint?> AppendAsync(string pluginId, MailAccount account, string folder, Stream message, MailFlags flags, CancellationToken ct)
        {
            Count(account);
            return Task.FromResult<uint?>(null);
        }

        public Task<string> SendAsync(string pluginId, MailAccount account, Stream message, CancellationToken ct)
        {
            Count(account);
            return Task.FromResult("<x@test>");
        }
    }

    private sealed class RecordingUi : IPluginUi
    {
        public List<(string Message, string? Target)> Notifications { get; } = [];

        public string? ActivePath => null;

        public string? OtherPath => null;

        public IReadOnlyList<VfsEntry> SelectedEntries => [];

        public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

        public void Notify(string message, bool isError, string? target)
        {
            lock (Notifications)
            {
                Notifications.Add((message, target));
            }
        }

        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult<string?>(null);

        public Task EditFileAsync(string path) => Task.CompletedTask;

        public Task OpenPanelAsync(string panelPluginId, string? argument) => Task.CompletedTask;

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task SearchWebAsync(string query, string? engine) => Task.CompletedTask;

        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Commander4K.sln")))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return directory;
    }
}
