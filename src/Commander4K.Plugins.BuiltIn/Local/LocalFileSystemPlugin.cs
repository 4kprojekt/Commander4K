using System.Runtime.CompilerServices;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Local;

/// <summary>
/// Local disks. <c>C:\...</c> paths are served directly; <c>local://</c> is the virtual root listing the drives.
/// </summary>
public sealed class LocalFileSystemPlugin : IVfsProviderPlugin, IVfsChangeNotifier
{
    public const string RootPath = "local://";

    private const int CancellationCheckInterval = 1024;

    public string Id => "builtin.local";

    public string DisplayName => "Local disks";

    public IReadOnlyList<string> Schemes { get; } = [VfsPath.FileScheme, "local"];

    public VfsCapabilities Capabilities => VfsCapabilities.Read | VfsCapabilities.Write | VfsCapabilities.CreateDirectory
        | VfsCapabilities.Delete | VfsCapabilities.Move | VfsCapabilities.Watch;

    public async IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var local = ToLocalPath(path);
        if (local is null)
        {
            foreach (var drive in EnumerateDrives())
            {
                yield return drive;
            }

            yield break;
        }

        var count = 0;
        foreach (var entry in FileSystemListing.Enumerate(local))
        {
            if (++count % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            yield return entry;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        var local = ToLocalPath(path);
        if (local is null)
        {
            return ValueTask.FromResult<VfsEntry?>(new VfsEntry(DisplayName, RootPath, VfsEntryKind.Directory));
        }

        FileSystemInfo info = Directory.Exists(local) ? new DirectoryInfo(local) : new FileInfo(local);
        return ValueTask.FromResult<VfsEntry?>(info.Exists ? FileSystemListing.ToEntry(info) : null);
    }

    public string? GetParentPath(string path)
    {
        var local = ToLocalPath(path);
        if (local is null)
        {
            return null;
        }

        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(local)) ?? RootPath;
    }

    public string Combine(string directoryPath, string name)
    {
        var local = ToLocalPath(directoryPath);
        return local is null ? name : Path.Join(local, name);
    }

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new FileStream(RequireLocal(path), new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        }));

    public ValueTask<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new FileStream(RequireLocal(path), new FileStreamOptions
        {
            Mode = overwrite ? FileMode.Create : FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        }));

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RequireLocal(path));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var local = RequireLocal(path);
        if (Directory.Exists(local))
        {
            Directory.Delete(local, recursive);
        }
        else
        {
            File.Delete(local);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var source = RequireLocal(sourcePath);
        var destination = RequireLocal(destinationPath);
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite);
        }

        return ValueTask.CompletedTask;
    }

    public IDisposable Watch(string directoryPath, Action changed) =>
        FileSystemListing.Watch(RequireLocal(directoryPath), changed);

    /// <summary>Returns the Windows path, or <c>null</c> for the virtual drive list.</summary>
    public static string? ToLocalPath(string path)
    {
        var stripped = VfsPath.StripScheme(path);
        if (!ReferenceEquals(stripped, path))
        {
            // file:///C:/dir and local://C:\dir both reach here.
            stripped = stripped.TrimStart('/').Replace('/', '\\');
        }

        if (stripped.Length == 0)
        {
            return null;
        }

        // "C:" alone means the drive's current directory to Win32; we always mean its root.
        return stripped.Length == 2 && stripped[1] == ':' ? stripped + "\\" : stripped;
    }

    private static string RequireLocal(string path) =>
        ToLocalPath(path) ?? throw new NotSupportedException("The drive list is virtual.");

    private static IEnumerable<VfsEntry> EnumerateDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string? description;
            long size = -1;
            try
            {
                // IsReady on a disconnected mapped drive can stall; we are on a background producer, so that is fine.
                if (drive.IsReady)
                {
                    size = drive.TotalSize;
                    var label = string.IsNullOrEmpty(drive.VolumeLabel) ? DescribeType(drive.DriveType) : drive.VolumeLabel;
                    description = $"{label} — wolne {FormatBytes(drive.AvailableFreeSpace)}";
                }
                else
                {
                    description = DescribeType(drive.DriveType);
                }
            }
            catch (IOException)
            {
                description = DescribeType(drive.DriveType);
            }
            catch (UnauthorizedAccessException)
            {
                description = DescribeType(drive.DriveType);
            }

            yield return new VfsEntry(drive.Name.TrimEnd('\\'), drive.Name, VfsEntryKind.Drive, size, Description: description);
        }
    }

    private static string DescribeType(DriveType type) => type switch
    {
        DriveType.Fixed => "Dysk lokalny",
        DriveType.Removable => "Dysk wymienny",
        DriveType.Network => "Dysk sieciowy",
        DriveType.CDRom => "Napęd optyczny",
        DriveType.Ram => "RAM-dysk",
        _ => "Dysk",
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
