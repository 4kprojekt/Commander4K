using Commander4K.Core.Localization;
using Commander4K.PluginSdk;
using Microsoft.Win32;

namespace Commander4K.Plugins.BuiltIn.Local;

/// <summary>
/// The user's and the system's well-known folders for "This PC" (<c>local://</c>): libraries (desktop, documents,
/// downloads…, including OneDrive redirection), the profile and the system locations. Only folders that exist.
/// </summary>
public static class KnownFolders
{
    /// <summary>Downloads has no <see cref="Environment.SpecialFolder"/>; Explorer keeps its (possibly moved) path here.</summary>
    private const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
    private const string DownloadsId = "{374DE290-123F-4565-9164-39C4925E467B}";

    public static IEnumerable<VfsEntry> Enumerate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path, description) in Candidates())
        {
            if (string.IsNullOrEmpty(path) || !seen.Add(Path.TrimEndingDirectorySeparator(path)))
            {
                continue;
            }

            bool exists;
            try
            {
                exists = Directory.Exists(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                exists = false;
            }

            if (exists)
            {
                yield return new VfsEntry(name, path, VfsEntryKind.Directory, Description: description);
            }
        }
    }

    private static IEnumerable<(string Name, string? Path, string Description)> Candidates()
    {
        var library = Loc.T("Folder użytkownika");
        var system = Loc.T("Folder systemowy");
        yield return (Loc.T("Pulpit"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), library);
        yield return (Loc.T("Dokumenty"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), library);
        yield return (Loc.T("Pobrane"), Downloads(), library);
        yield return (Loc.T("Obrazy"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), library);
        yield return (Loc.T("Muzyka"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), library);
        yield return (Loc.T("Wideo"), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), library);
        yield return (Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Loc.T("Profil użytkownika"));
        yield return ("OneDrive", Environment.GetEnvironmentVariable("OneDrive"), Loc.T("Chmura"));
        yield return ("Windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows), system);
        yield return ("Program Files", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), system);
        yield return ("Program Files (x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), system);
        yield return ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), system);
        yield return ("AppData (Roaming)", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), system);
        yield return ("AppData (Local)", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), system);
        yield return (Loc.T("Pliki tymczasowe"), Path.GetTempPath(), system);
        yield return (Loc.T("Autostart"), Environment.GetFolderPath(Environment.SpecialFolder.Startup), system);
    }

    private static string? Downloads()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ShellFoldersKey);
            if (key?.GetValue(DownloadsId, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw)
            {
                return Environment.ExpandEnvironmentVariables(raw);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
