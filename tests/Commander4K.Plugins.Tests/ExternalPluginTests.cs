using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Commander4K.Core.Execution;
using Commander4K.Core.Mail;
using Commander4K.Core.Plugins;
using Commander4K.Core.Updates;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Archives;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

/// <summary>Runs the example plugins from examples/plugins through the real protocol with the real runtimes.</summary>
public sealed class ExternalPluginTests : IAsyncLifetime
{
    private static readonly string Examples = Path.Combine(RepositoryRoot(), "examples", "plugins");

    private readonly string _folder = Directory.CreateTempSubdirectory("c4k-ext-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly FakeUi _ui = new();
    private readonly ArchiveVfsPlugin _archives = new();
    private readonly VfsRouter _router;
    private readonly HttpClient _http = new();
    private readonly PluginStorage _storage;
    private IReadOnlyList<RuntimeInfo> _runtimes = [];

    public ExternalPluginTests()
    {
        _storage = new PluginStorage(Path.Combine(_folder, "plugin-data"));
        _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin(), _archives]);
        _archives.Router = _router; // like AppHost: archives open their container through the router that contains them
    }

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
        foreach (var plugin in _started)
        {
            await plugin.DisposeAsync();
        }

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
    public async Task Preview_action_round_trip()
    {
        await using var plugin = Start("folder-stats-python");
        var csvPath = Path.Combine(_folder, "dane.csv");
        await File.WriteAllTextAsync(csvPath, "a;b\n1;2\n", TestToken);
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var entry = new VfsEntry("dane.csv", csvPath, VfsEntryKind.File, 8);
        var host = HostFor(plugin);

        // The pinned view (Enter) gets the action bar; the cursor-following quick view does not.
        var pinned = Assert.IsType<ActionsPreview>(await preview.LoadWithOptionsAsync(entry, host, null, pinned: true, TestToken));
        Assert.Contains(pinned.Actions, a => a.Target == "c4k:plugin/echo?x=1");
        Assert.IsType<MarkdownPreview>(await preview.LoadWithOptionsAsync(entry, host, null, pinned: false, TestToken));

        Assert.True(PreviewActionTargets.TryParse("c4k:plugin/echo?x=1", out var action, out var arguments));
        var result = await preview.InvokeActionAsync(new PreviewActionRequest(entry, null, action, arguments, null), host, TestToken);
        Assert.Equal(new MarkdownPreview("echo 1", false), result);

        await preview.FormChangedAsync(entry, null, new Dictionary<string, string> { ["to"] = "a@b.pl", ["body"] = "Cześć" }, host, TestToken);
        var stored = _storage.Get("examples.folder-stats", "form");
        Assert.Equal("a@b.pl", stored?["to"]?.GetValue<string>());
        Assert.Equal("Cześć", stored?["body"]?.GetValue<string>());
    }

    [Fact]
    public async Task Json_preview_formats_and_outlines()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("json-preview-python", "data.json", """{"name":"Kraków","tags":["a","b"],"geo":{"lat":50.06,"lon":19.94}}""");

        var result = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken));

        Assert.Contains("✅", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("\"lat\": 50.06", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("`tags` — tablica (2)", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("`geo.lat` — liczba", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_preview_reports_error_position()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("json-preview-python", "bad.jsonc", "{\n  // komentarz\n  \"a\": 1,\n  \"b\": ,\n}");

        var result = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken));
        var card = await preview.LoadCardAsync(new PreviewCardRequest(entry, null, null), host, TestToken);

        Assert.Contains("linia 4", result.Markdown, StringComparison.Ordinal);
        Assert.Equal(CardStatus.Error, card.Status);
    }

    [Fact]
    public async Task Json_and_openapi_files_with_a_bom_are_valid()
    {
        var (json, jsonHost, jsonEntry) = await CatalogPreviewAsync("json-preview-python", "bom.json", "{\"a\": 1}", new UTF8Encoding(true));
        var (openapi, openapiHost, openapiEntry) = await CatalogPreviewAsync("openapi-preview-python", "openapi.yaml", "openapi: 3.0.0\ninfo:\n  title: Bom\npaths: {}\n", new UTF8Encoding(true));

        var card = await json.LoadCardAsync(new PreviewCardRequest(jsonEntry, null, null), jsonHost, TestToken);
        var markdown = Assert.IsType<MarkdownPreview>(await openapi.LoadAsync(openapiEntry, openapiHost, null, TestToken)).Markdown;

        Assert.Equal(CardStatus.Ok, card.Status);
        Assert.Contains("# Bom", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_file_over_the_limit_is_not_called_invalid()
    {
        var big = "[" + string.Join(",", Enumerable.Repeat("\"" + new string('x', 100) + "\"", 45_000)) + "]";
        var (preview, host, entry) = await CatalogPreviewAsync("json-preview-python", "big.json", big);

        var card = await preview.LoadCardAsync(new PreviewCardRequest(entry, null, null), host, TestToken);

        Assert.Equal(CardStatus.Warning, card.Status);
    }

    [Fact]
    public async Task Json_card_shows_status_and_keys()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("json-preview-python", "data.json", """{"a":1,"b":2,"c":3,"d":4,"e":5,"f":6}""");

        var card = await preview.LoadCardAsync(new PreviewCardRequest(entry, null, null), host, TestToken);

        Assert.Equal(CardStatus.Ok, card.Status);
        Assert.Equal("obiekt, 6 kluczy", card.Items.OfType<CardValue>().Single(v => v.Label == "Zawartość").Value);
        Assert.Equal("a · b · c · d · e · …", Assert.Single(card.Items.OfType<CardText>()).Text);
    }

    private const string PetStoreYaml = """
        openapi: 3.0.3
        info:
          title: Pet Store
          version: "1.2.0"
          description: |
            Sklep ze zwierzętami.
            Druga linia.
        servers:
          - url: https://api.example.com/v1   # produkcja
        paths:
          /pets:
            get:
              tags: [pets]
              summary: Lista zwierząt
            post:
              tags: [pets]
              summary: 'Dodaj: zwierzę'
          /pets/{petId}:
            get:
              tags:
                - pets
              summary: Jedno zwierzę
            delete:
              summary: Usuń
          /health:
            get: { summary: Stan }
        components:
          schemas:
            Pet:
              type: object
            Error:
              type: object
        """;

    [Fact]
    public async Task Openapi3_yaml_lists_endpoints_by_tag()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("openapi-preview-python", "petstore.openapi.yaml", PetStoreYaml);

        var markdown = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;

        Assert.Contains("# Pet Store", markdown, StringComparison.Ordinal);
        Assert.Contains("1.2.0", markdown, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com/v1", markdown, StringComparison.Ordinal);
        Assert.Contains("## pets", markdown, StringComparison.Ordinal);
        Assert.Contains("| POST | `/pets` | Dodaj: zwierzę |", markdown, StringComparison.Ordinal);
        Assert.Contains("| DELETE | `/pets/{petId}` | Usuń |", markdown, StringComparison.Ordinal);
        Assert.Contains("| GET | `/health` | Stan |", markdown, StringComparison.Ordinal);
        Assert.Contains("`Pet`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Swagger2_json_reads_host_and_definitions()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("openapi-preview-python", "swagger.json", """
            { "swagger": "2.0", "info": { "title": "Old API", "version": "0.9" }, "host": "old.example.com", "basePath": "/api", "schemes": ["https"],
              "paths": { "/users": { "get": { "summary": "Users" } } }, "definitions": { "User": {} } }
            """);

        var markdown = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;

        Assert.Contains("https://old.example.com/api", markdown, StringComparison.Ordinal);
        Assert.Contains("| GET | `/users` | Users |", markdown, StringComparison.Ordinal);
        Assert.Contains("`User`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Openapi_card_has_method_chart()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("openapi-preview-python", "openapi.yaml", PetStoreYaml);

        var card = await preview.LoadCardAsync(new PreviewCardRequest(entry, null, null), host, TestToken);

        Assert.Equal("Pet Store", card.Title);
        Assert.Equal("5", card.Items.OfType<CardValue>().Single(v => v.Label == "Endpointy").Value);
        Assert.Equal("2", card.Items.OfType<CardValue>().Single(v => v.Label == "Schematy").Value);
        var chart = Assert.Single(card.Items.OfType<CardChart>()).Chart;
        Assert.Equal(ChartKind.Bar, chart.Kind);
        Assert.Equal(["GET", "POST", "DELETE"], chart.Series.Select(s => s.Name));
        Assert.Equal(3, chart.Series[0].Points.Single().Y);
    }

    [Fact]
    public async Task Openapi_preview_explains_unsupported_yaml()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("openapi-preview-python", "openapi.yml", "openapi: 3.0.0\npaths: &anchor\n  /a: *anchor\n");

        var result = await preview.LoadAsync(entry, host, null, TestToken);

        Assert.Contains("YAML", Assert.IsType<MarkdownPreview>(result).Markdown, StringComparison.Ordinal);
    }

    private const string Page = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Strona testowa</title>
        <meta name="description" content="Opis strony">
        <style>body { color: red }</style><script>var x = "<h1>nie</h1>";</script></head>
        <body><nav><a href="/menu">Menu</a></nav>
        <h1>Nagłówek</h1><p>Pierwszy <b>akapit</b>.</p>
        <h2>Sekcja</h2><ul><li>punkt</li></ul>
        <a href="https://example.com">Example</a> <a href="docs.html">Docs</a>
        <img src="logo.png" alt="Logo"><footer>stopka</footer></body></html>
        """;

    [Fact]
    public async Task Html_preview_extracts_title_headings_links()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("html-preview-python", "index.html", Page);

        var markdown = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;

        Assert.Contains("# Strona testowa", markdown, StringComparison.Ordinal);
        Assert.Contains("Opis strony", markdown, StringComparison.Ordinal);
        Assert.Contains("- Nagłówek", markdown, StringComparison.Ordinal);
        Assert.Contains("  - Sekcja", markdown, StringComparison.Ordinal);
        Assert.Contains("Pierwszy akapit.", markdown, StringComparison.Ordinal);
        Assert.Contains("[Example](https://example.com)", markdown, StringComparison.Ordinal);
        Assert.Contains("`logo.png` — Logo", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("color: red", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("stopka", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("nie</h1>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_preview_decodes_cp1250_meta_charset()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var (preview, host, entry) = await CatalogPreviewAsync("html-preview-python", "stara.htm",
            "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=windows-1250\"><title>Zażółć gęślą jaźń</title></head><body><p>Łódź</p></body></html>",
            Encoding.GetEncoding(1250));

        var markdown = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;

        Assert.Contains("Zażółć gęślą jaźń", markdown, StringComparison.Ordinal);
        Assert.Contains("Łódź", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_card_counts_links_and_images()
    {
        var (preview, host, entry) = await CatalogPreviewAsync("html-preview-python", "index.html", Page);

        var card = await preview.LoadCardAsync(new PreviewCardRequest(entry, null, null), host, TestToken);

        Assert.Equal("Strona testowa", card.Title);
        Assert.Equal("3", card.Items.OfType<CardValue>().Single(v => v.Label == "Linki").Value);
        Assert.Equal("1", card.Items.OfType<CardValue>().Single(v => v.Label == "Obrazy").Value);
        Assert.Equal(new Uri(entry.Path).AbsoluteUri, card.Actions.Single(a => a.Label == "Otwórz w przeglądarce").Target);
    }

    /// <summary>A catalog plugin started from its folder, with one sample file written to the test folder.</summary>
    private async Task<(ExternalPreviewPlugin Preview, IPluginHost Host, VfsEntry Entry)> CatalogPreviewAsync(string folder, string fileName, string content, Encoding? encoding = null)
    {
        var plugin = StartFrom(Path.Combine(RepositoryRoot(), "plugins-catalog", folder));
        _started.Add(plugin);
        var path = Path.Combine(_folder, fileName);
        await File.WriteAllTextAsync(path, content, encoding ?? new UTF8Encoding(false), TestToken);
        var entry = new VfsEntry(fileName, path, VfsEntryKind.File, new FileInfo(path).Length);
        var preview = plugin.Manifest.Contributes!.Previews!.Select(p => new ExternalPreviewPlugin(plugin, p)).First(p => p.CanPreview(entry));
        return (preview, HostFor(plugin), entry);
    }

    /// <summary>A catalog plugin started from its folder, previewing an existing file; the preview is the one with this id, else the first that accepts the entry.</summary>
    private async Task<(ExternalPreviewPlugin Preview, IPluginHost Host, VfsEntry Entry)> CatalogPreviewOfAsync(string folder, string path, string? previewId = null)
    {
        var plugin = StartFrom(Path.Combine(RepositoryRoot(), "plugins-catalog", folder));
        _started.Add(plugin);
        var entry = new VfsEntry(Path.GetFileName(path), path, VfsEntryKind.File, new FileInfo(path).Length);
        var previews = plugin.Manifest.Contributes!.Previews!.Select(p => new ExternalPreviewPlugin(plugin, p)).ToList();
        var preview = previewId is null
            ? previews.First(p => p.CanPreview(entry))
            : previews.First(p => p.Id.EndsWith("/" + previewId, StringComparison.Ordinal));
        return (preview, HostFor(plugin), entry);
    }

    private readonly List<ExternalPlugin> _started = [];

    [Fact]
    public async Task Pe_preview_reads_native_exe_headers_and_imports()
    {
        var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe"); // MTP apphost
        var (preview, host, entry) = await CatalogPreviewOfAsync("pe-info-python", exe, "exe");
        var md = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;
        Assert.Contains(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x64", md, StringComparison.Ordinal);
        Assert.Contains("konsola", md, StringComparison.Ordinal);
        Assert.Contains("KERNEL32.dll", md, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ASLR: ✅", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pe_preview_lists_dll_exports_and_forwarders()
    {
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var (preview, host, entry) = await CatalogPreviewOfAsync("pe-info-python", kernel32, "dll");
        var md = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;
        Assert.Contains("## Eksporty", md, StringComparison.Ordinal);
        Assert.Contains("CreateFileW", md, StringComparison.Ordinal);
        Assert.Contains("→", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pe_preview_of_non_pe_file()
    {
        var path = Path.Combine(_folder, "fake.exe");
        var bytes = new byte[128];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, 0x3C);
        await File.WriteAllBytesAsync(path, bytes, TestToken);
        var (preview, host, entry) = await CatalogPreviewOfAsync("pe-info-python", path, "exe");
        var md = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;
        Assert.Contains("nie jest prawidłowym plikiem PE", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pe_preview_survives_broken_import_table()
    {
        var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe");
        var bytes = await File.ReadAllBytesAsync(exe, TestToken);
        var lfanew = BitConverter.ToInt32(bytes, 0x3C);
        var plus = BitConverter.ToUInt16(bytes, lfanew + 24) == 0x20B;
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, lfanew + 24 + (plus ? 112 : 96) + 8);
        var path = Path.Combine(_folder, "broken.exe");
        await File.WriteAllBytesAsync(path, bytes, TestToken);
        var (preview, host, entry) = await CatalogPreviewOfAsync("pe-info-python", path, "exe");
        var md = Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;
        Assert.Contains("## Nagłówek", md, StringComparison.Ordinal);
        Assert.Contains("⚠", md, StringComparison.Ordinal);
        Assert.Contains("tabela importów", md, StringComparison.Ordinal);
    }

    private async Task<string> RenderAsync(string folder, string path, string previewId)
    {
        var (preview, host, entry) = await CatalogPreviewOfAsync(folder, path, previewId);
        return Assert.IsType<MarkdownPreview>(await preview.LoadAsync(entry, host, null, TestToken)).Markdown;
    }

    [Fact]
    public async Task Pe_preview_shows_version_and_execution_level()
    {
        var regedit = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");
        var md = await RenderAsync("pe-info-python", regedit, "exe");
        Assert.Contains("## Uprawnienia", md, StringComparison.Ordinal);
        Assert.Contains("highestAvailable", md, StringComparison.Ordinal); // verified on this machine: regedit.exe asks for highestAvailable
        Assert.Contains("Microsoft Corporation", md, StringComparison.Ordinal);
        Assert.Contains("## Wersja", md, StringComparison.Ordinal);
        Assert.True(md.IndexOf("## Uprawnienia", StringComparison.Ordinal) < md.IndexOf("## Nagłówek", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pe_preview_respects_read_budget()
    {
        var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe");
        var path = Path.Combine(_folder, "big.exe");
        await using (var output = File.Create(path))
        {
            await output.WriteAsync(await File.ReadAllBytesAsync(exe, TestToken), TestToken);
            await output.WriteAsync(new byte[40 * 1024 * 1024], TestToken);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var md = await RenderAsync("pe-info-python", path, "exe");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
        Assert.Contains("Dane doklejone", md, StringComparison.Ordinal);
        Assert.Contains("40,0 MB", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pe_preview_stops_endless_import_thunk_list()
    {
        var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe");
        var bytes = await File.ReadAllBytesAsync(exe, TestToken);
        var lfanew = BitConverter.ToInt32(bytes, 0x3C);
        var plus = BitConverter.ToUInt16(bytes, lfanew + 24) == 0x20B;
        var optSize = BitConverter.ToUInt16(bytes, lfanew + 20);
        var sectionCount = BitConverter.ToUInt16(bytes, lfanew + 6);
        var table = lfanew + 24 + optSize;
        var importDir = lfanew + 24 + (plus ? 112 : 96) + 8;
        var importRva = BitConverter.ToUInt32(bytes, importDir);
        uint ToOffset(uint rva)
        {
            for (var i = 0; i < sectionCount; i++)
            {
                var at = table + 40 * i;
                var va = BitConverter.ToUInt32(bytes, at + 12);
                var rawSize = BitConverter.ToUInt32(bytes, at + 16);
                if (rva >= va && rva < va + rawSize) return rva - va + BitConverter.ToUInt32(bytes, at + 20);
            }
            throw new InvalidOperationException("RVA outside sections");
        }
        // thunk array in the first section's raw data: 0x41 bytes never form a 0 terminator
        var firstVa = BitConverter.ToUInt32(bytes, table + 12);
        var firstRaw = (int)BitConverter.ToUInt32(bytes, table + 20);
        Array.Fill(bytes, (byte)0x41, firstRaw, 4096 * 8 + 256);
        BitConverter.GetBytes(firstVa).CopyTo(bytes, (int)ToOffset(importRva)); // OriginalFirstThunk of descriptor 0
        var path = Path.Combine(_folder, "loop.exe");
        await File.WriteAllBytesAsync(path, bytes, TestToken);
        var md = await RenderAsync("pe-info-python", path, "exe");
        Assert.Contains("## Nagłówek", md, StringComparison.Ordinal);
        Assert.Contains("⚠", md, StringComparison.Ordinal);
        Assert.Contains("możliwa pętla", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x86", @"C:\Windows", @"C:\Windows\SysWOW64")]
    [InlineData("x64", @"C:\Windows", @"C:\Windows\System32")]
    [InlineData("ARM64", @"C:\Windows", @"C:\Windows\System32")]
    public async Task Dependency_folder_for_machine(string machine, string root, string expected)
    {
        var output = await RunPluginPythonAsync("pe-info-python", $"import deps; sys.stdout.write(deps.system_folder('{machine}', r'{root}'))");
        Assert.Equal(expected, output);
    }

    /// <summary>Runs a snippet with the detected Python in isolated mode, with a catalog plugin's folder on sys.path; returns stdout.</summary>
    private async Task<string> RunPluginPythonAsync(string folder, string code)
    {
        var python = _runtimes.FirstOrDefault(r => r.Type == "python" && r.Executable is not null);
        Assert.SkipWhen(python is null, "Python is not installed");
        var pluginFolder = Path.Combine(RepositoryRoot(), "plugins-catalog", folder);
        var start = new System.Diagnostics.ProcessStartInfo(python!.Executable!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-I", "-c", $"import sys; sys.path.insert(0, r'{pluginFolder}'); {code}" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(TestToken);
        var output = await process.StandardOutput.ReadToEndAsync(TestToken);
        await process.WaitForExitAsync(TestToken);
        Assert.True(process.ExitCode == 0, await error);
        return output;
    }

    [Fact]
    public async Task Pe_preview_resolves_dependencies()
    {
        var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe");
        var md = await RenderAsync("pe-info-python", exe, "exe");
        Assert.Contains("## Zależności", md, StringComparison.Ordinal);
        var lines = md.Split('\n');
        var kernel = lines.Single(l => l.Contains("KERNEL32.dll", StringComparison.OrdinalIgnoreCase) && l.TrimStart().StartsWith("- ", StringComparison.Ordinal) && l.Contains('—', StringComparison.Ordinal) && !l.Contains("**", StringComparison.Ordinal));
        Assert.True(kernel.Contains("KnownDLLs", StringComparison.Ordinal) || kernel.Contains("System32", StringComparison.Ordinal), kernel);
        var apiSets = lines.Where(l => l.Contains("api-ms-win-", StringComparison.Ordinal) && !l.Contains("**", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(apiSets);
        Assert.All(apiSets, l => Assert.Contains("zestaw API", l, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains('❌', StringComparison.Ordinal) && l.Contains("nie znaleziono", StringComparison.Ordinal));
        Assert.True(md.IndexOf("## Uprawnienia", StringComparison.Ordinal) < md.IndexOf("## Zależności", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pe_preview_marks_missing_local_dependency()
    {
        var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe");
        var bytes = await File.ReadAllBytesAsync(exe, TestToken);
        var name = Encoding.ASCII.GetBytes("KERNEL32.dll\0");
        var at = bytes.AsSpan().IndexOf(name);
        Assert.True(at > 0, "the apphost imports KERNEL32.dll");
        Encoding.ASCII.GetBytes("KERNEQ32.dll").CopyTo(bytes, at);
        var path = Path.Combine(_folder, "app.exe");
        await File.WriteAllBytesAsync(path, bytes, TestToken);
        var md = await RenderAsync("pe-info-python", path, "exe");
        Assert.Contains("❌ KERNEQ32.dll — nie znaleziono", md, StringComparison.Ordinal);
        var notes = md[md.IndexOf("## Uwagi", StringComparison.Ordinal)..];
        Assert.Contains("KERNEQ32.dll", notes, StringComparison.Ordinal);
    }

    /// <summary>The apphost with its SHELL32.dll import renamed to VERSION.dll (same length; not a KnownDLL), next to a copy of System32's version.dll.</summary>
    private async Task<string> AppWithLocalDllAsync()
    {
        var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe");
        var bytes = await File.ReadAllBytesAsync(exe, TestToken);
        var at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes("SHELL32.dll\0"));
        Assert.True(at > 0, "the apphost imports SHELL32.dll");
        Encoding.ASCII.GetBytes("VERSION.dll").CopyTo(bytes, at);
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "local-tree")).FullName;
        var path = Path.Combine(folder, "app.exe");
        await File.WriteAllBytesAsync(path, bytes, TestToken);
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(folder, "VERSION.dll"));
        return path;
    }

    [Fact]
    public async Task Pe_preview_walks_app_local_dll_tree()
    {
        var md = await RenderAsync("pe-info-python", await AppWithLocalDllAsync(), "exe");
        var start = md.IndexOf("## Zależności", StringComparison.Ordinal);
        Assert.True(start >= 0, md);
        var section = md[start..md.IndexOf("\n## ", start + 3, StringComparison.Ordinal)].Split('\n').Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)).ToList();
        var local = section.FindIndex(l => l == "- ✅ VERSION.dll — obok pliku");
        Assert.True(local >= 0, string.Join('\n', section));
        Assert.StartsWith("  - ", section[local + 1], StringComparison.Ordinal); // version.dll's own imports follow, indented
        // KERNEL32.dll is imported by both app.exe and version.dll: listed once
        var names = section.Select(l => l.TrimStart()[2..].Split(" — ")[0].Split(' ', 2)[1].ToLowerInvariant()).ToList();
        Assert.Single(names, n => n == "kernel32.dll");
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public async Task Pe_dependency_walk_shares_one_budget_and_caps_api_sets()
    {
        var app = await AppWithLocalDllAsync();
        const string code = """
            import types, os
            from types import SimpleNamespace as NS
            fake = types.ModuleType('c4k')
            class HostError(Exception): pass
            def stat(p):
                return {'kind': 'file', 'size': os.path.getsize(p)} if os.path.isfile(p) else None
            def read_bytes(p, o=0, n=65536):
                with open(p, 'rb') as f:
                    f.seek(o)
                    return f.read(n)
            def list_dir(p):
                raise HostError('no reg')
            fake.HostError, fake.stat, fake.read_bytes, fake.list_dir = HostError, stat, read_bytes, list_dir
            sys.modules['c4k'] = fake
            import pe, deps
            app = r'APP'
            image = pe.parse(pe.RangeReader(app, os.path.getsize(app)), entropy=False)
            full = deps.Budget(8 << 20)
            walked = deps.tree(app, image, known=set(), budget=full)
            small = deps.Budget(1)
            starved = deps.tree(app, image, known=set(), budget=small)
            version = lambda t: next(n for n in t if n.resolution.name == 'VERSION.dll')
            fake_image = NS(machine='x64', imports=[pe.Import('foo.dll', [], True), pe.Import('foo.dll', [], False)]
                            + [pe.Import(f'api-ms-win-test-l1-1-{i}.dll') for i in range(250)])
            other = deps.tree(r'C:\nowhere\app.exe', fake_image, known=set())
            sys.stdout.write('|'.join(str(x) for x in (full.spent > 0, version(walked).error, version(walked).children_count > 0,
                version(starved).error, full.remaining == (8 << 20) - full.spent, other[0].delayed, len(other), other.more_api_sets)))
            """;
        var output = await RunPluginPythonAsync("pe-info-python", code.Replace("APP", app, StringComparison.Ordinal));
        Assert.Equal("True|None|True|przekroczono limit odczytu|True|False|201|50", output);
    }

    [Fact]
    public async Task Pe_preview_of_dll_shows_permissions_and_dependencies_after_exports()
    {
        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var md = await RenderAsync("pe-info-python", kernel32, "dll");
        var exports = md.IndexOf("## Eksporty", StringComparison.Ordinal);
        var permissions = md.IndexOf("## Uprawnienia", StringComparison.Ordinal);
        var dependencies = md.IndexOf("## Zależności", StringComparison.Ordinal);
        Assert.True(exports >= 0 && exports < permissions && permissions < dependencies, $"{exports} {permissions} {dependencies}");
    }

    [Fact]
    public async Task Pe_preview_reads_dotnet_assembly_references()
    {
        var dll = typeof(ExternalPluginTests).Assembly.Location;  // Commander4K.Plugins.Tests.dll
        var md = await RenderAsync("pe-info-python", dll, "dll");
        Assert.Contains("## .NET", md, StringComparison.Ordinal);
        Assert.Contains(".NETCoreApp,Version=v10.0", md, StringComparison.Ordinal);
        Assert.Contains("Commander4K.Core", md, StringComparison.Ordinal);
        Assert.Contains("obok pliku", md, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NETCore.App", md, StringComparison.Ordinal);  // from the runtimeconfig.json next to the dll
        var header = md.IndexOf("## Nagłówek", StringComparison.Ordinal);
        var dotnet = md.IndexOf("## .NET", StringComparison.Ordinal);
        Assert.True(header >= 0 && header < dotnet && md.IndexOf("## ", header + 3, StringComparison.Ordinal) == dotnet, $"{header} {dotnet}");
    }

    [Fact]
    public async Task Pe_preview_shows_embedded_signer()
    {
        var coreclr = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        var md = await RenderAsync("pe-info-python", coreclr, "dll");
        Assert.Contains("## Podpis", md, StringComparison.Ordinal);
        Assert.Contains("Microsoft Corporation", md, StringComparison.Ordinal);
        Assert.Contains("nie został zweryfikowany kryptograficznie", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Dane doklejone", md, StringComparison.Ordinal);  // the certificate table is not an overlay
        var permissions = md.IndexOf("## Uprawnienia", StringComparison.Ordinal);
        Assert.Equal(md.IndexOf("## ", permissions + 3, StringComparison.Ordinal), md.IndexOf("## Podpis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pe_preview_explains_missing_embedded_signature()
    {
        // the locally built apphost is never signed (kernel32.dll is no good here: current Windows builds embed a signature in it)
        var exe = Path.ChangeExtension(typeof(ExternalPluginTests).Assembly.Location, ".exe");
        var md = await RenderAsync("pe-info-python", exe, "exe");
        Assert.Contains("## Podpis", md, StringComparison.Ordinal);
        Assert.Contains("brak osadzonego podpisu", md, StringComparison.Ordinal);
        Assert.Contains("katalog", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pe_signature_refuses_huge_oids_quickly()
    {
        // a ContentInfo whose OID is ~1 MB of 0xFF (quadratic to decode before the cap), then one with a > 64-bit arc
        const string code = """

            import time, types, pe, signature
            def tlv(tag, body):
                n = len(body)
                count = (n.bit_length() + 7) // 8
                return bytes([tag]) + (bytes([n]) if n < 0x80 else bytes([0x80 | count]) + n.to_bytes(count, 'big')) + body
            results = []
            for oid in (b'\xff' * 1000000, b'\xff' * 60 + b'\x01'):
                blob = tlv(0x30, tlv(0x06, oid) + tlv(0xA0, tlv(0x30, b'')))
                cert = (8 + len(blob)).to_bytes(4, 'little') + b'\x00\x02\x02\x00' + blob
                data = b'\0' * 8 + cert
                reader = pe.RangeReader('C:\\hostile.exe', len(data), fetch=lambda p, o, n, d=data: d[o:o + n])
                image = types.SimpleNamespace(data_dirs=[(0, 0)] * 4 + [(8, len(cert))] + [(0, 0)] * 11)
                began = time.perf_counter()
                found = signature.signer(reader, image)
                results.append(f'{time.perf_counter() - began < 1}|{found.present}|{"OID" in (found.error or "")}')
            sys.stdout.write(';'.join(results))
            """;
        Assert.Equal("True|True|True;True|True|True", await RunPluginPythonAsync("pe-info-python", code));
    }

    [Fact]
    public async Task Pe_signature_keeps_the_signer_when_attributes_are_broken()
    {
        // coreclr.dll's signature with the OID of its first unauthenticated attribute (the timestamp) turned into a NULL
        var coreclr = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        const string code = """

            import pe, signature
            data = bytearray(open(r'CORECLR', 'rb').read())   # test code only; the plugin reads through c4k
            reader = pe.RangeReader(r'CORECLR', len(data), fetch=lambda p, o, n: bytes(data[o:o + n]))
            image = pe.parse(reader, details=False, entropy=False)
            at, size = image.data_dirs[4]
            fields = signature._signed_data(signature.read_tlv(bytes(data[at + 8:at + size]), 0), 0)
            signer = [f for f in fields if f.tag == 0x31][-1].children(3)[0]
            unauth = [p for p in signer.children(4) if p.tag == 0xA1][0]
            data[at + 8 + unauth.children(5)[0].children(6)[0].header] = 0x05
            reader = pe.RangeReader(r'CORECLR', len(data), fetch=lambda p, o, n: bytes(data[o:o + n]))
            found = signature.signer(reader, image)
            sys.stdout.write(f'{found.subject}|{found.organization}|{found.not_after is not None}|{found.error is not None}|{found.timestamped}')
            """;
        var output = await RunPluginPythonAsync("pe-info-python", code.Replace("CORECLR", coreclr, StringComparison.Ordinal));
        Assert.Equal(".NET|Microsoft Corporation|True|True|False", output);
    }

    [Fact]
    public async Task Pe_plural_and_target_framework_scan()
    {
        // the earliest framework string wins, whatever its prefix, and one starting just before a chunk boundary is whole
        const string code = """

            import types
            stub = types.ModuleType('c4k'); stub.handler = lambda m: (lambda f: f); stub.serve = lambda: None; stub.HostError = Exception
            sys.modules['c4k'] = stub
            import main, pe, clr
            plural = ','.join(main._plural(n, 'a', 'b', 'c') for n in (1, 2, 5, 12, 22))
            heap = b'\0' * (clr.SCAN_CHUNK - 3) + b'.NETFramework,Version=v4.7.2,Profile=Client-0123456789-0123456789-0123456789\x01' + b'.NETCoreApp,Version=v8.0\x01'
            reader = pe.RangeReader('C:\\x.dll', len(heap), fetch=lambda p, o, n: heap[o:o + n])
            sys.stdout.write(plural + '|' + str(clr._target_framework(reader, (0, len(heap)))))
            """;
        Assert.Equal("a,b,c,c,b|.NETFramework,Version=v4.7.2,Profile=Client-0123456789-0123456789-0123456789",
            await RunPluginPythonAsync("pe-info-python", code));
    }

    [Fact]
    public async Task Pe_manifest_text_is_trimmed_and_failures_are_named()
    {
        var output = await RunPluginPythonAsync("pe-info-python",
            "import resources; m = resources.parse_manifest('<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\"><trustInfo><security><requestedPrivileges>"
            + "<requestedExecutionLevel level=\"asInvoker\"/></requestedPrivileges></security></trustInfo></assembly>\\0\\0 \\r\\n'); "
            + "broken = resources.parse_manifest('<assembly>'); "
            + "sys.stdout.write(str(m.execution_level) + '|' + str(m.error) + '|' + str(broken.error is not None))");
        Assert.Equal("asInvoker|None|True", output);
    }

    /// <summary>Writes a zip with these text entries to the test folder.</summary>
    private string ZipOf(string name, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(_folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (inner, text) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(inner).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        return path;
    }

    [Fact]
    public async Task Archive_preview_summarises_zip()
    {
        var zip = ZipOf("demo.zip", ("readme.md", "# Witaj\nopis"), ("src/a.cs", "class A {}"), ("src/b.cs", "class B {}"), ("faktura.pdf.exe", "MZ"));
        var md = await RenderAsync("archive-info-python", zip, "archive");
        Assert.Contains("Pliki: 4", md, StringComparison.Ordinal);
        Assert.Contains("Foldery: 1", md, StringComparison.Ordinal);
        Assert.Contains("## README", md, StringComparison.Ordinal);
        Assert.Contains("# Witaj", md[md.IndexOf("## README", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("faktura.pdf.exe", md[md.IndexOf("## Uwagi", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains(".cs", md[md.IndexOf("## Typy plików", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_preview_lists_tar_gz()
    {
        var path = Path.Combine(_folder, "data.tar.gz");
        await using (var file = File.Create(path))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "x/y.txt") { DataStream = new MemoryStream("hello"u8.ToArray()) };
            await tar.WriteEntryAsync(entry, TestToken);
        }

        var md = await RenderAsync("archive-info-python", path, "archive");
        Assert.Contains("y.txt", md, StringComparison.Ordinal);
        Assert.DoesNotContain("⚠", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_preview_reads_nuspec_dependencies()
    {
        const string nuspec = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Demo</id>
                <version>1.0.0</version>
                <authors>4Kprojekt</authors>
                <dependencies>
                  <group targetFramework="net8.0"><dependency id="Newtonsoft.Json" version="13.0.1"/></group>
                </dependencies>
              </metadata>
            </package>
            """;
        var nupkg = ZipOf("Demo.1.0.0.nupkg", ("Demo.nuspec", nuspec), ("lib/net8.0/Demo.dll", "MZ"));
        var md = await RenderAsync("archive-info-python", nupkg, "archive");
        Assert.Contains("## Pakiet NuGet", md, StringComparison.Ordinal);
        Assert.Contains("Newtonsoft.Json", md, StringComparison.Ordinal);
        Assert.Contains("13.0.1", md, StringComparison.Ordinal);
        Assert.Contains("net8.0", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_preview_reads_wheel_and_jar()
    {
        var wheel = ZipOf("demo-1.0-py3-none-any.whl",
            ("demo/__init__.py", ""),
            ("demo-1.0.dist-info/METADATA", "Metadata-Version: 2.1\nName: demo\nVersion: 1.0\nRequires-Python: >=3.9\nRequires-Dist: requests>=2\n\nOpis pakietu.\n"));
        var md = await RenderAsync("archive-info-python", wheel, "archive");
        Assert.Contains("## Python (wheel)", md, StringComparison.Ordinal);
        Assert.Contains("requests>=2", md, StringComparison.Ordinal);
        Assert.Contains(">=3.9", md, StringComparison.Ordinal);

        var jar = ZipOf("app.jar", ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nMain-Class: pl.demo.App\r\nImplementation-Version: 2.3\r\n"), ("pl/demo/App.class", "x"));
        md = await RenderAsync("archive-info-python", jar, "archive");
        Assert.Contains("## Java (JAR)", md, StringComparison.Ordinal);
        Assert.Contains("pl.demo.App", md, StringComparison.Ordinal);
        Assert.Contains("2.3", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_preview_skips_xml_with_dtd()
    {
        const string nuspec = """<!DOCTYPE package [<!ENTITY x "y">]><package><metadata><id>&x;</id></metadata></package>""";
        var nupkg = ZipOf("Evil.1.0.0.nupkg", ("Evil.nuspec", nuspec));
        var md = await RenderAsync("archive-info-python", nupkg, "archive");
        Assert.Contains("pominięto: DTD w XML", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Traceback", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_preview_caps_large_listing()
    {
        var files = Enumerable.Range(0, 2500).Select(i => ($"d{i:D4}/f.txt", "x")).ToArray();
        var zip = ZipOf("big.zip", files);
        var md = await RenderAsync("archive-info-python", zip, "archive");
        Assert.Contains("lista niepełna", md, StringComparison.Ordinal);
        Assert.Contains("## Podsumowanie", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_preview_of_broken_zip()
    {
        var path = Path.Combine(_folder, "broken.zip");
        await File.WriteAllBytesAsync(path, [(byte)'P', (byte)'K', 3, 4, .. Enumerable.Repeat((byte)0x5A, 200)], TestToken);
        var md = await RenderAsync("archive-info-python", path, "archive");
        Assert.Contains("⚠", md, StringComparison.Ordinal);
        Assert.DoesNotContain("Traceback", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Python_preview_returns_a_card()
    {
        await using var plugin = Start("folder-stats-python");
        var csvPath = Path.Combine(_folder, "dane.csv");
        await File.WriteAllTextAsync(csvPath, "miasto;ludność\nKraków;800000\nGdańsk;470000\n", TestToken);
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var entry = new VfsEntry("dane.csv", csvPath, VfsEntryKind.File, 40);

        var card = await preview.LoadCardAsync(new PreviewCardRequest(entry, null, null), HostFor(plugin), TestToken);

        Assert.Equal("2", card.Items.OfType<CardValue>().Single(v => v.Label == "Wiersze").Value);
        Assert.Equal("2", card.Items.OfType<CardValue>().Single(v => v.Label == "Kolumny").Value);
        Assert.Equal(CardStatus.Ok, card.Status);
        Assert.Contains(card.Actions, a => a.Target == csvPath);
    }

    [Fact]
    public async Task Card_and_chart_json_parse()
    {
        await using var plugin = Start("folder-stats-python");
        using var json = JsonDocument.Parse("""
            { "title": "T", "status": "warning", "items": [
                { "kind": "value", "label": "A", "value": "1", "status": "error" },
                { "kind": "progress", "label": "Dysk", "fraction": 0.5, "text": "50 %" },
                { "kind": "text", "text": "hello" },
                { "kind": "chart", "chart": { "kind": "bar", "unit": "req", "series": [ { "name": "GET", "points": [ { "x": 0, "y": 3, "label": "GET" }, { "x": 1, "y": 1e400 } ] } ] } },
                { "kind": "unknown" }
              ], "actions": [ { "label": "Otwórz", "target": "https://example.com" } ] }
            """);

        var card = await ExternalPreviewPlugin.ParseCardAsync(json.RootElement, HostFor(plugin), TestToken);

        Assert.Equal("T", card.Title);
        Assert.Equal(CardStatus.Warning, card.Status);
        Assert.Equal(CardStatus.Error, Assert.IsType<CardValue>(card.Items[0]).Status);
        Assert.Equal(0.5, Assert.IsType<CardProgress>(card.Items[1]).Fraction);
        Assert.Equal("hello", Assert.IsType<CardText>(card.Items[2]).Text);
        var chart = Assert.IsType<CardChart>(card.Items[3]).Chart;
        Assert.Equal(ChartKind.Bar, chart.Kind);
        Assert.Equal(3, Assert.Single(chart.Series[0].Points).Y); // 1e400 is infinite and dropped
        Assert.Equal(4, card.Items.Count);
        Assert.Equal("https://example.com", Assert.Single(card.Actions).Target);
    }

    private FolderSnapshot SnapshotWithCsv(int csvFiles)
    {
        for (var i = 0; i < csvFiles; i++)
        {
            File.WriteAllText(Path.Combine(_folder, $"t{i}.csv"), "a;b\n1;2\n");
        }

        var entries = Directory.GetFiles(_folder).Select(f => new VfsEntry(Path.GetFileName(f), f, VfsEntryKind.File, new FileInfo(f).Length)).ToList();
        return new FolderSnapshot(new VfsEntry(Path.GetFileName(_folder), _folder, VfsEntryKind.Directory, 0), entries, false);
    }

    private static PreviewContribution Tables(ExternalPlugin plugin) => plugin.Manifest.Contributes!.Previews!.Single(p => p.Id == "tables");

    [Fact]
    public async Task Folder_suggest_rule_asks_the_plugin()
    {
        await using var plugin = Start("folder-stats-python");
        var preview = new ExternalPreviewPlugin(plugin, Tables(plugin));

        var suggestion = await preview.SuggestAsync(SnapshotWithCsv(3), HostFor(plugin), TestToken);

        Assert.NotNull(suggestion);
        Assert.Equal(0.8, suggestion.Score);
        Assert.Contains("3", suggestion.Message, StringComparison.Ordinal);
        Assert.Equal("examples.folder-stats", suggestion.View.Plugin);
        Assert.Equal("tables", suggestion.View.Preview);
    }

    [Fact]
    public async Task Folder_suggest_rule_mismatch_does_not_start_the_plugin()
    {
        await using var plugin = Start("folder-stats-python");
        var preview = new ExternalPreviewPlugin(plugin, Tables(plugin));

        Assert.Null(await preview.SuggestAsync(SnapshotWithCsv(1), HostFor(plugin), TestToken));
        Assert.False(plugin.IsRunning);
    }

    [Fact]
    public async Task Folder_suggest_without_ask_uses_manifest_message()
    {
        await using var plugin = Start("folder-stats-python");
        var tables = Tables(plugin);
        var preview = new ExternalPreviewPlugin(plugin, tables with { Suggest = tables.Suggest! with { Ask = false, Message = new Dictionary<string, string> { ["en"] = "{count} tables" } } });

        var suggestion = await preview.SuggestAsync(SnapshotWithCsv(3), HostFor(plugin), TestToken);

        Assert.NotNull(suggestion);
        Assert.Equal(0.7, suggestion.Score);
        Assert.Equal("3 tables", suggestion.Message);
        Assert.False(plugin.IsRunning);
    }

    [Fact]
    public void Manifest_reader_reads_suggest()
    {
        var tables = ManifestReader.Read(Path.Combine(Examples, "folder-stats-python")).Manifest!.Contributes!.Previews!.Single(p => p.Id == "tables");

        Assert.Equal(3, tables.Suggest!.MinCount);
        Assert.True(tables.Suggest.Ask);
    }

    [Fact]
    public async Task Folder_suggest_plugin_error_is_no_suggestion()
    {
        await using var plugin = Start("folder-stats-python");
        var csv = plugin.Manifest.Contributes!.Previews!.Single(p => p.Id == "csv");
        var preview = new ExternalPreviewPlugin(plugin, csv with { Suggest = Tables(plugin).Suggest });

        Assert.Null(await preview.SuggestAsync(SnapshotWithCsv(3), HostFor(plugin), TestToken));
    }

    [Fact]
    public async Task Previews_without_card_flag_get_the_default_card()
    {
        await using var plugin = Start("project-modules-python");
        var contribution = plugin.Manifest.Contributes!.Previews![0];
        Assert.False(contribution.Card);
        var preview = new ExternalPreviewPlugin(plugin, contribution);
        var entry = new VfsEntry("package.json", Path.Combine(_folder, "package.json"), VfsEntryKind.File, 10);

        var card = await preview.LoadCardAsync(new PreviewCardRequest(entry, null, null), HostFor(plugin), TestToken);

        Assert.Empty(card.Items);
        Assert.Equal(CardStatus.None, card.Status);
        Assert.Contains(card.Actions, a => a.Target == entry.Path);
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
    public async Task Web_preview_round_trip()
    {
        var folder = Path.Combine(_folder, "web-tour");
        Directory.CreateDirectory(Path.Combine(folder, "web"));
        File.WriteAllText(Path.Combine(folder, "web", "index.html"), "<html></html>");
        File.Copy(Path.Combine(RepositoryRoot(), "plugins-sdk", "python", "c4k.py"), Path.Combine(folder, "c4k.py"));
        File.WriteAllText(Path.Combine(folder, ManifestReader.FileName), """
            { "id": "tests.web-tour", "name": "Web tour", "version": "1.0.0", "author": "t", "description": "d",
              "runtime": { "type": "python", "entry": "main.py", "version": ">=3.9" },
              "permissions": [],
              "contributes": { "previews": [ { "id": "view", "title": "View", "extensions": [".webtest"], "webRoot": "web" } ] } }
            """);
        File.WriteAllText(Path.Combine(folder, "main.py"), """
            import c4k

            @c4k.handler("preview.render")
            def render(params):
                return {"kind": "web", "page": "index.html", "state": {"n": 1}}

            c4k.serve()
            """);
        await using var plugin = StartFrom(folder);
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var entry = new VfsEntry("a.webtest", Path.Combine(_folder, "a.webtest"), VfsEntryKind.File, 0);

        var web = Assert.IsType<WebPreview>(await preview.LoadAsync(entry, HostFor(plugin), null, TestToken));

        Assert.Equal("tests.web-tour", web.PluginId);
        Assert.Equal("index.html", web.Page);
        Assert.Equal("{\"n\":1}", web.StateJson);
        Assert.Equal(Path.Combine(folder, "web"), web.RootFolder);
    }

    [Fact]
    public async Task Pdf_viewer_returns_a_web_preview()
    {
        await using var plugin = Start("pdf-viewer-node");
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var path = Path.Combine(_folder, "one.pdf");
        var pdf = MinimalPdf();
        await File.WriteAllBytesAsync(path, pdf, TestToken);
        var entry = new VfsEntry("one.pdf", path, VfsEntryKind.File, pdf.Length);
        var host = HostFor(plugin);

        Assert.True(preview.CanPreview(entry));
        Assert.False(preview.CanPreview(entry with { Name = "one.txt" }));
        var web = Assert.IsType<WebPreview>(await preview.LoadAsync(entry, host, null, TestToken));

        Assert.Equal("examples.pdf-viewer", web.PluginId);
        Assert.Equal("index.html", web.Page);
        Assert.EndsWith("web", web.RootFolder, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(web.RootFolder, "index.html")));
        Assert.Equal("{\"zoom\":\"page-width\"}", web.StateJson);

        using var zoom = JsonDocument.Parse("{\"zoom\": 1.5}");
        await preview.MessageAsync(entry, "saveZoom", zoom.RootElement, host, TestToken);
        var again = Assert.IsType<WebPreview>(await preview.LoadAsync(entry, host, null, TestToken));
        Assert.Equal("{\"zoom\":1.5}", again.StateJson);
    }

    /// <summary>A valid one-page PDF with a correct cross-reference table.</summary>
    private static byte[] MinimalPdf()
    {
        const string content = "BT /F1 24 Tf 20 100 Td (Hello PDF) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(invariant, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = text.Length;
        text.Append(invariant, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            text.Append(invariant, $"{offset:D10} 00000 n \n");
        text.Append(invariant, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    [Fact]
    public async Task Preview_message_round_trip()
    {
        var folder = Path.Combine(_folder, "web-msg");
        Directory.CreateDirectory(Path.Combine(folder, "web"));
        File.WriteAllText(Path.Combine(folder, "web", "index.html"), "<html></html>");
        File.Copy(Path.Combine(RepositoryRoot(), "plugins-sdk", "python", "c4k.py"), Path.Combine(folder, "c4k.py"));
        File.WriteAllText(Path.Combine(folder, ManifestReader.FileName), """
            { "id": "tests.web-msg", "name": "Web msg", "version": "1.0.0", "author": "t", "description": "d",
              "runtime": { "type": "python", "entry": "main.py", "version": ">=3.9" },
              "permissions": [],
              "contributes": { "previews": [ { "id": "view", "title": "View", "extensions": [".webtest"], "webRoot": "web" } ] } }
            """);
        File.WriteAllText(Path.Combine(folder, "main.py"), """
            import c4k

            @c4k.handler("preview.render")
            def render(params):
                return c4k.web_preview("index.html", {"n": 1})

            @c4k.handler("preview.message")
            def message(params):
                return {"method": params["method"], "n": params["params"]["n"] + 1, "entry": params["entry"]["name"], "preview": params["preview"]}

            c4k.serve()
            """);
        await using var plugin = StartFrom(folder);
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);
        var entry = new VfsEntry("a.webtest", Path.Combine(_folder, "a.webtest"), VfsEntryKind.File, 0);

        var web = Assert.IsType<WebPreview>(await preview.LoadAsync(entry, HostFor(plugin), null, TestToken));
        using var parameters = JsonDocument.Parse("{\"n\": 41}");
        var answer = await preview.MessageAsync(entry, "bump", parameters.RootElement, HostFor(plugin), TestToken);

        Assert.Equal("{\"n\":1}", web.StateJson);
        Assert.Equal("bump", answer.GetProperty("method").GetString());
        Assert.Equal(42, answer.GetProperty("n").GetInt32());
        Assert.Equal("a.webtest", answer.GetProperty("entry").GetString());
        Assert.Equal("view", answer.GetProperty("preview").GetString());
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

    [Theory]
    [InlineData("python")]
    [InlineData("node")]
    [InlineData("powershell")]
    public async Task Notify_target_reaches_the_ui_from_every_sdk_client(string language)
    {
        var (client, entry, version, source) = language switch
        {
            "python" => ("c4k.py", "main.py", ">=3.9", """
                import c4k

                @c4k.handler("command.execute")
                def run(params):
                    c4k.notify("hello", False, "mail-target")

                c4k.serve()
                """),
            "node" => ("c4k.js", "main.js", ">=18", """
                const c4k = require('./c4k');
                c4k.on('command.execute', async () => { await c4k.notify('hello', false, 'mail-target'); });
                """),
            _ => ("c4k.ps1", "main.ps1", ">=7", """
                . "$PSScriptRoot/c4k.ps1"
                Register-C4KHandler 'command.execute' { param($p) Show-C4KNotification 'hello' -Target 'mail-target' }
                Start-C4KPlugin
                """),
        };
        var folder = Path.Combine(_folder, "notify-" + language);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(RepositoryRoot(), "plugins-sdk", language, client), Path.Combine(folder, client));
        File.WriteAllText(Path.Combine(folder, entry), source);
        File.WriteAllText(Path.Combine(folder, ManifestReader.FileName), $$"""
            { "id": "tests.notify-{{language}}", "name": "Notify", "version": "1.0.0", "author": "t", "description": "d",
              "runtime": { "type": "{{language}}", "entry": "{{entry}}", "version": "{{version}}" },
              "permissions": [],
              "contributes": { "commands": [ { "id": "go", "title": "Go", "category": "T" } ] } }
            """);
        await using var plugin = StartFrom(folder);
        _ui.LastNotificationTarget = null;

        await new ExternalCommandPlugin(plugin).ExecuteAsync("go", HostFor(plugin), TestToken);

        Assert.Equal("hello", _ui.LastNotification);
        Assert.Equal("mail-target", _ui.LastNotificationTarget);
    }

    [Fact]
    public async Task Mail_errors_reach_the_plugin_with_code_and_data()
    {
        var folder = Path.Combine(_folder, "mail-errors");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(RepositoryRoot(), "plugins-sdk", "python", "c4k.py"), Path.Combine(folder, "c4k.py"));
        File.WriteAllText(Path.Combine(folder, ManifestReader.FileName), """
            { "id": "tests.mail-errors", "name": "Mail errors", "version": "1.0.0", "author": "t", "description": "d",
              "runtime": { "type": "python", "entry": "main.py", "version": ">=3.9" },
              "permissions": ["mail"],
              "contributes": { "commands": [ { "id": "go", "title": "Go", "category": "T" } ] } }
            """);
        File.WriteAllText(Path.Combine(folder, "main.py"), """
            import c4k

            ACCOUNT = {"imap": "imap.example.org:993", "smtp": "smtp.example.org:465", "user": "anna", "interactive": False}

            def attempt(call):
                try:
                    call()
                    return "ok"
                except c4k.HostError as error:
                    return f"{error.code} {error.data}"

            @c4k.handler("command.execute")
            def go(params):
                lines = [str(len(c4k.mail_folders(ACCOUNT)))]
                lines.append(attempt(lambda: c4k.mail_sync(ACCOUNT, "INBOX")))
                lines.append(attempt(lambda: c4k.mail_set_flags(ACCOUNT, "INBOX", [1], ["seen"], [])))
                lines.append(attempt(lambda: c4k.mail_set_flags(ACCOUNT, "INBOX", [1], ["bogus"], [])))
                lines.append(attempt(lambda: c4k.call("mail.fetch", account=ACCOUNT, folder="INBOX", path="x")))
                lines.append(attempt(lambda: c4k.mail_fetch(ACCOUNT, "INBOX", -1, "x")))
                lines.append(attempt(lambda: c4k.call("mail.saveAttachment", path="x", target="y")))
                lines.append(attempt(lambda: c4k.mail_set_flags(ACCOUNT, "INBOX", [-5], ["seen"], [])))
                return {"text": {"title": "mail", "content": "|".join(lines)}}

            c4k.serve()
            """);
        await using var plugin = StartFrom(folder, new ErrorMailBackend());

        await new ExternalCommandPlugin(plugin).ExecuteAsync("go", HostFor(plugin), TestToken);

        Assert.Equal(
            "1|-32010 {'authRequired': True, 'realm': 'anna@imap.example.org'}|-32011 {'host': 'imap.example.org:993', 'fingerprint': 'AB12'}|-32602 None|-32602 None|-32602 None|-32602 None|-32602 None",
            _ui.LastText);
        Assert.Contains(_audit.Recent("tests.mail-errors"), e => e.Operation == "mail.sync" && e.Outcome == AuditOutcome.Failed);
    }

    private sealed class ErrorMailBackend : IMailBackend
    {
        public Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(string pluginId, MailAccount account, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MailFolderInfo>>([new MailFolderInfo("INBOX", "Odebrane", '/', MailSpecialFolder.Inbox, 1, 0, 0)]);

        public Task<MailSyncResult> SyncAsync(string pluginId, MailAccount account, MailSyncRequest request, CancellationToken ct) =>
            throw new MailAuthenticationRequiredException("anna@imap.example.org");

        public Task<long> FetchAsync(string pluginId, MailAccount account, string folder, uint uid, Stream destination, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SetFlagsAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken ct) =>
            throw new MailCertificateUntrustedException("imap.example.org:993", "AB12");

        public Task MoveAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<uint?> AppendAsync(string pluginId, MailAccount account, string folder, Stream message, MailFlags flags, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> SendAsync(string pluginId, MailAccount account, Stream message, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Upload_client_receives_and_provides()
    {
        // The app keeps one host per plugin: the session and the plugin's own requests share it, so read grants apply.
        var discovered = ManifestReader.Read(Path.Combine(Examples, "upload-client-python"));
        Assert.True(discovered.IsValid, string.Join("; ", discovered.Errors));
        var missing = RuntimeCatalog.CheckRequirement(discovered.Manifest!.Runtime, _runtimes);
        Assert.SkipWhen(missing is not null, missing ?? string.Empty);
        var host = HostFor(discovered.Manifest);
        await using var plugin = new ExternalPlugin(discovered, _runtimes.First(r => r.Type == discovered.Manifest.Runtime.Type), host, _audit, _storage);
        using var server = new RecordingServer("{\"ok\":true}");
        _storage.Set("examples.upload-client", "endpoints", new System.Text.Json.Nodes.JsonArray(server.Url));
        var session = new ExternalPanelPlugin(plugin, plugin.Manifest.Contributes!.Panels!.Single(p => p.Id == "endpoints"))
            .CreateSession(new PanelRequest(null, null), host);
        var a = Path.Combine(_folder, "a.txt");
        var b = Path.Combine(_folder, "b.txt");
        File.WriteAllText(a, "alpha");
        File.WriteAllText(b, "beta");
        var items = new[] { a, b }.Select(f => new TransferItem(f, Path.GetFileName(f), TransferItemKind.File, new FileInfo(f).Length)).ToList();

        var receivers = await session.GetReceiversAsync(items, TestToken);

        var receiver = Assert.Single(receivers);
        Assert.True(receiver.Multiple);
        Assert.True(receiver.ReturnsMessage);

        // A folder is refused per item and not counted: N/M and the progress count files only.
        var withFolder = items.Append(new TransferItem(_folder, Path.GetFileName(_folder), TransferItemKind.Folder, 0)).ToList();
        var progress = new SyncProgress();
        var result = await session.ReceiveAsync(receiver.Id, withFolder, progress, TestToken);

        Assert.Contains("2/2", result.Text, StringComparison.Ordinal);
        Assert.Equal([1, 2], progress.Items.Select(p => p.Done));
        Assert.All(progress.Items, p => Assert.Equal(2, p.Total));
        Assert.Equal(9, progress.Items[^1].Bytes);
        var refused = Assert.Single(result.Results!, r => !r.Ok);
        Assert.Equal(_folder, refused.Path);
        Assert.Equal(2, server.Posts.Count);
        Assert.All(server.Posts, post => Assert.Contains("multipart/form-data", post.ContentType, StringComparison.Ordinal));
        Assert.Contains(server.Posts, post => post.Body.Contains("alpha", StringComparison.Ordinal));
        Assert.Contains(server.Posts, post => post.Body.Contains("beta", StringComparison.Ordinal));

        var providers = await session.GetProvidersAsync([server.Url], TestToken);
        Assert.Contains(providers, p => p.Id == "save-response");

        var provideProgress = new SyncProgress();
        var provided = await session.ProvideAsync("save-response", [server.Url, server.Url + "api/data", server.Url + "missing"], provideProgress, TestToken);

        Assert.Equal(3, provided.Files.Count);
        Assert.Equal(3, provided.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(provided.Files, f => Assert.EndsWith(".json", f.Name, StringComparison.Ordinal));
        Assert.Equal("{\"ok\":true}", provided.Files[0].Text);
        Assert.Equal("{\"ok\":true}", provided.Files[1].Text);
        Assert.Contains("api_data", provided.Files[1].Name, StringComparison.Ordinal);

        // A failed GET is that file's error, never an error text saved as the file.
        Assert.Null(provided.Files[2].Text);
        Assert.Contains("404", provided.Files[2].Error, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], provideProgress.Items.Select(p => p.Done));

        // An address that is not http(s) is refused with an error, not silently dropped.
        _ui.PromptAnswer = "ftp://example.com/";
        await session.ExecuteActionAsync("add", [], TestToken);
        Assert.True(_ui.LastNotificationWasError);
        Assert.Contains("ftp://example.com/", _ui.LastNotification, StringComparison.Ordinal);
        Assert.Equal([server.Url], ((System.Text.Json.Nodes.JsonArray)_storage.Get("examples.upload-client", "endpoints")!).Select(n => n!.GetValue<string>()));
    }

    /// <summary>Records progress on the reporting thread (Progress&lt;T&gt; would post it later).</summary>
    private sealed class SyncProgress : IProgress<TransferProgress>
    {
        private readonly Lock _gate = new();
        private readonly List<TransferProgress> _items = [];

        public IReadOnlyList<TransferProgress> Items
        {
            get
            {
                lock (_gate)
                {
                    return [.. _items];
                }
            }
        }

        public void Report(TransferProgress value)
        {
            lock (_gate)
            {
                _items.Add(value);
            }
        }
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
    public async Task Git_repo_catalog_plugin_previews_a_repository()
    {
        Assert.SkipWhen(!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Any(d => File.Exists(Path.Combine(d.Trim(), "git.exe"))), "git is not installed");
        var repository = Path.Combine(_folder, "repo");
        Directory.CreateDirectory(repository);
        await GitAsync(repository, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repository, "readme.md"), "x", TestToken);
        await GitAsync(repository, "add", ".");
        await GitAsync(repository, "-c", "user.name=Ala", "-c", "user.email=a@b.c", "commit", "-m", "Pierwszy | commit");
        await GitAsync(repository, "tag", "v1.0");
        await GitAsync(repository, "remote", "add", "origin", "https://user:secret@example.com/r.git");
        await File.WriteAllTextAsync(Path.Combine(repository, "zażółć.txt"), "y", TestToken);

        await using var plugin = StartFrom(Path.Combine(RepositoryRoot(), "plugins-catalog", "git-repo-powershell"));
        var host = HostFor(plugin);
        var preview = new ExternalPreviewPlugin(plugin, plugin.Manifest.Contributes!.Previews![0]);

        var markdown = Assert.IsType<MarkdownPreview>(await preview.LoadWithOptionsAsync(new VfsEntry("repo", repository, VfsEntryKind.Directory), host, null, TestToken));

        Assert.Contains("**main**", markdown.Markdown, StringComparison.Ordinal);
        Assert.Contains(@"Pierwszy \| commit", markdown.Markdown, StringComparison.Ordinal);
        Assert.Contains("| nieśledzony | zażółć.txt |", markdown.Markdown, StringComparison.Ordinal);
        Assert.Contains("`v1.0`", markdown.Markdown, StringComparison.Ordinal);
        Assert.Contains("https://example.com/r.git", markdown.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", markdown.Markdown, StringComparison.Ordinal);

        var plain = Assert.IsType<MarkdownPreview>(await preview.LoadWithOptionsAsync(new VfsEntry("plugin-data", _folder, VfsEntryKind.Directory), host, null, TestToken));
        Assert.Contains("nie jest repozytorium", plain.Markdown, StringComparison.Ordinal);
        Assert.All(_audit.Recent("official.git-repo"), e => Assert.NotEqual(AuditOutcome.Denied, e.Outcome));

        // git's object files are read-only, which Directory.Delete refuses.
        foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static async Task GitAsync(string folder, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(TestToken);
        await process.StandardOutput.ReadToEndAsync(TestToken);
        await process.WaitForExitAsync(TestToken);
        Assert.True(process.ExitCode == 0, await error);
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

        var dashboard = Assert.IsType<BuiltIn.Dashboard.DashboardPreview>(await BuiltIn.Previews.FolderViews.LoadAsync(config, config.Enter!, "enter", folder, host, null, TestToken));
        Assert.Equal([Path.Combine(photos, "logs"), "c4k:run/enter/1"], dashboard.Slots.Select(s => s.Shortcut.Target));

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
    [InlineData("upload-client-python", "python", "c4k.py")]
    [InlineData("pdf-viewer-node", "node", "c4k.js")]
    [InlineData("../../plugins-catalog/steam-library-powershell", "powershell", "c4k.ps1")]
    [InlineData("../../plugins-catalog/json-preview-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/openapi-preview-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/html-preview-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/pe-info-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/archive-info-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/git-repo-powershell", "powershell", "c4k.ps1")]
    [InlineData("../../plugins-catalog/mail-powershell", "powershell", "c4k.ps1")]
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

    private string WriteBackgroundPlugin(string id, string permissions, bool contributesBackground)
    {
        var folder = Path.Combine(_folder, id);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(RepositoryRoot(), "plugins-sdk", "python", "c4k.py"), Path.Combine(folder, "c4k.py"));
        File.WriteAllText(Path.Combine(folder, "main.py"), """
            import c4k
            ticks = [{"more": True}, {}, None]

            @c4k.handler("background.tick")
            def tick(p):
                return ticks.pop(0) if ticks else None

            @c4k.handler("try.wake")
            def try_wake(p):
                try:
                    c4k.background_wake()
                    return "ok"
                except c4k.HostError as e:
                    return "denied " + str(e.code)

            c4k.serve()
            """);
        var contributes = contributesBackground
            ? """{ "background": { "interval": "5m" }, "commands": [{ "id": "x", "title": "X" }] }"""
            : """{ "commands": [{ "id": "x", "title": "X" }] }""";
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            { "id": "{{id}}", "name": "Bg", "version": "1.0.0", "author": "a", "description": "d",
              "runtime": { "type": "python", "entry": "main.py" }, "permissions": {{permissions}}, "contributes": {{contributes}} }
            """);
        return folder;
    }

    [Fact]
    public async Task Tick_parses_more_true_empty_object_and_null()
    {
        var plugin = StartFrom(WriteBackgroundPlugin("test.bgtick", """["background"]""", true));
        _started.Add(plugin);

        Assert.True(await plugin.TickAsync(TestToken));
        Assert.False(await plugin.TickAsync(TestToken));
        Assert.False(await plugin.TickAsync(TestToken));
    }

    [Fact]
    public async Task Wake_raises_event_with_permission_and_is_denied_without()
    {
        var allowed = StartFrom(WriteBackgroundPlugin("test.bgwake", """["background"]""", true));
        var denied = StartFrom(WriteBackgroundPlugin("test.bgdeny", """[]""", false));
        _started.Add(allowed);
        _started.Add(denied);
        var woken = new List<string>();
        allowed.WakeRequested += woken.Add;
        denied.WakeRequested += woken.Add;

        Assert.Equal("ok", (await allowed.CallAsync("try.wake", null, TestToken)).GetString());
        Assert.Equal(["test.bgwake"], woken);
        Assert.StartsWith("denied", (await denied.CallAsync("try.wake", null, TestToken)).GetString(), StringComparison.Ordinal);
        Assert.Equal(["test.bgwake"], woken);
        Assert.Contains(_audit.Recent("test.bgdeny"), e => e.Operation == "background.wake" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public void Background_selection_needs_contribution_and_permission()
    {
        var both = StartFrom(WriteBackgroundPlugin("test.sel1", """["background"]""", true));
        var noPermission = StartFrom(WriteBackgroundPlugin("test.sel2", """[]""", false));
        _started.Add(both);
        _started.Add(noPermission);
        // Contribution without permission cannot pass the manifest check, so build that case from a manifest copy.
        var contributesOnly = new ExternalPlugin(
            new DiscoveredPlugin(both.Folder, both.Manifest with { Permissions = [] }, []),
            _runtimes.First(r => r.Type == "python"),
            HostFor(both.Manifest with { Permissions = [] }), _audit, _storage);
        _started.Add(contributesOnly);

        var selected = BackgroundTickLoop.Select([both, noPermission, contributesOnly]).Select(p => p.Manifest.Id);

        Assert.Equal(["test.sel1"], selected);
    }

    private ExternalPlugin Start(string example) => StartFrom(Path.Combine(Examples, example));

    private ExternalPlugin StartFrom(string folder, IMailBackend? mail = null)
    {
        var discovered = ManifestReader.Read(folder);
        Assert.True(discovered.IsValid, string.Join("; ", discovered.Errors));
        var missing = RuntimeCatalog.CheckRequirement(discovered.Manifest!.Runtime, _runtimes);
        Assert.SkipWhen(missing is not null, missing ?? string.Empty);
        var runtime = _runtimes.First(r => r.Type == discovered.Manifest.Runtime.Type);
        return new ExternalPlugin(discovered, runtime, new ScopedPluginHost(discovered.Manifest, _ui, _router, _http, _audit, null, mail), _audit, _storage);
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

        public bool LastNotificationWasError { get; private set; }

        public string? LastNotificationTarget { get; set; }

        public void Notify(string message, bool isError, string? target) =>
            (LastNotification, LastNotificationWasError, LastNotificationTarget) = (message, isError, target);

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

    private sealed class RecordingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<(string ContentType, string Body)> _posts = [];

        public RecordingServer(string response)
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/";
            socket.Stop();
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        if (context.Request.HttpMethod == "POST")
                        {
                            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                            var body = await reader.ReadToEndAsync();
                            lock (_posts)
                            {
                                _posts.Add((context.Request.ContentType ?? string.Empty, body));
                            }
                        }

                        // Paths containing "missing" answer 404, so error handling can be tested.
                        var missing = context.Request.Url?.AbsolutePath.Contains("missing", StringComparison.Ordinal) == true;
                        var bytes = Encoding.UTF8.GetBytes(missing ? "not here" : response);
                        context.Response.StatusCode = missing ? 404 : 200;
                        context.Response.ContentType = "application/json";
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

        public IReadOnlyList<(string ContentType, string Body)> Posts
        {
            get
            {
                lock (_posts)
                {
                    return [.. _posts];
                }
            }
        }

        public void Dispose() => _listener.Close();
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
