# Web Previews (Plan B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** External preview plugins can return `kind: "web"` — an HTML/JS page from the plugin's folder shown in WebView2 inside the preview panel, sandboxed, talking to the app through `window.c4k` — and a `pdf-viewer-node` example (PDF.js: pages, zoom, search, print) proves it, including being written and installed by Claude Code over MCP.

**Architecture:** Protocol and parsing in PluginSdk/Core (`WebPreview` content, `webRoot` on the preview contribution, `preview.message`, byte-range helper); rendering in a new App control `WebPreviewView` that maps only the plugin's `webRoot` to a virtual host, blocks everything else, serves the current file under a per-entry token through the plugin's `ScopedPluginHost`, and injects the `c4k` bridge. `PreviewPanelView` shows it like any other preview.

**Tech Stack:** .NET 10, Avalonia 12 + Avalonia.Markup.Declarative, `Microsoft.Web.WebView2.Core` (already referenced), xunit.v3, Node for the example, `pdfjs-dist` (vendored, hash-checked).

**Spec:** `docs/superpowers/specs/2026-10-31-mcp-and-web-preview-design.md` — parts 3 and 4.

## Global Constraints

- `TreatWarningsAsErrors`; no new NuGet packages; no `.axaml`.
- The page never gets its own disk or network access: only files under the plugin's `webRoot` (virtual host) and the current entry through `c4k.fileUrl` (read via that plugin's `ScopedPluginHost`, `fs.read` gated and audited). A plugin with `net` permission still gets no network from the page.
- Navigation outside the virtual host is cancelled; http(s) links open in the browser panel (`IPluginHost.OpenUrlAsync`); new windows and downloads are blocked.
- Same plugin + same `page` on cursor move → `entry` event, no reload; different page/plugin → navigation.
- Without WebView2 or headless → `MessagePreview` with a message (like Mermaid).
- F-keys (including F5), Esc, Tab, Ctrl+Q and Alt/Ctrl+Shift combos go to the keymap via `AcceleratorKeyPressed`; plain keys, Ctrl+F, Ctrl+P, Ctrl+C stay in the page.
- When you add a protocol method: `ExternalPlugin`/adapters, all three SDK clients (then copy into every example and catalog plugin — `Example_sdk_clients_match_plugins_sdk`), the SDK table in `docs/plugins.md`, `docs/plugin.schema.json`.
- `PluginSourceCheck` must not lint files under `webRoot` (browser code legitimately uses `fetch` etc.) but keeps linting the plugin process code; bundled examples stay clean (`Bundled_examples_use_only_the_sdk`).
- UI text via `Loc.T` + `en.json`; developer/model text English; colours from `SkinBrushes`.
- `pdf-viewer-node` is **not** embedded as a template and **not** in the official catalog (PDF.js size).
- Debugger rule: expected exceptions never leave a `Task.Run` lambda.
- Work in worktree `C:\Repos\Commander4K-mcp`, branch `feature/web-preview` (from `feature/mcp` 67150d4).

## Review Focus

- A big PDF on SFTP/FTP/inside a zip (non-seekable stream) — expected: copied once to a temp file, then served with `Range`; temp file deleted when the entry changes or the view closes (test in Task 2 for the range logic; manual in Task 4).
- `page` like `../main.js`, `C:\x.html`, `web\..\..\x` or a `webRoot` pointing outside the plugin folder — expected: refused with a message preview, nothing served (tests in Task 1).
- Plugin without `fs.read` asks for `c4k.fileUrl` — expected: HTTP 403 to the page, audit line Denied, app keeps working (Task 3 headless check where possible; manual otherwise).
- Fast cursor movement across many PDFs — expected: old entry tokens stop working (404), late `request` replies for an old entry are dropped, no reload storm (test of token rotation in Task 2; manual in Task 4).
- Page tries `window.open`, `location = "https://…"`, a download, `fetch("https://example.com")` — expected: blocked or opened in the browser panel; the preview stays (manual checklist in Task 3).

---

## File Structure

- `src/Commander4K.PluginSdk/IPreviewPlugin.cs` — add `WebPreview` record.
- `src/Commander4K.PluginSdk/PluginManifest.cs` — `PreviewContribution` gets `WebRoot`.
- `src/Commander4K.Core/Plugins/ManifestReader.cs` — validate `webRoot`.
- `src/Commander4K.Core/Plugins/ExternalPluginAdapters.cs` — parse `kind: "web"`, `MessageAsync`.
- `src/Commander4K.Core/Plugins/PluginSourceCheck.cs` — skip `webRoot` folders.
- `src/Commander4K.Core/Web/ByteRange.cs` — `Range` header parsing.
- `src/Commander4K.Core/Web/EntryFileSource.cs` — seekable stream or temp spool for one entry, token rotation.
- `src/Commander4K.App/Preview/WebPreviewView.cs` — the WebView2 control, resource filter, bridge.
- `src/Commander4K.App/Preview/WebBridge.js` (embedded) — `window.c4k` implementation.
- `src/Commander4K.App/Preview/PreviewPanelView.cs` — show `WebPreview`.
- `src/Commander4K.App/Mcp/McpAppTools.cs` — `render_preview` describes web previews.
- `plugins-sdk/{python,node,powershell}` + copies — `web_preview` helper.
- `examples/plugins/pdf-viewer-node/` — the example.
- Docs: `docs/plugins.md`, `docs/plugin.schema.json`, `CLAUDE.md`, `README.md`.

---

### Task 1: Protocol — `webRoot`, `WebPreview`, parsing, lint skip

**Files:**
- Modify: `src/Commander4K.PluginSdk/PluginManifest.cs` (`PreviewContribution`), `src/Commander4K.PluginSdk/IPreviewPlugin.cs`
- Modify: `src/Commander4K.Core/Plugins/ManifestReader.cs`, `ExternalPluginAdapters.cs` (`ExternalPreviewPlugin.LoadWithOptionsAsync`), `PluginSourceCheck.cs`
- Modify: `docs/plugin.schema.json`, `docs/plugins.md` (preview.render row + new "Web previews" section: result shape, `webRoot`, security rules)
- Test: `tests/Commander4K.Core.Tests/PluginSourceCheckTests.cs`, `tests/Commander4K.Core.Tests/WebPreviewParsingTests.cs`, `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Produces:
  - `sealed record PreviewContribution(string Id, string Title, IReadOnlyList<string> Extensions, int Priority = 10, string? WebRoot = null)` — `webRoot` in JSON: relative folder (e.g. `"web"`).
  - `sealed record WebPreview(string PluginId, string RootFolder, string Page, string? StateJson) : PreviewContent` — `RootFolder` = absolute, normalized `<plugin folder>/<webRoot>`; `Page` = relative path inside it with `/` separators; `StateJson` = raw JSON of `state` or null.
  - `static PreviewContent ExternalPreviewPlugin.ParseWeb(JsonElement result, string pluginId, string pluginFolder, string? webRoot)` (internal static, unit-testable) — returns `WebPreview` or `MessagePreview` with an English reason.
  - `PluginSourceCheck.Scan(IEnumerable<GeneratedFile> files)` and `ScanFolder(string folder)` skip files under any `contributes.previews[].webRoot` of the plugin's `plugin.json` (read it from the files / folder; invalid manifest → skip nothing).
- Rules: `webRoot` must pass `PluginAuthoring.IsSafeRelativePath` and exist as a folder (ManifestReader error otherwise: `Loc.T("Podgląd „{0}”: webRoot „{1}” musi być folderem wewnątrz wtyczki.", id, webRoot)`); `kind: "web"` from a preview without `webRoot` → `MessagePreview("This preview returned kind \"web\" but declares no webRoot in plugin.json.")`; `page` unsafe or missing file → `MessagePreview("Web page not found inside webRoot: <page>")`; resolved path must stay under `RootFolder` after `Path.GetFullPath`.

- [ ] **Step 1: Write failing tests** — `WebPreviewParsingTests`: `Web_result_becomes_WebPreview` (page `"index.html"`, state `{"a":1}` → `StateJson == "{\"a\":1}"`, `RootFolder` ends with `web`); `Missing_webRoot_gives_message`; theory `Unsafe_pages_are_refused` with `"../main.js"`, `"C:\\x.html"`, `"web/../../x"`, `""`, `"missing.html"`; `ManifestReader` theory `Bad_webRoot_is_an_error` (`"../x"`, `"C:\\x"`, nonexistent). `PluginSourceCheckTests.WebRoot_files_are_not_linted` (`web/app.js` with `fetch(` → no findings; `main.js` with `fetch(` → finding). `ExternalPluginTests.Web_preview_round_trip` — throw-away Python plugin (copy `plugins-sdk/python/c4k.py`) returning `{"kind":"web","page":"index.html","state":{"n":1}}` → `WebPreview`.
- [ ] **Step 2: Run** `dotnet test --project tests/Commander4K.Core.Tests -- --filter-class "*WebPreviewParsingTests"` — FAIL (types missing).
- [ ] **Step 3: Implement** the records, ManifestReader validation, `ParseWeb` (called from `LoadWithOptionsAsync` when `kind == "web"`), lint skip, schema + docs.
- [ ] **Step 4: Run** both test projects — PASS; build clean.
- [ ] **Step 5: Commit** `Web previews: webRoot, WebPreview content, parsing and lint skip`.

---

### Task 2: `preview.message`, SDK helper, byte ranges and entry source (Core + SDK)

**Files:**
- Modify: `src/Commander4K.Core/Plugins/ExternalPluginAdapters.cs` (`ExternalPreviewPlugin.MessageAsync`)
- Create: `src/Commander4K.Core/Web/ByteRange.cs`, `src/Commander4K.Core/Web/EntryFileSource.cs`
- Modify: `plugins-sdk/python/c4k.py`, `plugins-sdk/node/c4k.js`, `plugins-sdk/powershell/c4k.ps1` + every copy in `examples/plugins/*` and `plugins-catalog/*` (byte-identical), `plugins-catalog` hashes via `pwsh ./plugins-catalog/Update-Catalog.ps1`
- Modify: `docs/plugins.md` (SDK table: `web_preview` / `webPreview` / `New-C4KWebPreview`; protocol table: `preview.message`)
- Test: `tests/Commander4K.Core.Tests/ByteRangeTests.cs`, `EntryFileSourceTests.cs`, `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Consumes: Task 1 `WebPreview`, `ExternalPreviewPlugin`.
- Produces:
  - `Task<JsonElement> ExternalPreviewPlugin.MessageAsync(VfsEntry entry, string method, JsonElement parameters, IPluginHost host, CancellationToken ct)` → JSON-RPC `preview.message` with `{ preview, entry: EntryDto, method, params, context }`, plugin timeout 30 s.
  - SDK: Python `web_preview(page, state=None) -> dict`; Node `webPreview(page, state)`; PowerShell `New-C4KWebPreview -Page <string> [-State <object>]`. `preview.message` is registered like any handler (doc example).
  - `readonly record struct ByteRange(long Start, long End)` + `static ByteRange? ByteRange.Parse(string? header, long length)` — single `bytes=a-b`, `a-`, `-n`; invalid/multi-range/unsatisfiable → `null` (caller answers 200 full or 416 — see below).
  - `sealed class EntryFileSource : IAsyncDisposable` — `static Task<EntryFileSource> OpenAsync(Func<CancellationToken, Task<Stream>> open, long? knownLength, string tempFolder, CancellationToken ct)`; `long Length`; `Task<Stream> OpenRangeAsync(ByteRange? range, CancellationToken ct)` (returns a stream positioned/limited to the range); if the opened stream is not seekable, it is copied once to `tempFolder/c4k-web-<guid>.bin` and that file serves ranges; `DisposeAsync` deletes the temp file. `string Token` — 32 hex chars, new per instance.
- Status rules for Task 3: no `Range` → 200 full; valid → 206 + `Content-Range: bytes a-b/len`; `ByteRange.Parse` null for an unsatisfiable range when header present → 416 with `Content-Range: bytes */len`. Always `Accept-Ranges: bytes`.

- [ ] **Step 1: Write failing tests** — `ByteRangeTests` theory: (`"bytes=0-99"`,1000 → 0..99), (`"bytes=900-"`,1000 → 900..999), (`"bytes=-100"`,1000 → 900..999), (`"bytes=0-5000"`,1000 → 0..999), (`"bytes=1000-"`,1000 → null), (`"bytes=0-1,5-6"` → null), (`"items=0-1"` → null), (`null` → null). `EntryFileSourceTests`: `Seekable_stream_serves_ranges_without_temp_file`; `Non_seekable_stream_is_spooled_once_and_deleted_on_dispose` (open delegate called once; temp file exists then gone); `Ranges_return_exact_bytes`; `Tokens_differ_per_instance`. `ExternalPluginTests.Preview_message_round_trip` — Python plugin handler `preview.message` echoes `{"method":…,"n":params.n+1}`.
- [ ] **Step 2: Run** `dotnet test --project tests/Commander4K.Core.Tests -- --filter-class "*ByteRangeTests"` — FAIL.
- [ ] **Step 3: Implement** `ByteRange`, `EntryFileSource`, `MessageAsync`, SDK helpers in all three clients, copy them into examples/catalog, run `pwsh ./plugins-catalog/Update-Catalog.ps1`, docs.
- [ ] **Step 4: Run** both test projects — PASS (incl. `Example_sdk_clients_match_plugins_sdk`, `PluginCatalogTests`); build clean.
- [ ] **Step 5: Commit** `preview.message, SDK web_preview helpers, byte ranges and entry file source`.

---

### Task 3: `WebPreviewView` — WebView2, sandbox, file serving, bridge, keys

**Files:**
- Create: `src/Commander4K.App/Preview/WebPreviewView.cs`, `src/Commander4K.App/Preview/WebBridge.js` (embedded, LogicalName `preview/web-bridge.js`)
- Modify: `src/Commander4K.App/Preview/PreviewPanelView.cs` (render `WebPreview`, reuse the view across entries, dispose on mode change), `src/Commander4K.App/Commander4K.App.csproj` (embed bridge; exclude `examples/plugins/pdf-viewer-node/**` from the examples EmbeddedResource), `src/Commander4K.App/Mcp/McpAppTools.cs` (`render_preview` for `WebPreview` → `"Web preview: page <page> of plugin <id> (shown in the app window; not available as text)."`), `en.json`

**Interfaces:**
- Consumes: Task 1 `WebPreview`; Task 2 `ExternalPreviewPlugin.MessageAsync`, `EntryFileSource`, `ByteRange`; `WebView2Host.GetEnvironmentAsync`, `WebView2Host` control; `BrowserPanelView.OnAcceleratorKey` pattern; `AppHost.HostFor(manifest)`; `IPluginHost.OpenUrlAsync/Notify/CopyTextAsync`.
- Produces: `sealed class WebPreviewView : UserControl, IDisposable` — `Task ShowAsync(WebPreview preview, VfsEntry entry, ExternalPreviewPlugin plugin, IPluginHost host)`; `void ApplySkin(SkinBrushes brushes)`; constructor `(AppHost app, SkinBrushes brushes, Func<KeyStroke, bool> runShortcut)`.
- Virtual host: `https://<id with non [a-z0-9-] replaced by '-'>.plugin.c4k/` mapped to `RootFolder` with `CoreWebView2HostResourceAccessKind.DenyCors`. Profile: the shared environment (`data/webview2`) is acceptable — **decision:** use a separate user-data folder `data/webview2-previews` so plugin pages never see the browser's cookies.
- Resource filter: `AddWebResourceRequestedFilter("*", All)`; allow `https://<host>/…` except `/__entry/…`; `/__entry/<token>` with the current `EntryFileSource.Token` → stream via `OpenRangeAsync` (statuses from Task 2; `Content-Type` from extension, `application/octet-stream` default); wrong/old token → 404; everything else (other hosts, `data:` is allowed by WebView2 itself, `blob:` fine) → 403. `fs.read` denial from the scoped host → 403 + audit (the host already audits).
- Navigation: `NavigationStarting` allows only the virtual host; http(s) elsewhere → cancel + `host.OpenUrlAsync(url)`; `NewWindowRequested` → handled, http(s) → `OpenUrlAsync`; `DownloadStarting` → cancel. Settings: `AreDevToolsEnabled = false` in Release, `IsStatusBarEnabled = false`, `AreDefaultContextMenusEnabled = true` (copy), `IsZoomControlEnabled = true`.
- Bridge (`window.c4k`, injected with `AddScriptToExecuteOnDocumentCreatedAsync`, initial data passed via a JSON literal appended to the script):
  - `c4k.entry {name, path, kind, size, modified}`, `c4k.state` (parsed `StateJson` or `null`), `c4k.skin {background, foreground, accent, muted, selection, border, fontFamily, fontSize}` (hex `#rrggbb` from `SkinBrushes`), `c4k.fileUrl` (`https://<host>/__entry/<token>/<url-encoded name>`).
  - `c4k.on(event, fn)` for `"entry"` (payload `{entry, fileUrl, state}`) and `"skin"`.
  - `c4k.request(method, params) → Promise` (postMessage `{id, type:"request", method, params}` → `MessageAsync` → `{id, result}` or `{id, error}`; replies for a previous entry are dropped page-side by id+entry token).
  - `c4k.notify(text, error=false)`, `c4k.copyText(text)` → `IPluginHost`.
  - Messages from any origin other than the virtual host are ignored.
- Keys: `AcceleratorKeyPressed` forwards F1–F12, Esc, Tab, Ctrl+Q and Alt/Ctrl+Shift combos to `runShortcut` (set `Handled` when it returns true); everything else stays in the page.
- Entry change: same plugin id + same page → dispose old `EntryFileSource`, open new one, post `entry` event; otherwise navigate.
- No WebView2 runtime (`WebView2Host.RuntimeVersion() == null`) or controller failure → show `MessagePreview(Loc.T("Ten podgląd potrzebuje WebView2 (Microsoft Edge WebView2 Runtime)."))`.

- [ ] **Step 1: Implement** `WebPreviewView`, `WebBridge.js`, the `PreviewPanelView` and `McpAppTools` changes, csproj embedding/exclusion, en.json.
- [ ] **Step 2: Build and test** — `dotnet build Commander4K.sln` clean; both test projects PASS.
- [ ] **Step 3: Window check (real desktop)** — a throw-away plugin in a temp folder with `webRoot: "web"` and a page that: shows `c4k.entry.name`, fetches `c4k.fileUrl` with `Range: bytes=0-15` and prints the status + bytes, calls `c4k.request("echo",{n:1})`, tries `fetch("https://example.com")`, `window.open("https://example.com")` and `location.href = "https://example.com"`. Expected: name shown; 206 with 16 bytes; echo reply; fetch fails; window.open and location open in the browser panel; the preview stays; moving the cursor to another matching file updates the name without reload; F5/Ctrl+Q/Esc reach the app. If no desktop session is available to the implementer, write the plugin into `C:\Users\kamil\AppData\Local\Temp\claude\c--Repos-Commander4K\9d54edbf-c1db-4f23-8724-5f9bf27159bd\scratchpad\web-preview-check` and hand the checklist to the controller.
- [ ] **Step 4: Commit** `Show web previews in WebView2 with a sandbox, entry file serving and the c4k bridge`.

---

### Task 4: `pdf-viewer-node` example

**Files:**
- Create: `examples/plugins/pdf-viewer-node/plugin.json`, `main.js`, `c4k.js` (SDK copy), `web/index.html`, `web/viewer.js`, `web/viewer.css`, `web/pdfjs/**` (vendored), `web/pdfjs/SOURCE.md` (version, npm tarball URL, sha512, licence), `README.md`
- Modify: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs` (add the example to `Example_sdk_clients_match_plugins_sdk` and a render test), `tests/Commander4K.Core.Tests/PluginSourceCheckTests.cs` (add to `Bundled_examples_use_only_the_sdk`)
- Modify: `docs/plugins.md` (example list), `README.md`, `CLAUDE.md` (web previews bullet: webRoot-only virtual host, separate WebView2 profile, `__entry` token + ranges, bridge, keys, not headless)

**Interfaces:**
- Consumes: Task 1–3 (`webRoot`, `web_preview`/`webPreview`, `window.c4k`, `fileUrl` with ranges).
- Manifest: id `examples.pdf-viewer`, runtime node, `contributes.previews: [{ id: "pdf", title: "PDF", extensions: [".pdf"], priority: 60, webRoot: "web" }]`, permissions `["fs.read"]`.
- `main.js`: `preview.render` → `webPreview("index.html", { zoom: <storage value or "page-width"> })`; `preview.message` `"saveZoom"` → `storage.set`.
- PDF.js: newest `pdfjs-dist` 5.x at implementation time (`npm view pdfjs-dist version`), verify the tarball against `dist.integrity` (sha512) before vendoring; vendor only `build/pdf.min.mjs`, `build/pdf.worker.min.mjs`, `web/pdf_viewer.mjs`, `web/pdf_viewer.css`, `cmaps/`, `standard_fonts/`, `wasm/` (if present), `LICENSE`. Viewer uses `PDFViewer` + `EventBus` + `PDFLinkService` + `PDFFindController` from `pdf_viewer.mjs`; `getDocument({ url: c4k.fileUrl, rangeChunkSize: 65536, cMapUrl, standardFontDataUrl, wasmUrl })`.
- UI in the page: toolbar with page `n / N`, zoom −/+ and a fit-width option, search box (Enter = next, Shift+Enter = previous, match count, highlighted matches; Ctrl+F focuses it), Print button; colours from `c4k.skin` (CSS variables), updated on `skin` events; `entry` event loads the new file without reload.
- Print: renders every page to an image at 150 dpi into a hidden print container (`@media print` shows only it), then `window.print()`; container cleared after `afterprint`.

- [ ] **Step 1: Write failing tests** — `ExternalPluginTests.Pdf_viewer_returns_a_web_preview` (render on a 1-page PDF fixture created in the test → `WebPreview` with `Page == "index.html"` and `RootFolder` ending in `web`); add `("pdf-viewer-node", "node", "c4k.js")` to the SDK-copy theory; add `"pdf-viewer-node"` to `Bundled_examples_use_only_the_sdk`.
- [ ] **Step 2: Run** — FAIL (folder missing).
- [ ] **Step 3: Implement** the example (vendor PDF.js with the hash check recorded in `SOURCE.md`).
- [ ] **Step 4: Run** both test projects — PASS; build clean; confirm the example is not in the embedded resources (`dotnet build` then list the App assembly's manifest resource names in a tiny scratch check, or inspect the csproj exclusion).
- [ ] **Step 5: Window check** (desktop; otherwise checklist to the controller): install from the folder, approve, open a multi-page PDF on a local disk and one on SFTP/zip (spooled), zoom, search a word (highlights, next/previous, count), print preview shows every page, move between two PDFs quickly, change skin. Expected: all work, GUI never freezes.
- [ ] **Step 6: Commit** `Add pdf-viewer-node example: PDF.js web preview with search and print`.

---

## Self-review notes

- Spec part 3: protocol → Task 1; display, sandbox, network, entry events, fallback, bridge (`entry`, `state`, `skin`, `fileUrl` with ranges and spooling, `request`/`preview.message`, `notify`, `copyText`), keys, printing → Tasks 2–3; SDK helpers → Task 2; lint skip → Task 1 (spec offered "page subfolder skipped" — implemented as the declared `webRoot`, which also limits what the virtual host serves). `c4k.save` was marked optional in the spec — not in this plan.
- Spec part 4: example → Task 4; acceptance test through Claude Code is the user's final manual check (connect MCP, ask for the PDF plugin, install, approve).
- Decisions beyond the spec, for the user to check: `webRoot` in the manifest (spec said no new contribution field); separate WebView2 profile for previews; `render_preview` over MCP describes web previews instead of rendering them.
