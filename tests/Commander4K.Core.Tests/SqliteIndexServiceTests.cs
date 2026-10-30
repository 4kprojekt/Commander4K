using Commander4K.Core.Indexing;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class SqliteIndexServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "c4k-tests", Guid.NewGuid().ToString("N"));
    private SqliteIndexService _index = null!;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _index = await SqliteIndexService.OpenAsync(Path.Combine(_directory, "index.db"), TestToken);

    public async ValueTask DisposeAsync()
    {
        await _index.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Frecency_ranks_frequent_paths_first()
    {
        _index.RecordVisit(@"C:\Rare");
        for (var i = 0; i < 5; i++)
        {
            _index.RecordVisit(@"C:\Projects");
        }

        _index.RecordVisit(@"\\nas\media");
        await _index.FlushAsync();

        var all = await _index.GetFrecentAsync(null, 10, TestToken);
        var filtered = await _index.GetFrecentAsync("nas", 10, TestToken);

        Assert.Equal(@"C:\Projects", all[0].Path);
        Assert.Equal(5, all[0].Visits);
        Assert.Equal([@"\\nas\media"], filtered.Select(f => f.Path));
    }

    [Fact]
    public async Task Trigram_search_finds_substrings_and_handles_removal()
    {
        _index.IndexEntries(
        [
            new VfsEntry("Quarterly_Report_2026.xlsx", @"C:\Docs\Quarterly_Report_2026.xlsx", VfsEntryKind.File, 1024),
            new VfsEntry("notes.txt", @"C:\Docs\notes.txt", VfsEntryKind.File, 12),
            new VfsEntry("report-old.pdf", @"C:\Archive\report-old.pdf", VfsEntryKind.File, 99),
        ]);
        await _index.FlushAsync();

        var hits = await _index.SearchFilesAsync("REPORT", 10, TestToken);
        Assert.Equal(2, hits.Count);

        var literalUnderscore = await _index.SearchFilesAsync("t_2", 10, TestToken);
        Assert.Equal(["Quarterly_Report_2026.xlsx"], literalUnderscore.Select(h => h.Name));

        _index.RemoveFromIndex(@"C:\Docs");
        await _index.FlushAsync();

        var remaining = await _index.SearchFilesAsync("report", 10, TestToken);
        Assert.Equal(["report-old.pdf"], remaining.Select(h => h.Name));
    }

    [Fact]
    public async Task Hosts_sizes_and_listing_cache_round_trip()
    {
        _index.UpsertHost(new NetworkHostRecord("NAS", "192.168.1.10", [445, 22], DateTimeOffset.UtcNow));
        _index.SetDirectorySize(@"C:\Big", 123_456, 42);
        _index.CacheListing(@"\\nas\media",
        [
            new VfsEntry("..", @"\\nas", VfsEntryKind.ParentLink),
            new VfsEntry("Movies", @"\\nas\media\Movies", VfsEntryKind.Directory),
        ]);
        await _index.FlushAsync();

        var host = Assert.Single(await _index.GetHostsAsync(TestToken));
        Assert.Equal([445, 22], host.OpenPorts);

        var size = await _index.GetDirectorySizeAsync(@"c:\big", TestToken);
        Assert.Equal(123_456, size?.Bytes);

        var cached = Assert.Single(await _index.GetCachedListingAsync(@"\\NAS\media", TestToken));
        Assert.Equal("Movies", cached.Name);
        Assert.True(cached.Flags.HasFlag(VfsEntryFlags.Cached));
    }
}
