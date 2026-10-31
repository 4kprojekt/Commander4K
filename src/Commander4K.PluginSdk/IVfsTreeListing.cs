namespace Commander4K.PluginSdk;

/// <summary>
/// Optional provider capability for tree walks (folder sizes): lists a container without throwing when it cannot be
/// opened. A walk of <c>C:\Windows</c> meets thousands of folders without access; as exceptions, each one suspends
/// the whole process (UI thread included) while an attached debugger looks at it.
/// </summary>
public interface IVfsTreeListing
{
    /// <summary>
    /// Like <see cref="IVfsProviderPlugin.EnumerateAsync"/>, but a container that cannot be read calls
    /// <paramref name="inaccessible"/> and ends the listing instead of throwing.
    /// </summary>
    IAsyncEnumerable<VfsEntry> EnumerateForWalkAsync(string path, Action inaccessible, CancellationToken cancellationToken);
}
