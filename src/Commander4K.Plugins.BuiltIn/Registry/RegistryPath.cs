using Commander4K.PluginSdk;
using Microsoft.Win32;

namespace Commander4K.Plugins.BuiltIn.Registry;

/// <summary>
/// <c>reg://HKCU\Software\Vendor</c> addresses a key; a value is the key path, a double backslash and the value name
/// (<c>reg://HKCU\Software\Vendor\\Version</c>). Key names can never be empty, so <c>\\</c> cannot occur inside a key
/// path, while value names may freely contain backslashes.
/// </summary>
internal readonly record struct RegistryPath(string? HiveName, string SubKey, string? ValueName)
{
    public const string Scheme = "reg";
    public const string RootPath = "reg://";
    public const string ValueSeparator = @"\\";

    public static readonly IReadOnlyList<(string Name, RegistryKey Key)> Hives =
    [
        ("HKCU", Microsoft.Win32.Registry.CurrentUser),
        ("HKLM", Microsoft.Win32.Registry.LocalMachine),
        ("HKCR", Microsoft.Win32.Registry.ClassesRoot),
        ("HKU", Microsoft.Win32.Registry.Users),
        ("HKCC", Microsoft.Win32.Registry.CurrentConfig),
    ];

    public bool IsRoot => HiveName is null;

    public bool IsValue => ValueName is not null;

    public RegistryKey Hive => ResolveHive(HiveName!);

    public string KeyPath => SubKey.Length == 0 ? RootPath + HiveName : $@"{RootPath}{HiveName}\{SubKey}";

    public static RegistryPath Parse(string path)
    {
        var rest = VfsPath.StripScheme(path).Replace('/', '\\');

        string? valueName = null;
        var valueSeparator = rest.IndexOf(ValueSeparator, StringComparison.Ordinal);
        if (valueSeparator >= 0)
        {
            valueName = rest[(valueSeparator + ValueSeparator.Length)..];
            rest = rest[..valueSeparator];
        }

        rest = rest.Trim('\\');
        if (rest.Length == 0)
        {
            return new RegistryPath(null, string.Empty, null);
        }

        var hiveEnd = rest.IndexOf('\\');
        var hive = NormalizeHive(hiveEnd < 0 ? rest : rest[..hiveEnd]);
        var subKey = hiveEnd < 0 ? string.Empty : rest[(hiveEnd + 1)..];
        return new RegistryPath(hive, subKey, valueName);
    }

    public static string ForValue(string keyPath, string valueName) => keyPath + ValueSeparator + valueName;

    public static string ForSubKey(string keyPath, string name) =>
        keyPath.EndsWith("://", StringComparison.Ordinal) ? keyPath + name : keyPath + @"\" + name;

    public static RegistryKey ResolveHive(string name)
    {
        foreach (var (hiveName, key) in Hives)
        {
            if (string.Equals(hiveName, name, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        throw new DirectoryNotFoundException($"Unknown registry hive '{name}'.");
    }

    private static string NormalizeHive(string name) => name.ToUpperInvariant() switch
    {
        "HKEY_CURRENT_USER" => "HKCU",
        "HKEY_LOCAL_MACHINE" => "HKLM",
        "HKEY_CLASSES_ROOT" => "HKCR",
        "HKEY_USERS" => "HKU",
        "HKEY_CURRENT_CONFIG" => "HKCC",
        var shortName => shortName,
    };
}
