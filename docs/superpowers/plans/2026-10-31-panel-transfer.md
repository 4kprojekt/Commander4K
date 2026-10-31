# F5 między panelem plików a panelem wtyczki — plan wdrożenia

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** F5 wysyła zaznaczone pliki do panelu wtyczki po drugiej stronie (odbiorcy), a z panelu wtyczki zapisuje jej elementy jako pliki w panelu plików (wydawcy); F5 nigdy nie kopiuje do folderu ukrytego pod panelem wtyczki/przeglądarką.

**Architecture:** Kontrakty w PluginSdk (`PanelContribution.Accepts/Provides`, rekordy przesyłu, domyślne metody `IPanelSession`). Core: sprawdzanie dopasowania odbiorcy po stronie hosta, czasowa zgoda odczytu w `ScopedPluginHost`, cztery nowe wywołania protokołu + powiadomienie `job.progress`, `net.request` z treścią z pliku, dwa zadania (`PanelReceiveJob`, `PanelProvideJob`). App: routing F5/F6, okna wyboru, przepuszczanie F5 w panelu z `provides`.

**Tech Stack:** .NET 10, Avalonia + Avalonia.Markup.Declarative (C# only), xunit.v3 (Microsoft.Testing.Platform), JSON-RPC 2.0 NDJSON, SDK w Pythonie/Node/PowerShell.

**Spec:** `docs/superpowers/specs/2026-10-31-panel-transfer-design.md`

## Global Constraints

- `dotnet build Commander4K.sln` bez ostrzeżeń (`TreatWarningsAsErrors`).
- Testy: `dotnet test --solution Commander4K.sln`; pojedynczy: `dotnet test --solution Commander4K.sln -- --filter-method "*Nazwa*"`.
- Każdy tekst widoczny dla użytkownika przez `Loc.T("polski tekst")` / `Loc.P(...)`, z wpisem w `src/Commander4K.Core/Localization/i18n/en.json` (`LocalizationTests`). Wyjątki deweloperskie i wpisy audytu po angielsku, bez `Loc`.
- Nowa metoda protokołu: `IPluginHost`/`ScopedPluginHost` (gated + audyt), `ExternalPlugin.HandleRequestAsync`, wszystkie trzy klienty SDK (`plugins-sdk/{python,node,powershell}`), potem skopiowane do każdego przykładu w `examples/plugins/*` i do wtyczek w `plugins-catalog/*/` (test pilnuje identyczności), tabela SDK w `docs/plugins.md`. Po zmianie plików wtyczki katalogowej: `pwsh ./plugins-catalog/Update-Catalog.ps1`.
- Rekordy JSON z generatorem źródeł: domyślne wartości przez `init => field = value ?? …` (generator podaje `null` dla brakujących kluczy).
- App: jeden plik na widok/okno, zdarzenia jednoparametrowe, żadnego I/O na wątku UI, kolory tylko z `SkinBrushes`.
- Oczekiwane wyjątki (anulowanie, brak dostępu, timeout) łapane w naszym kodzie; nie mogą wylecieć z lambdy `Task.Run`.
- Limit treści inline w `panel.provide`: **16 MB na plik** (po zdekodowaniu). Limit odpowiedzi na `panel.receivers`/`panel.providers`: **5 s**, potem komunikat. `panel.receive`/`panel.provide`: bez limitu czasu (tylko anulowanie).

## Review Focus

1. Zaznaczenie mieszane (pliki + folder) do odbiorcy bez `folders: true` → odbiorca odfiltrowany, komunikat „Żaden odbiorca nie przyjmuje folderów”, nic nie wysłane. (Task 1, test `Folder_selection_needs_folder_receiver`.)
2. Wtyczka czyta przyznany plik po anulowaniu zadania (Esc w trakcie) → odmowa; zgoda wygasa także przy wyjątku. (Task 2, test `Grant_ends_when_scope_is_disposed_after_failure`.)
3. Wtyczka-wydawca zwraca dwa pliki o tej samej nazwie albo nazwę różniącą się tylko wielkością liter → drugi traktowany wg polityki nadpisywania, nie rzuca. (Task 4, test `Duplicate_provided_names_follow_overwrite_policy`.)
4. Wtyczka nie odpowiada na `panel.receivers` (zawiesza się) → po 5 s komunikat, UI nie zamarza, F5 można powtórzyć. (Task 3, test `Receivers_call_times_out_after_five_seconds`.)
5. Ścieżka przyznana różni się od żądanej tylko wielkością liter lub separatorem (`C:\X\a.txt` vs `c:/x/a.txt`) → dozwolone; prefiks nazwy (`C:\X\a.txt.bak`, `C:\Xy\…` przy przyznanym folderze `C:\X`) → odmowa. (Task 2, test `Grant_matches_normalized_paths_not_name_prefixes`.)

---

### Task 1: Kontrakty SDK i dopasowanie odbiorców

**Files:**
- Modify: `src/Commander4K.PluginSdk/PluginManifest.cs` (`PanelContribution`)
- Modify: `src/Commander4K.PluginSdk/IPanelPlugin.cs` (`IPanelSession` + nowe rekordy)
- Create: `src/Commander4K.Core/Plugins/TransferMatching.cs`
- Modify: `docs/plugin.schema.json` (`accepts`, `provides` w panelu)
- Test: `tests/Commander4K.Core.Tests/TransferMatchingTests.cs`

**Interfaces:**
- Produces (PluginSdk):
  - `PanelContribution(..., bool Accepts = false, bool Provides = false)` — dopisane na końcu listy parametrów.
  - `public enum TransferItemKind : byte { File, Folder }`
  - `public sealed record TransferItem(string Path, string Name, TransferItemKind Kind, long Size);`
  - `public sealed record PanelReceiver(string Id, string Title, string? Description = null, IReadOnlyList<string>? Extensions = null, bool Multiple = false, bool Folders = false, bool ReturnsMessage = false);`
  - `public sealed record PanelProvider(string Id, string Title, string? Description = null);`
  - `public sealed record TransferItemResult(string Path, bool Ok, string? Error = null);`
  - `public sealed record ReceiveResult(string? Text, IReadOnlyList<TransferItemResult>? Results = null);`
  - `public sealed record ProvidedFile(string Name, string? Text = null, byte[]? Data = null, Uri? Url = null);`
  - `public sealed record ProvideResult(IReadOnlyList<ProvidedFile> Files, string? Text = null);`
  - W `IPanelSession` (domyślne implementacje = „brak”):
    - `bool CanReceive => false;` `bool CanProvide => false;`
    - `Task<IReadOnlyList<PanelReceiver>> GetReceiversAsync(IReadOnlyList<TransferItem> items, CancellationToken ct) => Task.FromResult<IReadOnlyList<PanelReceiver>>([]);`
    - `Task<ReceiveResult> ReceiveAsync(string receiverId, IReadOnlyList<TransferItem> items, IProgress<TransferProgress>? progress, CancellationToken ct) => throw new NotSupportedException();`
    - `Task<IReadOnlyList<PanelProvider>> GetProvidersAsync(IReadOnlyList<string> rowIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<PanelProvider>>([]);`
    - `Task<ProvideResult> ProvideAsync(string providerId, IReadOnlyList<string> rowIds, IProgress<TransferProgress>? progress, CancellationToken ct) => throw new NotSupportedException();`
  - `public readonly record struct TransferProgress(int Done, int Total, long Bytes = 0, string? Text = null);`
- Produces (Core): `public static class TransferMatching` z
  - `static bool Fits(PanelReceiver receiver, IReadOnlyList<TransferItem> items)`
  - `static string? WhyNone(IReadOnlyList<PanelReceiver> offered, IReadOnlyList<TransferItem> items)` — zwraca zlokalizowany powód, gdy żaden nie pasuje (null, gdy któryś pasuje).

- [ ] **Step 1: Napisz testy** w `TransferMatchingTests`:
  - `Multiple_false_rejects_two_files`: `Fits(new("r","R"), [file("a.txt"), file("b.txt")])` → `false`; z jednym plikiem → `true`.
  - `Extensions_use_preview_patterns`: odbiorca `Extensions: [".json", "openapi.*"]`, pliki `a.JSON` → true, `openapi.yaml` → true, `a.txt` → false (dopasowanie przez `ExternalPreviewPlugin.Matches`, każdy plik musi pasować).
  - `Folder_selection_needs_folder_receiver`: `[file("a.txt"), folder("src")]` do `new("r","R", Multiple: true)` → false; z `Folders: true` → true; `WhyNone([odbiorca bez folderów], items)` == `Loc.T("Żaden odbiorca nie przyjmuje folderów.")`.
  - `WhyNone_explains_single_file_limit`: dwa pliki, jedyny odbiorca bez `Multiple` → `Loc.T("Odbiorca „{0}” przyjmuje tylko jeden plik.", "R")`; brak odbiorców → `Loc.T("Wtyczka nie podała żadnego odbiorcy dla tych plików.")`; niepasujące typy → `Loc.T("Żaden odbiorca nie przyjmuje plików tego typu.")`.
- [ ] **Step 2:** `dotnet test --solution Commander4K.sln -- --filter-class "*TransferMatchingTests"` → FAIL (brak typów).
- [ ] **Step 3:** Dodaj rekordy i domyślne metody w SDK, `TransferMatching` w Core, pola `accepts`/`provides` (boolean, opis po angielsku) w `docs/plugin.schema.json`. Kolejność powodów w `WhyNone`: brak odbiorców → foldery → liczba → typy. Teksty do `en.json`.
- [ ] **Step 4:** Ten sam filtr + `*LocalizationTests` → PASS; `dotnet build Commander4K.sln` bez ostrzeżeń.
- [ ] **Step 5:** Commit `Add panel transfer contracts and host-side receiver matching`.

### Task 2: Czasowa zgoda odczytu w `ScopedPluginHost`

**Files:**
- Modify: `src/Commander4K.Core/Plugins/ScopedPluginHost.cs` (`Gate`, nowa metoda)
- Create: `src/Commander4K.Core/Plugins/ReadGrants.cs`
- Test: `tests/Commander4K.Plugins.Tests/ReadGrantTests.cs`

**Interfaces:**
- Produces: `public IDisposable GrantRead(IReadOnlyList<TransferItem> items)` na `ScopedPluginHost` (nie na `IPluginHost` — to nie jest metoda protokołu). Zgoda: plik = dokładnie ta ścieżka; folder = ta ścieżka i wszystko pod nią. Kilka zgód naraz się sumuje (licznik per wpis). Audyt: `audit.Record(id, "fs.grant", "<n> paths", AuditOutcome.Info)` przy nadaniu i `"fs.grant.end"` przy zakończeniu.
- `Gate`: gdy `permission == PluginPermissions.FileRead` i manifest nie ma `fs.read`, ale `ReadGrants.Covers(detail)` → wykonaj i zapisz w audycie jako `Allowed` z dopiskiem `(granted)` w `detail`.
- `ReadGrants` normalizuje ścieżki przez `VfsPath` (separator, wielkość liter dla `file`/`smb`; dla `sftp/ftp/zip` porównanie dokładne poza końcowym `/`) i dopasowuje folder tylko na granicy segmentu.

- [ ] **Step 1: Napisz testy** (wzoruj się na konstrukcji hosta w `ExternalPluginTests`: `new ScopedPluginHost(manifest bez fs.read, ui, router, http, audit, …)`):
  - `Granted_file_is_readable_neighbour_is_not`: grant `[file a.txt]` → `OpenReadAsync(a.txt)` OK, `OpenReadAsync(b.txt)` → `PluginPermissionException`.
  - `Granted_folder_covers_descendants`: grant `[folder X]` → `ListAsync(X)`, `OpenReadAsync(X\sub\c.txt)` OK.
  - `Grant_matches_normalized_paths_not_name_prefixes`: patrz Review Focus 5.
  - `Grant_ends_when_scope_is_disposed_after_failure`: `using (host.GrantRead(...)) { throw }` w `Assert.Throws`, potem odczyt → odmowa.
  - `Overlapping_grants_count`: dwa granty na ten sam plik, zwolnienie jednego → nadal czytelny; obu → nie.
  - `Grants_are_audited`: w `_audit.Recent(id)` są `fs.grant`, `fs.read` z `(granted)`, `fs.grant.end`.
- [ ] **Step 2:** `--filter-class "*ReadGrantTests"` → FAIL.
- [ ] **Step 3:** Implementacja (`ReadGrants` thread-safe: `lock` + słownik ścieżka→licznik; `GrantRead` zwraca obiekt zwalniający raz).
- [ ] **Step 4:** Filtr → PASS; cały `Commander4K.Plugins.Tests` → PASS.
- [ ] **Step 5:** Commit `Add temporary read grants for files sent to a plugin`.

### Task 3: Protokół — `panel.receivers/receive/providers/provide`, `job.progress`, `net.request` z pliku

**Files:**
- Modify: `src/Commander4K.Core/Plugins/ExternalPluginAdapters.cs` (`ExternalPanelPlugin.Session`)
- Modify: `src/Commander4K.Core/Plugins/ExternalPlugin.cs` (rejestr postępu, `job.progress`, `net.request` z `bodyFile`/`multipart`)
- Modify: `src/Commander4K.PluginSdk/IPluginHost.cs` (`HttpRequestSpec` + `HttpFilePart`)
- Modify: `src/Commander4K.Core/Plugins/ScopedPluginHost.cs` (`HttpRequestAsync` strumieniuje plik)
- Modify: `plugins-sdk/python/c4k.py`, `plugins-sdk/node/c4k.js`, `plugins-sdk/powershell/c4k.ps1` + kopie w `examples/plugins/*` i `plugins-catalog/*/`
- Modify: `docs/plugins.md` (tabela SDK, sekcja „Przyjmowanie i wydawanie plików”, F5 vs akcja F5)
- Test: `tests/Commander4K.Plugins.Tests/PanelTransferProtocolTests.cs` (fake plugin w Pythonie w folderze tymczasowym, wzór: `ExternalPluginTests.Start`)

**Interfaces:**
- Consumes: Task 1 rekordy; Task 2 `GrantRead`.
- Produces:
  - `Session.CanReceive => panel.Accepts`, `CanProvide => panel.Provides`.
  - `GetReceiversAsync` → `CallAsync("panel.receivers", { panel, files, context }, ct, timeout: TimeSpan.FromSeconds(5))`; `files` = `[{ path, name, kind: "file"|"folder", size }]`; wynik: tablica `{ id, title, description?, extensions?, multiple?, folders?, returnsMessage? }`.
  - `ReceiveAsync` → otwiera `((ScopedPluginHost)host).GrantRead(items)`, rejestruje `jobId` (GUID „N”) → `progress` w `ExternalPlugin.RegisterProgress(string jobId, IProgress<TransferProgress>) : IDisposable`, wywołuje `panel.receive { panel, receiver, jobId, files, context }` z `timeout: Timeout.InfiniteTimeSpan`; wynik `{ text?, results?: [{ path, ok, error? }] }`.
  - `GetProvidersAsync` → `panel.providers { panel, rowIds, context }` (5 s); wynik `[{ id, title, description? }]`.
  - `ProvideAsync` → `panel.provide { panel, provider, jobId, rowIds, context }` (bez limitu); wynik `{ files: [{ name, text? | base64? | url? }], text? }` → `ProvidedFile` (`base64` → `Data`, `url` → `Uri`, inne schematy niż http/https odrzucone `FormatException`).
  - Powiadomienie wtyczka→host `job.progress { jobId, done, total, bytes?, text? }` obsłużone w `HandleRequestAsync` (przychodzi przez `SafeHandleAsync`); nieznany `jobId` ignorowany.
  - `HttpRequestSpec(..., string? BodyFile = null, HttpFilePart? Part = null)`; `public sealed record HttpFilePart(string Field, string Path, string? FileName = null);`. `ScopedPluginHost.HttpRequestAsync`: gdy `BodyFile`/`Part` ustawione — najpierw `Gate(FileRead, "fs.read", path, …)` (działa z grantem), potem `StreamContent` z `router.OpenReadAsync` (multipart: `MultipartFormDataContent`). `Body` i `BodyFile`/`Part` naraz → `ArgumentException`.
  - `net.request` w `ExternalPlugin`: nowe opcjonalne pola `bodyFile`, `multipart: { field, path, fileName? }`.
  - SDK Python: `@handler("panel.receive")` itd. jak dziś; nowe `report_progress(job_id, done, total, bytes=0, text=None)` (powiadomienie bez `id`), `http_request(..., body_file=None, multipart=None)`. Node: `reportProgress(jobId, done, total, {bytes, text})`, `httpRequest(..., {bodyFile, multipart})`. PowerShell: `Send-C4kProgress -JobId -Done -Total [-Bytes] [-Text]`, `Invoke-C4kHttp ... -BodyFile -MultipartField -MultipartPath`. Nazwy dopasuj do istniejącej konwencji w danym pliku, jeśli odbiega.

- [ ] **Step 1: Napisz testy** (fake wtyczka Pythonowa z `accepts`/`provides`, permisje tylko `net`):
  - `Receivers_are_parsed_and_receive_reads_granted_file`: `GetReceiversAsync` zwraca 2 odbiorców z polami; `ReceiveAsync("r1", [a.txt])` — wtyczka czyta `a.txt` przez `read_bytes` i zwraca `{text: "got 100"}` → `Text == "got 100"`; wtyczka próbująca czytać `b.txt` dostaje odmowę (wynik `results[0].ok == false`).
  - `Progress_notifications_reach_the_job`: wtyczka wysyła 2× `report_progress`; zebrane `TransferProgress` == `[(1,2), (2,2)]`.
  - `Receive_is_cancelled_not_timed_out`: wtyczka nigdy nie odpowiada na `panel.receive`; anuluj token po 200 ms → `OperationCanceledException` (nie `TimeoutException`), a po nim odczyt `a.txt` przez hosta wtyczki → odmowa (grant zwolniony).
  - `Receivers_call_times_out_after_five_seconds`: wtyczka nie odpowiada na `panel.receivers` → `TimeoutException` po ~5 s (asercja < 8 s).
  - `Provide_parses_text_base64_and_url`: trzy pliki → `ProvidedFile` z odpowiednio `Text`, `Data`, `Url`; `url: "file:///c:/x"` → `FormatException`.
  - `Http_body_file_streams_granted_file`: lokalny `HttpListener` na wolnym porcie odbiera POST; wtyczka w `panel.receive` woła `http_request(url, "POST", body_file=path)`; serwer dostał 100 bajtów; bez grantu i bez `fs.read` → odmowa.
- [ ] **Step 2:** `--filter-class "*PanelTransferProtocolTests"` → FAIL.
- [ ] **Step 3:** Implementacja w Core + trzy SDK; skopiuj SDK do `examples/plugins/*/` i `plugins-catalog/*/`; `pwsh ./plugins-catalog/Update-Catalog.ps1`; dopisz `docs/plugins.md`.
- [ ] **Step 4:** Filtr → PASS; pełne `dotnet test --solution Commander4K.sln` → PASS (w tym test identyczności kopii SDK, `PluginCatalogTests`, `PluginSourceCheckTests`).
- [ ] **Step 5:** Commit `Add panel transfer protocol, job progress and file bodies for net.request`.

### Task 4: Zadania `PanelReceiveJob` i `PanelProvideJob`

**Files:**
- Create: `src/Commander4K.Core/Jobs/PanelTransferJobs.cs`
- Modify: `src/Commander4K.Core/Plugins/ScopedPluginHost.cs` (`OpenHttpReadAsync`)
- Test: `tests/Commander4K.Core.Tests/PanelTransferJobTests.cs` (fake `IPanelSession`, `FakeVfsProvider`)

**Interfaces:**
- Consumes: Task 1 rekordy, `IPanelSession` (z Task 1, implementacja w Task 3).
- Produces:
  - `public sealed class PanelReceiveJob(IPanelSession session, string title, PanelReceiver receiver, IReadOnlyList<TransferItem> items) : IJob` — `Title = Loc.T("Wysyłanie {0} → {1}", opis, receiver.Title)`; `TransferProgress` → `JobMetrics(Bytes, 0, Done, Total, Text)`; po zakończeniu właściwość `ReceiveResult? Result`.
  - `public sealed class PanelProvideJob(IPanelSession session, PanelProvider provider, IReadOnlyList<string> rowIds, VfsRouter router, string targetFolder, OverwritePolicy overwrite, Func<Uri, CancellationToken, Task<Stream>> openUrl) : IJob` — zapis przez `router.OpenWriteAsync`; nazwy przez `PluginAuthoring.IsSafeRelativePath` (podfoldery tworzone `CreateDirectoryAsync`); `Data`/`Text` > 16 MB (UTF-8 dla `Text`) → plik pominięty z błędem; `Url` → `openUrl`; `Skip` + istniejący plik (`router.GetEntryAsync`) → pominięty; właściwości `ProvideResult? Result` i `IReadOnlyList<string> Errors`.
  - `ScopedPluginHost.OpenHttpReadAsync(Uri url, CancellationToken ct) : Task<Stream>` — `Gate(Network, "net.download", url, …)`, tylko http/https, `ResponseHeadersRead`, rzuca przy kodzie ≠ 2xx.

- [ ] **Step 1: Napisz testy:**
  - `Receive_job_maps_progress_and_keeps_result`.
  - `Provide_writes_text_and_binary_into_target`.
  - `Provide_rejects_unsafe_names`: `..\x.txt`, `C:\x.txt`, `a:b.txt` → nic nie zapisane poza folderem, 3 błędy.
  - `Provide_refuses_inline_over_16_MB`: `Data` 16 MB + 1 B → błąd, plik nie istnieje.
  - `Duplicate_provided_names_follow_overwrite_policy` (Review Focus 3): `Skip` → pierwszy zostaje; `Overwrite` → drugi wygrywa; bez wyjątku.
  - `Provide_downloads_url_through_callback`.
- [ ] **Step 2:** `--filter-class "*PanelTransferJobTests"` → FAIL.
- [ ] **Step 3:** Implementacja; teksty do `en.json`.
- [ ] **Step 4:** Filtr + `*LocalizationTests` → PASS.
- [ ] **Step 5:** Commit `Add receive and provide jobs for plugin panels`.

### Task 5: UI — routing F5/F6, okna wyboru, klawisze w panelu wtyczki

**Files:**
- Modify: `src/Commander4K.App/MainWindow.cs` (`TransferAsync`, `ExecuteAsync` — bramka `FileCommands`)
- Modify: `src/Commander4K.App/PluginPanels/PluginPanelView.cs` (F5, publiczny `Session`, `SelectedRowIds`, `ReloadAfterTransfer()`)
- Create: `src/Commander4K.App/Dialogs/PanelTransferDialog.cs`
- Modify: `src/Commander4K.Core/Localization/i18n/en.json`

**Interfaces:**
- Consumes: Task 1 (`TransferMatching`), Task 4 (joby), `AppHost.HostFor` (rzutowanie na `ScopedPluginHost` dla `OpenHttpReadAsync`).
- Produces:
  - `PluginPanelView`: `public IPanelSession Session => _session;`, `public IReadOnlyList<string> SelectedRowIds { get; }` (zaznaczone albo wiersz pod kursorem — tak samo jak dla akcji z `AppliesToRows`), `public void Reload()` (już jest prywatny — upublicznić).
  - `HandleKey`: gdy `_session.CanProvide` i klawisz to `F5` bez akcji wtyczki pod F5 → `return false` (keymapa → `cm_Copy`). Ctrl+R zawsze odświeża.
  - `PanelTransferDialog : DialogWindow<PanelTransferChoice?>` — `public sealed record PanelTransferChoice(string OptionId, string? Target, OverwritePolicy Overwrite);` konstruktor `(SkinBrushes brushes, string title, string description, IReadOnlyList<(string Id, string Title, string? Description)> options, string? target)`; lista widoczna tylko przy >1 opcji (pierwsza zaznaczona), pole celu i „nadpisz” tylko gdy `target` ≠ null (wydanie).
  - `MainWindow.TransferAsync(mode)`:
    1. Aktywny = panel wtyczki: `Copy` + `Session.CanProvide` + drugi panel bez trybu → wydanie (`GetProvidersAsync` w tle, 5 s; brak → `Loc.T("Ta wtyczka nie wydaje plików.")`). `Move` → `Loc.T("Przenoszenie z wtyczek i do wtyczek nie jest obsługiwane.")`.
    2. Drugi panel = `PluginPanelView`: `Move` → ten sam komunikat; `Copy` + `CanReceive` → `GetReceiversAsync` w tle (5 s), filtr `TransferMatching.Fits`, brak → `WhyNone`; wybór w oknie; `RunJob(new PanelReceiveJob(...))`; po zakończeniu: `Result.Text` (jeśli `receiver.ReturnsMessage`) i błędy z `Results` pokazane jak wynik `panel.action` (`Notify`/`ShowText` — użyj tego samego, co dziś pokazuje tekst z akcji), potem `Reload()`. Bez `CanReceive` → `Loc.T("Panel „{0}” nie przyjmuje plików.", title)`.
    3. Drugi panel = `BrowserPanelView` → ten sam komunikat „nie przyjmuje plików”.
    4. Drugi panel = podgląd/przypięty folder albo brak trybu → dzisiejsza ścieżka `TransferDialog` + `VfsTransferJob` bez zmian.
  - Bramka w `ExecuteAsync`: `_active.Mode is PluginPanelView { Session.CanProvide: true } && commandId == CommandIds.Copy` przepuszcza; `CommandIds.Move` przy wtyczce w aktywnym panelu → komunikat z punktu 1.

- [ ] **Step 1:** Test headless (wzorzec z CLAUDE.md „UI verification without a desktop session”) w scratchpadzie, nie w repo: `MainWindow` + `Attach(host)`, w prawym panelu otwarty fake `IPanelPlugin` bez `accepts`, w lewym folder z `a.txt` → `window.KeyPress(F5)`, `RunJobs()` → w folderze pod prawym panelem nie pojawił się `a.txt`, `LastNotification` == `Loc.T("Panel „{0}” nie przyjmuje plików.", …)`. Drugi przypadek: Ctrl+Q (podgląd) w prawym → F5 otwiera `TransferDialog` jak dziś.
- [ ] **Step 2:** Uruchom → FAIL (dziś kopiuje).
- [ ] **Step 3:** Implementacja; teksty do `en.json`.
- [ ] **Step 4:** Test headless → PASS; `dotnet build` bez ostrzeżeń; `*LocalizationTests` → PASS. Ręcznie (prawdziwe okno, `dotnet run --project src/Commander4K.App`) po Task 6: F5 do przykładu, wybór z dwóch odbiorców, komunikat zwrotny, F5 w panelu przykładu zapisuje plik, Ctrl+R odświeża.
- [ ] **Step 5:** Commit `Route F5 to plugin panel receivers and providers`.

### Task 6: Przykład „upload-client-python” i test end-to-end

**Files:**
- Create: `examples/plugins/upload-client-python/plugin.json`, `main.py`, `c4k.py` (kopia SDK), `README.md`
- Modify: `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`
- Modify: `docs/plugins.md` (odnośnik do przykładu)

**Interfaces:**
- Consumes: wszystko powyżej.
- Produces: panel `endpoints` (`accepts: true`, `provides: true`, permisje: `net`), akcje: „Dodaj endpoint” (`ui.prompt` → URL, zapis w `storage`), „Usuń”. `panel.receivers` → jeden odbiorca na endpoint (`multiple: true`, `returnsMessage: true`); `panel.receive` → dla każdego pliku `http_request(url, "POST", multipart={field:"file", path})` + `report_progress`, `text` = „Wgrano N/M plików” + kody HTTP. `panel.providers` → `save-response` („Zapisz odpowiedź GET jako plik”); `panel.provide` → `http_request(GET)` per zaznaczony endpoint, plik `<host>.json` z `text`.

- [ ] **Step 1: Test** `Upload_client_receives_and_provides`: `HttpListener` lokalnie; `storage` z jednym endpointem; `GetReceiversAsync([a.txt, b.txt])` → 1 odbiorca `Multiple`; `ReceiveAsync` → serwer dostał 2 żądania multipart, `Text` zawiera „2/2”; `GetProvidersAsync` → `save-response`; `ProvideAsync` → jeden `ProvidedFile` z `Text` równym odpowiedzi serwera.
- [ ] **Step 2:** `--filter-method "*Upload_client*"` → FAIL.
- [ ] **Step 3:** Napisz przykład (bez `open()`, `print` itd. — `PluginSourceCheck` musi być czysty).
- [ ] **Step 4:** Pełne `dotnet test --solution Commander4K.sln` → PASS (identyczność SDK, `PluginSourceCheckTests`, `LocalizationTests`). Ręczna weryfikacja z Task 5 Step 4.
- [ ] **Step 5:** Commit `Add upload client example plugin for panel transfers`.
