namespace Commander4K.PluginSdk;

public enum VfsEntryKind : byte
{
    File,
    Directory,
    ParentLink,
    Drive,
    Host,
    Share,
    /// <summary>Virtual action row, e.g. "+ Add network connection".</summary>
    Action,
    RegistryKey,
    RegistryValue,
    Archive,
    /// <summary>A discovered service endpoint that is displayed for information but is not opened as a file.</summary>
    Service,
}

[Flags]
public enum VfsEntryFlags : ushort
{
    None = 0,
    Hidden = 1 << 0,
    System = 1 << 1,
    ReadOnly = 1 << 2,
    /// <summary>Symlink or junction; recursive walkers must not follow it.</summary>
    ReparsePoint = 1 << 3,
    Offline = 1 << 4,
    /// <summary>Served from the SQLite cache rather than the live source.</summary>
    Cached = 1 << 5,
}

/// <param name="Size">Size in bytes, or -1 when unknown (directories, hosts).</param>
public readonly record struct VfsEntry(
    string Name,
    string Path,
    VfsEntryKind Kind,
    long Size = -1,
    DateTimeOffset LastWriteTime = default,
    VfsEntryFlags Flags = VfsEntryFlags.None,
    string? Description = null)
{
    public bool IsContainer => Kind is VfsEntryKind.Directory or VfsEntryKind.ParentLink or VfsEntryKind.Drive
        or VfsEntryKind.Host or VfsEntryKind.Share or VfsEntryKind.RegistryKey or VfsEntryKind.Archive;
}
