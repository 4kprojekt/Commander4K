using System.Runtime.CompilerServices;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Tests;

/// <summary>In-memory provider for the <c>mem://</c> scheme; <see cref="HangOn"/> simulates a dead SMB share.</summary>
internal sealed class FakeVfsProvider : IVfsProviderPlugin
{
    private readonly Dictionary<string, List<VfsEntry>> _tree = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "test.mem";

    public string DisplayName => "Memory";

    public IReadOnlyList<string> Schemes { get; } = ["mem"];

    public VfsCapabilities Capabilities => VfsCapabilities.Read;

    /// <summary>Paths whose enumeration blocks the thread synchronously, ignoring cancellation.</summary>
    public HashSet<string> HangOn { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ManualResetEventSlim Release { get; } = new(false);

    public FakeVfsProvider AddDirectory(string path) => Add(path, VfsEntryKind.Directory, -1);

    public FakeVfsProvider AddFile(string path, long size) => Add(path, VfsEntryKind.File, size);

    public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (HangOn.Contains(path))
        {
            Release.Wait(); // Deliberately ignores the token, like a stuck FindFirstFile on UNC.
        }

        await Task.Yield();
        if (!_tree.TryGetValue(path, out var children))
        {
            throw new DirectoryNotFoundException(path);
        }

        foreach (var child in children)
        {
            yield return child;
        }
    }

    public ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult<VfsEntry?>(null);

    public string? GetParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= "mem://".Length ? null : path[..index];
    }

    public string Combine(string directoryPath, string name) => directoryPath.TrimEnd('/') + "/" + name;

    private FakeVfsProvider Add(string path, VfsEntryKind kind, long size)
    {
        var parent = GetParentPath(path) ?? "mem://";
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (!_tree.TryGetValue(parent, out var list))
        {
            _tree[parent] = list = [];
        }

        list.Add(new VfsEntry(name, path, kind, size));
        if (kind == VfsEntryKind.Directory)
        {
            _tree.TryAdd(path, []);
        }

        return this;
    }
}
