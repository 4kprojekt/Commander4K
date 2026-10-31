using Commander4K.Core.Mcp;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>
/// Temporary read permission for exactly the files and folders the user sent to a plugin: a file grant covers that
/// path only, a folder grant covers the folder and everything below it. Matching reuses <see cref="McpPathPolicy"/>
/// (text-only normalization, separators, case per scheme, <c>..</c> resolved, segment boundaries). Grants stack: each
/// entry has a counter and disappears when its last holder is disposed. Thread-safe.
/// </summary>
public sealed class ReadGrants
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(bool Folder, string Path), int> _counts = [];

    /// <summary>Grants read access; paths that are not recognised absolute VFS paths are ignored. Disposing releases once.</summary>
    public IDisposable Grant(IReadOnlyList<TransferItem> items)
    {
        var keys = new List<(bool Folder, string Path)>();
        foreach (var item in items)
        {
            if (!McpPathPolicy.IsInside(item.Path, item.Path))
            {
                continue; // Not a recognised absolute path: never grant it.
            }

            keys.Add((item.Kind == TransferItemKind.Folder, McpPathPolicy.Normalize(item.Path)));
        }

        lock (_gate)
        {
            foreach (var key in keys)
            {
                _counts[key] = _counts.GetValueOrDefault(key) + 1;
            }
        }

        return new Releaser(this, keys);
    }

    /// <summary>True when <paramref name="path"/> is a granted file or lies inside a granted folder.</summary>
    public bool Covers(string path)
    {
        (bool Folder, string Path)[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _counts.Keys];
        }

        foreach (var (folder, granted) in snapshot)
        {
            if (McpPathPolicy.IsInside(path, granted) && (folder || McpPathPolicy.IsInside(granted, path)))
            {
                return true;
            }
        }

        return false;
    }

    private void Release(List<(bool Folder, string Path)> keys)
    {
        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (_counts.TryGetValue(key, out var count) && count > 1)
                {
                    _counts[key] = count - 1;
                }
                else
                {
                    _counts.Remove(key);
                }
            }
        }
    }

    private sealed class Releaser(ReadGrants owner, List<(bool Folder, string Path)> keys) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(keys);
            }
        }
    }
}
