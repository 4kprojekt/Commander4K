using System.Collections.Concurrent;
using System.Threading.Channels;
using Commander4K.Core.Execution;

namespace Commander4K.Core.Jobs;

/// <summary>
/// Background job runner modelled on Total Commander's F5 / F2-queue behaviour: jobs enqueued without a queue name
/// start immediately in parallel; jobs sharing a queue name run one after another, while different queues run
/// concurrently.
/// </summary>
public sealed class JobQueueManager : IAsyncDisposable
{
    public const string DefaultQueue = "default";

    private readonly ConcurrentDictionary<long, JobHandle> _jobs = new();
    // Lazy: GetOrAdd may run its factory twice, and a Lane starts a consumer task that must exist exactly once.
    private readonly ConcurrentDictionary<string, Lazy<Lane>> _lanes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly TimeSpan _progressInterval;
    private volatile bool _disposed;

    public JobQueueManager(TimeSpan? progressInterval = null)
    {
        _progressInterval = progressInterval ?? ThrottledProgress<JobMetrics>.DefaultInterval;
    }

    /// <summary>
    /// Raised on a worker thread when a job changes state or reports (throttled) progress. UI subscribers must
    /// marshal to their dispatcher.
    /// </summary>
    public event Action<JobHandle>? JobChanged;

    public IReadOnlyCollection<JobHandle> Jobs => [.. _jobs.Values.OrderBy(j => j.Id)];

    /// <param name="queueName">Sequential queue to append to, or <c>null</c> to start right away in parallel.</param>
    public JobHandle Enqueue(IJob job, string? queueName = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var handle = new JobHandle(job, queueName);
        _jobs[handle.Id] = handle;
        Raise(handle);

        if (queueName is null)
        {
            Track(Task.Run(() => RunAsync(handle)));
        }
        else
        {
            var lane = _lanes.GetOrAdd(queueName, _ => new Lazy<Lane>(() => new Lane(this))).Value;
            lane.Pending.Writer.TryWrite(handle);
        }

        return handle;
    }

    /// <summary>Removes finished jobs from <see cref="Jobs"/>.</summary>
    public void ClearFinished()
    {
        foreach (var (id, handle) in _jobs)
        {
            if (handle.State is JobState.Completed or JobState.Failed or JobState.Canceled)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }

    public void CancelAll()
    {
        foreach (var handle in _jobs.Values)
        {
            handle.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAll();
        foreach (var lane in _lanes.Values)
        {
            lane.Value.Pending.Writer.TryComplete();
        }

        await Task.WhenAll(_running.Keys).ConfigureAwait(false);
    }

    private async Task RunAsync(JobHandle handle)
    {
        if (handle.CancellationToken.IsCancellationRequested)
        {
            Complete(handle, JobState.Canceled, null);
            return;
        }

        handle.MarkRunning();
        Raise(handle);

        var progress = new ThrottledProgress<JobMetrics>(
            metrics =>
            {
                handle.SetMetrics(metrics);
                Raise(handle);
            },
            _progressInterval);

        try
        {
            await handle.Job.ExecuteAsync(progress, handle.CancellationToken).ConfigureAwait(false);
            progress.Flush();
            Complete(handle, JobState.Completed, null);
        }
        catch (OperationCanceledException) when (handle.CancellationToken.IsCancellationRequested)
        {
            progress.Flush();
            Complete(handle, JobState.Canceled, null);
        }
        catch (Exception ex)
        {
            progress.Flush();
            Complete(handle, JobState.Failed, ex);
        }
    }

    private void Complete(JobHandle handle, JobState state, Exception? error)
    {
        handle.Finish(state, error);
        Raise(handle);
    }

    private void Raise(JobHandle handle)
    {
        try
        {
            JobChanged?.Invoke(handle);
        }
        catch (Exception ex)
        {
            // A broken subscriber must never kill a copy job.
            System.Diagnostics.Trace.TraceError($"JobChanged handler failed: {ex}");
        }
    }

    private void Track(Task task)
    {
        _running[task] = 0;
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
    }

    private sealed class Lane
    {
        public Lane(JobQueueManager owner)
        {
            owner.Track(Task.Run(async () =>
            {
                await foreach (var handle in Pending.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await owner.RunAsync(handle).ConfigureAwait(false);
                }
            }));
        }

        public Channel<JobHandle> Pending { get; } =
            Channel.CreateUnbounded<JobHandle>(new UnboundedChannelOptions { SingleReader = true });
    }
}
