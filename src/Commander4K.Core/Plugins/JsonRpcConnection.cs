using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Commander4K.Core.Localization;

namespace Commander4K.Core.Plugins;

public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerError = -32000;
    public const int PermissionDenied = -32001;
    public const int RequestCancelled = -32800;

    public int Code { get; } = code;
}

/// <summary>
/// A message from the plugin was longer than <see cref="JsonRpcConnection.MaxMessageLength"/>. The connection closes (the
/// plugin is restarted on its next call) and the calls waiting for an answer fail with this exception.
/// </summary>
public sealed class PluginMessageTooLargeException(string message) : IOException(message)
{
    public PluginMessageTooLargeException()
        : this(Loc.T("Odpowiedź wtyczki przekracza {0} MB; duże pliki wtyczka musi zwracać jako adres url.", JsonRpcConnection.MaxMessageLength / (1024 * 1024)))
    {
    }
}

/// <summary>
/// JSON-RPC 2.0 over a pair of streams, one message per line (NDJSON) — the plugin protocol transport. Both sides may
/// send requests; incoming requests are handled concurrently by <c>handler</c>.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    public const int MaxMessageLength = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Func<string, JsonElement?, CancellationToken, Task<object?>> _handler;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private Task _readLoop = Task.CompletedTask;
    private long _nextId;

    /// <summary>Why the connection closed, when there is a better explanation than "the plugin exited".</summary>
    private Exception? _closeReason;

    /// <param name="input">Messages from the other side (the plugin's stdout).</param>
    /// <param name="output">Messages to the other side (the plugin's stdin).</param>
    /// <param name="handler">Handles requests from the other side: (method, params) → result object serialised as JSON.</param>
    public JsonRpcConnection(TextReader input, TextWriter output, Func<string, JsonElement?, CancellationToken, Task<object?>> handler)
    {
        _input = input;
        _output = output;
        _handler = handler;
    }

    /// <summary>Completes when the other side closes the stream or sends invalid data.</summary>
    public Task Completion => _readLoop;

    public void Start() => _readLoop = Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        try
        {
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = ToNode(parameters) }).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() =>
            {
                _ = NotifyAsync("$/cancelRequest", new { id });
                pending.TrySetCanceled(cancellationToken);
            });
            using var closed = _closed.Token.Register(() => pending.TrySetException(ClosedError()));
            return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = ToNode(parameters) });

    public async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        try
        {
            // Reads from a pipe do not always observe cancellation; never let shutdown hang on one.
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
        }

        _writeGate.Dispose();
        _closed.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested && await _input.ReadLineAsync(_closed.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length > MaxMessageLength)
                {
                    // Pending calls (most likely the one this reply answers) fail with the reason, not with "the plugin exited".
                    var reason = new PluginMessageTooLargeException();
                    Volatile.Write(ref _closeReason, reason);
                    throw reason;
                }

                Dispatch(JsonDocument.Parse(line).RootElement.Clone());
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _closed.Cancel();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(ClosedError());
            }
        }
    }

    private Exception ClosedError() => Volatile.Read(ref _closeReason) is PluginMessageTooLargeException tooLarge
        ? new PluginMessageTooLargeException(tooLarge.Message)
        : new IOException(Loc.T("Wtyczka zakończyła działanie."));

    private void Dispatch(JsonElement message)
    {
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
        if (message.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString() ?? string.Empty;
            var parameters = message.TryGetProperty("params", out var p) && p.ValueKind != JsonValueKind.Null ? p : (JsonElement?)null;
            if (method == "$/cancelRequest")
            {
                if (parameters?.TryGetProperty("id", out var cancelled) == true && _incoming.TryGetValue(cancelled.ToString(), out var cts))
                {
                    cts.Cancel();
                }

                return;
            }

            if (hasId)
            {
                _ = HandleRequestAsync(id, method, parameters);
            }
            else
            {
                _ = SafeHandleAsync(method, parameters);
            }

            return;
        }

        if (hasId && id.TryGetInt64(out var responseId) && _pending.TryGetValue(responseId, out var waiting))
        {
            if (message.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : JsonRpcException.ServerError;
                var text = error.TryGetProperty("message", out var m) ? m.GetString() : Loc.T("Błąd wtyczki");
                waiting.TrySetException(new JsonRpcException(code, text ?? Loc.T("Błąd wtyczki")));
            }
            else
            {
                waiting.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
            }
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        var key = id.ToString();
        _incoming[key] = cts;
        JsonObject response;
        try
        {
            var result = await _handler(method, parameters, cts.Token).ConfigureAwait(false);
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = ToNode(result) };
        }
        catch (Exception ex)
        {
            var code = ex switch
            {
                JsonRpcException rpc => rpc.Code,
                UnauthorizedAccessException => JsonRpcException.PermissionDenied,
                OperationCanceledException => JsonRpcException.RequestCancelled,
                ArgumentException or KeyNotFoundException or FormatException or InvalidOperationException => JsonRpcException.InvalidParams,
                _ => JsonRpcException.ServerError,
            };
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = ex.Message },
            };
        }
        finally
        {
            _incoming.TryRemove(key, out _);
        }

        try
        {
            await WriteAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The plugin went away while we were answering it.
        }
    }

    private async Task SafeHandleAsync(string method, JsonElement? parameters)
    {
        try
        {
            await _handler(method, parameters, _closed.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Notifications have no response channel for errors.
        }
    }

    private async Task WriteAsync(JsonObject message)
    {
        var text = message.ToJsonString();
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(text + "\n").ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, Options),
    };
}
