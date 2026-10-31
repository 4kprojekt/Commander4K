# F5 między panelem plików a panelem wtyczki: odbiorcy i wydawcy plików

Data: 2026-10-31. Status: projekt do zatwierdzenia (po przeglądzie budowniczego).

## Po co

Kopiowanie (F5) to w Commander4K **wysłanie zaznaczenia z jednego panelu do drugiego**. Dziś działa tylko między systemami plików (dysk, SMB, SFTP, FTP, archiwa — wszystko przez `VfsRouter` i `VfsTransferJob`, [MainWindow.cs](../../../src/Commander4K.App/MainWindow.cs) `TransferAsync`). Panele wtyczek (`IPanelPlugin`, `contributes.panels`) to wiersze z akcjami i nie znają pojęcia „plik”.

Użytkownik chce, żeby wtyczki **same deklarowały, czy przyjmują pliki (jeden czy wiele), jakie, i co z nimi zrobią** — np. klient API: zaznaczam pliki po lewej, po prawej jest otwarty panel klienta, F5 → wybieram endpoint → upload. I w drugą stronę: zaznaczone elementy panelu wtyczki → F5 → lądują jako pliki w panelu plików.

Przy okazji: dziś, gdy drugi panel pokazuje wtyczkę, F5 **po cichu kopiuje do folderu ukrytego pod nią** (`Other.ActiveTab.Path` nie sprawdza `Other.Mode`). To jest błąd i ten projekt go usuwa.

## Cele

1. **Odbiór (pliki → wtyczka).** Panel wtyczki może przyjmować pliki z panelu plików po drugiej stronie. F5 w panelu plików, gdy drugi panel pokazuje taką wtyczkę, wysyła zaznaczenie do niej zamiast kopiować na dysk.
2. **Wydanie (wtyczka → pliki).** Panel wtyczki może zamieniać zaznaczone wiersze na pliki. F5 w panelu wtyczki zapisuje je w folderze panelu plików po drugiej stronie.
3. **Odbiorcy i wydawcy są dynamiczni.** Wtyczka podaje ich listę w chwili F5 (endpointy klienta API nie są zaszyte w wtyczce — zależą od tego, co użytkownik w niej ma). Manifest deklaruje tylko, że panel w ogóle to umie.
4. **Każdy odbiorca deklaruje swoje warunki:** typy plików (te same wzorce co podglądy: `.ext`, nazwy, `*`/`?`), jeden plik czy wiele, czy przyjmuje foldery, czy po odbiorze zwraca komunikat.
5. **Wybór:** pasuje kilku odbiorców/wydawców → lista w oknie F5; pasuje jeden → jest od razu wybrany (okno wygląda jak zwykłe potwierdzenie kopiowania, z nazwą akcji); nie pasuje żaden → komunikat dlaczego, nic się nie dzieje.
6. **Foldery:** odbiorca dostaje folder tylko jeśli zadeklarował, że przyjmuje foldery. Inaczej zaznaczenie z folderem do niego nie pasuje (aplikacja nie rozwija folderów sama).
7. **Postęp** w zwykłej kolejce zadań (pasek jak przy kopiowaniu, Esc/anuluj działa). **Komunikat po odbiorze**, jeśli odbiorca go zadeklarował i zwrócił (np. „Wgrano 3 pliki, link: …”, błąd per plik).
8. **Bezpieczeństwo:** wciśnięcie F5 i potwierdzenie to zgoda na odczyt **dokładnie tych** plików. Wtyczka nie potrzebuje ogólnego `fs.read` do odbioru ani `fs.write` do wydania. Każdy przesył jest w audycie (`AuditLog`).
9. Usunięcie błędu: F5 nigdy nie kopiuje do folderu schowanego pod panelem wtyczki ani pod przeglądarką. [M] Podgląd (Ctrl+Q) i przypięte widoki folderu z `c4k.dir` (`PinnedFolder`) zostają jak dziś — tam użytkownik jest „w” tym folderze i F5 kopiuje do niego (zachowanie Total Commandera przy szybkim podglądzie).

## Poza zakresem (na razie)

- **F6 (przenieś)** do/z wtyczek. F6, gdy po drugiej stronie (lub w aktywnym panelu) jest wtyczka, pokazuje komunikat, że przenoszenie z/do wtyczek nie jest obsługiwane.
- Wysyłanie do wtyczki **bez otwartego panelu** (menu, paleta, polecenie „Wyślij do…”).
- Przeciąganie myszą.
- Przesył między dwoma panelami wtyczek.
- Wbudowane wtyczki-odbiorcy. Pierwszym odbiorcą będzie przykład w `examples/plugins` (patrz niżej); wbudowane panele (menedżer zadań itd.) nic nie przyjmują.
- Odbiorcy dla wtyczek, które nie są panelami (`contributes.commands`, `previews`, `palettes`).

## Anty-cele (porażka, nawet jeśli działa)

- Wtyczka może przeczytać plik, którego użytkownik nie wysłał (zgoda wycieka poza zaznaczenie albo poza czas trwania zadania).
- F5 robi coś innego niż widać w oknie potwierdzenia (np. po cichu kopiuje na dysk albo wysyła do innego odbiorcy).
- Zwykłe kopiowanie między systemami plików zmienia zachowanie lub zwalnia.
- Wtyczka-wydawca może zapisać plik poza folderem docelowym (`..`, ścieżki absolutne, nazwy ze znakami niedozwolonymi w Windows).
- Duży upload przechodzi w całości przez pamięć wtyczki jako base64 w JSON-RPC, gdy wtyczka i tak tylko przekazuje go dalej do HTTP.

## Kształt (ustalone z użytkownikiem i moje decyzje)

Oznaczenia: **[U]** — ustalone z użytkownikiem; **[M]** — moja decyzja, do zmiany przez budowniczego, jeśli kod podpowiada lepiej.

### Manifest [M]

`PanelContribution` dostaje dwie flagi: `accepts: true` (panel może mieć odbiorców) i `provides: true` (panel może mieć wydawców). Bez flagi host nie wywołuje odpowiednich metod i zachowuje się jak dziś (z poprawką z celu 9). Aktualizacja `docs/plugin.schema.json` i `docs/plugins.md`.

### Protokół [M]

- `panel.receivers { panel, files: [{ path, name, kind: "file"|"folder", size }], context }` → `[{ id, title, description?, extensions?, multiple, folders, returnsMessage }]`. Host przekazuje opis zaznaczenia, więc wtyczka może od razu odfiltrować. Host **i tak** sam sprawdza każdy warunek (wzorce przez to samo dopasowanie co `ExternalPreviewPlugin.Matches`, `multiple`, `folders`) — nie ufa wtyczce.
- `panel.receive { panel, receiver, jobId, files: [...], context }` → `{ text?, results?: [{ path, ok, error? }] }`. [M] **Jedno wywołanie na całe zaznaczenie** (odbiorca z `multiple` może wysłać wszystko jednym żądaniem, np. multipart); błędy per plik w `results`, pokazywane w komunikacie zadania. Wywoływane w zadaniu w kolejce, **bez** domyślnego 60-sekundowego limitu `ExternalPlugin.DefaultTimeout` (tylko anulowanie przez użytkownika). Na czas tego wywołania ścieżki z `files` (i dla folderów — wszystko pod nimi) są dodane do zgody `fs.read` tej wtyczki; zgoda wygasa, gdy wywołanie się skończy lub zostanie anulowane. Uwaga: `AppHost.HostFor` trzyma jeden `ScopedPluginHost` na id wtyczki, a żądania wtyczki→host nie niosą powiązania z wywołaniem zewnętrznym, więc zgoda jest **per wtyczka na czas wywołania** (dotyczy każdego żądania tego procesu w tym czasie). To akceptowalne: proces i tak jest jeden, a okno jest krótkie i jawne. Wtyczka czyta przez istniejące `fs.readBytes`/`fs.readText`/`fs.list`/`fs.stat` — to działa też dla plików na SFTP/w archiwach, bo idzie przez router.
- `panel.providers { panel, rowIds, context }` → `[{ id, title, description? }]`.
- `panel.provide { panel, provider, jobId, rowIds, context }` → `{ files: [{ name, text? | base64? | url? }], text? }`. [M] Treść inline (`text`/`base64`) ma limit (np. 16 MB na plik, odrzucenie powyżej); duże pliki wtyczka podaje jako `url`, który host pobiera strumieniowo przez swojego klienta HTTP — gated `net` jak `net.request` i audytowany. Bez domyślnego 60-sekundowego limitu, jak `panel.receive`. Zapisuje host, do folderu drugiego panelu, przez `VfsRouter`, z tą samą polityką nadpisywania co F5 (`OverwritePolicy` z okna). Nazwy sprawdzane jak w `PluginAuthoring.Prepare` (bezpieczne ścieżki względne, podfoldery dozwolone).
- Postęp: protokół nie ma dziś powiadomień postępu, a `JsonRpcConnection` nie wiąże powiadomień z trwającym wywołaniem. Nowe powiadomienie wtyczka→host `job.progress { jobId, done, total, bytes?, text? }` — host kojarzy je po `jobId` z parametrów `panel.receive`/`panel.provide` i przekazuje do `IProgress<JobMetrics>` zadania; nieznany `jobId` ignorowany. Bez powiadomień pasek jest nieokreślony i pokazuje liczbę plików. Dodać do trzech SDK (np. `report_progress`).
- Anulowanie: tak jak inne wywołania `CallAsync` z tokenem zadania.

### Duże uploady [M]

Do `net.request` dochodzi opcja treści z pliku (`bodyFile: path`, opcjonalnie multipart `{ field, path, fileName? }`): host strumieniuje plik przez router prosto do żądania HTTP. Gated jak dziś (`net`) plus zgoda odczytu tego pliku (czyli działa w praktyce tylko w trakcie `panel.receive` albo z `fs.read`). Dzięki temu klient API nie wciąga pliku do pamięci. Wymaga dopisania do `IPluginHost`/`ScopedPluginHost`, `ExternalPlugin.HandleRequestAsync`, trzech klientów SDK (i kopii w przykładach) oraz tabeli w `docs/plugins.md` — zgodnie z regułą z CLAUDE.md.

### UI [U]+[M]

- [U] F5 w panelu plików, drugi panel = wtyczka z `accepts`: host pyta o odbiorców, filtruje, pokazuje okno: co wysyłam („3 pliki”), do kogo (lista, jeśli więcej niż jeden pasuje), opis odbiorcy. Bez opcji „kolejka/nadpisz”, które nie mają sensu przy odbiorze (zostaje tylko „w tle”/kolejka, jeśli to proste).
- [U] F5 w panelu wtyczki z `provides`: okno z listą wydawców (jeden → wybrany), celem (ścieżka drugiego panelu) i polityką nadpisywania. Dotyczy tylko `cm_Copy`; pozostałe `FileCommands` nadal pokazują dzisiejszy komunikat „zamknij panel (Esc)”.
- [U] **Klawisze w panelu z `provides`:** F5 przestaje odświeżać — odświeżanie zostaje tylko pod Ctrl+R. Panele bez `provides` zachowują F5 = odśwież jak dziś. [M] `PluginPanelView.HandleKey` w panelu z `provides` nie łapie F5, tylko przepuszcza klawisz do keymapy, więc działa to, pod czym użytkownik ma `cm_Copy` (także po przemapowaniu); bramka `FileCommands` w `MainWindow.ExecuteAsync` przepuszcza `cm_Copy`, gdy aktywny panel to wtyczka z `provides`. [M] Akcja wtyczki jawnie przypisana do F5 (`shortcut: "F5"`) wygrywa jak dziś (`ManifestReader.Validate` zwraca tylko błędy, więc bez osobnego ostrzeżenia; opisane w `docs/plugins.md`).
- [U] Nikt nie pasuje / wtyczka nie deklaruje: komunikat z powodem („Wtyczka X nie przyjmuje plików”, „Żaden odbiorca nie przyjmuje folderów”, „Odbiorca przyjmuje tylko jeden plik”).
- [M] Okno wydania: ścieżka docelowa jest edytowalna jak w `TransferDialog` (wpisany folder musi istnieć albo zostanie utworzony, jak przy F5).
- [M] Drugi panel to przeglądarka albo wbudowany panel wtyczki bez `accepts` (menedżer zadań, informacje o systemie…): komunikat, że ten panel nie przyjmuje plików — nie kopiowanie do ukrytego folderu.
- [U] Komunikat zwrotny (`text`) pokazany tak jak wynik `panel.action` dziś; panel wtyczki przeładowuje się po odbiorze (jak po akcji).
- Wszystkie nowe teksty przez `Loc.T` + wpisy w `en.json`.

### Wbudowane wtyczki [M]

`IPanelSession` dostaje opcjonalne metody z domyślną implementacją „brak” (`GetReceiversAsync`, `ReceiveAsync`, `GetProvidersAsync`, `ProvideAsync`), żeby wbudowane panele mogły kiedyś dołączyć tym samym kanałem; `ExternalPanelPlugin.Session` mapuje je na protokół. Zgoda czasowa na odczyt żyje w `ScopedPluginHost` (lista ścieżek z licznikiem/zakresem), nie w adapterze.

### Przykład [M]

Nowy (albo rozszerzony istniejący) przykład w `examples/plugins` — mały „klient uploadu”: panel z listą endpointów zapisanych w `storage`, odbiorca per endpoint (wysyła przez `net.request` z `bodyFile`, zwraca kod HTTP), wydawca „zapisz odpowiedź jako plik”. Musi przejść `PluginSourceCheck` i testy przykładów (`ExternalPluginTests`).

## Ograniczenia

Z CLAUDE.md, w szczególności: żadnego I/O na wątku UI (pytanie o odbiorców idzie w tle, okno czeka z timeoutem — [M] 5 s, potem komunikat); nowe metody protokołu we wszystkich trzech SDK i kopiach przykładów (test pilnuje identyczności); `TreatWarningsAsErrors`; jednoparametrowe zdarzenia i jeden plik na widok w App; oczekiwane wyjątki (anulowanie, brak dostępu) łapane w naszym kodzie, nie wylatujące z lambd `Task.Run`.

## Do decyzji budowniczego

- Dokładne nazwy pól JSON i typów w SDK.
- Czy okno odbioru to nowy dialog, czy wariant `TransferDialog`.
- Czy zgoda czasowa na odczyt obejmuje foldery rekursywnie przez prefiks ścieżki (z normalizacją jak w polityce MCP) czy przez jawną listę.
- Format audytu dla przesyłu (jedna linia na zadanie z listą plików czy jedna na plik).

## Testy (minimum)

- Host odrzuca odbiorcę, który nie pasuje (typ, `multiple`, `folders`), nawet jeśli wtyczka go zwróciła.
- Zgoda czasowa: w trakcie `panel.receive` wtyczka czyta wysłany plik, nie czyta sąsiedniego; po zakończeniu nie czyta już wysłanego.
- `panel.provide` z nazwą `..\x`, absolutną lub z `:` → odrzucona, nic nie zapisane.
- F5 do panelu wtyczki bez `accepts` nie tworzy żadnych plików w folderze pod panelem.
- Przykład end-to-end na prawdziwym runtime (jak `ExternalPluginTests`): odbiór i wydanie.
