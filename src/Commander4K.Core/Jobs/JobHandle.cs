using System.Diagnostics;

namespace Commander4K.Core.Jobs;

/// <summary>Live view of one job. Properties are updated from worker threads; read them from anywhere.</summary>
public sealed class JobHandle
{
    private static long s_nextId;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _metricsGate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _startedTimestamp;
    private int _state;
    private JobMetrics _metrics;

    internal JobHandle(IJob job, string? queueName)
    {
        Job = job;
        QueueName = queueName;
        Id = Interlocked.Increment(ref s_nextId);
    }

    public long Id { get; }

    public IJob Job { get; }

    public string Title => Job.Title;

    /// <summary>Queue the job runs in, or <c>null</c> when it started immediately in parallel.</summary>
    public string? QueueName { get; }

    public JobState State => (JobState)Volatile.Read(ref _state);

    public JobMetrics Metrics
    {
        get { lock (_metricsGate) { return _metrics; } }
    }

    public Exception? Error { get; private set; }

    /// <summary>Completes when the job reaches a terminal state; never faults.</summary>
    public Task Completion => _completion.Task;

    public TimeSpan Elapsed => _startedTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_startedTimestamp);

    public double BytesPerSecond
    {
        get
        {
            var seconds = Elapsed.TotalSeconds;
            return seconds > 0 ? Metrics.BytesProcessed / seconds : 0;
        }
    }

    internal CancellationToken CancellationToken => _cancellation.Token;

    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    internal void MarkRunning()
    {
        _startedTimestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref _state, (int)JobState.Running);
    }

    internal void SetMetrics(JobMetrics metrics)
    {
        lock (_metricsGate)
        {
            _metrics = metrics;
        }
    }

    internal void Finish(JobState state, Exception? error)
    {
        Error = error;
        Volatile.Write(ref _state, (int)state);
        _cancellation.Dispose();
        _completion.TrySetResult();
    }
}
