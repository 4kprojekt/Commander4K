using Commander4K.Core.Execution;
using Commander4K.Core.Folders;
using Commander4K.Core.Plugins;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Dashboard;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Previews;
using Commander4K.Plugins.BuiltIn.Remote;

namespace Commander4K.Plugins.Tests;

/// <summary>Which preview draws a dashboard tile as a card.</summary>
public sealed class DashboardTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("c4k-dashboard-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly HttpClient _http = new();
    private readonly FolderPreviewPlugin _folder = new();
    private readonly CardResolver _resolver;

    public DashboardTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        File.WriteAllBytes(Path.Combine(_root, "photos", "a.png"), [137, 80, 78, 71]);
        File.WriteAllText(Path.Combine(_root, "photos", "photos.c4k"), """{ "view": "gallery" }""");
        Directory.CreateDirectory(Path.Combine(_root, "servers"));
        File.WriteAllText(Path.Combine(_root, "servers", "1 vps.c4k"), """{ "title": "VPS", "open": "x" }""");
        File.WriteAllText(Path.Combine(_root, "servers", "2 nas.c4k"), """{ "title": "NAS", "open": "y" }""");
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        File.WriteAllText(Path.Combine(_root, "plain", "a.txt"), "x");

        _resolver = new CardResolver(
            [_folder, new MarkdownPreviewPlugin(), new TextPreviewPlugin()],
            [new Commander4K.Plugins.BuiltIn.SystemInfo.SystemInfoPanelPlugin(), new Commander4K.Plugins.BuiltIn.TaskManager.TaskManagerPanelPlugin()],
            manifest => new ScopedPluginHost(manifest, new NullPluginUi(), _router, _http, _audit),
            (path, ct) => C4kReader.ReadAsync(_router, path, TimeSpan.FromSeconds(2), ct),
            id => id == "official.steam-library",
            new ApiCards(new Commander4K.Core.Security.SecretStore(Path.Combine(_root, "secrets.json"))),
            new SshMonitor(new RemoteTransferVfsPlugin(), _audit));
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync();
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Plain_tiles_resolve_to_null()
    {
        Assert.Null(await _resolver.ResolveAsync(Item("""{ "title": "Plain", "open": "plain" }"""), TestToken));
    }

    [Fact]
    public async Task Panel_plugins_are_widgets_by_id_and_through_a_folder_that_opens_them()
    {
        var system = await LoadAsync("""{ "title": "System", "card": "builtin.sysinfo" }""");
        Assert.Contains(system.Items, item => item is CardProgress { Label: "Pamięć" });
        Assert.Contains(system.Actions, action => action.Target == CardActions.OpenPanel("builtin.sysinfo"));

        Directory.CreateDirectory(Path.Combine(_root, "tasks"));
        File.WriteAllText(Path.Combine(_root, "tasks", "tasks.c4k"), """{ "panel": "builtin.taskmanager" }""");
        var tasks = await LoadAsync("""{ "title": "Procesy", "open": "tasks", "card": true }""");
        Assert.Contains(tasks.Items, item => item is CardValue { Label: "Procesy" });
    }

    [Fact]
    public async Task Note_widget_shows_inline_text()
    {
        var card = await LoadAsync("""{ "title": "Notatka", "card": "note", "size": "full", "options": { "text": "Kopia w piątek" } }""");

        Assert.Equal("Kopia w piątek", Assert.Single(card.Items.OfType<CardText>()).Text);
    }

    [Fact]
    public async Task Note_widget_reads_the_file_in_open()
    {
        File.WriteAllText(Path.Combine(_root, "plain", "todo.md"), "- mleko\r\n- chleb\r\n");

        var card = await LoadAsync("""{ "title": "Zakupy", "card": "note", "open": "plain/todo.md" }""");

        Assert.Equal("- mleko\n- chleb", Assert.Single(card.Items.OfType<CardText>()).Text);
    }

    [Fact]
    public void Clock_widget_converts_to_the_named_zone()
    {
        var tile = Item("""{ "title": "Tokio", "card": "clock" }""");
        using var options = System.Text.Json.JsonDocument.Parse("""{ "timeZone": "Asia/Tokyo" }""");

        var card = WidgetCards.ClockCard(tile, options.RootElement.Clone(), new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("21:00", card.Title);
        Assert.Equal("44", card.Items.OfType<CardValue>().Single(v => v.Label == "Tydzień").Value);
    }

    [Fact]
    public async Task Auto_card_follows_target_c4k_view()
    {
        var card = await LoadAsync("""{ "title": "Foto", "open": "photos", "card": true }""");

        Assert.Single(card.Items.OfType<CardThumbnails>());
    }

    [Fact]
    public async Task Auto_card_on_plain_folder_counts_files()
    {
        var card = await LoadAsync("""{ "title": "Plain", "open": "plain", "card": true }""");

        Assert.Equal("1", card.Items.OfType<CardValue>().Single(v => v.Label == "Pliki").Value);
    }

    [Fact]
    public async Task Auto_card_on_dashboard_folder_gives_dashboard_card()
    {
        var card = await LoadAsync("""{ "title": "S", "open": "servers", "card": true }""");

        Assert.Equal("servers", card.Title);
        Assert.Equal("VPS · NAS", Assert.Single(card.Items.OfType<CardText>()).Text);
    }

    [Fact]
    public async Task Named_card_ignores_target_c4k()
    {
        var card = await LoadAsync("""{ "title": "Foto", "open": "photos", "card": "files" }""");

        Assert.Empty(card.Items.OfType<CardThumbnails>());
        Assert.Contains(card.Items.OfType<CardValue>(), v => v.Label == "Pliki");
    }

    [Fact]
    public async Task Post_api_and_ssh_need_trust_get_does_not()
    {
        var items = new[]
        {
            Item("""{ "title": "Get", "card": "api", "api": { "url": "https://example.com/a" } }"""),
            Item("""{ "title": "Post", "card": "api", "api": { "url": "https://example.com/a", "method": "POST" } }"""),
            Item("""{ "title": "Ssh", "open": "sftp://kamil@vps.example.com/", "card": "ssh" }"""),
        };

        var sources = await Task.WhenAll(items.Select(t => _resolver.ResolveAsync(t, TestToken)));

        Assert.Equal([false, true, true], sources.Select(s => s!.NeedsTrust));
        Assert.All(sources, s => Assert.Null(s!.Unavailable));
    }

    [Fact]
    public async Task Side_effect_request_from_a_nested_c4k_never_runs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "nested", "api.c4k"), """
            { "view": "api", "api": { "url": "http://127.0.0.1:9/delete", "method": "DELETE" } }
            """);

        var source = await _resolver.ResolveAsync(Item("""{ "title": "N", "open": "nested", "card": true }"""), TestToken);

        Assert.False(string.IsNullOrEmpty(source!.Unavailable));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"files\"")]
    [InlineData("\"official.steam-library\"")]
    public async Task Cards_on_remote_logins_need_trust_and_do_not_connect(string card)
    {
        var item = Item($$"""{ "title": "R", "open": "sftp://kamil@192.0.2.1/home", "card": {{card}} }""");

        var source = await _resolver.ResolveAsync(item, TestToken).WaitAsync(TimeSpan.FromSeconds(2), TestToken);

        Assert.True(source!.NeedsTrust);
    }

    [Fact]
    public async Task Ssh_card_in_an_sftp_folder_is_unavailable()
    {
        var item = C4kItem.Parse("sftp://kamil@vps.example.com/home", "ssh.c4k", """{ "title": "Ssh", "open": "sftp://kamil@vps.example.com/", "card": "ssh" }""");

        var source = await _resolver.ResolveAsync(item, TestToken);

        Assert.False(string.IsNullOrEmpty(source!.Unavailable));
    }

    [Fact]
    public async Task Unknown_plugin_card_offers_install()
    {
        var install = await LoadAsync("""{ "title": "Steam", "card": "official.steam-library" }""");
        var unknown = await LoadAsync("""{ "title": "X", "card": "someone.unknown" }""");

        Assert.Equal("c4k:install/official.steam-library", Assert.Single(install.Actions).Target);
        Assert.Equal("c4k:plugins", Assert.Single(unknown.Actions).Target);
    }

    [Fact]
    public async Task Tile_errors_become_error_cards()
    {
        var card = await LoadAsync("""{ "title": "Bad", "open": "plain", "card": true, "refresh": "fast" }""");

        Assert.Equal(CardStatus.Error, card.Status);
        Assert.Contains("fast", card.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Dashboard_view_keeps_shortcuts_as_today()
    {
        var folder = Folder(("1.c4k", """{ "title": "Docs", "url": "https://example.com" }"""), ("2.c4k", """{ "title": "Foto", "open": "photos", "card": true }"""));

        var dashboard = FolderViews.Dashboard(folder);

        Assert.Equal(2, dashboard.Slots.Count);
        Assert.Equal("https://example.com", dashboard.Slots[0].Shortcut.Target);
        Assert.Equal("🌐", dashboard.Slots[0].Shortcut.Icon);
        Assert.Equal(Path.Combine(_root, "photos"), dashboard.Slots[1].Shortcut.Target);
        Assert.Equal(FolderTile.AutoCard, dashboard.Slots[1].Item.Card);
    }

    [Fact]
    public void Dashboard_slots_follow_item_order()
    {
        var folder = Folder(("10-c.c4k", """{ "title": "C", "url": "https://c" }"""), ("2-b.c4k", """{ "title": "B", "url": "https://b" }"""), ("1-a.c4k", """{ "title": "A", "url": "https://a" }"""));

        var dashboard = FolderViews.Dashboard(folder);

        Assert.Equal(["A", "B", "C"], dashboard.Slots.Select(s => s.Shortcut.Title));
        Assert.Equal([0, 1, 2], dashboard.Slots.Select(s => s.Index));
    }

    [Fact]
    public void Run_item_links_to_its_file()
    {
        var folder = Folder(("1.c4k", """{ "title": "Build", "run": { "command": "build.ps1" } }"""), ("2.c4k", """{ "title": "B", "url": "https://b" }"""));

        Assert.Equal(FolderViews.RunTarget(Path.Combine(_root, "1.c4k")), FolderViews.Dashboard(folder).Slots[0].Shortcut.Target);
    }

    [Fact]
    public void Skipped_files_show_notice()
    {
        var files = Enumerable.Range(0, C4kReader.MaxItems + 2).Select(i => ($"{i:D3}.c4k", """{ "url": "https://x" }""")).ToList();

        var dashboard = FolderViews.Dashboard(C4kReader.Build(_root, files));

        Assert.Equal(C4kReader.MaxItems, dashboard.Slots.Count);
        Assert.Contains("2", dashboard.Description!, StringComparison.Ordinal);
        Assert.Null(FolderViews.Dashboard(Folder(("1.c4k", "{}"), ("2.c4k", "{}"))).Description);
    }

    [Fact]
    public async Task Item_with_error_gets_error_card_and_others_live()
    {
        var folder = Folder(("1.c4k", """{ "title": "Bad", "open": "plain", "card": true, "refresh": "fast" }"""), ("2.c4k", """{ "title": "Notatka", "card": "note", "options": { "text": "żyje" } }"""));

        var bad = await (await _resolver.ResolveAsync(folder.Items[0], TestToken))!.Load(null, TestToken);
        var live = await (await _resolver.ResolveAsync(folder.Items[1], TestToken))!.Load(null, TestToken);

        Assert.Equal(CardStatus.Error, bad.Status);
        Assert.Equal("żyje", Assert.Single(live.Items.OfType<CardText>()).Text);
    }

    [Fact]
    public void Gallery_ignores_c4k_files()
    {
        VfsEntry Entry(string name) => new(name, Path.Combine(_root, name), VfsEntryKind.File, 10);
        var listing = new[] { Entry("a.md"), Entry("b.md"), Entry("c.md"), Entry("1.c4k"), Entry("2.c4k"), Entry("3.c4k"), Entry("4.c4k") };

        Assert.True(NotesOverview.LooksLikeNotes(listing));
        Assert.DoesNotContain(listing, FolderViews.IsImage);
    }

    [Fact]
    public async Task Dashboard_view_unknown_view_message_mentions_c4k()
    {
        var host = new ScopedPluginHost(_folder.Manifest, new NullPluginUi(), _router, _http, _audit);
        using var thumbnails = new ThumbnailStore();

        var content = await FolderViews.LoadAsync(Item("""{ "view": "dashboard" }"""), new VfsEntry("x", _root, VfsEntryKind.Directory), host, thumbnails, null, TestToken);

        Assert.Contains(".c4k", Assert.IsType<MessagePreview>(content).Message, StringComparison.Ordinal);
    }

    private C4kItem Item(string json) => C4kItem.Parse(_root, "item.c4k", json);

    private C4kFolder Folder(params (string, string)[] files) => C4kReader.Build(_root, files);

    private async Task<PreviewCard> LoadAsync(string tile)
    {
        var source = await _resolver.ResolveAsync(Item(tile), TestToken);
        return await source!.Load(null, TestToken);
    }
}

internal sealed class NullPluginUi : IPluginUi
{
    public string? ActivePath => null;

    public string? OtherPath => null;

    public IReadOnlyList<VfsEntry> SelectedEntries => [];

    public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

    public void Notify(string message, bool isError, string? target)
    {
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
