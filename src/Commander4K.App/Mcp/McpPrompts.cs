using Commander4K.App.Ai;
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

            var sample = await Task.Run(() => AiAppContext.DescribeFileAsync(host, found, ct), ct);
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
