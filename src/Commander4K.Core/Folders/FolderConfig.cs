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

    /// <summary>Fields from an HTTP API (<see cref="FolderAction.Api"/>).</summary>
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

    public static readonly IReadOnlyList<string> All = [Files, Gallery, Dashboard, Notes, Markdown, Api, Settings, Skins, Plugins];
}

/// <summary>Footprint of a tile on the dashboard grid, in columns × rows.</summary>
public enum TileSize
{
    /// <summary>1 × 1.</summary>
    Small,

    /// <summary>2 × 1.</summary>
    Wide,

    /// <summary>1 × 2.</summary>
    Tall,

    /// <summary>2 × 2.</summary>
    Large,

    /// <summary>3 × 2: a chart or a long list.</summary>
    Huge,

    /// <summary>Every column of the row, one row high (a banner, a note, a status line).</summary>
    Full,
}

/// <summary>Names and spans of <see cref="TileSize"/>.</summary>
public static class TileSizes
{
    /// <summary>The order the dashboard's resize button cycles through.</summary>
    public static readonly IReadOnlyList<TileSize> Cycle = [TileSize.Small, TileSize.Wide, TileSize.Tall, TileSize.Large, TileSize.Huge, TileSize.Full];

    /// <summary>Spans in grid units; <see cref="TileSize.Full"/> reports 0 columns = the whole row.</summary>
    public static (int Columns, int Rows) Span(TileSize size) => size switch
    {
        TileSize.Wide => (2, 1),
        TileSize.Tall => (1, 2),
        TileSize.Large => (2, 2),
        TileSize.Huge => (3, 2),
        TileSize.Full => (0, 1),
        _ => (1, 1),
    };

    public static string Name(TileSize size) => size.ToString().ToLowerInvariant();

    /// <returns><c>null</c> for an unknown name.</returns>
    public static TileSize? Parse(string? name) => name switch
    {
        null or "small" => TileSize.Small,
        "wide" => TileSize.Wide,
        "tall" => TileSize.Tall,
        "large" => TileSize.Large,
        "huge" => TileSize.Huge,
        "full" => TileSize.Full,
        _ => null,
    };

    public static TileSize Next(TileSize size) => Cycle[(Cycle.IndexOf(size) + 1) % Cycle.Count];

    private static int IndexOf(this IReadOnlyList<TileSize> list, TileSize size)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == size)
            {
                return i;
            }
        }

        return 0;
    }
}

/// <summary>How an API field is shown: as a value, as a point added to a chart on every refresh, or as a status colour.</summary>
public enum ApiFieldKind
{
    Value,
    Chart,
    Status,
}

/// <summary>One field picked from an API response.</summary>
/// <param name="Path">Path in the JSON: <c>a.b</c>, <c>items[0].name</c>, <c>$</c> for the whole value.</param>
/// <param name="Ok">For <see cref="ApiFieldKind.Status"/>: values that mean good; empty = <c>true</c>, ok, up, healthy.</param>
public sealed record ApiField(string Path, string? Label, ApiFieldKind As, string? Unit, IReadOnlyList<string> Ok);

/// <summary>
/// An HTTP request from a <c>c4k.dir</c>. Url, header values and body may hold <c>{secret:name}</c> placeholders; the
/// values live in the app's secret store, never in the file.
/// </summary>
public sealed record ApiRequest(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string? Body, string? ContentType, IReadOnlyList<ApiField> Fields)
{
    /// <summary>Anything but GET and HEAD may change something on the server, so it needs the user's trust in the file.</summary>
    public bool HasSideEffects => Method is not ("GET" or "HEAD");
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
public sealed record FolderTile(string Title, string? Subtitle, string? Icon, string? Image, string? Open, string? Url, FolderRun? Run)
{
    /// <summary><see cref="Card"/> for <c>"card": true</c>: the preview Ctrl+Q would pick for the target.</summary>
    public const string AutoCard = "auto";

    /// <summary>
    /// <c>null</c> = a plain shortcut; otherwise the tile is a card drawn by this preview: <see cref="AutoCard"/>, a
    /// built-in view (<see cref="FolderViewIds"/>), or a plugin id (<c>&lt;plugin&gt;</c> or <c>&lt;plugin&gt;/&lt;preview&gt;</c>).
    /// </summary>
    public string? Card { get; init; }

    /// <summary>How often the card is read again while the dashboard is visible; <c>null</c> = once.</summary>
    public TimeSpan? Refresh { get; init; }

    /// <summary>Raw JSON object passed to the card's preview.</summary>
    public string? Options { get; init; }

    public TileSize Size { get; init; }

    /// <summary>A section heading across the whole row (title + optional subtitle), not a tile; groups the tiles after it.</summary>
    public bool Heading { get; init; }

    /// <summary>The footprint used for layout: a heading always takes one whole row.</summary>
    public TileSize LayoutSize => Heading ? TileSize.Full : Size;

    /// <summary>The request of an <see cref="FolderViewIds.Api"/> card.</summary>
    public ApiRequest? Api { get; init; }

    /// <summary>Why the tile's card fields are wrong; the dashboard shows it on the tile instead of failing the file.</summary>
    public string? Error { get; init; }
}

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

    /// <summary>The request of the <see cref="FolderViewIds.Api"/> view.</summary>
    public ApiRequest? Api { get; init; }

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

    /// <summary>
    /// What trusting this file unlocks, one line each, shown verbatim in the consent dialog: commands it runs, requests
    /// that may change something (anything but GET/HEAD) and SSH servers it signs in to.
    /// </summary>
    public IReadOnlyList<string> TrustSummary()
    {
        var lines = new List<string>();
        foreach (var action in new[] { Enter, Preview })
        {
            if (action is null)
            {
                continue;
            }

            if (action.Run is { } run)
            {
                lines.Add("run " + run);
            }

            if (action.Api is { HasSideEffects: true } api)
            {
                lines.Add($"{api.Method} {api.Url}");
            }

            foreach (var tile in action.Tiles)
            {
                if (tile.Run is { } tileRun)
                {
                    lines.Add("run " + tileRun);
                }
                else if (tile.Api is { HasSideEffects: true } tileApi)
                {
                    lines.Add($"{tileApi.Method} {tileApi.Url}");
                }
                else if (string.Equals(tile.Card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase) && tile.Open is { } server)
                {
                    lines.Add("SSH " + Resolve(server));
                }
            }
        }

        return [.. lines.Distinct(StringComparer.Ordinal)];
    }

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

            var previewAction = root.TryGetProperty("preview", out var preview) ? ParseAction(preview) : null;
            var enterAction = root.TryGetProperty("enter", out var enter) ? ParseAction(enter) : null;

            // "preview": "dashboard" next to a dashboard on Enter (or the other way round) shows the same tiles.
            if (previewAction is { View: FolderViewIds.Dashboard, Tiles.Count: 0 } && enterAction is { View: FolderViewIds.Dashboard, Tiles.Count: > 0 })
            {
                previewAction = previewAction with { Tiles = enterAction.Tiles };
            }
            else if (enterAction is { View: FolderViewIds.Dashboard, Tiles.Count: 0 } && previewAction is { View: FolderViewIds.Dashboard, Tiles.Count: > 0 })
            {
                enterAction = enterAction with { Tiles = previewAction.Tiles };
            }

            return new FolderConfig
            {
                Folder = folder,
                Hash = hash,
                Title = String(root, "title"),
                Description = String(root, "description"),
                Preview = previewAction,
                Enter = enterAction,
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
        // Any failure (SocketException for an unknown host, SSH.NET's own types…) means "no file": callers navigate anyway.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private const long MaxBytes = 256 * 1024;

    public static string ChildPath(string folder, string name) => C4kJson.ChildPath(folder, name);

    public string Resolve(string path) => C4kJson.Resolve(Folder, path);

    public static TimeSpan? ParseRefresh(string? text) => C4kJson.ParseRefresh(text);

    private static FolderRun ParseRun(JsonElement run) => C4kJson.ParseRun(run);

    private static ApiRequest ParseApi(JsonElement api) => C4kJson.ParseApi(api);

    private static string? String(JsonElement element, string name) => C4kJson.String(element, name);

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
            Api = element.TryGetProperty("api", out var api) ? ParseApi(api) : null,
        };
    }

    private static FolderTile ParseTile(JsonElement tile)
    {
        var parsed = new FolderTile(
            String(tile, "title") ?? throw new FormatException("Every tile needs a \"title\"."),
            String(tile, "subtitle"),
            String(tile, "icon"),
            String(tile, "image"),
            String(tile, "open"),
            String(tile, "url"),
            tile.TryGetProperty("run", out var run) ? ParseRun(run) : null)
        {
            Heading = tile.TryGetProperty("heading", out var heading) && heading.ValueKind == JsonValueKind.True,
        };
        if (parsed.Heading)
        {
            return parsed;
        }

        // Card fields are checked per tile: one mistyped tile shows its error and the rest of the dashboard still works.
        try
        {
            var card = tile.TryGetProperty("card", out var c) ? c.ValueKind switch
            {
                JsonValueKind.True => FolderTile.AutoCard,
                JsonValueKind.False or JsonValueKind.Null => null,
                JsonValueKind.String when c.GetString() is { Length: > 0 } name => name,
                _ => throw new FormatException("\"card\" must be true or a preview name."),
            } : null;
            if (card is not null && parsed.Run is not null)
            {
                throw new FormatException("A card cannot run a command; use a plain tile for \"run\".");
            }

            var api = tile.TryGetProperty("api", out var a) ? ParseApi(a) : null;
            if (string.Equals(card, FolderViewIds.Api, StringComparison.OrdinalIgnoreCase) && api is null)
            {
                throw new FormatException("An \"api\" card needs \"api\": { \"url\": … }.");
            }

            return parsed with
            {
                Card = card,
                Refresh = tile.TryGetProperty("refresh", out var refresh) && refresh.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                    ? throw new FormatException("\"refresh\" must be text such as \"10s\".")
                    : ParseRefresh(String(tile, "refresh")),
                Options = tile.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object ? options.GetRawText() : null,
                Size = TileSizes.Parse(String(tile, "size"))
                    ?? throw new FormatException($"Unknown \"size\" '{String(tile, "size")}' (small, wide, tall, large, huge, full)."),
                Api = api,
            };
        }
        catch (FormatException ex)
        {
            // Still a card, so the dashboard shows the message where the card would be.
            var name = tile.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return parsed with { Card = string.IsNullOrEmpty(name) ? FolderTile.AutoCard : name, Error = ex.Message };
        }
    }

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
