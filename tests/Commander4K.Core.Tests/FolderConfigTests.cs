using Commander4K.Core.Folders;
using Commander4K.Core.Plugins;

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
}

/// <summary>The catalog in the repository must describe exactly the files GitHub serves, or installs fail their checksum.</summary>
public sealed class PluginCatalogTests
{
    [Fact]
    public void Catalog_lists_every_plugin_file_with_its_current_hash()
    {
        var root = RepositoryRoot();
        var catalog = PluginCatalogService.Parse(File.ReadAllText(Path.Combine(root, "plugins-catalog", "catalog.json")));
        Assert.NotEmpty(catalog.Plugins);
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
