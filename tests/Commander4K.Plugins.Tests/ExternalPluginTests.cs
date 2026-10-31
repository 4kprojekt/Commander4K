using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    private readonly List<ExternalPlugin> _started = [];

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
                { "kind": "chart", "chart": { "kind": "bar", "unit": "req", "series": [ { "name": "GET", "points": [ { "x": 0, "y": 3, "label": "GET" } ] } ] } },
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
        Assert.Equal(3, chart.Series[0].Points[0].Y);
        Assert.Equal(4, card.Items.Count);
        Assert.Equal("https://example.com", Assert.Single(card.Actions).Target);
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
    [InlineData("pdf-viewer-node", "node", "c4k.js")]
    [InlineData("../../plugins-catalog/steam-library-powershell", "powershell", "c4k.ps1")]
    [InlineData("../../plugins-catalog/json-preview-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/openapi-preview-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/html-preview-python", "python", "c4k.py")]
    [InlineData("../../plugins-catalog/git-repo-powershell", "powershell", "c4k.ps1")]
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
