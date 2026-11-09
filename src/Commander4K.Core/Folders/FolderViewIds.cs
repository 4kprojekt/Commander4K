namespace Commander4K.Core.Folders;

/// <summary>Views built into the app that a <c>.c4k</c> item can name with <c>"view"</c>.</summary>
public static class FolderViewIds
{
    /// <summary>The plain file list (or, as a preview, folder sizes).</summary>
    public const string Files = "files";

    /// <summary>Image thumbnails of the folder.</summary>
    public const string Gallery = "gallery";

    /// <summary>Card only (not a view): a sub-dashboard, the <c>.c4k</c> items of the target folder as a small summary.</summary>
    public const string Dashboard = "dashboard";

    /// <summary>Markdown notes with their links (an Obsidian vault).</summary>
    public const string Notes = "notes";

    /// <summary>A Markdown file of the folder (<see cref="C4kItem.File"/>, README.md by default).</summary>
    public const string Markdown = "markdown";

    /// <summary>Fields from an HTTP API (<see cref="C4kItem.Api"/>).</summary>
    public const string Api = "api";

    /// <summary>Card only: status, CPU, memory and disks of an SSH server (the tile's <c>open</c> is an <c>sftp://</c> path).</summary>
    public const string Ssh = "ssh";

    /// <summary>The app's settings page (the view of <c>config/</c>).</summary>
    public const string Settings = "settings";

    /// <summary>The skins page (the view of <c>config/skins/</c>).</summary>
    public const string Skins = "skins";

    /// <summary>The plugin manager (the view of the plugins folder).</summary>
    public const string Plugins = "plugins";

    /// <summary>Views drawn by the app itself rather than by a preview plugin.</summary>
    public static readonly IReadOnlyList<string> AppViews = [Settings, Skins, Plugins];

    public static bool IsAppView(string? view) => view is not null && AppViews.Contains(view, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<string> All = [Files, Gallery, Notes, Markdown, Api, Settings, Skins, Plugins];
}
