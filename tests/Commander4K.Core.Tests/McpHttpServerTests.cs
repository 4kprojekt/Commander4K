using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Commander4K.Core.Mcp;

namespace Commander4K.Core.Tests;

public sealed class McpHttpServerTests : IAsyncDisposable
{
    private const string Token = "secret-token";
    private string? _tokenValue = Token;

    private sealed class CountingTools : IMcpToolHandler
    {
        public int Calls;
        public Func<CancellationToken, Task<McpToolResult>>? Hold;

        public IReadOnlyList<McpToolDefinition> List() =>
            [new McpToolDefinition("echo", "Echo", McpSchema.Object(), McpToolMode.Allow)];

        public Task<McpToolResult> CallAsync(string name, McpCall call, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Hold is { } hold ? hold(ct) : Task.FromResult(new McpToolResult("hi"));
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
        _http = new McpHttpServer(_server, () => _tokenValue);
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
        await using var second = new McpHttpServer(_server, () => _tokenValue);

        Assert.Equal(McpStartResult.PortBusy, second.Start(_port));
        Assert.False(second.IsRunning);
        Assert.True(_http.IsRunning);
    }

    [Fact]
    public async Task Path_with_and_without_trailing_slash_works_and_other_paths_are_not_served()
    {
        const string ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var sessionId = await InitializeAsync();
        var slash = Post(ping, session: sessionId);
        slash.RequestUri = new Uri(_http.Url + "/");
        using var withSlash = await _client.SendAsync(slash, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, withSlash.StatusCode);

        var other = Post(ping, session: sessionId);
        other.RequestUri = new Uri($"http://127.0.0.1:{_port}/other");
        using var elsewhere = await _client.SendAsync(other, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Body_over_the_limit_is_413()
    {
        var huge = new string('x', 9 * 1024 * 1024);
        using var response = await _client.SendAsync(Post(huge), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Bad_json_is_400_with_parse_error_and_batch_is_invalid_request()
    {
        using var bad = await _client.SendAsync(Post("{not json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var parseJson = JsonNode.Parse(await bad.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(-32700, (int)parseJson["error"]!["code"]!);

        using var batch = await _client.SendAsync(Post("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]"""), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        var batchJson = JsonNode.Parse(await batch.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(-32600, (int)batchJson["error"]!["code"]!);
    }

    [Fact]
    public async Task Origin_null_is_403()
    {
        using var response = await _client.SendAsync(Post(InitializeBody, origin: "null"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Unset_token_rejects_every_request(string? configured)
    {
        var sessionId = await InitializeAsync();
        _tokenValue = configured;

        using var withToken = await _client.SendAsync(Post(InitializeBody), TestContext.Current.CancellationToken);
        using var emptyBearer = await _client.SendAsync(Post(InitializeBody, token: ""), TestContext.Current.CancellationToken);
        var delete = new HttpRequestMessage(HttpMethod.Delete, _http.Url);
        delete.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var deleted = await _client.SendAsync(delete, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, withToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, emptyBearer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deleted.StatusCode);
    }

    [Fact]
    public async Task Initialize_with_non_string_version_is_invalid_params_and_creates_no_session()
    {
        const string body = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":5}}""";
        using var response = await _client.SendAsync(Post(body), TestContext.Current.CancellationToken);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(-32602, (int)json["error"]!["code"]!);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        Assert.Equal(0, _server.SessionCount);
    }

    [Fact]
    public async Task Cancelled_notification_over_http_ends_the_held_call()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tools.Hold = async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new McpToolResult("never");
        };
        var sessionId = await InitializeAsync();

        var held = _client.SendAsync(
            Post("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo"}}""", session: sessionId),
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var cancel = await _client.SendAsync(
            Post("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":5}}""", session: sessionId),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        using var response = await held.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.True((bool)json["result"]!["isError"]!);
        Assert.Contains("cancelled", (string?)json["result"]!["content"]![0]!["text"], StringComparison.Ordinal);
    }
}
