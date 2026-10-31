using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Folders;

/// <summary>Views built into the app that a <c>c4k.dir</c> can name with <c>"view"</c>.</summary>
public static class FolderViewIds
{
    /// <summary>The plain file list (or, as a preview, folder sizes).</summary>
    public const string Files = "files";

    /// <summary>Image thumbnails of the folder.</summary>
    public const string Gallery = "gallery";

    /// <summary>Tiles from <see cref="FolderAction.Tiles"/>: paths, links, scripts.</summary>
    public const string Dashboard = "dashboard";

    /// <summary>Markdown notes with their links (an Obsidian vault).</summary>
    public const string Notes = "notes";

    /// <summary>A Markdown file of the folder (<see cref="FolderAction.File"/>, README.md by default).</summary>
    public const string Markdown = "markdown";

    public static readonly IReadOnlyList<string> All = [Files, Gallery, Dashboard, Notes, Markdown];
}

public enum FolderActionKind
{
    /// <summary>A view: built-in (<see cref="FolderAction.View"/>) or a plugin's preview (<see cref="FolderAction.Plugin"/> + <see cref="FolderAction.Preview"/>).</summary>
    View,

    /// <summary>A panel plugin (<see cref="FolderAction.Panel"/>), e.g. a table of games.</summary>
    Panel,

    /// <summary>Go somewhere else: <c>sftp://user@host/dir</c> (with a key), a share, another folder.</summary>
    Open,

    /// <summary>A web page in the browser panel.</summary>
    Url,

    /// <summary>A program or script; runs only after the user trusts this <c>c4k.dir</c>.</summary>
    Run,
}

/// <summary>A program started from a <c>c4k.dir</c> (a script, a build, a game launcher).</summary>
/// <param name="WorkingDirectory">Relative to the folder; the folder itself by default.</param>
/// <param name="Hidden">Run without a window (the default shows a console, so the user sees what runs).</param>
public sealed record FolderRun(string Command, IReadOnlyList<string> Arguments, string? WorkingDirectory = null, bool Hidden = false)
{
    public override string ToString() => Arguments.Count == 0 ? Command : Command + " " + string.Join(" ", Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}

/// <summary>A dashboard tile: what it shows and what a click does (exactly one of open, url, run).</summary>
/// <param name="Image">Picture file relative to the folder (or any path).</param>
public sealed record FolderTile(string Title, string? Subtitle, string? Icon, string? Image, string? Open, string? Url, FolderRun? Run);

/// <summary>
/// What happens on preview or on entering the folder. Exactly one target is meant to be set; when several are,
/// <see cref="Kind"/> picks in the order run, open, url, panel, view.
/// </summary>
public sealed record FolderAction
{
    /// <summary>Built-in view (<see cref="FolderViewIds"/>).</summary>
    public string? View { get; init; }

    /// <summary>Plugin that renders or handles this folder (its manifest id); installed from the catalog when missing.</summary>
    public string? Plugin { get; init; }

    /// <summary>The plugin's preview id (<c>contributes.previews</c>); the plugin's first preview when omitted.</summary>
    public string? Preview { get; init; }

    /// <summary>Panel id: a built-in panel (<c>builtin.sysinfo</c>) or a plugin's (<c>&lt;plugin&gt;/&lt;panel&gt;</c>, or just the panel id with <see cref="Plugin"/>).</summary>
    public string? Panel { get; init; }

    /// <summary>Path to show instead of the folder (relative paths are resolved against it).</summary>
    public string? Open { get; init; }

    /// <summary>User name for <see cref="Open"/> on SFTP/FTP.</summary>
    public string? User { get; init; }

    /// <summary>Private key for <see cref="Open"/> on SFTP (<c>~</c> = the user's profile).</summary>
    public string? KeyFile { get; init; }

    public string? Url { get; init; }

    public FolderRun? Run { get; init; }

    /// <summary>File shown by the <c>markdown</c> view.</summary>
    public string? File { get; init; }

    public IReadOnlyList<FolderTile> Tiles { get; init; } = [];

    /// <summary>Raw JSON passed to the plugin (<c>preview.render</c> / <c>panel.load</c> <c>options</c>).</summary>
    public string? Options { get; init; }

    public FolderActionKind Kind =>
        Run is not null ? FolderActionKind.Run
        : Open is not null ? FolderActionKind.Open
        : Url is not null ? FolderActionKind.Url
        : Panel is not null ? FolderActionKind.Panel
        : FolderActionKind.View;

    /// <summary>The panel id to look up: <see cref="Panel"/> as given, or qualified with <see cref="Plugin"/>.</summary>
    public string? PanelId => Panel is null ? null : Plugin is not null && !Panel.Contains('/') && !Panel.StartsWith("builtin.", StringComparison.Ordinal) ? $"{Plugin}/{Panel}" : Panel;

    /// <summary>Whether the action is the ordinary file list (nothing to do).</summary>
    public bool IsPlainFiles => Kind == FolderActionKind.View && Plugin is null && (View is null || string.Equals(View, FolderViewIds.Files, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A folder's <c>c4k.dir</c>: what its preview shows and what entering it does. JSON with comments; each action is an
/// object (see <see cref="FolderAction"/>) or a string — a built-in view name, or a plugin id. Invalid files are
/// reported through <see cref="Error"/> instead of throwing, so the panel can say what is wrong.
/// </summary>
public sealed record FolderConfig
{
    public const string FileName = "c4k.dir";

    public const string SchemaUrl = "https://raw.githubusercontent.com/4kprojekt/Commander4K/main/docs/c4k.dir.schema.json";

    /// <summary>The folder that holds the file.</summary>
    public required string Folder { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public FolderAction? Preview { get; init; }

    public FolderAction? Enter { get; init; }

    /// <summary>SHA-256 of the file's text: trust for scripts is given to this exact content.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Why the file could not be read; the actions are empty then.</summary>
    public string? Error { get; init; }

    public string FilePath => ChildPath(Folder, FileName);

    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static FolderConfig Parse(string folder, string json)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new FolderConfig { Folder = folder, Hash = hash, Error = "c4k.dir must contain a JSON object." };
            }

            return new FolderConfig
            {
                Folder = folder,
                Hash = hash,
                Title = String(root, "title"),
                Description = String(root, "description"),
                Preview = root.TryGetProperty("preview", out var preview) ? ParseAction(preview) : null,
                Enter = root.TryGetProperty("enter", out var enter) ? ParseAction(enter) : null,
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return new FolderConfig { Folder = folder, Hash = hash, Error = ex.Message };
        }
    }

    /// <summary>Reads the folder's <c>c4k.dir</c> through the router; <c>null</c> when there is none (or it cannot be read in time).</summary>
    public static async Task<FolderConfig?> ReadAsync(Vfs.VfsRouter router, string folder, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var path = ChildPath(folder, FileName);
            if (await router.GetEntryAsync(path, limit.Token).ConfigureAwait(false) is not { Kind: VfsEntryKind.File, Size: <= MaxBytes })
            {
                return null;
            }

            await using var stream = await router.OpenReadAsync(path, limit.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Parse(folder, await reader.ReadToEndAsync(limit.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VfsAuthenticationRequiredException or VfsProviderNotFoundException or NotSupportedException or TimeoutException or FormatException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private const long MaxBytes = 256 * 1024;

    /// <summary>A child of a VFS folder: backslashes on disks and shares, slashes elsewhere (SFTP, archives).</summary>
    public static string ChildPath(string folder, string name)
    {
        var backslash = VfsPath.GetScheme(folder) is VfsPath.FileScheme or VfsPath.SmbScheme && !folder.Contains("://", StringComparison.Ordinal);
        var separator = backslash ? '\\' : '/';
        return folder.EndsWith('/') || folder.EndsWith('\\') ? folder + name : folder + separator + name;
    }

    /// <summary>A path from the file, resolved against the folder: absolute paths and URIs stay, <c>~</c> is the user's profile.</summary>
    public string Resolve(string path)
    {
        if (path.StartsWith('~'))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/', '\\').Replace('/', '\\'));
        }

        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal) || Path.IsPathRooted(path) && path.Length > 1 && path[1] == ':')
        {
            return path;
        }

        var result = Folder;
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            result = part switch
            {
                "." => result,
                ".." => ParentOf(result),
                _ => ChildPath(result, part),
            };
        }

        return result;
    }

    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut <= 0 || trimmed[..cut].EndsWith(':') && !trimmed[..cut].Contains("://", StringComparison.Ordinal) ? trimmed[..(cut + 1)] : trimmed[..cut];
    }

    private static FolderAction ParseAction(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var name = element.GetString() ?? string.Empty;
            return FolderViewIds.All.Contains(name, StringComparer.OrdinalIgnoreCase) ? new FolderAction { View = name } : new FolderAction { Plugin = name };
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("\"preview\" and \"enter\" must be an object or a view name.");
        }

        return new FolderAction
        {
            View = String(element, "view"),
            Plugin = String(element, "plugin"),
            Preview = String(element, "preview"),
            Panel = String(element, "panel"),
            Open = String(element, "open"),
            User = String(element, "user"),
            KeyFile = String(element, "keyFile"),
            Url = String(element, "url"),
            File = String(element, "file"),
            Run = element.TryGetProperty("run", out var run) ? ParseRun(run) : null,
            Tiles = element.TryGetProperty("tiles", out var tiles) && tiles.ValueKind == JsonValueKind.Array ? [.. tiles.EnumerateArray().Select(ParseTile)] : [],
            Options = element.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object ? options.GetRawText() : null,
        };
    }

    private static FolderTile ParseTile(JsonElement tile) => new(
        String(tile, "title") ?? throw new FormatException("Every tile needs a \"title\"."),
        String(tile, "subtitle"),
        String(tile, "icon"),
        String(tile, "image"),
        String(tile, "open"),
        String(tile, "url"),
        tile.TryGetProperty("run", out var run) ? ParseRun(run) : null);

    /// <summary><c>"run": "script.ps1"</c> or <c>{ "command": "pwsh", "args": ["-File", "build.ps1"], "cwd": "src", "hidden": false }</c>.</summary>
    private static FolderRun ParseRun(JsonElement run) => run.ValueKind switch
    {
        JsonValueKind.String => new FolderRun(run.GetString() ?? string.Empty, []),
        JsonValueKind.Object => new FolderRun(
            String(run, "command") ?? throw new FormatException("\"run\" needs a \"command\"."),
            run.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array ? [.. args.EnumerateArray().Select(a => a.ToString())] : [],
            String(run, "cwd"),
            run.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True),
        _ => throw new FormatException("\"run\" must be a command or an object."),
    };

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}

/// <summary>
/// Scripts from <c>c4k.dir</c> files the user allowed (<c>config/trusted-folders.json</c>): a file path and the hash of
/// its content. A changed file (another script, a copied folder) needs consent again.
/// </summary>
public sealed class FolderTrustStore(string path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _trusted;

    public bool IsTrusted(FolderConfig config)
    {
        lock (_gate)
        {
            return Load().TryGetValue(config.FilePath, out var hash) && hash == config.Hash;
        }
    }

    public void Trust(FolderConfig config)
    {
        lock (_gate)
        {
            var trusted = Load();
            trusted[config.FilePath] = config.Hash;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(trusted, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_trusted is not null)
        {
            return _trusted;
        }

        try
        {
            _trusted = File.Exists(path)
                ? new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [], StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return _trusted;
    }
}
