using Commander4K.Core.Theming;

namespace Commander4K.Core.Configuration;

/// <summary>Favourite server or share shown at the top of <c>net://</c>.</summary>
public sealed class ConnectionSetting
{
    public string Name { get; set; } = string.Empty;

    /// <summary>e.g. <c>\\nas\media</c> or <c>sftp://me@host/</c>.</summary>
    public string Path { get; set; } = string.Empty;
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

/// <summary>
/// Content of <c>config/settings.json</c>. Every user-facing property is also described in
/// <see cref="SettingsCatalog"/>, which is what the searchable settings window and the command palette show.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultUpdateRepository = "4kprojekt/Commander4K";

    /// <summary>Id of the active skin (built-in or from <c>config/skins</c>).</summary>
    public string Skin { get; set; } = BuiltInSkins.ModernDarkId;

    public string? LeftPath { get; set; }

    public string? RightPath { get; set; }

    public bool ShowHidden { get; set; } = true;

    public bool ConfirmDelete { get; set; } = true;

    /// <summary>Quick view opens Markdown files in side-by-side editing instead of the formatted view.</summary>
    public bool MarkdownEditByDefault { get; set; }

    /// <summary>Probe the local subnet for SMB/SSH/FTP hosts when opening <c>net://</c>.</summary>
    public bool LanDiscovery { get; set; } = true;

    public bool CheckForUpdates { get; set; } = true;

    public bool IncludePrereleases { get; set; }

    /// <summary>GitHub repository (<c>owner/name</c>) whose releases are checked for updates.</summary>
    public string UpdateRepository { get; set; } = DefaultUpdateRepository;

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>A release the user chose to skip; no reminder until a newer one appears.</summary>
    public string? SkippedVersion { get; set; }

    public List<ConnectionSetting> Connections { get; set; } = [];

    /// <summary>Docked palettes (quick actions, favourites, views, plugin palettes) are visible.</summary>
    public bool ShowPalettes { get; set; } = true;

    public List<FavoriteSetting> Favorites { get; set; } = [];

    /// <summary>Command ids on the quick-actions palette; <c>null</c> = the default set.</summary>
    public List<string>? QuickActions { get; set; }

    public List<ViewSetting> SavedViews { get; set; } = [];

    /// <summary>Palette id → dock (<c>hidden</c>, <c>bottom</c>, <c>left</c>, <c>right</c>, <c>between</c>); missing = the palette's default.</summary>
    public Dictionary<string, string> PaletteDocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
