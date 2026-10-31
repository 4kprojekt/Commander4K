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
        File.WriteAllText(Path.Combine(_root, "photos", FolderConfig.FileName), """{ "preview": "gallery" }""");
        Directory.CreateDirectory(Path.Combine(_root, "servers"));
        File.WriteAllText(Path.Combine(_root, "servers", FolderConfig.FileName), """
            { "title": "Serwery", "enter": { "view": "dashboard", "tiles": [ { "title": "VPS", "open": "x" } ] } }
            """);
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        File.WriteAllText(Path.Combine(_root, "plain", "a.txt"), "x");

        _resolver = new CardResolver(
            [_folder, new MarkdownPreviewPlugin(), new TextPreviewPlugin()],
            manifest => new ScopedPluginHost(manifest, new NullPluginUi(), _router, _http, _audit),
            (path, ct) => FolderConfig.ReadAsync(_router, path, TimeSpan.FromSeconds(2), ct),
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
        var config = Dashboard("""{ "title": "Plain", "open": "plain" }""");

        Assert.Null(await _resolver.ResolveAsync(config, config.Preview!.Tiles[0], TestToken));
    }

    [Fact]
    public async Task Auto_card_follows_target_c4k_dir_preview()
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

        Assert.Equal("Serwery", card.Title);
        Assert.Equal("VPS", Assert.Single(card.Items.OfType<CardText>()).Text);
    }

    [Fact]
    public async Task Named_card_ignores_target_c4k_dir()
    {
        var card = await LoadAsync("""{ "title": "Foto", "open": "photos", "card": "files" }""");

        Assert.Empty(card.Items.OfType<CardThumbnails>());
        Assert.Contains(card.Items.OfType<CardValue>(), v => v.Label == "Pliki");
    }

    [Fact]
    public async Task Post_api_and_ssh_need_trust_get_does_not()
    {
        var config = Dashboard("""
            { "title": "Get", "card": "api", "api": { "url": "https://example.com/a" } },
            { "title": "Post", "card": "api", "api": { "url": "https://example.com/a", "method": "POST" } },
            { "title": "Ssh", "open": "sftp://kamil@vps.example.com/", "card": "ssh" }
            """);

        var sources = await Task.WhenAll(config.Preview!.Tiles.Select(t => _resolver.ResolveAsync(config, t, TestToken)));

        Assert.Equal([false, true, true], sources.Select(s => s!.NeedsTrust));
        Assert.All(sources, s => Assert.Null(s!.Unavailable));
    }

    [Fact]
    public async Task Side_effect_request_from_a_nested_c4k_dir_never_runs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "nested", FolderConfig.FileName), """
            { "preview": { "view": "api", "api": { "url": "http://127.0.0.1:9/delete", "method": "DELETE" } } }
            """);
        var config = Dashboard("""{ "title": "N", "open": "nested", "card": true }""");

        var source = await _resolver.ResolveAsync(config, config.Preview!.Tiles[0], TestToken);

        Assert.False(string.IsNullOrEmpty(source!.Unavailable));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"files\"")]
    [InlineData("\"official.steam-library\"")]
    public async Task Cards_on_remote_logins_need_trust_and_do_not_connect(string card)
    {
        var config = Dashboard($$"""{ "title": "R", "open": "sftp://kamil@192.0.2.1/home", "card": {{card}} }""");

        var source = await _resolver.ResolveAsync(config, config.Preview!.Tiles[0], TestToken).WaitAsync(TimeSpan.FromSeconds(2), TestToken);

        Assert.True(source!.NeedsTrust);
    }

    [Fact]
    public async Task Ssh_card_on_sftp_folder_config_is_unavailable()
    {
        var config = FolderConfig.Parse("sftp://kamil@vps.example.com/home", """
            { "preview": { "view": "dashboard", "tiles": [ { "title": "Ssh", "open": "sftp://kamil@vps.example.com/", "card": "ssh" } ] } }
            """);

        var source = await _resolver.ResolveAsync(config, config.Preview!.Tiles[0], TestToken);

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
        var config = Dashboard("""{ "title": "Docs", "url": "https://example.com" }, { "title": "Foto", "open": "photos", "card": true }""");

        var dashboard = FolderViews.Dashboard(config, config.Preview!, "preview");

        Assert.Equal(2, dashboard.Slots.Count);
        Assert.Equal("https://example.com", dashboard.Slots[0].Shortcut.Target);
        Assert.Equal("🌐", dashboard.Slots[0].Shortcut.Icon);
        Assert.Equal(Path.Combine(_root, "photos"), dashboard.Slots[1].Shortcut.Target);
        Assert.Equal(FolderTile.AutoCard, dashboard.Slots[1].Tile.Card);
    }

    private FolderConfig Dashboard(string tiles) =>
        FolderConfig.Parse(_root, $$"""{ "preview": { "view": "dashboard", "tiles": [ {{tiles}} ] } }""");

    private async Task<PreviewCard> LoadAsync(string tile)
    {
        var config = Dashboard(tile);
        var source = await _resolver.ResolveAsync(config, config.Preview!.Tiles[0], TestToken);
        return await source!.Load(null, TestToken);
    }
}

internal sealed class NullPluginUi : IPluginUi
{
    public string? ActivePath => null;

    public string? OtherPath => null;

    public IReadOnlyList<VfsEntry> SelectedEntries => [];

    public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

    public void Notify(string message, bool isError)
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
