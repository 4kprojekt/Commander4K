using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Commander4K.Core.Ai;

public sealed record AiMessage(string Role, string Content)
{
    /// <summary>Tools the assistant asked for (assistant messages only).</summary>
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = [];

    /// <summary>The call this message answers (role <c>tool</c>).</summary>
    public string? ToolCallId { get; init; }

    public static AiMessage System(string content) => new("system", content);

    public static AiMessage User(string content) => new("user", content);

    public static AiMessage Assistant(string content) => new("assistant", content);

    public static AiMessage Tool(AiToolCall call, string result) => new("tool", result) { ToolCallId = call.Id };

    /// <summary>
    /// The conversation for a model without tool support: tool calls become assistant text and tool results become user
    /// messages, so the model still sees what was looked up.
    /// </summary>
    public static IReadOnlyList<AiMessage> WithoutTools(IReadOnlyList<AiMessage> messages)
    {
        var calls = messages.SelectMany(m => m.ToolCalls).ToDictionary(c => c.Id, c => c.Name);
        return [.. messages.Select(m => m switch
        {
            { Role: "tool" } => User($"[result of {calls.GetValueOrDefault(m.ToolCallId ?? string.Empty, "tool")}]\n{m.Content}"),
            { ToolCalls.Count: > 0 } => Assistant((m.Content + "\n" + string.Join("\n", m.ToolCalls.Select(c => $"[calling {c.Name}({c.Arguments})]"))).Trim()),
            _ => m,
        })];
    }
}

/// <summary>A function the assistant may call; <paramref name="Parameters"/> is a JSON schema object.</summary>
public sealed record AiTool(string Name, string Description, JsonObject Parameters);

/// <param name="Arguments">The arguments as the JSON text the model produced (may be invalid; parse defensively).</param>
public sealed record AiToolCall(string Id, string Name, string Arguments);

/// <summary>A reply: text, tool calls, or both.</summary>
public sealed record AiCompletion(string Text, IReadOnlyList<AiToolCall> ToolCalls);

/// <summary>Why a provider call failed, which decides what the router does next.</summary>
public enum AiFailureKind : byte
{
    /// <summary>Too many requests: this model waits (Retry-After), the next model or provider is tried.</summary>
    RateLimited,

    /// <summary>Daily/monthly quota or credit used up: the provider waits longer.</summary>
    QuotaExhausted,

    /// <summary>Bad, expired or revoked key: the provider is skipped until the user signs in again.</summary>
    Unauthorized,

    /// <summary>The model does not exist (any more) or is not available to this key: the next model is tried.</summary>
    ModelUnavailable,

    /// <summary>Server error, timeout, network: the provider waits briefly.</summary>
    Transient,

    /// <summary>The request itself was refused (too long, content policy) — another provider may accept it.</summary>
    Rejected,

    /// <summary>The model does not support tool calls: the same model is asked again without tools.</summary>
    ToolsUnsupported,
}

public sealed class AiProviderException(AiFailureKind kind, string message, TimeSpan? retryAfter = null, int? status = null) : Exception(message)
{
    public AiFailureKind Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public int? Status { get; } = status;
}

/// <summary>Chat completions and model lists for any OpenAI-compatible endpoint (all catalogue providers use one).</summary>
public sealed class OpenAiCompatibleClient(HttpClient http)
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Model ids that are not chat models (moderation, embeddings, speech) and never answer a question.</summary>
    private static readonly string[] NonChat = ["safety", "guard", "embed", "whisper", "tts", "rerank", "moderation", "ocr", "transcribe"];

    public async Task<string> CompleteAsync(AiProviderInfo provider, string? key, string model, IReadOnlyList<AiMessage> messages,
        CancellationToken cancellationToken) =>
        (await CompleteAsync(provider, key, model, messages, null, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>One chat turn; with <paramref name="tools"/> the reply may ask for tool calls instead of (or besides) text.</summary>
    public async Task<AiCompletion> CompleteAsync(AiProviderInfo provider, string? key, string model, IReadOnlyList<AiMessage> messages,
        IReadOnlyList<AiTool>? tools, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray([.. messages.Select(Serialize)]),
            ["stream"] = false,
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            })]);
        }

        using var request = Request(HttpMethod.Post, provider, key, "/chat/completions");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        string json;
        try
        {
            json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AiProviderException ex) when (tools is { Count: > 0 } && ex.Kind is AiFailureKind.Rejected or AiFailureKind.ModelUnavailable
            && (ex.Message.Contains("tool", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("function", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AiProviderException(AiFailureKind.ToolsUnsupported, ex.Message, status: ex.Status);
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node?["error"] is { } error)
            {
                // OpenRouter sometimes reports upstream failures with 200 and an error object.
                throw Classify(error["code"]?.GetValueKind() == JsonValueKind.Number ? error["code"]!.GetValue<int>() : 500, error.ToJsonString(), null);
            }

            var message = node?["choices"]?[0]?["message"];
            var content = message?["content"];
            var text = content?.GetValueKind() == JsonValueKind.String ? content.GetValue<string>() : string.Empty;
            var calls = (message?["tool_calls"] as JsonArray ?? [])
                .Select((call, index) => new AiToolCall(
                    call?["id"]?.ToString() is { Length: > 0 } id ? id : "call_" + index,
                    call?["function"]?["name"]?.ToString() ?? string.Empty,
                    call?["function"]?["arguments"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : call?["function"]?["arguments"]?.ToJsonString() ?? "{}"))
                .Where(c => c.Name.Length > 0)
                .ToList();
            return text.Length > 0 || calls.Count > 0
                ? new AiCompletion(text, calls)
                : throw new AiProviderException(AiFailureKind.Transient, "The response has no message.");
        }
        catch (JsonException ex)
        {
            throw new AiProviderException(AiFailureKind.Transient, "Invalid JSON in the response: " + ex.Message);
        }
    }

    /// <summary>Model ids from <c>/models</c>; <paramref name="freeOnly"/> keeps OpenRouter's zero-priced ones.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(AiProviderInfo provider, string? key, bool freeOnly, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, provider, key, "/models");
        var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var data = JsonNode.Parse(json)?["data"]?.AsArray() ?? [];
        return [.. data
            .Where(m => m is not null && IsChat(m) && (!freeOnly || IsFree(m)))
            .Select(m => m!["id"]?.GetValue<string>())
            .OfType<string>()
            .Where(id => !NonChat.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase)))];

        static bool IsFree(JsonNode model) =>
            model["pricing"] is { } pricing && IsZero(pricing["prompt"]) && IsZero(pricing["completion"]);

        // OpenRouter describes output modalities; music or image generators answer with more than text.
        static bool IsChat(JsonNode model) =>
            model["architecture"]?["output_modalities"] is not JsonArray outputs || outputs.All(o => o?.ToString() == "text");

        static bool IsZero(JsonNode? price) =>
            price is not null && decimal.TryParse(price.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value == 0;
    }

    private static JsonNode Serialize(AiMessage message)
    {
        var node = new JsonObject { ["role"] = message.Role, ["content"] = message.Content };
        if (message.ToolCallId is { } id)
        {
            node["tool_call_id"] = id;
        }

        if (message.ToolCalls.Count > 0)
        {
            node["tool_calls"] = new JsonArray([.. message.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            })]);
        }

        return node;
    }

    private static HttpRequestMessage Request(HttpMethod method, AiProviderInfo provider, string? key, string path)
    {
        var request = new HttpRequestMessage(method, provider.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        foreach (var (name, value) in provider.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiFailureKind.Transient, "No answer within " + RequestTimeout.TotalSeconds + " s.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiFailureKind.Transient, ex.Message);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return text;
            }

            throw Classify((int)response.StatusCode, text, RetryAfter(response));
        }
    }

    internal static AiProviderException Classify(int status, string body, TimeSpan? retryAfter)
    {
        var message = Summarize(body, status);
        var lower = body.ToLowerInvariant();
        bool Mentions(params string[] words) => words.Any(lower.Contains);
        var kind = status switch
        {
            401 => AiFailureKind.Unauthorized,
            402 => AiFailureKind.QuotaExhausted,
            403 when Mentions("quota", "credit", "limit") => AiFailureKind.QuotaExhausted,
            403 => AiFailureKind.Unauthorized,
            404 => AiFailureKind.ModelUnavailable,
            429 when Mentions("quota", "per day", "per-day", "daily", "credits", "billing", "free-models-per-day", "exceeded your current") => AiFailureKind.QuotaExhausted,
            429 => AiFailureKind.RateLimited,
            400 or 422 when Mentions("model") && Mentions("not found", "does not exist", "not supported", "unknown", "invalid model", "not a valid", "unavailable") => AiFailureKind.ModelUnavailable,
            400 or 413 or 422 => AiFailureKind.Rejected,
            _ => AiFailureKind.Transient,
        };
        return new AiProviderException(kind, message, retryAfter, status);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry)
        {
            return retry.Delta ?? (retry.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        }

        // Some providers only send the reset time of their window.
        foreach (var name in new[] { "x-ratelimit-reset-requests", "x-ratelimit-reset" })
        {
            if (response.Headers.TryGetValues(name, out var values) && ParseDuration(values.FirstOrDefault()) is { } delay)
            {
                return delay;
            }
        }

        return null;
    }

    /// <summary>Accepts seconds (<c>12</c>, <c>1.5</c>), Groq style (<c>2m59.56s</c>, <c>7.66s</c>) or a Unix time in ms.</summary>
    internal static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, invariant, out var number))
        {
            return number > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)number) - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(number);
        }

        var total = TimeSpan.Zero;
        foreach (System.Text.RegularExpressions.Match part in System.Text.RegularExpressions.Regex.Matches(value, @"([\d.]+)(ms|h|m|s)"))
        {
            var amount = double.Parse(part.Groups[1].Value, invariant);
            total += part.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "ms" => TimeSpan.FromMilliseconds(amount),
                _ => TimeSpan.FromSeconds(amount),
            };
        }

        return total > TimeSpan.Zero ? total : null;
    }

    private static string Summarize(string body, int status)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var error = node?["error"];
            var text = error?.GetValueKind() == JsonValueKind.String ? error.GetValue<string>()
                : error?["message"]?.ToString() ?? node?["message"]?.ToString() ?? node?["detail"]?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return $"HTTP {status}: {Trim(text)}";
            }
        }
        catch (JsonException)
        {
        }

        return $"HTTP {status}: {Trim(body)}";

        static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
    }
}
