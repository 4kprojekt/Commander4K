using Commander4K.App.Ai;
using Commander4K.App.Composition;
using Commander4K.Core.Mcp;

namespace Commander4K.App.Mcp;

/// <summary>
/// The MCP prompts (slash commands in Claude Code): write a plugin, write a preview plugin for a file, explain a
/// file format. Text is English because the model reads it. Prompts about a file only include its bytes when the file
/// is inside the folders allowed for MCP.
/// </summary>
public static class McpPrompts
{
    /// <summary>Appended to every prompt (and to the Claude Code requests built by the window) that asks for a plugin.</summary>
    public const string PluginHint = "\n\nUse the commander4k MCP tools (get_plugin_guide, check_plugin, install_plugin) to write and install it.";

    public static IReadOnlyList<McpPrompt> Create(AppHost host, Func<IReadOnlyList<string>> allowedRoots) =>
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
                var sample = await SampleAsync(host, path, allowedRoots(), ct);
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
                var sample = await SampleAsync(host, path, allowedRoots(), ct);
                return "Identify the format of this file and explain its structure and what it is used for. " +
                    "Say how sure you are, and what else you would need to check.\n\n" + sample;
            }),
    ];

    private static async Task<string> SampleAsync(AppHost host, string path, IReadOnlyList<string> roots, CancellationToken ct)
    {
        if (!McpPathPolicy.IsAllowed(path, roots))
        {
            return $"File: {path}\nThis file is outside the folders allowed for MCP, so its contents are not included. " +
                "Ask the user to add its folder to the allowed folders (settings, \"MCP\") and try again.";
        }

        try
        {
            var entry = await host.Router.GetEntryAsync(path, ct);
            if (entry is not { } found)
            {
                return $"File: {path}\nThe file does not exist.";
            }

            return await Task.Run(() => AiAppContext.DescribeFileAsync(host, found, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"File: {path}\nCould not read the file: {ex.Message}";
        }
    }
}
