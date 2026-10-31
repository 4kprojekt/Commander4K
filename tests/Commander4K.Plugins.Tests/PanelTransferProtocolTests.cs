using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Commander4K.Core.Execution;
using Commander4K.Core.Plugins;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;

namespace Commander4K.Plugins.Tests;

/// <summary>
/// <c>panel.receivers/receive/providers/provide</c>, <c>job.progress</c> and <c>net.request</c> with a file body, run
/// against a throw-away Python plugin that has only the <c>net</c> permission (reads work only through the grant).
/// </summary>
public sealed class PanelTransferProtocolTests : IAsyncLifetime
{
    private const string MainPy = """
        import time
        import c4k

        @c4k.handler("panel.receivers")
        def receivers(params):
            if params["panel"] == "slow":
                time.sleep(30)
            return [
                {"id": "r1", "title": "One", "description": "first", "extensions": [".txt", "*.log"],
                 "multiple": True, "folders": True, "returnsMessage": True},
                {"id": "r2", "title": "Two"},
            ]

        @c4k.handler("panel.receive")
        def receive(params):
            receiver = params["receiver"]
            files = params["files"]
            if receiver == "hang":
                time.sleep(30)
                return {}
            if receiver == "progress":
                c4k.report_progress(params["jobId"], 1, 2)
                c4k.report_progress(params["jobId"], 2, 2, bytes=50, text="half")
                return {"text": "done"}
            if receiver == "malformed":
                return {"results": [{"ok": True, "error": None}, {"path": "x", "ok": True}]}
            if receiver == "slow-upload":
                c4k.http_request(c4k.prompt("url", "url"), method="POST", body_file=files[0]["path"])
                return {"text": "uploaded"}
            if receiver == "upload":
                url = c4k.prompt("url", "url")
                plain = c4k.http_request(url, method="POST", body_file=files[0]["path"], content_type="application/x-test")
                form = c4k.http_request(url, method="POST", multipart={"field": "doc", "path": files[0]["path"], "fileName": "renamed.txt"})
                return {"text": f"{plain['status']} {form['status']}"}
            data = c4k.read_bytes(files[0]["path"], 0, 1000)
            other = files[0]["path"][:-5] + "b.txt"
            try:
                c4k.read_bytes(other)
                results = [{"path": other, "ok": True}]
            except c4k.HostError as error:
                results = [{"path": other, "ok": False, "error": str(error)}]
            return {"text": f"got {len(data)}", "results": results}

        @c4k.handler("panel.providers")
        def providers(params):
            return [{"id": "save", "title": "Save", "description": "as files"}, {"id": "bare", "title": "Bare"}]

        @c4k.handler("panel.provide")
        def provide(params):
            if params["provider"] == "bad":
                return {"files": [{"name": "x.txt", "url": "file:///c:/x"}, {"base64": "AA=="}, {"name": "empty.txt"},
                                  {"name": "broken.bin", "base64": "@@@"}, {"name": "ok.txt", "text": "y"}]}
            if params["provider"] == "big":
                # 9 M characters on the wire (under the 16 M message limit), 18 MB as UTF-8: over the inline limit.
                return {"files": [{"name": "big.txt", "text": "ż" * 9000000}, {"name": "ok.txt", "text": "fine"}]}
            return {"text": "3 files", "files": [
                {"name": "a.txt", "text": "hello"},
                {"name": "b.bin", "base64": "AAEC"},
                {"name": "c.zip", "url": "https://example.com/c.zip"},
            ]}

        c4k.serve()
        """;

    private const string Manifest = """
        { "id": "tests.transfer", "name": "Transfer", "version": "1.0.0", "author": "t", "description": "d",
          "runtime": { "type": "python", "entry": "main.py", "version": ">=3.9" },
          "permissions": ["net"],
          "contributes": { "panels": [
            { "id": "main", "title": "Main", "accepts": true, "provides": true },
            { "id": "slow", "title": "Slow", "accepts": true } ] } }
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("c4k-transfer-").FullName;
    private readonly AuditLog _audit = new(null);
    private readonly FakeUi _ui = new();
    private readonly VfsRouter _router = new(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
    private readonly HttpClient _http = new();
    private IReadOnlyList<RuntimeInfo> _runtimes = [];

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private string A => Path.Combine(_folder, "a.txt");

    public async ValueTask InitializeAsync()
    {
        _runtimes = await RuntimeCatalog.DetectAsync(TestToken);
        File.WriteAllBytes(A, new byte[100]);
        File.WriteAllBytes(Path.Combine(_folder, "b.txt"), new byte[50]);
        _ui.ActivePath = _folder;
    }

    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync();
        _http.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Receivers_are_parsed_and_receive_reads_granted_file()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;
        var session = Session(plugin, host, "main");
        Assert.True(session.CanReceive);
        Assert.True(session.CanProvide);

        var receivers = await session.GetReceiversAsync([Item(A)], TestToken);

        Assert.Equal(2, receivers.Count);
        Assert.Equal(new PanelReceiver("r1", "One", "first", null, true, true, true), receivers[0] with { Extensions = null });
        Assert.Equal([".txt", "*.log"], receivers[0].Extensions!);
        Assert.Equal(new PanelReceiver("r2", "Two"), receivers[1]);

        var result = await session.ReceiveAsync("r1", [Item(A)], null, TestToken);

        Assert.Equal("got 100", result.Text);
        var other = Assert.Single(result.Results!);
        Assert.False(other.Ok);
        Assert.EndsWith("b.txt", other.Path, StringComparison.Ordinal);
        Assert.NotNull(other.Error);
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.OpenReadAsync(A, TestToken));
    }

    [Fact]
    public async Task Progress_notifications_reach_the_job()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;
        var progress = new Collector();

        var result = await Session(plugin, host, "main").ReceiveAsync("progress", [Item(A)], progress, TestToken);

        Assert.Equal("done", result.Text);
        Assert.Equal([new TransferProgress(1, 2), new TransferProgress(2, 2, 50, "half")], progress.Items);
    }

    [Fact]
    public async Task Receive_is_cancelled_not_timed_out()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;
        var session = Session(plugin, host, "main");
        await session.GetReceiversAsync([Item(A)], TestToken); // started, so the 200 ms are spent waiting for the answer
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        cancel.CancelAfter(200);

        var error = await Record.ExceptionAsync(() => session.ReceiveAsync("hang", [Item(A)], new Collector(), cancel.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        await Assert.ThrowsAsync<PluginPermissionException>(() => host.OpenReadAsync(A, TestToken));
    }

    [Fact]
    public async Task Receivers_call_times_out_after_five_seconds()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;
        var session = Session(plugin, host, "slow");
        Assert.False(session.CanProvide);
        await session.GetProvidersAsync([], TestToken); // start the process outside the measured time
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => session.GetReceiversAsync([Item(A)], TestToken));

        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Provide_parses_text_base64_and_url()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;
        var session = Session(plugin, host, "main");

        var providers = await session.GetProvidersAsync(["row1"], TestToken);
        Assert.Equal([new PanelProvider("save", "Save", "as files"), new PanelProvider("bare", "Bare")], providers);

        var result = await session.ProvideAsync("save", ["row1"], null, TestToken);

        Assert.Equal("3 files", result.Text);
        Assert.Equal(3, result.Files.Count);
        Assert.Equal(("a.txt", "hello"), (result.Files[0].Name, result.Files[0].Text));
        Assert.Equal(new byte[] { 0, 1, 2 }, result.Files[1].Data);
        Assert.Equal(new Uri("https://example.com/c.zip"), result.Files[2].Url);
        Assert.All(result.Files, f => Assert.Null(f.Error));

        // Problems with one file are reported on that file; the rest of the reply still comes through.
        var bad = await session.ProvideAsync("bad", ["row1"], null, TestToken);
        Assert.Equal(["x.txt", "2", "empty.txt", "broken.bin", "ok.txt"], bad.Files.Select(f => f.Name));
        Assert.All(bad.Files.Take(4), f => Assert.NotNull(f.Error));
        Assert.All(bad.Files.Take(4), f => Assert.True(f.Text is null && f.Data is null && f.Url is null));
        Assert.Equal(new ProvidedFile("ok.txt", Text: "y"), bad.Files[4]);
    }

    [Fact]
    public async Task Provide_reports_an_over_limit_file_and_keeps_the_others()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;

        var result = await Session(plugin, host, "main").ProvideAsync("big", ["row1"], null, TestToken);

        Assert.Equal(2, result.Files.Count);
        Assert.Equal("big.txt", result.Files[0].Name);
        Assert.Null(result.Files[0].Text);
        Assert.Contains("16 MB", result.Files[0].Error, StringComparison.Ordinal);
        Assert.Equal(new ProvidedFile("ok.txt", Text: "fine"), result.Files[1]);
    }

    [Fact]
    public async Task Receive_result_without_path_becomes_a_failed_entry()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;

        var result = await Session(plugin, host, "main").ReceiveAsync("malformed", [Item(A)], null, TestToken);

        Assert.Equal(2, result.Results!.Count);
        Assert.False(result.Results[0].Ok);
        Assert.Equal(string.Empty, result.Results[0].Path);
        Assert.NotNull(result.Results[0].Error);
        Assert.Equal(new TransferItemResult("x", true), result.Results[1]);
    }

    [Fact]
    public async Task Bad_content_type_leaves_no_file_open()
    {
        var host = new ScopedPluginHost(new PluginManifest("tests.ct", "CT", "1.0.0", "a", "d", [PluginPermissions.Network]), _ui, _router, _http, _audit);
        using (host.GrantRead([Item(A)]))
        {
            await Assert.ThrowsAsync<FormatException>(() =>
                host.HttpRequestAsync(new HttpRequestSpec("POST", new Uri("http://127.0.0.1:9/"), ContentType: "not a type", BodyFile: A), TestToken));
            await Assert.ThrowsAsync<FormatException>(() =>
                host.HttpRequestAsync(new HttpRequestSpec("POST", new Uri("http://127.0.0.1:9/"), ContentType: "not a type", Part: new HttpFilePart("f", A)), TestToken));
        }

        using var exclusive = new FileStream(A, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Cancelling_receive_stops_a_streaming_upload()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;
        using var server = new RecordingServer(respond: false);
        _ui.PromptAnswer = server.Url;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

        var receive = Session(plugin, host, "main").ReceiveAsync("slow-upload", [Item(A)], null, cancel.Token);
        await WaitUntil(() => server.Requests.Count == 1, TimeSpan.FromSeconds(20));
        var clock = Stopwatch.StartNew();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        await WaitUntil(() => _audit.Recent("tests.transfer").Any(e => e.Operation == "net.request" && e.Outcome == AuditOutcome.Failed), TimeSpan.FromSeconds(10));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"The upload ran on for {clock.Elapsed}.");
        using var exclusive = new FileStream(A, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < limit, "Condition not met in time.");
            await Task.Delay(20, TestToken);
        }
    }

    [Fact]
    public async Task Http_body_file_streams_granted_file()
    {
        var (plugin, host) = Start();
        await using var owned = plugin;
        using var server = new RecordingServer();
        _ui.PromptAnswer = server.Url;

        var result = await Session(plugin, host, "main").ReceiveAsync("upload", [Item(A)], null, TestToken);

        Assert.Equal("200 200", result.Text);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(100, server.Requests[0].Body.Length);
        Assert.Equal("application/x-test", server.Requests[0].ContentType);
        Assert.StartsWith("multipart/form-data", server.Requests[1].ContentType, StringComparison.Ordinal);
        var form = System.Text.Encoding.Latin1.GetString(server.Requests[1].Body);
        Assert.Contains("name=doc", form, StringComparison.Ordinal);
        Assert.Contains("filename=renamed.txt", form, StringComparison.Ordinal);

        // Without the grant and without fs.read, the file is not read.
        await Assert.ThrowsAsync<PluginPermissionException>(() =>
            host.HttpRequestAsync(new HttpRequestSpec("POST", new Uri(server.Url), BodyFile: A), TestToken));
        await Assert.ThrowsAsync<PluginPermissionException>(() =>
            host.HttpRequestAsync(new HttpRequestSpec("POST", new Uri(server.Url), Part: new HttpFilePart("f", A)), TestToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.HttpRequestAsync(new HttpRequestSpec("POST", new Uri(server.Url), Body: "x", BodyFile: A), TestToken));
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains(_audit.Recent("tests.transfer"), e => e.Operation == "fs.read" && e.Outcome == AuditOutcome.Denied);
    }

    private const string MainJs = """
        const c4k = require('./c4k');
        c4k.on('panel.receive', async p => {
          c4k.reportProgress(p.jobId, 1, 1, { bytes: 100, text: 'up' });
          const url = await c4k.prompt('url', 'url');
          const plain = await c4k.httpRequest(url, { method: 'POST', bodyFile: p.files[0].path });
          const form = await c4k.httpRequest(url, { method: 'POST', multipart: { field: 'doc', path: p.files[0].path, fileName: 'renamed.txt' } });
          return { text: `${plain.status} ${form.status}` };
        });
        """;

    private const string MainPs1 = """
        . "$PSScriptRoot/c4k.ps1"
        Register-C4KHandler 'panel.receive' {
            param($p)
            Send-C4KProgress -JobId $p.jobId -Done 1 -Total 1 -Bytes 100 -Text 'up'
            $url = Read-C4KInput 'url' 'url'
            $plain = Invoke-C4KHttpRequest $url -Method POST -BodyFile $p.files[0].path
            $form = Invoke-C4KHttpRequest $url -Method POST -MultipartField doc -MultipartPath $p.files[0].path -MultipartFileName renamed.txt
            @{ text = "$($plain.status) $($form.status)" }
        }
        Start-C4KPlugin
        """;

    [Theory]
    [InlineData("node", "c4k.js", "main.js")]
    [InlineData("powershell", "c4k.ps1", "main.ps1")]
    public async Task Node_and_powershell_clients_report_progress_and_upload_files(string runtime, string client, string entry)
    {
        var (plugin, host) = Start(runtime, client, entry, runtime == "node" ? MainJs : MainPs1);
        await using var owned = plugin;
        using var server = new RecordingServer();
        _ui.PromptAnswer = server.Url;
        var progress = new Collector();

        var result = await Session(plugin, host, "main").ReceiveAsync("upload", [Item(A)], progress, TestToken);

        Assert.Equal("200 200", result.Text);
        Assert.Equal([new TransferProgress(1, 1, 100, "up")], progress.Items);
        Assert.Equal(100, server.Requests[0].Body.Length);
        Assert.Equal("application/octet-stream", server.Requests[0].ContentType);
        Assert.Contains("filename=renamed.txt", System.Text.Encoding.Latin1.GetString(server.Requests[1].Body), StringComparison.Ordinal);
    }

    private static TransferItem Item(string path) => new(path, Path.GetFileName(path), TransferItemKind.File, new FileInfo(path).Length);

    private static IPanelSession Session(ExternalPlugin plugin, ScopedPluginHost host, string panelId) =>
        new ExternalPanelPlugin(plugin, plugin.Manifest.Contributes!.Panels!.Single(p => p.Id == panelId)).CreateSession(new PanelRequest(null, null), host);

    /// <summary>The app keeps one host per plugin: the session and the plugin's own requests share it, so grants apply.</summary>
    private (ExternalPlugin Plugin, ScopedPluginHost Host) Start(string runtimeType = "python", string client = "c4k.py", string entry = "main.py", string code = MainPy)
    {
        var folder = Path.Combine(_folder, "plugin");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(RepositoryRoot(), "plugins-sdk", runtimeType, client), Path.Combine(folder, client));
        File.WriteAllText(Path.Combine(folder, ManifestReader.FileName), Manifest
            .Replace("\"type\": \"python\", \"entry\": \"main.py\", \"version\": \">=3.9\"", $"\"type\": \"{runtimeType}\", \"entry\": \"{entry}\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, entry), code);
        var discovered = ManifestReader.Read(folder);
        Assert.True(discovered.IsValid, string.Join("; ", discovered.Errors));
        var missing = RuntimeCatalog.CheckRequirement(discovered.Manifest!.Runtime, _runtimes);
        Assert.SkipWhen(missing is not null, missing ?? string.Empty);
        var runtime = _runtimes.First(r => r.Type == discovered.Manifest.Runtime.Type);
        var host = new ScopedPluginHost(discovered.Manifest, _ui, _router, _http, _audit);
        return (new ExternalPlugin(discovered, runtime, host, _audit), host);
    }

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Commander4K.sln")))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return directory;
    }

    private sealed class Collector : IProgress<TransferProgress>
    {
        private readonly Lock _gate = new();
        private readonly List<TransferProgress> _items = [];

        public IReadOnlyList<TransferProgress> Items
        {
            get
            {
                lock (_gate)
                {
                    return [.. _items];
                }
            }
        }

        public void Report(TransferProgress value)
        {
            lock (_gate)
            {
                _items.Add(value);
            }
        }
    }

    private sealed class FakeUi : IPluginUi
    {
        public string? ActivePath { get; set; }

        public string? OtherPath => null;

        public IReadOnlyList<VfsEntry> SelectedEntries => [];

        public string? PromptAnswer { get; set; }

        public Task NavigateAsync(string path, string? focusName) => Task.CompletedTask;

        public void Notify(string message, bool isError)
        {
        }

        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult(PromptAnswer);

        public Task EditFileAsync(string path) => Task.CompletedTask;

        public Task OpenPanelAsync(string panelPluginId, string? argument) => Task.CompletedTask;

        public Task OpenUrlAsync(string url) => Task.CompletedTask;

        public Task SearchWebAsync(string query, string? engine) => Task.CompletedTask;

        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }

    private sealed record ReceivedRequest(string? ContentType, byte[] Body);

    /// <summary>Loopback HTTP server that records every request body and answers 200.</summary>
    private sealed class RecordingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<ReceivedRequest> _requests = [];

        /// <param name="respond">False: record the request on arrival, never read its body and never answer.</param>
        public RecordingServer(bool respond = true)
        {
            using (var socket = new TcpListener(IPAddress.Loopback, 0))
            {
                socket.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/";
            }

            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        if (!respond)
                        {
                            lock (_gate)
                            {
                                _requests.Add(new ReceivedRequest(context.Request.ContentType, []));
                            }

                            continue;
                        }

                        using var body = new MemoryStream();
                        await context.Request.InputStream.CopyToAsync(body);
                        lock (_gate)
                        {
                            _requests.Add(new ReceivedRequest(context.Request.ContentType, body.ToArray()));
                        }

                        context.Response.StatusCode = 200;
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
        }

        public string Url { get; }

        public IReadOnlyList<ReceivedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        public void Dispose() => _listener.Close();
    }
}
