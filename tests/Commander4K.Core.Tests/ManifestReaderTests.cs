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
    public void Options_are_read_for_previews_and_panels()
    {
        var plugin = Read("""[]""", """
            { "previews": [{ "id": "p", "title": "P", "extensions": ["/"], "options": [
                { "id": "imap", "type": "number", "description": "d", "values": ["a", "b"], "required": true },
                { "id": "plain" } ] }],
              "panels": [{ "id": "q", "title": "Q", "options": [{ "id": "x", "type": "boolean" }] }] }
            """);

        Assert.Empty(plugin.Errors);
        var preview = plugin.Manifest!.Contributes!.Previews![0].Options!;
        Assert.Equal(2, preview.Count);
        Assert.Equal(("imap", "number", "d", true), (preview[0].Id, preview[0].Type, preview[0].Description, preview[0].Required));
        Assert.Equal(["a", "b"], preview[0].Values!);
        Assert.Equal(("plain", "string", null, false), (preview[1].Id, preview[1].Type, preview[1].Description, preview[1].Required));
        Assert.Null(preview[1].Values);
        Assert.Equal("boolean", plugin.Manifest.Contributes.Panels![0].Options![0].Type);
    }

    [Fact]
    public void Options_skip_bad_entries_and_unknown_types()
    {
        var plugin = Read("""[]""", """
            { "previews": [{ "id": "p", "title": "P", "extensions": ["/"], "options": [
                { "id": "" }, { "type": "string" }, 5, { "id": "ok", "type": "weird" } ] }] }
            """);

        Assert.Empty(plugin.Errors);
        var options = plugin.Manifest!.Contributes!.Previews![0].Options!;
        Assert.Equal("ok", Assert.Single(options).Id);
        Assert.Equal("string", options[0].Type);
    }

    [Fact]
    public void Options_that_are_not_an_array_are_ignored()
    {
        var plugin = Read("""[]""", """{ "previews": [{ "id": "p", "title": "P", "extensions": ["/"], "options": { "id": "x" } }] }""");

        Assert.Empty(plugin.Errors);
        Assert.Null(plugin.Manifest!.Contributes!.Previews![0].Options);
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
