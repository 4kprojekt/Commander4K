# Folder View Suggestions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the cursor rests on a folder whose content suits a preview (gallery, notes, an external plugin), show a small non-blocking hint; accepting writes `c4k.dir`, opens the view and shows a driver.js-like spotlight on the "back to files" button.

**Architecture:** Previews opt in through a new SDK interface `IFolderSuggestingPreview`; external plugins through a declarative `suggest` rule in `contributes.previews[]`, optionally confirmed by a `preview.suggest` request. A Core `FolderSuggestionService` takes one bounded listing, asks every candidate and streams the best suggestion. The App shows it as a card in the active panel and handles accept/decline, the `c4k.dir` write and the spotlight.

**Tech Stack:** .NET 10, Avalonia 12 + Avalonia.Markup.Declarative (C# only), xunit.v3, Python/Node/PowerShell SDK clients.

**Spec:** `docs/superpowers/specs/2026-11-01-folder-view-suggestions-design.md`

## Global Constraints

- `TreatWarningsAsErrors` is on: `dotnet build Commander4K.sln` must be clean after every task.
- Layering PluginSdk ← Core ← Plugins.BuiltIn ← App; no I/O on the UI thread.
- Every user-visible text goes through `Loc.T` / `Loc.P` (Polish key) with an entry in `src/Commander4K.Core/Localization/i18n/en.json`; `LocalizationTests` must pass.
- UI paints only from `SkinBrushes`; modern (`IsModern`) and retro skins both look right.
- Expected failures (cancel, access denied, timeout, plugin errors) are caught in our code, never leave a `Task.Run` lambda.
- Only `file` and `smb` schemes, and never a path containing `://` (same test as `MainWindow.ConfigureFolderViewAsync`).
- Thresholds: debounce 700 ms; listing ≤ 500 entries; 1.5 s budget for listing + built-in + rule-only candidates; 5 s per `preview.suggest` call; winner needs score ≥ 0.5; matched rule / built-in score = 0.7.
- Gallery: ≥ 5 images and ≥ 60 % of files. Notes: `.obsidian` directly in the folder, or ≥ 5 `.md` files making ≥ 60 % of files. No parent walk.
- Ties: external preview before built-in, then higher `Priority`.
- Declines are stored in `data/folder-suggestions.json`, never in the user's folder. "Nie teraz" mutes for the session only.
- Run tests: `dotnet test --solution Commander4K.sln`; one test: `dotnet test --solution Commander4K.sln -- --filter-method "*Name*"`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A folder on a slow or dead SMB share:** no hint, no stutter; moving the cursor cancels at once (`FolderSuggestionService` test with a listing that never completes → finishes within the budget with no result).
2. **Fast scrolling through many folders:** no card flashes, at most one evaluation running (MainWindow restarts the debounce and cancels; covered by the service test "cancelled token yields nothing").
3. **A folder that already has `c4k.dir` or is muted/declined:** never a hint, even if it is full of images (service test).
4. **An external plugin that crashes, hangs or returns garbage for `preview.suggest`:** no suggestion from it, others still win (external test with a malformed answer + service test with a throwing candidate).
5. **Write refused (read-only share, no rights):** error notice, the view still opens once, no partial `c4k.dir` (Task 6 manual check + `FolderConfigWriter` test for content).

---

### Task 1: SDK contract, manifest rule and rule evaluation

**Files:**
- Create: `src/Commander4K.PluginSdk/IFolderSuggestingPreview.cs`
- Modify: `src/Commander4K.PluginSdk/PluginManifest.cs:63` (`PreviewContribution`)
- Create: `src/Commander4K.Core/Folders/FolderSuggestionRules.cs`
- Test: `tests/Commander4K.Core.Tests/FolderSuggestionRulesTests.cs`

**Interfaces:**
- Produces (SDK):
  ```csharp
  public interface IFolderSuggestingPreview : IPreviewPlugin
  {
      Task<FolderSuggestion?> SuggestAsync(FolderSnapshot folder, IPluginHost host, CancellationToken cancellationToken);
  }
  public sealed record FolderSnapshot(VfsEntry Folder, IReadOnlyList<VfsEntry> Entries, bool Truncated);
  public sealed record FolderSuggestion(double Score, string Message, FolderSuggestionView View);
  /// Exactly one of View (built-in view id) or Plugin is set; Preview/OptionsJson only with Plugin.
  public sealed record FolderSuggestionView(string? View = null, string? Plugin = null, string? Preview = null, string? OptionsJson = null);
  /// Manifest: contributes.previews[].suggest
  public sealed record PreviewSuggestRule(IReadOnlyList<string>? Files = null, int MinCount = 1, double MinShare = 0,
      IReadOnlyList<string>? Contains = null, IReadOnlyDictionary<string, string>? Message = null, bool Ask = false);
  ```
  `PreviewContribution` gains a trailing `PreviewSuggestRule? Suggest = null`.
- Produces (Core, `Commander4K.Core.Folders`):
  - `static int? FolderSuggestionRules.Match(PreviewSuggestRule rule, FolderSnapshot folder)`: the number of matching files, or `null` when the rule fails.
  - `static string FolderSuggestionRules.MessageFor(PreviewSuggestRule rule, int count, string language, string fallbackTitle)`.

- [ ] **Step 1: Write failing tests** in `FolderSuggestionRulesTests` (build snapshots with `new VfsEntry(name, path, VfsEntryKind.File, 1)`):
  - `Files_and_thresholds_match`: rule `Files=["*.jpg","*.png"], MinCount=5, MinShare=0.6` on 6 jpg + 2 txt → `Match == 6`.
  - `Too_few_files_fail`: 4 jpg + 0 others → `null`.
  - `Share_below_threshold_fails`: 5 jpg + 5 txt with `MinShare=0.6` → `null`.
  - `Folders_do_not_count_toward_share`: 5 jpg + 10 directories → `5`.
  - `Contains_requires_entry`: `Contains=[".obsidian"]`, no `Files` → `null` without it, non-null with a directory `.obsidian`.
  - `Truncated_snapshot_uses_what_was_read`: `Truncated: true`, 6 jpg of 6 files → `6`.
  - `Pattern_rules_follow_extension_matching`: `Files=[".csv"]` matches `a.CSV` (same semantics as `ExternalPreviewPlugin.Matches`).
  - `Message_picks_language_then_en_then_first_and_fills_count`: `{"pl":"{count} zdjęć","en":"{count} photos"}` → `"6 zdjęć"` for `pl`, `"6 photos"` for `de`; only `{"fr":"x"}` → `"x"`; no message → `fallbackTitle`.
- [ ] **Step 2: Run** `dotnet test --solution Commander4K.sln -- --filter-class "*FolderSuggestionRulesTests"`. Expected: build error/FAIL (types missing).
- [ ] **Step 3: Implement** the SDK file, the `PreviewContribution` parameter and `FolderSuggestionRules`. Matching reuses `ExternalPreviewPlugin.Matches` (move the pattern test to `FolderSuggestionRules.MatchesPattern` and have `Matches` call it, so there is one implementation). `Files` null + `Contains` set = only the contains test (count = 0, still a match).
- [ ] **Step 4: Run** the same filter. Expected: PASS. Run the full build: clean.
- [ ] **Step 5: Commit** `feat: folder suggestion contract and manifest rule`.

### Task 2: External plugins: `preview.suggest`, docs, SDK helper, example

**Files:**
- Modify: `src/Commander4K.Core/Plugins/ExternalPluginAdapters.cs` (`ExternalPreviewPlugin` implements `IFolderSuggestingPreview`)
- Modify: `docs/plugin.schema.json` (`suggest` object under previews), `docs/plugins.md` (protocol table row for `preview.suggest`, a short "Folder suggestions" section, SDK table row)
- Modify: `plugins-sdk/python/c4k.py`, `plugins-sdk/node/c4k.js`, `plugins-sdk/powershell/c4k.ps1` (helper), then copy each into every example and `plugins-catalog/*` folder that carries it
- Modify: `examples/plugins/folder-stats-python/plugin.json`, `examples/plugins/folder-stats-python/main.py`
- Run afterwards: `pwsh ./plugins-catalog/Update-Catalog.ps1` (catalog copies of the SDK changed)
- Test: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Consumes: Task 1 types, `FolderSuggestionRules.Match/MessageFor`.
- Produces: `ExternalPreviewPlugin.SuggestAsync`. It returns `null` when `preview.Suggest` is null or `Match` fails. With `Ask == false` it returns `new FolderSuggestion(0.7, MessageFor(...), new FolderSuggestionView(Plugin: manifest.Id, Preview: preview.Id))`. With `Ask == true` it calls `preview.suggest` with `{ preview, folder: EntryDto, entries: [EntryDto…], truncated, context }` and parses `{ score, message, options? }` (or JSON `null` → `null`). The score is clamped to 0..1, and `options` becomes `OptionsJson` (raw text).
- SDK helper: Python `folder_suggestion(score, message, options=None)`, Node `folderSuggestion(score, message, options)`, PowerShell `New-C4KFolderSuggestion $Score $Message [$Options]`. Each returns the result object for `preview.suggest`.
- Example: in `folder-stats-python`, add the preview `{ "id": "tables", "title": "Tabele CSV w folderze", "extensions": ["/"], "priority": -1, "suggest": { "files": [".csv"], "minCount": 3, "minShare": 0.5, "ask": true } }`. Its `preview.render` for a folder returns Markdown listing the CSV files with row counts (read through `c4k.list_dir`/`c4k.read_text`). `preview.suggest` returns `folder_suggestion(0.8, f"{n} tabel CSV — pokazać jako tabele?")`.

- [ ] **Step 1: Write failing tests** in `ExternalPluginTests`:
  - `Folder_suggest_rule_asks_the_plugin`: 3 `.csv` files in `_folder` (plus the existing a.txt, b.txt, c.log is 3/6 = 0.5). Run `SuggestAsync` on the `tables` preview with a snapshot of the folder. Assert score `0.8`, the message contains `"3"`, and `View.Plugin == "examples.folder-stats"`, `View.Preview == "tables"`.
  - `Folder_suggest_rule_mismatch_does_not_start_the_plugin`: a folder with 1 csv returns `null`, and `_audit.Recent("examples.folder-stats")` has no entries (the process never ran).
  - `Folder_suggest_without_ask_uses_manifest_message`: take the `tables` contribution `with { Suggest = rule with { Ask = false, Message = {"en":"{count} tables"} } }` → score `0.7`, message `"3 tables"` (UI language en in tests).
  - `Manifest_reader_reads_suggest`: `ManifestReader` on the example: `Suggest!.MinCount == 3`, `Ask == true`.
  - `Folder_suggest_plugin_error_is_no_suggestion`: the example's `preview.suggest` handler raises for any preview other than `tables`. Build `new ExternalPreviewPlugin(plugin, csvContribution with { Suggest = tablesRule })` and assert `SuggestAsync` returns `null` without throwing.
  - Unapproved or disabled plugins never take part: `AppHost.PreviewPlugins` already holds only plugins with `PluginBlocker(p) is null`, so no test is needed; the service only sees `previews()`.
- [ ] **Step 2: Run** `dotnet test --solution Commander4K.sln -- --filter-method "*Folder_suggest*"`. Expected: FAIL.
- [ ] **Step 3: Implement** the adapter, the example and the SDK helpers. Copy the SDK files into every example and catalog copy. Update the docs, the schema and en.json for any new `Loc.T`, then run `Update-Catalog.ps1`. Plugin errors (`JsonRpc` error, bad JSON) become `null` inside `SuggestAsync`. Cancellation propagates.
- [ ] **Step 4: Run** the full suite: `dotnet test --solution Commander4K.sln`. Expected: PASS, including the SDK-copy identity test, `PluginSourceCheckTests` (examples clean), `PluginCatalogTests` and `LocalizationTests`.
- [ ] **Step 5: Commit** `feat: external previews suggest folders (preview.suggest)`.

### Task 3: Built-in gallery and notes suggestions

**Files:**
- Create: `src/Commander4K.Plugins.BuiltIn/Previews/FolderSuggestions.cs`
- Modify: `src/Commander4K.Plugins.BuiltIn/Previews/FilePreviewPlugins.cs` (`FolderPreviewPlugin : IPreviewCardPlugin, IFolderSuggestingPreview`)
- Test: `tests/Commander4K.Plugins.Tests/FolderSuggestionTests.cs`

**Interfaces:**
- Consumes: Task 1 types; `FolderViews.IsImage(VfsEntry)` (internal, same assembly); `NotesOverview.IsMarkdown`. Make it `internal` if it is private.
- Produces: `static FolderSuggestion? FolderSuggestions.Gallery(FolderSnapshot)`, `static FolderSuggestion? FolderSuggestions.Notes(FolderSnapshot)`. `FolderPreviewPlugin.SuggestAsync` returns gallery, otherwise notes, otherwise `null` (no host I/O). Views: `new FolderSuggestionView(View: FolderViewIds.Gallery)` / `FolderViewIds.Notes`. Score `0.7`.
- Messages (exact copy): gallery `Loc.P(n, "Ten folder zawiera {0} zdjęcie. Przełączyć w widok galerii?|Ten folder zawiera {0} zdjęcia. Przełączyć w widok galerii?|Ten folder zawiera {0} zdjęć. Przełączyć w widok galerii?")`. Notes: vault `Loc.T("To jest vault Obsidiana. Pokazać jako notatki z powiązaniami?")`, Markdown `Loc.P(n, "Ten folder zawiera {0} notatkę Markdown. Pokazać jako notatki?|Ten folder zawiera {0} notatki Markdown. Pokazać jako notatki?|Ten folder zawiera {0} notatek Markdown. Pokazać jako notatki?")`.

- [ ] **Step 1: Write failing tests:** `Gallery_needs_five_images_and_sixty_percent` (5 png + 3 txt → suggestion; 4 png → null; 5 png + 4 txt → null); `Gallery_message_counts_images` (contains `"5"`); `Notes_from_obsidian_folder` (directory `.obsidian` + 1 md → Notes view); `Notes_need_five_markdown_files` (4 md → null; 5 md + 3 txt → Notes); `Folder_preview_prefers_gallery_then_notes` via `FolderPreviewPlugin.SuggestAsync` (8 png + 2 md → `View.View == "gallery"`; 5 png + 5 md + directory `.obsidian` → gallery fails at 50 %, so `"notes"`).
- [ ] **Step 2: Run** `--filter-class "*FolderSuggestionTests"`. Expected: FAIL.
- [ ] **Step 3: Implement**; add the en.json entries for the messages (plural forms `one|other` per en.json convention).
- [ ] **Step 4: Run** the filter, plus `*LocalizationTests*`. Expected: PASS.
- [ ] **Step 5: Commit** `feat: gallery and notes suggest themselves for folders`.

### Task 4: `FolderSuggestionService` and `FolderSuggestionMemory`

**Files:**
- Create: `src/Commander4K.Core/Folders/FolderSuggestionService.cs`
- Create: `src/Commander4K.Core/Folders/FolderSuggestionMemory.cs`
- Test: `tests/Commander4K.Core.Tests/FolderSuggestionServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces:
  ```csharp
  public sealed record RankedSuggestion(VfsEntry Folder, FolderSuggestion Suggestion, IPreviewPlugin Source);

  public sealed class FolderSuggestionService(
      Func<IReadOnlyList<IPreviewPlugin>> previews,
      Func<IPreviewPlugin, IPluginHost> hostFor,
      Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> list,
      FolderSuggestionMemory memory)
  {
      public static bool IsEligible(VfsEntry entry);   // Directory (not ParentLink), file/smb, no "://"
      public IAsyncEnumerable<RankedSuggestion> SuggestAsync(VfsEntry folder, CancellationToken cancellationToken);
  }

  public sealed class FolderSuggestionMemory(string? filePath)  // null = in memory only (tests)
  {
      public bool IsMuted(string folder);          // declined or muted this session; case-insensitive path
      public void MuteForSession(string folder);
      public Task DeclineAsync(string folder);     // persists
      public Task ClearAsync();
      public bool ShouldShowTour { get; }          // tourDismissals < 3 && !tourUsed
      public Task RecordTourDismissedAsync();
      public Task RecordTourUsedAsync();
      public static Task<FolderSuggestionMemory> LoadAsync(string filePath, CancellationToken cancellationToken);
  }
  ```
- `SuggestAsync` behaviour:
  - It yields nothing when the folder is not eligible, is muted, or `previews()` contains no `IFolderSuggestingPreview`.
  - It reads ≤ 500 entries, then `Truncated = true` and stops reading. It yields nothing if the snapshot contains `c4k.dir` (`FolderConfig.FileName`, case-insensitive).
  - Candidates run in parallel. Built-ins, plus external previews whose rule has `Ask == false`, share the 1.5 s budget, which includes the listing. Each `Ask == true` call gets its own 5 s limit.
  - It yields every time the best result improves: score ≥ 0.5 → higher score → external before built-in (`Source is not` a `builtin.*` manifest id) → higher `Priority`.
  - Exceptions and timeouts per candidate are swallowed. External cancellation ends the enumeration quietly (no throw out of `SuggestAsync`; the consumer sees just the end).
  - Cache: the result per `(Path, Modified)` is kept in memory (LRU 256), including "none", and replayed without listing.
- JSON file `{ "declined": [paths], "tourDismissals": n, "tourUsed": bool }` via source-gen context (`init => field = value ?? …` defaults). The write runs off the UI thread; IO errors are swallowed and logged nowhere (it is preference data).

- [ ] **Step 1: Write failing tests** (fake previews implementing `IFolderSuggestingPreview` with configurable delay/score/throw; `list` from an in-memory array):
  - `Highest_score_wins_and_low_scores_are_ignored` (0.4 and 0.7 → one result 0.7);
  - `Tie_prefers_external_then_priority`;
  - `Folder_with_c4k_dir_yields_nothing`;
  - `Muted_and_declined_folders_yield_nothing` (also after `LoadAsync` from a temp file written by `DeclineAsync`);
  - `Throwing_candidate_does_not_hide_others`;
  - `Hanging_listing_ends_within_budget` (listing that awaits forever → enumeration completes in < 2.5 s with no items);
  - `Slow_ask_candidate_arrives_later_and_replaces` (built-in 0.7 at once, then an ask-candidate 0.8 after 2 s → two items in order);
  - `Cancelled_token_yields_nothing_and_does_not_throw`;
  - `Listing_is_capped_at_500_and_marked_truncated` (fake sees `Entries.Count == 500`, `Truncated`);
  - `Second_call_uses_cache` (the listing counter stays 1);
  - `Ineligible_entries` (`sftp://h/x`, `zip://…::/`, ParentLink, a file → nothing);
  - `Tour_shows_until_three_dismissals_or_use`.
- [ ] **Step 2: Run** `--filter-class "*FolderSuggestionServiceTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Merge the candidates with a `Channel<FolderSuggestion?>` fed by one task per candidate, each wrapped in a try/catch. Budgets use linked `CancellationTokenSource.CancelAfter`.
- [ ] **Step 4: Run** the filter. Expected: PASS. Build clean.
- [ ] **Step 5: Commit** `feat: folder suggestion service`.

### Task 5: Writing `c4k.dir` from a suggestion (shared with `cm_FolderView`)

**Files:**
- Modify: `src/Commander4K.Core/Folders/FolderConfigWriter.cs`
- Modify: `src/Commander4K.App/MainWindow.cs` (`ConfigureFolderViewAsync` uses the new helper)
- Test: `tests/Commander4K.Core.Tests/FolderConfigWriterTests.cs`

**Interfaces:**
- Produces:
  - `static string FolderConfigWriter.CreateForAction(JsonNode? action, string where)`: `where` is `"both" | "preview" | "enter"`. It produces exactly today's output: `$schema` = `FolderConfig.SchemaUrl`, `preview` and/or `enter`, indented, relaxed escaping, trailing `"\n"`.
  - `static JsonNode FolderConfigWriter.ActionFor(FolderSuggestionView view)`: `View` gives a `JsonValue` string. `Plugin` gives `{ "plugin": id, "preview"?: p, "options"?: parsed OptionsJson }`.
- [ ] **Step 1: Write failing tests:** `CreateForAction_both_writes_preview_and_enter` (parse back with `FolderConfig.Parse(folder, json)` → `Enter` and `Preview` are the gallery view); `ActionFor_plugin_view_round_trips_options` (the options object survives and `FolderConfig.Parse` sees plugin + preview + options).
- [ ] **Step 2: Run** `--filter-class "*FolderConfigWriterTests"`. Expected: FAIL.
- [ ] **Step 3: Implement**, and replace the hand-built JSON in `ConfigureFolderViewAsync` (lines ~1065–1083) with `CreateForAction(action, where)`. The behaviour stays unchanged.
- [ ] **Step 4: Run** the filter plus `*FolderConfigTests*`. Expected: PASS.
- [ ] **Step 5: Commit** `refactor: shared c4k.dir writer for folder actions`.

### Task 6: App: hint card, commands, setting, accept flow

**Files:**
- Create: `src/Commander4K.App/Panels/FolderSuggestionCard.cs`
- Modify: `src/Commander4K.App/Panels/FilePanelView.cs` (host the card as an overlay over the list, bottom-right corner, `IsHitTestVisible` only on the card, never `Focusable`)
- Modify: `src/Commander4K.App/MainWindow.cs` (wiring, commands)
- Modify: `src/Commander4K.App/Composition/AppHost.cs` (create `FolderSuggestionMemory` from `data/folder-suggestions.json` at start-up, expose `FolderSuggestions` service; `list` = router `OpenListing(path, ct).ReadAllAsync(ct)`)
- Modify: `src/Commander4K.Core/Configuration/AppSettings.cs` (`public bool FolderSuggestions { get; set; } = true;`), `src/Commander4K.Core/Configuration/SettingsCatalog.cs`, `src/Commander4K.Core/Input/CommandIds.cs`, `src/Commander4K.Core/Input/DefaultKeymap.cs` (`["Ctrl+Enter"] = CommandIds.AcceptFolderSuggestion`), en.json

**Interfaces:**
- Consumes: Task 4 service/memory, Task 5 writer, existing `RunFolderActionAsync(panel, folder, config, action)`, `FolderConfig.Parse`, `Notify`.
- Produces:
  - `CommandIds.AcceptFolderSuggestion = "cm_AcceptFolderSuggestion"` and `CommandIds.ResetFolderSuggestions = "cm_ResetFolderSuggestions"`.
  - `FolderSuggestionCard(SkinBrushes brushes, RankedSuggestion suggestion)` with single-argument events `Accepted`, `Postponed`, `Declined` (`Action<RankedSuggestion>`). Each event carries a `RankedSuggestion`, so the source generator stays happy.
  - `FilePanelView.ShowSuggestion(FolderSuggestionCard?)` (null hides it).
- Setting `folderSuggestions`, category Appearance or the one with folder views, title `Loc.T("Podpowiedzi widoku folderu")`, description `Loc.T("Gdy kursor zatrzyma się na folderze, którego zawartość pasuje do podglądu (np. zdjęcia → galeria), pokazuje propozycję przełączenia. Akceptacja zapisuje c4k.dir.")`, keywords `["podpowiedzi", "sugestie", "galeria", "widok folderu", "suggestions", "gallery", "c4k.dir"]`.
- Card copy: buttons `Loc.T("Przełącz")`, `Loc.T("Nie teraz")`, `Loc.T("Nie pytaj o ten folder")`, plus the key hint `Loc.T("Ctrl+Enter")` next to "Przełącz". Command descriptions: `Loc.T("Przyjmij podpowiedź widoku folderu")`, `Loc.T("Wyczyść odrzucone podpowiedzi widoku folderu")`.

Wiring in MainWindow:
- The active panel's active tab `Changed` and `ActiveTabChanged` (re)start a 700 ms `DispatcherTimer`. Any change hides the card and cancels the previous `CancellationTokenSource` first.
- On tick, with the setting on, `_active.Mode is null` and `FolderSuggestionService.IsEligible(entry)`, run `await foreach` over `SuggestAsync` off the UI thread and post each item to the UI. Post it only if the cursor is still on that entry; otherwise drop it.
- Accept (button or Ctrl+Enter while a card is shown):
  1. Write `CreateForAction(ActionFor(view), "both")` to `FolderConfig.ChildPath(folder, FolderConfig.FileName)` in `Task.Run`, catching `IOException`/`UnauthorizedAccessException`.
  2. On success, `Notify(Loc.T("Zapisano {0} — Enter na folderze go użyje, Shift+Enter pokazuje pliki, F4 edytuje.", file))`. On failure, `Notify(ex.Message, isError: true)`.
  3. Either way, build `FolderConfig.Parse(folder, json)` from the same JSON and call `RunFolderActionAsync(_active, entry, config, config.Enter!)`.
  4. Then hand over to Task 7's spotlight (Task 6 leaves a call `ShowBackToFilesTour(panel)` as a no-op stub).
- Postpone calls `memory.MuteForSession`. Decline calls `memory.DeclineAsync`, then `Notify(Loc.T("Nie będę proponować widoku dla tego folderu. Cofniesz to poleceniem „Wyczyść odrzucone podpowiedzi”."))`.
- `cm_ResetFolderSuggestions` calls `memory.ClearAsync`, then `Notify(Loc.T("Wyczyszczono odrzucone podpowiedzi."))`.

- [ ] **Step 1:** Add the setting, commands, binding and en.json entries. Run `dotnet test --solution Commander4K.sln -- --filter-class "*LocalizationTests"` and `*KeymapTests*`. Expected: PASS (and FAIL first if en.json is missing a key, which proves the scan sees them).
- [ ] **Step 2:** Implement the card, the overlay and the wiring. Build clean.
- [ ] **Step 3: Headless check** (scratch console per CLAUDE.md "UI verification without a desktop session"):
  1. Make a temp folder `photos` with 6 PNGs and point the left panel at its parent.
  2. Put the cursor on `photos`, pump `RunJobs` for 1 s, and assert the card is visible. Save `CaptureRenderedFrame()` as a PNG in the scratchpad and look at it in a modern skin and a retro skin.
  3. Press Ctrl+Enter. Assert `photos/c4k.dir` exists with `"enter": "gallery"` and the left panel's mode is a `PreviewPanelView`.
  4. Make the temp folder read-only, repeat and assert the error notice plus the open gallery.
- [ ] **Step 4:** Full suite `dotnet test --solution Commander4K.sln`. Expected: PASS.
- [ ] **Step 5: Commit** `feat: folder view suggestion card in panels`.

### Task 7: Spotlight "tu wrócisz do listy plików"

**Files:**
- Create: `src/Commander4K.App/Tour/SpotlightOverlay.cs`
- Modify: `src/Commander4K.App/Preview/PreviewPanelView.cs:94` (keep a reference: `public Control FilesButton { get; }`)
- Modify: `src/Commander4K.App/MainWindow.cs` (`ShowBackToFilesTour` from Task 6; the overlay sits in the window's root layer above everything)

**Interfaces:**
- Consumes: Task 4 `FolderSuggestionMemory.ShouldShowTour / RecordTourDismissedAsync / RecordTourUsedAsync`; Task 6 stub.
- Produces: `SpotlightOverlay(SkinBrushes brushes, Control target, string text, string buttonText)` with the event `Closed` (`Action<SpotlightOverlay>`). It draws in `Render`: a full-window fill of the skin background at ~60 % opacity, a rounded cut-out around `target.TranslatePoint` bounds + 6 px with a `focusRing` border, and a bubble (surface colour, border, arrow toward the target) below or above the target, whichever fits. It is the only focusable element while shown.
- Copy: text `Loc.T("Tu (albo Esc) wrócisz do zwykłej listy plików. Shift+Enter wchodzi do folderu bez widoku.")`, button `Loc.T("OK")` (reuse it if the key exists).
- Behaviour:
  - It closes on OK, Esc, Enter or a click anywhere. Esc and Enter are handled (`e.Handled = true`), so the first Esc does not leave the view; other keys close it and pass on.
  - It closes when the panel's mode changes.
  - Closing calls `RecordTourDismissedAsync`. If the user then leaves the pinned view with the Files button or Esc (`CloseRequested` on that `PreviewPanelView`), call `RecordTourUsedAsync`.
  - It is shown only if `ShouldShowTour`, after the view has laid out (post at `DispatcherPriority.Loaded`).

- [ ] **Step 1:** Implement the overlay and hook it up. Build clean, en.json updated, `*LocalizationTests*` PASS.
- [ ] **Step 2: Headless check:** continue the Task 6 harness after Ctrl+Enter. Pump, capture a PNG, confirm the cut-out frames "📄 Pliki (Esc)" in a modern and a retro skin. Press Esc: the overlay is gone and the mode is still the preview. Press Esc again: back to files. After 3 dismissals `ShouldShowTour` is false.
- [ ] **Step 3: Real window check** (`dotnet run --project src/Commander4K.App`): the hint, accept, spotlight and Esc twice on a real photo folder. The user verifies the look.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5:** Add a CLAUDE.md paragraph for "Folder suggestions" covering `IFolderSuggestingPreview`, the `suggest` rule + `preview.suggest`, `FolderSuggestionService` budgets, `data/folder-suggestions.json`, `SpotlightOverlay` and `cm_AcceptFolderSuggestion`. Commit `feat: spotlight back-to-files tour after switching a folder view`.
