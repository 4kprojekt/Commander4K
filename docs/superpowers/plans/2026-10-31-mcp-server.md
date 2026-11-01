# MCP Server (Plan A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove AI-through-the-browser and make Commander4K an MCP server (Streamable HTTP on loopback) that Claude Code can use to read app state, drive panels, work with files inside allowed folders, use plugins and write/install plugins — every tool gated by allow/ask/block.

**Architecture:** Protocol, transport, policy and VFS file tools live in `Commander4K.Core/Mcp` (UI-free, unit-tested). Tools that need the window (panels, commands, plugins, dialogs) live in `Commander4K.App/Mcp` and are registered into the Core dispatcher as delegates. One `McpToolDispatcher` enforces, in order: block → allowed roots / protected paths → mode (ask → confirmation) → run → audit.

**Tech Stack:** .NET 10, `System.Net.HttpListener`, `System.Text.Json` (`JsonNode`), Avalonia 12 + `Avalonia.Markup.Declarative` (C# only), xunit.v3.

**Spec:** `docs/superpowers/specs/2026-10-31-mcp-and-web-preview-design.md` (parts 1 and 2; parts 3–4 are Plan B).

## Global Constraints

- `TreatWarningsAsErrors` is on: every task ends with `dotnet build Commander4K.sln` clean.
- No new NuGet packages; no ASP.NET Core framework reference.
- Listen only on `http://127.0.0.1:<port>/mcp/`; server **off by default** (`McpSettings.Enabled = false`).
- Protocol: MCP Streamable HTTP, versions `2025-06-18` (preferred) and `2025-03-26`; JSON responses only (no SSE); `GET /mcp` → 405.
- Auth: `Authorization: Bearer <token>`, 32 random bytes base64url, stored with `SecretProtector.Protect` (DPAPI). A request with an `Origin` header whose host is not `127.0.0.1`/`localhost` → 403.
- Tool modes `allow` / `ask` / `block`; "always ask" tools never resolve to `allow` and never get "yes for this session".
- Every path argument outside `McpSettings.AllowedRoots` → blocked, whatever the mode. Empty list = everything blocked. Writes into installed plugin folders and the app's `config/` folder → blocked.
- "Yes for this session" = until the app closes, per (client name, tool); nothing written to disk.
- No I/O on the UI thread; tool calls run off it, UI only through `Dispatcher.UIThread`.
- UI text through `Loc.T(...)` with an `en.json` entry; tool descriptions, results for the model and audit lines stay English.
- New commands: `CommandIds` constant + Polish description in `MainWindow.RegisterCommands`.
- New options: `AppSettings` **and** `SettingsCatalog` (or `AppHost.ExtraSettings` for per-tool ones).
- Keep `Co-Authored-By` trailers in commits.

## Review Focus

- Path tricks against allowed roots: `C:\Repo\..\Windows`, `C:\Repos2` vs root `C:\Repo`, different case, trailing separators, `\\srv\share` vs `\\SRV\Share\x`, `sftp://h/a` vs `sftp://h/ab` — expected: only real descendants pass (tests in Task 3).
- Two tool calls needing confirmation at once, and a `notifications/cancelled` while a dialog is open — expected: dialogs one after another, cancelled call returns "declined", the other still runs (tests in Task 3 with a fake confirmer; manual check in Task 8).
- Port already taken when enabling — expected: status "port busy" in the connect window and settings, no crash, retry after changing the port (test in Task 2).
- Missing/wrong token, foreign `Origin`, unknown `Mcp-Session-Id` — expected 401/403/404 and no tool runs (tests in Task 2).
- Privilege escalation through configuration: `set_setting` on `mcp.enabled`, `mcp.roots`, `mcp:delete`, `pluginCatalogUrl`, `aiCatalogUrl`, `updateRepository` — expected: refused in every mode, also after "yes for this session"; `save_translation`/`save_skin` with ids like `..\..\x`, `C:\x`, `pl` — expected: refused, nothing written (tests in Task 9).
- `read_file` on a binary or 2 GB file — expected: text truncated at 60 000 chars or base64 of at most 256 KB with a note, read stops early, nothing loaded whole (test in Task 4).

---

## File Structure

Core (`src/Commander4K.Core/Mcp/`):
- `McpJson.cs` — JSON-RPC helpers (`Result`, `Error`, error codes), tool-result builders.
- `McpTool.cs` — `McpToolMode`, `McpToolDefinition`, `McpToolResult`, `McpTool`, `McpPrompt`, `McpConfirmation`, `McpConsent`.
- `McpServer.cs` — method dispatch (`initialize`, `ping`, `tools/list`, `tools/call`, `prompts/list`, `prompts/get`, notifications), sessions.
- `McpHttpServer.cs` — `HttpListener` transport, token/Origin/session checks.
- `McpPathPolicy.cs` — allowed-roots and protected-path checks.
- `McpToolDispatcher.cs` — policy pipeline, session grants, audit.
- `McpFileTools.cs` — `list_directory`, `stat`, `read_file`, `write_file`, `make_directory`, `move`, `delete` over `VfsRouter`.
- `McpSettings.cs` — settings class + token helpers.
- `McpConfigTools.cs` — `list_settings`, `set_setting`, `list_key_bindings`, `set_key_binding`, translation tools, skin read/check tools (Task 9).

App (`src/Commander4K.App/Mcp/`):
- `McpAppTools.cs` — context, commands, panels, plugins tools.
- `McpPrompts.cs` — the three MCP prompts.
- `McpConfirmDialog.cs` — confirmation dialog (`DialogWindow<McpConsent>`).
- `McpConnectWindow.cs` — `cm_McpConnect` window (command line to copy, status, token, roots).
- `McpHost.cs` — owns server + dispatcher, start/stop on settings, status, revoke grants.
- `UseClaudeCodeDialog.cs` — fallback when no in-app AI provider (Task 1, extended in Task 8).
- `McpSkinTools.cs` — `save_skin` with the live-preview confirmation (Task 9).

Tests (`tests/Commander4K.Core.Tests/`): `McpServerTests.cs`, `McpHttpServerTests.cs`, `McpPolicyTests.cs`, `McpFileToolsTests.cs`, `McpPluginToolsTests.cs`, `McpConfigToolsTests.cs`.

---

### Task 1: Remove AI through the browser

**Files:**
- Modify: `src/Commander4K.App/Browser/BrowserPanelView.cs` (remove `AiMenuButton`, `AskAssistantAsync`, `ContextNote`, `CurrentAssistant`, `WritePluginAsync`, `InstallPluginFromPageAsync`, `TypeIntoChatAsync`, `ReadCodeBlocksAsync`, properties `Ai`, `InstallPlugin`; change `AskWithPromptAsync`)
- Delete: `src/Commander4K.App/Browser/PageScripts.cs`
- Modify: `src/Commander4K.Core/Plugins/PluginAuthoring.cs:108-113` (remove `FromBlocks`), class summary
- Modify: `src/Commander4K.App/MainWindow.cs:692-696, 715-730, 1245-1310`
- Create: `src/Commander4K.App/Mcp/UseClaudeCodeDialog.cs`
- Modify: `src/Commander4K.Core/Localization/i18n/en.json`, `CLAUDE.md` (the "🤖 AI" / `PageScripts` bullets → MCP sentence), `docs/plugins.md` if it mentions the browser assistant install
- Test: `tests/Commander4K.Core.Tests/PluginAuthoringTests.cs`

**Interfaces:**
- Produces: `UseClaudeCodeDialog(SkinBrushes brushes, string prompt)` : `DialogWindow<bool>` — shows the prompt (read-only, selectable), copies it to the clipboard on open, text "Wklej to w Claude Code połączonym z Commander4K przez MCP"; Task 8 adds the MCP status/connect button. `MainWindow.UseClaudeCodeAsync(string prompt)` shows it.
- Kept on purpose: `SearchEngine.IsAssistant`, `ChatUrl`, `AppSettings.AssistantEngine` and `ui.search` with `"assistant"` (plugin API) — they only open pages now.

- [ ] **Step 1: Delete `Blocks_read_from_a_page_use_the_label_text` from `PluginAuthoringTests.cs`**; run `dotnet test --solution Commander4K.sln -- --filter-class "*PluginAuthoringTests"` — PASS (nothing else uses blocks).
- [ ] **Step 2: Remove `PluginAuthoring.FromBlocks`, `PageScripts.cs` and the browser members listed above.** `AskWithPromptAsync(prompt, engine)` becomes: copy to clipboard, navigate to `engine.ChatUrl` if not already on that host, notify `Loc.T("Pytanie jest w schowku — wklej je na stronie (Ctrl+V).")`. Remove the "🤖 AI ▾" button from the toolbar layout.
- [ ] **Step 3: In `MainWindow`:** `OpenBrowser` no longer sets `Ai`/`InstallPlugin`. `AskAiAsync`, `AskAiAboutFileAsync`, `CreatePreviewPluginAsync`, `WritePluginWithAiAsync`: keep the in-app chat branch; the else-branch calls `UseClaudeCodeAsync(prompt)` where prompt is the wish/question plus the file sample (no guide pasted; end with `"Use the commander4k MCP tools (get_plugin_guide, check_plugin, install_plugin) to write and install it."` for plugin requests).
- [ ] **Step 4: Update `en.json`** (add new keys, remove stale ones) and the CLAUDE.md bullets.
- [ ] **Step 5: Verify:** `dotnet build Commander4K.sln` clean; `dotnet test --solution Commander4K.sln` — all PASS (`LocalizationTests` proves en.json).
- [ ] **Step 6: Commit** `Remove AI through the browser; Claude Code fallback dialog`.

---

### Task 2: MCP protocol and HTTP transport (Core)

**Files:**
- Create: `src/Commander4K.Core/Mcp/McpJson.cs`, `McpTool.cs`, `McpServer.cs`, `McpHttpServer.cs`
- Test: `tests/Commander4K.Core.Tests/McpServerTests.cs`, `McpHttpServerTests.cs`

**Interfaces:**
- Produces (in `McpTool.cs`, namespace `Commander4K.Core.Mcp`):
  - `enum McpToolMode : byte { Allow, Ask, Block }`
  - `sealed record McpToolDefinition(string Name, string Description, JsonObject InputSchema, McpToolMode DefaultMode, bool AlwaysAsk = false, IReadOnlyList<string>? PathArguments = null, bool Writes = false, bool SelfConfirming = false)` — `SelfConfirming` tools show their own dialog (install/enable with permissions), so the dispatcher skips its confirmation for them but still applies block, roots and protected paths
  - `sealed record McpToolResult(string Text, bool IsError = false)` with `static McpToolResult Error(string text)`
  - `sealed record McpTool(McpToolDefinition Definition, Func<McpCall, CancellationToken, Task<McpToolResult>> Handler, Func<JsonObject, string>? Summary = null)` — `Summary` is the human line for confirmation/audit (default: name + path arguments)
  - `sealed record McpCall(string ClientName, JsonObject Arguments)` with `string Required(string name)`, `string? Optional(string name)`
  - `sealed record McpPromptArgument(string Name, string Description, bool Required)`
  - `sealed record McpPrompt(string Name, string Description, IReadOnlyList<McpPromptArgument> Arguments, Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> Render)`
  - `static JsonObject McpSchema.Object(params (string Name, string Type, string Description, bool Required)[] properties)`
- Produces (`McpServer.cs`):
  - `interface IMcpToolHandler { IReadOnlyList<McpToolDefinition> List(); Task<McpToolResult> CallAsync(string name, McpCall call, CancellationToken ct); }` (Task 3's dispatcher implements it)
  - `sealed class McpServer(IMcpToolHandler tools, Func<IReadOnlyList<McpPrompt>> prompts, string serverVersion)`:
    - `Task<JsonObject?> HandleAsync(JsonObject message, McpSession session, CancellationToken ct)` — `null` for notifications
    - `McpSession CreateSession()`, `McpSession? FindSession(string id)`, `bool CloseSession(string id)`, `int SessionCount`
    - `event Action? SessionsChanged`
  - `sealed class McpSession { string Id; string ClientName = "unknown"; string? ProtocolVersion; }` + internal in-flight map `requestId → CancellationTokenSource` for `notifications/cancelled`
- Produces (`McpHttpServer.cs`):
  - `sealed class McpHttpServer(McpServer server, Func<string?> token) : IAsyncDisposable`
  - `McpStartResult Start(int port)` → `enum McpStartResult { Started, PortBusy, Failed }`; `Task StopAsync()`; `bool IsRunning`; `string Url` = `http://127.0.0.1:{port}/mcp`

Values the spec pins: server info `{ "name": "commander4k", "version": <AppVersion> }`; capabilities `{ "tools": { "listChanged": false }, "prompts": { "listChanged": false } }`; header `Mcp-Session-Id` returned on the `initialize` response; error codes `-32700` parse, `-32600` invalid request, `-32601` method not found, `-32602` invalid params; notifications/responses-only POST → `202` empty body; `DELETE /mcp` with session → `204`.

- [ ] **Step 1: Write failing `McpServerTests`** (fake `IMcpToolHandler` with one tool `echo`):

```csharp
[Fact] public async Task Initialize_returns_server_info_and_negotiated_version()
{   var (server, session) = Create();
    var reply = await server.HandleAsync(Request(1, "initialize", new() { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "2.0" } }), session, default);
    Assert.Equal("2025-03-26", (string?)reply!["result"]!["protocolVersion"]);
    Assert.Equal("commander4k", (string?)reply["result"]!["serverInfo"]!["name"]);
    Assert.Equal("claude-code", session.ClientName); }
[Fact] public async Task Unknown_protocol_version_gets_the_latest() // "1999-01-01" → "2025-06-18"
[Fact] public async Task Tools_list_and_call_map_to_the_handler()   // tools/list has "echo" with inputSchema; tools/call → content[0].text == "hi", isError == false
[Fact] public async Task Tool_errors_come_back_as_isError_not_jsonrpc_errors()
[Fact] public async Task Unknown_method_is_32601_and_notification_returns_null()
[Fact] public async Task Cancelled_notification_cancels_the_running_call() // handler awaits Task.Delay(-1, ct); send notifications/cancelled {requestId:7}; call returns isError with "cancelled"
[Fact] public async Task Prompts_get_renders_arguments()             // prompts/get name=p arguments {path:"C:\\x"} → messages[0].content.text contains "C:\\x"
```

- [ ] **Step 2: Run** `dotnet test --solution Commander4K.sln -- --filter-class "*McpServerTests"` — FAIL (types missing).
- [ ] **Step 3: Implement `McpJson`, `McpTool`, `McpServer`.** `tools/call` with an unknown tool → JSON-RPC `-32602`; handler exceptions of type `IOException`/`UnauthorizedAccessException`/`ArgumentException`/`InvalidOperationException`/`NotSupportedException`/`TimeoutException`/`OperationCanceledException` → `isError: true` with the message (no throw out of `HandleAsync`).
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Write failing `McpHttpServerTests`** (real `HttpClient`, free port via `TcpListener(IPAddress.Loopback, 0)` like `ExternalPluginTests.FreePort`):

```csharp
[Fact] public async Task Initialize_over_http_returns_session_header()
[Fact] public async Task Missing_or_wrong_token_is_401_and_no_tool_runs()   // handler counter stays 0
[Fact] public async Task Foreign_origin_is_403()                           // Origin: https://evil.example
[Fact] public async Task Localhost_origin_is_allowed()                     // Origin: http://localhost:1234 → 200
[Fact] public async Task Unknown_session_is_404_and_delete_ends_session()
[Fact] public async Task Get_is_405_and_notification_post_is_202()
[Fact] public async Task Second_server_on_the_same_port_reports_PortBusy()
```

- [ ] **Step 6: Run** `--filter-class "*McpHttpServerTests"` — FAIL.
- [ ] **Step 7: Implement `McpHttpServer`.** Prefix `http://127.0.0.1:{port}/mcp/` (also accept path without the trailing slash); accept loop on a background task; each request handled in its own task; request body limit 8 MB (→ 413); `HttpListenerException` on start with "already in use"/error 183/32 → `PortBusy`. Token compare with `CryptographicOperations.FixedTimeEquals`. Expected exceptions caught inside the loop (debugger-friendly rule in CLAUDE.md).
- [ ] **Step 8: Run** — PASS; `dotnet build Commander4K.sln` clean.
- [ ] **Step 9: Commit** `Add MCP protocol and loopback HTTP transport`.

---

### Task 3: Policy — allowed roots, modes, confirmations, audit (Core)

**Files:**
- Create: `src/Commander4K.Core/Mcp/McpPathPolicy.cs`, `McpToolDispatcher.cs`, `McpSettings.cs`
- Modify: `src/Commander4K.Core/Configuration/AppSettings.cs` (add `public McpSettings Mcp { get; set; } = new();`), the JSON source-gen context if it needs the new type
- Test: `tests/Commander4K.Core.Tests/McpPolicyTests.cs`

**Interfaces:**
- Consumes: Task 2 `McpTool`, `McpCall`, `McpToolResult`, `IMcpToolHandler`; `AuditLog.Record(string pluginId, string operation, string detail, AuditOutcome outcome)`.
- Produces:
  - `sealed class McpSettings { bool Enabled; int Port = 47800; List<string> AllowedRoots = []; Dictionary<string,string> ToolModes (OrdinalIgnoreCase); string? Token; }` + `static string NewToken()` (32 bytes, base64url) + `string? ReadToken()` (`SecretProtector.Unprotect`) + `void SetToken(string)`. Defaults follow the source-gen `init => field = value ?? …` rule from CLAUDE.md where needed.
  - `static class McpPathPolicy`: `string Normalize(string path)` (resolves `.`/`..`, unifies separators per scheme, trims trailing separator except at roots), `bool IsInside(string path, string root)`, `bool IsAllowed(string path, IReadOnlyList<string> roots)`. Case-insensitive for `file`/`smb`/`reg`, case-sensitive for `sftp`/`ftp`; archive paths (`zip://<container>::/…`) are judged by their container path.
  - `sealed record McpConfirmation(string ClientName, string Tool, string Summary, bool AlwaysAsk)`
  - `enum McpConsent : byte { No, Yes, YesForSession }`
  - `sealed class McpToolDispatcher(IReadOnlyList<McpTool> tools, Func<McpSettings> settings, Func<IReadOnlyList<string>> protectedRoots, Func<McpConfirmation, CancellationToken, Task<McpConsent>> confirm, AuditLog audit) : IMcpToolHandler`
    - `McpToolMode EffectiveMode(McpToolDefinition tool)` — settings value (`"allow"|"ask"|"block"`) or `DefaultMode`; `AlwaysAsk` turns `Allow` into `Ask`
    - `void RevokeSessionGrants()`; `IReadOnlyList<McpCallRecord> Recent` (last 50: time, client, tool, summary, outcome) + `event Action? Called`
  - Audit line: plugin id `"mcp:" + clientName`, operation = tool name, detail = summary, outcome Allowed/Denied/Failed.

Messages for the model (exact):
- blocked tool: `"This tool is disabled in Commander4K settings (MCP)."`
- path outside roots: `"Path is outside the folders allowed for MCP: {path}. Ask the user to add a folder in Commander4K (MCP settings → allowed folders)."`
- protected path: `"Commander4K's plugin and config folders cannot be changed through MCP. Use install_plugin."`
- declined: `"The user declined this action in Commander4K."`
- `tools/list` descriptions of blocked tools get the suffix `" (Disabled by the user in Commander4K.)"`.

- [ ] **Step 1: Write failing `McpPolicyTests`:**

```csharp
[Theory]
[InlineData(@"C:\Repo\src\a.cs", @"C:\Repo", true)]
[InlineData(@"c:\repo\SRC", @"C:\Repo\", true)]
[InlineData(@"C:\Repos2\a", @"C:\Repo", false)]
[InlineData(@"C:\Repo\..\Windows\x", @"C:\Repo", false)]
[InlineData(@"C:\Repo", @"C:\Repo", true)]
[InlineData(@"\\SRV\Share\x", @"\\srv\share", true)]
[InlineData(@"\\srv\share2", @"\\srv\share", false)]
[InlineData("sftp://h/home/a/b", "sftp://h/home/a", true)]
[InlineData("sftp://h/home/ab", "sftp://h/home/a", false)]
[InlineData("sftp://h/home/A", "sftp://h/home/a", false)]
[InlineData(@"zip://C:\Repo\x.zip::/inner/f", @"C:\Repo", true)]
[InlineData(@"reg://HKCU\Software\X", @"reg://HKCU\Software", true)]
public void IsInside_handles_boundaries(string path, string root, bool expected)
[Fact] public void Empty_roots_allow_nothing()
[Fact] public async Task Blocked_tool_never_runs_and_is_audited_as_denied()
[Fact] public async Task Path_outside_roots_is_blocked_even_in_allow_mode()
[Fact] public async Task Write_into_protected_root_is_blocked()                 // Writes=true, path under protected root inside allowed root
[Fact] public async Task Ask_mode_calls_confirm_and_No_returns_declined()
[Fact] public async Task YesForSession_skips_the_next_confirmation_for_that_client_only()
[Fact] public async Task AlwaysAsk_tool_set_to_allow_still_asks_and_never_gets_session_grant() // confirmer returns YesForSession; second call asks again
[Fact] public async Task Concurrent_asks_are_serialized()                       // two calls, confirmer records max concurrency == 1
[Fact] public async Task Cancellation_while_confirming_returns_declined()
[Fact] public async Task RevokeSessionGrants_asks_again()
[Fact] public async Task SelfConfirming_tool_skips_confirm_but_not_roots_or_block()
```

- [ ] **Step 2: Run** `--filter-class "*McpPolicyTests"` — FAIL.
- [ ] **Step 3: Implement** `McpPathPolicy`, `McpSettings`, `McpToolDispatcher` (one `SemaphoreSlim(1)` around `confirm`; session grants in a `ConcurrentDictionary<(string Client, string Tool), bool>`; path arguments read from `call.Arguments` by `PathArguments` names, string or string array).
- [ ] **Step 4: Run** — PASS; build clean.
- [ ] **Step 5: Commit** `Add MCP policy: allowed folders, tool modes, confirmations, audit`.

---

### Task 4: File tools over VFS (Core)

**Files:**
- Create: `src/Commander4K.Core/Mcp/McpFileTools.cs`
- Test: `tests/Commander4K.Core.Tests/McpFileToolsTests.cs` (uses `FakeVfsProvider` + a temp folder through a real `VfsRouter`)

**Interfaces:**
- Consumes: `VfsRouter.OpenListing(path, ct)`, `OpenReadAsync`, `GetEntryAsync`, write/mkdir/move/delete members (use the same ones `ScopedPluginHost` uses for `fs.*`); Task 2 `McpTool`.
- Produces: `static IReadOnlyList<McpTool> McpFileTools.Create(VfsRouter router)` with these definitions (mode / flags):

| name | arguments | default | flags |
|---|---|---|---|
| `list_directory` | `path` | Allow | Path=[path] |
| `stat` | `path` | Allow | Path=[path] |
| `read_file` | `path`, `encoding?: "text"\|"base64"`, `offset?` (bytes) | Ask | Path=[path] |
| `write_file` | `path`, `content`, `encoding?` | Ask | Path=[path], Writes |
| `make_directory` | `path` | Ask | Path=[path], Writes |
| `move` | `source`, `destination` | Ask, AlwaysAsk | Path=[source,destination], Writes |
| `delete` | `path` | Ask, AlwaysAsk | Path=[path], Writes |

Limits: listing 500 entries then `"… (more entries not shown)"`; listing timeout 20 s; text read 60 000 chars (`"… (file truncated)"`); text with `\0` → hint to use `encoding: "base64"`; base64 read max 262 144 bytes from `offset` with `"next offset: N"` when more; write max 8 MB decoded.

- [ ] **Step 1: Write failing tests:** `List_directory_lists_names_kinds_sizes`, `Read_text_truncates_at_60000_chars`, `Read_binary_suggests_base64`, `Read_base64_pages_with_offset` (600 KB file → 3 reads, `next offset: 262144` then `524288`, last without "next"), `Read_of_huge_file_reads_only_what_it_returns` (stream wrapper counts bytes read ≤ limit + buffer), `Write_then_stat_roundtrip`, `Move_and_delete_work_on_temp_folder`, `Summaries_name_the_paths` (`move` summary contains both paths).
- [ ] **Step 2: Run** `--filter-class "*McpFileToolsTests"` — FAIL.
- [ ] **Step 3: Implement `McpFileTools.Create`.**
- [ ] **Step 4: Run** — PASS; build clean.
- [ ] **Step 5: Commit** `Add MCP file tools over VFS`.

---

### Task 5: Plugin authoring from a folder, binary files, check tool (Core)

**Files:**
- Modify: `src/Commander4K.Core/Plugins/PluginAuthoring.cs` (`GeneratedFile`, `Prepare`, `Install`, new `FromFolder`)
- Modify: `src/Commander4K.Core/Plugins/PluginSourceCheck.cs` (skip files with `Bytes != null`)
- Create: `src/Commander4K.Core/Mcp/McpPluginTools.cs` (UI-free plugin tools: `check_plugin`, `get_plugin_log`)
- Test: `tests/Commander4K.Core.Tests/PluginAuthoringTests.cs`, `tests/Commander4K.Core.Tests/McpPluginToolsTests.cs`

**Interfaces:**
- Produces:
  - `sealed record GeneratedFile(string Path, string Content, byte[]? Bytes = null)` — when `Bytes` is set it is written as-is and `Content` is empty.
  - `sealed record PluginLimits(int MaxFiles, long MaxFileBytes, long MaxTotalBytes)` with `static PluginLimits Answer = new(30, 1 MB, 30 MB)` and `static PluginLimits Folder = new(500, 8 MB, 64 MB)`.
  - `PluginAuthoring.Prepare(IReadOnlyList<GeneratedFile> files, string pluginsRoot, Func<string, string?> sdkClient, PluginLimits? limits = null)` (default `Answer`).
  - `static IReadOnlyList<GeneratedFile> PluginAuthoring.FromFolder(string folder, PluginLimits limits)` — local disk only; text for `.json .js .mjs .cjs .ts .py .ps1 .psm1 .md .txt .html .css .csv .xml .yml .yaml .svg` (UTF-8, no `\0`), bytes for everything else; skips `node_modules/.cache`, `.git`, `__pycache__`; throws `InvalidOperationException` past limits.
  - `static IReadOnlyList<McpTool> McpPluginTools.Create(Func<string, string?> sdkClient, string pluginsRoot, AuditLog audit)`:
    - `check_plugin` `{ files?: [{path, content}], folder? }` — Allow, Path=[folder]; returns `"OK: <name> (<id>) <version>, runtime <type>, permissions: …, N files"` or the `Prepare` error / `PluginSourceCheck` findings with `PluginSourceCheck.FixRequest(findings)`; nothing written outside temp.
    - `get_plugin_log` `{ id, count? = 100 }` — Allow; `audit.Recent(id)` last `count` lines (includes `stderr` entries recorded by `ExternalPlugin.PumpStandardErrorAsync`).

- [ ] **Step 1: Write failing tests:** `Binary_files_are_written_byte_exact` (Prepare+Install with a 3-byte file `[0,255,10]`), `Folder_limits_allow_big_assets` (2 MB `.mjs` passes with `Folder`, fails with `Answer`), `FromFolder_reads_text_and_binary_and_skips_git`, `Source_check_ignores_binary_files`, `Check_plugin_reports_bad_manifest` (`{files:[{path:"plugin.json",content:"{}"}]}` → `isError` false, text starts with `"plugin.json is not valid"`), `Check_plugin_reports_sdk_bypass` (python `open(` → text contains the fix request), `Get_plugin_log_returns_recent_entries`.
- [ ] **Step 2: Run** `--filter-class "*PluginAuthoringTests"` and `"*McpPluginToolsTests"` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS; full `dotnet test --solution Commander4K.sln` PASS (catalog/AI installer still use `Answer`).
- [ ] **Step 5: Commit** `Plugin authoring from folders with binary files; MCP check_plugin and get_plugin_log`.

---

### Task 6: App tools — context, panels, commands, plugins

**Files:**
- Create: `src/Commander4K.App/Mcp/McpAppTools.cs`
- Modify: `src/Commander4K.App/Ai/AiAppContext.cs` (`Describe(Func<string, bool>? showListing = null)` — panel paths always, listing sample and marks only where `showListing(path)`), `Example(bool preview = false)` gets an overload by example name for `get_plugin_guide`
- Modify: `src/Commander4K.App/Ai/AiPluginInstaller.cs` (new `bool forMcp = false` parameter)
- Modify: `src/Commander4K.App/MainWindow.cs` (small internal API the tools call, below)

**Interfaces:**
- Consumes: Task 2 `McpTool`/`McpSchema`, Task 3 `McpPathPolicy.IsAllowed`, `AppHost.ListCommands()`, `AppHost.Approvals`, `AppHost.ReloadExternalPluginsAsync()`, `AiPluginInstaller.InstallAsync`, `AiAppContext.PrepareInstall`, Task 5 `PluginAuthoring.FromFolder`/`PluginLimits.Folder`.
- Produces: `static IReadOnlyList<McpTool> McpAppTools.Create(MainWindow window, AppHost host, AiAppContext context, Func<IReadOnlyList<string>> allowedRoots)` with:

| name | arguments | default | flags |
|---|---|---|---|
| `get_context` | — | Allow | |
| `list_commands` | `filter?` | Allow | |
| `get_plugin_guide` | `runtime?`, `example?` | Allow | |
| `list_plugins` | — | Allow | |
| `navigate` | `panel: left\|right\|active\|other`, `path` | Allow | Path=[path] |
| `select` | `panel`, `names: string[]`, `cursor?` | Allow | |
| `set_panel_mode` | `panel`, `mode: "files"\|"preview"\|"panel:<id>"` | Allow | |
| `run_command` | `id` | Ask | |
| `run_plugin_command` | `plugin`, `command` | Ask | (checks active path + marks with roots in the handler) |
| `load_plugin_panel` | `plugin`, `argument?`, `filter?` | Ask | |
| `render_preview` | `path`, `plugin?` | Ask | Path=[path] |
| `install_plugin` | `files?: [{path, content}]`, `folder?` | Ask, AlwaysAsk | Path=[folder], SelfConfirming |
| `enable_plugin` | `id` | Ask, AlwaysAsk | SelfConfirming |
| `reload_plugin` | `id` | Allow | |

- `MainWindow` internal API used by the tools (all must be called through `Dispatcher.UIThread.InvokeAsync`): `FilePanelView McpPanel(string which)`, `Task McpNavigateAsync(FilePanelView panel, string path)`, `void McpSelect(FilePanelView panel, IReadOnlyList<string> names, string? cursor)`, `Task<bool> McpSetModeAsync(FilePanelView panel, string mode)`, `Task<bool> ExecuteAsync(string commandId)` (existing), `Task<string?> McpRunPluginCommandAsync(string pluginId, string commandId)`.
- `AiPluginInstaller.InstallAsync(..., bool forMcp = false)`: with `forMcp` the source-check `ChoiceDialog` and `openPluginManager()` are skipped; findings → returns `AiInstallResult(message, PluginSourceCheck.FixRequest(findings))` without installing; the `ConfirmDialog` with permissions stays — it is the confirmation, so `install_plugin` is defined with `SelfConfirming: true`.
- `enable_plugin` (`SelfConfirming: true`): shows the plugin manager's approval content (name, id, version, permissions with descriptions) in a `ConfirmDialog`; Yes → `host.Approvals.Approve(manifest)` + `ReloadExternalPluginsAsync()`; returns `"Enabled <id> <version>."` or declined.
- `render_preview`: pick the plugin like `PreviewPanelView` (highest `Priority` among `host.PreviewPlugins` whose `CanPreview(entry)`), run `LoadAsync` with that plugin's `ScopedPluginHost`; `MarkdownPreview`/`TextPreview` → text, `TilesPreview` → JSON, `ImagePreview`/`HexPreview` → one-line description, `MessagePreview` → its message.
- `load_plugin_panel`: `IPanelPlugin.CreateSession(...).LoadAsync` → columns + up to 500 rows as tab-separated text.

- [ ] **Step 1: Implement `McpAppTools` and the `MainWindow`/`AiAppContext`/`AiPluginInstaller` changes.** (Window-bound; covered by manual checks in Task 8 and the headless check in Step 2.)
- [ ] **Step 2: Headless smoke check** (scratch console per CLAUDE.md "UI verification without a desktop session"): create `MainWindow`, `Attach(host)`, call the `navigate` handler with a temp folder inside allowed roots, pump `Dispatcher.UIThread.RunJobs()`, assert the left panel path; call `get_context` and assert it contains that path. Expected: both assertions pass.
- [ ] **Step 3: Verify** `dotnet build Commander4K.sln` clean; `dotnet test --solution Commander4K.sln` PASS.
- [ ] **Step 4: Commit** `Add MCP app tools: context, panels, commands, plugins`.

---

### Task 7: Settings, per-tool modes, prompts

**Files:**
- Modify: `src/Commander4K.Core/Configuration/SettingsCatalog.cs` (category + 3 settings)
- Modify: `src/Commander4K.App/MainWindow.cs` (`RegisterPaletteSettings` → also registers `mcp:<tool>`)
- Create: `src/Commander4K.App/Mcp/McpPrompts.cs`
- Modify: `en.json`
- Test: `tests/Commander4K.Core.Tests/McpPolicyTests.cs` (settings search test)

**Interfaces:**
- Produces:
  - `SettingsCatalog.Mcp => Loc.T("MCP (AI z zewnątrz)")`; settings keys `mcp.enabled` (Toggle, `s.Mcp.Enabled`), `mcp.port` (Text, int 1024–65535, invalid → unchanged), `mcp.roots` (Text, one path per `;`) with keywords `["mcp", "claude", "claude code", "ai", "serwer", "port", "foldery", "dostęp"]`.
  - Runtime settings `mcp:<tool>` (Choice: `allow` → `Loc.T("Zezwalaj")`, `ask` → `Loc.T("Pytaj")`, `block` → `Loc.T("Zablokuj")`; always-ask tools offer only ask/block), stored in `s.Mcp.ToolModes`.
  - `static IReadOnlyList<McpPrompt> McpPrompts.Create(AppHost host)`: `write_plugin(wish)`, `preview_plugin_for_file(path, wish?)`, `explain_file_format(path)`; the two with `path` use `AiAppContext.DescribeFileAsync` (only if the path is inside allowed roots; otherwise the text says the file is outside the allowed folders) and end with the tool hint from Task 1.

- [ ] **Step 1: Write failing test** `Mcp_settings_are_searchable`: `SettingsCatalog.Search(SettingsCatalog.Create(() => []), "claude code")` contains key `mcp.enabled`; `Search(..., "port mcp")` contains `mcp.port`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement** settings, runtime registrations, prompts, en.json. **Step 4: Run** full tests — PASS (`LocalizationTests` included).
- [ ] **Step 5: Commit** `MCP settings, per-tool modes and prompts`.

---

### Task 8: Wiring — host, confirmation dialog, connect window, status badge, docs

**Files:**
- Create: `src/Commander4K.App/Mcp/McpHost.cs`, `McpConfirmDialog.cs`, `McpConnectWindow.cs`
- Modify: `src/Commander4K.App/Mcp/UseClaudeCodeDialog.cs` (status + "Połącz Claude Code" button)
- Modify: `src/Commander4K.App/MainWindow.cs` (create `McpHost` in `Attach`, apply on settings change, badge in status bar, command registration), `src/Commander4K.Core/Input/CommandIds.cs` (`McpConnect = "cm_McpConnect"`), `src/Commander4K.App/Composition/AppHost.cs` (dispose order), `en.json`
- Modify: `docs/plugins.md` (section "Writing plugins with Claude Code over MCP"), `README.md` (one paragraph), `CLAUDE.md` (MCP architecture bullet: Core/App split, dispatcher order, roots, protected paths, self-confirming tools)

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `sealed class McpHost(AppHost host, MainWindow window) : IAsyncDisposable` — `McpStartResult? Status`, `string Url`, `int Sessions`, `IReadOnlyList<McpCallRecord> Recent`, `event Action? Changed`, `Task ApplySettingsAsync()` (start/stop/restart on Enabled/Port change, creates token when enabling without one), `string ClaudeCommand()` = `claude mcp add --transport http commander4k {Url} --header "Authorization: Bearer {token}"`, `string McpJsonSnippet()`, `void NewToken()`, `void RevokeSessionGrants()`.
  - Protected roots passed to the dispatcher: both `plugins/` folders (next to the exe and in `DataDirectory`) and the `config/` folder (`host.Config` directory).
  - `McpConfirmDialog(SkinBrushes brushes, McpConfirmation confirmation)` : `DialogWindow<McpConsent>` — title `Loc.T("AI chce: {0}", <localized tool name>)`, body: client name, summary (selectable, max 2 000 chars), buttons `Tak` / `Nie` / `Tak do końca sesji` (hidden when `AlwaysAsk`); Esc = No. The dispatcher's `confirm` delegate marshals to the UI thread and awaits it.
  - Status bar badge: hidden when off; `Loc.T("MCP: wyłączony port")` style text when `PortBusy`; `Loc.T("AI podłączone ({0})", sessions)` when sessions > 0; click opens `McpConnectWindow`.
  - `McpConnectWindow` (non-modal, `ApplySkin`, followed via `MainWindow.FollowSkin`): enable toggle, port, status line, the `claude mcp add …` command with Copy, `.mcp.json` snippet with Copy, "Nowy token", allowed-roots list with "Dodaj folder z aktywnego panelu" / remove, last 50 calls (time, client, tool, summary, outcome), "Cofnij zgody z tej sesji".

- [ ] **Step 1: Implement** the files above; `cm_McpConnect` registered with description `Loc.T("Połącz Claude Code lub innego klienta MCP z Commander4K")` (no default key binding).
- [ ] **Step 2: Build and test:** `dotnet build Commander4K.sln` clean; `dotnet test --solution Commander4K.sln` PASS.
- [ ] **Step 3: Manual end-to-end with Claude Code** (real window, `dotnet run --project src/Commander4K.App`):
  1. `cm_McpConnect` → enable, add a temp folder as allowed root, copy the command, run it in a terminal; `claude mcp list` shows `commander4k` connected. Badge shows "AI podłączone (1)".
  2. In Claude Code: "what's in my left panel?" → `get_context` runs without a dialog.
  3. "navigate the left panel to <temp folder>" → panel changes.
  4. "read <file in temp folder>" → confirmation dialog; Nie → Claude reports it was declined; again with "Tak do końca sesji" → next read has no dialog.
  5. "read C:\Windows\win.ini" → blocked message about allowed folders, no dialog.
  6. Two parallel reads (ask Claude to read two files at once) → two dialogs one after another.
  7. "write a Python plugin with a command that counts files in the active folder, check it and install it" → `check_plugin`, then install dialog with permissions, then `enable_plugin` dialog, then `run_plugin_command` works.
  8. "write a file into <plugins folder>" → protected-path message.
  9. Change port to one in use (e.g. run `python -m http.server <port> --bind 127.0.0.1`) → status "port busy", no crash.
  Expected: every step behaves as written; audit lines appear in `data/logs/plugins-YYYYMMDD.log` with `mcp:claude-code`.
- [ ] **Step 4: Commit** `Wire the MCP server into the app: confirmations, connect window, status badge, docs`.

---

### Task 9: Configuration, translations and skins over MCP

Spec section "Konfiguracja aplikacji, tłumaczenia, skórki". The MCP client is the model, so nothing here calls an AI API: the tools hand out the same material the clipboard prompts contain, check the answer and save it.

**Files:**
- Create: `src/Commander4K.Core/Mcp/McpConfigTools.cs`
- Create: `src/Commander4K.App/Mcp/McpSkinTools.cs` (`save_skin` and its dialog)
- Modify: `src/Commander4K.App/Mcp/McpHost.cs` (register both), `src/Commander4K.App/Mcp/McpPrompts.cs` (`translate_ui`, `design_skin`), `src/Commander4K.App/MainWindow.cs` (`OnSettingChanged` → `internal`), `en.json`, `docs/plugins.md` (MCP section: list the new tools), `CLAUDE.md` (MCP bullet: config-writing tools and the read-only settings)
- Test: `tests/Commander4K.Core.Tests/McpConfigToolsTests.cs`

**Interfaces:**
- Consumes: `SettingDescriptor`/`SettingsCatalog.Search`, `AppSettings`, `ConfigStore` (`ConfigDirectory`, `SkinsDirectory`), `TranslationKit.PromptFor`/`Check`/`PluralForms`, `Loc.BuiltInFile`/`Loc.Parse`, `SkinPrompt.DesignBrief`, `SkinSchema.Create`, `SkinJson`, `SkinValidator`, `SkinCatalog`, `AppHost.KeyBindings`/`SaveKeyBindingsAsync`/`ReloadSkinsAsync`, `SkinManager.Preview`/`Apply`.
- Produces: `static IReadOnlyList<McpTool> McpConfigTools.Create(McpConfigContext context)` where
  ```csharp
  public sealed record McpConfigContext(
      Func<IReadOnlyList<SettingDescriptor>> Settings,   // AppHost.SettingsCatalog
      AppSettings Values,
      Func<string, Task> SettingChanged,                 // UI: OnSettingChanged(key) + SaveSettingsAsync, via Dispatcher
      Func<IReadOnlyDictionary<string, string>> KeyBindings,
      Func<Dictionary<string, string>, Task> SaveKeyBindings,
      Func<string, string?> CommandDescription,
      string ConfigDirectory,
      Func<SkinCatalog> Skins,
      Func<string> CurrentSkinId);
  ```

| name | arguments | default | notes |
|---|---|---|---|
| `list_settings` | `query?` | Allow | key, category, title, description, kind, value, choices (value=label), `requiresRestart`, `readOnly` |
| `set_setting` | `key`, `value` | Ask | toggle: `true/false`; choice: must be a choice value; text: as given (descriptor validates, e.g. port). Read-only keys → `isError` |
| `list_key_bindings` | `filter?` | Allow | `keys → command (description)` |
| `set_key_binding` | `keys`, `command?` | Ask | unknown command → error; `FormatException` → error with its message; reports the replaced command |
| `get_translation_kit` | `language`, `name?`, `onlyMissing?`, `offset?`, `limit?` (≤ 500, default 300) | Allow | rules from `PromptFor` with the JSON block cut off, plural form count, `{ "<pl key>": "<en value>" }` page, `next offset: N` |
| `check_translation` | `language`, `entries` (object) | Allow | `Check` on existing file + entries |
| `save_translation` | `language`, `entries` (object) | Ask | merge, refuse on issues other than "missing", write `config/i18n/<code>.json` atomically (temp + move), keys sorted like en.json |
| `get_skin_guide` | — | Allow | brief + schema + current skin JSON |
| `list_skins` | — | Allow | |
| `get_skin` | `id` | Allow | |
| `check_skin` | `json` | Allow | parse error or validator warnings, else "OK" |
| `save_skin` (App) | `json`, `apply?` (default true) | Ask | `SelfConfirming`: `McpSkinDialog` previews the skin live, shows warnings; Yes → save like `AiSkinDialog.SaveAsync`, reload, apply or restore; No/Esc/cancelled → restore previous skin |

- `McpConfigTools.ReadOnlyKeys(string key)`: `key.StartsWith("mcp", OrdinalIgnoreCase)` (covers `mcp.` and `mcp:`), `pluginCatalogUrl`, `aiCatalogUrl`, `updateRepository` — checked in `set_setting` before anything else, so a session grant does not help. Message: `"'<key>' can only be changed in the Commander4K settings window."`.
- Ids: language `^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$` and not `pl`; skin `^[a-z0-9][a-z0-9-]{0,63}$`. The file name is built from the id only.
- `McpCall` needs the object argument `entries`: add `JsonObject? Object(string name)` next to `Optional`. `McpSchema.Object` gets the `"object"` type (no `items`).
- Translation issues from `TranslationKit.Check` are localized UI text; return them with their key, which is enough for the model to fix the entry.

- [ ] **Step 1: Write failing `McpConfigToolsTests`** (temp config folder, fake `SettingChanged` recording keys):
  - `List_settings_finds_by_polish_and_english_words` (`"skórka"` and `"skin"` → `skin`).
  - `Set_setting_changes_value_and_notifies` (`showHidden` = `true` → value set, `SettingChanged("showHidden")` called once).
  - `Set_setting_rejects_unknown_choice` (`density` = `"huge"` → `isError`, value unchanged).
  - `Set_setting_refuses_read_only_keys` (`mcp.enabled`, `mcp.roots`, `mcp:delete`, `pluginCatalogUrl`, `aiCatalogUrl`, `updateRepository` → `isError`, nothing changed, no `SettingChanged`).
  - `Set_key_binding_reports_replaced_command` and `Set_key_binding_rejects_bad_keys`.
  - `Translation_kit_pages_and_only_missing` (offset/limit, `next offset`; with an existing `de.json` holding 10 keys, `onlyMissing` returns total − 10).
  - `Save_translation_merges_and_writes` (two calls → one file with both batches).
  - `Save_translation_refuses_placeholder_mismatch_bad_code_and_pl` (`{0}` dropped; `..\x`, `C:\x`, `pl` → `isError`, no file in the config folder).
  - `Check_skin_reports_low_contrast` (built-in skin with foreground = background → warning) and `Check_skin_reports_bad_json`.
- [ ] **Step 2: Run** `dotnet test --solution Commander4K.sln -- --filter-class "*McpConfigToolsTests"` — FAIL.
- [ ] **Step 3: Implement** `McpConfigTools`, `McpSkinTools` + dialog, registrations, prompts, `en.json`, docs.
- [ ] **Step 4: Run** full `dotnet test --solution Commander4K.sln` — PASS; `dotnet build Commander4K.sln` clean.
- [ ] **Step 5: Manual check with Claude Code** (real window):
  1. "switch to the Norton Commander skin and compact density" → `set_setting` ×2 with confirmation; the look changes at once.
  2. "design a skin inspired by solarized and save it" → `get_skin_guide`, `check_skin`, `save_skin` dialog previews it live; Nie restores the old skin; Tak saves it into `config/skins/` and it appears in `cm_SelectSkin`.
  3. "translate the UI into German, start with 100 entries" → `get_translation_kit` + `save_translation`; `config/i18n/de.json` exists; `cm_Translate` check shows only missing keys.
  4. "bind Ctrl+Alt+N to open the network" → `set_key_binding`; the shortcut works without a restart.
  5. "add C:\Windows to the MCP allowed folders" → refused with the settings-window message.
- [ ] **Step 6: Commit** `MCP tools for settings, key bindings, translations and skins`.

---

## Self-review notes

- Spec part 1 → Task 1; transport/auth/sessions → Task 2; roots, modes, session grants, protected paths, audit → Task 3; file tools → Task 4; `check_plugin`/`get_plugin_log`, binary + folder installs → Task 5; remaining tools → Task 6; settings + prompts → Task 7; connect window, badge, confirmation dialog, docs → Task 8; configuration, key bindings, translations, skins (added 2026-10-31) → Task 9.
- Task 9 is the only code that writes into `config/` on behalf of MCP; it never takes a path, only validated ids, and the settings that widen MCP's own reach are read-only over MCP.
- Spec parts 3–4 (`kind: "web"`, `pdf-viewer`) are Plan B, written after this plan lands; Task 5's binary/folder support is what Plan B's PDF.js install needs.
- `install_plugin` and `enable_plugin` confirm through their own permission dialogs (`SelfConfirming`), so the user sees one dialog per action, not two.
