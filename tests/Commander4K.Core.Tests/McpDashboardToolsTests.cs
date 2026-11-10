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
        _tools = McpDashboardTools.Create(new VfsRouter(new ResilientAsyncExecutor(), [_fs]), () => [Json], () => ["mfs://d"], () => []);
    }

    private Task<McpToolResult> Call(string name, params (string Key, string Value)[] args) => CallItems(name, null, args);

    private Task<McpToolResult> CallItems(string name, (string Name, string Json)[]? items, params (string Key, string Value)[] args)
    {
        var json = new JsonObject();
        foreach (var (key, value) in args)
        {
            json[key] = value;
        }

        if (items is not null)
        {
            json["items"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["json"] = i.Json })]);
        }

        return _tools.Single(t => t.Definition.Name == name).Handler(new McpCall("test", json), TestContext.Current.CancellationToken);
    }

    private static C4kFolder Build(params (string Name, string Json)[] items) =>
        C4kReader.Build(@"C:\Dom", items.Select(i => (i.Name + ".c4k", i.Json)));

    [Fact]
    public void Analyze_reports_errors_warnings_and_draws_the_grid()
    {
        var folder = Build(
            ("1 Pliki", """{ "card": "files" }"""),
            ("2 Konfig", """{ "open": "c.json", "card": "official.json/json" }"""),
            ("3 Brak", """{ "open": "x.yaml", "card": "someone.yaml" }"""),
            ("4 VPS", """{ "card": "ssh", "open": "sftp://u@h/" }"""),
            ("5 Szybko", """{ "title": "Z", "card": "clock", "refresh": "3s" }"""),
            ("6 Dyski", """{ "title": "Z", "card": "disks", "size": "wide" }"""));

        var report = McpDashboardTools.Analyze(folder, [Json]);

        Assert.Single(report.Errors);
        Assert.Contains("1 Pliki.c4k", report.Errors[0], StringComparison.Ordinal);
        Assert.Contains(report.Warnings, w => w.Contains("someone.yaml", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("SSH cards wait", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("aggressive", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("repeats", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("official.json", StringComparison.Ordinal));
        Assert.Contains("4 columns", report.Layout, StringComparison.Ordinal);
        Assert.Contains("A = 1 Pliki.c4k", report.Layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Picture_counts_empty_cells()
    {
        var picture = McpDashboardTools.Picture(Build(("a", """{ "open": "a", "size": "large" }"""), ("b", """{ "open": "b" }""")).Items);

        Assert.Contains("A A B · ", picture, StringComparison.Ordinal);
        Assert.Contains("empty cell", picture, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_writes_one_file_per_item_and_get_reads_them_back()
    {
        var bad = await CallItems("save_dashboard", [("a", """{ "card": "files" }""")], ("folder", "mfs://d"));
        Assert.True(bad.IsError);
        Assert.Empty(_fs.Files);

        var good = await CallItems("save_dashboard", [("10 Zegar", """{ "card": "clock" }"""), ("20 Dyski.c4k", """{ "card": "disks" }""")], ("folder", "mfs://d"));
        Assert.False(good.IsError, good.Text);
        Assert.Equal(["mfs://d/10 Zegar.c4k", "mfs://d/20 Dyski.c4k"], _fs.Files.Keys.Order().ToArray());
        Assert.Contains("\"card\": \"clock\"", Encoding.UTF8.GetString(_fs.Files["mfs://d/10 Zegar.c4k"]), StringComparison.Ordinal);

        var read = await Call("get_dashboard", ("folder", "mfs://d"));
        Assert.Contains("10 Zegar.c4k", read.Text, StringComparison.Ordinal);
        Assert.Contains("No errors.", read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_dashboard_refuses_existing_file_with_comments()
    {
        _fs.Files["mfs://d/a.c4k"] = Encoding.UTF8.GetBytes("{ // mine\n \"card\": \"clock\" }");
        _fs.Files["mfs://d/b.c4k"] = Encoding.UTF8.GetBytes("""{ "card": "disks" }""");

        var result = await CallItems("save_dashboard", [("a", """{ "card": "disks" }"""), ("b", """{ "card": "clock" }""")], ("folder", "mfs://d"));

        Assert.True(result.IsError);
        Assert.Contains("comments", result.Text, StringComparison.Ordinal);
        Assert.Contains("// mine", Encoding.UTF8.GetString(_fs.Files["mfs://d/a.c4k"]), StringComparison.Ordinal);
        Assert.Contains("disks", Encoding.UTF8.GetString(_fs.Files["mfs://d/b.c4k"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_dashboard_replaces_a_plain_file_and_protects_a_lone_view()
    {
        _fs.Files["mfs://d/view.c4k"] = Encoding.UTF8.GetBytes("""{ "view": "gallery" }""");

        var added = await CallItems("save_dashboard", [("clock", """{ "card": "clock" }""")], ("folder", "mfs://d"));
        Assert.True(added.IsError);
        Assert.Contains("exactly one item", added.Text, StringComparison.Ordinal);
        Assert.Single(_fs.Files);

        var replaced = await CallItems("save_dashboard", [("view", """{ "view": "notes" }"""), ("clock", """{ "card": "clock" }""")], ("folder", "mfs://d"));
        Assert.False(replaced.IsError, replaced.Text);
        Assert.Equal(2, _fs.Files.Count);
        Assert.Contains("notes", Encoding.UTF8.GetString(_fs.Files["mfs://d/view.c4k"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_dashboard_checks_the_folder_key_and_the_file_name()
    {
        var outside = await CallItems("save_dashboard", [("a", """{ "view": "files", "folder": "mfs://other" }""")], ("folder", "mfs://d"));
        Assert.True(outside.IsError);
        Assert.Contains("outside", outside.Text, StringComparison.Ordinal);

        var escape = await CallItems("save_dashboard", [(@"..\x", """{ "view": "files" }""")], ("folder", "mfs://d"));
        Assert.True(escape.IsError);
        Assert.Empty(_fs.Files);
    }

    [Fact]
    public async Task Guide_lists_installed_card_plugins_and_save_is_a_confirmed_write()
    {
        var guide = await Call("dashboard_guide");

        Assert.Contains("`official.json/json` — JSON", guide.Text, StringComparison.Ordinal);
        Assert.Contains("file names", guide.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("c4k.dir", guide.Text, StringComparison.Ordinal);
        var save = _tools.Single(t => t.Definition.Name == "save_dashboard").Definition;
        Assert.True(save.Writes);
        Assert.Equal(McpToolMode.Ask, save.DefaultMode);
        Assert.Equal(["folder"], save.PathArguments);
    }
}
