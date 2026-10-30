using Commander4K.Core.Theming;

namespace Commander4K.Core.Configuration;

/// <summary>Favourite server or share shown at the top of <c>net://</c>.</summary>
public sealed class ConnectionSetting
{
    public string Name { get; set; } = string.Empty;

    /// <summary>e.g. <c>\\nas\media</c> or <c>sftp://me@host/</c>.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Content of <c>config/settings.json</c>.</summary>
public sealed class AppSettings
{
    /// <summary>Id of the active skin (built-in or from <c>config/skins</c>).</summary>
    public string Skin { get; set; } = BuiltInSkins.ModernDarkId;

    public string? LeftPath { get; set; }

    public string? RightPath { get; set; }

    public bool ShowHidden { get; set; } = true;

    public List<ConnectionSetting> Connections { get; set; } = [];
}
