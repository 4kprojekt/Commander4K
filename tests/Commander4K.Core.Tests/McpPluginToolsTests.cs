using System.Text.Json.Nodes;
using Commander4K.Core.Mcp;
using Commander4K.Core.Plugins;

namespace Commander4K.Core.Tests;

public sealed class McpPluginToolsTests
{
    private readonly AuditLog _audit = new(null);
    private readonly IReadOnlyList<McpTool> _tools;

    public McpPluginToolsTests() =>
        _tools = McpPluginTools.Create(_ => null, Path.Combine(Path.GetTempPath(), "c4k-none-" + Guid.NewGuid().ToString("N")), _audit);

    private Task<McpToolResult> Call(string name, JsonObject args) =>
        _tools.Single(t => t.Definition.Name == name).Handler(new McpCall("test", args), TestContext.Current.CancellationToken);

    private static JsonObject Files(string path, string content) =>
        new() { ["files"] = new JsonArray(new JsonObject { ["path"] = path, ["content"] = content }) };

    [Fact]
    public async Task Check_plugin_reports_bad_manifest()
    {
        var result = await Call("check_plugin", Files("plugin.json", "{}"));
        Assert.False(result.IsError);
        Assert.StartsWith("plugin.json is not valid", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_plugin_reports_sdk_bypass_and_ok()
    {
        const string manifest = """
            { "id": "ai.t", "name": "T", "version": "1.0.0", "author": "a", "description": "d",
              "runtime": { "type": "python", "entry": "main.py", "version": ">=3.9" }, "permissions": [],
              "contributes": { "commands": [ { "id": "hi", "title": "Hi", "category": "AI" } ] } }
            """;
        var args = new JsonObject
        {
            ["files"] = new JsonArray(
                new JsonObject { ["path"] = "plugin.json", ["content"] = manifest },
                new JsonObject { ["path"] = "main.py", ["content"] = "x = open('a')\n" }),
        };
        var bad = await Call("check_plugin", args);
        Assert.Contains("bypasses the Commander4K SDK", bad.Text, StringComparison.Ordinal);
        args["files"]![1]!["content"] = "import c4k\nc4k.log('x')\n";
        var ok = await Call("check_plugin", args);
        Assert.StartsWith("OK: T (ai.t) 1.0.0, runtime python", ok.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_plugin_needs_exactly_one_source()
    {
        Assert.True((await Call("check_plugin", new JsonObject())).IsError);
        var both = Files("plugin.json", "{}");
        both["folder"] = "C:/x";
        Assert.True((await Call("check_plugin", both)).IsError);
        Assert.True((await Call("check_plugin", new JsonObject { ["folder"] = "sftp://h/x" })).IsError);
    }

    [Fact]
    public async Task Get_plugin_log_returns_recent_entries()
    {
        for (var i = 0; i < 5; i++)
        {
            _audit.Record("p.one", "stderr", "line " + i, AuditOutcome.Info);
        }

        _audit.Record("p.two", "stderr", "other", AuditOutcome.Info);
        var result = await Call("get_plugin_log", new JsonObject { ["id"] = "p.one", ["count"] = 2 });
        Assert.False(result.IsError);
        Assert.Contains("line 4", result.Text, StringComparison.Ordinal);
        Assert.Contains("line 3", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 2", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("other", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_plugin_malformed_files_is_an_error()
    {
        var args = new JsonObject { ["files"] = new JsonArray(new JsonObject { ["path"] = "x" }) };
        Assert.True((await Call("check_plugin", args)).IsError);
    }
}
