# Dashboard z kart: podglądy w małej formie, podgląd API, monitor SSH, wykresy

Data: 2026-10-31. Status: projekt do zatwierdzenia (po przeglądzie budowniczego).

## Po co

Widok `dashboard` z `c4k.dir` umie dziś pokazać tylko kafelki-skróty: tytuł, ikona, obrazek i cel kliknięcia (`open`, `url`, `run`) — `FolderViews.DashboardAsync` → `TilesPreview`. Kafelek nic nie wie o tym, na co wskazuje.

Użytkownik chce, żeby dashboard był **pulpitem z kart**, z których każda pokazuje stan wskazanej rzeczy: folder ze statystykami, galeria z kilkoma miniaturami, serwer SSH ze statusem, CPU i dyskiem, wartości z dowolnego API HTTP, wykres. Wygląd karty ma dostarczać **ten sam podgląd**, który rzecz pokazuje w pełnym widoku (Ctrl+Q) — podgląd uczy się drugiej, małej formy. Dzięki temu nowe podglądy (także z wtyczek) od razu dają nowe rodzaje kart.

## Cele

1. Podglądy mogą dostarczać **kartę**: małą formę z wartościami, miniaturami, wykresem, statusem i własnymi przyciskami (np. „Otwórz”, „Odśwież”).
2. Kafelki dashboardu w `c4k.dir` mogą być kartami: wskazują cel i (opcjonalnie) który podgląd go rysuje. Wszystkie kafelki zostają w jednym pliku, jak dziś.
3. **Odświeżanie konfigurowane per karta** (`"refresh"`); bez niego odczyt przy otwarciu + przycisk „Odśwież”.
4. Nowy wynik podglądu: **wykres** (liniowy, słupkowy), rysowany przez aplikację w kolorach skórki — jako pełny podgląd i jako element karty.
5. Nowy wbudowany podgląd **API** (`view: "api"`): zapytanie HTTP przez wspólnego klienta hosta (`IPluginHost.HttpRequestAsync`), na karcie i jako pełny podgląd folderu wybrane pola z odpowiedzi JSON.
6. Komenda **„Dodaj kartę API”**: zapytanie próbne, lista pól z odpowiedzi z wartościami, wybór pól i sposobu pokazania, dopisanie kafelka do `c4k.dir` bieżącego folderu.
7. Nowy wbudowany podgląd **monitor SSH**: status, obciążenie/CPU, pamięć, dyski serwera.
8. Karty z podglądów: zwykły folder (statystyki), galeria (miniatury + statystyki + „Otwórz”), API, SSH.

## Poza zakresem (na razie)

- Edycja dashboardu w interfejsie (dodawanie, usuwanie, przestawianie kart, przeciąganie). Dashboard edytuje się ręcznie w `c4k.dir`; jedyny kreator to „Dodaj kartę API”, który tylko dopisuje kafelek. Zarządzanie w UI — osobny projekt później.
- Historia wartości na dysku. Wykres z odświeżania żyje w pamięci, póki dashboard jest otwarty.
- Alerty i powiadomienia o progach.
- Monitor SSH dla serwerów innych niż Linux (inne pokazują tylko status połączenia).
- Logowanie OAuth do API; tylko stałe nagłówki/tokeny.
- Własne polecenia SSH z `c4k.dir` w monitorze (patrz anty-cele).

## Czego nie wolno (anty-cele)

- Żadnego I/O na wątku UI; wolna lub wisząca karta nie może opóźnić innych kart, przewijania ani nawigacji. Każda karta ładuje się niezależnie, z własnym anulowaniem i limitem czasu.
- Odświeżanie nie może działać, gdy dashboard nie jest widoczny (zamknięty podgląd, wyjście z folderu, inny tryb panelu). Nowy cykl nie startuje, póki poprzedni trwa.
- Sekrety (tokeny, klucze API) nigdy nie lądują w `c4k.dir` otwartym tekstem — plik bywa w udziale sieciowym i w repozytoriach.
- Monitor SSH wykonuje wyłącznie stały, wbudowany zestaw poleceń tylko do odczytu. Treść z `c4k.dir` nigdy nie staje się poleceniem zdalnym (to byłaby furtka obok zgody `FolderTrustStore` dla `run`).
- Samo pokazanie dashboardu nie może wywołać zapytania ze skutkami ubocznymi ani logowania SSH bez zgody (patrz „Zgoda na karty sieciowe”).
- Karta nie omija modelu uprawnień: podglądy zewnętrzne dostają tylko `ScopedPluginHost` swojej wtyczki; wszystko jest audytowane jak dziś.
- Istniejące `c4k.dir` z kafelkami bez nowych pól wyglądają i działają dokładnie jak dziś.

## Ograniczenia z projektu (CLAUDE.md)

- UI w C# bez XAML, rysowanie z `SkinBrushes` (żadnych kolorów na sztywno), układ kart w obu rodzinach skórek (`IsModern` i retro).
- Teksty przez `Loc.T`/`Loc.P` z wpisami w `en.json`; komendy z `CommandIds`, opisem w `RegisterCommands`; ewentualne ustawienia w `AppSettings` + `SettingsCatalog`.
- Nowa metoda protokołu = `IPluginHost`/`ScopedPluginHost` (jeśli dotyczy hosta), `ExternalPlugin`, trzy klienty SDK, kopie w przykładach i w `plugins-catalog`, tabela w `docs/plugins.md`.
- Zmiana `c4k.dir` = `FolderConfig`, `docs/c4k.dir.schema.json`, przewodnik w `docs/plugins.md`.
- Wyjątki oczekiwane (anulowanie, timeout, odmowa dostępu) łapane w naszym kodzie, nie wypuszczane z lambd `Task.Run`.

## Jak to działa — decyzje użytkownika

- Kafelki zostają w jednym `c4k.dir` (sekcja `tiles` widoku `dashboard`).
- Kafelek odnosi się do podglądu; podgląd dostarcza wygląd karty, w tym przyciski. Przykłady od użytkownika: galeria = „Otwórz” + kilka miniatur + statystyki; zwykły folder = tylko statystyki; SSH = „Otwórz”, „Odśwież”, status, CPU, dysk.
- Odświeżanie konfigurowane.
- Podgląd typu „zapytanie API” korzysta z asynchronicznego klienta HTTP dostarczanego przez aplikację, nie z własnego.
- Podgląd może zwrócić wykres, który aplikacja rysuje.
- Aplikacja pobiera pola z API i pozwala wybrać, które się pokazują.
- Dashboard na razie edytowany ręcznie; zarządzanie w UI później.

## Projekt — moje decyzje (do sprawdzenia przy czytaniu)

### Kafelek-karta w `c4k.dir`

Kafelek dostaje nowe, opcjonalne pola; kafelek bez nich jest zwykłym skrótem jak dziś.

```jsonc
{
  "preview": {
    "view": "dashboard",
    "tiles": [
      { "title": "Zdjęcia", "open": "D:/Foto/2026", "card": "gallery" },
      { "title": "Projekty", "open": "~/Repos" },                                   // stary kafelek-skrót
      { "title": "Projekty", "open": "~/Repos", "card": true },                      // karta z podglądu wybranego jak Ctrl+Q
      { "title": "VPS", "open": "sftp://kamil@vps.example.com/", "card": "ssh", "refresh": "10s" },
      { "title": "Grafana", "card": "api", "refresh": "1m",
        "api": {
          "url": "https://grafana.example.com/api/health",
          "headers": { "Authorization": "Bearer {secret:grafana}" },
          "fields": [
            { "path": "database", "label": "Baza", "as": "status", "ok": ["ok"] },
            { "path": "version", "label": "Wersja" }
          ]
        } },
      { "title": "Steam", "card": "official.steam-library", "options": { "top": 3 } } // karta z podglądu wtyczki
    ]
  }
}
```

- `card: true` idzie tą samą drogą co Ctrl+Q, łącznie z `c4k.dir` folderu docelowego (`ResolveFolderView`, ten sam limit 2 s i te same schematy). Jeśli podglądem celu jest znowu `dashboard`, karta nie zagnieżdża kart, tylko jest **kartą dashboardu** (patrz „Dashboardy wybierane z dashboardu”). Karta nazwana (`card: "gallery"`) ignoruje `c4k.dir` celu; `options` biorą się wyłącznie z kafelka. *(Moja decyzja.)*
- `card`: `true` = podgląd wybrany jak dla Ctrl+Q (najwyższy priorytet, który przyjmuje cel); nazwa = konkretny podgląd: wbudowany (`files`, `gallery`, `notes`, `markdown`, `api`, `ssh`) albo wtyczki (`<plugin id>` lub `<plugin id>/<preview id>`, jak w `FolderAction.Plugin`/`Preview`). Brak wtyczki → karta „zainstaluj” jak dzisiejszy kafelek instalacji.
- Cel karty to `open` kafelka (ścieżka VFS, `~`, względna do folderu); karta `api` celu nie potrzebuje.
- `refresh`: czas `"5s"`, `"1m"`, `"1h"` (sekundy/minuty/godziny); minimum 2 s (krótsze podbijane do 2 s). Brak = jeden odczyt.
- `options`: JSON przekazywany do podglądu, jak dziś `options` w `c4k.dir`.
- `size` (opcjonalnie): `"small"` / `"wide"` — szerokość karty w siatce (1 albo 2 kolumny). Domyślnie `small`.
- Kliknięcie w tło karty działa jak kliknięcie w dzisiejszy kafelek (cel `open`/`url`); `run` w karcie nie jest dozwolony (karta = podgląd, nie skrypt) — kafelek z `run` zostaje skrótem.

### Dashboardy wybierane z dashboardu (decyzja użytkownika: dashboard też zwraca kafelek)

- Widok `dashboard` sam dostarcza kartę: tytuł i opis z `c4k.dir`, liczba kafelków, nazwy kilku pierwszych, zbiorczy status (najgorszy status kart z `refresh`, jeśli dashboard był już otwarty w tej sesji; inaczej bez statusu — karta dashboardu sama nie ładuje kart w środku). `card: "dashboard"` albo `card: true` na folderze z dashboardem. Dzięki temu jeden dashboard („Dom”) wybiera inne („Serwery”, „Zdjęcia”).
- Kliknięcie karty dashboardu (albo „Otwórz”) działa jak Enter na tym folderze: panel plików pod spodem przechodzi do folderu, a w tym samym przypiętym podglądzie (`PinnedFolder`) otwiera się jego dashboard.

### Cofanie i powrót do plików (decyzja użytkownika: zawsze można przełączyć podgląd na pliki)

- **Esc** w przypiętym widoku zawsze pokazuje zwykłą listę plików folderu, którego widok jest na ekranie (jak dziś). **Shift+Enter** zawsze wchodzi do folderu jako lista plików. Tych dwóch zasad nie wolno złamać żadnemu widokowi ani wtyczce: klawisze trafiają do okna przed kartami i przed zawartością podglądu.
- Nagłówek każdego przypiętego widoku folderu ma stały pasek: przycisk **„←”** (wstecz, nieaktywny bez historii), okruszki (`Dom › Serwery`; klik = skok do poziomu) i przycisk **„📄 Pliki”** (to samo co Esc). Pasek rysuje host, nie widok, więc ma go także widok z wtyczki.
- **Wstecz** = przycisk „←”, Alt+← i Backspace (Backspace tylko gdy fokus nie jest w polu tekstowym). Wraca do poprzedniego widoku z historii i cofa panel plików do jego folderu.
- Historia to stos (folder, sekcja `enter`/`preview`) w przypiętym podglądzie; Esc ją czyści (lista plików ma swoją zwykłą historię i `..`). Cykle (A → B → A) są dozwolone, bo to historia nawigacji, nie rekurencja; stos ma limit (np. 50).
- W podglądzie śledzącym kursor (Ctrl+Q, bez przypięcia) kliknięcie karty dashboardu działa jak Enter na tym folderze.

### Kontrakt karty w SDK

Nowy, opcjonalny interfejs obok `IPreviewPlugin` (podgląd bez niego dostaje kartę domyślną: ikona, tytuł, podtytuł, jak dzisiejszy kafelek):

```csharp
public interface IPreviewCardPlugin : IPreviewPlugin
{
    Task<PreviewCard> LoadCardAsync(PreviewCardRequest request, IPluginHost host, CancellationToken cancellationToken);
}

// Target: wpis celu (null dla kart bez celu, np. API); Options: JSON z kafelka; Previous: poprzednia karta przy odświeżaniu
public sealed record PreviewCardRequest(VfsEntry? Target, string? OptionsJson, PreviewCard? Previous);

public sealed record PreviewCard(
    string? Title,                       // null = tytuł z kafelka
    string? Subtitle,
    string? Icon,
    CardStatus Status,                   // None / Ok / Warning / Error — pasek koloru karty
    IReadOnlyList<CardItem> Items,
    IReadOnlyList<CardAction> Actions);

public abstract record CardItem;
public sealed record CardValue(string Label, string Value, CardStatus Status = CardStatus.None) : CardItem;  // „CPU 12 %”
public sealed record CardThumbnails(IReadOnlyList<ReadOnlyMemory<byte>> Images) : CardItem;              // kilka miniatur
public sealed record CardChart(ChartData Chart) : CardItem;
public sealed record CardText(string Text) : CardItem;
public sealed record CardProgress(string Label, double Fraction, string? Text = null) : CardItem;         // dysk 73 %

// Target jak w PreviewTile (ścieżka, http(s), URI, c4k:…) plus c4k:refresh (odśwież tę kartę)
public sealed record CardAction(string Label, string Target);
```

- `Previous` pozwala podglądowi dokładać punkty do wykresu (seria w czasie) i liczyć różnice (CPU z dwóch odczytów `/proc/stat`). Host trzyma tylko ostatnią kartę.
- Wykres: `ChartData(ChartKind Kind /* Line, Bar */, IReadOnlyList<ChartSeries> Series, string? Unit, double? Min, double? Max)`, `ChartSeries(string Name, IReadOnlyList<ChartPoint> Points)`, `ChartPoint(double X, double Y, string? Label)`. Dla serii w czasie `X` = czas Unix w sekundach. Host przycina serie do 120 punktów.
- Pełny podgląd też może zwrócić wykres: nowy `ChartPreview(string Title, ChartData Chart, string? Description) : PreviewContent`.

### Protokół dla wtyczek zewnętrznych

- Nowa, opcjonalna metoda `preview.card` z parametrami jak `preview.render` (`path`/wpis, `options`) plus `previous` (ostatnia karta jako JSON). Wynik: `{ "title"?, "subtitle"?, "icon"?, "status"?, "items": [{ "kind": "value"|"thumbnails"|"chart"|"text"|"progress", … }], "actions": [{ "label", "target" }] }`; obrazy base64 jak w `kind: "tiles"`.
- Wtyczka deklaruje, że umie karty: `contributes.previews[].card: true` (schemat manifestu). Bez tego — karta domyślna, a `preview.card` nie jest wołane.
- `preview.render` może zwrócić `kind: "chart"`.
- `ExternalPreviewPlugin` implementuje `IPreviewCardPlugin`; trzy klienty SDK dostają pomocniki do budowania karty i wykresu (spójnie z istniejącymi pomocnikami `tiles`); kopie w przykładach i `plugins-catalog` (+ `Update-Catalog.ps1`); dokumentacja w `docs/plugins.md`. Jeden przykład (np. `folder-stats-python`) dostaje kartę, żeby test `ExternalPluginTests` przeszedł przez prawdziwy runtime.

### Rysowanie i odświeżanie (App)

- Widok `dashboard` zwraca nowy `DashboardPreview` (tytuł, opis, lista definicji kafelków); `PreviewPanelView` rysuje go jako siatkę kart (zawijaną, karty `wide` na 2 kolumny), skórki modern — zaokrąglone karty z paskiem statusu, retro — ramka i tekst.
- Każda karta ma własny cykl: ładowanie w tle (`LoadCardAsync` przez host podglądu: wbudowane przez host aplikacji, zewnętrzne przez ich `ScopedPluginHost`), limit 15 s na odczyt, błąd/timeout → karta w stanie błędu z komunikatem i „Odśwież”; pozostałe karty bez zmian. Do czasu pierwszego wyniku karta pokazuje tytuł z kafelka i wskaźnik ładowania.
- Odświeżanie: `PeriodicTimer` per karta z `refresh`, pomija takt, gdy poprzedni odczyt trwa; wszystkie cykle anulowane, gdy dashboard przestaje być widoczny (ten sam moment, w którym `PreviewPanelView` dziś anuluje ładowanie). Przy powrocie — świeży odczyt.
- Współbieżność ładowania kart ograniczona (np. 4 naraz), jak kolumny.
- Kontrolka wykresu: owner-draw (`Control.Render`), linie/słupki, oś Y z min/max i jednostką, kolory serii z tokenów skórki (`accent`, `success`, `warning`, `error`…, cyklicznie). Bez nowych bibliotek wykresów.
- Przycisk akcji z `c4k:refresh` odświeża kartę; pozostałe cele idą tą samą drogą co dzisiejsze kliknięcie kafelka (`PreviewPanelView` → `MainWindow`, z tymi samymi zabezpieczeniami dla URI).

### Karty wbudowanych podglądów

- **Folder** (`FolderPreviewPlugin`): od razu liczba plików i folderów na pierwszym poziomie i data ostatniej zmiany; rozmiar całkowity (`CalculateSizeAsync`) liczony w tle z postępem („liczę…”), poza 15-sekundowym limitem karty i poza limitem 4 równoległych odczytów. Wynik w pamięci po ścieżce + dacie zmiany folderu; odświeżenie liczy go ponownie tylko, gdy data się zmieniła. *(Moja decyzja.)* Akcja „Otwórz”.
- **Galeria** (`gallery`): do 4 miniatur (pierwsze obrazy alfabetycznie, ≤ 16 MB, jak dziś), liczba obrazów, łączny rozmiar. Akcja „Otwórz”. Miniatury mogą być pomniejszane przez host przy rysowaniu; podgląd czyta tylko te 4 pliki.
- **Notatki / Markdown**: liczba notatek i ostatnio zmieniona (notes) albo pierwszy akapit pliku (markdown). Akcja „Otwórz”.
- **API** i **SSH** — niżej.

### Podgląd API (`view: "api"`)

- Definicja `api`: `url` (wymagany, http/https), `method` (domyślnie GET), `headers`, `body`, `contentType`, `fields`.
- Zapytanie przez `IPluginHost.HttpRequestAsync` hosta aplikacji (wspólny `HttpClient`, audyt). Wbudowany podgląd ma uprawnienie `net`. Limit czasu odczytu karty (15 s) obejmuje zapytanie.
- `fields[]`: `path` (ścieżka w JSON: `a.b.c`, indeksy `items[0].name`, `$` = całość), `label`, `as`: `value` (domyślnie; liczba lub tekst), `chart` (liczba dopisywana do serii co odświeżenie; seria per pole, wszystkie pola `chart` na jednym wykresie), `status` (zielony, gdy wartość ∈ `ok`, czerwony w przeciwnym razie; bez `ok` — `true`/`"ok"`/`"up"`/2xx jako dobre), `unit` (np. `"%"`, `"ms"`).
- Status karty: odpowiedź nie-2xx lub błąd sieci → `Error` (z kodem); któryś `status` czerwony → `Warning`/`Error`; inaczej `Ok`. Na karcie zawsze mały czas odpowiedzi.
- Bez `fields` albo dla odpowiedzi nie-JSON: karta pokazuje status HTTP i początek treści (`CardText`, ~200 znaków).
- Jako pełny podgląd folderu (`"preview": { "view": "api", "api": { … } }`): wybrane pola w dużej formie + wykres + surowa odpowiedź (sformatowany JSON) poniżej.
- **Sekrety**: w nagłówkach, URL i treści placeholder `{secret:<nazwa>}`. Wartości w nowym magazynie `config/secrets.json`, szyfrowane `SecretProtector` (DPAPI), jak `AiCredentialStore`. Brakujący sekret → przy pierwszym odczycie karta pokazuje „brak sekretu <nazwa>” z akcją, która pyta o wartość (`PromptAsync` z maskowaniem, jeśli łatwe; inaczej zwykłe pole) i zapisuje ją. Sekret podstawiany tylko w zapytaniach do hosta, z którym był pierwszy raz użyty (zapisany razem z wartością), żeby podmieniony `c4k.dir` nie wysłał tokenu gdzie indziej.

### Zgoda na karty sieciowe (decyzja użytkownika: wariant A)

- Karty `api` z metodą GET lub HEAD działają bez zgody.
- Karty `api` z inną metodą (POST, PUT, PATCH, DELETE) i wszystkie karty `ssh` działają dopiero, gdy użytkownik zaufa temu `c4k.dir` — ta sama zgoda co dla `run`: `FolderTrustStore`, ścieżka pliku + SHA-256 treści; zmiana pliku = ponowna zgoda. Jedna zgoda obejmuje cały plik (wszystkie jego karty i skrypty).
- Do tego czasu taka karta nie łączy się z niczym i pokazuje stan „wymaga zgody” z akcją **„Zezwól”**, która otwiera to samo okno potwierdzenia co `run`, z listą tego, co plik zrobi: adresy i metody zapytań, hosty SSH, polecenia `run`.
- Kreator „Dodaj kartę API” po zapisie oznacza plik jako zaufany (użytkownik właśnie go napisał), ale tylko gdy przed zmianą plik był zaufany albo nie istniał; inaczej pokazuje zwykłe okno zgody na całą nową treść.
- Karty dashboardu, folderu, galerii i wtyczek nie potrzebują tej zgody (wtyczki mają własne uprawnienia zatwierdzone w menedżerze).
- Zgoda działa dla `c4k.dir` z dysków i udziałów (jak `run`). Na innych schematach (SFTP/FTP) karty `api` z innymi metodami niż GET/HEAD i karty `ssh` pozostają w stanie „niedostępne w tym miejscu”.

### Komenda „Dodaj kartę API” (`cm_AddApiCard`)

1. Działa w folderze na dysku lub w udziale (jak `cm_FolderView`).
2. Pyta o URL, potem (opcjonalnie) o nagłówek autoryzacji; wartość nagłówka zapisuje od razu jako sekret o nazwie od hosta i w pliku zostawia placeholder.
3. Robi zapytanie próbne (w tle, z anulowaniem), pokazuje status albo błąd.
4. Dla JSON: lista wszystkich ścieżek liści (do rozsądnego limitu, np. 500; tablice — pierwszy element jako wzór `[0]`) z obecną wartością; wybór wielu pól, przy każdym rodzaj (`value`/`chart`/`status`) i etykieta (domyślnie ostatni człon ścieżki). Wygląd: nowe okno dialogowe z listą z polem wyszukiwania i zaznaczaniem — szczegóły układu dla budowniczego, w stylu istniejących `PickerDialog`/`DialogWindow`.
5. Pyta o tytuł i odświeżanie (brak / 10 s / 1 min / 5 min).
6. Dopisuje kafelek do `tiles` dashboardu w `c4k.dir`: gdy pliku nie ma — tworzy go z `preview` i `enter` = dashboard; gdy jest, ale `preview` to nie dashboard — pyta, czy zamienić podgląd na dashboard (stary zostaje tylko w `enter`, jeśli tam był). Zapis zachowuje resztę pliku; komentarze w pliku mogą przepaść — przed zapisem pliku z komentarzami dialog o tym ostrzega.

### Monitor SSH (`card: "ssh"`, cel `sftp://…`)

- Nowy podgląd wbudowany dla celów `sftp://` (jako karta, i jako pełny podgląd przy Ctrl+Q na wpisie hosta/połączenia, jeśli tani do zrobienia; inaczej tylko karta).
- Monitor jest częścią `Plugins.BuiltIn/Remote` i używa SSH.NET bezpośrednio, jak dostawca SFTP (dostawcy VFS też nie idą przez `IPluginHost`); każde połączenie i odczyt trafia do dziennika audytu ręcznie. Bez nowej metody protokołu — wtyczki zewnętrzne nie dostają `ssh.exec`. `RemoteTransferVfsPlugin` udostępnia monitorowi wyszukiwanie loginu i walidator klucza hosta. *(Moja decyzja.)*
- Łączy się `SshClient` (SSH.NET) tymi samymi danymi logowania co SFTP: zapisane połączenie (`RemoteTransferVfsPlugin.SetLogin`, klucz/hasło), `keyFile`/`user` z kafelka (`options`), klucze domyślne z `~/.ssh`. Weryfikacja klucza hosta przez tę samą ścieżkę TOFU (`KnownHostsStore`) co SFTP. Odmowa logowania → karta „wymaga logowania” z akcją, która otwiera `LoginDialog`. Sesja SSH trzymana, póki dashboard odświeża (bez łączenia co takt), zamykana razem z cyklem karty.
- Jedno stałe polecenie, tylko odczyt, np. `uname -s; cat /proc/loadavg; head -1 /proc/stat; grep -E 'MemTotal|MemAvailable' /proc/meminfo; df -P -x tmpfs -x devtmpfs; cat /proc/uptime`. Parsowanie po stronie aplikacji.
- Karta: status (połączony / nieosiągalny / błąd logowania), CPU % (z różnicy `/proc/stat` względem `Previous`; pierwszy odczyt — load average), pamięć (`CardProgress`), dyski (do 3 największych, `CardProgress`), uptime, wykres CPU z kolejnych odświeżeń. Akcje: „Otwórz” (cel SFTP w panelu) i „Odśwież”.
- Serwer nie-Linux (`uname` ≠ Linux) → tylko status i nazwa systemu.

### Wtyczki do pobrania: HTML, JSON, OpenAPI (decyzja użytkownika: osobne, nie wbudowane, w repo do pobrania)

Trzy osobne wtyczki w `plugins-catalog/<nazwa>/` z wpisami w `catalog.json` (instalowane z menedżera wtyczek albo z kafelka „zainstaluj”), każda z podglądem (`preview.render`) i kartą (`preview.card`, `card: true` w manifeście). Służą też jako przykłady kart dla autorów wtyczek.

- **`official.html-preview`** (`.htm`, `.html`): pełny podgląd jako Markdown: tytuł, opis z `meta`, nagłówki jako spis treści, tekst główny, listy linków i obrazów. Karta: tytuł strony, rozmiar, liczba linków/obrazów, akcja „Otwórz w przeglądarce panelu” (`file://`). *(Moja decyzja: bez renderowania strony — `kind: "web"` z projektu MCP jeszcze nie istnieje; gdy powstanie, wtyczka może przejść na niego.)*
- **`official.json-preview`** (`.json`, `.jsonc`, `.geojson`): pełny podgląd: walidacja (błąd z linią i kolumną), sformatowany JSON i zarys struktury (klucze, typy, liczności tablic). Karta: poprawny/błędny (status), rozmiar, typ korzenia i liczba elementów, kilka kluczy najwyższego poziomu. Nie przejmuje plików, które ma OpenAPI (niższy priorytet).
- **`official.openapi-preview`** (`openapi.json|yaml|yml`, `swagger.json|yaml|yml`, `*.openapi.json|yaml` — wzorce z `ExternalPreviewPlugin.Matches`; plik JSON/YAML z kluczem `openapi`/`swagger`): pełny podgląd: tytuł, wersja, serwery, endpointy pogrupowane po tagach (metoda, ścieżka, opis), schematy. Karta: tytuł i wersja API, liczba endpointów per metoda (wykres słupkowy), liczba schematów, akcja „Otwórz” (pełny podgląd).
- Uprawnienia: tylko `fs.read`. Bez `net`.
- Runtime i parser YAML zostawiam budowniczemu z warunkami: działa bez doinstalowywania pakietów (`pip`/`npm`/moduły PS); zależność dołączona do folderu wtyczki dozwolona, jeśli ma licencję zgodną z MIT, jest wymieniona w `catalog.json` z SHA-256 jak każdy plik i przechodzi `PluginSourceCheck`. Każda wtyczka ma własną kopię klienta SDK (test identyczności) i jest objęta `PluginCatalogTests` (po `Update-Catalog.ps1`). Instalacja z katalogu działa dopiero po wypchnięciu na `main`.
- Istniejący przykład `examples.project-modules` (moduły OpenAPI w folderze projektu) zostaje bez zmian.

### Testy

- `FolderConfig`: parsowanie nowych pól kafelka (karta, refresh, api, fields), odrzucenie złych wartości z czytelnym `Error`.
- Ścieżki JSON i ocena `status`/`chart` w podglądzie API; spłaszczanie odpowiedzi do listy pól dla kreatora.
- Podstawianie sekretów, w tym odmowa dla innego hosta.
- Zgoda: GET bez zgody; POST i SSH zablokowane do zaufania; zmiana treści pliku cofa zaufanie; kreator zaufa tylko plikowi, który był zaufany albo nowy.
- Parser wyjścia monitora SSH na zapisanych próbkach (Linux, inny system, brak `/proc`).
- `ExternalPluginTests`: `preview.card` w przykładzie na prawdziwym runtime; test identyczności kopii SDK; `PluginSourceCheck` czysty dla przykładów; `PluginCatalogTests` po `Update-Catalog.ps1`.
- `LocalizationTests` (nowe klucze).
- Historia przypiętego widoku: dashboard → dashboard → wstecz → Esc (lista plików właściwego folderu), cykl A → B → A, limit stosu; Esc i Shift+Enter działają także, gdy fokus jest w karcie lub w widoku wtyczki.
- Trzy wtyczki z katalogu przez `ExternalPluginTests` na prawdziwych runtime'ach, z próbkami plików (poprawny/błędny JSON, OpenAPI 3 YAML i Swagger 2 JSON, HTML z kodowaniem innym niż UTF-8).
- UI: weryfikacja headless (zrzut dashboardu z kartami w skórce modern i retro) według przepisu z CLAUDE.md; monitor SSH opcjonalnie na kontenerze `atmoz/sftp` z `C4K_TEST_SFTP`.

## Zostawione budowniczemu

- Dokładny układ i rozmiary kart, typografia, pusty stan karty, wygląd wykresu (siatka, podpisy osi).
- Podział na pliki i nazwy klas poza kontraktem SDK; czy `DashboardPreview` zastępuje `TilesPreview` dla dashboardu, czy go rozszerza (warunek: stare kafelki wyglądają jak dziś).
- Wewnętrzny mechanizm harmonogramu odświeżania i limitu współbieżności.
- Kolejność prac; sugerowana: kontrakt SDK + wykres → dashboard z kartami folder/galeria + odświeżanie → historia, wstecz i pasek nawigacji → karta dashboardu → API + sekrety → kreator → SSH → protokół zewnętrzny + SDK → trzy wtyczki z katalogu.
