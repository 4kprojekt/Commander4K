using System.Text.Json;
using Commander4K.Core.Folders;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class FolderConfigTests
{
    [Fact]
    public void Names_are_views_or_plugins()
    {
        var config = FolderConfig.Parse(@"C:\Photos", """
            // comments are allowed
            { "preview": "gallery", "enter": "official.steam-library", }
            """);

        Assert.Null(config.Error);
        Assert.Equal(FolderViewIds.Gallery, config.Preview!.View);
        Assert.Equal(FolderActionKind.View, config.Preview.Kind);
        Assert.Equal("official.steam-library", config.Enter!.Plugin);
        Assert.False(config.Enter.IsPlainFiles);
    }

    [Fact]
    public void Ssh_dashboard_and_script_actions()
    {
        var config = FolderConfig.Parse(@"C:\Work\Server", """
            {
              "title": "Serwer",
              "enter": { "open": "sftp://kamil@beelink.lan/home/kamil", "keyFile": "~/.ssh/id_ed25519" },
              "preview": {
                "view": "dashboard",
                "tiles": [
                  { "title": "Logi", "open": "logs" },
                  { "title": "Build", "run": { "command": "build.ps1", "args": ["-Release"], "cwd": "src" } },
                  { "title": "Docs", "url": "https://example.com", "image": "..\\cover.png" }
                ]
              }
            }
            """);

        Assert.Equal("Serwer", config.Title);
        Assert.Equal(FolderActionKind.Open, config.Enter!.Kind);
        Assert.EndsWith(@".ssh\id_ed25519", config.Resolve(config.Enter.KeyFile!), StringComparison.Ordinal);
        Assert.Equal("sftp://kamil@beelink.lan/home/kamil", config.Resolve(config.Enter.Open!));

        var tiles = config.Preview!.Tiles;
        Assert.Equal(3, tiles.Count);
        Assert.Equal(@"C:\Work\Server\logs", config.Resolve(tiles[0].Open!));
        Assert.Equal(("build.ps1", "src", false), (tiles[1].Run!.Command, tiles[1].Run!.WorkingDirectory, tiles[1].Run!.Hidden));
        Assert.Equal("build.ps1 -Release", tiles[1].Run!.ToString());
        Assert.Equal(@"C:\Work\cover.png", config.Resolve(tiles[2].Image!));
    }

    [Theory]
    [InlineData("panel", "examples.git", "log", "examples.git/log")]
    [InlineData("panel", null, "builtin.sysinfo", "builtin.sysinfo")]
    [InlineData("panel", "x.y", "other.plugin/view", "other.plugin/view")]
    public void Panel_ids_are_qualified_with_the_plugin(string _, string? plugin, string panel, string expected) =>
        Assert.Equal(expected, new FolderAction { Plugin = plugin, Panel = panel }.PanelId);

    [Theory]
    [InlineData("{ \"enter\": 5 }")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"enter\": { \"run\": { \"args\": [] } } }")]
    [InlineData("{ broken")]
    public void Broken_files_report_an_error_instead_of_throwing(string json)
    {
        var config = FolderConfig.Parse(@"C:\X", json);

        Assert.NotNull(config.Error);
        Assert.Null(config.Enter);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\c4k.dir")]
    [InlineData(@"\\nas\media\films", @"\\nas\media\films\c4k.dir")]
    [InlineData("sftp://me@host/home/me", "sftp://me@host/home/me/c4k.dir")]
    [InlineData("zip://C:\\a.zip::/docs", "zip://C:\\a.zip::/docs/c4k.dir")]
    public void Child_paths_follow_the_location(string folder, string expected) =>
        Assert.Equal(expected, FolderConfig.ChildPath(folder, FolderConfig.FileName));

    [Fact]
    public void Trust_is_given_to_one_content_of_one_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "c4k-trust-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FolderTrustStore(path);
            var original = FolderConfig.Parse(@"C:\Repo", "{ \"enter\": { \"run\": \"build.ps1\" } }");
            Assert.False(store.IsTrusted(original));

            store.Trust(original);

            Assert.True(new FolderTrustStore(path).IsTrusted(original));
            Assert.False(store.IsTrusted(FolderConfig.Parse(@"C:\Repo", "{ \"enter\": { \"run\": \"format.ps1\" } }")));
            Assert.False(store.IsTrusted(FolderConfig.Parse(@"C:\Copy", "{ \"enter\": { \"run\": \"build.ps1\" } }")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Card_tiles_parse_card_refresh_options_size_and_api()
    {
        var config = FolderConfig.Parse(@"C:\Dom", """
            {
              "preview": {
                "view": "dashboard",
                "tiles": [
                  { "title": "Zdjęcia", "open": "D:/Foto/2026", "card": "gallery" },
                  { "title": "Projekty", "open": "~/Repos" },
                  { "title": "Projekty", "open": "~/Repos", "card": true, "size": "wide" },
                  { "title": "VPS", "open": "sftp://kamil@vps.example.com/", "card": "ssh", "refresh": "10s" },
                  { "title": "Grafana", "card": "api", "refresh": "1m",
                    "api": {
                      "url": "https://grafana.example.com/api/health",
                      "headers": { "Authorization": "Bearer {secret:grafana}" },
                      "fields": [
                        { "path": "database", "label": "Baza", "as": "status", "ok": ["ok"] },
                        { "path": "version", "label": "Wersja" }
                      ]
                    } },
                  { "title": "Steam", "card": "official.steam-library", "options": { "top": 3 } }
                ]
              }
            }
            """);

        Assert.Null(config.Error);
        var tiles = config.Preview!.Tiles;
        Assert.Equal(["gallery", null, FolderTile.AutoCard, FolderViewIds.Ssh, FolderViewIds.Api, "official.steam-library"], tiles.Select(t => t.Card));
        Assert.All(tiles, t => Assert.Null(t.Error));
        Assert.Equal(TileSize.Wide, tiles[2].Size);
        Assert.Equal(TileSize.Small, tiles[0].Size);
        Assert.Equal(TimeSpan.FromSeconds(10), tiles[3].Refresh);
        Assert.Null(tiles[0].Refresh);

        var api = tiles[4].Api!;
        Assert.Equal(TimeSpan.FromMinutes(1), tiles[4].Refresh);
        Assert.Equal("GET", api.Method);
        Assert.False(api.HasSideEffects);
        Assert.Equal("Bearer {secret:grafana}", api.Headers["Authorization"]);
        Assert.Equal(ApiFieldKind.Status, api.Fields[0].As);
        Assert.Equal(["ok"], api.Fields[0].Ok);
        Assert.Equal(ApiFieldKind.Value, api.Fields[1].As);
        Assert.Equal("Wersja", api.Fields[1].Label);

        Assert.Equal(3, System.Text.Json.JsonDocument.Parse(tiles[5].Options!).RootElement.GetProperty("top").GetInt32());
    }

    [Theory]
    [InlineData("1s", 2)]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("2h", 7200)]
    public void Refresh_is_parsed_and_clamped_to_two_seconds(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), FolderConfig.ParseRefresh(text));

    [Fact]
    public void Refresh_rejects_other_text()
    {
        Assert.Null(FolderConfig.ParseRefresh(null));
        Assert.Throws<FormatException>(() => FolderConfig.ParseRefresh("fast"));
        Assert.Throws<FormatException>(() => FolderConfig.ParseRefresh("10"));
        Assert.Throws<FormatException>(() => FolderConfig.ParseRefresh("300000000h"));
        Assert.Throws<FormatException>(() => FolderConfig.ParseRefresh("25h"));
    }

    [Fact]
    public void Huge_or_numeric_refresh_is_a_tile_error()
    {
        var config = FolderConfig.Parse(@"C:\Dom", """
            { "preview": { "view": "dashboard", "tiles": [
              { "title": "Huge", "open": "a", "card": true, "refresh": "300000000h" },
              { "title": "Number", "open": "a", "card": true, "refresh": 10 }
            ] } }
            """);

        Assert.Null(config.Error);
        Assert.All(config.Preview!.Tiles, t => Assert.False(string.IsNullOrEmpty(t.Error), t.Title));
    }

    [Fact]
    public void Bad_card_fields_become_tile_errors()
    {
        var config = FolderConfig.Parse(@"C:\Dom", """
            { "preview": { "view": "dashboard", "tiles": [
              { "title": "OK", "open": "a", "card": true },
              { "title": "Refresh", "open": "a", "card": true, "refresh": "fast" },
              { "title": "No url", "card": "api", "api": { "method": "GET" } },
              { "title": "Api missing", "card": "api" },
              { "title": "Number", "open": "a", "card": 5 },
              { "title": "Pie", "card": "api", "api": { "url": "https://x/y", "fields": [ { "path": "a", "as": "pie" } ] } },
              { "title": "Run", "card": true, "run": "build.ps1" }
            ] } }
            """);

        Assert.Null(config.Error);
        var tiles = config.Preview!.Tiles;
        Assert.Equal(7, tiles.Count);
        Assert.Null(tiles[0].Error);
        Assert.All(tiles.Skip(1), t => Assert.False(string.IsNullOrEmpty(t.Error), t.Title));
        Assert.Equal("Refresh", tiles[1].Title);
    }

    [Fact]
    public void Trust_summary_lists_post_ssh_and_run_but_not_get()
    {
        var config = FolderConfig.Parse(@"C:\Dom", """
            {
              "enter": { "run": { "command": "build.ps1", "args": ["-Release"] } },
              "preview": { "view": "dashboard", "tiles": [
                { "title": "Get", "card": "api", "api": { "url": "https://example.com/health" } },
                { "title": "Post", "card": "api", "api": { "url": "https://example.com/deploy", "method": "post" } },
                { "title": "Ssh", "open": "sftp://kamil@vps.example.com/", "card": "ssh" },
                { "title": "Script", "run": "clean.ps1" }
              ] }
            }
            """);

        Assert.Equal(
            ["run build.ps1 -Release", "POST https://example.com/deploy", "SSH sftp://kamil@vps.example.com/", "run clean.ps1"],
            config.TrustSummary());
    }

    [Fact]
    public void Changed_file_loses_trust_for_post_and_ssh()
    {
        var path = Path.Combine(Path.GetTempPath(), "c4k-trust-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FolderTrustStore(path);
            const string Template = """{ "preview": { "view": "dashboard", "tiles": [ { "title": "Post", "card": "api", "api": { "url": "URL", "method": "POST" } } ] } }""";
            var trusted = FolderConfig.Parse(@"C:\Dom", Template.Replace("URL", "https://example.com/deploy", StringComparison.Ordinal));
            store.Trust(trusted);

            var changed = FolderConfig.Parse(@"C:\Dom", Template.Replace("URL", "https://evil.example.net/deploy", StringComparison.Ordinal));

            Assert.True(store.IsTrusted(trusted));
            Assert.False(store.IsTrusted(changed));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dashboard_without_tiles_shares_the_other_sections_tiles()
    {
        var config = FolderConfig.Parse(@"C:\Dom", """{ "preview": "dashboard", "enter": { "view": "dashboard", "tiles": [ { "title": "A", "open": "a" } ] } }""");

        Assert.Equal("A", Assert.Single(config.Preview!.Tiles).Title);
    }

    [Fact]
    public void Appending_multiple_tiles_creates_one_dashboard()
    {
        var json = FolderConfigWriter.AppendTile(null, new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "Readme",
            ["open"] = @"C:\Notes\README.md",
        }, replacePreview: false);
        json = FolderConfigWriter.AppendTile(json, new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "Images",
            ["open"] = @"C:\Notes\Images",
        }, replacePreview: false);

        var config = FolderConfig.Parse(@"C:\Notes", json);

        Assert.Null(config.Error);
        Assert.Equal(FolderViewIds.Dashboard, config.Preview!.View);
        Assert.Equal(["Readme", "Images"], config.Preview.Tiles.Select(tile => tile.Title));
    }

    [Fact]
    public void Appending_to_preview_creates_preview_dashboard_from_enter_dashboard()
    {
        var existing = """{ "enter": { "view": "dashboard", "tiles": [ { "title": "Existing", "open": "old" } ] } }""";

        var json = FolderConfigWriter.AppendTile(existing, new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "New",
            ["open"] = "new",
        }, replacePreview: false, preferPreview: true);
        var config = FolderConfig.Parse(@"C:\Notes", json);

        Assert.Null(config.Error);
        Assert.Equal(["Existing", "New"], config.Preview!.Tiles.Select(tile => tile.Title));
        Assert.Equal(["Existing"], config.Enter!.Tiles.Select(tile => tile.Title));
    }

    [Fact]
    public void Replacing_preview_keeps_the_old_preview_for_enter()
    {
        var existing = """{ "preview": "gallery" }""";

        var json = FolderConfigWriter.AppendTile(existing, new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "Readme",
            ["open"] = "README.md",
        }, replacePreview: true, preferPreview: true);
        var config = FolderConfig.Parse(@"C:\Notes", json);

        Assert.Null(config.Error);
        Assert.Equal(FolderViewIds.Dashboard, config.Preview!.View);
        Assert.Equal(FolderViewIds.Gallery, config.Enter!.View);
        Assert.Equal("Readme", Assert.Single(config.Preview.Tiles).Title);
    }

    [Fact]
    public void Api_view_parses_as_preview_action()
    {
        var config = FolderConfig.Parse(@"C:\Api", """{ "preview": { "view": "api", "api": { "url": "https://x/y", "method": "post", "body": "{}" } } }""");

        Assert.Null(config.Error);
        Assert.Equal(FolderViewIds.Api, config.Preview!.View);
        Assert.Equal("POST", config.Preview.Api!.Method);
        Assert.True(config.Preview.Api.HasSideEffects);
        Assert.Equal("{}", config.Preview.Api.Body);
        Assert.False(config.Preview.IsPlainFiles);
    }
}

/// <summary>The catalog in the repository must describe exactly the files GitHub serves, or installs fail their checksum.</summary>
public sealed class PluginCatalogTests
{
    [Fact]
    public void Catalog_rejects_duplicate_plugin_ids()
    {
        const string json = """
            {
              "plugins": [
                { "id": "official.example", "name": "Example", "path": "plugins/example" },
                { "id": "OFFICIAL.EXAMPLE", "name": "Duplicate", "path": "plugins/duplicate" }
              ]
            }
            """;

        var exception = Assert.Throws<JsonException>(() => PluginCatalogService.Parse(json));

        Assert.Contains("official.example", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_lists_every_plugin_file_with_its_current_hash()
    {
        var root = RepositoryRoot();
        var catalog = PluginCatalogService.Parse(File.ReadAllText(Path.Combine(root, "plugins-catalog", "catalog.json")));
        Assert.NotEmpty(catalog.Plugins);
        Assert.Equal(catalog.Plugins.Count, catalog.Plugins.Select(plugin => plugin.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        const string hint = " — run: pwsh ./plugins-catalog/Update-Catalog.ps1";

        foreach (var plugin in catalog.Plugins)
        {
            var folder = Path.Combine(root, plugin.Path.Replace('/', Path.DirectorySeparatorChar));
            var onDisk = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains("__pycache__", StringComparison.Ordinal) && !f.EndsWith(".pyc", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal);
            Assert.Equal(onDisk, plugin.Files.Select(f => f.Path).Order(StringComparer.Ordinal));

            foreach (var file in plugin.Files)
            {
                var text = File.ReadAllText(Path.Combine(folder, file.Path));
                Assert.True(PluginCatalogService.ContentHash(text) == file.Sha256, $"{plugin.Id}/{file.Path} changed{hint}");
            }

            var manifest = ManifestReader.Read(folder);
            Assert.True(manifest.IsValid, string.Join("; ", manifest.Errors));
            Assert.Equal(plugin.Id, manifest.Manifest!.Id);
            Assert.True(plugin.Version == manifest.Manifest.Version, $"{plugin.Id}: version{hint}");
            Assert.Equal(plugin.Runtime, manifest.Manifest.Runtime.Type);
            var previewExtensions = manifest.Manifest.Contributes?.Previews?
                .SelectMany(preview => preview.Extensions)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
            Assert.Equal(
                previewExtensions,
                plugin.PreviewExtensions.Order(StringComparer.OrdinalIgnoreCase));
            Assert.Empty(PluginSourceCheck.ScanFolder(folder));
            if (plugin.Example is { } example)
            {
                Assert.Null(Folders.FolderConfig.Parse(folder, example).Error);
            }
        }
    }

    [Fact]
    public void Built_in_copy_is_the_repository_catalog() =>
        Assert.Equal(
            PluginCatalogService.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "plugins-catalog", "catalog.json"))).Plugins.Select(p => p.Id),
            PluginCatalogService.LoadBuiltIn().Plugins.Select(p => p.Id));

    [Fact]
    public void Hash_ignores_carriage_returns() =>
        Assert.Equal(PluginCatalogService.ContentHash("a\nb\n"), PluginCatalogService.ContentHash("a\r\nb\r\n"));

    [Fact]
    public void File_urls_are_escaped_per_segment()
    {
        var service = new PluginCatalogService(new HttpClient(), Path.Combine(Path.GetTempPath(), "unused.json"));
        var entry = new PluginCatalogEntry { Id = "a.b", Name = "x", Path = "plugins-catalog/my plugin" };

        Assert.Equal(PluginCatalogService.DefaultBaseUrl + "plugins-catalog/my%20plugin/lib/a%23b.ps1",
            service.FileUrl(entry, new PluginCatalogFile { Path = "lib/a#b.ps1", Sha256 = "" }));
    }

    [Fact]
    public void Preview_candidates_match_declared_file_patterns_and_service_names()
    {
        using var http = new HttpClient();
        var service = new PluginCatalogService(http, Path.Combine(Path.GetTempPath(), "unused.json"));

        var file = service.FindPreviewCandidates(new VfsEntry("api.openapi.yaml", @"C:\api.openapi.yaml", VfsEntryKind.File));
        var serviceEntry = service.FindPreviewCandidates(new VfsEntry("MikroTik API", "net://router/api", VfsEntryKind.Service));
        var unknown = service.FindPreviewCandidates(new VfsEntry("data.unknown-c4k", @"C:\data.unknown-c4k", VfsEntryKind.File));
        var unrelatedFolder = service.FindPreviewCandidates(new VfsEntry("random-c4k-folder", @"C:\random-c4k-folder", VfsEntryKind.Directory));

        Assert.Contains(file, plugin => plugin.Id == "official.openapi-preview");
        Assert.Contains(serviceEntry, plugin => plugin.Id == "official.mikrotik-routeros");
        Assert.Empty(unknown);
        Assert.Empty(unrelatedFolder);
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
