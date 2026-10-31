using System.Net;
using System.Text;
using System.Text.Json;
using Commander4K.Core.Execution;
using Commander4K.Core.Folders;
using Commander4K.Core.Plugins;
using Commander4K.Core.Security;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Dashboard;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

public sealed class ApiCardTests : IAsyncDisposable
{
    private readonly AuditLog _audit = new(null);
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly string _secrets = Path.Combine(Path.GetTempPath(), "c4k-api-secrets-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly List<HttpRequestMessage> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => Json("{}");

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync();
        File.Delete(_secrets);
    }

    [Fact]
    public async Task Fields_render_values_status_and_units()
    {
        _respond = _ => Json("""{ "cpu": 12.5, "db": "ok", "nested": { "v": "1.2" }, "up": false }""");
        var (api, host) = Create();

        var card = await api.LoadCardAsync(Request("""
            [ { "path": "cpu", "unit": "%" }, { "path": "db", "as": "status" }, { "path": "nested.v", "label": "Wersja" },
              { "path": "up", "label": "Up", "as": "status" } ]
            """), host, null, TestToken);

        Assert.Equal("12.5 %", Value(card, "cpu").Value);
        Assert.Equal(CardStatus.Ok, Value(card, "db").Status);
        Assert.Equal("1.2", Value(card, "Wersja").Value);
        Assert.Equal(CardStatus.Error, Value(card, "Up").Status);
        Assert.Equal(CardStatus.Warning, card.Status);
        Assert.EndsWith(" ms", Value(card, "Czas").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chart_fields_append_to_previous()
    {
        var n = 0;
        _respond = _ => Json($$"""{ "cpu": {{++n * 10}} }""");
        var (api, host) = Create();
        var request = Request("""[ { "path": "cpu", "label": "CPU", "as": "chart" } ]""");

        var first = await api.LoadCardAsync(request, host, null, TestToken);
        var second = await api.LoadCardAsync(request, host, first, TestToken);

        var series = Assert.Single(Assert.Single(second.Items.OfType<CardChart>()).Chart.Series);
        Assert.Equal("CPU", series.Name);
        Assert.Equal([10.0, 20.0], series.Points.Select(p => p.Y));
        Assert.Equal("20", Value(second, "CPU").Value);
        Assert.Equal(CardStatus.Ok, second.Status);
    }

    [Fact]
    public async Task Non_json_and_errors_show_status_and_excerpt()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>" + new string('x', 5000) + "</html>") };
        var (api, host) = Create();

        var card = await api.LoadCardAsync(Request("""[ { "path": "cpu" } ]"""), host, null, TestToken);

        Assert.Equal(CardStatus.Error, card.Status);
        Assert.Equal("502", Value(card, "HTTP").Value);
        Assert.True(Assert.Single(card.Items.OfType<CardText>()).Text.Length <= 201);
    }

    [Fact]
    public async Task Html_answer_or_missing_status_field_is_not_green()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Zaloguj się</html>") };
        var (api, host) = Create();
        var login = await api.LoadCardAsync(Request("""[ { "path": "status", "as": "status" } ]"""), host, null, TestToken);

        _respond = _ => Json("""{ "other": 1 }""");
        var missing = await api.LoadCardAsync(Request("""[ { "path": "status", "as": "status" } ]"""), host, null, TestToken);

        Assert.Equal(CardStatus.Warning, login.Status);
        Assert.Equal(CardStatus.Warning, missing.Status);
        Assert.Equal(CardStatus.Warning, Value(missing, "status").Status);
    }

    [Fact]
    public async Task Secrets_are_refused_in_the_url_and_bound_to_the_origin()
    {
        var store = new SecretStore(_secrets);
        store.Set("token", "abc", "https://api.example.com:443");
        var (api, host) = Create(store);
        var headers = new Dictionary<string, string> { ["Authorization"] = "{secret:token}" };

        var inUrl = await api.LoadCardAsync(new ApiRequest("https://api.example.com/x?key={secret:token}", "GET", new Dictionary<string, string>(), null, null, []), host, null, TestToken);
        var downgraded = await api.LoadCardAsync(new ApiRequest("http://api.example.com/x", "GET", headers, null, null, []), host, null, TestToken);
        var otherPort = await api.LoadCardAsync(new ApiRequest("https://api.example.com:8443/x", "GET", headers, null, null, []), host, null, TestToken);
        await api.LoadCardAsync(new ApiRequest("https://api.example.com/x", "GET", headers, null, null, []), host, null, TestToken);

        Assert.Equal(CardStatus.Error, inUrl.Status);
        Assert.Equal(CardStatus.Error, downgraded.Status);
        Assert.Equal(CardStatus.Error, otherPort.Status);
        Assert.Equal("abc", Assert.Single(_requests).Headers.Authorization!.ToString());
        Assert.Equal("https://api.example.com:443", ApiCards.Origin(new Uri("https://API.example.com/x")));
    }

    [Fact]
    public async Task Missing_path_shows_dash()
    {
        _respond = _ => Json("""{ "a": { "b": 1 } }""");
        var (api, host) = Create();

        var card = await api.LoadCardAsync(Request("""[ { "path": "a.c", "label": "C" }, { "path": "items[3].x", "label": "X" } ]"""), host, null, TestToken);

        Assert.Equal("—", Value(card, "C").Value);
        Assert.Equal("—", Value(card, "X").Value);
        Assert.Equal(CardStatus.Ok, card.Status);
    }

    [Fact]
    public async Task Secrets_expand_into_headers_and_are_refused_for_other_hosts()
    {
        var store = new SecretStore(_secrets);
        store.Set("token", "abc", "https://api.example.com:443");
        var (api, host) = Create(store);
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer {secret:token}" };

        await api.LoadCardAsync(new ApiRequest("https://api.example.com/x", "GET", headers, null, null, []), host, null, TestToken);
        var refused = await api.LoadCardAsync(new ApiRequest("https://evil.example.net/x", "GET", headers, null, null, []), host, null, TestToken);
        var missing = await api.LoadCardAsync(new ApiRequest("https://api.example.com/x", "GET", new Dictionary<string, string> { ["X-Key"] = "{secret:other}" }, null, null, []), host, null, TestToken);

        Assert.Equal("Bearer abc", Assert.Single(_requests).Headers.Authorization!.ToString());
        Assert.Equal(CardStatus.Error, refused.Status);
        Assert.Contains("api.example.com", refused.Subtitle, StringComparison.Ordinal);
        Assert.Equal(CardStatus.Error, missing.Status);
        Assert.Equal("c4k:secret/other", Assert.Single(missing.Actions).Target);
    }

    [Fact]
    public void Flatten_lists_leaf_paths_up_to_500()
    {
        using var document = JsonDocument.Parse("""{ "a": { "b": 1, "c": [ { "d": "x" }, { "d": "y" } ] }, "e": null, "big": [ ] }""");

        var fields = JsonFieldPath.Flatten(document.RootElement);

        Assert.Equal(["a.b", "a.c[0].d", "e", "big"], fields.Select(f => f.Path));
        Assert.Equal("x", fields[1].Preview);

        var many = JsonDocument.Parse("{" + string.Join(",", Enumerable.Range(0, 800).Select(i => $"\"k{i}\": {i}")) + "}");
        Assert.Equal(500, JsonFieldPath.Flatten(many.RootElement).Count);
    }

    [Theory]
    [InlineData("a.b", "1")]
    [InlineData("items[1].name", "\"second\"")]
    [InlineData("$", null)]
    [InlineData("items[9].name", "")]
    [InlineData("a.b.c", "")]
    public void Path_syntax(string path, string? expected)
    {
        using var document = JsonDocument.Parse("""{ "a": { "b": 1 }, "items": [ { "name": "first" }, { "name": "second" } ] }""");

        var found = JsonFieldPath.TryGet(document.RootElement, path, out var value);

        if (expected is null)
        {
            Assert.True(found);
            Assert.Equal(JsonValueKind.Object, value.ValueKind);
        }
        else if (expected.Length == 0)
        {
            Assert.False(found);
        }
        else
        {
            Assert.True(found);
            Assert.Equal(expected, value.GetRawText());
        }
    }

    private (ApiCards Api, IPluginHost Host) Create(SecretStore? store = null)
    {
        var api = new ApiCards(store ?? new SecretStore(_secrets));
        var http = new HttpClient(new StubHandler(request =>
        {
            _requests.Add(request);
            return _respond(request);
        }));
        return (api, new ScopedPluginHost(api.Manifest, new NullPluginUi(), _router, http, _audit));
    }

    private static ApiRequest Request(string fields)
    {
        var config = FolderConfig.Parse(@"C:\x", $$"""{ "preview": { "view": "api", "api": { "url": "https://api.example.com/status", "fields": {{fields}} } } }""");
        return config.Preview!.Api ?? throw new InvalidOperationException(config.Error);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static CardValue Value(PreviewCard card, string label) => card.Items.OfType<CardValue>().Single(v => v.Label == label);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
