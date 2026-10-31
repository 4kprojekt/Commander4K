using System.Diagnostics;
using Commander4K.Core.Plugins;
using Microsoft.Extensions.Time.Testing;

namespace Commander4K.Core.Tests;

public sealed class BackgroundTickLoopTests
{
    /// <summary>
    /// Waits for the loop's own signal that it is parked on a fake-clock timer (<see cref="BackgroundTickLoop.IsIdle"/>),
    /// so the clock never moves while the loop is between reading the time and setting its next timer. No timing
    /// assumption: the loop is checked, not given a head start.
    /// </summary>
    private static async Task SettleAsync(BackgroundTickLoop loop)
    {
        var limit = Stopwatch.StartNew();
        while (!loop.IsIdle)
        {
            Assert.True(limit.Elapsed < TimeSpan.FromSeconds(30), "the loop never parked on a timer");
            await Task.Yield();
            await Task.Delay(1);
        }
    }

    private static async Task AdvanceAsync(BackgroundTickLoop loop, FakeTimeProvider time, TimeSpan by)
    {
        // Step in slices of at most a second, each from a parked loop.
        var left = by;
        while (left > TimeSpan.Zero)
        {
            var step = left > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
            await SettleAsync(loop);
            time.Advance(step);
            left -= step;
        }

        await SettleAsync(loop);
    }

    private static (Task Run, CancellationTokenSource Stop) Start(BackgroundTickLoop loop)
    {
        var stop = new CancellationTokenSource();
        return (loop.RunAsync(stop.Token), stop);
    }

    [Fact]
    public async Task Ticks_follow_interval_and_more()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var stamps = new List<double>();
        var answers = new Queue<bool>([true, false, false]);
        var loop = new BackgroundTickLoop(time, () => true);
        loop.Register("p", TimeSpan.FromMinutes(1), _ =>
        {
            stamps.Add((time.GetUtcNow() - start).TotalSeconds);
            return Task.FromResult(answers.Count > 0 && answers.Dequeue());
        });
        var (run, stop) = Start(loop);
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(63));
        await stop.CancelAsync();
        await run;
        Assert.Equal([0d, 2d, 62d], stamps);
    }

    [Fact]
    public async Task Interval_below_one_minute_is_clamped()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var stamps = new List<double>();
        var loop = new BackgroundTickLoop(time, () => true);
        loop.Register("p", TimeSpan.FromSeconds(10), _ =>
        {
            stamps.Add((time.GetUtcNow() - start).TotalSeconds);
            return Task.FromResult(false);
        });
        var (run, stop) = Start(loop);
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(61));
        await stop.CancelAsync();
        await run;
        Assert.Equal([0d, 60d], stamps);
    }

    [Fact]
    public async Task No_overlap_while_tick_runs()
    {
        var time = new FakeTimeProvider();
        var release = new TaskCompletionSource<bool>();
        var count = 0;
        var loop = new BackgroundTickLoop(time, () => true);
        loop.Register("p", TimeSpan.FromMinutes(1), async _ =>
        {
            Interlocked.Increment(ref count);
            return await release.Task;
        });
        var (run, stop) = Start(loop);
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(59));
        loop.Wake("p");
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(0.5));
        Assert.Equal(1, count);
        release.SetResult(false);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Tick_that_hangs_is_abandoned_and_next_tick_runs()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var stamps = new List<double>();
        var never = new TaskCompletionSource<bool>();
        var loop = new BackgroundTickLoop(time, () => true);
        loop.Register("p", TimeSpan.FromMinutes(1), _ =>
        {
            stamps.Add((time.GetUtcNow() - start).TotalSeconds);
            return stamps.Count == 1 ? never.Task : Task.FromResult(false);
        });
        var (run, stop) = Start(loop);
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(125));
        never.SetException(new IOException("late failure"));
        await SettleAsync(loop);
        Assert.False(run.IsFaulted);
        await stop.CancelAsync();
        await run;
        Assert.Equal([0d, 120d], stamps);
    }

    [Fact]
    public async Task Disabled_setting_stops_ticks()
    {
        var time = new FakeTimeProvider();
        var enabled = false;
        var count = 0;
        var loop = new BackgroundTickLoop(time, () => enabled);
        loop.Register("p", TimeSpan.FromMinutes(1), _ =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(false);
        });
        var (run, stop) = Start(loop);
        await AdvanceAsync(loop, time, TimeSpan.FromMinutes(3));
        Assert.Equal(0, count);
        enabled = true;
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(3));
        Assert.Equal(1, count);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Wake_runs_now()
    {
        var time = new FakeTimeProvider();
        var count = 0;
        var loop = new BackgroundTickLoop(time, () => true);
        loop.Register("p", TimeSpan.FromMinutes(5), _ =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(false);
        });
        var (run, stop) = Start(loop);
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(2));
        Assert.Equal(1, count);
        loop.Wake("p");
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(2));
        Assert.Equal(2, count);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Wake_from_inside_a_tick_cannot_spin()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var stamps = new List<double>();
        var loop = new BackgroundTickLoop(time, () => true);
        loop.Register("p", TimeSpan.FromMinutes(5), _ =>
        {
            stamps.Add((time.GetUtcNow() - start).TotalSeconds);
            loop.Wake("p");
            return Task.FromResult(false);
        });
        var (run, stop) = Start(loop);
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(7));
        await stop.CancelAsync();
        await run;
        Assert.True(stamps.Count >= 2);
        for (var i = 1; i < stamps.Count; i++)
        {
            Assert.True(stamps[i] - stamps[i - 1] >= 2, string.Join(",", stamps));
        }
    }

    [Fact]
    public async Task Wake_at_due_time_does_not_double_tick()
    {
        var time = new FakeTimeProvider();
        var count = 0;
        var loop = new BackgroundTickLoop(time, () => true);
        loop.Register("p", TimeSpan.FromMinutes(1), _ =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(false);
        });
        var (run, stop) = Start(loop);
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(59));
        Assert.Equal(1, count);
        time.Advance(TimeSpan.FromSeconds(1));
        loop.Wake("p");
        await AdvanceAsync(loop, time, TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, count);
        await stop.CancelAsync();
        await run;
    }
}
