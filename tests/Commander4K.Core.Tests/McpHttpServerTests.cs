using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Commander4K.Core.Mcp;

namespace Commander4K.Core.Tests;

public sealed class McpHttpServerTests : IAsyncDisposable
{
    private const string Token = "secret-token";

    private sealed class CountingTools : IMcpToolHandler
    {
        public int Calls;

        public IReadOnlyList<McpToolDefinition> List() =>
            [new McpToolDefinition("echo", "Echo", McpSchema.Object(), McpToolMode.Allow)];

        public Task<McpToolResult> CallAsync(string name, McpCall call, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new McpToolResult("hi"));
        }
    }

    private readonly CountingTools _tools = new();
    private readonly McpServer _server;
    private readonly McpHttpServer _http;
    private readonly HttpClient _client = new();
    private readonly int _port;

    public McpHttpServerTests()
    {
        _server = new McpServer(_tools, () => [], "1.0.0");
        _http = new McpHttpServer(_server, () => Token);
        _port = FreePort();
        Assert.Equal(McpStartResult.Started, _http.Start(_port));
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _http.DisposeAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private HttpRequestMessage Post(string body, string? token = Token, string? session = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _http.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return request;
    }

    private const string InitializeBody =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    private async Task<string> InitializeAsync()
    {
        using var response = await _client.SendAsync(Post(InitializeBody), TestContext.Current.CancellationToken);
        return response.Headers.GetValues("Mcp-Session-Id").Single();
    }

    [Fact]
    public async Task Initialize_over_http_returns_session_header()
    {
        Assert.Equal($"http://127.0.0.1:{_port}/mcp", _http.Url);
        using var response = await _client.SendAsync(Post(InitializeBody), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var sessionId = response.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.NotNull(_server.FindSession(sessionId));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("commander4k", (string?)json["result"]!["serverInfo"]!["name"]);
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_401_and_no_tool_runs()
    {
        var sessionId = await InitializeAsync();
        const string call = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo"}}""";

        using var none = await _client.SendAsync(Post(call, token: null, session: sessionId), TestContext.Current.CancellationToken);
        using var wrong = await _client.SendAsync(Post(call, token: "nope", session: sessionId), TestContext.Current.CancellationToken);
        using var initNoToken = await _client.SendAsync(Post(InitializeBody, token: null), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, initNoToken.StatusCode);
        Assert.Equal(0, _tools.Calls);

        using var ok = await _client.SendAsync(Post(call, session: sessionId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, _tools.Calls);
    }

    [Fact]
    public async Task Foreign_origin_is_403()
    {
        using var response = await _client.SendAsync(Post(InitializeBody, origin: "https://evil.example"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _server.SessionCount);
    }

    [Fact]
    public async Task Localhost_origin_is_allowed()
    {
        using var response = await _client.SendAsync(Post(InitializeBody, origin: "http://localhost:1234"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_session_is_404_and_delete_ends_session()
    {
        const string ping = """{"jsonrpc":"2.0","id":2,"method":"ping"}""";
        using var unknown = await _client.SendAsync(Post(ping, session: "does-not-exist"), TestContext.Current.CancellationToken);
        using var missing = await _client.SendAsync(Post(ping), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var sessionId = await InitializeAsync();
        using var alive = await _client.SendAsync(Post(ping, session: sessionId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);

        var delete = new HttpRequestMessage(HttpMethod.Delete, _http.Url);
        delete.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        delete.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var deleted = await _client.SendAsync(delete, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(_server.FindSession(sessionId));

        using var after = await _client.SendAsync(Post(ping, session: sessionId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Get_is_405_and_notification_post_is_202()
    {
        var get = new HttpRequestMessage(HttpMethod.Get, _http.Url);
        get.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        using var got = await _client.SendAsync(get, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, got.StatusCode);

        var sessionId = await InitializeAsync();
        using var accepted = await _client.SendAsync(
            Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", session: sessionId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(string.Empty, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Second_server_on_the_same_port_reports_PortBusy()
    {
        await using var second = new McpHttpServer(_server, () => Token);

        Assert.Equal(McpStartResult.PortBusy, second.Start(_port));
        Assert.False(second.IsRunning);
        Assert.True(_http.IsRunning);
    }
}
