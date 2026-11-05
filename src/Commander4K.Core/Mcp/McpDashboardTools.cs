using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Commander4K.Core.Dashboard;
using Commander4K.Core.Folders;
using Commander4K.Core.Vfs;

namespace Commander4K.Core.Mcp;

/// <summary>A preview that can draw dashboard cards, as the dashboard tools describe it to the model.</summary>
/// <param name="Card">The name a tile's <c>card</c> uses: the plugin id, or <c>plugin/preview</c>.</param>
/// <param name="Extensions">What the preview shows (".json", "*.yaml", "folders"…), so the model knows what <c>open</c> may point at.</param>
public sealed record McpCardPreview(string Card, string Name, string? Description, IReadOnlyList<string> Extensions);

/// <summary>
/// MCP tools that let an AI client design <c>c4k.dir</c> dashboards: a guide with every card type and the design rules,
/// reading the current dashboard, checking a draft (errors, warnings and a picture of the grid at several widths)
/// and saving it. Saving goes through the dispatcher's confirmation like every write, and never trusts the file:
/// cards that run commands, sign in over SSH or send POST requests stay locked until the user allows them.
/// </summary>
public static class McpDashboardTools
{
    private const int MaxConfigBytes = 256 * 1024;

    /// <summary>Built-in card names (folder views, API/SSH, widgets) a tile's <c>card</c> may use besides plugin ids.</summary>
    public static readonly IReadOnlyList<string> BuiltInCards =
        [FolderViewIds.Files, FolderViewIds.Gallery, FolderViewIds.Notes, FolderViewIds.Markdown, FolderViewIds.Dashboard, FolderViewIds.Api, FolderViewIds.Ssh, "clock", "note", "disks"];

    private static readonly string[] FolderCards = [FolderViewIds.Files, FolderViewIds.Gallery, FolderViewIds.Notes, FolderViewIds.Markdown, FolderViewIds.Dashboard];

    /// <param name="cardPreviews">The installed previews that draw cards, read on every call (plugins come and go).</param>
    public static IReadOnlyList<McpTool> Create(VfsRouter router, Func<IReadOnlyList<McpCardPreview>> cardPreviews) =>
    [
        new McpTool(
            new McpToolDefinition(
                "dashboard_guide",
                "How to build a Commander4K folder dashboard (a c4k.dir file): tile fields, the six tile sizes and how the grid places them, "
                + "every card type with its options (folder views, API, SSH, clock, note, disks, and the cards of the installed plugins), "
                + "section headings, KPI tiles, design rules for a clean, professional dashboard and complete examples. Read it before writing a dashboard.",
                McpSchema.Object(),
                McpToolMode.Allow),
            (_, _) => Task.FromResult(new McpToolResult(Guide(cardPreviews()))),
            _ => "dashboard_guide"),
        new McpTool(
            new McpToolDefinition(
                "get_dashboard",
                "Reads the c4k.dir of a folder: its full JSON (keep the keys you do not change), its dashboards with every tile, "
                + "problems found in it and a picture of the grid. Returns a note when the folder has no c4k.dir yet.",
                McpSchema.Object(("folder", "string", "The folder (disk, share, sftp://…).", true)),
                McpToolMode.Allow,
                PathArguments: ["folder"]),
            (call, ct) => GetAsync(router, call.Required("folder"), cardPreviews(), ct),
            args => "get_dashboard " + args["folder"]),
        new McpTool(
            new McpToolDefinition(
                "check_dashboard",
                "Checks a draft c4k.dir without saving it: JSON and tile errors, unknown or missing card plugins, cards that will wait for the "
                + "user's trust, design warnings, and how the grid lays the tiles out at 2, 3 and 4 columns. Fix every error before save_dashboard.",
                McpSchema.Object(
                    ("folder", "string", "The folder the file is for (relative paths in open/image are resolved against it).", true),
                    ("config", "string", "The complete c4k.dir JSON text.", true)),
                McpToolMode.Allow,
                PathArguments: ["folder"]),
            (call, _) => Task.FromResult(Check(call.Required("folder"), call.Required("config"), cardPreviews())),
            args => "check_dashboard " + args["folder"]),
        new McpTool(
            new McpToolDefinition(
                "save_dashboard",
                "Writes c4k.dir into the folder (replacing the existing one) after the same checks as check_dashboard; refuses a file with errors. "
                + "Keep the keys of the existing file you did not mean to change (read it with get_dashboard first). The panel shows the new "
                + "dashboard the next time the folder is previewed or entered (use navigate to show it). Cards that run commands, use SSH or "
                + "send POST requests stay locked until the user presses \"Zezwól\".",
                McpSchema.Object(
                    ("folder", "string", "The folder (disk, share, sftp://…).", true),
                    ("config", "string", "The complete c4k.dir JSON text.", true)),
                McpToolMode.Ask,
                PathArguments: ["folder"],
                Writes: true),
            (call, ct) => SaveAsync(router, call.Required("folder"), call.Required("config"), cardPreviews(), ct),
            args => "save_dashboard " + args["folder"],
            args => args["config"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null),
    ];

    /// <summary>Errors (the file cannot be saved), warnings and the layout pictures of a draft.</summary>
    public sealed record DashboardReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string Layout);

    public static DashboardReport Analyze(FolderConfig config, IReadOnlyList<McpCardPreview> previews)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (config.Error is { } fileError)
        {
            errors.Add(fileError);
            return new DashboardReport(errors, warnings, string.Empty);
        }

        var layout = new StringBuilder();
        var dashboards = Dashboards(config).ToList();
        if (dashboards.Count == 0)
        {
            warnings.Add("Neither \"preview\" nor \"enter\" is a dashboard (\"view\": \"dashboard\" with \"tiles\").");
        }

        foreach (var (section, tiles) in dashboards)
        {
            if (tiles.Count == 0)
            {
                warnings.Add($"{section}: the dashboard has no tiles.");
                continue;
            }

            var titles = new HashSet<string>(StringComparer.Ordinal);
            var headings = 0;
            for (var i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                var where = $"{section} tile {i + 1} \"{tile.Title}\"";
                if (!titles.Add(tile.Title))
                {
                    warnings.Add($"{where}: the title repeats; the app finds tiles by title when it edits them, so make titles unique.");
                }

                if (tile.Heading)
                {
                    headings++;
                    continue;
                }

                if (tile.Error is { } tileError)
                {
                    errors.Add($"{where}: {tileError}");
                }

                CheckTile(tile, where, previews, errors, warnings);
            }

            if (tiles.Count > 24)
            {
                warnings.Add($"{section}: {tiles.Count} tiles is a lot for one screen; split it into dashboards in subfolders and link them with \"card\": \"dashboard\" tiles.");
            }
            else if (tiles.Count - headings > 8 && headings == 0)
            {
                warnings.Add($"{section}: {tiles.Count} tiles without a section heading; group them with \"heading\": true tiles.");
            }

            layout.Append(Picture(section, tiles));
        }

        return new DashboardReport(errors, warnings, layout.ToString());
    }

    private static void CheckTile(FolderTile tile, string where, IReadOnlyList<McpCardPreview> previews, List<string> errors, List<string> warnings)
    {
        if (tile.Card is null)
        {
            if (tile.Open is null && tile.Url is null && tile.Run is null)
            {
                warnings.Add($"{where}: a plain tile without open, url or run does nothing when clicked; make it a card or a heading.");
            }

            if (tile.Run is not null)
            {
                warnings.Add($"{where}: \"run\" works only after the user trusts this c4k.dir (they are asked on first use).");
            }

            return;
        }

        var card = tile.Card;
        if (tile.Refresh is { } refresh && refresh < TimeSpan.FromSeconds(10))
        {
            warnings.Add($"{where}: refresh every {refresh.TotalSeconds:0} s is aggressive; 30s–5m suits most cards.");
        }

        if (string.Equals(card, FolderTile.AutoCard, StringComparison.Ordinal) || FolderCards.Contains(card, StringComparer.OrdinalIgnoreCase))
        {
            if (tile.Open is null)
            {
                errors.Add($"{where}: card \"{card}\" needs \"open\" with the path it shows.");
            }

            return;
        }

        switch (card.ToLowerInvariant())
        {
            case FolderViewIds.Api:
                if (tile.Api?.HasSideEffects == true)
                {
                    warnings.Add($"{where}: a {tile.Api.Method} request waits until the user trusts this c4k.dir; prefer GET for read-only cards.");
                }

                return;
            case FolderViewIds.Ssh:
                if (tile.Open is null || !tile.Open.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{where}: card \"ssh\" needs \"open\" with an sftp:// path.");
                }

                warnings.Add($"{where}: SSH cards wait until the user trusts this c4k.dir.");
                return;
            case "clock" or "disks":
                return;
            case "note":
                if (tile.Open is null && !(tile.Options?.Contains("\"text\"", StringComparison.Ordinal) ?? false))
                {
                    errors.Add($"{where}: card \"note\" needs options.text or open with a text file.");
                }

                return;
        }

        var pluginId = card.Split('/')[0];
        if (!previews.Any(p => string.Equals(p.Card.Split('/')[0], pluginId, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"{where}: no installed preview plugin \"{pluginId}\"; the tile shows an install button (official catalog) or a link to the plugin manager.");
        }
    }

    /// <summary>The dashboards a file defines: (section, tiles); a tile-less dashboard shares the other section's tiles.</summary>
    public static IEnumerable<(string Section, IReadOnlyList<FolderTile> Tiles)> Dashboards(FolderConfig config)
    {
        var preview = config.Preview is { View: FolderViewIds.Dashboard } p ? p : null;
        var enter = config.Enter is { View: FolderViewIds.Dashboard } e ? e : null;

        // A tile-less dashboard shares the other one's tiles: report those tiles once.
        if (enter is not null)
        {
            var shared = preview is not null && (preview.Tiles.Count == 0 || enter.Tiles.Count == 0 || preview.Tiles.SequenceEqual(enter.Tiles));
            yield return (shared ? "enter + preview" : "enter", enter.Tiles.Count > 0 ? enter.Tiles : preview?.Tiles ?? []);
            if (shared)
            {
                yield break;
            }
        }

        if (preview is not null)
        {
            yield return ("preview", preview.Tiles);
        }
    }

    /// <summary>The grid at 4, 3 and 2 columns, one letter per tile (A, B, …; headings as ═), with a legend.</summary>
    public static string Picture(string section, IReadOnlyList<FolderTile> tiles)
    {
        var text = new StringBuilder();
        text.Append("Layout of ").Append(section).AppendLine(":");
        for (var i = 0; i < tiles.Count; i++)
        {
            var size = tiles[i].Heading ? "heading" : TileSizes.Name(tiles[i].Size);
            text.Append("  ").Append(Letter(i)).Append(" = ").Append(tiles[i].Title).Append(" (").Append(size)
                .Append(tiles[i].Card is { } card ? ", card " + card : string.Empty).AppendLine(")");
        }

        var sizes = tiles.Select(t => t.LayoutSize).ToList();
        foreach (var columns in new[] { 4, 3, 2 })
        {
            var placements = TileGridLayout.Place(sizes, columns);
            var rows = TileGridLayout.RowCount(placements);
            var grid = new char[rows, columns];
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    grid[r, c] = '·';
                }
            }

            for (var i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                for (var r = p.Row; r < p.Row + p.RowSpan; r++)
                {
                    for (var c = p.Column; c < p.Column + p.ColumnSpan; c++)
                    {
                        grid[r, c] = tiles[i].Heading ? '═' : Letter(i);
                    }
                }
            }

            var empty = 0;
            text.Append(columns).AppendLine(" columns (panel ≈ " + (columns * 252) + " px):");
            for (var r = 0; r < rows; r++)
            {
                text.Append("  ");
                for (var c = 0; c < columns; c++)
                {
                    text.Append(grid[r, c]).Append(' ');
                    empty += grid[r, c] == '·' ? 1 : 0;
                }

                text.AppendLine();
            }

            if (empty > 0)
            {
                text.Append("  ").Append(empty).AppendLine(" empty cell(s) (·): reorder tiles or change sizes to close the gaps.");
            }
        }

        return text.ToString();
    }

    private static char Letter(int index) => index < 26 ? (char)('A' + index) : index < 52 ? (char)('a' + index - 26) : '#';

    private static McpToolResult Check(string folder, string json, IReadOnlyList<McpCardPreview> previews)
    {
        var config = FolderConfig.Parse(folder, json);
        return new McpToolResult(Format(Analyze(config, previews), saved: null));
    }

    private static string Format(DashboardReport report, string? saved)
    {
        var text = new StringBuilder();
        if (saved is not null)
        {
            text.AppendLine(saved);
        }

        text.AppendLine(report.Errors.Count == 0 ? "No errors." : $"{report.Errors.Count} error(s):");
        foreach (var error in report.Errors)
        {
            text.Append("- ").AppendLine(error);
        }

        if (report.Warnings.Count > 0)
        {
            text.AppendLine($"{report.Warnings.Count} warning(s):");
            foreach (var warning in report.Warnings)
            {
                text.Append("- ").AppendLine(warning);
            }
        }

        text.Append(report.Layout);
        return text.ToString();
    }

    private static async Task<McpToolResult> GetAsync(VfsRouter router, string folder, IReadOnlyList<McpCardPreview> previews, CancellationToken ct)
    {
        var file = FolderConfig.ChildPath(folder, FolderConfig.FileName);
        var json = await ReadAsync(router, file, ct).ConfigureAwait(false);
        if (json is null)
        {
            return new McpToolResult($"{folder} has no {FolderConfig.FileName} yet. Read dashboard_guide, then write one with save_dashboard.");
        }

        var report = Analyze(FolderConfig.Parse(folder, json), previews);
        return new McpToolResult($"{file}:\n```json\n{json.TrimEnd()}\n```\n" + Format(report, saved: null));
    }

    private static async Task<McpToolResult> SaveAsync(VfsRouter router, string folder, string json, IReadOnlyList<McpCardPreview> previews, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
        {
            return McpToolResult.Error($"The file is larger than {MaxConfigBytes / 1024} KB.");
        }

        var config = FolderConfig.Parse(folder, json);
        var report = Analyze(config, previews);
        if (report.Errors.Count > 0)
        {
            return McpToolResult.Error("Not saved.\n" + Format(report, saved: null));
        }

        var entry = await router.GetEntryAsync(folder, ct).ConfigureAwait(false);
        if (entry is not { IsContainer: true })
        {
            return McpToolResult.Error($"{folder} is not a folder.");
        }

        var file = FolderConfig.ChildPath(folder, FolderConfig.FileName);
        var text = Pretty(json) + "\n";
        await using (var stream = await router.OpenWriteAsync(file, overwrite: true, ct).ConfigureAwait(false))
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        return new McpToolResult(Format(report, $"Saved {file}."));
    }

    /// <summary>Indented JSON, so the user can read and edit the file; text with comments is kept as written.</summary>
    private static string Pretty(string json)
    {
        try
        {
            return JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException)
        {
            return json.TrimEnd();
        }
    }

    private static async Task<string?> ReadAsync(VfsRouter router, string file, CancellationToken ct)
    {
        if (await router.GetEntryAsync(file, ct).ConfigureAwait(false) is not { } entry)
        {
            return null;
        }

        if (entry.Size > MaxConfigBytes)
        {
            throw new IOException($"{file} is larger than {MaxConfigBytes / 1024} KB.");
        }

        await using var stream = await router.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The guide the model reads before designing a dashboard.</summary>
    public static string Guide(IReadOnlyList<McpCardPreview> previews)
    {
        var text = new StringBuilder(GuideText);
        text.AppendLine();
        text.AppendLine("## Cards of the installed preview plugins");
        if (previews.Count == 0)
        {
            text.AppendLine("None installed. Official catalog plugins with cards: official.json-preview, official.openapi-preview, official.html-preview, "
                + "official.pe-info, official.archive-info (a tile naming one shows an install button).");
        }

        foreach (var preview in previews)
        {
            text.Append("- `").Append(preview.Card).Append("` — ").Append(preview.Name);
            if (preview.Description is { Length: > 0 } description)
            {
                text.Append(": ").Append(description);
            }

            if (preview.Extensions.Count > 0)
            {
                text.Append(" (open: ").Append(string.Join(", ", preview.Extensions)).Append(')');
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private const string GuideText = """
        # Commander4K dashboards (c4k.dir)

        A folder becomes a dashboard through a `c4k.dir` file (JSON, comments allowed) in it. `preview` is what quick view (Ctrl+Q)
        shows when the cursor is on the folder; `enter` is what Enter opens (Esc then shows the plain files). A tile-less
        `"preview": "dashboard"` shares the `enter` dashboard's tiles. Paths in `open` and `image` are relative to the folder;
        `~` is the user's profile.

        ```json
        {
          "title": "Infrastruktura",
          "description": "Serwery, kopie i certyfikaty — stan na żywo.",
          "enter": { "view": "dashboard", "tiles": [ … ] },
          "preview": "dashboard"
        }
        ```

        ## Tile fields
        - `title` (required, unique within the dashboard), `subtitle`, `icon` (one emoji or symbol), `image` (a picture file).
        - What a click does — at most one: `open` (a path: folder, file, sftp://, \\server\share), `url` (http/https), `run` (a command;
          needs the user's trust).
        - `card`: makes the tile live (see below). `refresh`: "30s", "5m", "1h" (≥ 2 s; only while visible). `options`: object for the card.
        - `size`: the footprint on the grid (columns × rows): `small` 1×1 (default), `wide` 2×1, `tall` 1×2, `large` 2×2, `huge` 3×2,
          `full` (whole row, 1 high).
        - `heading: true`: a section heading across the whole row (title in accent capitals, subtitle as a muted line, a rule below).
          It is not a tile; it groups the tiles after it.

        ## The grid
        Columns = panel width / 252 px (each at least 240 px, gap 12 px): about 2 on a half-screen panel, 3–4 on a wide one, up to 8.
        Tiles are placed in order; each takes the first free cell at or after the previous tile's row, so small tiles fill a hole beside
        a tall one. Spans wider than the grid shrink to it. A row is at least 136 px high and grows with its content. Taller cards show
        more lines, a taller chart and bigger thumbnails. check_dashboard draws the grid at 2, 3 and 4 columns — aim for no empty cells at 3–4.

        ## Card types (`card`)
        - `true` — whatever quick view would show for `open` (including that folder's own c4k.dir).
        - Folder views (need `open` with a folder): `files` (counts, size, last change), `gallery` (4 thumbnails), `notes` (Markdown notes:
          count, recent), `markdown` (a folder's main note), `dashboard` (another dashboard: its title and tiles; a click opens it in place,
          with back navigation — use it to link sub-dashboards).
        - `api` — values from an HTTP JSON API:
          `"api": { "url", "method" (GET), "headers", "body", "contentType", "fields": [ { "path": "a.b" | "items[0].name", "label",
          "as": "value" | "chart" | "status", "unit", "ok": ["ok","green"] } ] }`. `chart` adds a point on every refresh (a live line chart);
          `status` colours the card (ok values green, others red). Secrets: write `{secret:name}` in a header or the body (never in the url);
          the user enters the value once, it is stored encrypted. Methods other than GET/HEAD wait for the user's trust.
        - `ssh` — CPU, memory, load, disks and uptime of a Linux server: `open` = sftp://user@host/, `options.keyFile`, `options.user`.
          Waits for the user's trust.
        - Widgets (no folder needed): `clock` (`options.timeZone`: IANA or Windows id; refreshes every 15 s), `note` (`options.text`, or the
          start of the text file in `open`), `disks` (used/free space of this PC's disks, or of the disk holding `open`; every minute;
          amber ≥ 90 %, red ≥ 95 %).
        - A plugin id (`official.json-preview` or `plugin/preview`) — the plugin draws the card for `open`; see the list at the end.
        - A panel id (`builtin.sysinfo`: OS, memory with a chart, system disk, uptime; `builtin.taskmanager`: processes and the
          biggest by memory; `plugin/panel` for plugin panels with `"card": true`) — every panel is a widget, no `open` needed; its Open
          button opens the panel beside the dashboard. `card: true` on a folder whose c4k.dir opens a panel shows that panel's widget.

        A card whose answer has exactly one value is drawn as a KPI: the number large (coloured by its status), the label under it.
        So an `api` card with one field makes a clean KPI tile; give it `size: small` and a short title.

        ## Design rules for a professional dashboard
        1. Start with a purpose: one dashboard answers one question ("is production healthy?", "where is project X?").
        2. Structure top-down: a row of 3–4 KPI tiles (small) → the main chart or list (huge/large) → details (small/wide) → notes and
           links (full or wide). Open each group with a `heading` tile.
        3. Keep it to one screen: 6–16 tiles. Put more into sub-dashboards (`card: "dashboard"` tiles pointing at subfolders).
        4. Fill rows: sizes in a row should add up to the column count at 4 and 3 columns (check the picture). Pair a `tall` tile with
           two `small` ones beside it; a `large` with two `small` or one `wide` below it.
        5. Titles: 1–3 words, sentence case, no trailing punctuation; put details into `subtitle`. Use the user's language (Polish UI).
        6. Icons: one consistent family (e.g. all emoji symbols), one per tile; headings may have one too.
        7. Refresh by how fast data changes: health/status 30s–1m, metrics 1–5m, files and notes once (no refresh).
        8. Status colours carry meaning only: use `status` fields and `ok` lists for things that can fail; plain values for the rest.
        9. Never put tokens or passwords in the file; use `{secret:name}`.
        10. Prefer read-only cards (GET, folder views, widgets); `run`, `ssh` and POST cards need the user's trust — say so to the user.

        ## Recipes
        Operations (4 columns):
        ```json
        { "title": "Produkcja", "description": "Stan usług i serwerów",
          "enter": { "view": "dashboard", "tiles": [
            { "title": "Stan usług", "heading": true, "icon": "🛰" },
            { "title": "API", "card": "api", "refresh": "30s", "api": { "url": "https://api.example.com/health",
              "fields": [ { "path": "status", "label": "Status", "as": "status", "ok": ["ok"] } ] } },
            { "title": "Czas odpowiedzi", "card": "api", "refresh": "1m", "api": { "url": "https://api.example.com/metrics",
              "fields": [ { "path": "latency_ms", "label": "ms (p95)" } ] } },
            { "title": "Kolejka", "card": "api", "refresh": "1m", "api": { "url": "https://api.example.com/queue",
              "fields": [ { "path": "pending", "label": "zadań w kolejce" } ] } },
            { "title": "Dyski", "card": "disks" },
            { "title": "Ruch", "card": "api", "size": "huge", "refresh": "1m", "api": { "url": "https://api.example.com/metrics",
              "fields": [ { "path": "rps", "label": "Żądania/s", "as": "chart" } ] } },
            { "title": "VPS", "card": "ssh", "size": "tall", "open": "sftp://deploy@vps.example.com/", "refresh": "30s" },
            { "title": "Dokumentacja", "heading": true, "icon": "📚" },
            { "title": "Runbook", "open": "docs/runbook.md", "card": true, "size": "wide" },
            { "title": "Dyżur", "card": "note", "size": "wide", "options": { "text": "Ten tydzień: Ania (tel. wewn. 214)." } }
          ] },
          "preview": "dashboard" }
        ```
        Project: heading "Projekt" → `files` card on src (small), `markdown` card on docs (wide), `gallery` on design/ (small) →
        heading "Linki" → url tiles (small) for the repository, CI and the tracker → a `note` (full) with the next milestone.
        Home: clocks for two time zones (small), `disks` (tall), `gallery` of photos (large), shortcuts (small).
        """;
}
