# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository status

The sections below (in Polish) are the project specification and implementation plan; treat them as the source of truth for architecture. Steps 1–4 and the release script are done. Plugin architecture (protocol spec for plugin authors: `docs/plugins.md`, manifest schema `docs/plugin.schema.json`):
- Every plugin has a `PluginManifest` (id, version, author, declared permissions). Plugin code only ever receives an `IPluginHost` from `AppHost.HostFor(manifest)` — a `ScopedPluginHost` that checks the manifest's permissions on gated calls (fs.read/fs.write/process.kill/shell.run/net) and writes every call to `AuditLog` (`data/logs/plugins-YYYYMMDD.log`). Built-ins follow the same rule for side effects (task manager kills via `KillProcessAsync`, PowerShell runs via `RunProcessAsync`, log analyzer reads via `OpenReadAsync`). For external plugins `process.read` and `system.info` gate `process.list` / `system.info`; built-in panels still read WMI and processes directly.
- The SDK is the only channel to the system: fs.list/stat/readText/readBytes (fs.read), fs.writeText/writeBytes/mkdir/delete/move (fs.write), process.list/kill, shell.run, net.get/request, system.info, `ui.copyText`, and `storage.get/set/keys`.
  - Storage is per-plugin JSON in `data/plugin-data/<id>.json` (`PluginStorage`). It needs no permission, so plugins never read files next to their code.
  - When you add a protocol method, add it to `IPluginHost`/`ScopedPluginHost` (gated and audited), to `ExternalPlugin.HandleRequestAsync`, to all three SDK clients (then copy them into every example; a test keeps the copies identical) and to the SDK reference table in `docs/plugins.md`.
  - Core has no native interop: `process.list` gets parent PIDs and image paths from `Plugins.BuiltIn.Interop.ProcessLookups`, passed into `ScopedPluginHost`.
  - `PluginSourceCheck` lints plugin code for SDK bypasses (`open()`, `fs`, `subprocess`, `fetch`, `Get-Content`, stdout writes…; calls through the SDK alias are excluded). The AI installer turns its findings into a fix request, and the plugin manager shows them in the details and warns on enable. The bundled examples must stay clean (test).
- Preview `extensions` accept `.ext`, file names and `*`/`?` patterns (`ExternalPreviewPlugin.Matches`). The example `project-modules-python` shows this: dependencies and OpenAPI modules.
- Unknown files: `PreviewPanelView` shows a bar over hex / "no preview". Its buttons call `MainWindow.AskAiAboutFileAsync` (format question with the first 512 bytes, via `AiAppContext.DescribeFileAsync`) and `CreatePreviewPluginAsync` (preview-plugin request; the chat in plugin mode, otherwise Claude Code via `UseClaudeCodeDialog`).
- This PC (`local://`, `LocalFileSystemPlugin`):
  - It lists drives, then `KnownFolders` (libraries via SpecialFolder; Downloads from the User Shell Folders registry key), then a `net://` entry.
  - After that it streams `NetworkEntries`: AppHost passes the network plugin's root, i.e. saved connections, Explorer network locations and discovered hosts.
  - `PanelSorting.Rank` groups: actions, drives, folders, shares, hosts, files.
  - The ▾ button on the path bar (`FilePanelView.LocationMenuRequested`) opens the drive menu for that panel.
- Connections (`ConnectionDialog`, `ConnectionSetting.Auth/UserName/KeyFile/Secret`):
  - `Secret` is DPAPI via `Core/Security/SecretProtector`, which `AiCredentialStore` uses too.
  - `AppHost.ApplySettings` registers SFTP/FTP logins with `RemoteTransferVfsPlugin.SetLogin` (keyed by `RealmFor(path)` = the session key).
  - SFTP offers private keys (`RemoteLogin`), password and keyboard-interactive. With neither password nor key it tries `~/.ssh/id_ed25519|id_ecdsa|id_rsa` as the given or Windows user.
  - A refused login becomes `VfsAuthenticationRequiredException`, and `LoginDialog` (with a key option for SSH) asks.
  - Saved SMB logins are tried before asking.
- Mermaid (`App/Preview/MermaidRenderer`):
  - Mermaid 12.2.0 is vendored in `App/Assets/Mermaid` (npm tarball checked against its sha512) and embedded, then extracted to `data/mermaid/<version>`.
  - A hidden WebView2 controller, using the browser's shared environment (`WebView2Host.GetEnvironmentAsync`) and parented to the main window, loads `render.html` from a virtual host and returns PNGs through `postMessage`.
  - Configuration: `securityLevel: strict`, navigation locked to the virtual host, `htmlLabels: false` (so the canvas is not tainted), base theme from skin colours.
  - `MarkdownRenderer` draws ```mermaid blocks when they are attached; without WebView2, or on error, it shows the code and a message.
  - WebView2 needs an STA thread: headless harnesses on MTA show the fallback.
  - Labels are text, not HTML, so quotes in labels become apostrophes (entities would show literally).
- Folder preview (`FolderPreviewPlugin`):
  - It switches to `NotesOverview` (Markdown with a Mermaid graph) for an Obsidian vault (`.obsidian` here or up to 5 parents, disk paths only) or for a folder that is mostly Markdown.
  - `CanvasPreviewPlugin` shows `.canvas` (JSON Canvas) files as a diagram.
- Folder files `c4k.dir` (`Core/Folders/FolderConfig`, schema `docs/c4k.dir.schema.json`, guide in `docs/plugins.md`):
  - Each file has a `preview` and an `enter` action. An action is a view (`gallery`, `dashboard`, `notes`, `markdown`, `files`), a `plugin` (+ `preview`/`panel`, `options`), `open` (+ `user`, `keyFile`), `url` or `run`. A string is shorthand for a view name or a plugin id.
  - `FilePanelView.FolderEntering` → `MainWindow.EnterFolderAsync` reads the file, only on file/smb/sftp/ftp, with a 2 s limit, then `RunFolderActionAsync`.
  - A view on Enter is a pinned `PreviewPanelView` (`PinnedFolder`) over the same panel, already navigated into the folder, so Esc shows its files.
  - Shift+Enter is `cm_EnterFolderPlain`; `cm_FolderView` writes the file.
  - `PreviewPanelView` checks the folder's file before picking a preview plugin (`ResolveFolderView`). Built-in views are `Plugins.BuiltIn/Previews/FolderViews`.
  - Plugins get `options` in `preview.render` and `panel.load` (`ExternalPreviewPlugin.LoadWithOptionsAsync`, `PanelRequest.Options`). A missing plugin becomes an install tile.
  - `TilesPreview`/`PreviewTile` (SDK) render as a wrap of buttons.
  - Tile targets:
    - paths behave like Enter;
    - http(s) opens the browser panel;
    - other URI schemes need a confirmation once per session;
    - `c4k:install/<id>` and `c4k:plugins` go to the catalog and the plugin manager;
    - `c4k:run/<section>/<n>` re-reads the command from the file and never trusts the link.
  - `run` needs `FolderTrustStore` consent (`config/trusted-folders.json`, file path + SHA-256 of its content). It works on disks and shares only.
- Official plugin catalog (`plugins-catalog/catalog.json`, `Core/Plugins/PluginCatalog`):
  - It is embedded as a fallback and downloaded at start-up from `AppSettings.PluginCatalogUrl` (cached in `data/plugin-catalog.json`).
  - Entries point to a folder relative to `baseUrl` and list every file with the SHA-256 of its text without CR.
  - `DownloadAsync` refuses mismatches. Installation goes through `AiPluginInstaller.InstallAsync(fromCatalog: true)`: disabled until approved.
  - The plugin manager has a catalog group (Ctrl+I installs, Ctrl+R refreshes).
  - After changing a catalog plugin, run `pwsh ./plugins-catalog/Update-Catalog.ps1`; `PluginCatalogTests` fails otherwise.
  - Official plugins live in `plugins-catalog/<name>/` with their own SDK client copy, which is tested like the examples. A catalog install downloads from `main`, so it only works for files that are pushed.
- Drive menu (Alt+F1/F2): after the drives come net://, saved connections, Explorer's network locations and recently visited network paths from frecency, then "add connection".
  - Explorer's network locations are `NetworkLocations`: `%AppData%\Microsoft\Windows\Network Shortcuts`, with `.lnk` LinkInfo parsed per MS-SHLLINK, no COM. Don't hand plugins the window or `PluginHostAdapter` (that is the UI side, `IPluginUi`).
- External plugins: folders with `plugin.json` under `plugins/` (next to the exe and in the data folder), run out of process (`ExternalPlugin`, JSON-RPC 2.0 NDJSON over stdio via `JsonRpcConnection`), started lazily, disabled until the user approves their permissions (`PluginApprovalStore`, re-consent when permissions grow). `RuntimeCatalog` detects Node/Python/PowerShell/.NET versions and checks `runtime.version` ranges. Adapters `ExternalCommandPlugin`/`ExternalPanelPlugin` make them look like built-ins to the UI. There is no in-process loading of third-party assemblies — by design, since .NET has no sandbox.
- Client SDKs: `plugins-sdk/{python,node,powershell}` (single files); the examples in `examples/plugins` carry copies (a test keeps them identical) and are embedded in the app as templates for the plugin manager. `ExternalPluginTests` run the examples against the real runtimes.
- Panels open in the panel opposite the active one; while the active panel shows a plugin, plugin "context" (active path, selection) is the other — visible — file panel.
- Plugin panels: `IPanelPlugin` (data-only snapshots rendered by `App/PluginPanels/PluginPanelView`), `ICommandPlugin` (menu/palette/keymap commands as `plugin:<id>:<cmd>`, panels as `panel:<id>`). Built-ins: system info, task manager, log analyzer, PowerShell catalogue (`config/powershell.json`), plugin manager (`builtin.plugin-manager`).

Panel modes and preview: a `FilePanelView` shows either its file list or an `IPanelModeView` over it (`PluginPanelView`, `PreviewPanelView`; browser later). F1 = settings, F2 = panel mode picker (files / preview / any panel plugin; applied to the opposite panel), Ctrl+Q toggles the preview. Previews are `IPreviewPlugin`s (built-in: markdown 50, image 40, folder 10, text/hex 0; external via `contributes.previews` + `preview.render`); `PreviewPanelView` follows the source tab's `Changed`/`ActiveTabChanged` events (no polling), debounces 120 ms, cancels the previous load immediately, never follows while there are unsaved edits, and saves through the preview plugin's `ScopedPluginHost` (fs.write). `MarkdownRenderer` turns Markdig's AST into Avalonia controls (no HTML engine). Folder sizes go through `IPluginHost.CalculateSizeAsync` (one audit entry per folder, not per directory read). Keys pressed inside a mode's own text box: F-keys, Ctrl/Alt combos and Esc go to the mode first; Backspace/arrows/letters stay with the text box. Default-binding changes between versions go into `DefaultKeymap.ChangedDefaults` (applied once per keymap.json, recorded in `appliedMigrations`, never overriding a user's own binding).

Palettes: `IPalettePlugin` (items + header/per-item actions) rendered by `App/Palettes/PaletteView` in one of four docks (`MainWindow`: left/right outer columns, bottom row, and "between", which lives inside the right half so the `GridSplitter` keeps two star columns). Built-ins in `BuiltInPalettes.cs` (quick actions, favourites, saved views = both panels' paths + modes, recent/frecency) talk to the window through `IShell`; external ones come from `contributes.palettes` (`palette.items/invoke/action`, reloaded after navigation and after actions). Each palette's position is a runtime setting (`palette:<id>`, stored in `AppSettings.PaletteDocks`) registered into `AppHost.ExtraSettings`, so it is searchable like the rest; Ctrl+B hides all (palettes are off by default), Ctrl+D adds a favourite. Each palette has ✕ (sets its dock to hidden) and a drag grip on the edge facing the panels (width for side docks, height at the bottom, stored in `AppSettings.PaletteSizes`).

Right click: `FileViewportCanvas.RightClicked` → `FilePanelView` shows the Windows Explorer menu (`App/Shell/ShellContextMenu`: `SHParseDisplayName` + `SHBindToParent` + `IShellFolder.GetUIObjectOf` → `IContextMenu`, `TrackPopupMenuEx`, owner window subclassed meanwhile so IContextMenu2/3 can draw "Send to"/"Open with") for drive/UNC paths in one folder, the app's own menu elsewhere; `AppSettings.ShellContextMenu = false` restores Total Commander marking. Shift+F10 / Apps open it from the keyboard (`cm_ContextMenu`). It cannot be driven headless; `ShellContextMenu.ListItems` builds the menu without showing it (needs an STA thread).

Browser: `App/Browser/BrowserPanelView` is a panel mode (`cm_Browser` Ctrl+Alt+W, F2) hosting WebView2 through `WebView2Host : NativeControlHost` (only `Microsoft.Web.WebView2.Core` — the csproj drops the package's WinForms/WPF references, which break the build, and keeps its `.xml` out of publish). Profile in `data/webview2`, shared per process, so sign-ins persist. It is the built-in plugin `builtin.browser` (top-level navigations audited). Plugins reach it via `IPluginHost.OpenUrlAsync` / `SearchWebAsync` (`ui.openUrl` / `ui.search`); engines and address-bar parsing live in `Core/Web/SearchEngines` (Gemini takes no query parameter, so the question goes to the clipboard). Keys pressed inside the page never reach Avalonia: `AcceleratorKeyPressed` forwards F-keys (except F5/F12) and Ctrl+Shift/Alt combos to the keymap. It cannot run headless; verify in a real window (`CapturePreviewAsync` for the page image).

Archives: `Plugins.BuiltIn/Archives/ArchiveVfsPlugin` (schemes zip/7z/tar/rar/gz) addresses entries as `zip://<archive path>::/<inner/path>` — the archive part can be any VFS path, the first `::` splits it (Windows names cannot contain it), and the inner part starts with `/` so `PanelTab.Title` and ".." focus work. It reads the container through `Router` (set after the router is built, since the router contains it), caches up to 8 directory indexes keyed by size + mtime, and spools non-seekable streams to temp files. Compressed tar must go through SharpCompress's sequential `ReaderFactory` (`ArchiveFactory` sees a .tar.gz as one gzip entry), and PAX/GNU header records are filtered out. Read-only VFS; Enter opens archives, Alt+F5 packs (`Core/Jobs/VfsPackJob`, zip/tar/tar.gz by extension, partial output deleted on failure), Alt+F9 extracts. Preview disables editing on providers without `VfsCapabilities.Write`.

Remote (SFTP/FTP): `GetEntryAsync` is a real stat (null when missing — copy jobs create folders and honour "skip existing" from it), uploads, server-side move (same session only; otherwise `NotSupportedException` → copy + delete), recursive delete (SFTP walks the tree itself; symlinks are removed, never followed). FTP transfers go through FluentFTP's `UploadStream`/`DownloadStream` connected by a `System.IO.Pipelines` pipe — hand-rolled `OpenWrite` + `GetReply` desynchronised replies and truncated uploads. FluentFTP's `CreateDirectory` returns `false` instead of throwing on refusal. `RemoteServerTests` run against real servers when `C4K_TEST_SFTP` / `C4K_TEST_FTP` are set (e.g. `sftp://foo:pass@127.0.0.1:2222/upload` with docker `atmoz/sftp:alpine "foo:pass:::upload"`, `ftp://user:pass@127.0.0.1:2121/ftp/user` with `delfer/alpine-ftp-server -e "USERS=user|pass" -e ADDRESS=127.0.0.1 -p 21000-21010:21000-21010` — that server is not chrooted, use the home folder).

Columns (WDX): `IColumnMetadataPlugin.GetValueAsync` receives an `IPluginHost` (reads and processes go through it). Built-ins in `Plugins.BuiltIn/Columns`: Git status (`git rev-parse` + `git status --porcelain=v1 -z` per folder via `RunProcessAsync`, cached 3 s; folders roll up real changes but not ignored content), image size + EXIF date/camera (own header parser, ≤256 KB read once per file for all three columns), SHA-256. Each column is a runtime toggle setting `column:<pluginId>/<columnId>` (category "Kolumny", stored in `AppSettings.Columns` in display order). `PanelTab.ColumnData` (`ColumnLoader`) fills values after each listing — rows in view first, 4 in parallel, cancelled on navigation, keyed by path+size+mtime — and raises `Updated` (not `Changed`, which the preview follows). The canvas places them between name and size and drops them first when the panel is narrow; they are not sortable yet.

Not yet written: sorting by plugin columns, columns from external plugins, writing into archives, archive passwords. Beyond the spec, the user asked for switchable skins (Norton Commander, Midnight Commander, modern...) including skins generated with AI: the model lives in `Core/Theming`, UI code must paint from `SkinBrushes` (never hard-coded colours), and `App/Theming/ClaudeSkinGenerator.cs` designs skins via the Claude API.

AI providers (`Core/Ai`): every provider speaks the OpenAI-compatible chat completions API (`OpenAiCompatibleClient`), described in `Core/Ai/ai-providers.json` — embedded as the fallback and downloaded at start-up from `AppSettings.AiCatalogUrl` (raw GitHub URL of that same file, so fixing it on `main` updates every install; cached in `data/ai-providers.json`, user additions in `config/ai/providers.json`). Check model ids against the live endpoints before editing it (GitHub Models was retired 2026-07-30; OpenRouter's free ids change often — `openrouter/free` is its own free router). `AiCredentialStore` keeps keys in `config/ai/credentials.json`, DPAPI-encrypted (`dpapi:` prefix; plain text accepted for hand edits); keys may also come from `envVar` or the GitHub CLI. `AiRouter` rotates: 429 → that model cools down (Retry-After / reset headers) and the next model is tried; all models cooling → the provider waits; quota/402 → provider waits 1 h; 401 → "invalid key" until re-login or reset; 5xx/timeout → 30 s. OpenRouter signs in with OAuth PKCE on a loopback port (`AiLogin`). Local providers (`tier: local`, `auth: none`: Ollama :11434, LM Studio :1234) carry `install` (Markdown steps per language, winget id, download URL); the providers window probes them (`/models`, 2 s) and runs `winget install` in a visible PowerShell. The list is remote data: run only `AiInstallInfo.SafeWingetId` and open only `IsWebUrl` links. UI: `App/Ai/AiProvidersWindow` (cm_AiProviders) and `AiChatWindow` (cm_AskAi when `AiInApp` and something is signed in). AI context and plugins:
- `App/Ai/AiAppContext` describes the app for prompts: both panels, plugins, runtimes, and the embedded `docs/plugins.md` + schema + the example plugin for an installed runtime. It is English because it is a prompt.
- The chat sends it with every question and offers tools (`AiChatTools`: `get_context`, `list_directory`, `read_text_file` behind a consent dialog, `get_plugin_guide`). Models that reject tools get the conversation flattened (`AiMessage.WithoutTools`).
- `Core/Plugins/PluginAuthoring` holds the prompt and output format (`File: path` + one fenced block per file), the Markdown parser (`ParseFiles`), `FromFolder` (a local plugin folder) and `Prepare`/`Install`. `Prepare` validates in a temp folder: safe relative paths, `ManifestReader`, and it adds the SDK client.
- `AiPluginInstaller` shows the permissions and revokes any earlier approval before installing (new code is never auto-enabled).
- AI outside the app goes through Claude Code: `UseClaudeCodeDialog` copies the prompt, shows the MCP status and opens the connect window. The browser panel no longer types into or reads assistant pages; `SearchEngine.ChatUrl` is used when an assistant engine takes no query (the prompt goes to the clipboard).
- MCP server (Claude Code; guide in `docs/plugins.md` "Writing plugins with Claude Code over MCP"):
  - Core (`Core/Mcp`, no UI): `McpServer` (JSON-RPC, sessions, `notifications/cancelled`), `McpHttpServer` (HttpListener on `127.0.0.1:<port>/mcp`, bearer token, Origin check), `McpToolDispatcher`, `McpPathPolicy`, `McpSettings` (`AppSettings.Mcp`, token DPAPI-protected), `McpFileTools`, `McpPluginTools`. App (`App/Mcp`): `McpAppTools` (window tools, UI-thread marshalled), `McpPrompts`, `McpHost`, `McpConfirmDialog`, `McpConnectWindow` (`cm_McpConnect`), the status-bar badge.
  - Dispatcher order per call: block → path arguments against allowed roots and protected roots (every tool except `NamesOnly` list_directory/stat; `Writes` and `ReadsTree` tools also refuse a folder that contains a protected root) → mode (ask: one dialog at a time, "yes for this session" per client+tool, never for `AlwaysAsk`) → handler → audit line `mcp:<client>` (also for refusals) and the recent-calls ring. Path arguments are normalized before the handler runs; paths a handler takes from the window (panels, selection) or a prompt argument are checked inside the handler with `McpAccess.Refusal` / `McpToolDispatcher.Refusal` (allowed and not protected); `McpAppTools.Create` and `McpPrompts.Create` take both root functions.
  - Allowed roots: `Mcp.AllowedRoots` (any VFS path; empty = nothing). Protected roots (`McpHost.ProtectedRootsOf`): both `plugins/` folders, the exe folder (`AppContext.BaseDirectory`), `config/`, `data/` — neither read nor written through MCP. `McpPathPolicy` refuses NTFS stream syntax (any `:` after the drive/share, e.g. `config::$INDEX_ALLOCATION`) during normalization, so every check inherits it. `get_plugin_log` answers only for installed external plugin ids (never `builtin.*` or `mcp:*`). `McpTool.Details` is extra text for the confirmation dialog only (write_file's content preview), never audited. ConfirmDialogs that MCP opens unprompted use `cancelIsDefault: true`. `install_plugin`/`enable_plugin` are `SelfConfirming`: their own permission dialog replaces the dispatcher's confirmation (block/roots still apply); installs are disabled until enabled.
  - `McpHost` (created in `MainWindow.Attach`, re-applied from `OnSettingChanged` for `mcp*` keys) reuses `MainWindow.McpTools` (the same list the settings show), hands the dispatcher an immutable settings snapshot swapped on each `ApplySettingsAsync` (the settings window edits lists in place on the UI thread), and builds a new server + listener per start (port/enable change); stopping cancels in-flight calls, closes their dialogs as "No" and revokes session grants. `SessionsChanged`/`Called` fire on pool threads, so it posts `Changed` to the UI thread. At runtime the server stops when the main window closes (`Closing` → `StopMcp`, a fire-and-forget `DisposeAsync`); nothing in the app calls `AppHost.DisposeAsync`, which disposes `AppHost.Mcp` first only for hosts that are disposed (tools, tests).
  - Use `127.0.0.1` in URLs: HTTP.sys rejects a `localhost` Host header for this prefix. Client names are self-reported.

JSON source-generated records pass `null` for missing keys, so their defaults use `init => field = value ?? …`. Non-modal windows follow skin changes via `MainWindow.FollowSkin` (each has `ApplySkin` that rebuilds its layout).

Design system (`docs/DesignSystem/`, "Grafit" = the `modern-dark`/`modern-light` skins): its tokens are optional `SkinPalette` fields (mutedForeground, surface2, hover/pressed, focusRing, activeHeader*, selectedBackground, warning/success, warning/error/highlight/code backgrounds); `SkinBrushes` derives a fallback for each from the classic colours, so retro skins and old skin JSON keep working. `SkinBrushes.IsModern` (CornerRadius > 0) switches the card layout: rounded panel cards, tab bar, path bar with a 3 px accent edge, uppercase column header, inset rounded rows with `FileIcons` vector icons, palette search box in the menu bar; retro skins keep the frame-and-text look. `SkinManager.ApplyToControls` maps tokens onto Fluent resources (buttons, text boxes, combo boxes, menus) — prefer that over styling each control. Density (`AppSettings.Density`: compact/default/comfortable) scales font size and row height.

- Build: `dotnet build Commander4K.sln` — `TreatWarningsAsErrors` is on globally, so any warning fails the build.
- Run: `dotnet run --project src/Commander4K.App` (portable mode: `config/` and `data/index.db` are created next to the executable, `%AppData%\Commander4K` if that is not writable). `C4K_LANGUAGE=en` overrides the UI language for one run.
- Window placement:
  - `AppSettings.Window` is saved on exit: the restored bounds and whether the window was maximized. It is `null` until the first exit, so the first start opens maximized.
  - `Program` reads it before the window exists (`AppSettings.ReadWindowPlacement` → `MainWindow.StartupPlacement`), like the language, so the window does not jump.
  - The saved position is reused only if a screen still contains it.
- VS Code: `.vscode/` is checked in.
  - `launch.json`: F5 debugs the app with `coreclr`, with an English-UI profile and attach.
  - `tasks.json`: build (default), build app, run, watch, test (default), test (filter), release.
  - Also recommended extensions, and the `plugin.json` schema in `settings.json`.
  - From the app, `cm_OpenInVsCode` (Ctrl+Shift+E) uses the `vscode` tool. It opens a folder plus files through the `{files}` token. Tools missing from an older `tools.json` fall back to `ExternalToolsService.CreateDefaultConfig()`.
- Test: `dotnet test --solution Commander4K.sln` — `global.json` opts into Microsoft.Testing.Platform (VSTest mode is rejected by the .NET 10 SDK, so don't add `Microsoft.NET.Test.Sdk`). Tests use xunit.v3.
- Single test: `dotnet test --solution Commander4K.sln -- --filter-method "*Chord_waits*"` (xunit.v3 filter options go after `--`).
- Release: `pwsh ./build-release.ps1 [-Runtime win-arm64] [-Version 0.2.0 -Package]` → ReadyToRun single-file exe per RID in `dist/publish/<rid>/` (~260–310 MB; the size is R2R code, not symbols — the csproj already strips package `.pdb`s). It never touches `dist/<rid>/`, which may be a running portable copy. `-Package` writes `dist/release/Commander4K-<rid>.zip` (portable) and `.msi` (WiX 5 project in `installer/`, isolated from the repo's Directory.*.props, built with `--no-incremental` per RID — incremental builds once produced an x64 MSI under the arm64 name) plus `SHA256SUMS.txt`, the exact asset names `UpdateService` looks for. WiX 6+ is avoided on purpose (Open Source Maintenance Fee EULA).
- Install kinds (`UpdateService.DetectInstallKind`): `Commander4K.installed` next to the exe = MSI install (update by running the new MSI); otherwise single-file = portable (update by extracting the exe from the ZIP and swapping it in); `Commander4K.dll` present = development (no self-update).
- Publishing: the project is open source (MIT, © 4Kprojekt) at https://github.com/4kprojekt/Commander4K (remote `github`; `origin` is the maintainer's private Gitea). CI runs on every push/PR; pushing a `v*` tag runs `.github/workflows/release.yml`, which tests, packages both RIDs and creates the GitHub release (a `-` in the tag marks a pre-release). README states the project is built with AI; keep the `Co-Authored-By` trailers.
- Versioning: `VersionPrefix` in `Directory.Build.props` (local builds are `X.Y.Z-dev`); releases pass `-p:Version` from the tag. The SDK appends `+<commit>` to the informational version, which `AppVersion.Display` shows as `0.1.0 (abc1234)` in the title bar, settings and About.
- Updates: `UpdateService` reads GitHub Releases for `AppSettings.UpdateRepository`, refuses assets without a matching `SHA256SUMS.txt` entry, and installs by renaming the running exe to `.old` (deleted on next start). Self-install only works for the published single-file exe (`CanSelfUpdate`); `dotnet run` builds just link to the release page.
- UI verification without a desktop session: reference `Commander4K.App` plus `Avalonia.Headless` from a scratch console, `UseSkia().UseHeadless(new() { UseHeadlessDrawing = false }).SetupWithoutStarting()`, create `MainWindow`, call `Attach(host)` (skips the async start-up), pump `Dispatcher.UIThread.RunJobs()`, drive keys with `window.KeyPress(...)` and save `window.CaptureRenderedFrame()` as PNG.

Localization rule: every user-visible text goes through `Loc.T("polski tekst")` / `Loc.T("… {0} …", arg)` / `Loc.P(count, "{0} plik|{0} pliki|{0} plików")` (`Core/Localization`). Polish is the source language and the key (gettext style); never build UI text with `$"…"`. Each new key needs an entry in `src/Commander4K.Core/Localization/i18n/en.json` — `LocalizationTests` scans `src` and fails on missing or stale entries and on placeholder/plural mismatches. Keys starting with `_//` are comments. Not translated on purpose: logs and audit entries, developer exceptions in English, AI prompts, the default `powershell.json` catalogue, and the SDK (which cannot reference Core — permission descriptions are wrapped where shown). The language is loaded in `Program.Main` before the first window (static UI arrays are evaluated early); users add languages as `config/i18n/<code>.json`, `TranslationKit` builds the AI prompt (with en.json as reference) and checks files (`cm_Translate`).

Settings rule: everything user-configurable must be discoverable. A new option goes into `AppSettings` (JSON) **and** `SettingsCatalog.Create` (title, Polish description, search keywords); it then appears in the searchable settings window (`Ctrl+,`) and the command palette (`Ctrl+Shift+P`). New commands get a `CommandIds` constant, a Polish description in `MainWindow.RegisterCommands` and (optionally) a `DefaultKeymap` binding. Search (`SettingsCatalog.Search`) is word-based and ignores case and Polish diacritics.

UI (`Commander4K.App`) gotchas:
- `Avalonia.Markup.Declarative`'s source generator breaks on `partial` view/window classes split across files (duplicate hint name) and on public events with more than one parameter — keep each view in one file and use single-argument events (see `AuthenticationRequest`).
- Avalonia 12 renames: `Watermark` → `PlaceholderText`; clipboard `SetTextAsync` needs `using Avalonia.Input.Platform`; there is no `FocusManager.ClearFocus` (focus the window instead).
- Beware lambdas whose parameter is named `_`: inside them `_ = SomeTask()` assigns to that parameter instead of discarding (bit this code twice).
- `MainWindow` routes keys in a tunnelling handler: panel cursor keys first, then `KeymapEngine`; while a `TextBox` has focus only Ctrl/Alt combos, F-keys and Tab reach the keymap. Typing on a panel goes to the command line.
- `PanelTab` owns listing state; it streams batches into `Entries` while loading and swaps in a sorted list at the end — late batches are dropped by the `_generation`/`IsLoading` guard. Refreshes (`RefreshAsync`, file watcher) list off-screen and swap, preserving cursor and marks.
- `ClaudeSkinGenerator` uses `claude-opus-5-5`, structured outputs from `SkinSchema.CreateForStructuredOutput()` (every object closed with all properties required; no `pattern`, which the API rejects — colours are validated when parsing) and the server-side refusal fallback. Credentials come from the SDK chain (`ANTHROPIC_API_KEY` or `ant auth login`).

Build conventions: shared settings live in `Directory.Build.props` (TFM, RIDs, nullable, warnings-as-errors); package versions are pinned centrally in `Directory.Packages.props`, so `PackageReference` items carry no `Version`. `Commander4K.Plugins.BuiltIn` has `AllowUnsafeBlocks` for `[LibraryImport]`.

Core implementation notes that span files:
- `VfsRouter` resolves providers via `VfsPath.GetScheme` (`C:\` → `file`, `\\srv\share` → `smb`, otherwise the `scheme://` prefix), and wraps every provider listing in `ResilientAsyncExecutor.StreamAsync`, so plugin code never needs its own hang protection. `OpenListing` returns a bounded `ChannelReader<VfsEntry>`; the UI consumes it with `ChannelBatching.ReadBatchesAsync` (first batch immediately, then ≤ one per 16 ms).
- Debugger-friendly background work:
  - Expected failures (cancellation when leaving a folder, access denied, timeouts) must be caught in our code. They must never leave a lambda passed to `Task.Run`, `Task.Factory.StartNew` or a `Parallel.ForEachAsync` body. Otherwise VS Code with "Just My Code" stops on them as user-unhandled.
  - `ResilientAsyncExecutor` turns synchronous throws into faulted tasks. LAN discovery sources and probes end quietly on cancellation; only the consumer of the listing sees the cancellation.
  - Never call `.AsTask()` on an async iterator's `MoveNextAsync()`: .NET's adapter reads the failed result inside its own code. `StreamAsync` awaits steps in `StepAsync`/`AwaitStepAsync` instead (regression test: `StreamFaultTests`).
  - To see which exception a paused F5 session is on, take a dump with `MiniDumpWriteDump` and run `dotnet-dump analyze` → `clrthreads`, `setthread`, `pe -nested`.
- `SqliteIndexService`: writes are fire-and-forget records (`IndexWriteCommand` subclasses) drained by one writer in batched transactions; use `FlushAsync()` as a barrier (tests rely on it). Microsoft.Data.Sqlite's async methods are synchronous, so reads are explicitly `Task.Run`.
- Progress reporting goes through `ThrottledProgress<T>` (30 Hz, thread-safe, `Flush()` for the final value); `JobQueueManager` and `DirectorySizeCalculator` both use it. `JobChanged` fires on worker threads — the UI must marshal.
- Network plugin lessons verified on a real LAN: `NetServerEnum` blocks 30 s+ when no Computer Browser exists, so it runs under a 3 s abandonable timeout; `NetShareEnum` against a host without SMB waits ~10 s, so host level probes ports 445/22/21 first and only enumerates shares when 445 answers; reverse DNS yields `name.lan` and the bare label often does not resolve forward, so host paths use the full DNS name while the display name is the short label. More measured behaviour (2026-10-31, on a machine with Hyper-V/VPN adapters, 1012 candidate addresses):
  - A TCP probe must be bounded by `WaitAsync(timeout)` and closing the socket, not only by `ConnectAsync`'s token. A probe that never completed once kept `net://` and This PC "loading" for good.
  - Reverse lookups (`Dns.GetHostEntryAsync`) hold a pool thread for 5–10 s when an address has no name. They are capped at 4 concurrent.
  - ARP neighbours get a 600 ms connect timeout (150 ms for the rest), otherwise busy hosts were missed.
  - The whole root discovery has a 6 s budget (`DiscoveryBudget`). Found hosts are cached, so they appear at once on the next visit.
  - The preview follows the cursor while a folder is still loading.
- Paths: providers never emit `..` (the UI adds it from `GetParentPath`). Registry values are addressed as `reg://HIVE\key\\ValueName` (double backslash, which can't occur in key paths). Remote paths are parsed by hand, not with `Uri`, to keep `#`/`?`/`%` in names. The plugin's default SFTP `HostKeyValidator` accepts every key; the app replaces it with trust-on-first-use (`KnownHostsStore`, `config/known_hosts.json`) and a confirmation dialog — keep that wiring if you touch start-up.
- Plugin tests write to a throw-away `HKCU\Software\Commander4K.Tests\<guid>` key and delete it afterwards.
- Skins: `BuiltInSkins` + user JSON files in `config/skins/` (same id overrides a built-in), selected skin id persisted in `config/settings.json`; `config/skins/skin.schema.json` is regenerated from the model on every start. Commands `cm_SelectSkin` (F2, Ctrl+Shift+K) and `cm_NextSkin` (Ctrl+Alt+K). `SkinPrompt.ForChat` is the copy-to-clipboard prompt for Claude.ai / Claude Design; `SkinValidator` reports WCAG contrast problems.
- `config/keymap.json` and `config/tools.json` are generated from `DefaultKeymap` / `ExternalToolsService.CreateDefaultConfig()` on first run by `ConfigStore`, not checked in. Key names follow Avalonia's `Key` enum, normalised by `KeyStroke`.

Key constraints to keep in mind (details below): .NET 10 `net10.0-windows`; no `.axaml`/`.xaml` files (Avalonia + `Avalonia.Markup.Declarative`, C# only); no I/O on the UI thread; directory/search results stream via `IAsyncEnumerable` -> `Channel<T>` -> 16 ms UI batches; blocking UNC/SMB calls go through `ResilientAsyncExecutor` (`.WaitAsync(ct)`); no `ListBox`/`DataGrid` for file lists (custom `FileViewportCanvas` owner-draw); Win32 interop via `[LibraryImport]`, SIMD via `SearchValues<T>`/`Vector128/256<T>` (no hard-coded AVX2) for ARM64 parity; project layering is PluginSdk <- Core <- Plugins.BuiltIn <- App.

---

# ROLA I CEL PROJEKTU
Jesteś Principal .NET Systems Architectem. Twoim zadaniem jest zaprojektowanie, utworzenie struktury solucji i zaimplementowanie działającego fundamentu (MVP Core) ultraszybkiego, wielowątkowego, dwupanelowego menedżera plików dla systemu Windows 11 o nazwie **Commander4K**.

Projekt musi być napisany w **.NET 10 (`net10.0-windows`)**, w **100% w czystym kodzie C# (ZERO plików `.axaml` / `.xaml`)** przy użyciu **Avalonia UI (v11+ MIT)** oraz **`Avalonia.Markup.Declarative`**. 

Nadrzędnym celem architektonicznym **Commander4K** jest **ABSOLUTNY BRAK ZACIĘĆ INTERFEJSU (Zero-UI-Freeze Guarantee)**, maksymalne wykorzystanie wielowątkowości procesora, strumieniowa asynchroniczność (`IAsyncEnumerable` + `System.Threading.Channels`) oraz natywna obsługa przeglądania sieci lokalnej (LAN/SMB) i zdalnych serwerów.

---

## 1. ZASADY "ZERO-UI-FREEZE" I WIELOWĄTKOWOŚCI (CRITICAL RULES)

1. **Żadnego I/O na Wątku UI:** Wątek UI służy wyłącznie do odbierania zdarzeń klawiatury/myszy i rysowania klatek (`Render`). Jakiekolwiek odwołanie do dysku, rejestru, sieci czy SQLite na wątku głównym jest błędem architektonicznym.
2. **Strumieniowanie danych (`Channel<T>` + UI Batching):**
   - Wczytywanie katalogów, udziałów sieciowych i wyników wyszukiwania odbywa się przez `IAsyncEnumerable<VfsEntry>` przekierowane do `Channel<VfsEntry>`.
   - Warstwa prezentacji konsumuje kanał w tle, buforuje elementy w paczki (np. co 16 ms przez `PeriodicTimer`) i przekazuje gotowy snapshot do odrysowania na `FileViewportCanvas`. Dzięki temu użytkownik widzi pierwsze pliki po 1 ms, nawet gdy katalog na serwerze ma 500 000 plików.
3. **Twarda izolacja blokujących wywołań sieciowych (Abandonable Tasks):**
   - Systemowe wywołania UNC/SMB w Windows potrafią zablokować wątek na 30 sekund, ignorując standardowy `CancellationToken`.
   - Zaimplementuj w `Commander4K.Core` klasę `ResilientAsyncExecutor`, która uruchamia potencjalnie blokujące operacje I/O/SMB na dedykowanych zadaniach w tle i spina je przez `.WaitAsync(cancellationToken)`. Wciśnięcie `Esc` lub zmiana ścieżki przez użytkownika natychmiast odłącza UI od wiszącego zadania i wyświetla nowy widok w 0 ms.
4. **Równoległa Kolejka Zadań (`JobQueueManager`):**
   - Kopiowanie, przenoszenie, pakowanie i usuwanie działają w niezależnych pulach wątków w tle (jak kolejki F5 -> F2 w Total Commanderze).
   - Każde zadanie raportuje postęp bezalokacyjnie (przez `IProgress<JobMetrics>` z użyciem struktur `readonly record struct` i throttlingiem odświeżania paska postępu do 30 Hz).
5. **Custom Owner-Draw (`FileViewportCanvas`):**
   - NIE UŻYWAJ standardowego `ListBox` ani `DataGrid` z osobnymi kontrolkami dla każdej komórki!
   - Zaimplementuj dedykowaną kontrolkę dziedziczącą po `Control`, która w metodzie `Render(DrawingContext context)` rysuje bezpośrednio (Skia / Direct2D) wyłącznie te wiersze, które mieszczą się w aktualnym `Viewport`.

---

## 2. STRUKTURA SOLUCJI (`Commander4K.sln`)

Utwórz czystą, modularną architekturę opartą o 4 projekty:

1. **`src/Commander4K.PluginSdk`** (Class Library, kontrakty rozszerzeń):
   - `IVfsProviderPlugin` (WFX: asynchroniczny, strumieniowy interfejs dla systemów plików: `local://`, `net://`, `smb://`, `sftp://`, `ftp://`, `reg://`).
   - `IArchivePlugin` (WCX: obsługa archiwów).
   - `IViewerPlugin` (WLX: asynchroniczny podgląd plików `F3` / `Ctrl+Q`).
   - `IColumnMetadataPlugin` (WDX: wielowątkowe doczytywanie kolumn w tle – np. rozmiar folderów po wciśnięciu `Space`, status Git, EXIF, Hash).
   - `ISearchProviderPlugin` (silniki wielowątkowego wyszukiwania `Parallel.ForEachAsync` + SIMD `SearchValues<byte>`).

2. **`src/Commander4K.Core`** (Silnik wielowątkowy, VFS Router, SQLite Indexer, Keymap, External Tools):
   - `VfsRouter` oraz `ResilientAsyncExecutor` (zarządzanie sesjami, pulą połączeń, anulowaniem zadań i timeoutami).
   - `DirectorySizeCalculator` (wielowątkowe liczenie rozmiaru katalogów w tle z natychmiastową aktualizacją wiersza bez blokowania nawigacji).
   - `PluginLoader` oparty o izolowane `AssemblyLoadContext` (`isCollectible: true`).
   - `KeymapEngine` oraz `CommandRegistry` (obsługa skrótów w czasie $O(1)$ przez `FrozenDictionary`).
   - `SqliteIndexService` (asynchroniczny indeksator i cache).
   - `ExternalToolsService` (integracja z narzędziami systemu Windows 11).

3. **`src/Commander4K.Plugins.BuiltIn`** (Wbudowane wtyczki VFS):
   - `LocalFileSystemPlugin` (oparty o `FileSystemEnumerable<T>` + asynchroniczny `FileSystemWatcher` z debouncingiem).
   - **`NetworkNeighborhoodVfsPlugin` (`net://` oraz `smb://`)** – ultraszybkie przeglądanie sieci lokalnej i udziałów SMB.
   - `RemoteTransferVfsPlugin` (`sftp://`, `ftp://` na bazie `SSH.NET` i `FluentFTP` z podtrzymywaniem puli połączeń Keep-Alive).
   - `WindowsRegistryVfsPlugin` (`reg://` – przeglądanie, edycja i wielowątkowe przeszukiwanie Rejestru Windows).
   - `ArchiveVfsPlugin` (`zip://`, `7z://`, `tar://` na bazie `SharpCompress`).

4. **`src/Commander4K.App`** (Aplikacja okienkowa Avalonia UI 100% C#-First):
   - Deklaratywne UI zbudowane przy użyciu `Avalonia.Markup.Declarative`.
   - Układ Dual-Pane z obsługą zakładek (`Ctrl+T`), nieblokującym wskaźnikiem ładowania wewnątrz panelu (Spinner/Skeleton + przycisk `[Esc - Anuluj]`), menedżerem zadań w tle (Background Jobs Bar), linią poleceń oraz dolnym paskiem klawiszy funkcyjnych.

---

## 3. SZCZEGÓŁOWE WYMAGANIA FUNKCJONALNE

### A. Przeglądanie Sieci i Udziałów (`NetworkNeighborhoodVfsPlugin` - `net://`)
Wpisanie `net://` w pasku adresu (lub skrót `Alt+F1` / `Alt+F2` -> Sieć) otwiera wirtualny katalog sieciowy, który działa wielotorowo i całkowicie nieblokująco:
1. **Poziom główny (`net://`):**
   - Wyświetla zapisane/ulubione serwery i udziały (SFTP, FTP, SMB), aktywnie wykryte hosty w sieci LAN oraz szybki skrót `+ Dodaj połączenie sieciowe`.
   - **Fast LAN Discovery:** Zamiast zawieszać się na starym protokole NetBIOS, uruchamia w tle `Parallel.ForEachAsync` skanujący lokalną podsieć (ARP table + szybki asynchroniczny connect test na porty `445` SMB, `22` SFTP/SSH, `21` FTP z timeoutem 150 ms) oraz odpytuje Win32 API (`WNetOpenEnum` / `NetServerEnum`) na osobnej puli wątków, strumieniując wykryte maszyny na żywo do panelu.
2. **Poziom hosta (`net://NAZWA_HOSTA` lub `\\IP`):**
   - Wylicza dostępne udziały SMB (`NetShareEnum` przez P/Invoke w tle) wraz z opisami oraz otwarte porty usług (np. automatyczne wejście po SFTP jeśli wykryto port 22).
3. **Poziom udziału (`\\SERWER\Udział`):**
   - Płynne przejście do strumieniowego odczytu plików z obsługą uwierzytelniania (okno logowania dla chronionych udziałów/serwerów) oraz zapisywaniem struktury katalogów w cache SQLite (tryb *Offline/Cached Browsing* dla wolnych łącz).

### B. Hybrydowa Indeksacja SQLite (`SqliteIndexService`)
Skonfiguruj `Microsoft.Data.Sqlite` w trybie wysokiej współbieżności (`PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA mmap_size = 268435456;`).
- Wszystkie zapisy do bazy przechodzą przez pojedynczy, asynchroniczny `Channel<IndexWriteCommand>` (Single-Writer pattern), natomiast odczyty (wyszukiwanie) wykonują się współbieżnie z wielu wątków bez blokad.
- **Funkcje bazy:**
  1. **Frecency Navigation (`Ctrl+G` / `Alt+F10`):** Błyskawiczny skok do najczęściej używanych folderów lokalnych i udziałów sieciowych.
  2. **Indeks pełnotekstowy (`FTS5` trigram):** Wyszukiwanie plików w ułamku sekundy (`Alt+F7`).
  3. **Network & Metadata Cache:** Zapamiętywanie wykrytych hostów w sieci LAN, historii serwerów oraz wyliczonych rozmiarów katalogów i sum kontrolnych.

### C. Silnik Skrótów (`KeymapEngine`) i Integracja z Narzędziami (`ExternalToolsService`)
- Plik `config/keymap.json` mapujący skróty i akordy klawiszowe na komendy z `CommandRegistry` (np. `cm_SwitchPanel`, `cm_OpenNetwork`, `cm_OpenRegistry`, `cm_CalculateDirSizes`, `cm_SearchAsync`, `cm_CancelCurrentOperation`).
- Plik `config/tools.json` integrujący zewnętrzne aplikacje bez blokowania procesu `Commander4K` (`Process.Start` z flagą `UseShellExecute = false`):
  - Edycja (`F4` -> VS Code / Notepad++),
  - Porównywanie (`Ctrl+F12` -> VS Code Diff / WinMerge),
  - Terminal (`Ctrl+O` / `F9` -> Windows Terminal / PowerShell 7 otwarty w bieżącej ścieżce lokalnej lub UNC).

### D. Wielowątkowy Plugin Rejestru Windows (`reg://`)
- Przeglądanie drzewa Rejestru (`HKCU`, `HKLM`, `HKCR`, `HKU`, `HKCC`) jako systemu plików.
- Asynchroniczna wyszukiwarka (`Alt+F7`) przeszukująca równolegle gałęzie rejestru na wielu rdzeniach CPU (`Parallel.ForEachAsync`) ze strumieniowym zwracaniem wyników w czasie rzeczywistym i natychmiastowym przerwaniem po wciśnięciu `Esc`.

---

## 4. PLAN WDROŻENIA DLA AGENTA (KROK PO KROKU)

Realizuj projekt etapami, dbając o to, aby po każdym kroku kod kompilował się czysto (`dotnet build`) bez ostrzeżeń:

1. **Krok 1 (Inicjalizacja Solucji):** Utwórz `Commander4K.sln` oraz 4 projekty (`.NET 10`) wraz z paczkami NuGet (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Markup.Declarative`, `Microsoft.Data.Sqlite`, `FluentFTP`, `SSH.NET`, `SharpCompress`).
2. **Krok 2 (Silnik Wielowątkowy i VFS Core):** Zaimplementuj `Commander4K.PluginSdk` oraz `Commander4K.Core` — w szczególności strumieniowy `VfsRouter`, `ResilientAsyncExecutor` (ochrona przed zawieszaniem na I/O), `JobQueueManager`, `SqliteIndexService` (z kolejką `Channel`), `KeymapEngine` oraz `ExternalToolsService`.
3. **Krok 3 (Wtyczki Wbudowane i Sieć):** Zaimplementuj w `Commander4K.Plugins.BuiltIn` wtyczki: `LocalFileSystemPlugin`, `NetworkNeighborhoodVfsPlugin` (`net://` z równoległym skanerem LAN TCP/SMB i enumeracją udziałów Win32), `WindowsRegistryVfsPlugin` (`reg://`) oraz szkielet `RemoteTransferVfsPlugin` (`sftp://`, `ftp://`).
4. **Krok 4 (UI C#-First & `FileViewportCanvas`):** Zbuduj w `Commander4K.App` kompletny interfejs dwupanelowy (bez plików XAML) z użyciem `Avalonia.Markup.Declarative`. Zaimplementuj kontrolkę `FileViewportCanvas` z bezpośrednim rysowaniem na `DrawingContext`, płynnym odbieraniem strumieni danych z `Channel<VfsEntry>` (batching 16 ms), nieblokującym paskiem ładowania oraz obsługą przerwania operacji klawiszem `Esc`.
5. **Krok 5 (Weryfikacja i Testy Bojem):** Skompiluj cały projekt (`dotnet build`), zweryfikuj działanie skrótów klawiszowych, przełączania między dyskami lokalnymi, `net://` oraz `reg://`, i wygeneruj podsumowanie architektury.
6. **Pełne wsparcie Multi-Arch (`win-x64` oraz `win-arm64`):**
   - W plikach `.csproj` skonfiguruj `<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>` oraz `<PlatformTarget>AnyCPU</PlatformTarget>`.
   - Wszystkie wywołania natywne Win32 API (np. `NetShareEnum`, `WNetOpenEnum`) implementuj przy użyciu nowoczesnego generatora źródeł `[LibraryImport]` (zamiast starego `[DllImport]`) oraz typów `nint` / `nuint`, zapewniając 100% kompatybilności z architekturą ARM64 (Snapdragon X Elite) oraz x64.
   - Zamiast twardych instrukcji procesora (jak `Avx2`), do szybkiego wyszukiwania binarnego/tekstowego używaj abstrakcji `System.Buffers.SearchValues<T>` oraz `Vector128<T>` / `Vector256<T>`, które automatycznie wykorzystują instrukcje `AdvSIMD (NEON)` na ARM64 oraz `AVX2/AVX-512` na x64.
   - Przygotuj w głównym katalogu skrypt `build-release.ps1` (PowerShell 7), który buduje zoptymalizowane binarki ReadyToRun (`-p:PublishReadyToRun=true -p:PublishSingleFile=true --self-contained`) osobno dla `win-x64` (do `dist/win-x64`) oraz `win-arm64` (do `dist/win-arm64`).