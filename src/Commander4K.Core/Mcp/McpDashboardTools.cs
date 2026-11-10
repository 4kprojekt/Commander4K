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
/// MCP tools that let an AI client design folder dashboards made of <c>*.c4k</c> files: a guide with every card type
/// and the design rules, reading the current items, checking a draft (errors, warnings and a picture of the grid at
/// several widths) and saving it. Saving goes through the dispatcher's confirmation like every write, and never trusts
/// the files: cards that run commands, sign in over SSH or send POST requests stay locked until the user allows them.
/// </summary>
public static class McpDashboardTools
{
    private const int MaxConfigBytes = (int)C4kReader.MaxBytes;

    private static readonly string[] FolderCards = [FolderViewIds.Files, FolderViewIds.Gallery, FolderViewIds.Notes, FolderViewIds.Markdown, FolderViewIds.Dashboard];

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonObject ItemsSchema(string description)
    {
        var schema = McpSchema.Object(("folder", "string", "The folder (disk, share, sftp://…); relative paths in items are resolved against it.", true), ("items", "array", description, true));
        schema["properties"]!["items"]!["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "File name, with or without .c4k. Files are ordered by name (natural order) to make the dashboard." },
                ["json"] = new JsonObject { ["type"] = "string", ["description"] = "The item's JSON text." },
            },
            ["required"] = new JsonArray("name", "json"),
        };
        return schema;
    }

    /// <param name="cardPreviews">The installed previews that draw cards, read on every call (plugins come and go).</param>
    public static IReadOnlyList<McpTool> Create(VfsRouter router, Func<IReadOnlyList<McpCardPreview>> cardPreviews, Func<IReadOnlyList<string>> allowedRoots, Func<IReadOnlyList<string>> protectedRoots) =>
    [
        new McpTool(
            new McpToolDefinition(
                "dashboard_guide",
                "How to build a Commander4K folder dashboard (a set of .c4k files): the item keys, the six tile sizes and how the grid places them, "
                + "every card type with its options (folder views, API, SSH, clock, note, disks, and the cards of the installed plugins), "
                + "KPI tiles, design rules for a clean, professional dashboard and complete examples. Read it before writing a dashboard.",
                McpSchema.Object(),
                McpToolMode.Allow),
            (_, _) => Task.FromResult(new McpToolResult(Guide(cardPreviews()))),
            _ => "dashboard_guide"),
        new McpTool(
            new McpToolDefinition(
                "get_dashboard",
                "Reads the .c4k files of a folder: each as { name, json } (keep what you do not change), problems found in them and a picture "
                + "of the grid. Returns a note when the folder has no .c4k files yet.",
                McpSchema.Object(("folder", "string", "The folder (disk, share, sftp://…).", true)),
                McpToolMode.Allow,
                PathArguments: ["folder"]),
            (call, ct) => GetAsync(router, call.Required("folder"), cardPreviews(), ct),
            args => "get_dashboard " + args["folder"]),
        new McpTool(
            new McpToolDefinition(
                "check_dashboard",
                "Checks draft .c4k items without saving them: JSON and item errors, unknown or missing card plugins, cards that will wait for the "
                + "user's trust, design warnings, and how the grid lays the tiles out at 2, 3 and 4 columns. Fix every error before save_dashboard.",
                ItemsSchema("The complete set of items the folder will have: one { name, json } per .c4k file."),
                McpToolMode.Allow,
                PathArguments: ["folder"]),
            (call, _) => Task.FromResult(Check(call.Required("folder"), ReadItems(call), cardPreviews())),
            args => "check_dashboard " + args["folder"]),
        new McpTool(
            new McpToolDefinition(
                "save_dashboard",
                "Writes one .c4k file per item into the folder after the same checks as check_dashboard; refuses items with errors. One file is one "
                + "view, two or more make a dashboard ordered by file name. A file with the same name is replaced (this call is confirmed by the user) "
                + "unless it contains comments, which are never overwritten; a folder that has exactly one .c4k file is not turned into a dashboard "
                + "unless the items include that file. Cards that run commands, use SSH or send POST requests stay locked until the user presses \"Zezwól\".",
                ItemsSchema("The items to write: one { name, json } per .c4k file."),
                McpToolMode.Ask,
                PathArguments: ["folder"],
                Writes: true),
            (call, ct) => SaveAsync(router, call.Required("folder"), ReadItems(call), cardPreviews(), allowedRoots(), protectedRoots(), ct),
            args => "save_dashboard " + args["folder"],
            args => args["items"] is JsonArray items ? string.Join("\n\n", items.Select(i => $"{i?["name"]}:\n{i?["json"]}")) : null),
    ];

    private static List<(string Name, string Json)> ReadItems(McpCall call)
    {
        if (call.Arguments["items"] is not JsonArray array)
        {
            throw new ArgumentException("Missing required argument 'items' (an array of { name, json }).");
        }

        var items = new List<(string, string)>();
        foreach (var node in array)
        {
            var name = node?["name"] is JsonValue n && n.TryGetValue<string>(out var text) ? text.Trim() : null;
            var json = node?["json"] switch { JsonValue v when v.TryGetValue<string>(out var j) => j, JsonObject o => o.ToJsonString(), _ => null };
            if (string.IsNullOrEmpty(name) || json is null)
            {
                throw new ArgumentException("Every item needs a \"name\" and a \"json\".");
            }

            items.Add((name, json));
        }

        return items;
    }

    /// <summary>The file name for an item name, or <c>null</c> + error when the name could escape the folder or hide the file.</summary>
    private static string? FileNameOf(string name, out string? error)
    {
        var file = name.EndsWith(C4kReader.Extension, StringComparison.OrdinalIgnoreCase) ? name : name + C4kReader.Extension;
        error = file.StartsWith('.') || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Contains("..", StringComparison.Ordinal)
            ? $"\"{name}\" is not a valid file name (no folders, no leading dot, no : / \\ characters)." : null;
        return error is null ? file : null;
    }

    /// <summary>Errors (the files cannot be saved), warnings and the layout pictures of a draft.</summary>
    public sealed record DashboardReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string Layout);

    public static DashboardReport Analyze(C4kFolder folder, IReadOnlyList<McpCardPreview> previews)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (folder.Items.Count == 0)
        {
            warnings.Add("No .c4k files: nothing is shown for the folder.");
            return new DashboardReport(errors, warnings, string.Empty);
        }

        var titles = new HashSet<string>(StringComparer.Ordinal);
        var tile = folder.Items.Count > 1;
        foreach (var item in folder.Items)
        {
            var where = item.Name + C4kReader.Extension;
            if (item.Error is { } error)
            {
                errors.Add($"{where}: {error}");
            }

            if (tile && !titles.Add(item.Title))
            {
                warnings.Add($"{where}: the title \"{item.Title}\" repeats; make titles unique.");
            }

            CheckItem(item, where, tile, previews, errors, warnings);
        }

        if (folder.Skipped > 0)
        {
            errors.Add($"More than {C4kReader.MaxItems} .c4k files; only the first {C4kReader.MaxItems} are used.");
        }
        else if (folder.Items.Count > 24)
        {
            warnings.Add($"{folder.Items.Count} tiles is a lot for one screen; split it into dashboards in subfolders and link them with \"card\": \"dashboard\" items.");
        }

        return new DashboardReport(errors, warnings, tile ? Picture(folder.Items) : string.Empty);
    }

    private static void CheckItem(C4kItem item, string where, bool tile, IReadOnlyList<McpCardPreview> previews, List<string> errors, List<string> warnings)
    {
        if (item.Card is null)
        {
            if (item.View is null && item.Plugin is null && item.Panel is null && item.Open is null && item.Url is null && item.Run is null)
            {
                warnings.Add($"{where}: an item with no view, plugin, panel, open, url or run does nothing; give it one.");
            }

            if (item.Run is not null)
            {
                warnings.Add($"{where}: \"run\" works only after the user trusts this file (they are asked on first use).");
            }

            return;
        }

        var card = item.Card;
        if (!tile)
        {
            warnings.Add($"{where}: \"card\" only matters in a dashboard (two or more .c4k files).");
        }

        if (item.Refresh is { } refresh && refresh < TimeSpan.FromSeconds(10))
        {
            warnings.Add($"{where}: refresh every {refresh.TotalSeconds:0} s is aggressive; 30s–5m suits most cards.");
        }

        if (string.Equals(card, C4kItem.AutoCard, StringComparison.Ordinal) || FolderCards.Contains(card, StringComparer.OrdinalIgnoreCase))
        {
            if (item.Open is null)
            {
                errors.Add($"{where}: card \"{card}\" needs \"open\" with the path it shows.");
            }

            return;
        }

        switch (card.ToLowerInvariant())
        {
            case FolderViewIds.Api:
                if (item.Api?.HasSideEffects == true)
                {
                    warnings.Add($"{where}: a {item.Api.Method} request waits until the user trusts this file; prefer GET for read-only cards.");
                }

                return;
            case FolderViewIds.Ssh:
                if (item.Open is null || !item.Open.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{where}: card \"ssh\" needs \"open\" with an sftp:// path.");
                }

                warnings.Add($"{where}: SSH cards wait until the user trusts this file.");
                return;
            case "clock" or "disks":
                return;
            case "note":
                if (item.Open is null && !(item.Options?.Contains("\"text\"", StringComparison.Ordinal) ?? false))
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

    /// <summary>The grid at 4, 3 and 2 columns, one letter per tile (A, B, … in file order), with a legend.</summary>
    public static string Picture(IReadOnlyList<C4kItem> tiles)
    {
        var text = new StringBuilder();
        text.AppendLine("Layout (files in name order):");
        for (var i = 0; i < tiles.Count; i++)
        {
            text.Append("  ").Append(Letter(i)).Append(" = ").Append(tiles[i].Name).Append(C4kReader.Extension).Append(" (").Append(TileSizes.Name(tiles[i].Size))
                .Append(tiles[i].Card is { } card ? ", card " + card : string.Empty).AppendLine(")");
        }

        var sizes = tiles.Select(t => t.Size).ToList();
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
                        grid[r, c] = Letter(i);
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
                text.Append("  ").Append(empty).AppendLine(" empty cell(s) (·): rename files to reorder or change sizes to close the gaps.");
            }
        }

        return text.ToString();
    }

    private static char Letter(int index) => index < 26 ? (char)('A' + index) : index < 52 ? (char)('a' + index - 26) : '#';

    private static McpToolResult Check(string folder, List<(string Name, string Json)> items, IReadOnlyList<McpCardPreview> previews)
    {
        var files = new List<(string, string)>();
        foreach (var (name, json) in items)
        {
            if (FileNameOf(name, out var error) is not { } file)
            {
                return McpToolResult.Error(error!);
            }

            files.Add((file, json));
        }

        return new McpToolResult(Format(Analyze(C4kReader.Build(folder, files), previews), saved: null));
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

    /// <summary>The item files of a folder (by name, at most <see cref="C4kReader.MaxItems"/> read) and how many exist.</summary>
    private static async Task<(List<(string Name, string Json)> Files, int Total)> ReadFolderAsync(VfsRouter router, string folder, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var entry in router.OpenListing(folder, ct).ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (C4kReader.IsItemFile(entry))
            {
                names.Add(entry.Name);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string, string)>();
        foreach (var name in names.Take(C4kReader.MaxItems))
        {
            files.Add((name, await ReadAsync(router, C4kJson.ChildPath(folder, name), ct).ConfigureAwait(false) ?? string.Empty));
        }

        return (files, names.Count);
    }

    private static async Task<McpToolResult> GetAsync(VfsRouter router, string folder, IReadOnlyList<McpCardPreview> previews, CancellationToken ct)
    {
        var (files, _) = await ReadFolderAsync(router, folder, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return new McpToolResult($"{folder} has no {C4kReader.Extension} files yet. Read dashboard_guide, then write them with save_dashboard.");
        }

        var list = new JsonArray([.. files.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["json"] = f.Json })]);
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var report = Analyze(C4kReader.Build(folder, files), previews);
        return new McpToolResult($"```json\n{list.ToJsonString(options)}\n```\n" + Format(report, saved: null));
    }

    private static async Task<McpToolResult> SaveAsync(
        VfsRouter router, string folder, List<(string Name, string Json)> items, IReadOnlyList<McpCardPreview> previews,
        IReadOnlyList<string> roots, IReadOnlyList<string> protectedRoots, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return McpToolResult.Error("No items.");
        }

        var files = new List<(string Name, string Json)>();
        foreach (var (name, json) in items)
        {
            if (FileNameOf(name, out var nameError) is not { } file)
            {
                return McpToolResult.Error(nameError!);
            }

            if (files.Any(f => string.Equals(f.Name, file, StringComparison.OrdinalIgnoreCase)))
            {
                return McpToolResult.Error($"{file} appears twice.");
            }

            if (Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
            {
                return McpToolResult.Error($"{file} is larger than {MaxConfigBytes / 1024} KB.");
            }

            files.Add((file, json));
        }

        var draft = C4kReader.Build(folder, files);
        var report = Analyze(draft, previews);
        if (report.Errors.Count > 0)
        {
            return McpToolResult.Error("Not saved.\n" + Format(report, saved: null));
        }

        // The "folder" key is a path too: it gets the same policy as the folder argument.
        foreach (var item in draft.Items)
        {
            if (McpToolDispatcher.Refusal(item.ContextFolder, roots, protectedRoots) is { } refusal)
            {
                return McpToolResult.Error($"{item.Name}{C4kReader.Extension}: \"folder\" - {refusal}");
            }
        }

        if (await router.GetEntryAsync(folder, ct).ConfigureAwait(false) is not { IsContainer: true })
        {
            return McpToolResult.Error($"{folder} is not a folder.");
        }

        var (existing, total) = await ReadFolderAsync(router, folder, ct).ConfigureAwait(false);
        bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // A lone item is the folder's own view: the writer does not quietly add a second one next to it.
        if (total == 1 && !files.Any(f => Same(f.Name, existing[0].Name)))
        {
            return McpToolResult.Error($"{folder} has exactly one item ({existing[0].Name}) that is its view; adding more would turn it into a dashboard. Include {existing[0].Name} in the items (replace it) or ask the user.");
        }

        if (total + files.Count(f => !existing.Any(e => Same(e.Name, f.Name))) > C4kReader.MaxItems)
        {
            return McpToolResult.Error($"A folder shows at most {C4kReader.MaxItems} .c4k files.");
        }

        foreach (var (file, _) in files)
        {
            if (existing.FirstOrDefault(e => Same(e.Name, file)) is { Json: { } old } && C4kWriter.HasComments(old))
            {
                return McpToolResult.Error($"{file} already exists and contains comments, which would be lost; choose another name.");
            }
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var (file, json) in files)
        {
            var text = C4kWriter.Create(JsonNode.Parse(json, documentOptions: Lenient)!.AsObject());
            await using var stream = await router.OpenWriteAsync(C4kJson.ChildPath(folder, file), overwrite: true, ct).ConfigureAwait(false);
            await stream.WriteAsync(encoding.GetBytes(text), ct).ConfigureAwait(false);
        }

        return new McpToolResult(Format(report, $"Saved {files.Count} file(s) in {folder}: {string.Join(", ", files.Select(f => f.Name))}."));
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
        # Commander4K dashboards (.c4k files)

        A folder's look is defined by `*.c4k` files in it, one item per file (JSON; comments are allowed in files the user writes, but files
        saved through save_dashboard are written without comments). No files: an ordinary folder. One file: its view fills the panel.
        Two or more: an automatic dashboard, one tile per file, in the natural order of the file names (`1 cpu.c4k` before `2 ram.c4k`
        before `10 disks.c4k`). There are no headings and no order keys: to reorder, rename the files (number them). Hidden files
        (leading dot) and files over 256 KB are ignored; at most 50 files are used. Paths in `open`, `image` and `file` are relative
        to the folder; `~` is the user's profile. Use `get_dashboard` to see what exists and `save_dashboard` to write; an item's `name` is its file name.

        ```json
        { "title": "Serwery", "description": "Stan na żywo.", "card": "api", "size": "wide", "refresh": "30s", "api": { "url": "https://…" } }
        ```

        ## Item keys
        - `title` (default: the file name), `description`, `icon` (one emoji or symbol), `image` (a picture file).
        - What the item is, one of: `view` (`files`, `gallery`, `dashboard`, `notes`, `markdown`), `plugin` (+ `preview`/`panel`, `options`),
          `open` (a path: folder, file, sftp://, \\server\share; + `user`, `keyFile`), `url` (http/https), `run` (a command; needs the
          user's trust), `panel`.
        - `folder`: the folder the item is about, instead of the one that holds the file (relative to it).
        - `card`: makes the tile live (see below). `refresh`: "30s", "5m", "1h" (at least 2 s; only while visible). `options`: object for the card.
        - `size`: the tile's footprint (columns x rows): `small` 1x1 (default), `wide` 2x1, `tall` 1x2, `large` 2x2, `huge` 3x2,
          `full` (whole row, 1 high).

        ## The grid
        Columns = panel width / 252 px (each at least 240 px, gap 12 px): about 2 on a half-screen panel, 3-4 on a wide one, up to 8.
        Tiles are placed in file-name order; each takes the first free cell at or after the previous tile's row, so small tiles fill a hole
        beside a tall one. Spans wider than the grid shrink to it. A row is at least 136 px high and grows with its content. check_dashboard
        draws the grid at 2, 3 and 4 columns; aim for no empty cells at 3-4.

        ## Card types (`card`)
        - `true`: whatever quick view would show for `open` (including that folder's own .c4k files).
        - Folder views (need `open` with a folder): `files` (counts, size, last change), `gallery` (4 thumbnails), `notes` (Markdown notes:
          count, recent), `markdown` (a folder's main note), `dashboard` (another dashboard: its title and tiles; a click opens it in place,
          with back navigation; use it to link sub-dashboards).
        - `api`: values from an HTTP JSON API:
          `"api": { "url", "method" (GET), "headers", "body", "contentType", "fields": [ { "path": "a.b" | "items[0].name", "label",
          "as": "value" | "chart" | "status", "unit", "ok": ["ok","green"] } ] }`. `chart` adds a point on every refresh (a live line chart);
          `status` colours the card (ok values green, others red). Secrets: write `{secret:name}` in a header or the body (never in the url);
          the user enters the value once, it is stored encrypted. Methods other than GET/HEAD wait for the user's trust.
        - `ssh`: CPU, memory, load, disks and uptime of a Linux server: `open` = sftp://user@host/, `user`, `keyFile`. Waits for the user's trust.
        - Widgets (no folder needed): `clock` (`options.timeZone`: IANA or Windows id; refreshes every 15 s), `note` (`options.text`, or the
          start of the text file in `open`), `disks` (used/free space of this PC's disks, or of the disk holding `open`; every minute;
          amber from 90 %, red from 95 %).
        - A plugin id (`official.json-preview` or `plugin/preview`): the plugin draws the card for `open`; see the list at the end.
        - A panel id (`builtin.sysinfo`: OS, memory with a chart, system disk, uptime; `builtin.taskmanager`: processes and the
          biggest by memory; `plugin/panel` for plugin panels with `"card": true`): every panel is a widget, no `open` needed.

        A card whose answer has exactly one value is drawn as a KPI: the number large (coloured by its status), the label under it.
        So an `api` card with one field makes a clean KPI tile; give it `size: small` and a short title.

        ## Trust
        `run`, POST-like `api`, `ssh` and sftp/ftp cards stay locked ("Zezwól") until the user trusts the file; save_dashboard never trusts.
        Tell the user which files wait. Trust is bound to what it unlocks, so resizing or retitling keeps it. Never put tokens or passwords
        in a file; use `{secret:name}`.

        ## Design rules for a professional dashboard
        1. Start with a purpose: one dashboard answers one question ("is production healthy?", "where is project X?").
        2. Order by file name, top-down: 3-4 KPI tiles (small), then the main chart or list (huge/large), then details (small/wide), then
           notes and links (full or wide). Number the names with gaps (`10 ...`, `20 ...`) so files can be inserted later.
        3. Keep it to one screen: 6-16 tiles. Put more into sub-dashboards (`card: "dashboard"` items pointing at subfolders).
        4. Fill rows: sizes in a row should add up to the column count at 4 and 3 columns (check the picture). Pair a `tall` tile with
           two `small` ones beside it; a `large` with two `small` or one `wide` below it.
        5. Titles: 1-3 words, sentence case, no trailing punctuation; put details into `description`. Use the user's language (Polish UI).
        6. Icons: one consistent family (e.g. all emoji symbols), one per tile.
        7. Refresh by how fast data changes: health/status 30s-1m, metrics 1-5m, files and notes once (no refresh).
        8. Status colours carry meaning only: use `status` fields and `ok` lists for things that can fail; plain values for the rest.
        9. Prefer read-only cards (GET, folder views, widgets); `run`, `ssh` and POST cards need the user's trust, so say so to the user.
        10. A folder that already has exactly one .c4k file has that as its view: do not add files next to it unless the user asks.

        ## Recipe: operations (4 columns)
        Files (name: content):
        - `10 API.c4k`: `{ "icon": "🛰", "card": "api", "refresh": "30s", "api": { "url": "https://api.example.com/health", "fields": [ { "path": "status", "label": "Status", "as": "status", "ok": ["ok"] } ] } }`
        - `20 Czas odpowiedzi.c4k`: `{ "card": "api", "refresh": "1m", "api": { "url": "https://api.example.com/metrics", "fields": [ { "path": "latency_ms", "label": "ms (p95)" } ] } }`
        - `30 Kolejka.c4k`: `{ "card": "api", "refresh": "1m", "api": { "url": "https://api.example.com/queue", "fields": [ { "path": "pending", "label": "zadań w kolejce" } ] } }`
        - `40 Dyski.c4k`: `{ "card": "disks" }`
        - `50 Ruch.c4k`: `{ "card": "api", "size": "huge", "refresh": "1m", "api": { "url": "https://api.example.com/metrics", "fields": [ { "path": "rps", "label": "Żądania/s", "as": "chart" } ] } }`
        - `60 VPS.c4k`: `{ "card": "ssh", "size": "tall", "open": "sftp://deploy@vps.example.com/", "refresh": "30s" }`
        - `70 Runbook.c4k`: `{ "open": "docs/runbook.md", "card": true, "size": "wide" }`
        - `80 Dyżur.c4k`: `{ "card": "note", "size": "wide", "options": { "text": "Ten tydzień: Ania (tel. wewn. 214)." } }`

        Project: `files` card on src (small), `markdown` card on docs (wide), `gallery` on design/ (small), url items (small) for the
        repository, CI and the tracker, a `note` (full) with the next milestone.
        Home: clocks for two time zones (small), `disks` (tall), `gallery` of photos (large), shortcuts (small).
        """;
}
