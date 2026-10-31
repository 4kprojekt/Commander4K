using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Commander4K.Core.Mcp;

/// <summary>Executes tools for the protocol layer; the policy dispatcher implements it.</summary>
public interface IMcpToolHandler
{
    IReadOnlyList<McpToolDefinition> List();

    Task<McpToolResult> CallAsync(string name, McpCall call, CancellationToken ct);
}

public sealed class McpSession
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();

    internal McpSession(string id) => Id = id;

    public string Id { get; }

    public string ClientName { get; internal set; } = "unknown";

    public string? ProtocolVersion { get; internal set; }

    internal void Register(string requestId, CancellationTokenSource source) => _inFlight[requestId] = source;

    internal void Unregister(string requestId, CancellationTokenSource source) =>
        _inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(requestId, source));

    internal void Cancel(string requestId)
    {
        if (!_inFlight.TryGetValue(requestId, out var source))
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call finished at the same moment.
        }
    }

    internal void CancelAll()
    {
        foreach (var requestId in _inFlight.Keys)
        {
            Cancel(requestId);
        }
    }
}

/// <summary>Transport-independent MCP (2025-06-18) server: tools and prompts over JSON-RPC.</summary>
public sealed class McpServer(IMcpToolHandler tools, Func<IReadOnlyList<McpPrompt>> prompts, string serverVersion)
{
    private const string LatestProtocol = "2025-06-18";
    private static readonly string[] SupportedProtocols = [LatestProtocol, "2025-03-26"];

    private readonly ConcurrentDictionary<string, McpSession> _sessions = new();

    public event Action? SessionsChanged;

    public int SessionCount => _sessions.Count;

    public McpSession CreateSession()
    {
        var session = new McpSession(Guid.NewGuid().ToString("N"));
        _sessions[session.Id] = session;
        SessionsChanged?.Invoke();
        return session;
    }

    public McpSession? FindSession(string id) => _sessions.GetValueOrDefault(id);

    public bool CloseSession(string id)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        session.CancelAll();
        SessionsChanged?.Invoke();
        return true;
    }

    /// <summary>Returns the reply, or <c>null</c> for notifications and client responses.</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject message, McpSession session, CancellationToken ct)
    {
        message.TryGetPropertyValue("id", out var id);
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : null;
        if (method is null)
        {
            // A response from the client, or a request without a method.
            return id is not null && !message.ContainsKey("result") && !message.ContainsKey("error")
                ? McpJson.Error(id, McpJson.InvalidRequest, "Missing method.")
                : null;
        }

        var parameters = message["params"] as JsonObject ?? new JsonObject();
        if (id is null)
        {
            if (method == "notifications/cancelled" && parameters["requestId"] is { } target)
            {
                session.Cancel(target.ToJsonString());
            }

            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => McpJson.Result(id, Initialize(parameters, session)),
                "ping" => McpJson.Result(id, new JsonObject()),
                "tools/list" => McpJson.Result(id, ListTools()),
                "tools/call" => await CallToolAsync(id, parameters, session, ct).ConfigureAwait(false),
                "prompts/list" => McpJson.Result(id, ListPrompts()),
                "prompts/get" => await GetPromptAsync(id, parameters, ct).ConfigureAwait(false),
                _ => McpJson.Error(id, McpJson.MethodNotFound, $"Method not found: {method}"),
            };
        }
        catch (ArgumentException ex)
        {
            return McpJson.Error(id, McpJson.InvalidParams, ex.Message);
        }
    }

    /// <summary>Reads an optional string parameter; any other JSON type is invalid params.</summary>
    private static string? StringParam(JsonNode? node, string name)
    {
        if (node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new ArgumentException($"Parameter '{name}' must be a string.");
    }

    private JsonObject Initialize(JsonObject parameters, McpSession session)
    {
        var requested = StringParam(parameters["protocolVersion"], "protocolVersion");
        string? client = null;
        if (parameters["clientInfo"] is { } info)
        {
            client = info is JsonObject infoObject
                ? StringParam(infoObject["name"], "clientInfo.name")
                : throw new ArgumentException("Parameter 'clientInfo' must be an object.");
        }

        session.ProtocolVersion = requested is not null && SupportedProtocols.Contains(requested) ? requested : LatestProtocol;
        if (client is { Length: > 0 })
        {
            session.ClientName = client;
        }

        return new JsonObject
        {
            ["protocolVersion"] = session.ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["prompts"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject { ["name"] = "commander4k", ["version"] = serverVersion },
            ["instructions"] = "Commander4K is a dual-pane file manager. Tools act on the user's panels and files; "
                + "the user may be asked to confirm some calls.",
        };
    }

    private JsonObject ListTools()
    {
        var array = new JsonArray();
        foreach (var tool in tools.List())
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return new JsonObject { ["tools"] = array };
    }

    private async Task<JsonObject> CallToolAsync(JsonNode id, JsonObject parameters, McpSession session, CancellationToken ct)
    {
        var name = parameters["name"] is JsonValue v && v.TryGetValue<string>(out var n) ? n : null;
        if (string.IsNullOrEmpty(name))
        {
            return McpJson.Error(id, McpJson.InvalidParams, "Missing tool name.");
        }

        if (!tools.List().Any(t => t.Name == name))
        {
            return McpJson.Error(id, McpJson.InvalidParams, $"Unknown tool: {name}");
        }

        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        var key = id.ToJsonString();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.Register(key, source);
        try
        {
            var result = await tools.CallAsync(name, new McpCall(session.ClientName, arguments), source.Token).ConfigureAwait(false);
            return McpJson.Result(id, McpJson.TextResult(result.Text, result.IsError));
        }
        catch (OperationCanceledException)
        {
            return McpJson.Result(id, McpJson.TextResult("The request was cancelled.", true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or TimeoutException)
        {
            return McpJson.Result(id, McpJson.TextResult(ex.Message, true));
        }
        finally
        {
            session.Unregister(key, source);
        }
    }

    private JsonObject ListPrompts()
    {
        var array = new JsonArray();
        foreach (var prompt in prompts())
        {
            var arguments = new JsonArray();
            foreach (var a in prompt.Arguments)
            {
                arguments.Add(new JsonObject { ["name"] = a.Name, ["description"] = a.Description, ["required"] = a.Required });
            }

            array.Add(new JsonObject { ["name"] = prompt.Name, ["description"] = prompt.Description, ["arguments"] = arguments });
        }

        return new JsonObject { ["prompts"] = array };
    }

    private async Task<JsonObject> GetPromptAsync(JsonNode id, JsonObject parameters, CancellationToken ct)
    {
        var name = parameters["name"] is JsonValue v && v.TryGetValue<string>(out var n) ? n : null;
        var prompt = prompts().FirstOrDefault(p => p.Name == name);
        if (prompt is null)
        {
            return McpJson.Error(id, McpJson.InvalidParams, $"Unknown prompt: {name}");
        }

        var values = new Dictionary<string, string>();
        if (parameters["arguments"] is JsonObject given)
        {
            foreach (var (key, value) in given)
            {
                if (value is not null)
                {
                    values[key] = value is JsonValue s && s.TryGetValue<string>(out var str) ? str : value.ToJsonString();
                }
            }
        }

        var missing = prompt.Arguments.FirstOrDefault(a => a.Required && !values.ContainsKey(a.Name));
        if (missing is not null)
        {
            return McpJson.Error(id, McpJson.InvalidParams, $"Missing prompt argument '{missing.Name}'.");
        }

        string text;
        try
        {
            text = await prompt.Render(values, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            return McpJson.Error(id, McpJson.InternalError, ex.Message);
        }

        return McpJson.Result(id, new JsonObject
        {
            ["description"] = prompt.Description,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
            }),
        });
    }
}
