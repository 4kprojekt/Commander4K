using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Commander4K.Core.Plugins;

namespace Commander4K.Core.Mcp;

/// <summary>What the user is asked before a tool runs.</summary>
public sealed record McpConfirmation(string ClientName, string Tool, string Summary, bool AlwaysAsk);

public enum McpConsent : byte
{
    No,
    Yes,
    YesForSession,
}

/// <summary>One tool call as shown in the MCP settings (most recent calls).</summary>
public sealed record McpCallRecord(DateTimeOffset Time, string Client, string Tool, string Summary, AuditOutcome Outcome);

/// <summary>
/// The policy every MCP tool call passes through: tool mode (allow / ask / block), allowed folders, protected
/// folders for writing tools, the user's confirmation (one dialog at a time, "yes for this session" per client and
/// tool) and an audit line per call (<c>mcp:&lt;client&gt;</c>). Path arguments are replaced by their normalized form
/// (<see cref="McpPathPolicy.Normalize"/>) before the handler runs, so the handler acts on exactly what was checked.
/// </summary>
public sealed class McpToolDispatcher(
    IReadOnlyList<McpTool> tools,
    Func<McpSettings> settings,
    Func<IReadOnlyList<string>> protectedRoots,
    Func<McpConfirmation, CancellationToken, Task<McpConsent>> confirm,
    AuditLog audit) : IMcpToolHandler
{
    public const string BlockedMessage = "This tool is disabled in Commander4K settings (MCP).";
    public const string ProtectedMessage = "Commander4K's plugin and config folders cannot be changed through MCP. Use install_plugin.";
    public const string DeclinedMessage = "The user declined this action in Commander4K.";
    public const string DisabledSuffix = " (Disabled by the user in Commander4K.)";
    private const int RecentCapacity = 50;

    private readonly SemaphoreSlim _confirmGate = new(1, 1);
    private readonly ConcurrentDictionary<(string Client, string Tool), bool> _sessionGrants = new();
    private readonly LinkedList<McpCallRecord> _recent = new();
    private readonly Lock _recentGate = new();

    /// <summary>Raised after each call is recorded (on the caller's thread).</summary>
    public event Action? Called;

    public IReadOnlyList<McpCallRecord> Recent
    {
        get
        {
            lock (_recentGate)
            {
                return [.. _recent];
            }
        }
    }

    public static string OutsideRootsMessage(string path) =>
        $"Path is outside the folders allowed for MCP: {path}. Ask the user to add a folder in Commander4K (MCP settings → allowed folders).";

    public McpToolMode EffectiveMode(McpToolDefinition tool)
    {
        var mode = tool.DefaultMode;
        if (settings().ToolModes.TryGetValue(tool.Name, out var configured))
        {
            mode = configured?.Trim().ToLowerInvariant() switch
            {
                "allow" => McpToolMode.Allow,
                "ask" => McpToolMode.Ask,
                "block" => McpToolMode.Block,
                // A typo must not loosen the policy: unknown values ask at least.
                _ => (McpToolMode)Math.Max((byte)tool.DefaultMode, (byte)McpToolMode.Ask),
            };
        }

        return mode == McpToolMode.Allow && tool.AlwaysAsk ? McpToolMode.Ask : mode;
    }

    public void RevokeSessionGrants() => _sessionGrants.Clear();

    public IReadOnlyList<McpToolDefinition> List() =>
    [
        .. tools.Select(t => EffectiveMode(t.Definition) == McpToolMode.Block
            ? t.Definition with { Description = t.Definition.Description + DisabledSuffix }
            : t.Definition),
    ];

    public async Task<McpToolResult> CallAsync(string name, McpCall call, CancellationToken ct)
    {
        var tool = tools.FirstOrDefault(t => t.Definition.Name == name);
        if (tool is null)
        {
            Record(call, name, "unknown tool", AuditOutcome.Denied);
            return McpToolResult.Error($"Unknown tool: {name}");
        }

        var definition = tool.Definition;
        var arguments = call.Arguments;
        if (EffectiveMode(definition) == McpToolMode.Block)
        {
            Record(call, name, Summarize(tool, arguments, []), AuditOutcome.Denied);
            return McpToolResult.Error(BlockedMessage);
        }

        if (CheckPaths(definition, arguments, out var paths) is { } refusal)
        {
            Record(call, name, Summarize(tool, arguments, paths), AuditOutcome.Denied);
            return McpToolResult.Error(refusal);
        }

        var summary = Summarize(tool, arguments, paths);
        if (EffectiveMode(definition) == McpToolMode.Ask && !definition.SelfConfirming)
        {
            bool confirmed;
            try
            {
                confirmed = await ConfirmAsync(call.ClientName, definition, summary, ct).ConfigureAwait(false);
            }
            catch
            {
                // Treated like a failing handler: audited, then left to McpServer to map.
                Record(call, name, summary, AuditOutcome.Failed);
                throw;
            }

            if (!confirmed)
            {
                Record(call, name, summary, AuditOutcome.Denied);
                return McpToolResult.Error(DeclinedMessage);
            }
        }

        McpToolResult result;
        try
        {
            result = await tool.Handler(call, ct).ConfigureAwait(false);
        }
        catch
        {
            Record(call, name, summary, AuditOutcome.Failed);
            throw;
        }

        Record(call, name, summary, result.IsError ? AuditOutcome.Failed : AuditOutcome.Allowed);
        return result;
    }

    /// <summary>Checks and normalizes every path argument in place; returns the refusal message, if any.</summary>
    private string? CheckPaths(McpToolDefinition definition, JsonObject arguments, out List<string> paths)
    {
        paths = [];
        if (definition.PathArguments is not { Count: > 0 } names)
        {
            return null;
        }

        var current = settings();
        var roots = current.AllowedRoots;
        IReadOnlyList<string>? protectedList = definition.Writes ? protectedRoots() : null;
        foreach (var argument in names)
        {
            // An absent argument is fine (tools may take one of several); handlers never default it (see PathArguments).
            if (!arguments.TryGetPropertyValue(argument, out var node))
            {
                continue;
            }

            if (node is null)
            {
                return $"Argument '{argument}' must be a path.";
            }

            switch (node)
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    if (CheckPath(text, roots, protectedList, paths) is { } refusal)
                    {
                        return refusal;
                    }

                    arguments[argument] = Normalized(text);
                    break;

                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is not JsonValue item || !item.TryGetValue<string>(out var entry))
                        {
                            return $"Argument '{argument}' must be a list of paths.";
                        }

                        if (CheckPath(entry, roots, protectedList, paths) is { } arrayRefusal)
                        {
                            return arrayRefusal;
                        }

                        array[i] = Normalized(entry);
                    }

                    break;

                default:
                    return $"Argument '{argument}' must be a path.";
            }
        }

        return null;
    }

    private static string Normalized(string path) => McpPathPolicy.Normalize(path);

    private static string? CheckPath(string path, IReadOnlyList<string> roots, IReadOnlyList<string>? protectedList, List<string> paths)
    {
        paths.Add(Normalized(path));
        if (!McpPathPolicy.IsAllowed(path, roots))
        {
            return OutsideRootsMessage(path);
        }

        // Writing into a protected folder, or into a folder above one (move/delete), would change it.
        if (protectedList is not null
            && protectedList.Any(p => McpPathPolicy.IsInside(path, p) || McpPathPolicy.IsInside(p, path)))
        {
            return ProtectedMessage;
        }

        return null;
    }

    private async Task<bool> ConfirmAsync(string client, McpToolDefinition definition, string summary, CancellationToken ct)
    {
        var key = (client, definition.Name);
        if (!definition.AlwaysAsk && _sessionGrants.ContainsKey(key))
        {
            return true;
        }

        try
        {
            await _confirmGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            // Another call may have been granted for the session while this one waited.
            if (!definition.AlwaysAsk && _sessionGrants.ContainsKey(key))
            {
                return true;
            }

            var consent = await confirm(new McpConfirmation(client, definition.Name, summary, definition.AlwaysAsk), ct).ConfigureAwait(false);
            if (consent == McpConsent.YesForSession && !definition.AlwaysAsk)
            {
                _sessionGrants[key] = true;
            }

            return consent is McpConsent.Yes or McpConsent.YesForSession;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _confirmGate.Release();
        }
    }

    private static string Summarize(McpTool tool, JsonObject arguments, List<string> paths)
    {
        if (tool.Summary is not null)
        {
            try
            {
                return tool.Summary(arguments);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The summary sees unvalidated input; a broken one must not skip the audit line.
            }
        }

        var shown = paths.Count > 0 ? paths : RawPaths(tool.Definition, arguments);
        return shown.Count == 0 ? tool.Definition.Name : tool.Definition.Name + " " + string.Join(", ", shown);
    }

    private static List<string> RawPaths(McpToolDefinition definition, JsonObject arguments)
    {
        var result = new List<string>();
        foreach (var argument in definition.PathArguments ?? [])
        {
            switch (arguments[argument])
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    result.Add(text);
                    break;
                case JsonArray array:
                    result.AddRange(array.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var t) ? t : null).OfType<string>());
                    break;
            }
        }

        return result;
    }

    private void Record(McpCall call, string tool, string summary, AuditOutcome outcome)
    {
        audit.Record("mcp:" + call.ClientName, tool, summary, outcome);
        lock (_recentGate)
        {
            _recent.AddLast(new McpCallRecord(DateTimeOffset.Now, call.ClientName, tool, summary, outcome));
            if (_recent.Count > RecentCapacity)
            {
                _recent.RemoveFirst();
            }
        }

        try
        {
            Called?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A broken subscriber (UI) must not fail a call that already happened.
        }
    }
}
