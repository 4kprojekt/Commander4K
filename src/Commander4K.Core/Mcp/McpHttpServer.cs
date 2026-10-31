using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Commander4K.Core.Mcp;

public enum McpStartResult
{
    Started,
    PortBusy,
    Failed,
}

/// <summary>
/// Loopback-only Streamable HTTP transport for <see cref="McpServer"/>: bearer token, Origin check, one JSON reply per POST.
/// </summary>
public sealed class McpHttpServer(McpServer server, Func<string?> token) : IAsyncDisposable
{
    private const int MaxBodyBytes = 8 * 1024 * 1024;
    private const string SessionHeader = "Mcp-Session-Id";

    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _port;

    public bool IsRunning => _listener?.IsListening == true;

    public string Url => $"http://127.0.0.1:{_port}/mcp";

    public McpStartResult Start(int port)
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return McpStartResult.Failed;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                return ex.ErrorCode is 183 or 32 || ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)
                    ? McpStartResult.PortBusy
                    : McpStartResult.Failed;
            }

            _port = port;
            _listener = listener;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            return McpStartResult.Started;
        }
    }

    public async Task StopAsync()
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            (listener, cts, loop) = (_listener, _cts, _loop);
            (_listener, _cts, _loop) = (null, null, null);
        }

        if (listener is null)
        {
            return;
        }

        cts?.Cancel();
        try
        {
            listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        cts?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleSafelyAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            await HandleAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException
            or IOException or InvalidOperationException)
        {
            // Client went away or the server is stopping; nothing left to answer.
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Connection already gone.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath.TrimEnd('/');
        if (path != "/mcp")
        {
            response.StatusCode = 404;
            return;
        }

        if (!OriginAllowed(request.Headers["Origin"]))
        {
            response.StatusCode = 403;
            return;
        }

        if (!Authorized(request.Headers["Authorization"]))
        {
            response.StatusCode = 401;
            response.AddHeader("WWW-Authenticate", "Bearer");
            return;
        }

        var sessionId = request.Headers[SessionHeader];
        switch (request.HttpMethod)
        {
            case "DELETE":
                response.StatusCode = sessionId is null ? 400 : server.CloseSession(sessionId) ? 204 : 404;
                return;
            case "POST":
                await HandlePostAsync(context, sessionId, ct).ConfigureAwait(false);
                return;
            default:
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST, DELETE");
                return;
        }
    }

    private async Task HandlePostAsync(HttpListenerContext context, string? sessionId, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        if (request.ContentLength64 > MaxBodyBytes)
        {
            response.StatusCode = 413;
            return;
        }

        var body = await ReadBodyAsync(request.InputStream, ct).ConfigureAwait(false);
        if (body is null)
        {
            response.StatusCode = 413;
            return;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(response, 400, McpJson.Error(null, McpJson.ParseError, "Parse error."), ct).ConfigureAwait(false);
            return;
        }

        if (parsed is not JsonObject message)
        {
            await WriteJsonAsync(response, 400,
                McpJson.Error(null, McpJson.InvalidRequest, "Batches and non-object messages are not supported."), ct).ConfigureAwait(false);
            return;
        }

        McpSession? session;
        var isInitialize = (message["method"] as JsonValue)?.TryGetValue<string>(out var method) == true && method == "initialize";
        if (isInitialize)
        {
            session = server.CreateSession();
            response.AddHeader(SessionHeader, session.Id);
        }
        else if (sessionId is null)
        {
            response.StatusCode = 400;
            return;
        }
        else if ((session = server.FindSession(sessionId)) is null)
        {
            response.StatusCode = 404;
            return;
        }

        var reply = await server.HandleAsync(message, session, ct).ConfigureAwait(false);
        if (reply is null)
        {
            response.StatusCode = 202;
            return;
        }

        await WriteJsonAsync(response, 200, reply, ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadBodyAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, JsonObject json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1");
    }

    private bool Authorized(string? header)
    {
        var expected = token();
        if (string.IsNullOrEmpty(expected) || header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(expected));
    }
}
