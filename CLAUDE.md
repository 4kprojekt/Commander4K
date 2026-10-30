# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository status

The sections below (in Polish) are the project specification and implementation plan; treat them as the source of truth for architecture. Steps 1–4 and the release script are done. Plugin architecture (protocol spec for plugin authors: `docs/plugins.md`, manifest schema `docs/plugin.schema.json`):
- Every plugin has a `PluginManifest` (id, version, author, declared permissions). Plugin code only ever receives an `IPluginHost` from `AppHost.HostFor(manifest)` — a `ScopedPluginHost` that checks the manifest's permissions on gated calls (fs.read/fs.write/process.kill/shell.run/net) and writes every call to `AuditLog` (`data/logs/plugins-YYYYMMDD.log`). Built-ins follow the same rule for side effects (task manager kills via `KillProcessAsync`, PowerShell runs via `RunProcessAsync`, log analyzer reads via `OpenReadAsync`); `process.read`/`system.info` are declarative only. Don't hand plugins the window or `PluginHostAdapter` (that is the UI side, `IPluginUi`).
- External plugins: folders with `plugin.json` under `plugins/` (next to the exe and in the data folder), run out of process (`ExternalPlugin`, JSON-RPC 2.0 NDJSON over stdio via `JsonRpcConnection`), started lazily, disabled until the user approves their permissions (`PluginApprovalStore`, re-consent when permissions grow). `RuntimeCatalog` detects Node/Python/PowerShell/.NET versions and checks `runtime.version` ranges. Adapters `ExternalCommandPlugin`/`ExternalPanelPlugin` make them look like built-ins to the UI. There is no in-process loading of third-party assemblies — by design, since .NET has no sandbox.
- Client SDKs: `plugins-sdk/{python,node,powershell}` (single files); the examples in `examples/plugins` carry copies (a test keeps them identical) and are embedded in the app as templates for the plugin manager. `ExternalPluginTests` run the examples against the real runtimes.
- Panels open in the panel opposite the active one; while the active panel shows a plugin, plugin "context" (active path, selection) is the other — visible — file panel.
- Plugin panels: `IPanelPlugin` (data-only snapshots rendered by `App/PluginPanels/PluginPanelView`), `ICommandPlugin` (menu/palette/keymap commands as `plugin:<id>:<cmd>`, panels as `panel:<id>`). Built-ins: system info, task manager, log analyzer, PowerShell catalogue (`config/powershell.json`), plugin manager (`builtin.plugin-manager`).

Panel modes and preview: a `FilePanelView` shows either its file list or an `IPanelModeView` over it (`PluginPanelView`, `PreviewPanelView`; browser later). F1 = settings, F2 = panel mode picker (files / preview / any panel plugin; applied to the opposite panel), Ctrl+Q toggles the preview. Previews are `IPreviewPlugin`s (built-in: markdown 50, image 40, folder 10, text/hex 0; external via `contributes.previews` + `preview.render`); `PreviewPanelView` follows the source tab's `Changed`/`ActiveTabChanged` events (no polling), debounces 120 ms, cancels the previous load immediately, never follows while there are unsaved edits, and saves through the preview plugin's `ScopedPluginHost` (fs.write). `MarkdownRenderer` turns Markdig's AST into Avalonia controls (no HTML engine). Folder sizes go through `IPluginHost.CalculateSizeAsync` (one audit entry per folder, not per directory read). Keys pressed inside a mode's own text box: F-keys, Ctrl/Alt combos and Esc go to the mode first; Backspace/arrows/letters stay with the text box. Default-binding changes between versions go into `DefaultKeymap.ChangedDefaults` (applied once per keymap.json, recorded in `appliedMigrations`, never overriding a user's own binding).

Palettes: `IPalettePlugin` (items + header/per-item actions) rendered by `App/Palettes/PaletteView` in one of four docks (`MainWindow`: left/right outer columns, bottom row, and "between", which lives inside the right half so the `GridSplitter` keeps two star columns). Built-ins in `BuiltInPalettes.cs` (quick actions, favourites, saved views = both panels' paths + modes, recent/frecency) talk to the window through `IShell`; external ones come from `contributes.palettes` (`palette.items/invoke/action`, reloaded after navigation and after actions). Each palette's position is a runtime setting (`palette:<id>`, stored in `AppSettings.PaletteDocks`) registered into `AppHost.ExtraSettings`, so it is searchable like the rest; Ctrl+B hides all, Ctrl+D adds a favourite.

Browser: `App/Browser/BrowserPanelView` is a panel mode (`cm_Browser` Ctrl+Alt+W, F2) hosting WebView2 through `WebView2Host : NativeControlHost` (only `Microsoft.Web.WebView2.Core` — the csproj drops the package's WinForms/WPF references, which break the build, and keeps its `.xml` out of publish). Profile in `data/webview2`, shared per process, so sign-ins persist. It is the built-in plugin `builtin.browser` (top-level navigations audited). Plugins reach it via `IPluginHost.OpenUrlAsync` / `SearchWebAsync` (`ui.openUrl` / `ui.search`); engines and address-bar parsing live in `Core/Web/SearchEngines` (Gemini takes no query parameter, so the question goes to the clipboard). Keys pressed inside the page never reach Avalonia: `AcceleratorKeyPressed` forwards F-keys (except F5/F12) and Ctrl+Shift/Alt combos to the keymap. It cannot run headless; verify in a real window (`CapturePreviewAsync` for the page image).

Not yet written: `ArchiveVfsPlugin`, WDX columns, recursive delete of remote directories. Beyond the spec, the user asked for switchable skins (Norton Commander, Midnight Commander, modern...) including skins generated with AI: the model lives in `Core/Theming`, UI code must paint from `SkinBrushes` (never hard-coded colours), and `App/Theming/ClaudeSkinGenerator.cs` designs skins via the Claude API.

- Build: `dotnet build Commander4K.sln` — `TreatWarningsAsErrors` is on globally, so any warning fails the build.
- Run: `dotnet run --project src/Commander4K.App` (portable mode: `config/` and `data/index.db` are created next to the executable, `%AppData%\Commander4K` if that is not writable).
- Test: `dotnet test --solution Commander4K.sln` — `global.json` opts into Microsoft.Testing.Platform (VSTest mode is rejected by the .NET 10 SDK, so don't add `Microsoft.NET.Test.Sdk`). Tests use xunit.v3.
- Single test: `dotnet test --solution Commander4K.sln -- --filter-method "*Chord_waits*"` (xunit.v3 filter options go after `--`).
- Release: `pwsh ./build-release.ps1 [-Runtime win-arm64] [-Version 0.2.0 -Package]` → ReadyToRun single-file exe per RID in `dist/publish/<rid>/` (~260–310 MB; the size is R2R code, not symbols — the csproj already strips package `.pdb`s). It never touches `dist/<rid>/`, which may be a running portable copy. `-Package` writes `dist/release/Commander4K-<rid>.zip` (portable) and `.msi` (WiX 5 project in `installer/`, isolated from the repo's Directory.*.props, built with `--no-incremental` per RID — incremental builds once produced an x64 MSI under the arm64 name) plus `SHA256SUMS.txt`, the exact asset names `UpdateService` looks for. WiX 6+ is avoided on purpose (Open Source Maintenance Fee EULA).
- Install kinds (`UpdateService.DetectInstallKind`): `Commander4K.installed` next to the exe = MSI install (update by running the new MSI); otherwise single-file = portable (update by extracting the exe from the ZIP and swapping it in); `Commander4K.dll` present = development (no self-update).
- Publishing: the project is open source (MIT, © 4Kprojekt) at https://github.com/4kprojekt/Commander4K (remote `github`; `origin` is the maintainer's private Gitea). CI runs on every push/PR; pushing a `v*` tag runs `.github/workflows/release.yml`, which tests, packages both RIDs and creates the GitHub release (a `-` in the tag marks a pre-release). README states the project is built with AI; keep the `Co-Authored-By` trailers.
- Versioning: `VersionPrefix` in `Directory.Build.props` (local builds are `X.Y.Z-dev`); releases pass `-p:Version` from the tag. The SDK appends `+<commit>` to the informational version, which `AppVersion.Display` shows as `0.1.0 (abc1234)` in the title bar, settings and About.
- Updates: `UpdateService` reads GitHub Releases for `AppSettings.UpdateRepository`, refuses assets without a matching `SHA256SUMS.txt` entry, and installs by renaming the running exe to `.old` (deleted on next start). Self-install only works for the published single-file exe (`CanSelfUpdate`); `dotnet run` builds just link to the release page.
- UI verification without a desktop session: reference `Commander4K.App` plus `Avalonia.Headless` from a scratch console, `UseSkia().UseHeadless(new() { UseHeadlessDrawing = false }).SetupWithoutStarting()`, create `MainWindow`, call `Attach(host)` (skips the async start-up), pump `Dispatcher.UIThread.RunJobs()`, drive keys with `window.KeyPress(...)` and save `window.CaptureRenderedFrame()` as PNG.

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
- `SqliteIndexService`: writes are fire-and-forget records (`IndexWriteCommand` subclasses) drained by one writer in batched transactions; use `FlushAsync()` as a barrier (tests rely on it). Microsoft.Data.Sqlite's async methods are synchronous, so reads are explicitly `Task.Run`.
- Progress reporting goes through `ThrottledProgress<T>` (30 Hz, thread-safe, `Flush()` for the final value); `JobQueueManager` and `DirectorySizeCalculator` both use it. `JobChanged` fires on worker threads — the UI must marshal.
- Network plugin lessons verified on a real LAN: `NetServerEnum` blocks 30 s+ when no Computer Browser exists, so it runs under a 3 s abandonable timeout; `NetShareEnum` against a host without SMB waits ~10 s, so host level probes ports 445/22/21 first and only enumerates shares when 445 answers; reverse DNS yields `name.lan` and the bare label often does not resolve forward, so host paths use the full DNS name while the display name is the short label.
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