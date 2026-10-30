using System.Text;
using Commander4K.Core.Execution;
using Commander4K.Core.Jobs;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;
using Commander4K.Plugins.BuiltIn.Local;
using Commander4K.Plugins.BuiltIn.Search;
using Commander4K.Plugins.BuiltIn.Viewers;

namespace Commander4K.Plugins.Tests;

public sealed class TransferAndSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("c4k-transfer-").FullName;
    private readonly VfsRouter _router;

    public TransferAndSearchTests()
    {
        _router = new VfsRouter(new ResilientAsyncExecutor(), [new LocalFileSystemPlugin()]);
        Directory.CreateDirectory(Path.Combine(_root, "src", "nested"));
        Directory.CreateDirectory(Path.Combine(_root, "dst"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "alpha");
        File.WriteAllBytes(Path.Combine(_root, "src", "nested", "big.bin"), new byte[3 * 1024 * 1024 + 7]);
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Copy_job_copies_tree_and_reports_bytes()
    {
        var source = await SourceAsync();
        var job = new VfsTransferJob(_router, [source], Path.Combine(_root, "dst"), TransferMode.Copy, OverwritePolicy.Skip);
        var last = default(JobMetrics);

        await job.ExecuteAsync(new Progress<JobMetrics>(m => last = m), TestToken);

        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(_root, "dst", "src", "a.txt"), TestToken));
        Assert.Equal(3 * 1024 * 1024 + 7, new FileInfo(Path.Combine(_root, "dst", "src", "nested", "big.bin")).Length);
        Assert.True(Directory.Exists(Path.Combine(_root, "src")));
    }

    [Fact]
    public async Task Copy_skips_existing_files_unless_overwriting()
    {
        var source = await SourceAsync();
        Directory.CreateDirectory(Path.Combine(_root, "dst", "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "dst", "src", "a.txt"), "old", TestToken);

        var skip = new VfsTransferJob(_router, [source], Path.Combine(_root, "dst"), TransferMode.Copy, OverwritePolicy.Skip);
        await skip.ExecuteAsync(new Progress<JobMetrics>(), TestToken);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_root, "dst", "src", "a.txt"), TestToken));
        Assert.Equal(1, skip.SkippedFiles);

        var overwrite = new VfsTransferJob(_router, [source], Path.Combine(_root, "dst"), TransferMode.Copy, OverwritePolicy.Overwrite);
        await overwrite.ExecuteAsync(new Progress<JobMetrics>(), TestToken);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(_root, "dst", "src", "a.txt"), TestToken));
    }

    [Fact]
    public async Task Move_job_renames_on_same_volume()
    {
        var source = await SourceAsync();
        var job = new VfsTransferJob(_router, [source], Path.Combine(_root, "dst"), TransferMode.Move, OverwritePolicy.Skip);

        await job.ExecuteAsync(new Progress<JobMetrics>(), TestToken);

        Assert.False(Directory.Exists(Path.Combine(_root, "src")));
        Assert.True(File.Exists(Path.Combine(_root, "dst", "src", "nested", "big.bin")));
    }

    [Fact]
    public async Task Copy_into_itself_is_refused()
    {
        var source = await SourceAsync();
        var job = new VfsTransferJob(_router, [source], Path.Combine(_root, "src", "nested"), TransferMode.Copy, OverwritePolicy.Skip);

        await Assert.ThrowsAsync<IOException>(() => job.ExecuteAsync(new Progress<JobMetrics>(), TestToken));
    }

    [Fact]
    public async Task Delete_job_removes_trees()
    {
        var source = await SourceAsync();

        await new VfsDeleteJob(_router, [source]).ExecuteAsync(new Progress<JobMetrics>(), TestToken);

        Assert.False(Directory.Exists(Path.Combine(_root, "src")));
    }

    [Fact]
    public async Task Search_matches_names_and_content_across_chunk_boundaries()
    {
        var padding = new string('x', 256 * 1024 - 3);
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "nested", "log.txt"), padding + "NeedLE here", TestToken);
        var plugin = new LocalFileSearchPlugin();

        var byName = await CollectAsync(plugin, new SearchQuery(_root, "*.bin"));
        var byContent = await CollectAsync(plugin, new SearchQuery(_root, "*", ContainsText: "needle"));
        var caseSensitive = await CollectAsync(plugin, new SearchQuery(_root, "*", ContainsText: "needle", CaseSensitive: true));

        Assert.Equal(["big.bin"], byName.Select(h => h.Entry.Name));
        var hit = Assert.Single(byContent);
        Assert.Equal(padding.Length, hit.Offset);
        Assert.Empty(caseSensitive);
    }

    [Theory]
    [InlineData("needle", "hay needle", true, 4)]
    [InlineData("NEEDLE", "hay needle", false, 4)]
    [InlineData("NEEDLE", "hay needle", true, -1)]
    [InlineData("zażółć", "jest zażółć tu", true, 5)]
    public void Content_matcher_finds_offsets(string needle, string haystack, bool caseSensitive, int expectedChars)
    {
        var matcher = LocalFileSearchPlugin.ContentMatcher.Create(needle, caseSensitive);
        var bytes = Encoding.UTF8.GetBytes(haystack);
        var expected = expectedChars < 0 ? -1 : Encoding.UTF8.GetByteCount(haystack[..expectedChars]);

        Assert.Equal(expected, matcher.IndexOf(bytes));
    }

    [Fact]
    public async Task Viewer_detects_binary_and_text()
    {
        var viewer = new TextHexViewerPlugin();
        var entry = new VfsEntry("x", "x", VfsEntryKind.File, 4);

        var text = await viewer.LoadAsync(entry, new MemoryStream("zażółć"u8.ToArray()), TestToken);
        var binary = await viewer.LoadAsync(entry, new MemoryStream([1, 0, 2, 3]), TestToken);

        Assert.Equal("zażółć", Assert.IsType<TextViewerContent>(text).Text);
        Assert.IsType<HexViewerContent>(binary);
        Assert.StartsWith("00000000  01 00 02 03", TextHexViewerPlugin.FormatHex([1, 0, 2, 3]), StringComparison.Ordinal);
    }

    private async Task<VfsEntry> SourceAsync() => (await _router.GetEntryAsync(Path.Combine(_root, "src"), TestToken))!.Value;

    private static async Task<List<SearchHit>> CollectAsync(LocalFileSearchPlugin plugin, SearchQuery query)
    {
        var hits = new List<SearchHit>();
        await foreach (var hit in plugin.SearchAsync(query, TestToken))
        {
            hits.Add(hit);
        }

        return hits;
    }
}
