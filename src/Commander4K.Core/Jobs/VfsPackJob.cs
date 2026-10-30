using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Jobs;

/// <summary>Writes an archive stream from the inputs; the format is the packer's business (e.g. from the file name).</summary>
public delegate ValueTask ArchivePacker(Stream output, IReadOnlyList<ArchiveInput> inputs, IProgress<long>? progress, CancellationToken cancellationToken);

/// <summary>
/// Packs files and folders from any VFS into a new archive on any writable VFS (Alt+F5). Folders are walked through
/// the router; entries keep their paths relative to the packed items. A cancelled or failed job removes the partial
/// archive.
/// </summary>
public sealed class VfsPackJob(VfsRouter router, ArchivePacker packer, IReadOnlyList<VfsEntry> sources, string archivePath) : IJob
{
    public string Title { get; } = $"Pakowanie {VfsTransferJob.Describe(sources)} → {archivePath}";

    public async Task ExecuteAsync(IProgress<JobMetrics> progress, CancellationToken cancellationToken)
    {
        var files = new List<(VfsEntry Entry, string EntryPath)>();
        var stack = new Stack<(VfsEntry Entry, string EntryPath)>(sources.Select(s => (s, s.Name)));
        long total = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entry, entryPath) = stack.Pop();
            if (!entry.IsContainer)
            {
                files.Add((entry, entryPath));
                total += Math.Max(0, entry.Size);
                continue;
            }

            if ((entry.Flags & VfsEntryFlags.ReparsePoint) != 0)
            {
                continue;
            }

            await foreach (var child in router.EnumerateAsync(entry.Path, cancellationToken).ConfigureAwait(false))
            {
                stack.Push((child, entryPath + "/" + child.Name));
            }

            progress.Report(new JobMetrics(0, total, 0, files.Count, "Skanowanie: " + entry.Name));
        }

        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.EntryPath, b.EntryPath));
        var inputs = files.Select(f => new ArchiveInput(
            f.EntryPath,
            async ct => await router.OpenReadAsync(f.Entry.Path, ct).ConfigureAwait(false),
            f.Entry.LastWriteTime == default ? null : f.Entry.LastWriteTime)).ToList();

        var completed = false;
        try
        {
            await using (var output = await router.OpenWriteAsync(archivePath, overwrite: false, cancellationToken).ConfigureAwait(false))
            {
                await packer(output, inputs, new SynchronousProgress(done => progress.Report(new JobMetrics(done, total, 0, files.Count, Path.GetFileName(archivePath)))), cancellationToken)
                    .ConfigureAwait(false);
            }

            progress.Report(new JobMetrics(total, total, files.Count, files.Count));
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    await router.DeleteAsync(archivePath, recursive: false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // The archive may not have been created at all.
                }
            }
        }
    }

    /// <summary>The job's progress sink is already throttled and thread-safe; skip <see cref="Progress{T}"/>'s posting.</summary>
    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
