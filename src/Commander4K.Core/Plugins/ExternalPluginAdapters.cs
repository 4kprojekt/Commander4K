using System.Text.Json;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

/// <summary>Exposes an external plugin's <c>contributes.commands</c> as an <see cref="ICommandPlugin"/>.</summary>
public sealed class ExternalCommandPlugin(ExternalPlugin plugin) : ICommandPlugin
{
    public string Id => plugin.Manifest.Id;

    public string DisplayName => plugin.Manifest.Name;

    public PluginManifest Manifest => plugin.Manifest;

    public IReadOnlyList<PluginCommand> Commands =>
        [.. (plugin.Manifest.Contributes?.Commands ?? []).Select(c => new PluginCommand(c.Id, c.Title, c.Category, c.Description, c.Shortcut))];

    public event Action? CommandsChanged
    {
        add { }
        remove { }
    }

    /// <remarks>Result: <c>{ "message": "...", "text": { "title": "...", "content": "..." } }</c>, both optional.</remarks>
    public async Task ExecuteAsync(string commandId, IPluginHost host, CancellationToken cancellationToken)
    {
        var result = await plugin.CallAsync("command.execute", new { command = commandId, context = ExternalPlugin.Context(host) }, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (result.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
        {
            host.Notify(text);
        }

        if (result.TryGetProperty("text", out var output) && output.ValueKind == JsonValueKind.Object)
        {
            await host.ShowTextAsync(
                output.TryGetProperty("title", out var t) ? t.GetString() ?? DisplayName : DisplayName,
                output.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty).ConfigureAwait(false);
        }
    }
}

/// <summary>One <c>contributes.panels</c> entry of an external plugin as an <see cref="IPanelPlugin"/>.</summary>
public sealed class ExternalPanelPlugin(ExternalPlugin plugin, PanelContribution panel) : IPanelPlugin
{
    /// <summary><c>&lt;pluginId&gt;/&lt;panelId&gt;</c>: a plugin may contribute several panels.</summary>
    public string Id => $"{plugin.Manifest.Id}/{panel.Id}";

    public string DisplayName => panel.Title;

    public PluginManifest Manifest => plugin.Manifest;

    public string Title => panel.Title;

    public string Description => panel.Description ?? plugin.Manifest.Description;

    public bool RequiresArgument => panel.RequiresArgument;

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session(plugin, panel, host);

    private sealed class Session(ExternalPlugin plugin, PanelContribution panel, IPluginHost host) : IPanelSession
    {
        public TimeSpan? RefreshInterval => panel.RefreshSeconds is int seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null;

        public IReadOnlyList<PanelAction> Actions { get; } =
            [.. (panel.Actions ?? []).Select(a => new PanelAction(a.Id, a.Title, a.Shortcut, a.Confirm, a.AppliesToRows))];

        public async Task<PanelSnapshot> LoadAsync(PanelRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            using var options = request.Options is null ? null : JsonDocument.Parse(request.Options);
            var result = await plugin.CallAsync("panel.load",
                new { panel = panel.Id, argument = request.Argument, filter = request.Filter, context = ExternalPlugin.Context(host), options = options?.RootElement },
                cancellationToken).ConfigureAwait(false);
            return ParseSnapshot(result);
        }

        public async Task<PanelDetails?> GetDetailsAsync(string rowId, CancellationToken cancellationToken)
        {
            var result = await plugin.CallAsync("panel.details", new { panel = panel.Id, rowId }, cancellationToken).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("sections", out var sections)
                ? new PanelDetails([.. sections.EnumerateArray().Select(s => new PanelDetailSection(
                    s.GetProperty("title").GetString() ?? string.Empty,
                    [.. s.GetProperty("items").EnumerateArray().Select(i => new KeyValuePair<string, string>(Text(i[0]), Text(i[1])))]))])
                : null;
        }

        public async Task<string?> ExecuteActionAsync(string actionId, IReadOnlyList<string> rowIds, CancellationToken cancellationToken)
        {
            var result = await plugin.CallAsync("panel.action",
                new { panel = panel.Id, action = actionId, rowIds, context = ExternalPlugin.Context(host) },
                cancellationToken).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("text", out var text) ? text.GetString() : null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// <c>{ columns: [{id, header, align?, width?}], rows: [{id, parentId?, cells: [], severity?, sortKeys?, expanded?}], status? }</c>.
    /// </summary>
    internal static PanelSnapshot ParseSnapshot(JsonElement result)
    {
        var columns = result.GetProperty("columns").EnumerateArray().Select(c => new ColumnDefinition(
            c.GetProperty("id").GetString()!,
            c.GetProperty("header").GetString() ?? string.Empty,
            c.TryGetProperty("align", out var align) && Enum.TryParse<ColumnAlignment>(align.GetString(), ignoreCase: true, out var a) ? a : ColumnAlignment.Left,
            c.TryGetProperty("width", out var width) && width.TryGetDouble(out var w) ? w : 160)).ToList();

        var rows = result.GetProperty("rows").EnumerateArray().Select(r => new PanelRow(
            Text(r.GetProperty("id")),
            r.TryGetProperty("parentId", out var parent) && parent.ValueKind != JsonValueKind.Null ? Text(parent) : null,
            [.. r.GetProperty("cells").EnumerateArray().Select(Text)],
            r.TryGetProperty("severity", out var severity) && Enum.TryParse<RowSeverity>(severity.GetString(), ignoreCase: true, out var s) ? s : RowSeverity.Normal,
            r.TryGetProperty("sortKeys", out var keys) && keys.ValueKind == JsonValueKind.Array
                ? [.. keys.EnumerateArray().Select(k => k.ValueKind == JsonValueKind.Number && k.TryGetInt64(out var n) ? n : (long?)null)]
                : null,
            r.TryGetProperty("expanded", out var expanded) && expanded.ValueKind == JsonValueKind.True)).ToList();

        return new PanelSnapshot(columns, rows, result.TryGetProperty("status", out var status) ? status.GetString() : null);
    }

    private static string Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null => string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        _ => element.ToString(),
    };
}

/// <summary>One <c>contributes.previews</c> entry: quick view rendered by the plugin (<c>preview.render</c>).</summary>
public sealed class ExternalPreviewPlugin(ExternalPlugin plugin, PreviewContribution preview) : IPreviewPlugin
{
    public string Id => $"{plugin.Manifest.Id}/{preview.Id}";

    public string DisplayName => preview.Title;

    public PluginManifest Manifest => plugin.Manifest;

    public int Priority => preview.Priority;

    public bool CanPreview(VfsEntry entry) =>
        entry.IsContainer
            ? entry.Kind != VfsEntryKind.ParentLink && preview.Extensions.Contains("/")
            : preview.Extensions.Any(pattern => Matches(pattern, entry.Name));

    /// <summary>
    /// <c>.csv</c> = extension; anything else is a file name or a pattern with <c>*</c>/<c>?</c> (<c>package.json</c>,
    /// <c>requirements*.txt</c>, <c>*.openapi.json</c>), so a preview can claim specific files without a whole extension.
    /// </summary>
    public static bool Matches(string pattern, string name) =>
        pattern.StartsWith('.') && pattern.IndexOfAny(['*', '?']) < 0
            ? string.Equals(Path.GetExtension(name), pattern, StringComparison.OrdinalIgnoreCase)
            : pattern != "/" && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    public Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken) =>
        LoadWithOptionsAsync(entry, host, null, cancellationToken);

    private const int MaxTileImageBytes = 4 * 1024 * 1024;
    private const int MaxTiles = 1000;

    /// <param name="options">JSON object from the folder's <c>c4k.dir</c>, passed as <c>options</c>.</param>
    /// <remarks>
    /// Result: <c>{ "kind": "markdown" | "text", "content": "...", "editable": false }</c> or
    /// <c>{ "kind": "tiles", "title", "description"?, "tileWidth"?, "tiles": [{ "title", "subtitle"?, "icon"?, "image"?, "target"? }] }</c>;
    /// a tile's <c>image</c> is a path the host reads with the plugin's own fs.read permission.
    /// </remarks>
    public async Task<PreviewContent> LoadWithOptionsAsync(VfsEntry entry, IPluginHost host, string? options, CancellationToken cancellationToken)
    {
        using var optionsDocument = options is null ? null : JsonDocument.Parse(options);
        var result = await plugin.CallAsync("preview.render",
            new { preview = preview.Id, entry = ExternalPlugin.EntryDto(entry), context = ExternalPlugin.Context(host), options = optionsDocument?.RootElement },
            cancellationToken).ConfigureAwait(false);
        var kind = result.TryGetProperty("kind", out var k) ? k.GetString() : "text";
        if (string.Equals(kind, "tiles", StringComparison.OrdinalIgnoreCase))
        {
            return await ParseTilesAsync(result, host, cancellationToken).ConfigureAwait(false);
        }

        var content = result.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var editable = result.TryGetProperty("editable", out var e) && e.ValueKind == JsonValueKind.True;
        return string.Equals(kind, "markdown", StringComparison.OrdinalIgnoreCase)
            ? new MarkdownPreview(content, editable)
            : new TextPreview(content, editable);
    }

    private static async Task<TilesPreview> ParseTilesAsync(JsonElement result, IPluginHost host, CancellationToken cancellationToken)
    {
        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

        var tiles = new List<PreviewTile>();
        if (result.TryGetProperty("tiles", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray().Take(MaxTiles))
            {
                ReadOnlyMemory<byte>? image = null;
                if (Str(item, "image") is { } imagePath)
                {
                    try
                    {
                        await using var stream = await host.OpenReadAsync(imagePath, cancellationToken).ConfigureAwait(false);
                        var buffer = new byte[MaxTileImageBytes + 1];
                        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                        image = read <= MaxTileImageBytes ? buffer.AsMemory(0, read) : null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PluginPermissionException or ArgumentException or NotSupportedException)
                    {
                        // A missing cover leaves the icon; the rest of the tiles still show.
                    }
                }

                tiles.Add(new PreviewTile(Str(item, "title") ?? string.Empty, Str(item, "subtitle"), Str(item, "icon"), image, Str(item, "target")));
            }
        }

        return new TilesPreview(
            Str(result, "title") ?? string.Empty,
            tiles,
            Str(result, "description"),
            result.TryGetProperty("tileWidth", out var width) && width.TryGetDouble(out var w) ? Math.Clamp(w, 80, 600) : 200);
    }
}

/// <summary>One <c>contributes.palettes</c> entry (<c>palette.items</c>, <c>palette.invoke</c>, <c>palette.action</c>).</summary>
public sealed class ExternalPalettePlugin(ExternalPlugin plugin, PaletteContribution palette) : IPalettePlugin
{
    public string Id => $"{plugin.Manifest.Id}/{palette.Id}";

    public string DisplayName => palette.Title;

    public PluginManifest Manifest => plugin.Manifest;

    public string Title => palette.Title;

    public PaletteDock DefaultDock => palette.Dock.ToLowerInvariant() switch
    {
        "left" => PaletteDock.Left,
        "right" => PaletteDock.Right,
        "between" => PaletteDock.BetweenPanels,
        "hidden" => PaletteDock.Hidden,
        _ => PaletteDock.Bottom,
    };

    public IReadOnlyList<PaletteAction> Actions { get; } =
        [.. (palette.Actions ?? []).Select(a => new PaletteAction(a.Id, a.Title, a.Icon, a.ForItem))];

    /// <summary>External palettes are reloaded by the host when the user navigates.</summary>
    public event Action? ItemsChanged
    {
        add { }
        remove { }
    }

    /// <remarks>Result: <c>{ "items": [{ "id", "title", "icon"?, "tooltip"? }] }</c>.</remarks>
    public async Task<IReadOnlyList<PaletteItem>> GetItemsAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        var result = await plugin.CallAsync("palette.items", new { palette = palette.Id, context = ExternalPlugin.Context(host) }, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("items", out var items))
        {
            return [];
        }

        return [.. items.EnumerateArray().Select(i => new PaletteItem(
            i.GetProperty("id").GetString() ?? string.Empty,
            i.GetProperty("title").GetString() ?? string.Empty,
            i.TryGetProperty("icon", out var icon) ? icon.GetString() : null,
            i.TryGetProperty("tooltip", out var tip) ? tip.GetString() : null))];
    }

    public Task InvokeAsync(string itemId, IPluginHost host, CancellationToken cancellationToken) =>
        Respond(plugin.CallAsync("palette.invoke", new { palette = palette.Id, item = itemId, context = ExternalPlugin.Context(host) }, cancellationToken), host);

    public Task InvokeActionAsync(string actionId, string? itemId, IPluginHost host, CancellationToken cancellationToken) =>
        Respond(plugin.CallAsync("palette.action", new { palette = palette.Id, action = actionId, item = itemId, context = ExternalPlugin.Context(host) }, cancellationToken), host);

    /// <summary>Same optional result as <c>command.execute</c>: <c>{ message?, text?: { title, content } }</c>.</summary>
    private static async Task Respond(Task<JsonElement> call, IPluginHost host)
    {
        var result = await call.ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (result.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
        {
            host.Notify(text);
        }

        if (result.TryGetProperty("text", out var output) && output.ValueKind == JsonValueKind.Object)
        {
            await host.ShowTextAsync(
                output.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                output.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The user's consent per external plugin (<c>config/plugin-approvals.json</c>). A plugin runs only when enabled and its
/// manifest asks for no permission beyond those the user approved; a manifest that asks for more needs new consent.
/// </summary>
public sealed class PluginApprovalStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Lock _gate = new();
    private Dictionary<string, PluginApproval>? _approvals;

    public sealed record PluginApproval(bool Enabled, IReadOnlyList<string> Permissions, string Version, DateTimeOffset ApprovedAt);

    public bool IsApproved(PluginManifest manifest)
    {
        lock (_gate)
        {
            return Load().TryGetValue(manifest.Id, out var approval)
                && approval.Enabled
                && manifest.Permissions.All(p => approval.Permissions.Contains(p, StringComparer.OrdinalIgnoreCase));
        }
    }

    public PluginApproval? Get(string pluginId)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(pluginId);
        }
    }

    public void Approve(PluginManifest manifest) =>
        Save(manifest.Id, new PluginApproval(true, manifest.Permissions, manifest.Version, DateTimeOffset.Now));

    public void Disable(string pluginId)
    {
        lock (_gate)
        {
            if (Load().TryGetValue(pluginId, out var approval))
            {
                Save(pluginId, approval with { Enabled = false });
            }
        }
    }

    private void Save(string pluginId, PluginApproval approval)
    {
        lock (_gate)
        {
            var approvals = Load();
            approvals[pluginId] = approval;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(approvals, Options));
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    private Dictionary<string, PluginApproval> Load()
    {
        if (_approvals is not null)
        {
            return _approvals;
        }

        _approvals = new Dictionary<string, PluginApproval>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            try
            {
                foreach (var (id, approval) in JsonSerializer.Deserialize<Dictionary<string, PluginApproval>>(File.ReadAllText(path), Options) ?? [])
                {
                    _approvals[id] = approval;
                }
            }
            catch (JsonException)
            {
                // A damaged file means no consent: plugins stay disabled until approved again.
            }
        }

        return _approvals;
    }
}

