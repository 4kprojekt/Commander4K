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
    public void Remove_tile_updates_the_selected_dashboard_and_its_mirrored_view()
    {
        const string Existing = """{ "enter": { "view": "dashboard", "tiles": [ { "title": "A", "open": "a" }, { "title": "B", "open": "b" } ] }, "preview": "dashboard" }""";

        var config = FolderConfig.Parse(@"C:\Dom", FolderConfigWriter.RemoveTile(Existing, "preview", 0));

        Assert.Equal(["B"], config.Preview!.Tiles.Select(tile => tile.Title));
        Assert.Equal(["B"], config.Enter!.Tiles.Select(tile => tile.Title));
    }

    [Fact]
    public void Move_tile_preserves_dashboard_order_in_both_matching_sections()
    {
        const string Existing = """{ "preview": { "view": "dashboard", "tiles": [ { "title": "A", "open": "a" }, { "title": "B", "open": "b" } ] }, "enter": { "view": "dashboard", "tiles": [ { "title": "A", "open": "a" }, { "title": "B", "open": "b" } ] } }""";

        var config = FolderConfig.Parse(@"C:\Dom", FolderConfigWriter.MoveTile(Existing, "preview", 1, -1));

        Assert.Equal(["B", "A"], config.Preview!.Tiles.Select(tile => tile.Title));
        Assert.Equal(["B", "A"], config.Enter!.Tiles.Select(tile => tile.Title));
    }

    [Fact]
    public void Resize_tile_writes_size_and_small_removes_it()
    {
        const string Existing = """{ "preview": { "view": "dashboard", "tiles": [ { "title": "A", "open": "a" }, { "title": "B", "open": "b", "size": "wide" } ] }, "enter": "dashboard" }""";

        var large = FolderConfigWriter.ResizeTile(Existing, "preview", 0, TileSize.Large, expectedTitle: "A");
        var config = FolderConfig.Parse(@"C:\Dom", large);
        Assert.Equal([TileSize.Large, TileSize.Wide], config.Preview!.Tiles.Select(tile => tile.Size));

        var small = FolderConfig.Parse(@"C:\Dom", FolderConfigWriter.ResizeTile(large, "preview", 1, TileSize.Small));
        Assert.Equal([TileSize.Large, TileSize.Small], small.Preview!.Tiles.Select(tile => tile.Size));
        Assert.DoesNotContain("\"wide\"", FolderConfigWriter.ResizeTile(large, "preview", 1, TileSize.Small), StringComparison.Ordinal);
    }

    [Fact]
    public void Move_tile_rejects_moving_past_dashboard_edges()
    {
        const string Existing = """{ "preview": { "view": "dashboard", "tiles": [ { "title": "A", "open": "a" } ] } }""";

        Assert.Throws<InvalidOperationException>(() => FolderConfigWriter.MoveTile(Existing, "preview", 0, -1));
    }

    [Fact]
    public void Move_tile_resolves_stale_index_by_expected_title()
    {
        const string Existing = """{ "preview": { "view": "dashboard", "tiles": [ { "title": "A", "open": "a" }, { "title": "B", "open": "b" } ] } }""";

        var config = FolderConfig.Parse(@"C:\Dom", FolderConfigWriter.MoveTile(Existing, "preview", 1, 1, expectedTitle: "A"));

        Assert.Equal(["B", "A"], config.Preview!.Tiles.Select(tile => tile.Title));
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
    public async Task CreateNew_writes_a_new_file()
    {
        var folder = Directory.CreateTempSubdirectory("c4k-writer-").FullName;
        try
        {
            var file = Path.Combine(folder, FolderConfig.FileName);

            var (result, error) = await FolderConfigWriter.CreateNewAsync(file, "{}\n", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(FolderConfigWrite.Written, result);
            Assert.Null(error);
            Assert.Equal("{}\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task CreateNew_never_overwrites_an_existing_file()
    {
        var folder = Directory.CreateTempSubdirectory("c4k-writer-").FullName;
        try
        {
            var file = Path.Combine(folder, FolderConfig.FileName);
            await File.WriteAllTextAsync(file, "mine", TestContext.Current.CancellationToken);

            var (result, _) = await FolderConfigWriter.CreateNewAsync(file, "{}\n", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(FolderConfigWrite.AlreadyExists, result);
            Assert.Equal("mine", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task CreateNew_reports_failure_without_throwing()
    {
        var file = Path.Combine(Path.GetTempPath(), "c4k-missing-" + Guid.NewGuid().ToString("N"), FolderConfig.FileName);

        var (result, error) = await FolderConfigWriter.CreateNewAsync(file, "{}\n", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(FolderConfigWrite.Failed, result);
        Assert.False(string.IsNullOrEmpty(error));
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
