using System.Diagnostics;
using System.Runtime.CompilerServices;
using Commander4K.Core.Folders;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class FolderSuggestionServiceTests : IDisposable
{
    private static readonly VfsEntry Photos = new("photos", @"C:\photos", VfsEntryKind.Directory, 0, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private readonly string _folder = Directory.CreateTempSubdirectory("c4k-suggest-").FullName;
    private int _listings;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakePreview(string id, double score, int priority = 10, TimeSpan delay = default, bool throws = false, bool asks = false)
        : IFolderSuggestingPreview, IAskingFolderSuggester
    {
        public FolderSnapshot? Seen { get; private set; }

        public string Id => id;

        public string DisplayName => id;

        public PluginManifest Manifest { get; } = new(id, id, "1.0.0", "test", "", []);

        public int Priority => priority;

        public bool AsksPlugin => asks;

        public bool CanPreview(VfsEntry entry) => false;

        public Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<FolderSuggestion?> SuggestAsync(FolderSnapshot folder, IPluginHost host, CancellationToken cancellationToken)
        {
            Seen = folder;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return throws ? throw new InvalidOperationException("broken") : new FolderSuggestion(score, id, new FolderSuggestionView(View: id));
        }
    }

    private FolderSuggestionService Service(IReadOnlyList<IPreviewPlugin> previews, IReadOnlyList<string> names, FolderSuggestionMemory? memory = null, bool hang = false) =>
        new(() => previews, _ => null!, (path, ct) => List(names, hang, ct), memory ?? new FolderSuggestionMemory(null));

    private async IAsyncEnumerable<VfsEntry> List(IReadOnlyList<string> names, bool hang, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listings);
        if (hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        foreach (var name in names)
        {
            yield return new VfsEntry(name, @"C:\photos\" + name, VfsEntryKind.File, 1);
        }
    }

    private static async Task<List<RankedSuggestion>> Collect(FolderSuggestionService service, VfsEntry folder, CancellationToken? token = null)
    {
        var all = new List<RankedSuggestion>();
        await foreach (var item in service.SuggestAsync(folder, token ?? TestToken))
        {
            all.Add(item);
        }

        return all;
    }

    private static readonly string[] Pictures = ["a.png", "b.png"];

    [Fact]
    public async Task Highest_score_wins_and_low_scores_are_ignored()
    {
        var result = await Collect(Service([new FakePreview("low", 0.4), new FakePreview("good", 0.7)], Pictures), Photos);

        Assert.Equal("good", Assert.Single(result).Suggestion.Message);
    }

    [Fact]
    public async Task Tie_prefers_external_then_priority()
    {
        var result = await Collect(Service([new FakePreview("builtin.preview.folder", 0.7, priority: 50), new FakePreview("plugin.a", 0.7, priority: 1), new FakePreview("plugin.b", 0.7, priority: 5)], Pictures), Photos);

        Assert.Equal("plugin.b", result[^1].Suggestion.Message);
    }

    [Fact]
    public async Task Folder_with_c4k_dir_yields_nothing() =>
        Assert.Empty(await Collect(Service([new FakePreview("good", 0.9)], ["a.png", "C4K.DIR"]), Photos));

    [Fact]
    public async Task Muted_and_declined_folders_yield_nothing()
    {
        var file = Path.Combine(_folder, "folder-suggestions.json");
        var memory = await FolderSuggestionMemory.LoadAsync(file, TestToken);
        memory.MuteForSession(@"C:\PHOTOS");
        await memory.DeclineAsync(@"C:\other");

        Assert.Empty(await Collect(Service([new FakePreview("good", 0.9)], Pictures, memory), Photos));
        var reloaded = await FolderSuggestionMemory.LoadAsync(file, TestToken);
        Assert.True(reloaded.IsMuted(@"C:\Other"));
        Assert.False(reloaded.IsMuted(@"C:\photos"));

        await reloaded.ClearAsync();
        Assert.False((await FolderSuggestionMemory.LoadAsync(file, TestToken)).IsMuted(@"C:\other"));
    }

    [Fact]
    public async Task Throwing_candidate_does_not_hide_others()
    {
        var result = await Collect(Service([new FakePreview("broken", 0.9, throws: true), new FakePreview("good", 0.6)], Pictures), Photos);

        Assert.Equal("good", Assert.Single(result).Suggestion.Message);
    }

    [Fact]
    public async Task Hanging_listing_ends_within_budget()
    {
        var clock = Stopwatch.StartNew();

        var result = await Collect(Service([new FakePreview("good", 0.9)], Pictures, hang: true), Photos);

        Assert.Empty(result);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2.5), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task Slow_ask_candidate_arrives_later_and_replaces()
    {
        var result = await Collect(Service([new FakePreview("fast", 0.7), new FakePreview("asked", 0.8, delay: TimeSpan.FromSeconds(2), asks: true)], Pictures), Photos);

        Assert.Equal(["fast", "asked"], result.Select(r => r.Suggestion.Message));
    }

    [Fact]
    public async Task Slow_fast_candidate_is_dropped_after_budget() =>
        Assert.Empty(await Collect(Service([new FakePreview("slow", 0.9, delay: TimeSpan.FromSeconds(3))], Pictures), Photos));

    [Fact]
    public async Task Cancelled_token_yields_nothing_and_does_not_throw()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await Collect(Service([new FakePreview("slow", 0.9, delay: TimeSpan.FromSeconds(1))], Pictures), Photos, cancel.Token);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Listing_is_capped_at_500_and_marked_truncated()
    {
        var preview = new FakePreview("good", 0.9);

        await Collect(Service([preview], [.. Enumerable.Range(0, 800).Select(i => $"p{i}.png")]), Photos);

        Assert.Equal(500, preview.Seen!.Entries.Count);
        Assert.True(preview.Seen.Truncated);
    }

    [Fact]
    public async Task Second_call_uses_cache()
    {
        var service = Service([new FakePreview("good", 0.9)], Pictures);

        await Collect(service, Photos);
        var again = await Collect(service, Photos);

        Assert.Single(again);
        Assert.Equal(1, _listings);
    }

    [Theory]
    [InlineData("sftp://h/x", VfsEntryKind.Directory)]
    [InlineData(@"zip://C:\a.zip::/x", VfsEntryKind.Directory)]
    [InlineData(@"C:\photos\..", VfsEntryKind.ParentLink)]
    [InlineData(@"C:\photos\a.png", VfsEntryKind.File)]
    [InlineData(@"C:\", VfsEntryKind.Drive)]
    public void Ineligible_entries(string path, VfsEntryKind kind) =>
        Assert.False(FolderSuggestionService.IsEligible(new VfsEntry("x", path, kind)));

    [Fact]
    public void Disk_and_share_folders_are_eligible()
    {
        Assert.True(FolderSuggestionService.IsEligible(Photos));
        Assert.True(FolderSuggestionService.IsEligible(new VfsEntry("x", @"\\srv\share\x", VfsEntryKind.Directory)));
    }

    [Fact]
    public async Task Tour_shows_until_three_dismissals_or_use()
    {
        var file = Path.Combine(_folder, "tour.json");
        var memory = await FolderSuggestionMemory.LoadAsync(file, TestToken);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(memory.ShouldShowTour);
            await memory.RecordTourDismissedAsync();
        }

        Assert.False((await FolderSuggestionMemory.LoadAsync(file, TestToken)).ShouldShowTour);

        var used = new FolderSuggestionMemory(null);
        await used.RecordTourUsedAsync();
        Assert.False(used.ShouldShowTour);
    }
}
