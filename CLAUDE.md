# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository status

The sections below (in Polish) are the project specification and implementation plan; treat them as the source of truth for architecture and follow the step order in section 4. Steps 1–3 are done (solution, `PluginSdk`, `Core`, built-in plugins for local/`net://`/`reg://`/SFTP+FTP skeleton, tests). `ArchiveVfsPlugin` is not written yet. Step 4 (UI) is next; `Commander4K.App` is still a placeholder window. Beyond the spec, the user asked for switchable skins (Norton Commander, Midnight Commander, modern...): the model lives in `Core/Theming` and the UI must render from `Skin` rather than hard-coded colours.

- Build: `dotnet build Commander4K.sln` — `TreatWarningsAsErrors` is on globally, so any warning fails the build.
- Test: `dotnet test --solution Commander4K.sln` — `global.json` opts into Microsoft.Testing.Platform (VSTest mode is rejected by the .NET 10 SDK, so don't add `Microsoft.NET.Test.Sdk`). Tests use xunit.v3.
- Single test: `dotnet test --solution Commander4K.sln -- --filter-method "*Chord_waits*"` (xunit.v3 filter options go after `--`).
- Release (not written yet): `pwsh ./build-release.ps1` → ReadyToRun single-file `win-x64` and `win-arm64` into `dist/`.

Build conventions: shared settings live in `Directory.Build.props` (TFM, RIDs, nullable, warnings-as-errors); package versions are pinned centrally in `Directory.Packages.props`, so `PackageReference` items carry no `Version`. `Commander4K.Plugins.BuiltIn` has `AllowUnsafeBlocks` for `[LibraryImport]`.

Core implementation notes that span files:
- `VfsRouter` resolves providers via `VfsPath.GetScheme` (`C:\` → `file`, `\\srv\share` → `smb`, otherwise the `scheme://` prefix), and wraps every provider listing in `ResilientAsyncExecutor.StreamAsync`, so plugin code never needs its own hang protection. `OpenListing` returns a bounded `ChannelReader<VfsEntry>`; the UI consumes it with `ChannelBatching.ReadBatchesAsync` (first batch immediately, then ≤ one per 16 ms).
- `SqliteIndexService`: writes are fire-and-forget records (`IndexWriteCommand` subclasses) drained by one writer in batched transactions; use `FlushAsync()` as a barrier (tests rely on it). Microsoft.Data.Sqlite's async methods are synchronous, so reads are explicitly `Task.Run`.
- Progress reporting goes through `ThrottledProgress<T>` (30 Hz, thread-safe, `Flush()` for the final value); `JobQueueManager` and `DirectorySizeCalculator` both use it. `JobChanged` fires on worker threads — the UI must marshal.
- Network plugin lessons verified on a real LAN: `NetServerEnum` blocks 30 s+ when no Computer Browser exists, so it runs under a 3 s abandonable timeout; `NetShareEnum` against a host without SMB waits ~10 s, so host level probes ports 445/22/21 first and only enumerates shares when 445 answers; reverse DNS yields `name.lan` and the bare label often does not resolve forward, so host paths use the full DNS name while the display name is the short label.
- Paths: providers never emit `..` (the UI adds it from `GetParentPath`). Registry values are addressed as `reg://HIVE\key\\ValueName` (double backslash, which can't occur in key paths). Remote paths are parsed by hand, not with `Uri`, to keep `#`/`?`/`%` in names. The SFTP `HostKeyValidator` currently accepts every key: the UI must replace it with a known-hosts check.
- Plugin tests write to a throw-away `HKCU\Software\Commander4K.Tests\<guid>` key and delete it afterwards.
- Skins: `BuiltInSkins` + user JSON files in `config/skins/` (same id overrides a built-in), selected skin id persisted in `config/settings.json`; commands `cm_SelectSkin` (Ctrl+Shift+K) and `cm_NextSkin` (Ctrl+Alt+K).
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