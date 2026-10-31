using Commander4K.Core.Mcp;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>
/// Temporary read permission for exactly the files and folders the user sent to a plugin: a file grant covers that
/// path only, a folder grant covers the folder and everything below it. Matching reuses <see cref="McpPathPolicy"/>
/// (text-only normalization, separators, case per scheme, <c>..</c> resolved, segment boundaries). Grants stack: a path
/// stays covered while any grant that covers it is held. Thread-safe.
/// </summary>
public sealed class ReadGrants
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _active = [];

    /// <summary>
    /// Grants read access; paths that are not recognised absolute VFS paths are ignored. Disposing releases this grant
    /// and cancels its <see cref="TryCover"/> token, so work started under it (a streamed upload) stops with it.
    /// </summary>
    public IDisposable Grant(IReadOnlyList<TransferItem> items) => Grant(items, out _, out _);

    /// <inheritdoc cref="Grant(IReadOnlyList{TransferItem})"/>
    /// <param name="granted">The items actually granted.</param>
    /// <param name="ignored">Items whose path was not a recognised absolute VFS path (not granted).</param>
    public IDisposable Grant(IReadOnlyList<TransferItem> items, out IReadOnlyList<TransferItem> granted, out IReadOnlyList<TransferItem> ignored)
    {
        var keys = new List<(bool Folder, string Path)>();
        var accepted = new List<TransferItem>();
        var refused = new List<TransferItem>();
        foreach (var item in items)
        {
            if (!McpPathPolicy.IsInside(item.Path, item.Path))
            {
                refused.Add(item); // Not a recognised absolute path: never grant it.
                continue;
            }

            accepted.Add(item);
            keys.Add((item.Kind == TransferItemKind.Folder, McpPathPolicy.Normalize(item.Path)));
        }

        granted = accepted;
        ignored = refused;

        var entry = new Entry(keys);
        lock (_gate)
        {
            _active.Add(entry);
        }

        return new Releaser(this, entry);
    }

    /// <summary>True when <paramref name="path"/> is a granted file or lies inside a granted folder.</summary>
    public bool Covers(string path) => TryCover(path, out _);

    /// <summary>Like <see cref="Covers"/>; <paramref name="ended"/> is cancelled when the covering grant is released.</summary>
    public bool TryCover(string path, out CancellationToken ended)
    {
        Entry[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _active];
        }

        foreach (var entry in snapshot)
        {
            foreach (var (folder, granted) in entry.Keys)
            {
                if (McpPathPolicy.IsInside(path, granted) && (folder || McpPathPolicy.IsInside(granted, path)))
                {
                    ended = entry.Ended.Token;
                    return true;
                }
            }
        }

        ended = CancellationToken.None;
        return false;
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            _active.Remove(entry);
        }

        // Not disposed: a request may still be linking to the token; cancelling is all that is needed.
        entry.Ended.Cancel();
    }

    private sealed class Entry(List<(bool Folder, string Path)> keys)
    {
        public List<(bool Folder, string Path)> Keys { get; } = keys;

        public CancellationTokenSource Ended { get; } = new();
    }

    private sealed class Releaser(ReadGrants owner, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(entry);
            }
        }
    }
}
