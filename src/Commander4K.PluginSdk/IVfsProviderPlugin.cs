namespace Commander4K.PluginSdk;

[Flags]
public enum VfsCapabilities
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    CreateDirectory = 1 << 2,
    Delete = 1 << 3,
    Move = 1 << 4,
    Watch = 1 << 5,
}

/// <summary>
/// File-system provider (Total Commander WFX equivalent) for one or more schemes such as <c>file</c>, <c>net</c>,
/// <c>smb</c>, <c>sftp</c>, <c>ftp</c> or <c>reg</c>.
/// </summary>
/// <remarks>
/// <see cref="EnumerateAsync"/> is consumed on a background producer and may block (slow SMB calls); the host wraps it
/// so that a hung enumeration never reaches the UI thread. Implementations should still honour the token wherever
/// the underlying API allows it.
/// </remarks>
public interface IVfsProviderPlugin : IPlugin
{
    /// <summary>Lower-case schemes handled by this provider, matched against <see cref="VfsPath.GetScheme"/>.</summary>
    IReadOnlyList<string> Schemes { get; }

    VfsCapabilities Capabilities { get; }

    /// <summary>Streams the children of a container. The first entry should arrive as soon as it is known.</summary>
    IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, CancellationToken cancellationToken);

    ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Returns the parent container, or <c>null</c> at the root of this provider.</summary>
    string? GetParentPath(string path);

    string Combine(string directoryPath, string name);

    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{DisplayName} does not support reading.");

    ValueTask<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{DisplayName} does not support writing.");

    ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{DisplayName} does not support creating directories.");

    ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{DisplayName} does not support deleting.");

    ValueTask MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{DisplayName} does not support moving.");
}
