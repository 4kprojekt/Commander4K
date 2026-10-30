using System.Buffers;
using Commander4K.Core.Localization;
using Commander4K.Core.Vfs;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Jobs;

public enum TransferMode : byte
{
    Copy,
    Move,
}

public enum OverwritePolicy : byte
{
    Skip,
    Overwrite,
}

/// <summary>
/// Copies or moves entries between any two VFS providers (local → SMB, SFTP → local, ...). A move within one
/// provider is tried natively first (an instant rename on the same volume) and falls back to copy + delete.
/// </summary>
public sealed class VfsTransferJob(
    VfsRouter router,
    IReadOnlyList<VfsEntry> sources,
    string destinationDirectory,
    TransferMode mode,
    OverwritePolicy overwrite) : IJob
{
    private const int BufferSize = 1024 * 1024;

    public string Title { get; } = $"{(mode == TransferMode.Copy ? Loc.T("Kopiowanie") : Loc.T("Przenoszenie"))} {Describe(sources)} → {destinationDirectory}";

    public int SkippedFiles { get; private set; }

    public async Task ExecuteAsync(IProgress<JobMetrics> progress, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            var target = router.Combine(destinationDirectory, source.Name);
            if (string.Equals(Normalize(target), Normalize(source.Path), StringComparison.OrdinalIgnoreCase)
                || (source.IsContainer && Normalize(target).StartsWith(Normalize(source.Path) + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException(Loc.T("Nie można skopiować '{0}' do samego siebie.", source.Path));
            }
        }

        var pending = new List<VfsEntry>(sources);
        if (mode == TransferMode.Move && router.AreOnSameProvider(sources[0].Path, destinationDirectory))
        {
            pending.Clear();
            var moved = 0;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await router.MoveAsync(source.Path, router.Combine(destinationDirectory, source.Name), overwrite == OverwritePolicy.Overwrite, cancellationToken)
                        .ConfigureAwait(false);
                    progress.Report(new JobMetrics(0, 0, ++moved, sources.Count, source.Name));
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException)
                {
                    pending.Add(source); // e.g. across volumes: copy + delete below.
                }
            }

            if (pending.Count == 0)
            {
                return;
            }
        }

        var plan = await PlanAsync(pending, progress, cancellationToken).ConfigureAwait(false);
        await CopyAsync(plan, progress, cancellationToken).ConfigureAwait(false);

        if (mode == TransferMode.Move)
        {
            foreach (var source in pending)
            {
                await router.DeleteAsync(source.Path, recursive: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<TransferPlan> PlanAsync(IReadOnlyList<VfsEntry> roots, IProgress<JobMetrics> progress, CancellationToken cancellationToken)
    {
        var plan = new TransferPlan();
        var stack = new Stack<(VfsEntry Entry, string Target)>();
        foreach (var root in roots)
        {
            stack.Push((root, router.Combine(destinationDirectory, root.Name)));
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entry, target) = stack.Pop();
            if (!entry.IsContainer)
            {
                plan.Files.Add((entry, target));
                plan.TotalBytes += Math.Max(0, entry.Size);
                continue;
            }

            plan.Directories.Add(target);
            if ((entry.Flags & VfsEntryFlags.ReparsePoint) != 0)
            {
                continue;
            }

            await foreach (var child in router.EnumerateAsync(entry.Path, cancellationToken).ConfigureAwait(false))
            {
                stack.Push((child, router.Combine(target, child.Name)));
            }

            progress.Report(new JobMetrics(0, plan.TotalBytes, 0, plan.Files.Count, "Skanowanie: " + entry.Name));
        }

        return plan;
    }

    private async Task CopyAsync(TransferPlan plan, IProgress<JobMetrics> progress, CancellationToken cancellationToken)
    {
        foreach (var directory in plan.Directories)
        {
            if (await router.GetEntryAsync(directory, cancellationToken).ConfigureAwait(false) is null)
            {
                await router.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
            }
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            var files = 0;
            foreach (var (source, target) in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (overwrite == OverwritePolicy.Skip && await router.GetEntryAsync(target, cancellationToken).ConfigureAwait(false) is not null)
                {
                    SkippedFiles++;
                    done += Math.Max(0, source.Size);
                    progress.Report(new JobMetrics(done, plan.TotalBytes, ++files, plan.Files.Count, Loc.T("Pominięto: ") + source.Name));
                    continue;
                }

                await using (var input = await router.OpenReadAsync(source.Path, cancellationToken).ConfigureAwait(false))
                await using (var output = await router.OpenWriteAsync(target, overwrite == OverwritePolicy.Overwrite, cancellationToken).ConfigureAwait(false))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        done += read;
                        progress.Report(new JobMetrics(done, plan.TotalBytes, files, plan.Files.Count, source.Name));
                    }
                }

                progress.Report(new JobMetrics(done, plan.TotalBytes, ++files, plan.Files.Count, source.Name));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    internal static string Describe(IReadOnlyList<VfsEntry> entries) =>
        entries.Count == 1 ? Loc.T("„{0}”", entries[0].Name) : Loc.P(entries.Count, "{0} element|{0} elementy|{0} elementów");

    private sealed class TransferPlan
    {
        public List<string> Directories { get; } = [];

        public List<(VfsEntry Source, string Target)> Files { get; } = [];

        public long TotalBytes { get; set; }
    }
}

public sealed class VfsDeleteJob(VfsRouter router, IReadOnlyList<VfsEntry> targets) : IJob
{
    public string Title { get; } = $"Usuwanie {VfsTransferJob.Describe(targets)}";

    public async Task ExecuteAsync(IProgress<JobMetrics> progress, CancellationToken cancellationToken)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new JobMetrics(0, 0, i, targets.Count, targets[i].Name));
            await router.DeleteAsync(targets[i].Path, recursive: true, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(new JobMetrics(0, 0, targets.Count, targets.Count));
    }
}
