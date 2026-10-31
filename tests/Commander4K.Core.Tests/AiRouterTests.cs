using System.Net;
using System.Text;
using Commander4K.Core.Ai;

namespace Commander4K.Core.Tests;

public sealed class AiRouterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "c4k-ai-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Built_in_catalog_lists_openai_compatible_providers()
    {
        var catalog = AiCatalogService.LoadBuiltIn();

        Assert.Contains(catalog.Providers, p => p.Id == "openrouter" && p.Auth == AiAuthKinds.OpenRouterOAuth);
        Assert.Contains(catalog.Providers, p => p.Id == "groq");
        Assert.All(catalog.Providers, p => Assert.StartsWith("http", p.BaseUrl, StringComparison.Ordinal));
        Assert.Equal(catalog.Providers.Count, catalog.Providers.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Local_providers_come_with_install_help()
    {
        var local = AiCatalogService.LoadBuiltIn().Providers.Where(p => p.Tier == "local").ToList();

        Assert.Equal(["ollama", "lmstudio"], local.Select(p => p.Id));
        Assert.All(local, p =>
        {
            Assert.Equal(AiAuthKinds.None, p.Auth);
            Assert.StartsWith("http://localhost:", p.BaseUrl, StringComparison.Ordinal);
            Assert.NotNull(p.Install!.SafeWingetId);
            Assert.True(AiInstallInfo.IsWebUrl(p.Install.DownloadUrl));
            Assert.Contains("1.", p.Install.Steps["pl"], StringComparison.Ordinal);
            Assert.Contains("1.", p.Install.Steps["en"], StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("Ollama.Ollama", true)]
    [InlineData("ElementLabs.LMStudio", true)]
    [InlineData("Ollama.Ollama; Remove-Item C:\\", false)]
    [InlineData("x && calc", false)]
    [InlineData("$(evil)", false)]
    public void Only_plain_winget_ids_are_run(string id, bool accepted) =>
        Assert.Equal(accepted, new AiInstallInfo { Winget = id }.SafeWingetId is not null);

    [Theory]
    [InlineData("https://ollama.com/download", true)]
    [InlineData("http://localhost:1234", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData("ms-settings:", false)]
    public void Only_web_links_are_opened(string url, bool accepted) =>
        Assert.Equal(accepted, AiInstallInfo.IsWebUrl(url));

    [Fact]
    public void Keys_are_encrypted_on_disk_and_read_back()
    {
        var store = new AiCredentialStore(Path.Combine(_directory, "credentials.json"));
        store.SaveKey("groq", "secret-key-123", "pasted");

        Assert.DoesNotContain("secret-key-123", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
        Assert.Equal("secret-key-123", new AiCredentialStore(store.FilePath).GetKey("groq"));
    }

    [Fact]
    public async Task Rate_limited_provider_rotates_to_the_next_and_waits()
    {
        var (router, handler) = Create(("a", "key-a"), ("b", "key-b"));
        handler.Respond("a.test", HttpStatusCode.TooManyRequests, """{"error":{"message":"Rate limit reached"}}""", retryAfterSeconds: 120);
        handler.Respond("b.test", HttpStatusCode.OK, Answer("hello from b"));

        var answer = await router.CompleteAsync([AiMessage.User("hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hello from b", answer.Text);
        Assert.Equal("b", answer.Provider.Id);
        Assert.Contains(answer.Attempts, a => a.ProviderId == "a" && a.Failure == AiFailureKind.RateLimited);
        // Both models of "a" were rate limited, so the provider as a whole waits.
        var state = router.States().Single(s => s.Provider.Id == "a");
        Assert.Equal(AiProviderStatus.Waiting, state.Status);
        Assert.True(state.Until > DateTimeOffset.UtcNow.AddSeconds(100));
    }

    [Fact]
    public async Task Refused_key_marks_provider_invalid_and_quota_waits_an_hour()
    {
        var (router, handler) = Create(("a", "key-a"), ("b", "key-b"), ("c", "key-c"));
        handler.Respond("a.test", HttpStatusCode.Unauthorized, """{"error":"invalid api key"}""");
        handler.Respond("b.test", HttpStatusCode.TooManyRequests, """{"error":{"message":"You exceeded your daily quota"}}""");
        handler.Respond("c.test", HttpStatusCode.OK, Answer("ok"));

        var answer = await router.CompleteAsync([AiMessage.User("hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("c", answer.Provider.Id);
        Assert.Equal(AiProviderStatus.InvalidKey, router.States().Single(s => s.Provider.Id == "a").Status);
        var quota = router.States().Single(s => s.Provider.Id == "b");
        Assert.Equal(AiProviderStatus.Waiting, quota.Status);
        Assert.True(quota.Until > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.Single(handler.Requests, r => r.StartsWith("b.test", StringComparison.Ordinal)); // Quota: no second model tried.
    }

    [Fact]
    public async Task Missing_model_falls_back_to_the_next_model_of_the_same_provider()
    {
        var (router, handler) = Create(("a", "key-a"));
        handler.Respond("a.test", request => request.Contains("\"model-1\"", StringComparison.Ordinal)
            ? (HttpStatusCode.NotFound, """{"error":{"message":"model not found"}}""")
            : (HttpStatusCode.OK, Answer("second model")));

        var answer = await router.CompleteAsync([AiMessage.User("hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("model-2", answer.Model);
        Assert.Equal(AiProviderStatus.Ready, router.States().Single(s => s.Provider.Id == "a").Status);
    }

    [Fact]
    public async Task Nothing_signed_in_reports_it()
    {
        var (router, _) = Create();

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() => router.CompleteAsync([AiMessage.User("hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(error.Attempts);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("1.5", 1.5)]
    [InlineData("2m59.5s", 179.5)]
    [InlineData("7.66s", 7.66)]
    [InlineData("1h", 3600)]
    public void Reset_headers_are_parsed(string value, double seconds) =>
        Assert.Equal(seconds, OpenAiCompatibleClient.ParseDuration(value)!.Value.TotalSeconds, 2);

    private (AiRouter Router, FakeHandler Handler) Create(params (string Id, string Key)[] signedIn)
    {
        Directory.CreateDirectory(_directory);
        var all = new[] { "a", "b", "c" };
        var json = "{\"providers\":[" + string.Join(",", all.Select(id =>
            $$"""{"id":"{{id}}","name":"{{id.ToUpperInvariant()}}","baseUrl":"https://{{id}}.test/v1","models":["model-1","model-2"]}""")) + "]}";
        var userPath = Path.Combine(_directory, "providers.json");
        File.WriteAllText(Path.Combine(_directory, "cache.json"), json);
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        var catalog = new AiCatalogService(http, Path.Combine(_directory, "cache.json"), userPath);
        catalog.Load();
        var credentials = new AiCredentialStore(Path.Combine(_directory, "credentials.json"));
        foreach (var (id, key) in signedIn)
        {
            credentials.SaveKey(id, key, "pasted");
        }

        return (new AiRouter(catalog, credentials, new OpenAiCompatibleClient(http)), handler);
    }

    private static string Answer(string text) => $$$"""{"choices":[{"message":{"role":"assistant","content":"{{{text}}}"}}]}""";

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<string, (HttpStatusCode, string)>> _responses = [];
        private readonly Dictionary<string, int> _retryAfter = [];

        public List<string> Requests { get; } = [];

        public void Respond(string host, HttpStatusCode status, string body, int? retryAfterSeconds = null)
        {
            _responses[host] = _ => (status, body);
            if (retryAfterSeconds is { } seconds)
            {
                _retryAfter[host] = seconds;
            }
        }

        public void Respond(string host, Func<string, (HttpStatusCode, string)> respond) => _responses[host] = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(host + request.RequestUri.AbsolutePath);
            var (status, text) = _responses[host](body);
            var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            if (_retryAfter.TryGetValue(host, out var seconds))
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
