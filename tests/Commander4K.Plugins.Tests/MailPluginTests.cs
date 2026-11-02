using System.Diagnostics;
using System.Security.AccessControl;
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
        var backend = new FakeMailServer { ThrowAuth = true };
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

        await CheckAsync(plugin, account);

        Assert.Equal(1, backend.Calls);
        Assert.True(backend.LastAccount!.Interactive);
    }

    [Fact]
    public async Task Card_reads_files_only()
    {
        var backend = new FakeMailServer();
        backend.Inbox.Add();
        backend.Inbox.Add();
        backend.Inbox.Add(MailFlags.Seen);
        backend.Inbox.Add();
        var trash = backend.AddFolder("Trash", "Kosz", MailSpecialFolder.Trash, 1);
        trash.Add();
        var projects = backend.AddFolder("Projekty", "Projekty", MailSpecialFolder.None, 1);
        projects.Add();
        projects.Add(MailFlags.Seen);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var unread = Directory.GetFiles(inbox, "*.eml").Count(f => Path.GetFileName(f).StartsWith(Unread, StringComparison.Ordinal));
        Assert.Equal(3, unread);
        backend.Calls = 0;

        var card = await AccountPreview(plugin).LoadCardAsync(new PreviewCardRequest(Entry(account), null, null), Host(plugin), Token);

        Assert.Equal(0, backend.Calls);
        Assert.Equal(CardStatus.Ok, card.Status);
        Assert.Equal(new CardValue("Nieprzeczytane", unread.ToString()), card.Items[0]);
        Assert.Contains(new CardValue("Projekty", "1"), card.Items);
        Assert.DoesNotContain(card.Items, i => i is CardValue { Label: "Kosz" });
        var text = Assert.Single(card.Items.OfType<CardText>());
        Assert.Matches(@"^sprawdzono \d\d:\d\d$", text.Text);
        Assert.Equal("Otwórz", card.Actions[0].Label);
        Assert.Equal(inbox, card.Actions[0].Target);
        Assert.Equal("Sprawdź teraz", card.Actions[1].Label);
        Assert.Equal("c4k:plugin/check", card.Actions[1].Target.Split('?')[0]);
    }

    [Fact]
    public async Task Card_shows_auth_required_as_warning()
    {
        var backend = new FakeMailServer { ThrowAuth = true };
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await plugin.TickAsync(Token);
        backend.Calls = 0;

        var card = await AccountPreview(plugin).LoadCardAsync(new PreviewCardRequest(Entry(account), null, null), Host(plugin), Token);

        Assert.Equal(CardStatus.Warning, card.Status);
        Assert.Contains(card.Items.OfType<CardText>(), t => t.Text == "wymaga logowania");
        Assert.Equal(0, backend.Calls);
        Assert.Contains(card.Actions, a => a.Label == "Sprawdź teraz");
    }

    [Fact]
    public async Task Card_shows_error_and_lock_held_elsewhere()
    {
        var backend = new FakeMailServer();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var state = ReadState(account);
        state["error"] = "serwer nie odpowiada";
        File.WriteAllText(Path.Combine(account, ".mail", "state.json"), state.ToJsonString());

        var failed = await AccountPreview(plugin).LoadCardAsync(new PreviewCardRequest(Entry(account), null, null), Host(plugin), Token);

        Assert.Equal(CardStatus.Error, failed.Status);
        Assert.Contains(failed.Items.OfType<CardText>(), t => t.Text.Contains("serwer nie odpowiada", StringComparison.Ordinal));

        WriteLock(account, "BIURKO", DateTimeOffset.UtcNow.AddMinutes(-1));
        var locked = await AccountPreview(plugin).LoadCardAsync(new PreviewCardRequest(Entry(account), null, null), Host(plugin), Token);

        Assert.Equal(CardStatus.Warning, locked.Status);
        Assert.Contains(locked.Items.OfType<CardText>(), t => t.Text == "synchronizuje BIURKO");
    }

    [Fact]
    public async Task Check_is_bounded_and_continues_in_the_background()
    {
        var backend = new FakeMailServer();
        for (var i = 0; i < 120; i++)
        {
            backend.Inbox.Add();
        }

        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);

        await CheckAsync(plugin, account);

        Assert.InRange(backend.Fetches, 1, 50);
        Assert.NotNull(ReadState(account)["progress"]);
        Assert.True(await plugin.TickAsync(Token));
    }

    [Fact]
    public async Task Add_account_writes_both_c4k_dir_files()
    {
        var backend = new FakeMailServer();
        var plugin = Start(backend);
        var poczta = Path.Combine(_dir, "Poczta");
        Directory.CreateDirectory(poczta);
        File.WriteAllText(Path.Combine(poczta, "c4k.dir"),
            "// pulpit\n{ \"enter\": { \"view\": \"dashboard\", \"tiles\": [ { \"title\": \"Dokumenty\", \"open\": \"Dokumenty\" } ] }, \"preview\": \"dashboard\", \"note\": 42 }");
        _ui.Active = poczta;
        // One window; the servers come from the address ("auto"), so IMAP and SMTP are not answered.
        _ui.Dialogs.Enqueue(Answers(("user", "anna@example.org"), ("name", "Anna Nowak"), ("folder", "Firma"), ("servers", "auto"), ("refresh", "5m"), ("since", "3m")));

        await new ExternalCommandPlugin(plugin).ExecuteAsync("add-account", Host(plugin), Token);

        var request = Assert.Single(_ui.DialogRequests);
        Assert.Equal("Dodaj konto pocztowe", request.Title);
        var imap = request.Fields.Single(f => f.Id == "imap");
        Assert.Equal(["manual"], imap.VisibleWhen!["servers"]);
        Assert.Equal(DialogFieldType.Segmented, request.Fields.Single(f => f.Id == "servers").Type);
        Assert.True(request.Fields.Single(f => f.Id == "user").Required);
        var accountDir = Path.Combine(poczta, "Firma");
        var accountText = File.ReadAllText(Path.Combine(accountDir, "c4k.dir"));
        Assert.DoesNotContain("password", accountText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("haslo", accountText, StringComparison.OrdinalIgnoreCase);
        var account = JsonNode.Parse(accountText)!;
        foreach (var key in new[] { "preview", "enter" })
        {
            Assert.Equal(PluginId, account[key]!["plugin"]!.GetValue<string>());
            var options = account[key]!["options"]!;
            Assert.Equal("imap.example.org:993", options["imap"]!.GetValue<string>());
            Assert.Equal("smtp.example.org:465", options["smtp"]!.GetValue<string>());
            Assert.Equal("anna@example.org", options["user"]!.GetValue<string>());
            Assert.Equal("Anna Nowak", options["name"]!.GetValue<string>());
            Assert.Equal("5m", options["refresh"]!.GetValue<string>());
        }

        var dashboard = JsonNode.Parse(File.ReadAllText(Path.Combine(poczta, "c4k.dir")))!;
        Assert.Equal(42, dashboard["note"]!.GetValue<int>());
        var tiles = dashboard["enter"]!["tiles"]!.AsArray();
        Assert.Equal(2, tiles.Count);
        Assert.Equal("Dokumenty", tiles[0]!["title"]!.GetValue<string>());
        Assert.Equal("Firma", tiles[1]!["title"]!.GetValue<string>());
        Assert.Equal("Firma", tiles[1]!["open"]!.GetValue<string>());
        Assert.Equal("official.mail/account", tiles[1]!["card"]!.GetValue<string>());
        Assert.Equal("30s", tiles[1]!["refresh"]!.GetValue<string>());
        Assert.Contains(accountDir, _storage.Get(PluginId, "accounts")!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(backend.LastAccount!.Interactive);
    }

    [Fact]
    public async Task Add_account_creates_a_new_dashboard_and_refuses_remote_paths()
    {
        var backend = new FakeMailServer();
        var plugin = Start(backend);
        var poczta = Path.Combine(_dir, "Nowa");
        Directory.CreateDirectory(poczta);
        _ui.Active = poczta;
        _ui.Dialogs.Enqueue(Answers(("user", "jan@example.org"), ("name", ""), ("folder", "Jan"), ("servers", "manual"), ("imap", "imap.example.org:993"), ("smtp", ""), ("refresh", "off"), ("since", "1y")));

        await new ExternalCommandPlugin(plugin).ExecuteAsync("add-account", Host(plugin), Token);

        var dashboard = JsonNode.Parse(File.ReadAllText(Path.Combine(poczta, "c4k.dir")))!;
        Assert.Equal("dashboard", dashboard["preview"]!.GetValue<string>());
        var options = JsonNode.Parse(File.ReadAllText(Path.Combine(poczta, "Jan", "c4k.dir")))!["preview"]!["options"]!;
        Assert.False(options["refresh"]!.GetValue<bool>());
        Assert.Equal("1y", options["since"]!.GetValue<string>());
        Assert.Equal("Jan", dashboard["enter"]!["tiles"]![0]!["title"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(poczta, "Jan", "c4k.dir")))!["preview"]!["options"]!["smtp"]);

        _ui.Active = "sftp://host/dir";
        var before = backend.Calls;
        await Assert.ThrowsAnyAsync<Exception>(() => new ExternalCommandPlugin(plugin).ExecuteAsync("add-account", Host(plugin), Token));
        Assert.Equal(before, backend.Calls);
    }

    [Fact]
    public async Task Add_account_with_a_wrong_login_asks_again_and_writes_nothing()
    {
        var backend = new FakeMailServer { ThrowAuth = true };
        var plugin = Start(backend);
        var poczta = Path.Combine(_dir, "Zle");
        Directory.CreateDirectory(poczta);
        _ui.Active = poczta;
        _ui.Dialogs.Enqueue(Answers(("user", "jan@example.org"), ("folder", "Jan"), ("servers", "auto")));
        // The second window (after the refused login) is cancelled.

        await new ExternalCommandPlugin(plugin).ExecuteAsync("add-account", Host(plugin), Token);

        Assert.Equal(2, _ui.DialogRequests.Count);
        var again = _ui.DialogRequests[1];
        Assert.StartsWith("Nie można się zalogować", again.Message, StringComparison.Ordinal);
        Assert.Equal("jan@example.org", again.Fields.Single(f => f.Id == "user").Value);
        Assert.Empty(Directory.GetFileSystemEntries(poczta));
        Assert.Null(_storage.Get(PluginId, "accounts"));
    }

    [Fact]
    public async Task Lock_held_by_other_machine_skips_sync()
    {
        var backend = new FakeMailServer();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        WriteLock(account, "OTHER", DateTimeOffset.UtcNow.AddMinutes(-1));
        await RenderAsync(plugin, account);

        await plugin.TickAsync(Token);
        Assert.Equal(0, backend.Calls);

        WriteLock(account, "OTHER", DateTimeOffset.UtcNow.AddMinutes(-11));
        await plugin.TickAsync(Token);

        Assert.True(backend.Calls > 0);
        Assert.Equal(Environment.MachineName, ReadLock(account).Machine, ignoreCase: true);
        Assert.True(new DirectoryInfo(Path.Combine(account, ".mail")).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.Equal("Odebrane", ReadState(account)["folders"]!["INBOX"]!["local"]!.GetValue<string>());
        Assert.True(Directory.Exists(Path.Combine(account, "Odebrane")));
    }

    [Fact]
    public async Task Lock_is_refreshed_on_every_tick_while_the_account_is_not_due()
    {
        var backend = new FakeMailServer();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org", refresh: "30m");
        await RenderAsync(plugin, account);
        await plugin.TickAsync(Token);
        var calls = backend.Calls;

        // 11 minutes later, the account is still not due (refresh 30m).
        WriteLock(account, Environment.MachineName, DateTimeOffset.UtcNow.AddMinutes(-11));
        Age(account, TimeSpan.FromMinutes(11));
        await plugin.TickAsync(Token);

        Assert.Equal(calls, backend.Calls);
        var (machine, lastTick) = ReadLock(account);
        Assert.Equal(Environment.MachineName, machine, ignoreCase: true);
        // What a second computer sees: a lock younger than 10 minutes.
        Assert.True(DateTimeOffset.UtcNow - lastTick < TimeSpan.FromMinutes(1), $"lock is {DateTimeOffset.UtcNow - lastTick} old");
    }

    [Fact]
    public async Task Folders_with_the_same_local_name_get_distinct_stable_folders()
    {
        var backend = new FakeMailServer();
        backend.AddFolder("Sent", "Wysłane", MailSpecialFolder.Sent, 2);
        backend.AddFolder("Sent Items", "Wysłane", MailSpecialFolder.Sent, 3);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);

        await plugin.TickAsync(Token);
        backend.Folders.Reverse();
        Age(account);
        await plugin.TickAsync(Token);

        var folders = ReadState(account)["folders"]!;
        Assert.Equal("Wysłane", folders["Sent"]!["local"]!.GetValue<string>());
        Assert.Equal("Wysłane (2)", folders["Sent Items"]!["local"]!.GetValue<string>());
        Assert.True(Directory.Exists(Path.Combine(account, "Wysłane (2)")));
    }

    [Fact]
    public async Task Unreadable_state_skips_the_account_and_is_never_overwritten()
    {
        var backend = new FakeMailServer();
        backend.Inbox.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        Directory.CreateDirectory(Path.Combine(account, ".mail"));
        var statePath = Path.Combine(account, ".mail", "state.json");
        const string corrupt = """{ "folders": { "INBOX": { "local": "Odebrane", "known": [1, 2""";
        File.WriteAllText(statePath, corrupt);
        await RenderAsync(plugin, account);

        await plugin.TickAsync(Token);
        await CheckAsync(plugin, account);

        Assert.Equal(0, backend.Calls);
        Assert.Equal(corrupt, File.ReadAllText(statePath));
    }

    [Fact]
    public async Task First_sync_does_not_notify_and_later_mail_does()
    {
        var backend = new FakeMailServer();
        for (var i = 0; i < 60; i++)
        {
            backend.Inbox.Add();
        }

        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);

        Assert.True(await plugin.TickAsync(Token));
        await TickUntilDoneAsync(plugin);
        Assert.Equal(60, Directory.GetFiles(InboxFolder(account), "*.eml").Length);
        Assert.Empty(_ui.Notifications);

        backend.Inbox.Add();
        Age(account);
        await TickUntilDoneAsync(plugin);

        var (message, target) = Assert.Single(_ui.Notifications);
        Assert.Equal("Firma: 1 nowa wiadomość", message);
        Assert.Equal(InboxFolder(account), target);
    }

    [Fact]
    public async Task A_file_already_saved_for_a_new_uid_is_adopted_not_fetched()
    {
        var backend = new FakeMailServer();
        var uid = backend.Inbox.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        var inbox = Path.Combine(account, "Odebrane");
        Directory.CreateDirectory(inbox);
        File.WriteAllText(Path.Combine(inbox, Unread + FakeMailServer.FileName(uid)), "Subject: saved before\r\n\r\nx");
        await RenderAsync(plugin, account);

        await TickUntilDoneAsync(plugin);

        Assert.Equal(0, backend.Fetches);
        var folder = ReadState(account)["folders"]!["INBOX"]!;
        Assert.Equal(uid, folder["lastUid"]!.GetValue<long>());
        Assert.Contains((long)uid, folder["known"]!.AsArray().Select(x => x!.GetValue<long>()));
        Assert.Single(Directory.GetFiles(inbox, "*.eml"));
    }

    [Fact]
    public async Task Read_and_unread_copies_of_one_uid_collapse_to_the_server_state()
    {
        var backend = new FakeMailServer();
        var uid = backend.Inbox.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        File.WriteAllText(Path.Combine(inbox, FakeMailServer.FileName(uid)), "copy");

        backend.Inbox.Messages[uid] = MailFlags.Seen;
        Age(account);
        await TickUntilDoneAsync(plugin);

        var file = Assert.Single(Directory.GetFiles(inbox, "*.eml"));
        Assert.Equal(FakeMailServer.FileName(uid), Path.GetFileName(file));
        Assert.Null(ReadState(account)["error"]);
    }

    [Fact]
    public async Task Files_of_a_folder_deleted_on_the_server_are_never_adopted()
    {
        var backend = new FakeMailServer();
        var projects = backend.AddFolder("Projekty", "Projekty", MailSpecialFolder.None, 5);
        projects.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var oldFile = Assert.Single(Directory.GetFiles(Path.Combine(account, "Projekty"), "*.eml"));

        backend.Folders.Remove(projects);
        Age(account);
        await TickUntilDoneAsync(plugin);
        var recreated = backend.AddFolder("Projekty", "Projekty", MailSpecialFolder.None, 9);
        recreated.Add();
        Age(account);
        await TickUntilDoneAsync(plugin);
        Age(account);
        await TickUntilDoneAsync(plugin);

        Assert.True(File.Exists(oldFile), "the old folder's file was deleted");
        Assert.Equal("Projekty (2)", ReadState(account)["folders"]!["Projekty"]!["local"]!.GetValue<string>());
        Assert.Single(Directory.GetFiles(Path.Combine(account, "Projekty (2)"), "*.eml"));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_listed_is_skipped_and_keeps_its_drafts()
    {
        var backend = new FakeMailServer();
        backend.AddFolder("Drafts", "Szkice", MailSpecialFolder.Drafts, 7);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var drafts = Path.Combine(account, "Szkice");
        File.WriteAllText(Path.Combine(drafts, "Odpowiedź.eml"), "Subject: draft\r\n\r\nv1");
        Age(account);
        await TickUntilDoneAsync(plugin);
        Assert.Single(backend.Appended);

        DenyListing(drafts);
        try
        {
            backend.Inbox.Add();
            Age(account);
            await TickUntilDoneAsync(plugin);
        }
        finally
        {
            AllowListing(drafts);
        }

        Assert.Single(backend.Appended);
        Assert.Empty(backend.Deleted);
        Assert.NotNull(ReadState(account)["drafts"]!["Odpowiedź.eml"]);
        Assert.Single(Directory.GetFiles(InboxFolder(account), "*.eml"));
        Assert.Contains("Drafts", ReadState(account)["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drafts_are_appended_once_and_replaced_when_edited()
    {
        var backend = new FakeMailServer();
        var serverDrafts = backend.AddFolder("Drafts", "Szkice", MailSpecialFolder.Drafts, 7);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var draft = Path.Combine(account, "Szkice", "Nowa.eml");
        File.WriteAllText(draft, "Subject: draft\r\n\r\nv1");

        Age(account);
        await TickUntilDoneAsync(plugin);
        var first = Assert.Single(backend.Appended);
        Age(account);
        await TickUntilDoneAsync(plugin);
        Assert.Single(backend.Appended);

        File.WriteAllText(draft, "Subject: draft\r\n\r\nv2");
        File.SetLastWriteTimeUtc(draft, DateTime.UtcNow.AddMinutes(1));
        Age(account);
        await TickUntilDoneAsync(plugin);

        Assert.Equal(2, backend.Appended.Count);
        Assert.Equal([first], backend.Deleted);
        Assert.Equal([backend.Appended[1]], serverDrafts.Messages.Keys.ToList());
        Assert.Equal(["Nowa.eml"], Directory.GetFiles(Path.Combine(account, "Szkice")).Select(Path.GetFileName).ToList());
        Age(account);
        await TickUntilDoneAsync(plugin);
        Assert.Equal(2, backend.Appended.Count);
    }

    [Fact]
    public async Task A_failing_account_does_not_stop_the_next_one()
    {
        var backend = new FakeMailServer();
        backend.Inbox.Add();
        var plugin = Start(backend);
        var broken = WriteAccount("Zepsute", "imap.example.org:993", "broken@example.org");
        File.WriteAllText(Path.Combine(broken, ".mail"), "a file where the folder should be");
        var good = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, broken);
        await RenderAsync(plugin, good);

        await TickUntilDoneAsync(plugin);

        Assert.Single(Directory.GetFiles(InboxFolder(good), "*.eml"));
        Assert.Contains(_audit.Recent(PluginId), e => e.Detail.Contains("broken", StringComparison.OrdinalIgnoreCase) || e.Detail.Contains("Zepsute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Renames_onto_an_existing_name_pick_a_free_name()
    {
        var backend = new FakeMailServer();
        var uid = backend.Inbox.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var stale = Unread + FakeMailServer.FileName(uid)[..^4] + " [stary].eml";
        File.WriteAllText(Path.Combine(inbox, stale), "from an earlier generation");

        backend.Inbox.Validity = 2;
        Age(account);
        await TickUntilDoneAsync(plugin);

        Assert.Null(ReadState(account)["error"]);
        var names = Directory.GetFiles(inbox, "*.eml").Select(Path.GetFileName).ToList();
        Assert.Contains(stale, names);
        Assert.Contains(names, n => n!.Contains(" (2) [uid ", StringComparison.Ordinal) && n.EndsWith("[stary].eml", StringComparison.Ordinal));
        Assert.Contains(Unread + FakeMailServer.FileName(uid), names);
        Assert.Equal(3, names.Count);
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
        Assert.Empty(_ui.Notifications); // a first sync over several ticks is not "new mail"
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

    // ---------------------------------------------------------------- reading, actions, composing (server-backed)

    [Fact]
    public async Task Reply_flow_sends_and_marks_answered()
    {
        var server = await MailboxAsync();
        await server.SendAsync(["Reply me"]);
        await server.WaitForCountAsync(1);
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User, smtp: server.SmtpHostPort);
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var unread = Assert.Single(Directory.GetFiles(inbox, "*.eml"));
        Assert.StartsWith(Unread, Path.GetFileName(unread), StringComparison.Ordinal);
        var uid = (uint)await server.HighestUidAsync();
        var originalId = MimeMessage.Load(unread, Token).MessageId;
        Assert.False(string.IsNullOrEmpty(originalId));

        // 1. Enter: marked read on the server, renamed, and the host is told to follow the new name.
        var opened = Assert.IsType<MovedPreview>(await MessagePreview(plugin).LoadWithOptionsAsync(FileEntry(unread), Host(plugin), null, pinned: true, Token));
        var read = Assert.Single(Directory.GetFiles(inbox, "*.eml"));
        Assert.False(Path.GetFileName(read).StartsWith(Unread, StringComparison.Ordinal), read);
        Assert.Equal(read, opened.Entry.Path);
        var shown = Assert.IsType<ActionsPreview>(opened.Content);
        Assert.Contains("Reply me", Assert.IsType<MarkdownPreview>(shown.Content).Markdown);
        Assert.Contains(shown.Actions, a => a.Target == "c4k:plugin/reply" && a.Shortcut == "R");
        Assert.True((await server.FlagsAsync(uid) & MessageFlags.Seen) != 0);

        // 2. Reply: a form for a new draft in Szkice.
        var replied = Assert.IsType<MovedPreview>(await ActAsync(plugin, read, "reply"));
        var draft = replied.Entry.Path;
        Assert.Equal("Szkice", Path.GetFileName(Path.GetDirectoryName(draft)));
        Assert.True(File.Exists(draft));
        var form = Assert.IsType<FormPreview>(replied.Content);
        Assert.Contains("sender@localhost", Field(form, "to"), StringComparison.Ordinal);
        Assert.StartsWith("Re: ", Field(form, "subject"), StringComparison.Ordinal);
        Assert.Contains(form.Buttons, b => b.Target == "c4k:plugin/send" && b.Shortcut == "Ctrl+Enter");

        // 3. Typing keeps the draft.
        var values = Values(form);
        values["body"] = "Dzięki\n" + values["body"];
        await MessagePreview(plugin).FormChangedAsync(FileEntry(draft), null, values, Host(plugin), Token);
        Assert.Contains("Dzięki", MimeMessage.Load(draft, Token).TextBody, StringComparison.Ordinal);

        // 4. Send: delivered with In-Reply-To, copied to Sent, original answered, draft gone; the original is shown again.
        var sent = Assert.IsType<MovedPreview>(await ActAsync(plugin, draft, "send", values));
        Assert.Equal(read, sent.Entry.Path);
        Assert.IsType<ActionsPreview>(sent.Content);
        var delivered = await server.FindAsync("sender@localhost", sentFolder: false, originalId);
        Assert.NotNull(delivered);
        Assert.Contains("Dzięki", delivered!.TextBody, StringComparison.Ordinal);
        Assert.NotNull(await server.FindAsync(server.User, sentFolder: true, originalId));
        Assert.True((await server.FlagsAsync(uid) & MessageFlags.Answered) != 0);
        Assert.False(File.Exists(draft));
    }

    [Fact]
    public async Task Cursor_preview_does_not_mark_read()
    {
        var server = await MailboxAsync();
        await server.SendAsync(["Just looking"]);
        await server.WaitForCountAsync(1);
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User);
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var file = Assert.Single(Directory.GetFiles(InboxFolder(account), "*.eml"));
        var uid = (uint)await server.HighestUidAsync();

        var content = await MessagePreview(plugin).LoadWithOptionsAsync(FileEntry(file), Host(plugin), null, pinned: false, Token);

        Assert.IsNotType<MovedPreview>(content);
        Assert.True(File.Exists(file));
        Assert.StartsWith(Unread, Path.GetFileName(file), StringComparison.Ordinal);
        Assert.Equal(MessageFlags.None, await server.FlagsAsync(uid) & MessageFlags.Seen);
    }

    [Fact]
    public async Task Delete_moves_to_trash_on_server_and_locally()
    {
        var server = await MailboxAsync();
        await server.CreateFolderAsync("Trash");
        await server.SendAsync(["Delete me"]);
        await server.WaitForCountAsync(1);
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User);
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var file = Assert.Single(Directory.GetFiles(inbox, "*.eml"));
        var trash = Path.Combine(account, "Kosz");
        Assert.True(Directory.Exists(trash));

        var deleted = Assert.IsType<MovedPreview>(await ActAsync(plugin, file, "delete"));

        Assert.Empty(Directory.GetFiles(inbox, "*.eml"));
        var moved = Assert.Single(Directory.GetFiles(trash, "*.eml"));
        Assert.Equal(moved, deleted.Entry.Path);
        Assert.Empty(await server.UidsAsync());
        var trashUid = Assert.Single(await server.UidsAsync("Trash"));
        Assert.EndsWith($"[uid {trashUid}].eml", moved, StringComparison.Ordinal);

        // The next sync adopts the moved file instead of downloading a second copy.
        Age(account);
        await TickUntilDoneAsync(plugin);
        Assert.Equal([moved], Directory.GetFiles(trash, "*.eml"));
        Assert.Empty(Directory.GetFiles(inbox, "*.eml"));

        // Deleting inside Kosz expunges it and removes the file.
        await ActAsync(plugin, moved, "delete");
        Assert.Empty(Directory.GetFiles(trash, "*.eml"));
        Assert.Empty(await server.UidsAsync("Trash"));
    }

    [Fact]
    public async Task Send_failure_keeps_draft_and_shows_error()
    {
        var server = await MailboxAsync();
        await server.SendAsync(["Cannot answer"]);
        await server.WaitForCountAsync(1);
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User, smtp: "127.0.0.1:1");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var file = Assert.Single(Directory.GetFiles(InboxFolder(account), "*.eml"));
        var replied = Assert.IsType<MovedPreview>(await ActAsync(plugin, file, "reply"));
        var values = Values(Assert.IsType<FormPreview>(replied.Content));
        values["body"] = "Odpowiedź";

        var result = await ActAsync(plugin, replied.Entry.Path, "send", values);

        var form = Assert.IsType<FormPreview>(Inner(result));
        Assert.True(form.MessageIsError);
        Assert.False(string.IsNullOrWhiteSpace(form.Message));
        Assert.Equal("Odpowiedź", Field(form, "body"));
        Assert.True(File.Exists(replied.Entry.Path));
        Assert.Contains("Odpowiedź", MimeMessage.Load(replied.Entry.Path, Token).TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_sync_of_a_large_folder_asks_for_at_most_the_remaining_budget()
    {
        var backend = new FakeMailServer();
        for (var i = 0; i < 130; i++)
        {
            backend.Inbox.Add(MailFlags.Seen);
        }

        var sent = backend.AddFolder("Sent", "Wysłane", MailSpecialFolder.Sent, 3);
        for (var i = 0; i < 10; i++)
        {
            sent.Add(MailFlags.Seen);
        }

        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);

        var inboxFiles = 0;
        for (var tick = 1; await plugin.TickAsync(Token); tick++)
        {
            Assert.True(tick < 10, "the first sync does not finish");
            var now = Directory.GetFiles(InboxFolder(account), "*.eml").Length;
            Assert.InRange(now - inboxFiles, 1, 50); // one tick never downloads more than 50
            inboxFiles = now;
        }

        Assert.Equal(130, Directory.GetFiles(InboxFolder(account), "*.eml").Length);
        Assert.Equal(10, Directory.GetFiles(Path.Combine(account, "Wysłane"), "*.eml").Length);
        Assert.Equal(140, backend.Fetches);
        Assert.All(backend.SyncRequests, r => Assert.InRange(r.Limit ?? 0, 1, 50));
        Assert.Equal([50, 50, 50], backend.SyncRequests.Where(r => r.Folder == "INBOX").Select(r => r.Limit).Take(3));
        // The tick that finished the Inbox (30 left) had 20 of its 50 fetches left for Wysłane.
        Assert.Equal(20, backend.SyncRequests.Single(r => r.Folder == "Sent").Limit);
        Assert.Null(ReadState(account)["progress"]);
    }

    [Fact]
    public async Task State_is_written_through_a_temp_file_and_recovered_from_it()
    {
        var backend = new FakeMailServer();
        backend.Inbox.Add(MailFlags.Seen);
        backend.Inbox.Add(MailFlags.Seen);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var state = Path.Combine(account, ".mail", "state.json");
        var temp = state + ".tmp";
        Assert.False(File.Exists(temp)); // moved over state.json
        Assert.Equal(2, backend.Fetches);

        // A write that stopped before the move: state.json is gone, the finished temp file is left.
        Age(account);
        File.Move(state, temp);
        await TickUntilDoneAsync(plugin);
        Assert.Equal(2, backend.Fetches); // the state was recovered: nothing downloaded again
        Assert.True(File.Exists(state));
        Assert.False(File.Exists(temp));

        // A truncated state.json next to a finished temp file.
        Age(account);
        File.Copy(state, temp);
        File.WriteAllText(state, """{ "folders": { "INBOX": { "local": "Odebr""");
        await TickUntilDoneAsync(plugin);
        Assert.Equal(2, backend.Fetches);
        Assert.Equal(2, ReadState(account)["folders"]!["INBOX"]!["known"]!.AsArray().Count);
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task A_state_that_cannot_be_read_can_be_started_over_and_files_are_adopted()
    {
        var backend = new FakeMailServer();
        backend.Inbox.Add(MailFlags.Seen);
        backend.Inbox.Add(MailFlags.Seen);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var state = Path.Combine(account, ".mail", "state.json");
        const string corrupt = """{ "folders": { "INBOX": { "local": "Odebrane", "known": [1, 2""";
        File.WriteAllText(state, corrupt);
        var calls = backend.Calls;

        var view = await AccountPreview(plugin).LoadWithOptionsAsync(Entry(account), Host(plugin), null, pinned: true, Token);
        var actions = Assert.IsType<ActionsPreview>(view).Actions;
        Assert.Contains(actions, a => a.Target.StartsWith("c4k:plugin/resetState", StringComparison.Ordinal));
        await plugin.TickAsync(Token);
        Assert.Equal(calls, backend.Calls);
        Assert.Equal(corrupt, File.ReadAllText(state));

        await AccountPreview(plugin).InvokeActionAsync(
            new PreviewActionRequest(Entry(account), null, "resetState", new Dictionary<string, string> { ["account"] = account }, null), Host(plugin), Token);

        Assert.False(File.Exists(state));
        var broken = Assert.Single(Directory.GetFiles(Path.Combine(account, ".mail"), "state.json.broken-*"));
        Assert.Equal(corrupt, File.ReadAllText(broken));
        await TickUntilDoneAsync(plugin);
        Assert.Equal(2, backend.Fetches); // the files were adopted by their uid
        Assert.Equal(2, Directory.GetFiles(InboxFolder(account), "*.eml").Length);
        Assert.Equal(2, ReadState(account)["folders"]!["INBOX"]!["known"]!.AsArray().Count);
        Assert.Null(ReadState(account)["error"]);
    }

    [Fact]
    public async Task Permanent_delete_inside_trash_needs_confirmation()
    {
        var backend = new FakeMailServer();
        var trash = backend.AddFolder("Trash", "Kosz", MailSpecialFolder.Trash, 4);
        var uid = trash.Add(MailFlags.Seen);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(account, "Kosz"), "*.eml"));

        _ui.ConfirmAnswer = false;
        await ActAsync(plugin, file, "delete");

        Assert.Single(_ui.Confirmations);
        Assert.Contains("na stałe", _ui.Confirmations[0].Message, StringComparison.Ordinal);
        Assert.True(File.Exists(file));
        Assert.Empty(backend.Deleted);
        Assert.True(trash.Messages.ContainsKey(uid));

        _ui.ConfirmAnswer = true;
        await ActAsync(plugin, file, "delete");

        Assert.Equal(2, _ui.Confirmations.Count);
        Assert.False(File.Exists(file));
        Assert.Equal([uid], backend.Deleted);
    }

    [Fact]
    public async Task Discarding_a_draft_needs_confirmation()
    {
        var backend = new FakeMailServer();
        var serverDrafts = backend.AddFolder("Drafts", "Szkice", MailSpecialFolder.Drafts, 7);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var draft = Path.Combine(account, "Szkice", "Nowa.eml");
        File.WriteAllText(draft, "To: jan@example.org\r\nSubject: draft\r\n\r\nv1");
        Age(account);
        await TickUntilDoneAsync(plugin);
        var uploaded = Assert.Single(backend.Appended);

        _ui.ConfirmAnswer = false;
        var kept = Assert.IsType<FormPreview>(Inner(await ActAsync(plugin, draft, "discard")));

        Assert.Single(_ui.Confirmations);
        Assert.Equal("jan@example.org", Field(kept, "to"));
        Assert.True(File.Exists(draft));
        Assert.Empty(backend.Deleted);
        Assert.True(serverDrafts.Messages.ContainsKey(uploaded));

        _ui.ConfirmAnswer = true;
        await ActAsync(plugin, draft, "discard");

        Assert.False(File.Exists(draft));
        Assert.Equal([uploaded], backend.Deleted);
    }

    [Fact]
    public async Task An_unconfirmed_send_says_it_may_have_gone_out_and_keeps_the_draft()
    {
        var backend = new FakeMailServer();
        backend.AddFolder("Drafts", "Szkice", MailSpecialFolder.Drafts, 7);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org", smtp: "smtp.example.org:465");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var draft = Path.Combine(account, "Szkice", "Nowa.eml");
        File.WriteAllText(draft, "To: jan@example.org\r\nSubject: draft\r\n\r\nv1");

        backend.SendFailure = new MailSendOutcomeUnknownException(new TimeoutException());
        var unknown = Assert.IsType<FormPreview>(Inner(await ActAsync(plugin, draft, "send")));

        Assert.True(unknown.MessageIsError);
        Assert.Equal("Nie wiadomo, czy wiadomość została wysłana — sprawdź folder Wysłane przed ponowną próbą.", unknown.Message);
        Assert.True(File.Exists(draft));

        backend.SendFailure = new InvalidOperationException("odmowa");
        var refused = Assert.IsType<FormPreview>(Inner(await ActAsync(plugin, draft, "send")));

        Assert.StartsWith("Nie wysłano: ", refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(draft));
    }

    [Fact]
    public async Task Old_and_downloaded_files_in_drafts_are_not_uploaded()
    {
        var backend = new FakeMailServer();
        var serverDrafts = backend.AddFolder("Drafts", "Szkice", MailSpecialFolder.Drafts, 7);
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var dir = Path.Combine(account, "Szkice");
        File.WriteAllText(Path.Combine(dir, "2026-10-01 1200 Jan - Stary [uid 3] [stary].eml"), "Subject: old\r\n\r\nold");
        File.WriteAllText(Path.Combine(dir, "2026-10-01 1200 Jan - Kopia [uid 4] (2).eml"), "Subject: copy\r\n\r\ncopy");
        File.WriteAllText(Path.Combine(dir, "Nowa.eml"), "Subject: draft\r\n\r\nv1");

        Age(account);
        await TickUntilDoneAsync(plugin);

        Assert.Single(backend.Appended);
        Assert.Single(serverDrafts.Messages);
        Assert.Equal(["Nowa.eml"], ReadState(account)["drafts"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task Add_account_keeps_date_like_strings_and_key_order_in_the_dashboard()
    {
        var backend = new FakeMailServer();
        var plugin = Start(backend);
        var poczta = Path.Combine(_dir, "Daty");
        Directory.CreateDirectory(poczta);
        File.WriteAllText(Path.Combine(poczta, "c4k.dir"),
            "{ \"zeta\": \"2026-01-01T10:00:00Z\", \"enter\": { \"view\": \"dashboard\", \"tiles\": [ { \"title\": \"2026-01-01\", \"open\": \"x\" } ] }, \"preview\": \"dashboard\", \"alpha\": 1.50 }");
        _ui.Active = poczta;
        _ui.Dialogs.Enqueue(Answers(("user", "anna@example.org"), ("folder", "Firma"), ("servers", "auto")));

        await new ExternalCommandPlugin(plugin).ExecuteAsync("add-account", Host(plugin), Token);

        var dashboard = JsonNode.Parse(File.ReadAllText(Path.Combine(poczta, "c4k.dir")))!.AsObject();
        Assert.Equal(["zeta", "enter", "preview", "alpha"], dashboard.Select(p => p.Key));
        Assert.Equal("2026-01-01T10:00:00Z", dashboard["zeta"]!.GetValue<string>());
        Assert.Equal(1.50m, dashboard["alpha"]!.GetValue<decimal>());
        var tiles = dashboard["enter"]!["tiles"]!.AsArray();
        Assert.Equal("2026-01-01", tiles[0]!["title"]!.GetValue<string>());
        Assert.Equal("Firma", tiles[1]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Html_message_renders_without_remote_content()
    {
        var server = await MailboxAsync();
        await server.AppendHtmlAsync("Newsletter", "<p>Hej&nbsp;<b>Jan</b></p><script>alert(1)</script><img src=\"https://t.example/p.gif\" alt=\"logo\"><a href=\"javascript:x()\">klik</a><a href=\"https://ok.example/a\">tu</a><style>p{}</style>");
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User);
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var file = Assert.Single(Directory.GetFiles(InboxFolder(account), "*.eml"));

        var content = await MessagePreview(plugin).LoadWithOptionsAsync(FileEntry(file), Host(plugin), null, pinned: false, Token);

        var markdown = Assert.IsType<MarkdownPreview>(Assert.IsType<ActionsPreview>(content).Content).Markdown;
        Assert.Contains("Hej Jan", markdown, StringComparison.Ordinal);
        Assert.Contains("Newsletter", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("t.example", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", markdown, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- composing (offline)

    [Fact]
    public async Task Attachments_are_added_from_the_selection_and_removed_and_the_draft_reopens_as_the_form()
    {
        var backend = new FakeMailServer();
        backend.Inbox.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var message = Assert.Single(Directory.GetFiles(InboxFolder(account), "*.eml"));
        var attachment = Path.Combine(_dir, "raport.txt");
        File.WriteAllText(attachment, "liczby");

        var replied = Assert.IsType<MovedPreview>(await ActAsync(plugin, message, "forward"));
        var draft = replied.Entry.Path;
        var values = Values(Assert.IsType<FormPreview>(replied.Content));
        values["to"] = "Jan Kowalski <jan@example.org>, ewa@example.org";
        values["bcc"] = "ukryty@example.org";
        _ui.Selection = [FileEntry(attachment)];

        var attached = Assert.IsType<FormPreview>(Inner(await ActAsync(plugin, draft, "attachSelected", values)));
        Assert.Equal(["raport.txt"], attached.Files.Select(f => f.Name));
        Assert.Contains(attached.Buttons, b => b.Target == "c4k:plugin/removeAttachment?i=0");
        var saved = MimeMessage.Load(draft, Token);
        Assert.Equal(["jan@example.org", "ewa@example.org"], saved.To.Mailboxes.Select(m => m.Address));
        Assert.Equal("Jan Kowalski", saved.To.Mailboxes.First().Name);
        Assert.Equal(["raport.txt"], saved.Attachments.Select(a => a.ContentDisposition?.FileName ?? a.ContentType.Name));

        // Opening the draft file again shows the same form (including the blind copy).
        var reopened = Assert.IsType<FormPreview>(Inner(await MessagePreview(plugin).LoadWithOptionsAsync(FileEntry(draft), Host(plugin), null, pinned: true, Token)));
        Assert.Equal("ukryty@example.org", Field(reopened, "bcc"));
        Assert.Contains("jan@example.org", Field(reopened, "to"), StringComparison.Ordinal);
        Assert.Single(reopened.Files);

        var removed = Assert.IsType<FormPreview>(Inner(await ActAsync(plugin, draft, "removeAttachment", Values(reopened), new() { ["i"] = "0" })));
        Assert.Empty(removed.Files);
        Assert.Empty(MimeMessage.Load(draft, Token).Attachments);

        await ActAsync(plugin, draft, "discard", Values(removed));
        Assert.False(File.Exists(draft));
    }

    [Fact]
    public async Task Save_attachment_goes_to_the_other_file_panel()
    {
        var backend = new FakeMailServer();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var mail = new MimeMessage { Subject = "Z plikiem" };
        mail.From.Add(MailboxAddress.Parse("jan@example.org"));
        var body = new BodyBuilder { TextBody = "w załączniku" };
        body.Attachments.Add("..\\zły:plik.txt", Encoding.UTF8.GetBytes("treść"));
        mail.Body = body.ToMessageBody();
        var file = Path.Combine(inbox, "2026-10-31 1200 Jan - Z plikiem.eml");
        mail.WriteTo(file, Token);
        var target = Directory.CreateDirectory(Path.Combine(_dir, "Pobrane")).FullName;
        _ui.Active = inbox;
        _ui.Other = target;

        var content = await MessagePreview(plugin).LoadWithOptionsAsync(FileEntry(file), Host(plugin), null, pinned: false, Token);
        var save = Assert.Single(Assert.IsType<ActionsPreview>(content).Actions, a => a.Target.StartsWith("c4k:plugin/saveAttachment", StringComparison.Ordinal));
        Assert.Equal("c4k:plugin/saveAttachment?i=0", save.Target);
        await ActAsync(plugin, file, "saveAttachment", args: new() { ["i"] = "0" });

        var saved = Assert.Single(Directory.GetFiles(target));
        Assert.Equal("treść", File.ReadAllText(saved));
        Assert.DoesNotContain("..", Path.GetFileName(saved), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_trash_folder_delete_is_neither_offered_nor_accepted()
    {
        var backend = new FakeMailServer();
        var uid = backend.Inbox.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var file = Assert.Single(Directory.GetFiles(InboxFolder(account), "*.eml"));

        var content = await MessagePreview(plugin).LoadWithOptionsAsync(FileEntry(file), Host(plugin), null, pinned: false, Token);

        var actions = Assert.IsType<ActionsPreview>(content).Actions;
        Assert.DoesNotContain(actions, a => a.Target == "c4k:plugin/delete");
        Assert.DoesNotContain(actions, a => a.Target == "c4k:plugin/archive");
        await Assert.ThrowsAnyAsync<Exception>(() => ActAsync(plugin, file, "delete"));
        Assert.True(File.Exists(file));
        Assert.Empty(backend.Deleted);
        Assert.True(backend.Inbox.Messages.ContainsKey(uid));
    }

    [Fact]
    public async Task Form_edits_only_rewrite_drafts()
    {
        var backend = new FakeMailServer();
        backend.Inbox.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var file = Assert.Single(Directory.GetFiles(InboxFolder(account), "*.eml"));
        var before = File.ReadAllBytes(file);

        await MessagePreview(plugin).FormChangedAsync(FileEntry(file), null,
            new Dictionary<string, string> { ["to"] = "x@example.org", ["subject"] = "Zmiana", ["body"] = "nadpisane" }, Host(plugin), Token);

        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Attach_selected_skips_folders()
    {
        var backend = new FakeMailServer();
        backend.Inbox.Add();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var message = Assert.Single(Directory.GetFiles(InboxFolder(account), "*.eml"));
        var replied = Assert.IsType<MovedPreview>(await ActAsync(plugin, message, "reply"));
        var file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "a");
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "Folder")).FullName;
        _ui.Selection = [new VfsEntry("Folder", folder, VfsEntryKind.Directory), FileEntry(file)];

        var form = Assert.IsType<FormPreview>(Inner(await ActAsync(plugin, replied.Entry.Path, "attachSelected", Values(Assert.IsType<FormPreview>(replied.Content)))));

        Assert.Equal(["a.txt"], form.Files.Select(f => f.Name));
    }

    [Fact]
    public async Task Archive_moves_the_message_and_unread_clears_seen()
    {
        var server = await MailboxAsync();
        await server.CreateFolderAsync("Archive");
        await server.SendAsync(["Archive me", "Unread me"]);
        await server.WaitForCountAsync(2);
        var plugin = Start(server.Service);
        var account = WriteAccount("Test", server.ImapHostPort, server.User);
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var archive = Path.Combine(account, "Archiwum");
        Assert.True(Directory.Exists(archive));
        var files = Directory.GetFiles(inbox, "*.eml");
        var toArchive = files.Single(f => f.Contains("Archive me", StringComparison.Ordinal));
        var toUnread = files.Single(f => f.Contains("Unread me", StringComparison.Ordinal));
        var content = await MessagePreview(plugin).LoadWithOptionsAsync(FileEntry(toArchive), Host(plugin), null, pinned: false, Token);
        Assert.Contains(Assert.IsType<ActionsPreview>(content).Actions, a => a.Target == "c4k:plugin/archive" && a.Shortcut == "A");

        var moved = Assert.IsType<MovedPreview>(await ActAsync(plugin, toArchive, "archive"));

        Assert.False(File.Exists(toArchive));
        Assert.Equal(archive, Path.GetDirectoryName(moved.Entry.Path));
        Assert.True(File.Exists(moved.Entry.Path));
        var archived = Assert.Single(await server.UidsAsync("Archive"));
        Assert.EndsWith($"[uid {archived}].eml", moved.Entry.Path, StringComparison.Ordinal);
        Assert.Single(await server.UidsAsync());

        // Opening marks it read; "unread" undoes it on the server and in the name.
        var uid = (uint)await server.HighestUidAsync();
        var opened = Assert.IsType<MovedPreview>(await MessagePreview(plugin).LoadWithOptionsAsync(FileEntry(toUnread), Host(plugin), null, pinned: true, Token));
        Assert.True((await server.FlagsAsync(uid) & MessageFlags.Seen) != 0);
        var unread = Assert.IsType<MovedPreview>(await ActAsync(plugin, opened.Entry.Path, "unread"));
        Assert.Equal(MessageFlags.None, await server.FlagsAsync(uid) & MessageFlags.Seen);
        Assert.StartsWith(Unread, Path.GetFileName(unread.Entry.Path), StringComparison.Ordinal);
        Assert.True(File.Exists(unread.Entry.Path));
        Assert.False(File.Exists(opened.Entry.Path));
    }

    [Fact]
    public async Task Save_attachment_names_are_sanitised_exactly()
    {
        var backend = new FakeMailServer();
        var plugin = Start(backend);
        var account = WriteAccount("Firma", "imap.example.org:993", "anna@example.org");
        await RenderAsync(plugin, account);
        await TickUntilDoneAsync(plugin);
        var inbox = InboxFolder(account);
        var mail = new MimeMessage { Subject = "Trzy pliki" };
        mail.From.Add(MailboxAddress.Parse("jan@example.org"));
        var body = new BodyBuilder { TextBody = "x" };
        body.Attachments.Add("..\\x", Encoding.UTF8.GetBytes("0"));
        body.Attachments.Add("C:\\Windows\\evil.txt", Encoding.UTF8.GetBytes("1"));
        body.Attachments.Add("CON.txt", Encoding.UTF8.GetBytes("2"));
        mail.Body = body.ToMessageBody();
        var file = Path.Combine(inbox, "2026-10-31 1200 Jan - Trzy pliki.eml");
        mail.WriteTo(file, Token);
        var target = Directory.CreateDirectory(Path.Combine(_dir, "Pobrane")).FullName;
        _ui.Other = target;

        for (var i = 0; i < 3; i++)
        {
            await ActAsync(plugin, file, "saveAttachment", args: new() { ["i"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        Assert.Equal(["evil.txt", "x", "zalacznik-2CON.txt"], Directory.GetFiles(target).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- helpers

    private static ExternalPreviewPlugin MessagePreview(ExternalPlugin plugin) =>
        new(plugin, plugin.Manifest.Contributes!.Previews!.Single(p => p.Id == "message"));

    private static VfsEntry FileEntry(string path) => new(Path.GetFileName(path), path, VfsEntryKind.File, new FileInfo(path).Length);

    private Task<PreviewContent?> ActAsync(ExternalPlugin plugin, string path, string action, Dictionary<string, string>? values = null, Dictionary<string, string>? args = null) =>
        MessagePreview(plugin).InvokeActionAsync(new PreviewActionRequest(FileEntry(path), null, action, args ?? [], values), Host(plugin), Token);

    private static PreviewContent? Inner(PreviewContent? content) => content is MovedPreview moved ? moved.Content : content;

    private static string Field(FormPreview form, string id) => form.Fields.Single(f => f.Id == id).Value;

    private static Dictionary<string, string> Values(FormPreview form) => form.Fields.ToDictionary(f => f.Id, f => f.Value, StringComparer.Ordinal);

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

    private Task CheckAsync(ExternalPlugin plugin, string account) =>
        AccountPreview(plugin).InvokeActionAsync(
            new PreviewActionRequest(Entry(account), null, "check", new Dictionary<string, string>(), null), Host(plugin), Token);

    private static (string Machine, DateTimeOffset LastTick) ReadLock(string account)
    {
        using var lockFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(account, ".mail", "lock")));
        return (lockFile.RootElement.GetProperty("machine").GetString()!, DateTimeOffset.Parse(lockFile.RootElement.GetProperty("lastTick").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static FileSystemAccessRule ListRule() =>
        new(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);

    private static void DenyListing(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(ListRule());
        info.SetAccessControl(security);
    }

    private static void AllowListing(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.RemoveAccessRule(ListRule());
        info.SetAccessControl(security);
    }

    private string WriteAccount(string name, string imap, string user, string? since = null, string refresh = "1m", string? smtp = null)
    {
        var folder = Path.Combine(_dir, "Poczta", name);
        Directory.CreateDirectory(folder);
        var options = new JsonObject { ["imap"] = imap, ["smtp"] = smtp, ["user"] = user, ["refresh"] = refresh };
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
    private static void Age(string account, TimeSpan? by = null)
    {
        var state = ReadState(account);
        state["lastCheck"] = DateTimeOffset.UtcNow.Subtract(by ?? TimeSpan.FromHours(1)).ToString("O");
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
            var credentials = new MailCredentials(secrets, (_, _, _) => Task.FromResult<string?>(User));
            var certificates = new MailCertificateStore(Path.Combine(dir, $"certs-{User}.json"), (_, _) => Task.FromResult(true));
            Service = new MailService(credentials, certificates, options: new MailServiceOptions { UseCondStore = false, ImplicitTlsPorts = implicitTls });
        }

        public string User { get; }

        public string ImapHostPort => $"{_imap.Host}:{_imap.Port}";

        public string SmtpHostPort => $"{_smtp.Host}:{_smtp.Port}";

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
                message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("localhost");
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

        public async Task<List<uint>> UidsAsync(string folder) => await WithClientAsync(User, async client =>
        {
            var f = await client.GetFolderAsync(folder, Token);
            await f.OpenAsync(FolderAccess.ReadOnly, Token);
            return (await f.SearchAsync(MailKit.Search.SearchQuery.All, Token)).Select(u => u.Id).Order().ToList();
        });

        public async Task<MessageFlags> FlagsAsync(uint uid) => await WithInboxAsync(async inbox =>
            (await inbox.FetchAsync([new UniqueId(uid)], MessageSummaryItems.Flags, Token)).Single().Flags ?? MessageFlags.None);

        public Task CreateFolderAsync(string name) => WithClientAsync(User, async client =>
        {
            var root = await client.GetFolderAsync(client.PersonalNamespaces[0].Path, Token);
            await root.CreateAsync(name, isMessageFolder: true, Token);
            return true;
        });

        public async Task AppendHtmlAsync(string subject, string html)
        {
            var message = new MimeMessage { Subject = subject, Body = new TextPart("html") { Text = html } };
            message.From.Add(MailboxAddress.Parse("sender@localhost"));
            message.To.Add(MailboxAddress.Parse(User));
            await WithInboxAsync(inbox => inbox.AppendAsync(new AppendRequest(message), Token));
        }

        /// <summary>A message answering <paramref name="inReplyTo"/> in a user's Inbox (or Sent), waiting a little for delivery.</summary>
        public async Task<MimeMessage?> FindAsync(string user, bool sentFolder, string inReplyTo)
        {
            for (var i = 0; i < 50; i++)
            {
                var found = await WithClientAsync(user, async client =>
                {
                    IMailFolder folder = client.Inbox;
                    if (sentFolder)
                    {
                        var root = await client.GetFolderAsync(client.PersonalNamespaces[0].Path, Token);
                        folder = (await root.GetSubfoldersAsync(false, Token)).First(f => f.Name.Equals("Sent", StringComparison.OrdinalIgnoreCase) || (f.Attributes & FolderAttributes.Sent) != 0);
                    }

                    await folder.OpenAsync(FolderAccess.ReadOnly, Token);
                    foreach (var id in await folder.SearchAsync(MailKit.Search.SearchQuery.All, Token))
                    {
                        var message = await folder.GetMessageAsync(id, Token);
                        if (message.InReplyTo == inReplyTo)
                        {
                            return message;
                        }
                    }

                    return null;
                });
                if (found is not null)
                {
                    return found;
                }

                await Task.Delay(100, Token);
            }

            return null;
        }

        private async Task<T> WithClientAsync<T>(string user, Func<ImapClient, Task<T>> action)
        {
            using var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
            await client.ConnectAsync(_imap.Host, _imap.Port, _imap.Scheme == "imaps" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, Token);
            await client.AuthenticateAsync(user, user, Token);
            var result = await action(client);
            await client.DisconnectAsync(true, Token);
            return result;
        }

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

    /// <summary>An IMAP server in memory: folders with UIDVALIDITY and messages with flags, answering like MailService without CONDSTORE.</summary>
    private sealed class FakeMailServer : IMailBackend
    {
        public FakeMailServer() => Inbox = AddFolder("INBOX", "Odebrane", MailSpecialFolder.Inbox, 1);

        public sealed class Folder(string server, string local, MailSpecialFolder special, uint validity)
        {
            public string Server { get; } = server;

            public string Local { get; } = local;

            public MailSpecialFolder Special { get; } = special;

            public uint Validity { get; set; } = validity;

            public SortedDictionary<uint, MailFlags> Messages { get; } = [];

            public uint NextUid { get; set; } = 1;

            public uint Add(MailFlags flags = MailFlags.None)
            {
                var uid = NextUid++;
                Messages[uid] = flags;
                return uid;
            }
        }

        public List<Folder> Folders { get; } = [];

        public Folder Inbox { get; }

        public int Calls { get; set; }

        public int Fetches { get; private set; }

        public List<uint> Appended { get; } = [];

        public List<uint> Deleted { get; } = [];

        public bool ThrowAuth { get; set; }

        public Exception? SendFailure { get; set; }

        public List<(string Folder, int? Limit)> SyncRequests { get; } = [];

        public MailAccount? LastAccount { get; private set; }

        public static string FileName(uint uid) => $"2026-10-31 1200 Jan - Temat {uid} [uid {uid}].eml";

        public Folder AddFolder(string server, string local, MailSpecialFolder special, uint validity)
        {
            var folder = new Folder(server, local, special, validity);
            Folders.Add(folder);
            return folder;
        }

        private Folder Find(string server) => Folders.Single(f => f.Server == server);

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
            return Task.FromResult<IReadOnlyList<MailFolderInfo>>([.. Folders.Select(f => new MailFolderInfo(f.Server, f.Local, '/', f.Special, f.Validity, f.Messages.Count, 0))]);
        }

        public Task<MailSyncResult> SyncAsync(string pluginId, MailAccount account, MailSyncRequest request, CancellationToken ct)
        {
            Count(account);
            SyncRequests.Add((request.Folder, request.Limit));
            var folder = Find(request.Folder);
            var changed = request.UidValidity != 0 && request.UidValidity != folder.Validity;
            var lastUid = changed ? 0 : request.LastUid;
            var known = changed ? [] : request.KnownUids.ToHashSet();
            var fresh = folder.Messages.Where(m => m.Key > lastUid && !known.Contains(m.Key))
                .Select(m => new MailHeader(m.Key, DateTimeOffset.UtcNow, "Jan <jan@example.org>", $"Temat {m.Key}", m.Value, 10, FileName(m.Key)))
                .ToList();
            var hasMore = request.Limit is { } limit && fresh.Count > limit;
            if (hasMore)
            {
                fresh = fresh.Take(request.Limit!.Value).ToList();
            }

            var removed = known.Where(u => !folder.Messages.ContainsKey(u)).Order().ToList();
            var flags = known.Where(folder.Messages.ContainsKey).Order().Select(u => (u, folder.Messages[u])).ToList();
            return Task.FromResult(new MailSyncResult(folder.Validity, changed, 0, fresh, flags, removed, hasMore));
        }

        public async Task<long> FetchAsync(string pluginId, MailAccount account, string folder, uint uid, Stream destination, CancellationToken ct)
        {
            Count(account);
            Fetches++;
            var bytes = Encoding.ASCII.GetBytes($"Subject: Temat {uid}\r\n\r\nbody");
            await destination.WriteAsync(bytes, ct);
            return bytes.Length;
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
            foreach (var uid in uids)
            {
                Find(folder).Messages.Remove(uid);
                Deleted.Add(uid);
            }

            return Task.CompletedTask;
        }

        public Task<uint?> AppendAsync(string pluginId, MailAccount account, string folder, Stream message, MailFlags flags, CancellationToken ct)
        {
            Count(account);
            var uid = Find(folder).Add(flags);
            Appended.Add(uid);
            return Task.FromResult<uint?>(uid);
        }

        public Task<string> SendAsync(string pluginId, MailAccount account, Stream message, CancellationToken ct)
        {
            Count(account);
            return SendFailure is { } failure ? Task.FromException<string>(failure) : Task.FromResult("<x@test>");
        }
    }

    private static Dictionary<string, string> Answers(params (string Id, string Value)[] values) =>
        values.ToDictionary(v => v.Id, v => v.Value, StringComparer.Ordinal);

    private sealed class RecordingUi : IPluginUi
    {
        public List<(string Message, string? Target)> Notifications { get; } = [];

        public string? Active { get; set; }

        public string? Other { get; set; }

        public IReadOnlyList<VfsEntry> Selection { get; set; } = [];

        public string? ActivePath => Active;

        public string? OtherPath => Other;

        public IReadOnlyList<VfsEntry> SelectedEntries => Selection;

        public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

        public void Notify(string message, bool isError, string? target)
        {
            lock (Notifications)
            {
                Notifications.Add((message, target));
            }
        }

        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;

        public bool ConfirmAnswer { get; set; } = true;

        public List<(string Title, string Message)> Confirmations { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Confirmations.Add((title, message));
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult<string?>(null);

        /// <summary>Answers of the next <c>ui.dialog</c> windows; an empty queue = Cancel.</summary>
        public Queue<IReadOnlyDictionary<string, string>> Dialogs { get; } = new();

        public List<DialogRequest> DialogRequests { get; } = [];

        public Task<IReadOnlyDictionary<string, string>?> ShowDialogAsync(DialogRequest request)
        {
            DialogRequests.Add(request);
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(Dialogs.TryDequeue(out var answers) ? answers : null);
        }

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
