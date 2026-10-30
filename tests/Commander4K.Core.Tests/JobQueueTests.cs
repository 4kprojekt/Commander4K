using System.Collections.Concurrent;
using Commander4K.Core.Jobs;

namespace Commander4K.Core.Tests;

public sealed class JobQueueTests
{
    [Fact]
    public async Task Jobs_in_one_queue_run_sequentially()
    {
        await using var manager = new JobQueueManager();
        var log = new ConcurrentQueue<string>();

        Task Work(string name) => Task.Run(async () =>
        {
            log.Enqueue(name + ":start");
            await Task.Delay(30);
            log.Enqueue(name + ":end");
        });

        var first = manager.Enqueue(new DelegateJob("first", (_, _) => Work("a")), JobQueueManager.DefaultQueue);
        var second = manager.Enqueue(new DelegateJob("second", (_, _) => Work("b")), JobQueueManager.DefaultQueue);
        await Task.WhenAll(first.Completion, second.Completion);

        Assert.Equal(["a:start", "a:end", "b:start", "b:end"], log);
        Assert.Equal(JobState.Completed, second.State);
    }

    [Fact]
    public async Task Jobs_without_queue_run_in_parallel()
    {
        await using var manager = new JobQueueManager();
        var bothRunning = new TaskCompletionSource();
        var running = 0;

        Task Job(IProgress<JobMetrics> _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref running) == 2)
            {
                bothRunning.TrySetResult();
            }

            return bothRunning.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        var a = manager.Enqueue(new DelegateJob("a", Job));
        var b = manager.Enqueue(new DelegateJob("b", Job));
        await Task.WhenAll(a.Completion, b.Completion);

        Assert.Equal(JobState.Completed, a.State);
        Assert.Equal(JobState.Completed, b.State);
    }

    [Fact]
    public async Task Cancel_and_failure_are_reported()
    {
        await using var manager = new JobQueueManager();

        var canceled = manager.Enqueue(new DelegateJob("loop", (_, ct) => Task.Delay(Timeout.Infinite, ct)));
        var failed = manager.Enqueue(new DelegateJob("boom", (_, _) => throw new IOException("disk full")));
        canceled.Cancel();
        await Task.WhenAll(canceled.Completion, failed.Completion);

        Assert.Equal(JobState.Canceled, canceled.State);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.IsType<IOException>(failed.Error);
    }

    [Fact]
    public async Task Progress_is_throttled_but_final_value_arrives()
    {
        await using var manager = new JobQueueManager(TimeSpan.FromMilliseconds(50));
        var notifications = 0;
        manager.JobChanged += _ => Interlocked.Increment(ref notifications);

        var job = manager.Enqueue(new DelegateJob("copy", (progress, _) =>
        {
            for (var i = 1; i <= 100_000; i++)
            {
                progress.Report(new JobMetrics(i, 100_000, 0, 0));
            }

            return Task.CompletedTask;
        }));
        await job.Completion;

        Assert.Equal(100_000, job.Metrics.BytesProcessed);
        Assert.InRange(job.Metrics.Fraction, 1, 1);
        Assert.True(notifications < 100, $"Expected throttled notifications, got {notifications}");
    }
}
