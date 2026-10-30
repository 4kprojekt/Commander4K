namespace Commander4K.Core.Jobs;

public enum JobState : byte
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
}

public readonly record struct JobMetrics(
    long BytesProcessed,
    long BytesTotal,
    int ItemsProcessed,
    int ItemsTotal,
    string? CurrentItem = null)
{
    /// <summary>Completion in [0, 1], by bytes when known, otherwise by items; -1 when indeterminate.</summary>
    public double Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesProcessed / BytesTotal, 0, 1)
        : ItemsTotal > 0 ? Math.Clamp((double)ItemsProcessed / ItemsTotal, 0, 1) : -1;
}

/// <summary>Background operation (copy, move, pack, delete...) executed by <see cref="JobQueueManager"/>.</summary>
public interface IJob
{
    string Title { get; }

    /// <remarks>
    /// <paramref name="progress"/> is thread-safe and throttled, so it may be called for every buffer or file.
    /// </remarks>
    Task ExecuteAsync(IProgress<JobMetrics> progress, CancellationToken cancellationToken);
}

public sealed class DelegateJob(string title, Func<IProgress<JobMetrics>, CancellationToken, Task> execute) : IJob
{
    public string Title { get; } = title;

    public Task ExecuteAsync(IProgress<JobMetrics> progress, CancellationToken cancellationToken) =>
        execute(progress, cancellationToken);
}
