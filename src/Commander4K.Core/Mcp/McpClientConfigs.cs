using System.Text.Json;
using System.Text.Json.Nodes;
using Commander4K.Core.Localization;

namespace Commander4K.Core.Mcp;

/// <summary>How one MCP client connects: its name, where the text goes, and the text with the URL and token filled in.</summary>
public sealed record McpClientConfig(string Name, string Where, string Text);

/// <summary>Connection instructions for the MCP clients people use, built from the server's current URL and token.</summary>
public static class McpClientConfigs
{
    public const string ServerName = "commander4k";

    public static IReadOnlyList<McpClientConfig> For(string url, string? token)
    {
        var bearer = "Bearer " + token;
        return
        [
            new("Claude Code", Loc.T("Uruchom raz w terminalu; potem „claude mcp list” pokazuje commander4k."),
                $"claude mcp add --transport http {ServerName} {url} --header \"Authorization: {bearer}\""),
            new(".mcp.json", Loc.T("Plik .mcp.json w folderze projektu (Claude Code bez polecenia „claude mcp add”)."),
                Json("mcpServers", new() { ["type"] = "http", ["url"] = url, ["headers"] = Headers(bearer) })),
            new("Codex", Loc.T("Dopisz do %USERPROFILE%\\.codex\\config.toml i uruchom Codex ponownie."),
                $"[mcp_servers.{ServerName}]\nurl = \"{url}\"\nhttp_headers = {{ \"Authorization\" = \"{bearer}\" }}"),
            new("Gemini CLI", Loc.T("Dopisz do %USERPROFILE%\\.gemini\\settings.json (klucz mcpServers); /mcp pokazuje stan."),
                Json("mcpServers", new() { ["httpUrl"] = url, ["headers"] = Headers(bearer) })),
            new("Cursor", Loc.T("Dopisz do %USERPROFILE%\\.cursor\\mcp.json albo .cursor\\mcp.json w projekcie."),
                Json("mcpServers", new() { ["url"] = url, ["headers"] = Headers(bearer) })),
            new("VS Code", Loc.T("Plik .vscode\\mcp.json w projekcie (GitHub Copilot, tryb agenta)."),
                Json("servers", new() { ["type"] = "http", ["url"] = url, ["headers"] = Headers(bearer) })),
            new("Windsurf", Loc.T("Dopisz do %USERPROFILE%\\.codeium\\windsurf\\mcp_config.json i odśwież serwery MCP."),
                Json("mcpServers", new() { ["serverUrl"] = url, ["headers"] = Headers(bearer) })),
            // For any other agent: an instruction it can carry out itself (English, because it is for the model).
            new(Loc.T("Inny agent"), Loc.T("Wklej agentowi jako polecenie — sam dopisze serwer do swojej konfiguracji."),
                $"Add an MCP server named \"{ServerName}\" to your MCP configuration. Transport: Streamable HTTP. URL: {url}. "
                + $"Every request must send the header \"Authorization: {bearer}\". It runs only on this computer (127.0.0.1) "
                + "while Commander4K is open. After adding it, reconnect or restart so its tools load, then call its get_context tool to check the connection."),
        ];
    }

    private static JsonObject Headers(string bearer) => new() { ["Authorization"] = bearer };

    private static string Json(string rootKey, JsonObject server) =>
        new JsonObject { [rootKey] = new JsonObject { [ServerName] = server } }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
