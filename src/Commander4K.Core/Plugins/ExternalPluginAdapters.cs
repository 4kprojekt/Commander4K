using System.Text.Json;
using Commander4K.Core.Localization;
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
public sealed class ExternalPreviewPlugin(ExternalPlugin plugin, PreviewContribution preview) : IPreviewCardPlugin
{
    private const int MaxCardImages = 4;

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

        if (string.Equals(kind, "chart", StringComparison.OrdinalIgnoreCase) && result.TryGetProperty("chart", out var chart))
        {
            return new ChartPreview(CardString(result, "title") ?? entry.Name, ParseChart(chart), CardString(result, "description"));
        }

        if (string.Equals(kind, "web", StringComparison.OrdinalIgnoreCase))
        {
            return ParseWeb(result, plugin.Manifest.Id, plugin.Folder, preview.WebRoot);
        }

        var content = result.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var editable = result.TryGetProperty("editable", out var e) && e.ValueKind == JsonValueKind.True;
        return string.Equals(kind, "markdown", StringComparison.OrdinalIgnoreCase)
            ? new MarkdownPreview(content, editable)
            : new TextPreview(content, editable);
    }

    /// <summary>
    /// <c>preview.card</c> with the same parameters as <c>preview.render</c> plus <c>previous</c> (the last card, so a
    /// refresh can extend a chart). Previews that do not declare <c>"card": true</c> get the plain card: title and Open.
    /// </summary>
    public async Task<PreviewCard> LoadCardAsync(PreviewCardRequest request, IPluginHost host, CancellationToken cancellationToken)
    {
        if (!preview.Card)
        {
            return new PreviewCard(null, null, "🧩", CardStatus.None, [],
                request.Target is { } target ? [new CardAction(Loc.T("Otwórz"), target.Path)] : []);
        }

        using var optionsDocument = request.OptionsJson is null ? null : JsonDocument.Parse(request.OptionsJson);
        var result = await plugin.CallAsync("preview.card", new
        {
            preview = preview.Id,
            entry = request.Target is { } entry ? ExternalPlugin.EntryDto(entry) : null,
            context = ExternalPlugin.Context(host),
            options = optionsDocument?.RootElement,
            previous = request.Previous is { } previous ? CardDto(previous) : null,
        }, cancellationToken).ConfigureAwait(false);
        return await ParseCardAsync(result, host, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>{ title?, subtitle?, icon?, status?, items: [{ kind: value|progress|text|chart|thumbnails, … }], actions: [{ label, target }] }</c>.
    /// Thumbnails are paths read with the plugin's own fs.read; unknown item kinds are skipped.
    /// </summary>
    public static async Task<PreviewCard> ParseCardAsync(JsonElement result, IPluginHost host, CancellationToken cancellationToken)
    {
        var items = new List<CardItem>();
        if (result.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                switch (CardString(item, "kind"))
                {
                    case "value":
                        items.Add(new CardValue(CardString(item, "label") ?? string.Empty, item.TryGetProperty("value", out var v) ? Text(v) : string.Empty, Status(item)));
                        break;
                    case "progress":
                        items.Add(new CardProgress(CardString(item, "label") ?? string.Empty,
                            item.TryGetProperty("fraction", out var f) && f.TryGetDouble(out var fraction) ? Math.Clamp(fraction, 0, 1) : 0, CardString(item, "text")));
                        break;
                    case "text":
                        items.Add(new CardText(CardString(item, "text") ?? string.Empty));
                        break;
                    case "chart" when item.TryGetProperty("chart", out var chart):
                        items.Add(new CardChart(ParseChart(chart)));
                        break;
                    case "thumbnails" when item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array:
                        var pictures = new List<ReadOnlyMemory<byte>>();
                        foreach (var path in images.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Take(MaxCardImages))
                        {
                            if (await ReadImageAsync(path.GetString()!, host, cancellationToken).ConfigureAwait(false) is { } picture)
                            {
                                pictures.Add(picture);
                            }
                        }

                        items.Add(new CardThumbnails(pictures));
                        break;
                }
            }
        }

        var actions = result.TryGetProperty("actions", out var buttons) && buttons.ValueKind == JsonValueKind.Array
            ? [.. buttons.EnumerateArray().Select(a => new CardAction(CardString(a, "label") ?? string.Empty, CardString(a, "target") ?? string.Empty)).Where(a => a.Target.Length > 0)]
            : new List<CardAction>();
        return new PreviewCard(CardString(result, "title"), CardString(result, "subtitle"), CardString(result, "icon"), Status(result), items, actions);
    }

    /// <summary><c>{ kind: line|bar, unit?, min?, max?, series: [{ name, points: [{ x, y, label? }] }] }</c>.</summary>
    public static ChartData ParseChart(JsonElement chart)
    {
        static double? Number(JsonElement element, string name) => element.TryGetProperty(name, out var n) && n.TryGetDouble(out var d) ? d : null;
        var series = chart.TryGetProperty("series", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(one => new ChartSeries(CardString(one, "name") ?? string.Empty,
                one.TryGetProperty("points", out var p) && p.ValueKind == JsonValueKind.Array
                    ? [.. p.EnumerateArray().Select(point => new ChartPoint(Number(point, "x") ?? 0, Number(point, "y") ?? 0, CardString(point, "label"))).Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)).TakeLast(ChartData.MaxPoints)]
                    : [])).ToList()
            : [];
        return new ChartData(string.Equals(CardString(chart, "kind"), "bar", StringComparison.OrdinalIgnoreCase) ? ChartKind.Bar : ChartKind.Line,
            series, CardString(chart, "unit"), Number(chart, "min"), Number(chart, "max"));
    }

    /// <summary>The previous card as the plugin returned it (thumbnails become their count).</summary>
    private static object CardDto(PreviewCard card) => new
    {
        title = card.Title,
        subtitle = card.Subtitle,
        icon = card.Icon,
        status = card.Status.ToString().ToLowerInvariant(),
        items = card.Items.Select(item => item switch
        {
            CardValue v => (object)new { kind = "value", label = v.Label, value = v.Value, status = v.Status.ToString().ToLowerInvariant() },
            CardProgress p => new { kind = "progress", label = p.Label, fraction = p.Fraction, text = p.Text },
            CardText t => new { kind = "text", text = t.Text },
            CardChart c => new
            {
                kind = "chart",
                chart = new
                {
                    kind = c.Chart.Kind.ToString().ToLowerInvariant(),
                    unit = c.Chart.Unit,
                    min = c.Chart.Min,
                    max = c.Chart.Max,
                    series = c.Chart.Series.Select(s => new { name = s.Name, points = s.Points.Select(p => new { x = p.X, y = p.Y, label = p.Label }) }),
                },
            },
            CardThumbnails th => new { kind = "thumbnails", count = th.Images.Count },
            _ => new { kind = "unknown" },
        }),
        actions = card.Actions.Select(a => new { label = a.Label, target = a.Target }),
    };

    private static CardStatus Status(JsonElement element) => CardString(element, "status")?.ToLowerInvariant() switch
    {
        "ok" => CardStatus.Ok,
        "warning" => CardStatus.Warning,
        "error" => CardStatus.Error,
        _ => CardStatus.None,
    };

    private static string? CardString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static async Task<ReadOnlyMemory<byte>?> ReadImageAsync(string path, IPluginHost host, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await host.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxTileImageBytes + 1];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return read <= MaxTileImageBytes ? buffer.AsMemory(0, read) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PluginPermissionException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Forwards a message from a web preview page (<c>c4k.request</c>) to the plugin's <c>preview.message</c> handler.</summary>
    public Task<JsonElement> MessageAsync(VfsEntry entry, string method, JsonElement parameters, IPluginHost host, CancellationToken cancellationToken) =>
        plugin.CallAsync("preview.message",
            new { preview = preview.Id, entry = ExternalPlugin.EntryDto(entry), method, @params = parameters, context = ExternalPlugin.Context(host) },
            cancellationToken, TimeSpan.FromSeconds(30));

    /// <summary><c>{ "kind": "web", "page": "index.html", "state"?: any }</c>; the page must be a file inside the preview's <c>webRoot</c>.</summary>
    internal static PreviewContent ParseWeb(JsonElement result, string pluginId, string pluginFolder, string? webRoot)
    {
        if (webRoot is null)
        {
            return new MessagePreview("This preview returned kind \"web\" but declares no webRoot in plugin.json.");
        }

        var page = result.TryGetProperty("page", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
        var root = Path.GetFullPath(Path.Combine(pluginFolder, webRoot));
        var full = PluginAuthoring.IsSafeRelativePath(page) ? Path.GetFullPath(Path.Combine(root, page)) : null;
        if (full is null || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return new MessagePreview("Web page not found inside webRoot: " + page);
        }

        var state = result.TryGetProperty("state", out var s) && s.ValueKind != JsonValueKind.Undefined && s.ValueKind != JsonValueKind.Null ? JsonSerializer.Serialize(s) : null;
        return new WebPreview(pluginId, root, Path.GetRelativePath(root, full).Replace('\\', '/'), state);
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

