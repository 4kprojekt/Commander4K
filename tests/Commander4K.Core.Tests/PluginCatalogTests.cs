using System.Text.Json;
using Commander4K.Core.Folders;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;


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
                Assert.Null(Folders.C4kItem.Parse(folder, "example.c4k", example).Error);
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
