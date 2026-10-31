using Commander4K.Core.Plugins;
using Microsoft.Extensions.Time.Testing;

namespace Commander4K.Core.Tests;

public sealed class BackgroundTickLoopTests
{
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await Task.Run(() => Thread.Sleep(2));
        }
    }

    private static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan by)
    {
        // Step in small slices so every timer the loop sets is registered before time moves on.
        var left = by;
        while (left > TimeSpan.Zero)
        {
            var step = left > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
            await SettleAsync();
            time.Advance(step);
            left -= step;
        }

        await SettleAsync();
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
        await AdvanceAsync(time, TimeSpan.FromSeconds(63));
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
        await AdvanceAsync(time, TimeSpan.FromSeconds(61));
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
        await AdvanceAsync(time, TimeSpan.FromSeconds(59));
        loop.Wake("p");
        await AdvanceAsync(time, TimeSpan.FromSeconds(0.5));
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
        await AdvanceAsync(time, TimeSpan.FromSeconds(125));
        never.SetException(new IOException("late failure"));
        await SettleAsync();
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
        await AdvanceAsync(time, TimeSpan.FromMinutes(3));
        Assert.Equal(0, count);
        enabled = true;
        await AdvanceAsync(time, TimeSpan.FromSeconds(3));
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
        await AdvanceAsync(time, TimeSpan.FromSeconds(2));
        Assert.Equal(1, count);
        loop.Wake("p");
        await AdvanceAsync(time, TimeSpan.FromSeconds(2));
        Assert.Equal(2, count);
        await stop.CancelAsync();
        await run;
    }
}
