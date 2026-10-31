using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class ManifestReaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "c4k-manifest-" + Guid.NewGuid().ToString("N"));

    public ManifestReaderTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "main.py"), "# plugin");
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private DiscoveredPlugin Read(string permissions, string contributes)
    {
        File.WriteAllText(Path.Combine(_folder, ManifestReader.FileName), $$"""
            {
              "id": "acme.mail", "name": "Mail", "version": "1.0.0", "author": "a", "description": "d",
              "runtime": { "type": "python", "entry": "main.py" },
              "permissions": {{permissions}},
              "contributes": {{contributes}}
            }
            """);
        return ManifestReader.Read(_folder);
    }

    [Fact]
    public void Manifest_reads_background_and_preview_enter()
    {
        var plugin = Read("""["mail","background"]""",
            """{ "background": { "interval": "5m" }, "previews": [{ "id": "eml", "title": "Mail", "extensions": [".eml"], "enter": true }] }""");

        Assert.Empty(plugin.Errors);
        var manifest = plugin.Manifest!;
        Assert.Equal("5m", manifest.Contributes!.Background!.Interval);
        Assert.True(manifest.Contributes.Previews![0].Enter);
        Assert.True(manifest.Allows("mail"));
        Assert.True(manifest.Allows("background"));
    }

    [Fact]
    public void Background_only_plugin_is_valid()
    {
        var plugin = Read("""["background"]""", """{ "background": { "interval": "90s" } }""");

        Assert.Empty(plugin.Errors);
    }

    [Fact]
    public void Background_requires_the_permission()
    {
        var plugin = Read("""[]""", """{ "background": { "interval": "5m" } }""");

        Assert.Contains(plugin.Errors, e => e.Contains("background"));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("5x")]
    [InlineData("-5m")]
    public void Background_interval_must_parse(string interval)
    {
        var plugin = Read("""["background"]""", $$"""{ "background": { "interval": "{{interval}}" } }""");

        Assert.Contains(plugin.Errors, e => e.Contains("interval"));
    }

    [Fact]
    public void Manifest_rejects_unknown_permission_still()
    {
        var plugin = Read("""["mail.raw"]""", """{ "previews": [{ "id": "eml", "title": "Mail", "extensions": [".eml"] }] }""");

        Assert.Contains(plugin.Errors, e => e.Contains("mail.raw"));
    }

    [Fact]
    public void PreviewActionTargets_parses_action_and_arguments()
    {
        Assert.True(PreviewActionTargets.TryParse("c4k:plugin/check?account=C%3A%5CPoczta%5CFirma&x=1", out var action, out var arguments));
        Assert.Equal("check", action);
        Assert.Equal(@"C:\Poczta\Firma", arguments["account"]);
        Assert.Equal("1", arguments["x"]);
        Assert.False(PreviewActionTargets.TryParse("c4k:refresh", out _, out _));
    }
}
