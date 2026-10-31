using System.Text.Json.Nodes;

namespace Commander4K.Core.Mcp;

/// <summary>What happens when a tool is called and nothing else decides.</summary>
public enum McpToolMode : byte
{
    Allow,
    Ask,
    Block,
}

/// <param name="PathArguments">Arguments holding paths (string or string array), checked against the allowed and
/// protected folders and normalized before the handler runs. Hard rule: handlers must never substitute a default for
/// a declared path argument; any path a handler derives itself (active panel, selection) must be checked with
/// <see cref="McpPathPolicy.IsAllowed"/> inside the handler.</param>
/// <param name="SelfConfirming">The tool shows its own dialog (install/enable with permissions), so the dispatcher skips
/// its confirmation but still applies block, roots and protected paths.</param>
/// <param name="NamesOnly">The tool only reports names and metadata (list_directory, stat), so it may look into the
/// protected folders; every other tool is refused there.</param>
/// <param name="ReadsTree">A folder argument is read recursively (check_plugin, install_plugin), so a folder that
/// contains a protected folder is refused too, as for writing tools.</param>
public sealed record McpToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema,
    McpToolMode DefaultMode,
    bool AlwaysAsk = false,
    IReadOnlyList<string>? PathArguments = null,
    bool Writes = false,
    bool SelfConfirming = false,
    bool NamesOnly = false,
    bool ReadsTree = false);

public sealed record McpToolResult(string Text, bool IsError = false)
{
    public static McpToolResult Error(string text) => new(text, true);
}

public sealed record McpCall(string ClientName, JsonObject Arguments)
{
    public string Required(string name) =>
        Optional(name) ?? throw new ArgumentException($"Missing required argument '{name}'.");

    public string? Optional(string name)
    {
        if (!Arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();
    }
}

/// <param name="Summary">Human line for confirmation/audit (default: name + path arguments).</param>
public sealed record McpTool(
    McpToolDefinition Definition,
    Func<McpCall, CancellationToken, Task<McpToolResult>> Handler,
    Func<JsonObject, string>? Summary = null);

public sealed record McpPromptArgument(string Name, string Description, bool Required);

public sealed record McpPrompt(
    string Name,
    string Description,
    IReadOnlyList<McpPromptArgument> Arguments,
    Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> Render);

public static class McpSchema
{
    /// <summary>Builds <c>{type:"object", properties, required}</c>; types are JSON schema names.</summary>
    public static JsonObject Object(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            var property = new JsonObject { ["type"] = type, ["description"] = description };
            if (type == "array")
            {
                property["items"] = new JsonObject { ["type"] = "string" };
            }

            props[name] = property;
            if (isRequired)
            {
                required.Add(name);
            }
        }

        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
    }
}
