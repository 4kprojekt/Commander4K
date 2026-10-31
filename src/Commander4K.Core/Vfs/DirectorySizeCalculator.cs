using System.Threading.Channels;
using Commander4K.Core.Execution;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Vfs;

public readonly record struct DirectorySize(long Bytes, long Files, long Directories, int InaccessibleDirectories);

/// <summary>
/// Computes the recursive size of a directory on any VFS provider with a pool of workers sharing one work queue, so
/// wide trees use every core. Reparse points are counted but not followed. Directory reads of all calculations share
/// one gate of half the cores, so marking ten folders with Space does not starve the UI and render threads (more
/// readers barely speed up a disk walk; measured on 12 cores, the total time stayed the same).
/// </summary>
public sealed class DirectorySizeCalculator(VfsRouter router)
{
    private static readonly SemaphoreSlim SharedGate = new(Math.Max(1, Environment.ProcessorCount / 2));

    private readonly VfsRouter _router = router ?? throw new ArgumentNullException(nameof(router));

    /// <summary>0 means <see cref="Environment.ProcessorCount"/>.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <param name="progress">Receives running totals, throttled to 30 Hz, plus the final value.</param>
    public async Task<DirectorySize> CalculateAsync(
        string path,
        IProgress<DirectorySize>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workerCount = MaxDegreeOfParallelism > 0 ? MaxDegreeOfParallelism : Environment.ProcessorCount;
        var throttled = progress is null ? null : new ThrottledProgress<DirectorySize>(progress.Report);

        var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false });
        long bytes = 0, files = 0, directories = 0;
        int inaccessible = 0;
        var outstanding = 1;
        queue.Writer.TryWrite(path);

        DirectorySize Snapshot() => new(
            Interlocked.Read(ref bytes),
            Interlocked.Read(ref files),
            Interlocked.Read(ref directories),
            Volatile.Read(ref inaccessible));

        void Inaccessible() => Interlocked.Increment(ref inaccessible);

        async Task WorkerAsync()
        {
            await foreach (var directory in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var entered = false;
                try
                {
                    await SharedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                    await foreach (var entry in _router.EnumerateForWalkAsync(directory, Inaccessible, cancellationToken).ConfigureAwait(false))
                    {
                        switch (entry.Kind)
                        {
                            case VfsEntryKind.Directory:
                                Interlocked.Increment(ref directories);
                                if ((entry.Flags & VfsEntryFlags.ReparsePoint) == 0)
                                {
                                    Interlocked.Increment(ref outstanding);
                                    queue.Writer.TryWrite(entry.Path);
                                }

                                break;
                            case VfsEntryKind.File:
                                Interlocked.Increment(ref files);
                                Interlocked.Add(ref bytes, Math.Max(0, entry.Size));
                                break;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref inaccessible);
                }
                finally
                {
                    if (entered)
                    {
                        SharedGate.Release();
                    }

                    if (Interlocked.Decrement(ref outstanding) == 0)
                    {
                        queue.Writer.TryComplete();
                    }
                }

                throttled?.Report(Snapshot());
            }
        }

        var workers = new Task[workerCount];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(WorkerAsync, cancellationToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        var result = Snapshot();
        progress?.Report(result);
        return result;
    }
}
