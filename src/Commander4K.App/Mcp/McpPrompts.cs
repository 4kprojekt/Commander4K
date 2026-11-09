using Commander4K.App.Composition;
using Commander4K.Core.Mcp;
using Commander4K.Core.Plugins;

namespace Commander4K.App.Mcp;

/// <summary>
/// The MCP prompts (slash commands in Claude Code): write a plugin, write a preview plugin for a file, explain a
/// file format. Text is English because the model reads it. Prompts about a file only include its bytes when the file
/// is inside the folders allowed for MCP, outside the protected ones, and read_file is not blocked; each such prompt is
/// audited as <c>mcp:&lt;client&gt;</c> / <c>prompt:&lt;name&gt;</c>.
/// </summary>
public static class McpPrompts
{
    /// <summary>Appended to every prompt (and to the Claude Code requests built by the window) that asks for a plugin.</summary>
    public const string PluginHint = "\n\nUse the commander4k MCP tools (get_plugin_guide, check_plugin, install_plugin) to write and install it.";

    /// <param name="readFileBlocked">True while read_file's effective mode is Block: prompts then never include file bytes.</param>
    public static IReadOnlyList<McpPrompt> Create(
        AppHost host, Func<IReadOnlyList<string>> allowedRoots, Func<IReadOnlyList<string>> protectedRoots, Func<bool> readFileBlocked) =>
    [
        new McpPrompt(
            "write_plugin",
            "Write a Commander4K plugin for what the user describes.",
            [new McpPromptArgument("wish", "What the plugin should do.", true)],
            (args, _) => Task.FromResult(args["wish"].Trim() + PluginHint)),
        new McpPrompt(
            "preview_plugin_for_file",
            "Write a quick-view (preview) plugin for the type of a file.",
            [
                new McpPromptArgument("path", "Full path of a sample file.", true),
                new McpPromptArgument("wish", "How the preview should look (optional).", false),
            ],
            async (args, ct) =>
            {
                var path = McpPathPolicy.Normalize(args["path"]);
                var extension = Path.GetExtension(path);
                var wish = args.TryGetValue("wish", out var w) && !string.IsNullOrWhiteSpace(w)
                    ? w.Trim()
                    : "Show a readable preview of this kind of file.";
                var sample = await SampleAsync(host, args.ClientName, "preview_plugin_for_file", path,
                    new McpAccess(allowedRoots(), protectedRoots()), readFileBlocked(), ct);
                var pattern = extension.Length > 0 ? extension : Path.GetFileName(path);
                return wish + "\n\nThe preview must declare `contributes.previews` with \"extensions\": [\"" + pattern +
                    "\"] and read the file only with the SDK (read_bytes / read_text). Sample file:\n\n" + sample + PluginHint;
            }),
        new McpPrompt(
            "explain_file_format",
            "Explain what kind of file this is and what is inside, from its name and first bytes.",
            [new McpPromptArgument("path", "Full path of the file.", true)],
            async (args, ct) =>
            {
                var path = McpPathPolicy.Normalize(args["path"]);
                var sample = await SampleAsync(host, args.ClientName, "explain_file_format", path,
                    new McpAccess(allowedRoots(), protectedRoots()), readFileBlocked(), ct);
                return "Identify the format of this file and explain its structure and what it is used for. " +
                    "Say how sure you are, and what else you would need to check.\n\n" + sample;
            }),
        new McpPrompt(
            "build_dashboard",
            "Design and save a clean, professional dashboard (a set of .c4k files, one per tile, ordered by file name) for a folder: KPIs, charts, server status, notes and links.",
            [
                new McpPromptArgument("folder", "The folder that gets the dashboard.", true),
                new McpPromptArgument("goal", "What the dashboard should answer or show (optional; otherwise infer it from the folder).", false),
            ],
            (args, _) =>
            {
                var folder = McpPathPolicy.Normalize(args["folder"]);
                if (McpToolDispatcher.Refusal(folder, allowedRoots(), protectedRoots()) is { } refusal)
                {
                    return Task.FromResult(refusal);
                }

                var goal = args.TryGetValue("goal", out var g) && !string.IsNullOrWhiteSpace(g) ? g.Trim() : null;
                return Task.FromResult(
                    $"Build a Commander4K dashboard for the folder {folder}." +
                    (goal is null ? " Work out from its contents what it is for." : $" Goal: {goal}") + "\n\n" +
                    "Steps:\n" +
                    "1. Call dashboard_guide and follow its design rules.\n" +
                    "2. Call get_dashboard for the folder (keep existing .c4k files you are not asked to change; a folder with exactly one .c4k file is that folder's view, so do not add files next to it unless the user asks), and list_directory to see what is there " +
                    "(subfolders for folder cards and sub-dashboards, docs for markdown/note cards, pictures for gallery, configs for plugin cards).\n" +
                    "3. Plan the tile order (file names such as "10 ...", "20 ..."), the KPI row, the main chart or list and the details; ask the user for API URLs, servers " +
                    "or secrets you cannot find, and never invent endpoints.\n" +
                    "4. Run check_dashboard until there are no errors and the 3- and 4-column pictures have no empty cells.\n" +
                    "5. save_dashboard (one { name, json } per file), then navigate to the folder and set the other panel to preview (set_panel_mode) so the user sees it. " +
                    "Tell the user which cards wait for their trust (\"Zezwól\") and which plugins the tiles offer to install.\n" +
                    "Titles and texts in Polish unless the user writes in another language.");
            }),
        new McpPrompt(
            "translate_ui",
            "Translate the Commander4K user interface into a language (or complete an existing translation).",
            [
                new McpPromptArgument("language", "Language code, e.g. de, uk, pt-BR.", true),
                new McpPromptArgument("name", "Language name in English (optional).", false),
            ],
            (args, _) =>
            {
                var language = args["language"].Trim();
                var name = args.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : language;
                return Task.FromResult(
                    $"Translate the Commander4K user interface into {name} (language code \"{language}\"). " +
                    $"Use the commander4k MCP tools: get_translation_kit (language \"{language}\", onlyMissing true) gives the rules and a page of entries; " +
                    "translate the page, check it with check_translation, save it with save_translation, and continue with the next offset " +
                    "until no entries are missing. Finally offer to switch the language with set_setting (key \"language\"); it needs a restart.");
            }),
        new McpPrompt(
            "design_skin",
            "Design a Commander4K colour skin from a description.",
            [new McpPromptArgument("description", "What the skin should look like.", true)],
            (args, _) => Task.FromResult(
                $"Design a Commander4K skin: \"{args["description"].Trim()}\". " +
                "Use the commander4k MCP tools: read get_skin_guide, write the skin JSON with a new id, check it with check_skin and fix every warning, " +
                "then save_skin (the user sees it live and accepts it).")),
    ];

    private static async Task<string> SampleAsync(
        AppHost host, string client, string prompt, string path, McpAccess access, bool readFileBlocked, CancellationToken ct)
    {
        void Audit(AuditOutcome outcome) => host.Audit.Record("mcp:" + client, "prompt:" + prompt, path, outcome);

        if (readFileBlocked)
        {
            Audit(AuditOutcome.Denied);
            return $"File: {path}\nReading files is disabled in Commander4K (MCP settings: read_file is blocked), so its contents are not included.";
        }

        if (McpPathPolicy.IsProtected(path, access.Protected))
        {
            Audit(AuditOutcome.Denied);
            return $"File: {path}\nThis file is in Commander4K's own plugin, config or data folders, which MCP cannot read.";
        }

        if (!McpPathPolicy.IsAllowed(path, access.Allowed))
        {
            Audit(AuditOutcome.Denied);
            return $"File: {path}\nThis file is outside the folders allowed for MCP, so its contents are not included. " +
                "Ask the user to add its folder to the allowed folders (settings, \"MCP\") and try again.";
        }

        try
        {
            var entry = await host.Router.GetEntryAsync(path, ct);
            if (entry is not { } found)
            {
                Audit(AuditOutcome.Failed);
                return $"File: {path}\nThe file does not exist.";
            }

            var sample = await Task.Run(() => McpAppContext.DescribeFileAsync(host, found, ct), ct);
            Audit(AuditOutcome.Allowed);
            return sample;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Audit(AuditOutcome.Failed);
            return $"File: {path}\nCould not read the file: {ex.Message}";
        }
    }
}
