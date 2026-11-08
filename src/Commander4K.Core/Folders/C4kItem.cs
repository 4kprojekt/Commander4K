using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Folders;

/// <summary>One <c>*.c4k</c> file: a view, a plugin, a link or a command, and when in a dashboard a tile.</summary>
public sealed record C4kItem
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public required string FilePath { get; init; }

    /// <summary>The folder that holds the file.</summary>
    public required string Folder { get; init; }

    /// <summary>File name without <c>.c4k</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The folder the item is about (key <c>folder</c> against <see cref="Folder"/>), else <see cref="Folder"/>.</summary>
    public string ContextFolder { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Icon { get; init; }

    public string? Image { get; init; }

    public string? View { get; init; }

    public string? Plugin { get; init; }

    /// <summary>JSON key <c>preview</c>: the plugin's preview id.</summary>
    public string? PreviewId { get; init; }

    public string? Panel { get; init; }

    public string? Open { get; init; }

    public string? User { get; init; }

    public string? KeyFile { get; init; }

    public string? Url { get; init; }

    public string? File { get; init; }

    /// <summary>Raw JSON object passed to the plugin.</summary>
    public string? Options { get; init; }

    /// <summary>As a dashboard tile: <see cref="FolderTile.AutoCard"/>, a view or a plugin id; <c>null</c> = a plain shortcut.</summary>
    public string? Card { get; init; }

    public FolderRun? Run { get; init; }

    public ApiRequest? Api { get; init; }

    public TimeSpan? Refresh { get; init; }

    public TileSize Size { get; init; }

    /// <summary>Why the file (or one field) is wrong; the item keeps what parsed.</summary>
    public string? Error { get; init; }

    /// <summary>SHA-256 of the file text: trust is given to this exact content.</summary>
    public string Hash { get; init; } = string.Empty;

    public FolderActionKind Kind =>
        Run is not null ? FolderActionKind.Run
        : Open is not null ? FolderActionKind.Open
        : Url is not null ? FolderActionKind.Url
        : Panel is not null ? FolderActionKind.Panel
        : FolderActionKind.View;

    public string? PanelId => Panel is null ? null : Plugin is not null && !Panel.Contains('/') && !Panel.StartsWith("builtin.", StringComparison.Ordinal) ? $"{Plugin}/{Panel}" : Panel;

    public bool IsPlainFiles => Kind == FolderActionKind.View && Plugin is null && (View is null || string.Equals(View, FolderViewIds.Files, StringComparison.OrdinalIgnoreCase));

    /// <summary>SHA-256 over only what trust unlocks (run, api, an ssh card's server and login), so resizing or retitling keeps it.</summary>
    public string TrustHash
    {
        get
        {
            var parts = new List<object?>();
            if (Run is { } run)
            {
                parts.Add(new object?[] { "run", run.Command, run.Arguments, run.WorkingDirectory, run.Hidden });
            }

            if (Api is { } api)
            {
                parts.Add(new object?[] { "api", api.Method, api.Url, api.Body, api.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Select(h => new[] { h.Key, h.Value }) });
            }

            if (string.Equals(Card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(new object?[] { "ssh", Open, User, KeyFile });
            }

            if (IsRemoteCard && !string.Equals(Card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(new object?[] { "remote", Open, User, KeyFile });
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
        }
    }

    /// <summary>What trusting this file unlocks, one line each (same wording as <see cref="FolderConfig.TrustSummary"/>).</summary>
    public IReadOnlyList<string> TrustSummary()
    {
        var lines = new List<string>();
        if (Run is { } run)
        {
            lines.Add("run " + run);
        }
        else if (Api is { HasSideEffects: true } api)
        {
            lines.Add($"{api.Method} {api.Url}");
        }
        else if (string.Equals(Card, FolderViewIds.Ssh, StringComparison.OrdinalIgnoreCase) && Open is { } server)
        {
            lines.Add("SSH " + Resolve(server));
        }
        else if (IsRemoteCard && Open is { } remote)
        {
            var path = Resolve(remote);
            lines.Add((VfsPath.GetScheme(path) == "sftp" ? "SFTP " : "FTP ") + path);
        }

        return lines;
    }

    /// <summary>A card that reads an sftp/ftp target signs in with saved logins or the user's keys.</summary>
    private bool IsRemoteCard => Card is not null && Open is { } open && VfsPath.GetScheme(Resolve(open)) is "sftp" or "ftp";

    public bool NeedsTrust => TrustSummary().Count > 0;

    public string Resolve(string path) => C4kJson.Resolve(Folder, path);

    /// <summary>Never throws: bad JSON or a bad field sets <see cref="Error"/>.</summary>
    public static C4kItem Parse(string folder, string fileName, string json)
    {
        var name = fileName.EndsWith(C4kReader.Extension, StringComparison.OrdinalIgnoreCase) ? fileName[..^C4kReader.Extension.Length] : fileName;
        var item = new C4kItem
        {
            FilePath = C4kJson.ChildPath(folder, fileName),
            Folder = folder,
            Name = name,
            ContextFolder = folder,
            Title = name,
            Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
        };
        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return item with { Error = $"{fileName} must contain a JSON object." };
            }

            string? S(string key) => C4kJson.String(root, key);
            item = item with
            {
                Title = S("title") ?? name,
                Description = S("description"),
                Icon = S("icon"),
                Image = S("image"),
                View = S("view"),
                Plugin = S("plugin"),
                PreviewId = S("preview"),
                Panel = S("panel"),
                Open = S("open"),
                User = S("user"),
                KeyFile = S("keyFile"),
                Url = S("url"),
                File = S("file"),
                Options = root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object ? options.GetRawText() : null,
            };
            if (S("folder") is { } context)
            {
                item = item with { ContextFolder = item.Resolve(context) };
            }

            // Run and card fields are checked on their own so one mistake does not hide what else parsed.
            string? error = null;
            try
            {
                item = item with { Run = root.TryGetProperty("run", out var run) ? C4kJson.ParseRun(run) : null };
            }
            catch (FormatException ex)
            {
                error = ex.Message;
            }

            try
            {
                item = ParseCard(root, item);
            }
            catch (FormatException ex)
            {
                var card = root.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                item = item with { Card = string.IsNullOrEmpty(card) ? FolderTile.AutoCard : card };
                error ??= ex.Message;
            }

            return error is null ? item : item with { Error = error };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return item with { Error = ex.Message };
        }
    }

    private static C4kItem ParseCard(JsonElement root, C4kItem item)
    {
        var card = root.TryGetProperty("card", out var c) ? c.ValueKind switch
        {
            JsonValueKind.True => FolderTile.AutoCard,
            JsonValueKind.False or JsonValueKind.Null => null,
            JsonValueKind.String when c.GetString() is { Length: > 0 } text => text,
            _ => throw new FormatException("\"card\" must be true or a preview name."),
        } : null;
        if (card is not null && item.Run is not null)
        {
            throw new FormatException("A card cannot run a command; use a plain item for \"run\".");
        }

        var api = root.TryGetProperty("api", out var a) ? C4kJson.ParseApi(a) : null;
        if (string.Equals(card, FolderViewIds.Api, StringComparison.OrdinalIgnoreCase) && api is null)
        {
            throw new FormatException("An \"api\" card needs \"api\": { \"url\": … }.");
        }

        var refresh = root.TryGetProperty("refresh", out var r) && r.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            ? throw new FormatException("\"refresh\" must be text such as \"10s\".")
            : C4kJson.ParseRefresh(C4kJson.String(root, "refresh"));
        var size = TileSizes.Parse(C4kJson.String(root, "size"))
            ?? throw new FormatException($"Unknown \"size\" '{C4kJson.String(root, "size")}' (small, wide, tall, large, huge, full).");
        return item with { Card = card, Api = api, Refresh = refresh, Size = size };
    }
}
