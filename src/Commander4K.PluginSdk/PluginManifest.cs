namespace Commander4K.PluginSdk;

/// <summary>Capabilities a plugin must declare in its manifest; the host refuses API calls outside them.</summary>
public static class PluginPermissions
{
    /// <summary>List folders and read files through the host (any VFS: disks, shares, SFTP, registry).</summary>
    public const string FileRead = "fs.read";

    /// <summary>Create, write and delete files through the host.</summary>
    public const string FileWrite = "fs.write";

    /// <summary>Enumerate processes and their details (<c>process.list</c>; built-in panels read them directly).</summary>
    public const string ProcessRead = "process.read";

    /// <summary>Terminate processes through the host.</summary>
    public const string ProcessControl = "process.kill";

    /// <summary>Run programs and scripts through the host (output captured, optionally elevated after confirmation).</summary>
    public const string ShellRun = "shell.run";

    /// <summary>HTTP requests through the host.</summary>
    public const string Network = "net";

    /// <summary>Read OS, machine, memory and drive information (<c>system.info</c>; built-in panels read WMI directly).</summary>
    public const string SystemInfo = "system.info";

    /// <summary>IMAP/SMTP mail through the host (<see cref="IMailHost"/>); the plugin never sees the credentials.</summary>
    public const string Mail = "mail";

    /// <summary>Periodic work while the app runs, without the plugin's panel being open (<c>contributes.background</c>).</summary>
    public const string Background = "background";

    public static IReadOnlyList<string> All { get; } =
        [FileRead, FileWrite, ProcessRead, ProcessControl, ShellRun, Network, SystemInfo, Mail, Background];

    public static string Describe(string permission) => permission switch
    {
        FileRead => "odczyt plików i folderów",
        FileWrite => "zapis i usuwanie plików",
        ProcessRead => "odczyt listy procesów",
        ProcessControl => "zamykanie procesów",
        ShellRun => "uruchamianie programów i skryptów",
        Network => "dostęp do sieci (HTTP)",
        SystemInfo => "odczyt informacji o systemie i sprzęcie",
        Mail => "poczta (IMAP/SMTP) przez aplikację",
        Background => "praca w tle",
        _ => permission,
    };
}

/// <param name="Type"><c>dotnet</c>, <c>node</c>, <c>python</c>, <c>powershell</c> or <c>exe</c>; <c>builtin</c> for plugins compiled into the app.</param>
/// <param name="Entry">Script, assembly or executable relative to the plugin folder.</param>
/// <param name="Version">Required runtime version range, e.g. <c>&gt;=3.10</c> or <c>&gt;=20 &lt;25</c>.</param>
public sealed record PluginRuntime(string Type, string? Entry = null, string? Version = null);

public sealed record CommandContribution(string Id, string Title, string Category, string? Description = null, string? Shortcut = null);

public sealed record PanelActionContribution(string Id, string Title, string? Shortcut = null, bool Confirm = false, bool AppliesToRows = true);

public sealed record PanelContribution(
    string Id,
    string Title,
    string? Description = null,
    bool RequiresArgument = false,
    int? RefreshSeconds = null,
    IReadOnlyList<PanelActionContribution>? Actions = null);

/// <param name="Extensions">File extensions handled, with the dot (".csv"); <c>"/"</c> means folders.</param>
/// <param name="Card">The plugin answers <c>preview.card</c>, so dashboards can show this preview as a live card.</param>
/// <param name="Enter">Enter on a matching file opens the pinned preview; Shift+Enter keeps the shell open.</param>
public sealed record PreviewContribution(string Id, string Title, IReadOnlyList<string> Extensions, int Priority = 10, bool Card = false, bool Enter = false);

/// <param name="Interval">How often the host calls the plugin in the background (<c>"1m"</c>, <c>"5m"</c>…); clamped to at least one minute.</param>
public sealed record BackgroundContribution(string Interval);

public sealed record PaletteActionContribution(string Id, string Title, string? Icon = null, bool ForItem = false);

/// <param name="Dock">Default position: <c>bottom</c>, <c>left</c>, <c>right</c>, <c>between</c> or <c>hidden</c>.</param>
public sealed record PaletteContribution(string Id, string Title, string Dock = "bottom", IReadOnlyList<PaletteActionContribution>? Actions = null);

/// <summary>What a plugin adds to the application. In-process plugins describe it by implementing interfaces instead.</summary>
public sealed record PluginContributions(
    IReadOnlyList<CommandContribution>? Commands = null,
    IReadOnlyList<PanelContribution>? Panels = null,
    IReadOnlyList<PreviewContribution>? Previews = null,
    IReadOnlyList<PaletteContribution>? Palettes = null,
    BackgroundContribution? Background = null);

/// <summary>
/// Identity and declared capabilities of a plugin: <c>plugin.json</c> for external plugins, a property for built-in
/// ones. The host shows it to the user, enforces <see cref="Permissions"/> on every API call and writes the plugin id
/// into the audit log.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    IReadOnlyList<string> Permissions)
{
    public PluginRuntime Runtime { get; init; } = new("builtin");

    public PluginContributions? Contributes { get; init; }

    /// <summary>Minimum protocol version the plugin speaks (external plugins).</summary>
    public int Protocol { get; init; } = 1;

    public bool IsBuiltIn => string.Equals(Runtime.Type, "builtin", StringComparison.OrdinalIgnoreCase);

    public bool Allows(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}
