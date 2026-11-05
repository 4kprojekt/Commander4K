using System.Text.Json.Nodes;
using Commander4K.Core.Mcp;

namespace Commander4K.Core.Tests;

public sealed class McpServerTests
{
    private sealed class FakeTools : IMcpToolHandler
    {
        public Func<McpCall, CancellationToken, Task<McpToolResult>> OnEcho { get; set; } =
            (call, _) => Task.FromResult(new McpToolResult(call.Required("text")));

        public IReadOnlyList<McpToolDefinition> List() =>
        [
            new McpToolDefinition("echo", "Echoes text", McpSchema.Object(("text", "string", "What to echo", true)), McpToolMode.Allow),
        ];

        public Task<McpToolResult> CallAsync(string name, McpCall call, CancellationToken ct) => OnEcho(call, ct);
    }

    private static (McpServer Server, McpSession Session, FakeTools Tools) Create()
    {
        var tools = new FakeTools();
        var prompts = new List<McpPrompt>
        {
            new("p", "A prompt", [new McpPromptArgument("path", "Where", true)],
                (args, _) => Task.FromResult("Look at " + args["path"] + " for " + args.ClientName)),
        };
        var server = new McpServer(tools, () => prompts, "1.2.3");
        return (server, server.CreateSession(), tools);
    }

    private static JsonObject Request(int id, string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
    };

    [Fact]
    public async Task Initialize_returns_server_info_and_negotiated_version()
    {
        var (server, session, _) = Create();
        var reply = await server.HandleAsync(Request(1, "initialize", new()
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "2.0" },
        }), session, TestContext.Current.CancellationToken);

        Assert.Equal("2025-03-26", (string?)reply!["result"]!["protocolVersion"]);
        Assert.Equal("commander4k", (string?)reply["result"]!["serverInfo"]!["name"]);
        Assert.Equal("1.2.3", (string?)reply["result"]!["serverInfo"]!["version"]);
        Assert.False((bool)reply["result"]!["capabilities"]!["tools"]!["listChanged"]!);
        Assert.Equal("claude-code", session.ClientName);
    }

    [Fact]
    public async Task Initialize_carries_the_instructions_built_for_each_session()
    {
        var installed = "official.mail";
        var server = new McpServer(new FakeTools(), () => [], "1", () => "Installed plugins: " + installed);

        var first = await server.HandleAsync(Request(1, "initialize", new() { ["protocolVersion"] = "2025-06-18" }), server.CreateSession(), TestContext.Current.CancellationToken);
        installed = "official.git-repo";
        var second = await server.HandleAsync(Request(1, "initialize", new() { ["protocolVersion"] = "2025-06-18" }), server.CreateSession(), TestContext.Current.CancellationToken);

        Assert.Equal("Installed plugins: official.mail", (string?)first!["result"]!["instructions"]);
        Assert.Equal("Installed plugins: official.git-repo", (string?)second!["result"]!["instructions"]);
    }

    [Fact]
    public async Task Unknown_protocol_version_gets_the_latest()
    {
        var (server, session, _) = Create();
        var reply = await server.HandleAsync(Request(1, "initialize", new() { ["protocolVersion"] = "1999-01-01" }), session, TestContext.Current.CancellationToken);

        Assert.Equal("2025-06-18", (string?)reply!["result"]!["protocolVersion"]);
    }

    [Fact]
    public async Task Tools_list_and_call_map_to_the_handler()
    {
        var (server, session, _) = Create();

        var list = await server.HandleAsync(Request(1, "tools/list"), session, TestContext.Current.CancellationToken);
        var tool = list!["result"]!["tools"]!.AsArray().Single()!;
        Assert.Equal("echo", (string?)tool["name"]);
        Assert.Equal("object", (string?)tool["inputSchema"]!["type"]);

        var call = await server.HandleAsync(Request(2, "tools/call", new()
        {
            ["name"] = "echo",
            ["arguments"] = new JsonObject { ["text"] = "hi" },
        }), session, TestContext.Current.CancellationToken);
        Assert.Equal("hi", (string?)call!["result"]!["content"]![0]!["text"]);
        Assert.Equal("text", (string?)call["result"]!["content"]![0]!["type"]);
        Assert.False((bool)call["result"]!["isError"]!);
    }

    [Fact]
    public async Task Tool_errors_come_back_as_isError_not_jsonrpc_errors()
    {
        var (server, session, tools) = Create();
        tools.OnEcho = (_, _) => throw new IOException("disk gone");

        var reply = await server.HandleAsync(Request(1, "tools/call", new() { ["name"] = "echo" }), session, TestContext.Current.CancellationToken);

        Assert.Null(reply!["error"]);
        Assert.True((bool)reply["result"]!["isError"]!);
        Assert.Equal("disk gone", (string?)reply["result"]!["content"]![0]!["text"]);

        var unknown = await server.HandleAsync(Request(2, "tools/call", new() { ["name"] = "nope" }), session, TestContext.Current.CancellationToken);
        Assert.Equal(-32602, (int)unknown!["error"]!["code"]!);
    }

    [Fact]
    public async Task Unknown_method_is_32601_and_notification_returns_null()
    {
        var (server, session, _) = Create();

        var reply = await server.HandleAsync(Request(1, "does/not/exist"), session, TestContext.Current.CancellationToken);
        Assert.Equal(-32601, (int)reply!["error"]!["code"]!);

        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
        Assert.Null(await server.HandleAsync(notification, session, TestContext.Current.CancellationToken));

        var ping = await server.HandleAsync(Request(3, "ping"), session, TestContext.Current.CancellationToken);
        Assert.NotNull(ping!["result"]);
    }

    [Fact]
    public async Task Cancelled_notification_cancels_the_running_call()
    {
        var (server, session, tools) = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tools.OnEcho = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new McpToolResult("never");
        };

        var running = server.HandleAsync(Request(7, "tools/call", new() { ["name"] = "echo" }), session, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await server.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject { ["requestId"] = 7 },
        }, session, TestContext.Current.CancellationToken);

        var reply = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True((bool)reply!["result"]!["isError"]!);
        Assert.Contains("cancelled", (string?)reply["result"]!["content"]![0]!["text"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompts_get_renders_arguments()
    {
        var (server, session, _) = Create();

        var list = await server.HandleAsync(Request(1, "prompts/list"), session, TestContext.Current.CancellationToken);
        Assert.Equal("p", (string?)list!["result"]!["prompts"]![0]!["name"]);

        var reply = await server.HandleAsync(Request(2, "prompts/get", new()
        {
            ["name"] = "p",
            ["arguments"] = new JsonObject { ["path"] = "C:\\x" },
        }), session, TestContext.Current.CancellationToken);

        Assert.Equal("user", (string?)reply!["result"]!["messages"]![0]!["role"]);
        Assert.Contains("C:\\x", (string?)reply["result"]!["messages"]![0]!["content"]!["text"], StringComparison.Ordinal);
        // The prompt knows which client asked (McpPrompts audits it as mcp:<client>).
        Assert.Contains("for unknown", (string?)reply["result"]!["messages"]![0]!["content"]!["text"], StringComparison.Ordinal);
    }
}
