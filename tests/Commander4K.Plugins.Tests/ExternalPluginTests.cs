using System.Net;
using System.Net.Sockets;
using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Plugins;
using Commander4K.Core.Updates;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

/// <summary>Runs the example plugins from examples/plugins through the real protocol with the real runtimes.</summary>
public sealed class ExternalPluginTests : IAsyncLifetime
{
    private static readonly string Examples = Path.Combine(RepositoryRoot(), "examples", "plugins");

    private readonly string _folder = Directory.CreateTempSubdirectory("c4k-ext-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly FakeUi _ui = new();
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly HttpClient _http = new();
    private readonly PluginStorage _storage;
    private IReadOnlyList<RuntimeInfo> _runtimes = [];

    public ExternalPluginTests() => _storage = new PluginStorage(Path.Combine(_folder, "plugin-data"));

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _runtimes = await RuntimeCatalog.DetectAsync(TestToken);
        File.WriteAllBytes(Path.Combine(_folder, "a.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_folder, "b.txt"), new byte[50]);
        File.WriteAllBytes(Path.Combine(_folder, "c.log"), new byte[10]);
        _ui.ActivePath = _folder;
    }

    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync();
        _http.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Python_plugin_reads_through_host_and_is_denied_writes()
    {
        await using var plugin = Start("folder-stats-python");
        var command = new ExternalCommandPlugin(plugin);
        var host = HostFor(plugin);

        await command.ExecuteAsync("summary", host, TestToken);
        Assert.Contains(".txt", _ui.LastText, StringComparison.Ordinal);
        Assert.Contains("2 plików", _ui.LastText, StringComparison.Ordinal);

        await command.ExecuteAsync("try-write", host, TestToken);
        Assert.Contains("Zablokowano", _ui.LastNotification, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_folder, "c4k-test.txt")));
        Assert.Contains(_audit.Recent("examples.folder-stats"), e => e.Operation == "fs.write" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(_audit.Recent("examples.folder-stats"), e => e.Operation == "fs.list" && e.Outcome == AuditOutcome.Allowed);

        var panel = new ExternalPanelPlugin(plugin, plugin.Manifest.Contributes!.Panels![0]);
        var session = panel.CreateSession(new PanelRequest(null, null), host);
        var snapshot = await session.LoadAsync(new PanelRequest(null, null), null, TestToken);
        Assert.Equal(["Rozszerzenie", "Pliki", "Rozmiar"], snapshot.Columns.Select(c => c.Header));
        Assert.Equal(".txt", snapshot.Rows[0].Id);
        var details = await session.GetDetailsAsync(".txt", TestToken);
        Assert.Equal("a.txt", details!.Sections[0].Items[0].Key);
    }

    [Fact]
    public async Task Python_plugin_renders_csv_quick_view_as_markdown()
    {
        await using var plugin = Start("folder-stats-python");
        var csvPath = Path.Combine(_folder, "dane.csv");
        await File.WriteAllTextAsync(csvPath, "miasto;ludność\nKraków;800000\nGdańsk;470000\n", TestToken);
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var entry = new VfsEntry("dane.csv", csvPath, VfsEntryKind.File, 40);

        Assert.True(preview.CanPreview(entry));
        Assert.False(preview.CanPreview(entry with { Name = "x.txt" }));
        var result = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, HostFor(plugin), null, TestToken));

        Assert.Contains("| miasto | ludność |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| Gdańsk | 470000 |", result.Markdown, StringComparison.Ordinal);
        Assert.False(result.CanEdit);
    }

    [Fact]
    public async Task Project_modules_preview_shows_dependencies_and_api_modules()
    {
        await using var plugin = Start("project-modules-python");
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var host = HostFor(plugin);
        async Task<string> Render(string name, string content)
        {
            var path = Path.Combine(_folder, name);
            await File.WriteAllTextAsync(path, content, TestToken);
            var entry = new VfsEntry(name, path, VfsEntryKind.File, content.Length);
            Assert.True(preview.CanPreview(entry), name);
            return Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;
        }

        var npm = await Render("package.json", """{ "name": "app", "version": "1.2.0", "dependencies": { "react": "^19.0.0" }, "scripts": { "build": "vite build" } }""");
        Assert.Contains("| react | ^19.0.0 |", npm, StringComparison.Ordinal);
        Assert.Contains("| build | vite build |", npm, StringComparison.Ordinal);

        var csproj = await Render("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><PackageReference Include="Serilog" Version="4.0.0" /><ProjectReference Include="..\Core\Core.csproj" /></ItemGroup></Project>
            """);
        Assert.Contains("net10.0", csproj, StringComparison.Ordinal);
        Assert.Contains("| Serilog | 4.0.0 |", csproj, StringComparison.Ordinal);

        var api = await Render("openapi.json", """
            { "openapi": "3.1.0", "info": { "title": "Shop", "version": "2" },
              "paths": { "/orders": { "get": { "tags": ["orders"], "summary": "List orders" }, "post": { "tags": ["orders"] } },
                         "/users/{id}": { "get": { "tags": ["users"], "summary": "User" } } } }
            """);
        Assert.Contains("2 modułów, 3 operacji", api, StringComparison.Ordinal);
        Assert.Contains("## Moduł `orders` (2)", api, StringComparison.Ordinal);
        Assert.Contains("| GET | /orders | List orders |", api, StringComparison.Ordinal);

        Assert.False(preview.CanPreview(new VfsEntry("notes.txt", "x", VfsEntryKind.File)));
        Assert.True(preview.CanPreview(new VfsEntry("requirements-dev.txt", "x", VfsEntryKind.File)));
        Assert.Contains(_audit.Recent("examples.project-modules"), e => e.Operation == "fs.read" && e.Outcome == AuditOutcome.Allowed);
    }

    [Fact]
    public async Task Sdk_covers_files_processes_system_storage_clipboard_and_http()
    {
        // A throw-away plugin that uses every new SDK helper; the app does the work and audits it.
        var folder = Path.Combine(_folder, "sdk-tour");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(RepositoryRoot(), "plugins-sdk", "python", "c4k.py"), Path.Combine(folder, "c4k.py"));
        File.WriteAllText(Path.Combine(folder, ManifestReader.FileName), """
            { "id": "tests.sdk-tour", "name": "SDK tour", "version": "1.0.0", "author": "t", "description": "d",
              "runtime": { "type": "python", "entry": "main.py", "version": ">=3.9" },
              "permissions": ["fs.read", "fs.write", "process.read", "system.info", "net"],
              "contributes": { "commands": [ { "id": "tour", "title": "Tour", "category": "T" } ] } }
            """);
        File.WriteAllText(Path.Combine(folder, "main.py"), """
            import c4k

            @c4k.handler("command.execute")
            def tour(params):
                base = c4k.context()["activePath"]
                work = base + "\\work"
                c4k.make_dir(work)
                c4k.write_bytes(work + "\\data.bin", bytes(range(10)))
                head = c4k.read_bytes(work + "\\data.bin", 2, 3)
                c4k.move(work + "\\data.bin", work + "\\moved.bin")
                info = c4k.stat(work + "\\moved.bin")
                missing = c4k.stat(work + "\\data.bin")
                c4k.remove(work, recursive=True)
                processes = c4k.list_processes()
                system = c4k.system_info()
                c4k.storage_set("count", {"n": 3})
                stored = c4k.storage_get("count")
                c4k.copy_text("copied")
                response = c4k.http_request(c4k.prompt("url", "url"), method="POST", headers={"X-Test": "1"}, body='{"a":1}')
                lines = [f"head={list(head)}", f"size={info['size']}", f"missing={missing}", f"gone={c4k.stat(work)}",
                         f"processes={len(processes) > 5}", f"processors={system['processors'] > 0}",
                         f"stored={stored['n']}", f"status={response['status']}", f"body={response['body']}"]
                return {"text": {"title": "tour", "content": "\n".join(lines)}}

            c4k.serve()
            """);
        await using var plugin = StartFrom(folder);
        using var server = new LocalHttpServer("pong");

        var command = new ExternalCommandPlugin(plugin);
        _ui.PromptAnswer = server.Url;
        await command.ExecuteAsync("tour", HostFor(plugin), TestToken);

        Assert.Equal(
            "head=[2, 3, 4]\nsize=10\nmissing=None\ngone=None\nprocesses=True\nprocessors=True\nstored=3\nstatus=200\nbody=pong",
            _ui.LastText);
        Assert.Equal("copied", _ui.LastCopied);
        Assert.False(Directory.Exists(Path.Combine(_folder, "work")));
        var audit = _audit.Recent("tests.sdk-tour").Select(e => e.Operation).ToHashSet();
        Assert.Superset(new HashSet<string> { "fs.mkdir", "fs.write", "fs.read", "fs.move", "fs.stat", "fs.delete", "process.list", "system.info", "net.request", "storage.set", "ui.copy" }, audit);
    }

    [Fact]
    public async Task New_api_calls_are_gated_by_permissions()
    {
        var host = HostFor(new PluginManifest("acme.none", "None", "1.0.0", "a", "d", []));

        await Assert.ThrowsAsync<PluginPermissionException>(() => host.ListProcessesAsync(TestToken));
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.GetSystemInfoAsync(TestToken));
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.DeleteAsync(_folder, true, TestToken));
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.HttpRequestAsync(new HttpRequestSpec("GET", new Uri("http://127.0.0.1/")), TestToken));
        Assert.True(Directory.Exists(_folder));
        Assert.Equal(4, _audit.Recent("acme.none").Count(e => e.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task Node_plugin_uses_net_permission_against_local_server()
    {
        await using var plugin = Start("site-check-node");
        using var server = new LocalHttpServer("<html><title>Witaj C4K</title></html>");
        _ui.PromptAnswer = server.Url;

        await new ExternalCommandPlugin(plugin).ExecuteAsync("check-url", HostFor(plugin), TestToken);

        Assert.Contains("Tytuł: Witaj C4K", _ui.LastText, StringComparison.Ordinal);
        Assert.Contains(_audit.Recent("examples.site-check"), e => e.Operation == "net.get" && e.Detail.StartsWith(server.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Node_plugin_palette_lists_adds_invokes_and_removes_items()
    {
        // The site list lives in the plugin's storage (storage.get/set), not in its folder.
        await using var plugin = Start("site-check-node");
        using var server = new LocalHttpServer("<html><title>Paleta C4K</title></html>");
        var palette = new ExternalPalettePlugin(plugin, plugin.Manifest.Contributes!.Palettes![0]);
        var host = HostFor(plugin);
        Assert.Equal("examples.site-check/sites", palette.Id);
        Assert.Equal(PaletteDock.Right, palette.DefaultDock);
        Assert.Contains(palette.Actions, a => a.Id == "remove" && a.ForItem);

        var before = await palette.GetItemsAsync(host, TestToken);
        _ui.PromptAnswer = server.Url;
        await palette.InvokeActionAsync("add", null, host, TestToken);
        var added = Assert.Single(await palette.GetItemsAsync(host, TestToken), i => i.Id == server.Url);
        Assert.Equal(before.Count + 1, (await palette.GetItemsAsync(host, TestToken)).Count);
        Assert.StartsWith("127.0.0.1:", added.Title, StringComparison.Ordinal);

        await palette.InvokeAsync(added.Id, host, TestToken);
        Assert.Contains("OK", _ui.LastNotification, StringComparison.Ordinal);
        Assert.Contains("Paleta C4K", _ui.LastNotification, StringComparison.Ordinal);

        await palette.InvokeActionAsync("remove", added.Id, host, TestToken);
        Assert.DoesNotContain(await palette.GetItemsAsync(host, TestToken), i => i.Id == server.Url);
        Assert.DoesNotContain(server.Url, await File.ReadAllTextAsync(_storage.FileFor("examples.site-check"), TestToken), StringComparison.Ordinal);
        Assert.Contains(_audit.Recent("examples.site-check"), e => e.Operation == "storage.set");
    }

    [Fact]
    public async Task Browser_calls_allow_only_http_and_are_audited()
    {
        var host = HostFor(new PluginManifest("acme.web", "Web", "1.0.0", "a", "d", []));

        await host.OpenUrlAsync("https://example.com/a");
        Assert.Equal("https://example.com/a", _ui.LastUrl);
        await Assert.ThrowsAsync<ArgumentException>(() => host.OpenUrlAsync("file:///C:/Windows/win.ini"));
        await host.SearchWebAsync("błąd 0x80070005", "assistant");

        Assert.Equal("search:assistant:błąd 0x80070005", _ui.LastUrl);
        var audit = _audit.Recent("acme.web");
        Assert.Contains(audit, e => e.Operation == "ui.openUrl" && e.Outcome == AuditOutcome.Denied && e.Detail.StartsWith("file:", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Operation == "ui.search" && e.Detail.Contains("0x80070005", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PowerShell_plugin_runs_git_through_host()
    {
        Assert.SkipWhen(!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(d => File.Exists(Path.Combine(d.Trim(), "git.exe"))), "git is not installed");
        _ui.ActivePath = RepositoryRoot();
        await using var plugin = Start("git-powershell");
        var panel = new ExternalPanelPlugin(plugin, plugin.Manifest.Contributes!.Panels![0]);

        var snapshot = await panel.CreateSession(new PanelRequest(null, null), HostFor(plugin)).LoadAsync(new PanelRequest(null, null), null, TestToken);

        Assert.NotEmpty(snapshot.Rows);
        Assert.Equal(4, snapshot.Rows[0].Cells.Count);
        Assert.Contains(_audit.Recent("examples.git"), e => e.Operation == "shell.run" && e.Detail.StartsWith("git ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Steam_catalog_plugin_lists_games_as_tiles_with_covers_and_as_a_table()
    {
        // A fake Steam: steamapps with two games (one in a second library from libraryfolders.vdf) and a cover.
        var steam = Path.Combine(_folder, "Steam");
        var steamapps = Path.Combine(steam, "steamapps");
        var other = Path.Combine(_folder, "Library2", "steamapps");
        Directory.CreateDirectory(steamapps);
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(Path.Combine(steam, "appcache", "librarycache", "570"));
        await File.WriteAllBytesAsync(Path.Combine(steam, "appcache", "librarycache", "570", "header.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], TestToken);
        await File.WriteAllTextAsync(Path.Combine(steamapps, "appmanifest_570.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"570\"\n\t\"name\"\t\t\"Dota 2\"\n\t\"installdir\"\t\t\"dota 2 beta\"\n\t\"LastUpdated\"\t\t\"1700000000\"\n\t\"SizeOnDisk\"\t\t\"40000000000\"\n}\n", TestToken);
        await File.WriteAllTextAsync(Path.Combine(other, "appmanifest_620.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"620\"\n\t\"name\"\t\t\"Portal 2\"\n\t\"SizeOnDisk\"\t\t\"12000000000\"\n}\n", TestToken);
        await File.WriteAllTextAsync(Path.Combine(steamapps, "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{steam.Replace("\\", "\\\\")}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{Path.GetDirectoryName(other)!.Replace("\\", "\\\\")}\"\n\t}}\n}}\n", TestToken);

        await using var plugin = StartFrom(Path.Combine(RepositoryRoot(), "plugins-catalog", "steam-library-powershell"));
        var host = HostFor(plugin);
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var folder = new VfsEntry("steamapps", steamapps, VfsEntryKind.Directory);

        var tiles = Assert.IsType<TilesPreview>(await preview.LoadWithOptionsAsync(folder, host, "{ \"sort\": \"size\" }", TestToken));

        Assert.Equal(["Dota 2", "Portal 2"], tiles.Tiles.Select(t => t.Title));
        Assert.Equal("steam://rungameid/570", tiles.Tiles[0].Target);
        Assert.Equal(7, tiles.Tiles[0].Image!.Value.Length);
        Assert.Null(tiles.Tiles[1].Image);
        Assert.Contains("2 gier", tiles.Title, StringComparison.Ordinal);

        var panel = new ExternalPanelPlugin(plugin, plugin.Manifest.Contributes!.Panels![0]);
        var request = new PanelRequest(steam, "portal", "{ \"allLibraries\": true }");
        var snapshot = await panel.CreateSession(request, host).LoadAsync(request, null, TestToken);
        var row = Assert.Single(snapshot.Rows);
        Assert.Equal("620", row.Id);
        Assert.Equal("Portal 2", row.Cells[0]);
        Assert.All(_audit.Recent("official.steam-library"), e => Assert.NotEqual(AuditOutcome.Denied, e.Outcome));
    }

    [Fact]
    public async Task Folder_views_show_a_gallery_and_a_dashboard()
    {
        var photos = Path.Combine(_folder, "photos");
        Directory.CreateDirectory(photos);
        await File.WriteAllBytesAsync(Path.Combine(photos, "b.png"), [0x89, 0x50, 0x4E, 0x47], TestToken);
        await File.WriteAllBytesAsync(Path.Combine(photos, "a.jpg"), [0xFF, 0xD8], TestToken);
        await File.WriteAllTextAsync(Path.Combine(photos, "notes.txt"), "x", TestToken);
        await File.WriteAllTextAsync(Path.Combine(photos, Core.Folders.FolderConfig.FileName), """
            { "title": "Wakacje", "preview": "gallery",
              "enter": { "view": "dashboard", "tiles": [ { "title": "Logi", "open": "logs" }, { "title": "Build", "run": "build.ps1" } ] } }
            """, TestToken);
        var host = HostFor(new PluginManifest("builtin.preview.folder", "Folder", "1.0.0", "a", "d", [PluginPermissions.FileRead]));
        var config = await Core.Folders.FolderConfig.ReadAsync(_router, photos, TimeSpan.FromSeconds(5), TestToken);
        var folder = new VfsEntry("photos", photos, VfsEntryKind.Directory);

        var gallery = Assert.IsType<TilesPreview>(await BuiltIn.Previews.FolderViews.LoadAsync(config!, config!.Preview!, "preview", folder, host, null, TestToken));
        Assert.Equal("Wakacje", gallery.Title);
        Assert.Equal(["a.jpg", "b.png"], gallery.Tiles.Select(t => t.Title));
        Assert.Equal(4, gallery.Tiles[1].Image!.Value.Length);
        Assert.Equal(Path.Combine(photos, "a.jpg"), gallery.Tiles[0].Target);

        var dashboard = Assert.IsType<TilesPreview>(await BuiltIn.Previews.FolderViews.LoadAsync(config, config.Enter!, "enter", folder, host, null, TestToken));
        Assert.Equal([Path.Combine(photos, "logs"), "c4k:run/enter/1"], dashboard.Tiles.Select(t => t.Target));

        Assert.Null(await Core.Folders.FolderConfig.ReadAsync(_router, _folder, TimeSpan.FromSeconds(5), TestToken));
    }

    [Fact]
    public void Manifest_validation_rejects_traversal_unknown_permissions_and_reserved_ids()
    {
        var folder = Path.Combine(_folder, "bad");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ManifestReader.FileName), """
            { "id": "builtin.evil", "name": "x", "version": "1.0", "author": "a", "description": "d",
              "runtime": { "type": "python", "entry": "..\\..\\outside.py" },
              "permissions": ["fs.read", "root.everything"], "contributes": {} }
            """);

        var discovered = ManifestReader.Read(folder);

        Assert.False(discovered.IsValid);
        Assert.Contains(discovered.Errors, e => e.Contains("zarezerwowany", StringComparison.Ordinal));
        Assert.Contains(discovered.Errors, e => e.Contains("wewnątrz folderu", StringComparison.Ordinal));
        Assert.Contains(discovered.Errors, e => e.Contains("root.everything", StringComparison.Ordinal));
        Assert.Contains(discovered.Errors, e => e.Contains("nic nie dodaje", StringComparison.Ordinal));
        Assert.All(ManifestReader.Discover([Examples]), example => Assert.True(example.IsValid, string.Join("; ", example.Errors)));
    }

    [Theory]
    [InlineData("folder-stats-python", "python", "c4k.py")]
    [InlineData("site-check-node", "node", "c4k.js")]
    [InlineData("git-powershell", "powershell", "c4k.ps1")]
    [InlineData("project-modules-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/steam-library-powershell", "powershell", "c4k.ps1")]
    public void Example_sdk_clients_match_plugins_sdk(string example, string language, string file) =>
        Assert.Equal(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "plugins-sdk", language, file)),
            File.ReadAllText(Path.Combine(Examples, example, file)));

    [Theory]
    [InlineData("3.12.10", ">=3.9", true)]
    [InlineData("3.8.0", ">=3.9", false)]
    [InlineData("24.20.0", ">=18 <25", true)]
    [InlineData("25.0.0", ">=18 <25", false)]
    [InlineData("7.6.7", "^7", true)]
    [InlineData("8.0.0", "^7", false)]
    [InlineData("5.1.0", "7.2", false)]
    public void Runtime_version_ranges(string version, string range, bool expected) =>
        Assert.Equal(expected, RuntimeCatalog.Satisfies(SemanticVersion.Parse(version), range));

    [Fact]
    public void Approvals_require_new_consent_when_permissions_grow()
    {
        var store = new PluginApprovalStore(Path.Combine(_folder, "approvals.json"));
        var manifest = new PluginManifest("acme.tool", "Tool", "1.0.0", "a", "d", [PluginPermissions.FileRead]);
        Assert.False(store.IsApproved(manifest));

        store.Approve(manifest);
        Assert.True(new PluginApprovalStore(Path.Combine(_folder, "approvals.json")).IsApproved(manifest));
        Assert.False(store.IsApproved(manifest with { Version = "1.1.0", Permissions = [PluginPermissions.FileRead, PluginPermissions.ShellRun] }));

        store.Disable(manifest.Id);
        Assert.False(store.IsApproved(manifest));
    }

    private ExternalPlugin Start(string example) => StartFrom(Path.Combine(Examples, example));

    private ExternalPlugin StartFrom(string folder)
    {
        var discovered = ManifestReader.Read(folder);
        Assert.True(discovered.IsValid, string.Join("; ", discovered.Errors));
        var missing = RuntimeCatalog.CheckRequirement(discovered.Manifest!.Runtime, _runtimes);
        Assert.SkipWhen(missing is not null, missing ?? string.Empty);
        var runtime = _runtimes.First(r => r.Type == discovered.Manifest.Runtime.Type);
        return new ExternalPlugin(discovered, runtime, HostFor(discovered.Manifest), _audit, _storage);
    }

    private ScopedPluginHost HostFor(ExternalPlugin plugin) => HostFor(plugin.Manifest);

    private ScopedPluginHost HostFor(PluginManifest manifest) => new(manifest, _ui, _router, _http, _audit);

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Commander4K.sln")))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return directory;
    }

    private sealed class FakeUi : IPluginUi
    {
        public string? ActivePath { get; set; }

        public string? OtherPath => null;

        public IReadOnlyList<VfsEntry> SelectedEntries => [];

        public string LastText { get; private set; } = string.Empty;

        public string LastNotification { get; private set; } = string.Empty;

        public string? PromptAnswer { get; set; }

        public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

        public void Notify(string message, bool isError) => LastNotification = message;

        public Task ShowTextAsync(string title, string text)
        {
            LastText = text;
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult(PromptAnswer);

        public Task EditFileAsync(string path) => Task.CompletedTask;

        public Task OpenPanelAsync(string panelPluginId, string? argument) => Task.CompletedTask;

        public string? LastUrl { get; private set; }

        public Task OpenUrlAsync(string url)
        {
            LastUrl = url;
            return Task.CompletedTask;
        }

        public Task SearchWebAsync(string query, string? engine)
        {
            LastUrl = $"search:{engine}:{query}";
            return Task.CompletedTask;
        }

        public string? LastCopied { get; private set; }

        public Task CopyTextAsync(string text)
        {
            LastCopied = text;
            return Task.CompletedTask;
        }
    }

    private sealed class LocalHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public LocalHttpServer(string body)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        var bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }
                }
            });
        }

        public string Url { get; }

        public void Dispose() => _listener.Close();

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }
}
