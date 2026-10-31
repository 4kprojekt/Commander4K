using Commander4K.Core.Theming;

namespace Commander4K.Core.Configuration;

/// <summary>Favourite server or share shown at the top of <c>net://</c>, "This PC" and the drive menu.</summary>
public sealed class ConnectionSetting
{
    public string Name { get; set; } = string.Empty;

    /// <summary>e.g. <c>\\nas\media</c> or <c>sftp://me@host:2222/home/me</c> (the user is part of the address).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary><c>password</c>, <c>key</c> (private key file, optional passphrase) or <c>null</c> (ask / default keys).</summary>
    public string? Auth { get; set; }

    public string? UserName { get; set; }

    /// <summary>Private key file for SSH (<c>key</c> authentication).</summary>
    public string? KeyFile { get; set; }

    /// <summary>The password or the key's passphrase, encrypted with DPAPI (<see cref="Security.SecretProtector"/>).</summary>
    public string? Secret { get; set; }
}

public sealed class FavoriteSetting
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

/// <summary>A saved layout: both panels' locations and modes (<c>null</c> = files, <c>preview</c>, <c>panel:&lt;id&gt;</c>).</summary>
public sealed class ViewSetting
{
    public string Name { get; set; } = string.Empty;

    public string? LeftPath { get; set; }

    public string? RightPath { get; set; }

    public string? LeftMode { get; set; }

    public string? RightMode { get; set; }
}

/// <summary>Main window state on exit: bounds of the normal (restored) window, plus whether it was maximized.</summary>
public sealed class WindowPlacement
{
    public bool Maximized { get; set; }

    /// <summary>Screen position of the restored window; <c>null</c> = centred (it was never shown restored).</summary>
    public int? X { get; set; }

    public int? Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

/// <summary>
/// Content of <c>config/settings.json</c>. Every user-facing property is also described in
/// <see cref="SettingsCatalog"/>, which is what the searchable settings window and the command palette show.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Main window position, size and state, saved on exit; <c>null</c> until the first exit, so the very first start
    /// opens maximized. Read before the window exists (<see cref="ReadWindowPlacement"/>) to avoid a visible jump.
    /// </summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>The saved window placement straight from settings.json (the host loads later); <c>null</c> on first run.</summary>
    public static WindowPlacement? ReadWindowPlacement(string settingsPath) => ReadStartupSettings(settingsPath)?.Window;

    /// <summary>settings.json read before any window exists (placement, splash, skin id); <c>null</c> when missing or unreadable.</summary>
    public static AppSettings? ReadStartupSettings(string settingsPath)
    {
        try
        {
            return File.Exists(settingsPath)
                ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(settingsPath), ConfigJsonContext.Default.AppSettings)
                : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public const string DefaultUpdateRepository = "4kprojekt/Commander4K";

    /// <summary>UI language code (<c>pl</c>, <c>en</c>, or a file in <c>config/i18n</c>); <c>auto</c> follows Windows.</summary>
    public string Language { get; set; } = Localization.Loc.Auto;

    /// <summary>List density: <c>compact</c> (12 px, 18 px rows), <c>default</c> (the skin's), <c>comfortable</c> (14 px, 24 px rows).</summary>
    public string Density { get; set; } = "default";

    /// <summary>Id of the active skin (built-in or from <c>config/skins</c>).</summary>
    public string Skin { get; set; } = BuiltInSkins.ModernDarkId;

    public string? LeftPath { get; set; }

    public string? RightPath { get; set; }

    public bool ShowHidden { get; set; } = true;

    public bool ConfirmDelete { get; set; } = true;

    /// <summary>Quick view opens Markdown files in side-by-side editing instead of the formatted view.</summary>
    public bool MarkdownEditByDefault { get; set; }

    /// <summary>Honour <c>c4k.dir</c> files: a folder's own preview and what entering it does.</summary>
    public bool FolderConfigs { get; set; } = true;

    /// <summary>Where the official plugin catalog (<c>plugins-catalog/catalog.json</c>) is downloaded from at start-up.</summary>
    public string PluginCatalogUrl { get; set; } = DefaultPluginCatalogUrl;

    public const string DefaultPluginCatalogUrl = Plugins.PluginCatalogService.DefaultUrl;

    /// <summary>Probe the local subnet for SMB/SSH/FTP hosts when opening <c>net://</c>.</summary>
    public bool LanDiscovery { get; set; } = true;

    /// <summary><see cref="Web.SearchEngines"/> id used by the browser's address bar.</summary>
    public string SearchEngine { get; set; } = Web.SearchEngines.DefaultSearch;

    /// <summary>AI assistant asked by "find a solution" actions (plugin manager, <c>cm_AskAi</c>).</summary>
    public string AssistantEngine { get; set; } = Web.SearchEngines.DefaultAssistant;

    /// <summary>Where the live AI provider list (<c>ai-providers.json</c>) is downloaded from at start-up.</summary>
    public string AiCatalogUrl { get; set; } = DefaultAiCatalogUrl;

    public const string DefaultAiCatalogUrl = "https://raw.githubusercontent.com/4kprojekt/Commander4K/main/src/Commander4K.Core/Ai/ai-providers.json";

    /// <summary>"Ask AI" answers in the app through the signed-in providers (with rotation) instead of opening the web assistant.</summary>
    public bool AiInApp { get; set; } = true;

    public bool CheckForUpdates { get; set; } = true;

    public bool IncludePrereleases { get; set; }

    /// <summary>GitHub repository (<c>owner/name</c>) whose releases are checked for updates.</summary>
    public string UpdateRepository { get; set; } = DefaultUpdateRepository;

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>A release the user chose to skip; no reminder until a newer one appears.</summary>
    public string? SkippedVersion { get; set; }

    public List<ConnectionSetting> Connections { get; set; } = [];

    /// <summary>Plugin columns shown in the file lists, as <c>pluginId/columnId</c>, in display order.</summary>
    public List<string> Columns { get; set; } = [];

    /// <summary>Docked palettes (quick actions, favourites, views, plugin palettes) are visible; off by default (Ctrl+B).</summary>
    public bool ShowPalettes { get; set; }

    /// <summary>Palette id → width (side docks) or height (bottom) dragged by the user.</summary>
    public Dictionary<string, double> PaletteSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Right click opens the Windows (Explorer) context menu; off = right click marks, as in Total Commander.</summary>
    public bool ShellContextMenu { get; set; } = true;

    /// <summary>Animated splash screen while the app starts (also under "About").</summary>
    public bool SplashScreen { get; set; } = true;

    public List<FavoriteSetting> Favorites { get; set; } = [];

    /// <summary>Command ids on the quick-actions palette; <c>null</c> = the default set.</summary>
    public List<string>? QuickActions { get; set; }

    public List<ViewSetting> SavedViews { get; set; } = [];

    /// <summary>Palette id → dock (<c>hidden</c>, <c>bottom</c>, <c>left</c>, <c>right</c>, <c>between</c>); missing = the palette's default.</summary>
    public Dictionary<string, string> PaletteDocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Local MCP server for Claude Code: allowed folders, tool modes, port and token.</summary>
    public Mcp.McpSettings Mcp { get; set => field = value ?? new(); } = new();
}
