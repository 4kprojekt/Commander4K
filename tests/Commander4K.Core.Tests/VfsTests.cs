using System.Diagnostics;
using System.Threading.Channels;
using Commander4K.Core.Execution;
using Commander4K.Core.Streaming;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class VfsTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(@"C:\Windows", VfsPath.FileScheme)]
    [InlineData(@"\\?\C:\Very\Long", VfsPath.FileScheme)]
    [InlineData(@"\\server\share", VfsPath.SmbScheme)]
    [InlineData(@"\\?\UNC\server\share", VfsPath.SmbScheme)]
    [InlineData("net://", "net")]
    [InlineData("REG://HKCU/Software", "reg")]
    [InlineData("sftp://host/home", "sftp")]
    [InlineData("relative\\path", null)]
    public void GetScheme_classifies_paths(string path, string? expected) =>
        Assert.Equal(expected, VfsPath.GetScheme(path));

    [Fact]
    public async Task Router_streams_listing_through_channel()
    {
        var provider = new FakeVfsProvider().AddFile("mem://a.txt", 10).AddDirectory("mem://dir");
        var router = new VfsRouter(new ResilientAsyncExecutor(), [provider]);

        var entries = new List<VfsEntry>();
        await foreach (var entry in router.OpenListing("mem://", TestToken).ReadAllAsync(TestToken))
        {
            entries.Add(entry);
        }

        Assert.Equal(["a.txt", "dir"], entries.Select(e => e.Name));
    }

    [Fact]
    public void Router_rejects_unknown_scheme()
    {
        var router = new VfsRouter(new ResilientAsyncExecutor());
        Assert.Throws<VfsProviderNotFoundException>(() => router.Resolve("ftp://nowhere"));
    }

    [Fact]
    public async Task Cancelling_a_hung_listing_detaches_immediately()
    {
        var provider = new FakeVfsProvider().AddDirectory("mem://dead");
        provider.HangOn.Add("mem://dead");
        var executor = new ResilientAsyncExecutor();
        var router = new VfsRouter(executor, [provider]);

        using var cts = new CancellationTokenSource();
        var reader = router.OpenListing("mem://dead", cts.Token);
        await Task.Delay(50, TestToken);

        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in reader.ReadAllAsync(TestToken))
            {
            }
        });
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"Detach took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1, executor.AbandonedOperations);

        provider.Release.Set();
        await WaitUntilAsync(() => executor.AbandonedOperations == 0);
    }

    [Fact]
    public async Task RunAsync_times_out_blocking_call()
    {
        var executor = new ResilientAsyncExecutor();
        using var release = new ManualResetEventSlim(false);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            executor.RunAsync(_ => release.Wait(Timeout.Infinite), TestToken, TimeSpan.FromMilliseconds(50)));

        release.Set();
        await WaitUntilAsync(() => executor.AbandonedOperations == 0);
    }

    [Fact]
    public async Task Batches_emit_first_items_immediately_then_coalesce()
    {
        var channel = Channel.CreateUnbounded<int>();
        var batches = new List<IReadOnlyList<int>>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var batch in channel.Reader.ReadBatchesAsync(TimeSpan.FromMilliseconds(30), cancellationToken: TestToken))
            {
                batches.Add(batch);
            }
        }, TestToken);

        channel.Writer.TryWrite(1);
        await WaitUntilAsync(() => batches.Count == 1);
        for (var i = 2; i <= 1000; i++)
        {
            channel.Writer.TryWrite(i);
        }

        channel.Writer.Complete();
        await consumer;

        Assert.Equal([1], batches[0]);
        Assert.Equal(1000, batches.Sum(b => b.Count));
        Assert.True(batches.Count <= 3, $"Expected coalesced batches, got {batches.Count}");
    }

    [Fact]
    public async Task Directory_size_sums_tree_in_parallel()
    {
        var provider = new FakeVfsProvider()
            .AddDirectory("mem://root")
            .AddFile("mem://root/a", 100)
            .AddDirectory("mem://root/sub")
            .AddFile("mem://root/sub/b", 50)
            .AddDirectory("mem://root/sub/deeper")
            .AddFile("mem://root/sub/deeper/c", 25)
            .AddDirectory("mem://root/empty");
        var calculator = new DirectorySizeCalculator(new VfsRouter(new ResilientAsyncExecutor(), [provider]));

        var size = await calculator.CalculateAsync("mem://root", cancellationToken: TestToken);

        Assert.Equal(new DirectorySize(175, 3, 3, 0), size);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "Condition not reached in time.");
            await Task.Delay(10, TestToken);
        }
    }
}
