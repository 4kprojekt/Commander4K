using System.IO.Enumeration;
using Commander4K.Core.Execution;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Local;

/// <summary>Allocation-light directory listing and watching shared by the local and SMB providers.</summary>
internal static class FileSystemListing
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorPasswordExpired = 1330;
    private const int ErrorAccountDisabled = 1331;
    private const int ErrorAccountLockedOut = 1909;

    private static readonly EnumerationOptions Options = new()
    {
        // The listing is non-recursive, so an inaccessible directory can only be the one requested: surface it
        // (it is how we detect that a share needs credentials) instead of returning an empty listing.
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    public static FileSystemEnumerable<VfsEntry> Enumerate(string directory) =>
        new(directory, static (ref FileSystemEntry entry) => ToEntry(ref entry), Options);

    /// <summary>
    /// For tree walks: a directory that cannot be opened (access denied, vanished) calls <paramref name="inaccessible"/>
    /// and yields nothing, without an exception.
    /// </summary>
    public static IEnumerable<VfsEntry> EnumerateForWalk(string directory, Action inaccessible)
    {
        using var enumerator = new WalkEnumerator(directory, inaccessible);
        while (enumerator.MoveNext())
        {
            yield return enumerator.Current;
        }
    }

    public static VfsEntry ToEntry(FileSystemInfo info)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        return new VfsEntry(
            info.Name,
            info.FullName,
            isDirectory ? VfsEntryKind.Directory : VfsEntryKind.File,
            isDirectory ? -1 : ((FileInfo)info).Length,
            info.LastWriteTimeUtc,
            ToFlags(info.Attributes));
    }

    /// <summary>True for errors a login dialog can fix (as opposed to the server being unreachable).</summary>
    public static bool IsAuthenticationError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,
        IOException io => (io.HResult & 0xFFFF) is ErrorAccessDenied or ErrorLogonFailure or ErrorAccountRestriction
            or ErrorPasswordExpired or ErrorAccountDisabled or ErrorAccountLockedOut,
        _ => false,
    };

    public static IDisposable Watch(string directoryPath, Action changed)
    {
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(150), changed);
        var watcher = new FileSystemWatcher(directoryPath)
        {
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                | NotifyFilters.LastWrite | NotifyFilters.Attributes,
        };

        void OnEvent(object? sender, EventArgs e) => debouncer.Trigger();
        watcher.Created += OnEvent;
        watcher.Deleted += OnEvent;
        watcher.Changed += OnEvent;
        watcher.Renamed += OnEvent;
        watcher.Error += OnEvent; // Buffer overflow: a full reload is the only safe answer.
        watcher.EnableRaisingEvents = true;

        return new WatchSubscription(watcher, debouncer);
    }

    private static VfsEntry ToEntry(ref FileSystemEntry entry)
    {
        var isDirectory = entry.IsDirectory;
        return new VfsEntry(
            entry.FileName.ToString(),
            entry.ToFullPath(),
            isDirectory ? VfsEntryKind.Directory : VfsEntryKind.File,
            isDirectory ? -1 : entry.Length,
            entry.LastWriteTimeUtc,
            ToFlags(entry.Attributes));
    }

    private static VfsEntryFlags ToFlags(FileAttributes attributes)
    {
        var flags = VfsEntryFlags.None;
        if ((attributes & FileAttributes.Hidden) != 0) flags |= VfsEntryFlags.Hidden;
        if ((attributes & FileAttributes.System) != 0) flags |= VfsEntryFlags.System;
        if ((attributes & FileAttributes.ReadOnly) != 0) flags |= VfsEntryFlags.ReadOnly;
        if ((attributes & FileAttributes.ReparsePoint) != 0) flags |= VfsEntryFlags.ReparsePoint;
        if ((attributes & FileAttributes.Offline) != 0) flags |= VfsEntryFlags.Offline;
        return flags;
    }

    /// <summary>
    /// <see cref="ContinueOnError"/> is .NET's hook for skipping a directory it cannot open; it runs from the base
    /// constructor, so the callback is a field initializer (assigned before the base constructor runs).
    /// </summary>
    private sealed class WalkEnumerator(string directory, Action inaccessible) : FileSystemEnumerator<VfsEntry>(directory, Options)
    {
        private readonly Action _inaccessible = inaccessible;

        protected override VfsEntry TransformEntry(ref FileSystemEntry entry) => ToEntry(ref entry);

        protected override bool ContinueOnError(int error)
        {
            _inaccessible();
            return true;
        }
    }

    private sealed class WatchSubscription(FileSystemWatcher watcher, Debouncer debouncer) : IDisposable
    {
        public void Dispose()
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            debouncer.Dispose();
        }
    }
}
