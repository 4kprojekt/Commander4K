using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Jobs;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class PanelTransferJobTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly PanelProvider Provider = new("p", "Biblioteka");

    private sealed class Sink : IProgress<JobMetrics>
    {
        public List<JobMetrics> Items { get; } = [];

        public void Report(JobMetrics value) => Items.Add(value);
    }

    private sealed class Session : IPanelSession
    {
        public Func<IProgress<TransferProgress>?, ReceiveResult>? OnReceive { get; init; }

        public ProvideResult? Provided { get; init; }

        public TimeSpan? RefreshInterval => null;

        public IReadOnlyList<PanelAction> Actions => [];

        public Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ReceiveResult> ReceiveAsync(string receiverId, IReadOnlyList<TransferItem> items, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            Task.FromResult(OnReceive!(progress));

        public Task<ProvideResult> ProvideAsync(string providerId, IReadOnlyList<string> rowIds, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            Task.FromResult(Provided!);
    }

    /// <summary>Writable in-memory file system under <c>w://</c>.</summary>
    private sealed class WritableProvider : IVfsProviderPlugin
    {
        public bool FailOpenWrite { get; set; }

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase) { "w://t" };

        public string Id => "test.w";

        public string DisplayName => "W";

        public PluginManifest Manifest { get; } = new("test.w", "W", "1.0.0", "tests", "w", []);

        public IReadOnlyList<string> Schemes { get; } = ["w"];

        public VfsCapabilities Capabilities => VfsCapabilities.Read | VfsCapabilities.Write | VfsCapabilities.CreateDirectory | VfsCapabilities.Delete;

        public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            VfsEntry? entry = Files.TryGetValue(path, out var data) ? new VfsEntry(name, path, VfsEntryKind.File, data.Length)
                : Directories.Contains(path) ? new VfsEntry(name, path, VfsEntryKind.Directory, -1) : null;
            return ValueTask.FromResult(entry);
        }

        public string? GetParentPath(string path) => path[..path.LastIndexOf('/')];

        public string Combine(string directoryPath, string name) => directoryPath.TrimEnd('/') + "/" + name;

        public ValueTask<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken)
        {
            if (FailOpenWrite)
            {
                throw new IOException("locked");
            }

            if (!overwrite && Files.ContainsKey(path))
            {
                throw new IOException("exists");
            }

            Files[path] = [];
            return ValueTask.FromResult<Stream>(new Capture(this, path));
        }

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            Directories.Add(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
        {
            Files.Remove(path);
            return ValueTask.CompletedTask;
        }

        private sealed class Capture(WritableProvider owner, string path) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    owner.Files[path] = ToArray();
                }

                base.Dispose(disposing);
            }
        }
    }

    private static (PanelProvideJob Job, WritableProvider Fs) ProvideJob(
        IEnumerable<ProvidedFile> files,
        OverwritePolicy policy = OverwritePolicy.Skip,
        Func<Uri, CancellationToken, Task<Stream>>? openUrl = null)
    {
        var fs = new WritableProvider();
        var router = new VfsRouter(new ResilientAsyncExecutor(), [fs]);
        var session = new Session { Provided = new ProvideResult([.. files]) };
        return (new PanelProvideJob(session, Provider, ["r"], router, "w://t", policy, openUrl ?? ((_, _) => throw new InvalidOperationException())), fs);
    }

    [Fact]
    public async Task Receive_job_maps_progress_and_keeps_result()
    {
        var expected = new ReceiveResult("ok");
        var session = new Session { OnReceive = progress => { progress!.Report(new TransferProgress(1, 3, 42, "a.txt")); return expected; } };
        var receiver = new PanelReceiver("r", "Biblioteka");
        var job = new PanelReceiveJob(session, "Panel", receiver, [new TransferItem("C:\\a.txt", "a.txt", TransferItemKind.File, 5)]);
        var sink = new Sink();

        await job.ExecuteAsync(sink, Token);

        Assert.Contains(new JobMetrics(42, 0, 1, 3, "a.txt"), sink.Items);
        Assert.Same(expected, job.Result);
        Assert.Contains("Biblioteka", job.Title);
    }

    [Fact]
    public async Task Provide_writes_text_and_binary_into_target()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("a.txt", Text: "zażółć"), new ProvidedFile("sub/b.bin", Data: [1, 2, 3])]);

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Empty(job.Errors);
        Assert.Equal("zażółć", Encoding.UTF8.GetString(fs.Files["w://t/a.txt"]));
        Assert.Equal(new byte[] { 1, 2, 3 }, fs.Files["w://t/sub/b.bin"]);
        Assert.Contains("w://t/sub", fs.Directories);
    }

    [Fact]
    public async Task Provide_creates_a_missing_target_folder()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("a.txt", Text: "x")]);
        fs.Directories.Clear();

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Contains("w://t", fs.Directories);
        Assert.True(fs.Files.ContainsKey("w://t/a.txt"));
    }

    [Fact]
    public async Task Provide_rejects_unsafe_names()
    {
        var (job, fs) = ProvideJob([
            new ProvidedFile("..\\x.txt", Text: "x"), new ProvidedFile("C:\\x.txt", Text: "x"), new ProvidedFile("a:b.txt", Text: "x"),
            new ProvidedFile("NUL", Text: "x"), new ProvidedFile("con.txt", Text: "x"), new ProvidedFile("COM1", Text: "x"),
            new ProvidedFile("sub/LPT9.log", Text: "x"), new ProvidedFile("a.", Text: "x"), new ProvidedFile("b ", Text: "x")]);

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Equal(9, job.Errors.Count);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public async Task Provide_refuses_inline_over_16_MB()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("big.bin", Data: new byte[PanelProvideJob.MaxInlineBytes + 1]), new ProvidedFile("ok.bin", Data: new byte[PanelProvideJob.MaxInlineBytes])]);

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Single(job.Errors);
        Assert.DoesNotContain("w://t/big.bin", fs.Files.Keys);
        Assert.Contains("w://t/ok.bin", fs.Files.Keys);
    }

    [Fact]
    public async Task Provide_skips_files_with_error()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("bad.txt", Error: "too large"), new ProvidedFile("good.txt", Text: "g")]);

        await job.ExecuteAsync(new Sink(), Token);

        var error = Assert.Single(job.Errors);
        Assert.StartsWith("bad.txt", error);
        Assert.Contains("too large", error);
        Assert.Equal(["w://t/good.txt"], fs.Files.Keys);
    }

    [Theory]
    [InlineData(OverwritePolicy.Skip, "first")]
    [InlineData(OverwritePolicy.Overwrite, "second")]
    public async Task Duplicate_provided_names_follow_overwrite_policy(OverwritePolicy policy, string expected)
    {
        var (job, fs) = ProvideJob([new ProvidedFile("d.txt", Text: "first"), new ProvidedFile("d.txt", Text: "second")], policy);

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Equal(expected, Encoding.UTF8.GetString(fs.Files["w://t/d.txt"]));
        Assert.Equal(policy == OverwritePolicy.Skip ? 1 : 0, job.Errors.Count);
    }

    [Fact]
    public async Task Provide_downloads_url_through_callback()
    {
        Uri? asked = null;
        var (job, fs) = ProvideJob([new ProvidedFile("u.bin", Url: new Uri("https://example.test/u"))], openUrl: (uri, _) =>
        {
            asked = uri;
            return Task.FromResult<Stream>(new MemoryStream([9, 8, 7]));
        });

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Equal("https://example.test/u", asked?.ToString());
        Assert.Equal(new byte[] { 9, 8, 7 }, fs.Files["w://t/u.bin"]);
    }

    [Fact]
    public async Task Provide_removes_partial_file_when_download_fails()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("u.bin", Url: new Uri("https://example.test/u"))], openUrl: (_, _) =>
            throw new HttpRequestException("404"));

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Single(job.Errors);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public async Task Provide_keeps_existing_file_when_download_fails()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("e.txt", Url: new Uri("https://example.test/u"))], OverwritePolicy.Overwrite,
            (_, _) => throw new HttpRequestException("404"));
        fs.Files["w://t/e.txt"] = [1, 2];

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Single(job.Errors);
        Assert.Equal(new byte[] { 1, 2 }, fs.Files["w://t/e.txt"]);
    }

    [Fact]
    public async Task Provide_does_not_delete_file_it_could_not_open()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("e.txt", Text: "new")], OverwritePolicy.Overwrite);
        fs.Files["w://t/e.txt"] = [1, 2];
        fs.FailOpenWrite = true;

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Single(job.Errors);
        Assert.Equal(new byte[] { 1, 2 }, fs.Files["w://t/e.txt"]);
    }

    [Fact]
    public async Task Provide_turns_any_provider_exception_into_file_error()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("a.txt", Text: "x"), new ProvidedFile("b.txt", Text: "y")]);
        fs.FailOpenWrite = true;

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Equal(2, job.Errors.Count);
    }

    [Fact]
    public async Task Provide_ignores_leading_dot_segment()
    {
        var (job, fs) = ProvideJob([new ProvidedFile("./a.txt", Text: "x")]);

        await job.ExecuteAsync(new Sink(), Token);

        Assert.Empty(job.Errors);
        Assert.Contains("w://t/a.txt", fs.Files.Keys);
    }
}
