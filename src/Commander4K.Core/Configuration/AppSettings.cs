using Commander4K.Core.Theming;

namespace Commander4K.Core.Configuration;

/// <summary>Favourite server or share shown at the top of <c>net://</c>.</summary>
public sealed class ConnectionSetting
{
    public string Name { get; set; } = string.Empty;

    /// <summary>e.g. <c>\\nas\media</c> or <c>sftp://me@host/</c>.</summary>
    public string Path { get; set; } = string.Empty;
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
}
