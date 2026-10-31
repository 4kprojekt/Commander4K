using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>
/// Process list and machine facts served to plugins (<c>process.list</c>, <c>system.info</c>). Blocking; call off the
/// UI thread. Core has no native interop, so parent PIDs and image paths of protected processes come from optional
/// lookups the app plugs in (Plugins.BuiltIn's Toolhelp snapshot).
/// </summary>
public static class SystemSnapshots
{
    public static IReadOnlyList<ProcessSnapshot> Processes(Func<Dictionary<int, int>>? parentIds = null, Func<int, string?>? imagePath = null)
    {
        var parents = parentIds?.Invoke() ?? [];
        var list = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                long workingSet;
                string name;
                try
                {
                    workingSet = process.WorkingSet64;
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue; // Exited meanwhile.
                }

                DateTimeOffset? started = null;
                try
                {
                    started = process.StartTime;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected and system processes do not expose their start time to a normal user.
                }

                var path = imagePath?.Invoke(process.Id);
                list.Add(new ProcessSnapshot(process.Id, parents.TryGetValue(process.Id, out var parent) ? parent : null, name, path, workingSet, started));
            }
        }

        return [.. list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static SystemSnapshot System()
    {
        var drives = DriveInfo.GetDrives().Select(drive =>
        {
            try
            {
                return drive.IsReady
                    ? new DriveSnapshot(drive.Name, drive.DriveType.ToString(), drive.DriveFormat, drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace)
                    : new DriveSnapshot(drive.Name, drive.DriveType.ToString(), null, null, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new DriveSnapshot(drive.Name, drive.DriveType.ToString(), null, null, null, null);
            }
        }).ToList();
        return new SystemSnapshot(
            RuntimeInformation.OSDescription,
            Environment.OSVersion.Version.ToString(),
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.MachineName,
            Environment.UserName,
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            drives);
    }
}
