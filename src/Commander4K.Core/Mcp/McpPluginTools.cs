using System.Text;
using System.Text.Json.Nodes;
using Commander4K.Core.Plugins;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Mcp;

/// <summary>
/// UI-free plugin tools: validating a plugin (files in the call, or a local folder) and reading a plugin's log.
/// Nothing is written outside a temp staging folder. Messages are for the model, hence English.
/// </summary>
public static class McpPluginTools
{
    public const int DefaultLogLines = 100;
    public const int MaxLogLines = 1000;

    /// <param name="isExternalPlugin">True for the id of an installed external plugin. get_plugin_log answers only
    /// for those: built-in logs (the browser's navigations and searches) and other MCP clients' calls stay private.</param>
    public static IReadOnlyList<McpTool> Create(Func<string, string?> sdkClient, string pluginsRoot, AuditLog audit, Func<string, bool> isExternalPlugin) =>
    [
        new McpTool(
            new McpToolDefinition(
                "check_plugin",
                "Validates a plugin without installing it: manifest, file limits and SDK bypasses (open(), fs, subprocess, fetch, stdout…). "
                + "Give exactly one of: files (array of {path, content}) or folder (local plugin folder on disk).",
                FilesOrFolderSchema(),
                McpToolMode.Allow,
                PathArguments: ["folder"],
                ReadsTree: true),
            // Check catches cancellation itself, so nothing expected leaves the Task.Run lambda (debugger rule).
            (call, ct) => Task.Run(() => Check(call, sdkClient, pluginsRoot, ct), CancellationToken.None),
            args => "check_plugin " + (args["folder"]?.ToString() ?? "(files)")),
        new McpTool(
            new McpToolDefinition(
                "get_plugin_log",
                "Returns the most recent audit/log lines of an installed external plugin (host calls, errors, and what the plugin wrote to stderr). "
                + "Built-in plugins and other MCP clients are not available.",
                McpSchema.Object(
                    ("id", "string", "Plugin id.", true),
                    ("count", "integer", "How many lines (default 100, 1 to 1000).", false)),
                McpToolMode.Allow),
            (call, _) => Task.FromResult(Log(call, audit, isExternalPlugin)),
            args => "get_plugin_log " + args["id"]),
    ];

    private static McpToolResult Check(McpCall call, Func<string, string?> sdkClient, string pluginsRoot, CancellationToken ct)
    {
        var hasFiles = call.Arguments["files"] is JsonArray;
        var folder = call.Optional("folder");
        if (hasFiles == (folder is not null))
        {
            return McpToolResult.Error("Give exactly one of 'files' or 'folder'.");
        }

        try
        {
            IReadOnlyList<GeneratedFile> files;
            PluginLimits limits;
            if (hasFiles)
            {
                try
                {
                    files = ReadFiles((JsonArray)call.Arguments["files"]!);
                }
                catch (ArgumentException ex)
                {
                    return McpToolResult.Error(ex.Message);
                }

                limits = PluginLimits.Answer;
            }
            else
            {
                if (LocalFolderError(folder!) is { } error)
                {
                    return McpToolResult.Error(error);
                }

                files = PluginAuthoring.FromFolder(folder!, PluginLimits.Folder, ct);
                limits = PluginLimits.Folder;
            }

            ct.ThrowIfCancellationRequested();
            var draft = PluginAuthoring.Prepare(files, pluginsRoot, sdkClient, limits);
            var findings = PluginSourceCheck.Scan(draft.Files);
            if (findings.Count > 0)
            {
                return new McpToolResult(PluginSourceCheck.FixRequest(findings));
            }

            var m = draft.Manifest;
            var permissions = m.Permissions.Count == 0 ? "none" : string.Join(", ", m.Permissions);
            return new McpToolResult($"OK: {m.Name} ({m.Id}) {m.Version}, runtime {m.Runtime?.Type}, permissions: {permissions}, {draft.Files.Count} files");
        }
        catch (OperationCanceledException)
        {
            return McpToolResult.Error("The request was cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            // Validation problems are the answer of this tool, not a protocol error.
            return new McpToolResult(ex.Message);
        }
    }

    /// <summary>Why <paramref name="folder"/> cannot be read as a plugin folder (not a local disk path, missing), or <c>null</c>.</summary>
    public static string? LocalFolderError(string folder)
    {
        if (VfsPath.GetScheme(folder) != VfsPath.FileScheme || !Path.IsPathFullyQualified(folder))
        {
            return "Only local folders are supported (a disk path such as C:\\plugins\\mine).";
        }

        return Directory.Exists(folder) ? null : $"Folder not found: {folder}";
    }

    /// <summary>Input schema of tools that take a plugin as <c>files</c> ({path, content}) or a local <c>folder</c>.</summary>
    public static JsonObject FilesOrFolderSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["files"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Plugin files: objects with path (relative) and content (text).",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string" },
                        ["content"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("path", "content"),
                },
            },
            ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "Local plugin folder (disk path)." },
        },
    };

    /// <summary>Reads the <c>files</c> argument; throws <see cref="ArgumentException"/> on a malformed entry.</summary>
    public static List<GeneratedFile> ReadFiles(JsonArray array)
    {
        var files = new List<GeneratedFile>();
        foreach (var node in array)
        {
            if (node is not JsonObject item || item["path"] is not JsonValue path || item["content"] is not JsonValue content
                || !path.TryGetValue<string>(out var p) || !content.TryGetValue<string>(out var c))
            {
                throw new ArgumentException("Each entry of 'files' needs a string 'path' and a string 'content'.");
            }

            files.Add(new GeneratedFile(p, c));
        }

        return files;
    }

    private static McpToolResult Log(McpCall call, AuditLog audit, Func<string, bool> isExternalPlugin)
    {
        var id = call.Required("id");
        if (id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase) || id.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase)
            || !isExternalPlugin(id))
        {
            return McpToolResult.Error($"'{id}' is not an installed external plugin; only their logs are available.");
        }

        var count = DefaultLogLines;
        if (call.Optional("count") is { } text)
        {
            if (!int.TryParse(text, out count))
            {
                return McpToolResult.Error("'count' must be an integer.");
            }

            count = Math.Clamp(count, 1, MaxLogLines);
        }

        var entries = audit.Recent(id);
        if (entries.Count == 0)
        {
            return new McpToolResult($"No log entries for {id}.");
        }

        var log = new StringBuilder();
        foreach (var entry in entries.Skip(Math.Max(0, entries.Count - count)))
        {
            log.AppendLine(entry.ToString());
        }

        return new McpToolResult(log.ToString());
    }
}
