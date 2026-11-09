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
public sealed class ExternalPanelPlugin(ExternalPlugin plugin, PanelContribution panel) : IPanelCardPlugin
{
    /// <summary><c>&lt;pluginId&gt;/&lt;panelId&gt;</c>: a plugin may contribute several panels.</summary>
    public string Id => $"{plugin.Manifest.Id}/{panel.Id}";

    public string DisplayName => panel.Title;

    public PluginManifest Manifest => plugin.Manifest;

    public string Title => panel.Title;

    public string Description => panel.Description ?? plugin.Manifest.Description;

    public bool RequiresArgument => panel.RequiresArgument;

    /// <summary>The manifest declares <c>"card": true</c>: the plugin draws its own widget (<c>panel.card</c>).</summary>
    public bool HasCard => panel.Card;

    public IPanelSession CreateSession(PanelRequest request, IPluginHost host) => new Session(plugin, panel, host);

    /// <summary>
    /// <c>panel.card { panel, entry?, context, options?, previous? }</c>, answered like <c>preview.card</c>. Panels
    /// without <c>"card": true</c> get the plain card: the title and a button that opens the panel.
    /// </summary>
    public async Task<PreviewCard> LoadCardAsync(PreviewCardRequest request, IPluginHost host, CancellationToken cancellationToken)
    {
        if (!panel.Card)
        {
            return new PreviewCard(null, Description, "🧩", CardStatus.None, [], [new CardAction(Loc.T("Otwórz"), CardActions.OpenPanel(Id))]);
        }

        using var optionsDocument = request.OptionsJson is null ? null : JsonDocument.Parse(request.OptionsJson);
        var result = await plugin.CallAsync("panel.card", new
        {
            panel = panel.Id,
            entry = request.Target is { } entry ? ExternalPlugin.EntryDto(entry) : null,
            context = ExternalPlugin.Context(host),
            options = optionsDocument?.RootElement,
            previous = request.Previous is { } previous ? ExternalPreviewPlugin.CardDto(previous) : null,
        }, cancellationToken).ConfigureAwait(false);
        return await ExternalPreviewPlugin.ParseCardAsync(result, host, cancellationToken).ConfigureAwait(false);
    }

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

        public bool CanReceive => panel.Accepts;

        public bool CanProvide => panel.Provides;

        /// <remarks>Result: <c>[{ id, title, description?, extensions?, multiple?, folders?, returnsMessage? }]</c>; a repeated id keeps its first entry.</remarks>
        public async Task<IReadOnlyList<PanelReceiver>> GetReceiversAsync(IReadOnlyList<TransferItem> items, CancellationToken ct)
        {
            var result = await plugin.CallAsync("panel.receivers",
                new { panel = panel.Id, files = items.Select(FileDto).ToList(), context = ExternalPlugin.Context(host) },
                ct, QuestionTimeout).ConfigureAwait(false);
            return [.. Objects(result).Select(r => new PanelReceiver(
                RequiredString(r, "id"),
                RequiredString(r, "title"),
                OptionalString(r, "description"),
                r.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Array
                    ? [.. extensions.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                    : null,
                Flag(r, "multiple"),
                Flag(r, "folders"),
                Flag(r, "returnsMessage"))).DistinctBy(r => r.Id, StringComparer.Ordinal)];
        }

        /// <remarks>
        /// No time limit, only the token: uploads may take long. While the call runs, the plugin may read the offered
        /// files (folders: everything under them) even without <c>fs.read</c>; the grant ends with the call.
        /// Result: <c>{ text?, results?: [{ path, ok, error? }] }</c>.
        /// </remarks>
        public async Task<ReceiveResult> ReceiveAsync(string receiverId, IReadOnlyList<TransferItem> items, IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            var jobId = Guid.NewGuid().ToString("N");
            // Granted by the plugin itself, on the host that serves its requests, so the grant cannot miss them.
            using var grant = plugin.GrantRead(items);
            var registration = progress is null ? null : plugin.RegisterProgress(jobId, progress);
            try
            {
                var result = await plugin.CallAsync("panel.receive",
                    new { panel = panel.Id, receiver = receiverId, jobId, files = items.Select(FileDto).ToList(), context = ExternalPlugin.Context(host) },
                    ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (result.ValueKind != JsonValueKind.Object)
                {
                    return new ReceiveResult(null);
                }

                var results = result.TryGetProperty("results", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(ParseItemResult).ToList()
                    : null;
                return new ReceiveResult(OptionalString(result, "text"), results);
            }
            finally
            {
                registration?.Dispose();
            }
        }

        /// <remarks>Result: <c>[{ id, title, description? }]</c>; a repeated id keeps its first entry.</remarks>
        public async Task<IReadOnlyList<PanelProvider>> GetProvidersAsync(IReadOnlyList<string> rowIds, CancellationToken ct)
        {
            var result = await plugin.CallAsync("panel.providers",
                new { panel = panel.Id, rowIds, context = ExternalPlugin.Context(host) },
                ct, QuestionTimeout).ConfigureAwait(false);
            return [.. Objects(result).Select(r => new PanelProvider(RequiredString(r, "id"), RequiredString(r, "title"), OptionalString(r, "description")))
                .DistinctBy(p => p.Id, StringComparer.Ordinal)];
        }

        /// <remarks>
        /// No time limit, only the token. Result: <c>{ files: [{ name, text? | base64? | url? | error? }], text? }</c>;
        /// inline content is limited to <see cref="PanelTransferLimits.MaxInlineBytes"/> per file and the whole reply to
        /// <see cref="JsonRpcConnection.MaxMessageLength"/> characters; larger files come as an http(s) <c>url</c>.
        /// </remarks>
        public async Task<ProvideResult> ProvideAsync(string providerId, IReadOnlyList<string> rowIds, IProgress<TransferProgress>? progress, CancellationToken ct)
        {
            var jobId = Guid.NewGuid().ToString("N");
            using var registration = progress is null ? null : plugin.RegisterProgress(jobId, progress);
            var result = await plugin.CallAsync("panel.provide",
                new { panel = panel.Id, provider = providerId, jobId, rowIds, context = ExternalPlugin.Context(host) },
                ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object)
            {
                return new ProvideResult([]);
            }

            var files = result.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(ParseProvidedFile).ToList()
                : [];
            return new ProvideResult(files, OptionalString(result, "text"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Questions asked while a dialog waits (receivers, providers) give up quickly.</summary>
    private static readonly TimeSpan QuestionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Inline <c>text</c>/<c>base64</c> limit per provided file.</summary>
    public const int MaxInlineBytes = PanelTransferLimits.MaxInlineBytes;

    private static object FileDto(TransferItem item) => new
    {
        path = item.Path,
        name = item.Name,
        kind = item.Kind == TransferItemKind.Folder ? "folder" : "file",
        size = item.Size,
    };

    /// <summary>
    /// One entry of <c>panel.provide</c>'s <c>files</c>. Problems with one file become its <see cref="ProvidedFile.Error"/>
    /// (shown to the user), so the other files of the reply still come through.
    /// </summary>
    private static ProvidedFile ParseProvidedFile(JsonElement file, int index)
    {
        if (file.ValueKind != JsonValueKind.Object || OptionalString(file, "name") is not { Length: > 0 } name)
        {
            var placeholder = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new ProvidedFile(placeholder, Error: Loc.T("Wtyczka nie podała nazwy pliku nr {0}.", placeholder));
        }

        // The plugin's own per-file failure (a download that failed, a row without data): shown, nothing written.
        if (OptionalString(file, "error") is { Length: > 0 } error)
        {
            return new ProvidedFile(name, Error: error);
        }

        if (OptionalString(file, "url") is { } url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? new ProvidedFile(name, Url: uri)
                : new ProvidedFile(name, Error: Loc.T("Plik „{0}”: dozwolone są tylko adresy http i https.", name));
        }

        if (OptionalString(file, "base64") is { } base64)
        {
            // Checked before decoding, so an oversized reply never allocates the decoded copy.
            if (base64.Length / 4 * 3 > MaxInlineBytes)
            {
                return TooLarge(name);
            }

            try
            {
                return new ProvidedFile(name, Data: Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return new ProvidedFile(name, Error: Loc.T("Plik „{0}”: uszkodzona treść base64.", name));
            }
        }

        if (OptionalString(file, "text") is { } text)
        {
            return System.Text.Encoding.UTF8.GetByteCount(text) <= MaxInlineBytes ? new ProvidedFile(name, Text: text) : TooLarge(name);
        }

        return new ProvidedFile(name, Error: Loc.T("Plik „{0}”: wtyczka nie podała treści (text, base64 ani url).", name));
    }

    private static ProvidedFile TooLarge(string name) =>
        new(name, Error: Loc.T("Plik „{0}” przekracza {1} MB; wtyczka powinna podać go jako adres url.", name, MaxInlineBytes / (1024 * 1024)));

    /// <summary>An entry without <c>path</c> does not fail the whole receive: it becomes a failed result.</summary>
    private static TransferItemResult ParseItemResult(JsonElement entry) =>
        OptionalString(entry, "path") is { } path
            ? new TransferItemResult(path, entry.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, OptionalString(entry, "error"))
            : new TransferItemResult(string.Empty, false, OptionalString(entry, "error") ?? Loc.T("Wtyczka zwróciła wynik bez ścieżki pliku."));

    private static IEnumerable<JsonElement> Objects(JsonElement result) =>
        result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : [];

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"Plugin result is missing the string '{name}'.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

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
public sealed class ExternalPreviewPlugin(ExternalPlugin plugin, PreviewContribution preview) : IPreviewCardPlugin, IInteractivePreviewPlugin, IFolderSuggestingPreview, Folders.IAskingFolderSuggester
{
    private const int MaxCardImages = 4;

    /// <summary>How long a <c>preview.suggest</c> answer may take, process start included.</summary>
    public static readonly TimeSpan SuggestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The manifest rule decides alone; only with <c>ask</c> does a matching folder reach the plugin (<c>preview.suggest</c>).</summary>
    public bool AsksPlugin => preview.Suggest?.Ask == true;

    /// <remarks>
    /// <c>preview.suggest</c> gets <c>{ preview, folder, entries, truncated, context }</c> and answers
    /// <c>{ score, message, options? }</c> or <c>null</c>. Plugin errors and malformed answers mean "no suggestion".
    /// </remarks>
    public async Task<FolderSuggestion?> SuggestAsync(FolderSnapshot folder, IPluginHost host, CancellationToken cancellationToken)
    {
        if (preview.Suggest is not { } rule || Folders.FolderSuggestionRules.Match(rule, folder) is not { } count)
        {
            return null;
        }

        var view = new FolderSuggestionView(Plugin: plugin.Manifest.Id, Preview: preview.Id);
        if (!rule.Ask)
        {
            return new FolderSuggestion(0.7, Folders.FolderSuggestionRules.MessageFor(rule, count, Loc.Language, preview.Title), view);
        }

        try
        {
            var result = await plugin.CallAsync("preview.suggest", new
            {
                preview = preview.Id,
                folder = ExternalPlugin.EntryDto(folder.Folder),
                entries = folder.Entries.Select(ExternalPlugin.EntryDto).ToArray(),
                truncated = folder.Truncated,
                context = ExternalPlugin.Context(host),
            }, cancellationToken, SuggestTimeout).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("score", out var score) || !score.TryGetDouble(out var value)
                || CardString(result, "message") is not { Length: > 0 } message)
            {
                return null;
            }

            var options = result.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o.GetRawText() : null;
            return new FolderSuggestion(Math.Clamp(value, 0, 1), message, view with { OptionsJson = options });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null; // A failing plugin only loses its own suggestion.
        }
    }

    public string Id => $"{plugin.Manifest.Id}/{preview.Id}";

    public string DisplayName => preview.Title;

    /// <summary>The manifest's <c>contributes.previews[]</c> entry (extensions, card support…).</summary>
    public PreviewContribution Contribution => preview;

    public PluginManifest Manifest => plugin.Manifest;

    public int Priority => preview.Priority;

    /// <summary><c>"enter": true</c>: Enter on a matching file opens this preview pinned over the panel.</summary>
    public bool OpensOnEnter => preview.Enter;

    public bool CanPreview(VfsEntry entry) =>
        entry.IsContainer
            ? entry.Kind != VfsEntryKind.ParentLink && preview.Extensions.Contains("/")
            : preview.Extensions.Any(pattern => Matches(pattern, entry.Name));

    /// <summary>
    /// <c>.csv</c> = extension; anything else is a file name or a pattern with <c>*</c>/<c>?</c> (<c>package.json</c>,
    /// <c>requirements*.txt</c>, <c>*.openapi.json</c>), so a preview can claim specific files without a whole extension.
    /// </summary>
    public static bool Matches(string pattern, string name) => Folders.FolderSuggestionRules.MatchesPattern(pattern, name);

    public Task<PreviewContent> LoadAsync(VfsEntry entry, IPluginHost host, IProgress<PreviewContent>? progress, CancellationToken cancellationToken) =>
        LoadWithOptionsAsync(entry, host, null, pinned: false, cancellationToken);

    private const int MaxTileImageBytes = 4 * 1024 * 1024;
    private const int MaxTiles = 1000;
    private const int MaxActions = 20;
    private const int MaxFormFields = 50;
    private const int MaxFormFiles = 200;

    /// <param name="options">JSON object from the folder's <c>.c4k</c> item, passed as <c>options</c>.</param>
    public Task<PreviewContent> LoadWithOptionsAsync(VfsEntry entry, IPluginHost host, string? options, CancellationToken cancellationToken) =>
        LoadWithOptionsAsync(entry, host, options, pinned: false, cancellationToken);

    /// <param name="options">JSON object from the folder's <c>.c4k</c> item, passed as <c>options</c>.</param>
    /// <param name="pinned"><c>true</c> for a view opened with Enter (it stays on this entry), <c>false</c> for the quick view that follows the cursor.</param>
    public async Task<PreviewContent> LoadWithOptionsAsync(VfsEntry entry, IPluginHost host, string? options, bool pinned, CancellationToken cancellationToken)
    {
        using var optionsDocument = options is null ? null : JsonDocument.Parse(options);
        var result = await plugin.CallAsync("preview.render",
            new { preview = preview.Id, entry = ExternalPlugin.EntryDto(entry), context = ExternalPlugin.Context(host), options = optionsDocument?.RootElement, pinned },
            cancellationToken).ConfigureAwait(false);
        return await ParseRenderAsync(result, entry, host, cancellationToken, WebContext()).ConfigureAwait(false);
    }

    private (string PluginId, string Folder, string? WebRoot) WebContext() => (plugin.Manifest.Id, plugin.Folder, preview.WebRoot);

    /// <summary><c>preview.action</c>: a <c>c4k:plugin/…</c> button; the plugin returns <c>null</c> (render again) or a render result.</summary>
    public async Task<PreviewContent?> InvokeActionAsync(PreviewActionRequest request, IPluginHost host, CancellationToken cancellationToken)
    {
        using var optionsDocument = request.OptionsJson is null ? null : JsonDocument.Parse(request.OptionsJson);
        var result = await plugin.CallAsync("preview.action", new
        {
            preview = preview.Id,
            entry = ExternalPlugin.EntryDto(request.Entry),
            options = optionsDocument?.RootElement,
            action = request.Action,
            args = request.Arguments,
            values = request.FormValues,
        }, cancellationToken).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object
            ? await ParseRenderAsync(result, request.Entry, host, cancellationToken, WebContext()).ConfigureAwait(false)
            : null;
    }

    /// <summary><c>preview.formChanged</c>: the form's current values after the user paused typing.</summary>
    public async Task FormChangedAsync(VfsEntry entry, string? optionsJson, IReadOnlyDictionary<string, string> values, IPluginHost host, CancellationToken cancellationToken)
    {
        using var optionsDocument = optionsJson is null ? null : JsonDocument.Parse(optionsJson);
        await plugin.CallAsync("preview.formChanged",
            new { preview = preview.Id, entry = ExternalPlugin.EntryDto(entry), options = optionsDocument?.RootElement, values },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A <c>preview.render</c> (or <c>preview.action</c>) result:
    /// <c>{ "kind": "markdown" | "text", "content": "...", "editable": false }</c>,
    /// <c>{ "kind": "tiles", "title", "description"?, "tileWidth"?, "tiles": [{ "title", "subtitle"?, "icon"?, "image"?, "target"? }] }</c>,
    /// <c>{ "kind": "chart", … }</c> or
    /// <c>{ "kind": "form", "title", "fields": [{ id, label, value, multiline? }], "files": [{ name, path, size }], "buttons": [{ label, target, shortcut? }], "message"?, "messageError"? }</c>.
    /// Any kind may add <c>"actions": [{ label, target, shortcut? }]</c>, shown as a bar above it. Unknown kinds are text.
    /// <c>"path"</c> (an absolute path other than the entry's) says the plugin renamed the entry or the view is now about
    /// another file: the result becomes a <see cref="MovedPreview"/> and the host follows that path.
    /// A tile's <c>image</c> is a path the host reads with the plugin's own fs.read permission (skipped without a host).
    /// </summary>
    public static async Task<PreviewContent> ParseRenderAsync(JsonElement result, VfsEntry entry, IPluginHost? host, CancellationToken cancellationToken, (string PluginId, string Folder, string? WebRoot)? web = null)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return new TextPreview(string.Empty, false);
        }

        var kind = CardString(result, "kind") ?? "text";
        PreviewContent content;
        if (string.Equals(kind, "tiles", StringComparison.OrdinalIgnoreCase))
        {
            content = await ParseTilesAsync(result, host, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(kind, "web", StringComparison.OrdinalIgnoreCase) && web is { } site)
        {
            content = ParseWeb(result, site.PluginId, site.Folder, site.WebRoot);
        }
        else if (string.Equals(kind, "chart", StringComparison.OrdinalIgnoreCase) && result.TryGetProperty("chart", out var chart))
        {
            content = new ChartPreview(CardString(result, "title") ?? entry.Name, ParseChart(chart), CardString(result, "description"));
        }
        else if (string.Equals(kind, "form", StringComparison.OrdinalIgnoreCase))
        {
            content = ParseForm(result, entry);
        }
        else if (string.Equals(kind, "list", StringComparison.OrdinalIgnoreCase))
        {
            content = ParseList(result, entry);
        }
        else if (string.Equals(kind, "mail", StringComparison.OrdinalIgnoreCase))
        {
            content = ParseMail(result, entry);
        }
        else
        {
            var text = result.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
            var editable = result.TryGetProperty("editable", out var e) && e.ValueKind == JsonValueKind.True;
            content = string.Equals(kind, "markdown", StringComparison.OrdinalIgnoreCase)
                ? new MarkdownPreview(text, editable)
                : new TextPreview(text, editable);
        }

        var actions = ParseActions(result, "actions");
        PreviewContent shown = actions.Count > 0 ? new ActionsPreview(content, actions) : content;
        return CardString(result, "path") is { } path && IsOtherEntry(path, entry)
            ? new MovedPreview(shown, new VfsEntry(path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)[^1], path, VfsEntryKind.File))
            : shown;
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

    /// <summary>A <c>path</c> in a result that is not the entry being shown and passes <see cref="PreviewMoves.IsAcceptable"/>.</summary>
    private static bool IsOtherEntry(string path, VfsEntry entry) =>
        !string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase) && PreviewMoves.IsAcceptable(entry.Path, path);

    private static FormPreview ParseForm(JsonElement result, VfsEntry entry)
    {
        var fields = result.TryGetProperty("fields", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()
                .Where(f => CardString(f, "id") is not null)
                .Take(MaxFormFields)
                .Select(f => new FormField(CardString(f, "id")!, CardString(f, "label") ?? string.Empty,
                    f.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty,
                    f.TryGetProperty("multiline", out var m) && m.ValueKind == JsonValueKind.True,
                    f.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.True))]
            : new List<FormField>();
        var files = result.TryGetProperty("files", out var attached) && attached.ValueKind == JsonValueKind.Array
            ? [.. attached.EnumerateArray()
                .Where(f => CardString(f, "path") is not null)
                .Take(MaxFormFiles)
                .Select(f => new FormFile(CardString(f, "name") ?? Path.GetFileName(CardString(f, "path")!), CardString(f, "path")!,
                    f.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0))]
            : new List<FormFile>();
        return new FormPreview(CardString(result, "title") ?? entry.Name, fields, files, ParseActions(result, "buttons"),
            CardString(result, "message"), result.TryGetProperty("messageError", out var error) && error.ValueKind == JsonValueKind.True);
    }

    /// <summary><c>[{ label, target, shortcut? }]</c>; entries without a target are dropped, reserved or invalid shortcuts (<see cref="PreviewShortcuts"/>) too.</summary>
    private static List<PreviewAction> ParseActions(JsonElement result, string name) =>
        result.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()
                .Where(a => CardString(a, "target") is not null)
                .Take(MaxActions)
                .Select(a => new PreviewAction(CardString(a, "label") ?? CardString(a, "target")!, CardString(a, "target")!,
                    PreviewShortcuts.Normalize(CardString(a, "shortcut")) is null ? null : CardString(a, "shortcut"),
                    CardString(a, "icon"), CardString(a, "style")))]
            : [];

    private const int MaxListItems = 2000;
    private const int MaxListTabs = 40;
    private const int MaxMailAttachments = 100;
    private const int MaxMailText = 2_000_000;

    /// <summary>
    /// <c>{ "kind": "list", "folder", "items": [{ path, title, subtitle?, snippet?, meta?, group?, tags? }], "title"?,
    /// "tabs"?: [{ label, target, count?, selected?, icon? }], "filters"?: [{ tag, label }], "search"?, "status"?,
    /// "statusRight"?, "selected"?, "empty"?, "badge"? }</c>. Rows must lie in <c>folder</c>; others are dropped.
    /// </summary>
    internal static PreviewContent ParseList(JsonElement result, VfsEntry entry)
    {
        var folder = CardString(result, "folder") ?? entry.Path;
        var items = result.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()
                .Where(i => CardString(i, "path") is { } path && IsUnderFolder(path, folder))
                .Take(MaxListItems)
                .Select(i => new ListPreviewItem(CardString(i, "path")!, CardString(i, "title") ?? Path.GetFileName(CardString(i, "path")!),
                    CardString(i, "subtitle"), CardString(i, "snippet"), CardString(i, "meta"), CardString(i, "group"), Strings(i, "tags")))]
            : new List<ListPreviewItem>();
        var tabs = result.TryGetProperty("tabs", out var tabList) && tabList.ValueKind == JsonValueKind.Array
            ? [.. tabList.EnumerateArray()
                .Where(t => CardString(t, "target") is not null)
                .Take(MaxListTabs)
                .Select(t => new ListPreviewTab(CardString(t, "label") ?? CardString(t, "target")!, CardString(t, "target")!,
                    t.TryGetProperty("count", out var count) && count.TryGetInt32(out var n) ? n : null,
                    t.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True, CardString(t, "icon")))]
            : new List<ListPreviewTab>();
        var filters = result.TryGetProperty("filters", out var filterList) && filterList.ValueKind == JsonValueKind.Array
            ? [.. filterList.EnumerateArray()
                .Where(f => CardString(f, "label") is not null)
                .Take(8)
                .Select(f => new ListPreviewFilter(f.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() ?? string.Empty : string.Empty, CardString(f, "label")!))]
            : new List<ListPreviewFilter>();
        var more = result.TryGetProperty("more", out var m) && CardString(m, "target") is { } moreTarget
            ? new PreviewAction(CardString(m, "label") ?? moreTarget, moreTarget)
            : null;
        return new ListPreview(folder, items, CardString(result, "title"), tabs, filters, CardString(result, "search"), CardString(result, "status"),
            CardString(result, "statusRight"), CardString(result, "selected"), CardString(result, "empty"), CardString(result, "badge"),
            more, CardString(result, "searchTarget"), result.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null);
    }

    /// <summary>
    /// <c>{ "kind": "mail", "subject", "from", "fromAddress"?, "to"?, "date"?, "attachments"?: [{ name, size?, target? }],
    /// "body", "source"?, "folder"?, "quickReply"?: { target, placeholder?, from? }, "status"?, "badge"?, "note"? }</c>.
    /// </summary>
    internal static MailPreview ParseMail(JsonElement result, VfsEntry entry)
    {
        static string Clip(string? text) => text is null ? string.Empty : text.Length > MaxMailText ? text[..MaxMailText] + "\n[…]" : text;
        var attachments = result.TryGetProperty("attachments", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray()
                .Where(a => CardString(a, "name") is not null)
                .Take(MaxMailAttachments)
                .Select(a => new MailPreviewAttachment(CardString(a, "name")!, CardString(a, "size"), CardString(a, "target")))]
            : new List<MailPreviewAttachment>();
        var reply = result.TryGetProperty("quickReply", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        return new MailPreview(CardString(result, "subject") ?? entry.Name, CardString(result, "from") ?? string.Empty, CardString(result, "fromAddress"),
            CardString(result, "to"), CardString(result, "date"), attachments, Clip(CardString(result, "body")),
            CardString(result, "source") is { } source ? Clip(source) : null, CardString(result, "folder"),
            CardString(reply, "target"), CardString(reply, "placeholder"), CardString(reply, "from"),
            CardString(result, "status"), CardString(result, "badge"), CardString(result, "note"));
    }

    private static List<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(16)]
            : [];

    /// <summary>A path inside <paramref name="folder"/> or its subfolders (the list moves the panel there), never outside or with <c>..</c>.</summary>
    private static bool IsUnderFolder(string path, string folder)
    {
        var root = folder.TrimEnd('\\', '/');
        return path.Length > root.Length + 1 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] is '\\' or '/'
            && !path[(root.Length + 1)..].Split('\\', '/').Any(part => part is "" or "." or "..");
    }

    /// <summary>
    /// <c>preview.card</c> with the same parameters as <c>preview.render</c> plus <c>previous</c> (the last card, so a
    /// refresh can extend a chart). Previews that do not declare <c>"card": true</c> get the plain card: title and Open.
    /// </summary>
    /// <summary>The preview answers <c>preview.setup</c> (manifest <c>"setup": true</c>).</summary>
    public bool SupportsSetup => preview.Setup;

    /// <summary>
    /// <c>preview.setup</c> for a folder that is getting this preview: the plugin asks the user what it needs (through
    /// ui.prompt, ui.confirm…) and answers <c>{ options }</c> for the folder's <c>.c4k</c> item, or <c>null</c> when
    /// the user cancelled. Interactive, so the call may take as long as the user does (up to 15 minutes).
    /// </summary>
    /// <returns>The options as JSON text; <c>null</c> = cancelled.</returns>
    public async Task<string?> SetupAsync(string folder, IPluginHost host, CancellationToken cancellationToken)
    {
        var result = await plugin.CallAsync("preview.setup", new
        {
            preview = preview.Id,
            folder,
            context = ExternalPlugin.Context(host),
        }, cancellationToken, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return result.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object ? options.GetRawText() : "{}";
    }

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
    internal static object CardDto(PreviewCard card) => new
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

    private static async Task<TilesPreview> ParseTilesAsync(JsonElement result, IPluginHost? host, CancellationToken cancellationToken)
    {
        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

        var tiles = new List<PreviewTile>();
        if (result.TryGetProperty("tiles", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray().Take(MaxTiles))
            {
                ReadOnlyMemory<byte>? image = null;
                if (host is not null && Str(item, "image") is { } imagePath)
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
