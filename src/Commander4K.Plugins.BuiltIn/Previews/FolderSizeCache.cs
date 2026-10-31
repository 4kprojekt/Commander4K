using System.Collections.Concurrent;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Previews;

/// <summary>
/// Recursive folder sizes for cards. A whole tree (node_modules, a share) can take far longer than a card may wait, so
/// the size is computed once in the background and kept per path and modification time; a refresh reuses it until the
/// folder changes. <see cref="Completed"/> tells the dashboard to draw the card again.
/// </summary>
public sealed class FolderSizeCache
{
    private readonly ConcurrentDictionary<string, Entry> _sizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A size finished computing (raised on a pool thread with the folder's path).</summary>
    public event Action<string>? Completed;

    /// <summary>
    /// The size of <paramref name="folder"/> as of its <see cref="VfsEntry.LastWriteTime"/>, or <c>null</c> while it is
    /// being computed (the computation starts on the first call). A size that could not be computed has negative bytes.
    /// </summary>
    public DirectorySizeInfo? GetOrStart(IPluginHost host, VfsEntry folder)
    {
        var current = _sizes.AddOrUpdate(folder.Path,
            _ => new Entry(folder.LastWriteTime),
            (_, existing) => existing.Stamp == folder.LastWriteTime ? existing : new Entry(folder.LastWriteTime));
        if (current.TryStart())
        {
            _ = ComputeAsync(host, folder.Path, current);
        }

        return current.Result;
    }

    private async Task ComputeAsync(IPluginHost host, string path, Entry entry)
    {
        DirectorySizeInfo size;
        try
        {
            size = await host.CalculateSizeAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or VfsAuthenticationRequiredException
            or VfsProviderNotFoundException or NotSupportedException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            // PluginPermissionException is an UnauthorizedAccessException. Whatever went wrong, the card stops counting.
            size = new DirectorySizeInfo(-1, 0, 0, 1);
        }

        entry.Result = size;
        if (_sizes.TryGetValue(path, out var latest) && ReferenceEquals(latest, entry))
        {
            Completed?.Invoke(path);
        }
    }

    private sealed class Entry(DateTimeOffset stamp)
    {
        private int _started;
        private volatile StrongBox? _result;

        public DateTimeOffset Stamp { get; } = stamp;

        /// <summary>Boxed and volatile: written on a pool thread, read on the card's, never torn.</summary>
        public DirectorySizeInfo? Result
        {
            get => _result?.Value;
            set => _result = value is { } size ? new StrongBox(size) : null;
        }

        public bool TryStart() => Interlocked.Exchange(ref _started, 1) == 0;
    }

    private sealed record StrongBox(DirectorySizeInfo Value);
}
