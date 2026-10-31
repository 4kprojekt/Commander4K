using System.Collections.Concurrent;
using System.Diagnostics;
using Commander4K.Core.Dashboard;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

public sealed class CardRefreshLoopTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static PreviewCard Card(string title) => new(title, null, null, CardStatus.Ok, [], []);

    [Fact]
    public async Task Hanging_loader_times_out_without_blocking_others()
    {
        var states = new ConcurrentQueue<CardState>();
        await using var loop = new CardRefreshLoop(maxConcurrent: 4, timeout: TimeSpan.FromMilliseconds(200));
        loop.Updated += states.Enqueue;
        var never = new TaskCompletionSource<PreviewCard>();
        loop.Add(0, (_, _) => never.Task, null); // ignores its token, like a hung SMB call
        for (var i = 1; i < 5; i++)
        {
            var title = $"card {i}";
            loop.Add(i, (_, _) => Task.FromResult(Card(title)), null);
        }

        var clock = Stopwatch.StartNew();
        loop.Start();
        await WaitUntilAsync(() => states.Any(s => s.Index == 0 && s.Error is not null) && Enumerable.Range(1, 4).All(i => states.Any(s => s.Index == i && s.Card is not null)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), clock.Elapsed.ToString());
        var timedOut = states.Last(s => s.Index == 0);
        Assert.False(timedOut.Loading);
        Assert.Null(timedOut.Card);
        Assert.Contains("15", new CardRefreshLoop().TimeoutMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_passes_previous_and_skips_ticks_while_loading()
    {
        var previous = new ConcurrentQueue<string?>();
        var calls = 0;
        await using var loop = new CardRefreshLoop();
        loop.Add(0, async (last, ct) =>
        {
            previous.Enqueue(last?.Title);
            var n = Interlocked.Increment(ref calls);
            await Task.Delay(n == 1 ? 250 : 1, ct); // the first read outlasts several ticks
            return Card($"read {n}");
        }, TimeSpan.FromMilliseconds(40));

        loop.Start();
        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 3);
        await loop.StopAsync();

        var seen = previous.ToArray();
        Assert.Null(seen[0]);
        Assert.Equal("read 1", seen[1]);
        Assert.Equal("read 2", seen[2]);
    }

    [Fact]
    public async Task Stop_cancels_in_flight_loads_and_timers()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updatesAfterStop = 0;
        var stopped = false;
        var loop = new CardRefreshLoop();
        loop.Updated += _ =>
        {
            if (Volatile.Read(ref stopped))
            {
                Interlocked.Increment(ref updatesAfterStop);
            }
        };
        loop.Add(0, async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return Card("never");
        }, TimeSpan.FromMilliseconds(30));
        loop.Add(1, (_, _) => Task.FromResult(Card("quick")), TimeSpan.FromMilliseconds(30));

        loop.Start();
        await Task.Delay(100, TestToken);
        await loop.StopAsync();
        Volatile.Write(ref stopped, true);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestToken);
        await Task.Delay(300, TestToken);
        Assert.Equal(0, Volatile.Read(ref updatesAfterStop));
    }

    [Fact]
    public async Task Refresh_now_is_ignored_while_loading()
    {
        var calls = 0;
        var release = new TaskCompletionSource<PreviewCard>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var loop = new CardRefreshLoop();
        loop.Add(0, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return release.Task;
        }, null);

        loop.Start();
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
        loop.RefreshNow(0);
        loop.RefreshNow(0);
        await Task.Delay(100, TestToken);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.SetResult(Card("done"));
        await Task.Delay(50, TestToken);
        loop.RefreshNow(0);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
    }

    [Fact]
    public async Task Abandoned_read_blocks_new_reads_until_it_finishes()
    {
        var calls = 0;
        var hung = new TaskCompletionSource<PreviewCard>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var loop = new CardRefreshLoop(timeout: TimeSpan.FromMilliseconds(100));
        loop.Add(0, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return hung.Task; // ignores its token, like a hung SMB call holding a thread
        }, TimeSpan.FromMilliseconds(40));

        loop.Start();
        await Task.Delay(700, TestToken);
        Assert.Equal(1, Volatile.Read(ref calls));

        hung.SetResult(Card("late"));
        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task Failures_keep_the_previous_card_and_report_the_message()
    {
        var states = new ConcurrentQueue<CardState>();
        var calls = 0;
        await using var loop = new CardRefreshLoop();
        loop.Updated += states.Enqueue;
        loop.Add(0, (_, _) => Interlocked.Increment(ref calls) == 1 ? Task.FromResult(Card("good")) : throw new IOException("share gone"), null);

        loop.Start();
        await WaitUntilAsync(() => states.Any(s => s.Card is not null));
        loop.RefreshNow(0);
        await WaitUntilAsync(() => states.Any(s => s.Error is not null));

        var failed = states.Last();
        Assert.Equal("share gone", failed.Error);
        Assert.Equal("good", failed.Card!.Title);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "condition not met in 5 s");
            await Task.Delay(10, TestToken);
        }
    }
}
