using System.Text.Json;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class WebPreviewParsingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "c4k-web-" + Guid.NewGuid().ToString("N"));

    public WebPreviewParsingTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "web"));
        File.WriteAllText(Path.Combine(_folder, "web", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(_folder, "main.js"), "// x");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Web_result_becomes_WebPreview()
    {
        var result = ExternalPreviewPlugin.ParseWeb(Json("""{"kind":"web","page":"index.html","state":{"a":1}}"""), "t.p", _folder, "web");

        var web = Assert.IsType<WebPreview>(result);
        Assert.Equal("t.p", web.PluginId);
        Assert.Equal("index.html", web.Page);
        Assert.Equal("{\"a\":1}", web.StateJson);
        Assert.EndsWith("web", web.RootFolder, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_webRoot_gives_message()
    {
        var result = ExternalPreviewPlugin.ParseWeb(Json("""{"kind":"web","page":"index.html"}"""), "t.p", _folder, null);

        Assert.Equal("This preview returned kind \"web\" but declares no webRoot in plugin.json.", Assert.IsType<MessagePreview>(result).Message);
    }

    [Theory]
    [InlineData("../main.js")]
    [InlineData(@"C:\x.html")]
    [InlineData("web/../../x")]
    [InlineData("")]
    [InlineData("missing.html")]
    public void Unsafe_pages_are_refused(string page)
    {
        var json = JsonSerializer.Serialize(new { kind = "web", page });

        var result = ExternalPreviewPlugin.ParseWeb(Json(json), "t.p", _folder, "web");

        Assert.StartsWith("Web page not found inside webRoot:", Assert.IsType<MessagePreview>(result).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData(@"C:\x")]
    [InlineData("nonexistent")]
    public void Bad_webRoot_is_an_error(string webRoot)
    {
        var manifest = new PluginManifest("t.p", "T", "1.0.0", "a", "d", [])
        {
            Runtime = new PluginRuntime("python", "main.js"),
            Contributes = new PluginContributions(Previews: [new PreviewContribution("v", "V", [".x"], WebRoot: webRoot)]),
        };

        var errors = ManifestReader.Validate(manifest, _folder);

        Assert.Contains(errors, e => e.Contains("webRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void Good_webRoot_is_accepted()
    {
        var manifest = new PluginManifest("t.p", "T", "1.0.0", "a", "d", [])
        {
            Runtime = new PluginRuntime("python", "main.js"),
            Contributes = new PluginContributions(Previews: [new PreviewContribution("v", "V", [".x"], WebRoot: "web")]),
        };

        Assert.Empty(ManifestReader.Validate(manifest, _folder));
    }

    [Fact]
    public void Dot_slash_webRoot_works()
    {
        var web = Assert.IsType<WebPreview>(ExternalPreviewPlugin.ParseWeb(Json("""{"kind":"web","page":"index.html"}"""), "t.p", _folder, "./web"));
        Assert.EndsWith("web", web.RootFolder, StringComparison.Ordinal);
        Assert.Equal("index.html", web.Page);
        Assert.Empty(ManifestReader.Validate(Manifest("./web", "main.js"), _folder));
    }

    [Theory]
    [InlineData("web", "web/main.js")]
    [InlineData("web", "web/sub/main.js")]
    [InlineData("web/ui", "web/main.js")]
    [InlineData("./web", "./web/main.js")]
    public void WebRoot_overlapping_the_entry_folder_is_an_error(string webRoot, string entry)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "web", "ui"));
        Directory.CreateDirectory(Path.Combine(_folder, "web", "sub"));
        File.WriteAllText(Path.Combine(_folder, "web", "main.js"), "//");
        File.WriteAllText(Path.Combine(_folder, "web", "sub", "main.js"), "//");

        var errors = ManifestReader.Validate(Manifest(webRoot, entry), _folder);

        Assert.Contains(errors, e => e.Contains("webRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void WebRoot_inside_a_sibling_of_the_entry_folder_is_fine()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "src"));
        File.WriteAllText(Path.Combine(_folder, "src", "main.js"), "//");

        Assert.Empty(ManifestReader.Validate(Manifest("web", "src/main.js"), _folder));
    }

    private static PluginManifest Manifest(string webRoot, string entry) => new("t.p", "T", "1.0.0", "a", "d", [])
    {
        Runtime = new PluginRuntime("node", entry),
        Contributes = new PluginContributions(Previews: [new PreviewContribution("v", "V", [".x"], WebRoot: webRoot)]),
    };
}
