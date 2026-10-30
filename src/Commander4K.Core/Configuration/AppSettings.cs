using Commander4K.Core.Theming;

namespace Commander4K.Core.Configuration;

/// <summary>Content of <c>config/settings.json</c>.</summary>
public sealed class AppSettings
{
    /// <summary>Id of the active skin (built-in or from <c>config/skins</c>).</summary>
    public string Skin { get; set; } = BuiltInSkins.ModernDarkId;
}
