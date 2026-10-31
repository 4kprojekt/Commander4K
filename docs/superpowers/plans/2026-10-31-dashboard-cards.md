# Dashboard Cards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dashboard tiles in `c4k.dir` become live cards rendered by previews (folder, gallery, notes, dashboard, API, SSH monitor, external plugins), with charts, configurable refresh, consent for side-effecting network cards, back navigation between dashboards, and three downloadable preview plugins (HTML, JSON, OpenAPI).

**Architecture:** The SDK gets a card contract (`IPreviewCardPlugin`, `PreviewCard`, `ChartData`). Plugins.BuiltIn gets the card loaders (folder views, API, SSH) and a resolver that maps a tile to a loader. Core gets the UI-free machinery (refresh loop, view history, secrets, consent rules, `c4k.dir` tile parsing/writing). App renders `DashboardPreview` as a card grid with an owner-drawn chart control, adds the navigation bar to pinned views, the consent dialog and the API-card wizard. External plugins get `preview.card` + `kind: "chart"`.

**Tech Stack:** .NET 10, Avalonia 12 + Avalonia.Markup.Declarative (C# only), SSH.NET, System.Text.Json, xunit.v3; Python 3 for the catalog plugins.

**Spec:** `docs/superpowers/specs/2026-10-31-dashboard-cards-design.md` (Polish). Read it before every task; this plan does not repeat its rationale.

**Commits:** the working tree already holds unrelated uncommitted work in files this plan touches (`MainWindow.cs`, `PreviewPanelView.cs`, `en.json`…). The "Commit" steps are therefore checkpoints: run the build and tests, but do not commit unless the user asks.

## Global Constraints

- No I/O on the UI thread; every card loads independently with its own cancellation and a **15 s** timeout; at most **4** card loads at once; a slow card never delays others.
- Refresh: `"refresh"` = `"<n>s" | "<n>m" | "<n>h"`; values below **2 s** are raised to 2 s; no refresh = one read + „Odśwież”. Refresh runs only while the dashboard is visible; a tick is skipped while the previous read runs.
- Charts keep at most **120** points per series, in memory only.
- View history stack limit **50**; cycles allowed.
- API wizard lists at most **500** field paths.
- Consent: `api` cards with GET/HEAD need none; other methods and every `ssh` card need `FolderTrustStore` trust of the `c4k.dir` (path + SHA-256), only on `file`/`smb`; elsewhere those cards are "niedostępne w tym miejscu".
- Secrets: `{secret:<name>}` placeholders in url/headers/body; values in `config/secrets.json`, `SecretProtector` (DPAPI), each bound to the host it was first used with; never written to `c4k.dir`.
- SSH monitor runs one fixed read-only command; nothing from `c4k.dir` becomes a remote command.
- Esc in a pinned view always shows the files of the folder on screen; Shift+Enter always enters as files; the window handles both before cards/plugin content.
- Old `c4k.dir` tiles without new fields render exactly as today.
- UI text via `Loc.T`/`Loc.P` + `en.json` entries; colours only from `SkinBrushes`; works in modern (`IsModern`) and retro skins; no `.axaml`; single-argument public events; no `partial` view classes split across files.
- New protocol surface: `ExternalPlugin`/adapters, all three SDK clients, copies in every example and `plugins-catalog` plugin (identity test), `docs/plugins.md` table, `docs/plugin.schema.json`; `c4k.dir` changes also go to `docs/c4k.dir.schema.json` and the `c4k.dir` guide in `docs/plugins.md`.
- Expected exceptions (cancellation, timeout, access denied, auth) are caught in our code, never escape `Task.Run` lambdas.
- Build: `dotnet build Commander4K.sln` (warnings are errors). Tests: `dotnet test --solution Commander4K.sln`; single: `dotnet test --solution Commander4K.sln -- --filter-method "*Name*"`.

## Review Focus

1. **A card target that hangs** (dead SSH host, API that never answers, hung share): the card turns into an error card after 15 s with „Odśwież”, other cards keep refreshing, the window stays responsive → Task 5 test `Hanging_loader_times_out_without_blocking_others`.
2. **Dashboard closed while loads and timers are in flight** (Esc, folder change, panel mode switch): nothing keeps running, no late result is applied, no exception surfaces → Task 5 test `Stop_cancels_in_flight_loads_and_timers`.
3. **Hand-edited tile with bad values** (`"refresh": "fast"`, `api` without `url`, `card: 5`, unknown `as`): the dashboard shows the other tiles and the bad one as an error card naming the problem; it never fails the whole file → Task 2 test `Bad_card_fields_become_tile_errors`.
4. **API answers with something unexpected** (HTML error page, 5xx, empty body, 5 MB JSON, path that does not exist): status `Error` or the field shows „—”, the body excerpt is ≤ 200 chars → Task 10 tests `Non_json_and_errors_show_status_and_excerpt`, `Missing_path_shows_dash`.
5. **`c4k.dir` edited after trust** (someone changes a POST URL in a shared folder): the card returns to „wymaga zgody” before any request → Task 8 test `Changed_file_loses_trust_for_post_and_ssh`.

---

### Task 1: SDK card and chart contracts

**Files:**
- Create: `src/Commander4K.PluginSdk/PreviewCards.cs`
- Modify: `src/Commander4K.PluginSdk/IPreviewPlugin.cs` (add `ChartPreview`, `DashboardPreview` records)
- Test: `tests/Commander4K.Core.Tests/PreviewCardTests.cs`

**Interfaces:**
- Produces (exact):
  ```csharp
  public enum CardStatus { None, Ok, Warning, Error }
  public sealed record PreviewCardRequest(VfsEntry? Target, string? OptionsJson, PreviewCard? Previous);
  public sealed record PreviewCard(string? Title, string? Subtitle, string? Icon, CardStatus Status, IReadOnlyList<CardItem> Items, IReadOnlyList<CardAction> Actions);
  public abstract record CardItem;
  public sealed record CardValue(string Label, string Value, CardStatus Status = CardStatus.None) : CardItem;
  public sealed record CardThumbnails(IReadOnlyList<ReadOnlyMemory<byte>> Images) : CardItem;
  public sealed record CardChart(ChartData Chart) : CardItem;
  public sealed record CardText(string Text) : CardItem;
  public sealed record CardProgress(string Label, double Fraction, string? Text = null) : CardItem;
  public sealed record CardAction(string Label, string Target);   // Target as PreviewTile.Target, plus "c4k:refresh"
  public enum ChartKind { Line, Bar }
  public sealed record ChartPoint(double X, double Y, string? Label = null);
  public sealed record ChartSeries(string Name, IReadOnlyList<ChartPoint> Points);
  public sealed record ChartData(ChartKind Kind, IReadOnlyList<ChartSeries> Series, string? Unit = null, double? Min = null, double? Max = null)
  {
      public const int MaxPoints = 120;
      /// Appends to the series named `series` (created when missing), keeping the last MaxPoints.
      public ChartData Append(string series, ChartPoint point);
  }
  public interface IPreviewCardPlugin : IPreviewPlugin
  {
      Task<PreviewCard> LoadCardAsync(PreviewCardRequest request, IPluginHost host, CancellationToken cancellationToken);
  }
  public sealed record ChartPreview(string Title, ChartData Chart, string? Description = null) : PreviewContent;
  public static class CardActions { public const string Refresh = "c4k:refresh"; }
  ```
- `DashboardPreview` is defined in Task 4 (it needs Core types) — not here.

- [ ] **Step 1: Write the failing tests** `Append_creates_series_and_keeps_last_120` (append 130 points to "cpu" → one series, 120 points, first X == 10) and `Append_leaves_other_series_untouched`.
- [ ] **Step 2: Run** `dotnet test --solution Commander4K.sln -- --filter-class "*PreviewCardTests"` → FAIL (types missing).
- [ ] **Step 3: Implement** the records above with XML doc comments in the style of `IPreviewPlugin.cs`.
- [ ] **Step 4: Run** the same filter → PASS; `dotnet build Commander4K.sln` clean.
- [ ] **Step 5: Commit** `Add preview card and chart contracts to the SDK`.

### Task 2: `c4k.dir` card tiles

**Files:**
- Modify: `src/Commander4K.Core/Folders/FolderConfig.cs`
- Modify: `docs/c4k.dir.schema.json`
- Test: `tests/Commander4K.Core.Tests/FolderConfigTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  ```csharp
  public static class FolderViewIds { public const string Api = "api"; public const string Ssh = "ssh"; /* All gains Api; Ssh is card-only */ }
  public enum TileSize { Small, Wide }
  public sealed record ApiField(string Path, string? Label, ApiFieldKind As, string? Unit, IReadOnlyList<string> Ok);
  public enum ApiFieldKind { Value, Chart, Status }
  public sealed record ApiRequest(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string? Body, string? ContentType, IReadOnlyList<ApiField> Fields)
  { public bool HasSideEffects => Method is not ("GET" or "HEAD"); }
  // FolderTile gains (as init properties so existing positional construction still compiles):
  //   string? Card  — null = plain shortcut; "auto" when the file says `true`; otherwise the name
  //   TimeSpan? Refresh; string? Options (raw JSON object); TileSize Size; ApiRequest? Api; string? Error
  // FolderAction gains: ApiRequest? Api  (for "view": "api")
  public static TimeSpan? ParseRefresh(string? text);   // "10s" → 10 s, "1m", "1h"; < 2 s → 2 s; invalid → FormatException
  ```
- A tile with an invalid card field gets `Error` set (message in English, like other `FolderConfig` errors) and keeps `Title`; the rest of the file parses. Only `title` missing stays a file-level error (today's behaviour).
- `card: "dashboard"` is a valid name; `run` + `card` on one tile → tile `Error` ("A card cannot run a command.").

- [ ] **Step 1: Write failing tests**:
  - `Card_tiles_parse_card_refresh_options_size_and_api` — the jsonc example from the spec section "Kafelek-karta w c4k.dir"; assert `Card` values `"gallery"`, `"auto"`, `"ssh"`, `"api"`, `"official.steam-library"`; VPS `Refresh == 10 s`; Grafana `Api.Fields[0].As == ApiFieldKind.Status`, `Ok == ["ok"]`, header value kept verbatim `"Bearer {secret:grafana}"`; Steam `Options == "{ \"top\": 3 }"` (raw); old shortcut tile has `Card == null`.
  - `Refresh_is_clamped_and_parsed` — `ParseRefresh("1s") == 2 s`, `"90s"`, `"5m"`, `"2h"`; `"fast"` throws `FormatException`.
  - `Bad_card_fields_become_tile_errors` (Review Focus 3) — tiles with `"refresh": "fast"`, `"card": "api"` without `api.url`, `"card": 5`, field `"as": "pie"`, `run`+`card`: `config.Error` is null, each of those tiles has non-null `Error`, a good tile beside them has `Error == null`.
  - `Api_view_parses_as_preview_action` — `"preview": { "view": "api", "api": { "url": "https://x/y" } }` → `Preview.Api.Method == "GET"`.
- [ ] **Step 2: Run** `--filter-class "*FolderConfigTests"` → FAIL.
- [ ] **Step 3: Implement** parsing in `ParseTile`/`ParseAction`; method upper-cased; headers object of strings only; `fields` default `as` = Value.
- [ ] **Step 4: Update** `docs/c4k.dir.schema.json` (tile: `card` bool|string, `refresh` pattern `^[0-9]+[smh]$`, `options` object, `size` enum, `api` object; action: `api`; view enum gains `api`).
- [ ] **Step 5: Run** tests → PASS; build clean.
- [ ] **Step 6: Commit** `Parse card tiles in c4k.dir`.

### Task 3: Card loaders for folder, gallery, notes, markdown and dashboard

**Files:**
- Create: `src/Commander4K.Plugins.BuiltIn/Previews/FolderCards.cs`
- Create: `src/Commander4K.Plugins.BuiltIn/Previews/FolderSizeCache.cs`
- Modify: `src/Commander4K.Plugins.BuiltIn/Previews/FilePreviewPlugins.cs` (`FolderPreviewPlugin` implements `IPreviewCardPlugin`) — or wherever `FolderPreviewPlugin` lives
- Test: `tests/Commander4K.Plugins.Tests/PreviewTests.cs`

**Interfaces:**
- Consumes: Task 1 records; Task 2 `FolderConfig`, `FolderTile`.
- Produces:
  ```csharp
  public static class FolderCards
  {
      // view: FolderViewIds.Files | Gallery | Notes | Markdown | Dashboard
      public static Task<PreviewCard> LoadAsync(string view, VfsEntry folder, FolderConfig? config, IPluginHost host,
          PreviewCardRequest request, FolderSizeCache sizes, CancellationToken cancellationToken);
  }
  public sealed class FolderSizeCache
  {
      // Starts (or reuses) a background CalculateSizeAsync for path+lastWrite; returns the finished value or null while computing.
      public DirectorySizeInfo? GetOrStart(IPluginHost host, VfsEntry folder, Action onCompleted);
  }
  ```
- Folder card: `CardValue`s „Pliki”, „Foldery” (top level, from `ListAsync`), „Zmieniono” (folder `LastWrite`), „Rozmiar” = cached total or „liczę…”; action „Otwórz” → folder path. The size computation runs outside the card's 15 s timeout; `onCompleted` lets the dashboard re-render that card (Task 6 passes it).
- Gallery card: up to 4 `CardThumbnails` (same image rules as `FolderViews.GalleryAsync`, only those 4 files read), „Obrazy” count, total size; action „Otwórz”.
- Notes: note count + most recently changed note name; Markdown: first paragraph (≤ 300 chars) of `action.File ?? README.md`; action „Otwórz”.
- Dashboard card: title/description from `config`, „Kafelki” count, `CardText` with the first 3 tile titles joined by „ · ”; no nested loads; action „Otwórz” → folder path.

- [ ] **Step 1: Write failing tests** in `PreviewTests`: `Folder_card_counts_top_level_and_fills_size_later` (first call: size value „liczę…”; after `onCompleted`: second call shows 501 000 B formatted), `Folder_size_is_recomputed_only_when_folder_changes`, `Gallery_card_reads_only_four_images` (6 images; assert 4 thumbnails; audit shows ≤ 4 `fs.read` opens of image files), `Dashboard_card_lists_first_three_tiles_without_loading_them`.
- [ ] **Step 2: Run** `--filter-class "*PreviewTests"` → FAIL.
- [ ] **Step 3: Implement** `FolderCards`, `FolderSizeCache` (key = path + `LastWrite`; `ConcurrentDictionary`; failures stored as „—”).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Add folder, gallery, notes and dashboard cards`.

### Task 4: Tile → card resolver and `DashboardPreview`

**Files:**
- Create: `src/Commander4K.Plugins.BuiltIn/Dashboard/CardResolver.cs`
- Modify: `src/Commander4K.Plugins.BuiltIn/Previews/FolderViews.cs` (`DashboardAsync` returns `DashboardPreview`)
- Create: `src/Commander4K.Plugins.BuiltIn/Dashboard/DashboardPreview.cs` (it references `FolderTile`, so it cannot live in the SDK)
- Test: `tests/Commander4K.Plugins.Tests/DashboardTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:
  ```csharp
  public sealed record DashboardSlot(int Index, FolderTile Tile, PreviewTile Shortcut);   // Shortcut = today's tile (fallback look, click target)
  public sealed record DashboardPreview(string Title, string? Description, FolderConfig Config, string Section, IReadOnlyList<DashboardSlot> Slots) : PreviewContent;
  public delegate Task<PreviewCard> CardLoader(PreviewCard? previous, CancellationToken cancellationToken);
  public sealed record CardSource(CardLoader Load, IPluginHost Host, bool NeedsTrust, string? Unavailable);
  public sealed class CardResolver(
      IReadOnlyList<IPreviewPlugin> previews,
      Func<IPluginHost> builtInHost,
      Func<PluginManifest, IPluginHost> hostFor,
      Func<string, CancellationToken, Task<FolderConfig?>> readFolderConfig,
      FolderSizeCache sizes,
      ApiCards api,          // Task 10
      SshMonitor ssh)        // Task 12
  {
      // null for plain shortcut tiles (Card == null) → rendered as today's tile
      public Task<CardSource?> ResolveAsync(FolderConfig config, FolderTile tile, CancellationToken cancellationToken);
  }
  ```
- Resolution order: `tile.Error` → loader returning an `Error` card with that message; `"api"` → `ApiCards`; `"ssh"` → `SshMonitor` (target must be `sftp://`); built-in view names → `FolderCards`; `"auto"` → stat target; if container: read its `c4k.dir` (same 2 s limit and schemes as `AppHost.ReadFolderConfigAsync`), use its `preview` view/plugin, else highest-priority `IPreviewPlugin` accepting the entry; plugin id (with optional `/preview`) → that `IPreviewCardPlugin`; a plugin without card support → default card from `Shortcut` (icon, title, subtitle); missing plugin → card with action `c4k:install/<id>` or `c4k:plugins` like `PreviewPanelView.ResolveFolderView`.
- `NeedsTrust` = `ssh` or `api` with `HasSideEffects`; `Unavailable` set for those when `config.Folder` is not on `file`/`smb`.
- Tasks 10 and 12 add `ApiCards`/`SshMonitor`; until then pass stubs — create both classes in this task as empty shells whose `LoadCardAsync` returns an `Error` card „not implemented”, so the resolver compiles and is tested now.

- [ ] **Step 1: Write failing tests**: `Plain_tiles_resolve_to_null`, `Auto_card_follows_target_c4k_dir_preview` (target folder with `c4k.dir` `"preview": "gallery"` → gallery card), `Auto_card_on_dashboard_folder_gives_dashboard_card`, `Named_card_ignores_target_c4k_dir`, `Post_api_and_ssh_need_trust_get_does_not`, `Ssh_card_on_sftp_folder_config_is_unavailable`, `Unknown_plugin_card_offers_install`.
- [ ] **Step 2: Run** `--filter-class "*DashboardTests"` → FAIL.
- [ ] **Step 3: Implement** resolver + `DashboardPreview`; change `FolderViews.DashboardAsync` to build slots (shortcut tiles exactly as today's `PreviewTile`s).
- [ ] **Step 4: Run** → PASS; existing `PreviewTests`/`FolderConfigTests` still pass.
- [ ] **Step 5: Commit** `Resolve dashboard tiles to card sources`.

### Task 5: Card refresh loop

**Files:**
- Create: `src/Commander4K.Core/Dashboard/CardRefreshLoop.cs`
- Test: `tests/Commander4K.Core.Tests/CardRefreshLoopTests.cs`

**Interfaces:**
- Consumes: Task 1 (`PreviewCard`, `CardStatus`).
- Produces:
  ```csharp
  public sealed record CardState(int Index, PreviewCard? Card, bool Loading, string? Error);
  public sealed class CardRefreshLoop : IAsyncDisposable
  {
      public CardRefreshLoop(int maxConcurrent = 4, TimeSpan? timeout = null /* 15 s */, TimeProvider? time = null);
      public event Action<CardState>? Updated;          // raised on a pool thread; the UI marshals
      public void Add(int index, Func<PreviewCard?, CancellationToken, Task<PreviewCard>> load, TimeSpan? refresh);
      public void RefreshNow(int index);               // c4k:refresh / „Odśwież”; ignored while that card loads
      public void Start();
      public Task StopAsync();                          // cancels loads and timers; no Updated after it completes
      public ValueTask DisposeAsync();                  // = StopAsync
  }
  ```
- Each card: first load at `Start`; with `refresh`, a `PeriodicTimer` (via `TimeProvider`) that skips a tick while loading; `previous` = last successful card; timeout → `Error` = „Przekroczono czas (15 s).” (localized), previous card kept in `Card`; exceptions → `Error` = message; a `SemaphoreSlim(maxConcurrent)` gates loads; nothing escapes `Task.Run` lambdas.

- [ ] **Step 1: Write failing tests** (use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` if already referenced; otherwise a minimal fake `TimeProvider` in the test file — do not add a package without checking `Directory.Packages.props`):
  - `Hanging_loader_times_out_without_blocking_others` (Review Focus 1): 5 cards, card 0 never completes, timeout 200 ms, `maxConcurrent` 4 → cards 1–4 report a card; card 0 reports the timeout error; total wall time < 2 s.
  - `Refresh_passes_previous_and_skips_ticks_while_loading`.
  - `Stop_cancels_in_flight_loads_and_timers` (Review Focus 2): after `StopAsync`, loaders observed cancellation and no `Updated` arrives within 300 ms.
  - `Refresh_now_is_ignored_while_loading`.
- [ ] **Step 2: Run** `--filter-class "*CardRefreshLoopTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS (run 3× to check for flakiness).
- [ ] **Step 5: Commit** `Add the card refresh loop`.

### Task 6: Dashboard rendering and chart control (App)

**Files:**
- Create: `src/Commander4K.App/Preview/DashboardView.cs` (card grid, one file, no `partial`)
- Create: `src/Commander4K.App/Preview/ChartControl.cs` (owner-draw `Control.Render`)
- Modify: `src/Commander4K.App/Preview/PreviewPanelView.cs` (`Show`: `DashboardPreview` → `DashboardView`, `ChartPreview` → `ChartControl`; stop the dashboard's loop in `LoadAsync`/`DisposeModeAsync`)
- Modify: `src/Commander4K.App/Composition/AppHost.cs` (create one `FolderSizeCache`, `ApiCards`, `SshMonitor`, expose `CardResolver CardResolver`)
- Modify: `src/Commander4K.Core/Localization/i18n/en.json`

**Interfaces:**
- Consumes: Tasks 1, 3, 4, 5; `PreviewPanelView.OpenTile` for clicks and actions.
- Produces: `DashboardView(AppHost app, DashboardPreview dashboard, SkinBrushes brushes, Func<PreviewTile, Task> open, Func<int, Task> askTrust)` with `Task StopAsync()`; `ChartControl { ChartData? Chart; SkinBrushes Brushes; }`.
- Layout: `WrapPanel` of cards, width 260 (`Small`) or 536 (`Wide`); modern skin: rounded card (`CornerRadius` from skin), 3 px status strip on the left (`success`/`warning`/`error` tokens; none for `None`); retro: frame + text. Card: icon + title row, subtitle, items in order (`CardValue` label/value rows, `CardProgress` bar, `CardThumbnails` row of 4 × 56 px, `CardChart` 80 px high, `CardText` wrapped), action buttons at the bottom, small „⏳”/error line. Plain tiles (`CardSource == null`) render with today's tile look inside the same grid. Click on card background = `Shortcut.Target`. Action `c4k:refresh` → `loop.RefreshNow`; others → `open`.
- Thumbnails decode off the UI thread (reuse the `_thumbnails` approach in `PreviewPanelView`).
- Chart: line/bar, Y axis min/max (from data or `Min`/`Max`) with unit, series colours cycling `accent`, `success`, `warning`, `error`, `highlight` brushes.
- `NeedsTrust` cards (not trusted) show „Wymaga zgody” + „Zezwól” (calls `askTrust(index)` → Task 8); `Unavailable` cards show that text, no loader runs.

- [ ] **Step 1: Implement** the view, chart control and wiring; strings through `Loc.T` + `en.json`.
- [ ] **Step 2: Run** `dotnet build Commander4K.sln` → clean; `--filter-class "*LocalizationTests"` → PASS.
- [ ] **Step 3: Verify headless** per CLAUDE.md recipe (scratch console in the scratchpad, not committed): temp folder with `c4k.dir` holding a gallery card, a folder card, an `auto` card, a plain shortcut and an API card pointing to a local `HttpListener` returning `{"cpu": 12}` with `refresh: "2s"`; enter it, pump 5 s, save PNGs for `modern-dark` and a retro skin. Expected: all cards visible, chart has ≥ 2 points, plain tile looks like before.
- [ ] **Step 4: Commit** `Render dashboards as live cards with charts`.

### Task 7: Pinned-view history, navigation bar and dashboard-in-dashboard

**Files:**
- Create: `src/Commander4K.Core/Folders/ViewHistory.cs`
- Modify: `src/Commander4K.App/Preview/PreviewPanelView.cs` (pinned: bar with „←”, breadcrumbs, „📄 Pliki”; history; keys)
- Modify: `src/Commander4K.App/MainWindow.cs` (`OpenTileAsync`: a container target whose `c4k.dir` `enter`/`preview` is a dashboard opens inside the current pinned view instead of closing it; Alt+Left routing)
- Test: `tests/Commander4K.Core.Tests/ViewHistoryTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class ViewHistory<T>(int limit = 50)
  {
      public T Current { get; }            // throws when empty
      public IReadOnlyList<T> Trail { get; }  // oldest → current (breadcrumbs)
      public bool CanGoBack { get; }
      public void Push(T item);            // drops the oldest beyond limit
      public T Back();                     // returns the new Current
      public T JumpTo(int trailIndex);     // breadcrumb click; drops later entries
      public void Clear();
  }
  ```
- `PreviewPanelView` pinned constructor seeds `ViewHistory<PinnedFolder>`; new method `Task OpenPinnedAsync(PinnedFolder next)` pushes and loads; Back/JumpTo also call `_source.NavigateAsync(folder)` so Esc shows the right files.
- Keys in pinned mode: Esc → `CloseRequested` (unchanged); Backspace (editor not focused) and Alt+Left → Back when `CanGoBack`, otherwise Backspace keeps today's close behaviour. Keys are handled in `HandleKey`, which the window calls before content — check `MainWindow`'s tunnelling handler also routes Alt+Left to the mode first.
- In the unpinned preview, a dashboard card click keeps today's behaviour (Enter on the folder).

- [ ] **Step 1: Write failing tests** `Push_back_and_trail`, `Cycles_are_kept`, `Limit_drops_oldest_at_50`, `Jump_to_breadcrumb_drops_later`.
- [ ] **Step 2: Run** `--filter-class "*ViewHistoryTests"` → FAIL; implement; → PASS.
- [ ] **Step 3: Implement** the bar and routing in App; `Loc.T` + `en.json`.
- [ ] **Step 4: Verify headless**: dashboards A → B (card click) → Alt+Left (A on screen, panel path A) → card B → Esc (file list of B). Save PNG of the bar in modern and retro.
- [ ] **Step 5: Commit** `Navigate between dashboards with back and breadcrumbs`.

### Task 8: Consent for side-effecting network cards

**Files:**
- Modify: `src/Commander4K.Core/Folders/FolderConfig.cs` (add `TrustSummary`)
- Modify: `src/Commander4K.App/MainWindow.cs` (extract the existing `RunFolderCommandAsync` consent dialog into `Task<bool> AskFolderTrustAsync(FolderConfig config)` and use it for cards)
- Modify: `src/Commander4K.App/Preview/DashboardView.cs` (trust check before starting `NeedsTrust` loaders)
- Test: `tests/Commander4K.Core.Tests/FolderConfigTests.cs`

**Interfaces:**
- Produces: `public IReadOnlyList<string> FolderConfig.TrustSummary()` — one line per thing trust unlocks: `POST https://…`, `SSH kamil@vps.example.com`, `run build.ps1 -Release` (English/technical, shown verbatim in the dialog under a localized heading).
- The dialog text lists `TrustSummary()`; consent = `FolderTrust.Trust(config)`; after consent the dashboard starts those loaders without reloading the whole view.

- [ ] **Step 1: Write failing tests** `Trust_summary_lists_post_ssh_and_run_but_not_get` and `Changed_file_loses_trust_for_post_and_ssh` (Review Focus 5: trust config A, parse a variant with a different POST URL from the same path → `IsTrusted` false).
- [ ] **Step 2: Run** → FAIL; implement; → PASS.
- [ ] **Step 3: Wire** the App side; build clean.
- [ ] **Step 4: Commit** `Ask for consent before POST and SSH cards`.

### Task 9: Secret store

**Files:**
- Create: `src/Commander4K.Core/Security/SecretStore.cs`
- Modify: `src/Commander4K.App/Composition/AppHost.cs` (`SecretStore Secrets` at `config/secrets.json`)
- Test: `tests/Commander4K.Core.Tests/SecretStoreTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class SecretStore(string path)
  {
      public void Set(string name, string value, string host);                 // host bound at first save
      public string? Get(string name, string host);                           // null if missing or bound to another host
      public bool Exists(string name);
      // Replaces every {secret:<name>} in text; missing → SecretMissingException(name); wrong host → SecretHostMismatchException(name, boundHost)
      public string Expand(string text, string host);
  }
  ```
- File format `{ "<name>": { "value": "dpapi:…", "host": "grafana.example.com" } }`, written atomically (tmp + move) like `FolderTrustStore`; host comparison case-insensitive.

- [ ] **Step 1: Write failing tests** `Values_are_encrypted_on_disk`, `Expand_replaces_placeholders`, `Secret_is_refused_for_another_host`, `Missing_secret_throws_with_its_name`.
- [ ] **Step 2: Run** → FAIL; implement; → PASS.
- [ ] **Step 3: Commit** `Add the DPAPI secret store for API cards`.

### Task 10: API card and API view

**Files:**
- Modify (fill the shell from Task 4): `src/Commander4K.Plugins.BuiltIn/Dashboard/ApiCards.cs`
- Create: `src/Commander4K.Plugins.BuiltIn/Dashboard/JsonFieldPath.cs`
- Modify: `src/Commander4K.Plugins.BuiltIn/Previews/FolderViews.cs` (`view: "api"` full preview)
- Test: `tests/Commander4K.Plugins.Tests/ApiCardTests.cs`

**Interfaces:**
- Consumes: Task 2 `ApiRequest`/`ApiField`, Task 9 `SecretStore` (passed as `Func<string, string, string> expand` to keep BuiltIn free of the store type, or the store itself — Core is referenced by BuiltIn, so pass `SecretStore`).
- Produces:
  ```csharp
  public static class JsonFieldPath
  {
      public static bool TryGet(JsonElement root, string path, out JsonElement value);       // "a.b", "items[0].name", "$"
      public static IReadOnlyList<(string Path, string Preview)> Flatten(JsonElement root, int max = 500); // leaves; arrays sampled at [0]
  }
  public sealed class ApiCards(SecretStore secrets)
  {
      public Task<PreviewCard> LoadCardAsync(ApiRequest request, IPluginHost host, PreviewCard? previous, CancellationToken cancellationToken);
      public Task<PreviewContent> LoadViewAsync(ApiRequest request, IPluginHost host, CancellationToken cancellationToken); // fields large + chart + formatted JSON
  }
  ```
- Request via `host.HttpRequestAsync` (built-in host has `net`); placeholders expanded in url, header values, body with `host = new Uri(expandedUrl).Host` (expand URL first using the URL's literal host).
- Field rendering: Value → `CardValue(label ?? last path segment, text + unit)`; missing path → „—”; Chart → append to series named after the label in `previous`'s `CardChart` (one chart for all chart fields), X = Unix seconds; Status → ok when value ∈ `Ok`, or without `Ok`: `true`, `"ok"`, `"up"`, `"healthy"` (case-insensitive) → `Ok` else `Error`.
- Card status: non-2xx or exception → `Error` with `CardValue("HTTP", "<status>")`; any status field `Error` → `Warning`; else `Ok`. Always a `CardValue("Czas", "<ms> ms")`. Non-JSON or no fields → `CardText` of the first 200 chars. Missing secret → `Error` card with action `c4k:secret/<name>` (handled in Task 11's App code by prompting and `Secrets.Set`).

- [ ] **Step 1: Write failing tests** with a stub `HttpMessageHandler` behind the `HttpClient` given to `ScopedPluginHost` (pattern as in `PreviewTests` constructor): `Fields_render_values_status_and_units`, `Chart_fields_append_to_previous`, `Non_json_and_errors_show_status_and_excerpt` (Review Focus 4: HTML 502 body → `Error`, excerpt ≤ 200 chars), `Missing_path_shows_dash`, `Secrets_expand_into_headers_and_are_refused_for_other_hosts`, `Flatten_lists_leaf_paths_up_to_500`, `Path_syntax` (`a.b`, `items[0].name`, `$`).
- [ ] **Step 2: Run** `--filter-class "*ApiCardTests"` → FAIL; implement; → PASS.
- [ ] **Step 3: Commit** `Add the API card and API view`.

### Task 11: „Dodaj kartę API” wizard

**Files:**
- Create: `src/Commander4K.Core/Folders/FolderConfigWriter.cs`
- Create: `src/Commander4K.App/Dialogs/ApiFieldsDialog.cs`
- Modify: `src/Commander4K.Core/Input/CommandIds.cs` (`AddApiCard = "cm_AddApiCard"`), `src/Commander4K.App/MainWindow.cs` (`RegisterCommands` + `AddApiCardAsync`; handle `c4k:secret/<name>` targets in `OpenTileAsync`), `en.json`
- Test: `tests/Commander4K.Core.Tests/FolderConfigWriterTests.cs`

**Interfaces:**
- Consumes: Task 10 `JsonFieldPath.Flatten`, Task 9 `SecretStore`, Task 8 trust.
- Produces:
  ```csharp
  public static class FolderConfigWriter
  {
      public static bool HasComments(string json);
      // Returns the new file text. existing null → new file with preview+enter = dashboard. If preview is not a dashboard:
      // replacePreview must be true (caller asked) → preview becomes dashboard, old preview moved to enter only if enter was empty.
      public static string AppendTile(string? existing, JsonObject tile, bool replacePreview);
  }
  ```
- Flow exactly as spec section „Komenda Dodaj kartę API” steps 1–6; header value stored as secret named after the host (`grafana.example.com` → `grafana-example-com`), file gets `Bearer {secret:…}`-style placeholder (whole header value replaced by `{secret:name}`); probe request off the UI thread with cancel; `ApiFieldsDialog` = searchable checklist with per-row kind (value/chart/status) and label; refresh choice none/10 s/1 min/5 min; warns when `HasComments`; trust after save only if the file was trusted before or did not exist (Task 8 store).

- [ ] **Step 1: Write failing tests** `Append_to_new_file_creates_dashboard`, `Append_keeps_other_tiles_and_keys`, `Replace_preview_moves_old_preview_to_empty_enter`, `Detects_comments`.
- [ ] **Step 2: Run** → FAIL; implement writer; → PASS.
- [ ] **Step 3: Implement** dialog + command; build clean; `LocalizationTests` PASS.
- [ ] **Step 4: Verify headless**: run the command against a local `HttpListener` JSON, select two fields, check the written `c4k.dir` parses with `FolderConfig.Parse` and the dashboard shows the card.
- [ ] **Step 5: Commit** `Add the API card wizard`.

### Task 12: SSH monitor

**Files:**
- Modify (fill the shell): `src/Commander4K.Plugins.BuiltIn/Remote/SshMonitor.cs`
- Create: `src/Commander4K.Plugins.BuiltIn/Remote/SshStats.cs` (parser)
- Modify: `src/Commander4K.Plugins.BuiltIn/Remote/RemoteTransferVfsPlugin.cs` (`public RemoteLogin? LoginFor(string path)`; expose `HostKeyValidator` use), `RemoteSessions.cs` (make `AuthenticationMethods` internal static for reuse)
- Test: `tests/Commander4K.Plugins.Tests/SshMonitorTests.cs` (+ live case in `RemoteServerTests` behind `C4K_TEST_SFTP`)

**Interfaces:**
- Produces:
  ```csharp
  public sealed record SshStats(string System, double[] LoadAverage, long[]? CpuTicks, long MemTotalKb, long MemAvailableKb,
      IReadOnlyList<(string Mount, long SizeKb, long UsedKb)> Disks, TimeSpan Uptime);
  public static class SshStatsParser { public const string Command = "…"; public static SshStats Parse(string output); }
  public sealed class SshMonitor(RemoteTransferVfsPlugin remote, AuditLog audit) : IAsyncDisposable
  {
      public Task<PreviewCard> LoadCardAsync(string sftpPath, string? optionsJson, PreviewCard? previous, CancellationToken cancellationToken);
      public ValueTask ReleaseAsync(string sftpPath);   // called by the dashboard when it stops
  }
  ```
- `Command` = the fixed command from the spec, with section markers (e.g. `echo @@uname; uname -s; echo @@loadavg; cat /proc/loadavg; …`) so parsing does not depend on line counts. CPU % = busy delta / total delta from `/proc/stat` vs the ticks of the previous read, which `SshMonitor` keeps in memory per path; the first read shows the load average.
- Login: `options` `user`/`keyFile` (resolved like `FolderConfig.Resolve`), else `remote.LoginFor(path)`, else default keys as user from the path or Windows user. Auth refusal → card `Error` „Wymaga logowania” with action = the `sftp://` path (opening it triggers today's `LoginDialog`). Host key through `remote.HostKeyValidator`. One `SshClient` per session key kept until `ReleaseAsync`. Every connect and exec audited (`audit.Record("builtin.remote", "ssh.monitor", host)` style — check `AuditLog` API).
- Card: status Ok/Error, „CPU” % (or load), `CardProgress` „Pamięć”, up to 3 largest disks as `CardProgress`, „Uptime”, `CardChart` CPU appended; non-Linux → status + system name only. Actions „Otwórz” (path) and „Odśwież” (`c4k:refresh`).

- [ ] **Step 1: Write failing tests** on saved outputs: `Parses_linux_sample`, `Non_linux_sample_gives_system_only`, `Missing_proc_sections_do_not_throw`, `Cpu_percent_from_two_samples`.
- [ ] **Step 2: Run** → FAIL; implement parser; → PASS.
- [ ] **Step 3: Implement** `SshMonitor`; add live test `Ssh_monitor_reads_a_real_server` (skipped without `C4K_TEST_SFTP`; the `atmoz/sftp` image may not allow exec — if exec is refused, assert the card is `Error` with a message, not an exception).
- [ ] **Step 4: Commit** `Add the SSH monitor card`.

### Task 13: External plugins — `preview.card` and `kind: "chart"`

**Files:**
- Modify: `src/Commander4K.PluginSdk/PluginManifest.cs` (`PreviewContribution` gains `bool Card = false`), `src/Commander4K.Core/Plugins/ManifestReader.cs`, `docs/plugin.schema.json`
- Modify: `src/Commander4K.Core/Plugins/ExternalPluginAdapters.cs` (`ExternalPreviewPlugin : IPreviewCardPlugin`; parse `kind: "chart"`; `LoadCardAsync` calls `preview.card` only when `Card`)
- Modify: `plugins-sdk/{python/c4k.py,node/c4k.js,powershell/c4k.ps1}` + copies in `examples/plugins/*` and `plugins-catalog/steam-library-powershell`
- Modify: `examples/plugins/folder-stats-python/{main.py,plugin.json}` (card for its folder preview)
- Modify: `docs/plugins.md`
- Test: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Wire format (document in `docs/plugins.md`): request `preview.card` `{ preview, entry, context, options, previous }`; result `{ title?, subtitle?, icon?, status?: "ok"|"warning"|"error", items: [{kind:"value",label,value,status?} | {kind:"thumbnails",images:[path…]} | {kind:"chart",chart} | {kind:"text",text} | {kind:"progress",label,fraction,text?}], actions: [{label,target}] }`; `chart` = `{ kind: "line"|"bar", unit?, min?, max?, series: [{ name, points: [{x,y,label?}] }] }`; `preview.render` may return `{ kind: "chart", title, description?, chart }`. Thumbnail paths read with the plugin's own `fs.read` (same as tile images, ≤ 4 MB, at most 4).
- SDK helpers (same names in all three, idiomatic casing): `card(title=None, items=(), actions=(), status=None, subtitle=None, icon=None)`, `card_value(label, value, status=None)`, `card_progress(label, fraction, text=None)`, `card_text(text)`, `card_chart(chart)`, `card_thumbnails(paths)`, `chart(kind, series, unit=None, min=None, max=None)`, `action(label, target)`.

- [ ] **Step 1: Write failing tests**: `Python_preview_returns_a_card` (folder-stats card on `_folder`: items contain a value with „3” files), `Chart_preview_kind_parses`, `Previews_without_card_flag_get_the_default_card` (site-check or project-modules preview → `LoadCardAsync` returns title-only card without calling the plugin — assert no `preview.card` in audit), extend `Example_sdk_clients_match_plugins_sdk` (no new rows needed unless new plugins — Tasks 14–16 add theirs).
- [ ] **Step 2: Run** `--filter-class "*ExternalPluginTests"` → FAIL; implement; copy SDK files; → PASS. `PluginSourceCheckTests` PASS.
- [ ] **Step 3: Run** `pwsh ./plugins-catalog/Update-Catalog.ps1` (the steam plugin's SDK copy changed) and `--filter-class "*FolderConfigTests"` (catalog hash test) → PASS.
- [ ] **Step 4: Commit** `Let external previews provide cards and charts`.

### Tasks 14–16: Catalog plugins (one task each, same shape)

Runtime decision (my call, within the spec's constraint): **Python 3 (`runtime.version` `>=3.9`), standard library only**, using SDK calls for every read (`c4k.read_text`) so `PluginSourceCheck` stays clean. OpenAPI YAML is read by a small vendored parser in the plugin folder (`yaml_lite.py`, own code, MIT like the repo) supporting block mappings, block sequences, plain/single/double-quoted scalars, `|`/`>` block scalars, comments and simple flow `[a, b]`/`{a: b}` — enough for OpenAPI files; anything else → readable error in the preview.

Shape of each task:

**Files:** Create `plugins-catalog/<folder>/{plugin.json,main.py,c4k.py,README.md}` (+ `yaml_lite.py` for OpenAPI); add a row to `catalog.json` via `Update-Catalog.ps1` (check the script; add the entry with id, name, Polish/English description, tags, `folderView` example if the catalog schema has it); add an `[InlineData("../../plugins-catalog/<folder>", "python", "c4k.py")]` row to `Example_sdk_clients_match_plugins_sdk`; sample files under `tests/Commander4K.Plugins.Tests/Samples/<folder>/` (copied to output).
**Manifest:** `permissions: ["fs.read"]`; `contributes.previews` with `card: true` and the patterns below; priorities so OpenAPI (70) beats JSON (55) on `openapi.json`, both above built-in text (0).
**Steps:** failing test(s) → run (FAIL) → implement → run (PASS) → `pwsh ./plugins-catalog/Update-Catalog.ps1` → `--filter-class "*FolderConfigTests"` PASS → commit.

- **Task 14 `plugins-catalog/json-preview-python`, id `official.json-preview`:** extensions `.json`, `.jsonc`, `.geojson`. Full preview Markdown: validation result (error with line/column from `json.JSONDecodeError`; `.jsonc` strips `//` and `/* */` comments outside strings first), structure outline (keys, types, array lengths, depth ≤ 4), formatted JSON in a code block (≤ 200 KB, otherwise truncated with a note). Card: status ok/error, size, root type + element count, up to 5 top-level keys. Tests: `Json_preview_formats_and_outlines`, `Json_preview_reports_error_position`, `Json_card_shows_status_and_keys`.
- **Task 15 `plugins-catalog/openapi-preview-python`, id `official.openapi-preview`:** extensions `openapi.json`, `openapi.yaml`, `openapi.yml`, `swagger.json`, `swagger.yaml`, `swagger.yml`, `*.openapi.json`, `*.openapi.yaml`, `*.openapi.yml`. Full preview Markdown: title, version, servers (Swagger 2: `host`+`basePath`+`schemes`), endpoints grouped by first tag (method, path, summary) as tables, schema names (`components.schemas` / `definitions`). Card: title + version, endpoint count per method as a `bar` chart, schema count, action „Otwórz” (path). Tests: `Openapi3_yaml_lists_endpoints_by_tag`, `Swagger2_json_reads_host_and_definitions`, `Openapi_card_has_method_chart`, `Yaml_lite_parses_block_and_flow_collections`.
- **Task 16 `plugins-catalog/html-preview-python`, id `official.html-preview`:** extensions `.htm`, `.html`. Full preview Markdown via `html.parser`: `<title>`, meta description, headings as a table of contents, main text (skip `script`/`style`/`nav`/`footer`), links list (≤ 200) and images list (≤ 100); decode using `<meta charset>` or BOM, fallback UTF-8 then cp1250 (read bytes with `read_bytes`). Card: page title, size, link and image counts, action „Otwórz w przeglądarce” with target `file:///…` — note: `OpenTileAsync` only opens `http(s)` in the browser panel; extend it so `file://` URLs of `.htm/.html` files on local disks open in the browser panel too (App change in this task, with `IsWebUrl` untouched). Tests: `Html_preview_extracts_title_headings_links`, `Html_preview_decodes_cp1250_meta_charset`, `Html_card_counts_links_and_images`.

### Task 17: Docs, CLAUDE.md, final verification

**Files:** `docs/plugins.md` (c4k.dir guide: card tiles, refresh, api, ssh, consent, secrets, navigation keys), `CLAUDE.md` (one paragraph under Folder files describing cards, `CardResolver`, `CardRefreshLoop`, `SecretStore`, consent rule, `ViewHistory`, catalog plugins), `README.md` if it lists features.

- [ ] **Step 1:** Update docs.
- [ ] **Step 2: Run** `dotnet build Commander4K.sln` → 0 warnings; `dotnet test --solution Commander4K.sln` → all PASS (paste the summary line into the task report).
- [ ] **Step 3: Verify headless** one last time: dashboard „Dom” with a dashboard card to „Serwery” (API card + SSH card unavailable/consent state) and „Pliki” (folder, gallery, JSON file card via `auto` with the catalog plugin installed from the local folder); navigate in and back; PNGs in modern-dark and norton skins.
- [ ] **Step 4: Commit** `Document dashboard cards`.
