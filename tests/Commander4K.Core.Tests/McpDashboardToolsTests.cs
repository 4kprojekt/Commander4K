using System.Text;
using System.Text.Json.Nodes;
using Commander4K.Core.Execution;
using Commander4K.Core.Folders;
using Commander4K.Core.Mcp;
using Commander4K.Core.Vfs;

namespace Commander4K.Core.Tests;

public sealed class McpDashboardToolsTests
{
    private static readonly McpCardPreview Json = new("official.json/json", "JSON", "Shows JSON", [".json"]);

    private readonly McpFileToolsTests.MemFs _fs = new();
    private readonly IReadOnlyList<McpTool> _tools;

    public McpDashboardToolsTests()
    {
        _fs.Dirs.Add("mfs://d");
        _tools = McpDashboardTools.Create(new VfsRouter(new ResilientAsyncExecutor(), [_fs]), () => [Json]);
    }

    private Task<McpToolResult> Call(string name, params (string Key, string Value)[] args)
    {
        var json = new JsonObject();
        foreach (var (key, value) in args)
        {
            json[key] = value;
        }

        return _tools.Single(t => t.Definition.Name == name).Handler(new McpCall("test", json), TestContext.Current.CancellationToken);
    }

    private static FolderConfig Parse(string tiles) =>
        FolderConfig.Parse(@"C:\Dom", $$"""{ "enter": { "view": "dashboard", "tiles": [ {{tiles}} ] }, "preview": "dashboard" }""");

    [Fact]
    public void Heading_tiles_parse_and_take_a_whole_row()
    {
        var config = Parse("""{ "title": "Serwery", "heading": true, "size": "small" }, { "title": "A", "open": "a" }""");
        var heading = config.Enter!.Tiles[0];

        Assert.True(heading.Heading);
        Assert.Equal(TileSize.Full, heading.LayoutSize);
        Assert.Null(heading.Card);
    }

    [Fact]
    public void Analyze_reports_errors_warnings_and_draws_the_grid()
    {
        var config = Parse("""
            { "title": "Stan", "heading": true },
            { "title": "Pliki", "card": "files" },
            { "title": "Konfig", "open": "c.json", "card": "official.json/json" },
            { "title": "Brak", "open": "x.yaml", "card": "someone.yaml" },
            { "title": "VPS", "card": "ssh", "open": "sftp://u@h/" },
            { "title": "Szybko", "card": "clock", "refresh": "3s" },
            { "title": "Szybko", "card": "disks", "size": "wide" }
            """);

        var report = McpDashboardTools.Analyze(config, [Json]);

        Assert.True(report.Errors.Count == 1, string.Join(" | ", report.Errors));
        Assert.Single(report.Errors, e => e.Contains("\"Pliki\"", StringComparison.Ordinal) && e.Contains("needs \"open\"", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("someone.yaml", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("SSH cards wait", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("aggressive", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("title repeats", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("official.json", StringComparison.Ordinal));
        Assert.Contains("4 columns", report.Layout, StringComparison.Ordinal);
        Assert.Contains("═ ═ ═ ═", report.Layout, StringComparison.Ordinal);
        Assert.Contains("B C D E", report.Layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Picture_counts_empty_cells()
    {
        var picture = McpDashboardTools.Picture("enter", Parse("""{ "title": "A", "open": "a", "size": "large" }, { "title": "B", "open": "b" }""").Enter!.Tiles);

        Assert.Contains("A A B · ", picture, StringComparison.Ordinal);
        Assert.Contains("empty cell", picture, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_refuses_errors_and_writes_valid_files()
    {
        var bad = await Call("save_dashboard", ("folder", "mfs://d"), ("config", """{ "enter": { "view": "dashboard", "tiles": [ { "title": "P", "card": "files" } ] } }"""));
        Assert.True(bad.IsError);
        Assert.Empty(_fs.Files);

        var good = await Call("save_dashboard", ("folder", "mfs://d"), ("config", """{ "title": "Dom", "enter": { "view": "dashboard", "tiles": [ { "title": "Zegar", "card": "clock" } ] } }"""));
        Assert.False(good.IsError, good.Text);
        var written = Encoding.UTF8.GetString(_fs.Files["mfs://d/c4k.dir"]);
        Assert.Contains("\"title\": \"Dom\"", written, StringComparison.Ordinal);

        var read = await Call("get_dashboard", ("folder", "mfs://d"));
        Assert.Contains("Zegar", read.Text, StringComparison.Ordinal);
        Assert.Contains("No errors.", read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guide_lists_installed_card_plugins_and_save_is_a_confirmed_write()
    {
        var guide = await Call("dashboard_guide");

        Assert.Contains("`official.json/json` — JSON", guide.Text, StringComparison.Ordinal);
        Assert.Contains("heading", guide.Text, StringComparison.Ordinal);
        var save = _tools.Single(t => t.Definition.Name == "save_dashboard").Definition;
        Assert.True(save.Writes);
        Assert.Equal(McpToolMode.Ask, save.DefaultMode);
        Assert.Equal(["folder"], save.PathArguments);
    }
}
