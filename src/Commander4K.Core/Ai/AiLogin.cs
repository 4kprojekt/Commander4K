using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace Commander4K.Core.Ai;

/// <summary>Ways of signing in that need no key copied by hand.</summary>
public static class AiLogin
{
    public const string OpenRouterAuthUrl = "https://openrouter.ai/auth";
    public const string OpenRouterKeysUrl = "https://openrouter.ai/api/v1/auth/keys";

    /// <summary>
    /// OpenRouter OAuth PKCE: listens on a free loopback port, opens the sign-in page with that callback, waits for the
    /// redirect carrying <c>code</c> and exchanges it (with the verifier) for an API key created for this app.
    /// </summary>
    public static async Task<string> OpenRouterAsync(HttpClient http, Func<string, Task> openBrowser, CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var callback = $"http://127.0.0.1:{port}/callback";
        await openBrowser($"{OpenRouterAuthUrl}?callback_url={Uri.EscapeDataString(callback)}&code_challenge={challenge}&code_challenge_method=S256").ConfigureAwait(false);

        string? code = null;
        while (code is null)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
            var target = requestLine.Split(' ') is [_, var path, ..] ? path : "/";
            if (!target.StartsWith("/callback", StringComparison.Ordinal))
            {
                await RespondAsync(stream, 404, "Not found", cancellationToken).ConfigureAwait(false); // e.g. /favicon.ico
                continue;
            }

            var query = HttpUtility.ParseQueryString(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : string.Empty);
            code = query["code"];
            await RespondAsync(stream, code is null ? 400 : 200, code is null
                ? "Sign-in failed — no code was returned. You can close this tab."
                : "Commander4K is signed in to OpenRouter. You can close this tab.", cancellationToken).ConfigureAwait(false);
            if (code is null)
            {
                throw new InvalidOperationException("OpenRouter returned no authorization code.");
            }
        }

        var body = new JsonObject { ["code"] = code, ["code_verifier"] = verifier, ["code_challenge_method"] = "S256" };
        using var response = await http.PostAsync(OpenRouterKeysUrl, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenRouter refused the key exchange (HTTP {(int)response.StatusCode}): {json}");
        }

        return JsonNode.Parse(json)?["key"]?.GetValue<string>() ?? throw new InvalidOperationException("OpenRouter returned no key.");
    }

    /// <summary>The GitHub CLI's token (<c>gh auth token</c>), or <c>null</c> when gh is missing or signed out. Blocks up to 5 s.</summary>
    public static string? GitHubCliToken()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh", "auth token")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }

            var token = output.Result.Trim();
            return process.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Reads the request head (up to the blank line) so the browser is not reset mid-send; only the first line matters.
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        var head = Encoding.ASCII.GetString(buffer, 0, total);
        var end = head.IndexOf("\r\n", StringComparison.Ordinal);
        return end >= 0 ? head[..end] : head;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>Commander4K</title><body style=\"font:16px Segoe UI,sans-serif;padding:40px\">{WebUtility.HtmlEncode(message)}</body>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
