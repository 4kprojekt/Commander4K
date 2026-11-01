using System.Text.Json.Nodes;
using Commander4K.Core.Folders;

namespace Commander4K.Core.Tests;

public sealed class FolderConfigWriterTests
{
    private static JsonObject Tile(string title) => new() { ["title"] = title, ["card"] = "api", ["api"] = new JsonObject { ["url"] = "https://example.com/" + title } };

    [Fact]
    public void Append_to_new_file_creates_dashboard()
    {
        var text = FolderConfigWriter.AppendTile(null, Tile("A"), replacePreview: false);
        var config = FolderConfig.Parse(@"C:\Dom", text);

        Assert.Null(config.Error);
        Assert.Equal(FolderViewIds.Dashboard, config.Enter!.View);
        Assert.Equal(FolderViewIds.Dashboard, config.Preview!.View);
        Assert.Equal("A", Assert.Single(config.Enter.Tiles).Title);
        Assert.Equal("A", Assert.Single(config.Preview.Tiles).Title);
        Assert.Contains(FolderConfig.SchemaUrl, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_keeps_other_tiles_and_keys()
    {
        const string Existing = """{ "title": "Dom", "enter": { "view": "dashboard", "tiles": [ { "title": "Old", "open": "x" } ] }, "preview": "dashboard" }""";

        var config = FolderConfig.Parse(@"C:\Dom", FolderConfigWriter.AppendTile(Existing, Tile("New"), replacePreview: false));

        Assert.Equal("Dom", config.Title);
        Assert.Equal(["Old", "New"], config.Enter!.Tiles.Select(t => t.Title));
        Assert.Equal(["Old", "New"], config.Preview!.Tiles.Select(t => t.Title));
    }

    [Fact]
    public void Append_to_preview_dashboard_object()
    {
        const string Existing = """{ "preview": { "view": "dashboard", "tiles": [ { "title": "Old", "open": "x" } ] } }""";

        var config = FolderConfig.Parse(@"C:\Dom", FolderConfigWriter.AppendTile(Existing, Tile("New"), replacePreview: false));

        Assert.Equal(["Old", "New"], config.Preview!.Tiles.Select(t => t.Title));
        Assert.Null(config.Enter);
    }

    [Fact]
    public void Replace_preview_moves_old_preview_to_empty_enter()
    {
        const string Existing = """{ "preview": "gallery" }""";

        Assert.Throws<InvalidOperationException>(() => FolderConfigWriter.AppendTile(Existing, Tile("A"), replacePreview: false));
        var config = FolderConfig.Parse(@"C:\Dom", FolderConfigWriter.AppendTile(Existing, Tile("A"), replacePreview: true));

        Assert.Equal(FolderViewIds.Dashboard, config.Preview!.View);
        Assert.Equal("A", Assert.Single(config.Preview.Tiles).Title);
        Assert.Equal(FolderViewIds.Gallery, config.Enter!.View);
    }

    [Fact]
    public void Detects_comments()
    {
        Assert.True(FolderConfigWriter.HasComments("{ // note\n \"preview\": \"gallery\" }"));
        Assert.True(FolderConfigWriter.HasComments("""{ /* a */ "preview": "gallery" }"""));
        Assert.False(FolderConfigWriter.HasComments("""{ "url": "https://example.com/a//b" }"""));
    }

    [Fact]
    public void CreateForAction_both_writes_preview_and_enter()
    {
        var text = FolderConfigWriter.CreateForAction(FolderConfigWriter.ActionFor(new PluginSdk.FolderSuggestionView(View: FolderViewIds.Gallery)), "both");
        var config = FolderConfig.Parse(@"C:\Photos", text);

        Assert.Null(config.Error);
        Assert.Equal(FolderViewIds.Gallery, config.Enter!.View);
        Assert.Equal(FolderViewIds.Gallery, config.Preview!.View);
        Assert.Contains(FolderConfig.SchemaUrl, text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateForAction_enter_only_leaves_preview_out()
    {
        var config = FolderConfig.Parse(@"C:\Photos", FolderConfigWriter.CreateForAction(JsonValue.Create("notes"), "enter"));

        Assert.Equal(FolderViewIds.Notes, config.Enter!.View);
        Assert.Null(config.Preview);
    }

    [Fact]
    public void ActionFor_plugin_view_round_trips_options()
    {
        var view = new PluginSdk.FolderSuggestionView(Plugin: "examples.folder-stats", Preview: "tables", OptionsJson: """{ "limit": 5 }""");
        var config = FolderConfig.Parse(@"C:\Data", FolderConfigWriter.CreateForAction(FolderConfigWriter.ActionFor(view), "both"));

        Assert.Equal("examples.folder-stats", config.Enter!.Plugin);
        Assert.Equal("tables", config.Enter.Preview);
        Assert.Equal(5, JsonNode.Parse(config.Enter.Options!)!["limit"]!.GetValue<int>());
    }
}
