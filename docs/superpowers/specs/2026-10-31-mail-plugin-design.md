# Poczta: wtyczka z katalogu, metody pocztowe w SDK, synchronizacja w tle

Data: 2026-10-31. Status: projekt do zatwierdzenia. Zależy od specyfikacji kart dashboardu (`2026-10-31-dashboard-cards-design.md`): karty (`IPreviewCardPlugin`, `preview.card`) i magazyn sekretów (`config/secrets.json`). **Ta specyfikacja musi być wdrożona w całości przed etapem kart poczty**, w tym obsługa `preview.card` we wtyczkach zewnętrznych (dziś niepodłączona). Plan poczty jej nie obejmuje.

## Po co

Użytkownik chce mieć w Commanderze folder `Poczta`, a w nim osobny podfolder na każde konto. Konfiguracja konta leży w `c4k.dir` tego podfolderu. Pocztę ma się łatwo przeglądać i łatwo na nią odpowiadać, bez wychodzenia z aplikacji. Na dashboardzie `Poczta/` każde konto jest kartą z liczbą nieprzeczytanych i czasem ostatniego sprawdzenia. Poczta sprawdza się w tle przez cały czas działania aplikacji.

## Cele

1. Wtyczka pocztowa **spoza wbudowanych**, w oficjalnym katalogu (`plugins-catalog/mail-…`), instalowana jak inne wtyczki z katalogu.
2. **Metody pocztowe w SDK.** IMAP i SMTP obsługuje aplikacja. Wtyczka woła je przez SDK, ma osobne uprawnienie, każde wywołanie trafia do audytu, a wtyczka nigdy nie widzi hasła.
3. Maile lądują na dysku jako pliki `.eml`, w folderach odwzorowujących skrzynkę na serwerze.
4. Podgląd maila z akcjami: Odpowiedz, Odpowiedz wszystkim, Przekaż, Usuń, Archiwizuj, Oznacz jako nieprzeczytany.
5. Pisanie odpowiedzi i nowych maili w podglądzie, z wysyłką przez SMTP.
6. Konfiguracja konta w `c4k.dir`: serwery, login, `refresh` (czy i co ile sprawdzać), `since` (jak daleko wstecz; puste = 3 miesiące).
7. Synchronizacja **w tle przez cały czas działania aplikacji**, na osobnym wątku. Wymaga ustawienia aplikacji i uprawnienia wtyczki. Nowa poczta daje powiadomienie.
8. Karta konta na dashboardzie: nieprzeczytane, ostatnie sprawdzenie, błąd połączenia.

## Poza zakresem (na razie)

- Gmail, Microsoft 365, OAuth/XOAUTH2. Na start tylko własny serwer IMAP z loginem i hasłem (decyzja użytkownika).
- POP3, Exchange/EWS, JMAP.
- Pisanie w HTML, podpisy HTML, szablony.
- S/MIME, PGP.
- Pełnotekstowy indeks poczty w SQLite. Wystarcza zwykłe szukanie po plikach (Alt+F7).
- Filtry i reguły.
- Limity rozmiaru załączników.
- Kilka komputerów synchronizujących ten sam folder `Poczta` (np. w udziale). Jeden folder konta obsługuje jeden komputer naraz (decyzja użytkownika). Plan może dodać prostą blokadę: plik `.mail/lock` z nazwą komputera i czasem ostatniego taktu. Inny komputer widzi wtedy na karcie „synchronizuje <komputer>” i nie zaczyna synchronizacji.
- Powiadomienia systemowe Windows (toast). Na start tylko powiadomienie w aplikacji.

## Czego nie wolno (anty-cele)

- Hasło nigdy nie trafia do `c4k.dir`, do pamięci wtyczki (`storage`) ani do jej procesu.
- Żadnego I/O na wątku UI. Wiszący serwer IMAP nie blokuje nawigacji, podglądu, kart ani zamykania aplikacji.
- Synchronizacja nie kasuje lokalnych maili tylko dlatego, że wypadły poza `since`. Lokalnie znika tylko to, co usunięto na serwerze.
- Wyświetlenie maila nie może niczego pobrać z sieci: obrazków zdalnych, pikseli śledzących, skryptów. HTML pokazujemy jako tekst z linkami.
- Treść maila (linki, załączniki) nie uruchamia niczego bez działania użytkownika. Linki idą tą samą drogą co dziś cele kafelków, z tymi samymi potwierdzeniami.
- Wtyczka nie omija SDK: żadnych własnych gniazd ani bibliotek IMAP. Musi przejść `PluginSourceCheck`.
- Praca w tle nie startuje bez zgody: wyłączone ustawienie albo niezatwierdzone uprawnienie oznacza zero połączeń, dopóki użytkownik sam nie otworzy konta.

## Ograniczenia z projektu (CLAUDE.md)

- Nowa metoda protokołu wymaga zmian w kilku miejscach:
  - `IPluginHost`/`ScopedPluginHost` (bramkowana i audytowana);
  - `ExternalPlugin.HandleRequestAsync`;
  - trzy klienty SDK, ich kopie w przykładach i w `plugins-catalog`, potem `Update-Catalog.ps1`;
  - tabela SDK w `docs/plugins.md`;
  - `docs/plugin.schema.json`: nowe uprawnienia i `contributes`.
- Core nie ma natywnego interopu. MailKit idzie do warstwy, która może mieć pakiety (patrz niżej). Wersja pakietu w `Directory.Packages.props`.
- Teksty przez `Loc.T`/`Loc.P` i `en.json`. Ustawienia w `AppSettings` + `SettingsCatalog`. Komendy w `CommandIds` + `RegisterCommands`.
- Wyjątki oczekiwane (anulowanie, timeout, odmowa logowania, zerwane połączenie) łapiemy w naszym kodzie. Nie mogą opuścić lambd `Task.Run` (debugger, „Just My Code”).
- Kolory tylko ze `SkinBrushes`. Układ w obu rodzinach skórek.

## Decyzje użytkownika

- Folder `Poczta`, w nim podfolder na każde konto, a konfiguracja konta w `c4k.dir` tego podfolderu.
- Wtyczka nie jest wbudowana.
- Metody pocztowe (IMAP/SMTP) dodajemy do SDK, nie ograniczamy się do API HTTP.
- Na start tylko własny serwer IMAP.
- Odpowiadanie: otwieram maila, a w jego podglądzie mam „Odpowiedz”, „Odpowiedz wszystkim” itd.
- W konfiguracji ustawia się, czy i co ile poczta ma się odświeżać, oraz jak daleko wstecz pobierać. Puste oznacza 3 miesiące.
- Odświeżanie działa w tle przez cały czas. Może to być dodatkowe ustawienie, z workerem na osobnym wątku.
- Karta na dashboardzie pokazuje liczbę nieprzeczytanych i czas ostatniego sprawdzenia.

## Projekt — moje decyzje (do sprawdzenia przy czytaniu)

### Przechowywanie: pliki, nie SQLite

Maile trzymamy w plikach `.eml` (RFC 822, bajt w bajt tak, jak przyszły z serwera), bo:

- zwykły panel daje przeglądanie, sortowanie, szukanie, kopiowanie i archiwizację bez dodatkowej pracy;
- plików nie trzeba synchronizować z osobną bazą;
- wtyczki nie mają dostępu do SQLite.

Układ:

```
Poczta/
  c4k.dir                      ← dashboard z kartami kont (dopisywane przez „Dodaj konto”)
  Firma/
    c4k.dir                    ← konto
    .mail/state.json           ← stan synchronizacji (UIDVALIDITY, ostatni UID, MODSEQ per folder, ostatnie sprawdzenie, błąd)
    Odebrane/
      2026-10-30 1412 Jan Kowalski - Oferta [uid 4711].eml
      …
    Wysłane/  Szkice/  Archiwum/  Kosz/  Projekty/Klient A/ …
```

- **Foldery odpowiadają skrzynce na serwerze**, łącznie z zagnieżdżeniem: separator IMAP zamieniamy na `\`.
  - Foldery specjalne, czyli INBOX i te ze znacznikami SPECIAL-USE (`\Sent`, `\Drafts`, `\Trash`, `\Archive`, `\Junk`), dostają polskie nazwy przez `Loc.T`.
  - Nazwy na dysku i nazwy na serwerze mapujemy w `.mail/state.json`. Lokalna nazwa nie musi więc być nazwą serwerową.
  - Znaki niedozwolone w Windows zamieniamy.
- **Nazwa pliku:** data i godzina, nadawca, temat (przycięty, bez znaków niedozwolonych), na końcu `[uid N]`. Tak sortowanie po nazwie daje kolejność chronologiczną, a UID łączy plik z serwerem.
- **Status przeczytania jest w nazwie pliku, nie w bazie.** Nieprzeczytany mail ma przedrostek `● ` (moja decyzja; zamiast `:2,S` z Maildira, bo `:` jest w Windows niedozwolony). Dzięki temu:
  - nieprzeczytane widać w zwykłym panelu;
  - liczba nieprzeczytanych to policzenie plików;
  - zmiana statusu to zmiana nazwy pliku.
  Flagę „oflagowany” trzymamy tylko w `state.json`.
- `.mail/` jest ukryty (atrybut Hidden), żeby nie zaśmiecał listy.

### Konto w `c4k.dir`

```jsonc
{
  "preview": { "plugin": "official.mail", "options": {
    "imap": "imap.mojadomena.pl:993",          // port 993 = TLS; 143 = STARTTLS
    "smtp": "smtp.mojadomena.pl:465",          // 465 = TLS; 587 = STARTTLS
    "user": "kamil@mojadomena.pl",
    "name": "Kamil Nowak",                     // nadawca w From
    "refresh": "5m",                           // brak/false = tylko przy otwarciu i „Sprawdź teraz”
    "since": "3m"                              // okres (d/m/y) albo data RRRR-MM-DD; puste = 3 miesiące
  } },
  "enter": "official.mail"
}
```

- Enter na folderze konta pokazuje stan konta: foldery z liczbą nieprzeczytanych, ostatnie sprawdzenie, błąd, „Sprawdź teraz”. Esc lub Shift+Enter daje zwykłą listę plików (zasada z dashboardu).
- Wtyczka dostaje te opcje przez `options` w `preview.render` / `preview.card`, tak jak dziś.
- `refresh` ma minimum 1 minutę. Krótsze wartości podnosimy do 1 minuty.

### Hasła

- Hasło trzymamy w magazynie sekretów z karty API (`SecretStore`, `config/secrets.json`, DPAPI). Klucz to `mail:<plugin id>:<user>@<host imap>`. **Konto jest przypięte do wtyczki:** inna wtyczka z uprawnieniem `mail` nie użyje cudzego hasła, tylko dostanie własne okno logowania. *(Moja decyzja.)* Ten sam klucz obsługuje SMTP, a osobne hasło SMTP można dodać później, jeśli zajdzie potrzeba.
- Brak hasła albo odmowa logowania: aplikacja sama pokazuje `LoginDialog` (login wypełniony) i zapisuje hasło. Wtyczka dostaje tylko błąd `authRequired`, a hasła nie widzi.
- W tle okno logowania **nie wyskakuje**. Konto przechodzi w stan „wymaga logowania” (widoczny na karcie), a okno pojawia się dopiero po otwarciu konta lub kliknięciu karty.
- Sekret jest przypięty do hosta IMAP/SMTP, jak w karcie API. Jeśli ktoś podmieni w `c4k.dir` serwer na obcy, hasło tam nie poleci, tylko pojawi się prośba o nowe logowanie.

### Metody SDK (`mail.*`)

Nowe uprawnienie `mail`. Wtyczka podaje konto jako `{ imap, smtp, user }` z `options`. Host sam szuka hasła i trzyma pulę połączeń.

| Metoda | Co robi |
|---|---|
| `mail.folders(account)` | lista folderów: nazwa serwerowa, separator, znacznik SPECIAL-USE, UIDVALIDITY, liczba wiadomości i nieprzeczytanych |
| `mail.sync(account, folder, { uidValidity, lastUid, modSeq, since })` | zmiany od ostatniego stanu: nowe UID-y z nagłówkami, zmienione flagi, usunięte UID-y (CONDSTORE/QRESYNC, gdy serwer je ma; inaczej porównanie listy UID-ów z zakresu `since`) |
| `mail.fetch(account, folder, uid, targetPath)` | **host sam zapisuje** surową wiadomość do `targetPath`, sprawdzając `fs.write` wtyczki dla tej ścieżki; duże wiadomości nie przechodzą przez JSON-RPC |
| `mail.setFlags(account, folder, uids, add, remove)` | `\Seen`, `\Flagged`, `\Answered` |
| `mail.move(account, folder, uids, targetFolder)` / `mail.delete(...)` | MOVE (albo COPY + `\Deleted` + EXPUNGE); delete = przeniesienie do Kosza, a w Koszu usunięcie na stałe |
| `mail.parse(path)` | MIME z pliku `.eml`: nagłówki, treść tekstowa (HTML zamieniony na tekst z linkami, bez pobierania czegokolwiek), lista załączników; wymaga `fs.read` |
| `mail.saveAttachment(path, index, targetPath)` | zapis załącznika; wymaga `fs.read` + `fs.write` |
| `mail.compose(path, { to, cc, bcc, subject, body, inReplyTo, references, attachments[] })` | buduje `.eml` szkicu (MimeKit) i zapisuje go pod `path` |
| `mail.send(account, draftPath)` | wysyła szkic przez SMTP, wrzuca kopię do serwerowego folderu Wysłane (APPEND), chyba że serwer robi to sam; zwraca `Message-ID` |

- Parsowanie i budowanie MIME robi host (MimeKit), a nie wtyczka. Dzięki temu wtyczka jest prosta, a zamiana HTML na bezpieczny tekst dzieje się w jednym, testowanym miejscu.
- Audyt zapisuje metodę, konto, folder i liczbę UID-ów. Treści i hasła nie trafiają do dziennika.
- Połączenia: najwyżej 2 na konto, z wygaszaniem po 5 minutach bezczynności. Każda operacja ma limit czasu (30 s, a `fetch`/`send` rosną z rozmiarem) i jest anulowalna.
- Gdzie mieszka kod: Core nie może mieć natywnego interopu, a MailKit jest czystym .NET. Klient poczty (`MailService`) mieści się więc w `Commander4K.Plugins.BuiltIn/Mail`, obok SFTP/FTP, a `ScopedPluginHost` dostaje go jak `ProcessLookups`. Ostateczne miejsce wybierze budowniczy, zgodnie z warstwami.
- Certyfikaty TLS: domyślnie sprawdzane przez system. Jeśli certyfikat jest nieważny lub samopodpisany, pokazujemy potwierdzenie z odciskiem i zapamiętujemy zgodę (trust-on-first-use, jak `KnownHostsStore`).

### Praca w tle (nowość dla wszystkich wtyczek)

- Nowe ustawienie aplikacji: **„Wtyczki mogą działać w tle”** (`AppSettings.PluginBackground`, w `SettingsCatalog`). Domyślnie włączone, ale bez znaczenia, dopóki żadna wtyczka nie ma zatwierdzonego uprawnienia.
- Nowe uprawnienie wtyczki `background` i nowy wpis w manifeście `contributes.background: { "interval": "1m" }`.
- `PluginBackgroundService` (App/Composition) działa na **osobnym wątku**: dedykowany `Thread` z własną pętlą async, a nie zadanie z puli. Co `interval` woła w każdej włączonej wtyczce z tym wpisem metodę `background.tick`, z limitem czasu i anulowaniem. Kolejny takt nie startuje, póki poprzedni trwa. Wtyczka startuje w tym momencie, jeśli jeszcze nie działała.
- Wtyczka sama decyduje, które konta są do sprawdzenia: według `refresh` każdego konta i czasu ostatniego sprawdzenia.
- **Skąd wtyczka zna konta:** trzyma listę folderów kont w swoim `storage` (`accounts: [path…]`). Konto trafia tam:
  - gdy użytkownik pierwszy raz otworzy podgląd lub kartę konta;
  - przez komendę „Dodaj konto pocztowe”.
  Przy każdym takcie wtyczka czyta `c4k.dir` z tej ścieżki przez `fs.read`. Konto, którego folder zniknął, jest usuwane z listy.
- Powiadomienia idą przez istniejące `ui.notify`. Wtyczka grupuje je: jedno na takt, w stylu „Firma: 3 nowe wiadomości”. Na razie bez toastów systemowych. Klikalny cel powiadomienia (np. Odebrane konta) dochodzi tylko wtedy, gdy `ui.notify` go dziś nie ma.
- **Takty są krótkie, bo klient PowerShell obsługuje jedno żądanie naraz.** Długi takt zablokowałby podgląd, karty i wysyłkę. Dlatego jeden takt robi ograniczoną porcję pracy, np. najwyżej 50 pobrań albo 15 s, i kończy się. Kolejny takt zaczyna tam, gdzie poprzedni przerwał, a stan siedzi w `state.json`. Podczas zaległej pierwszej synchronizacji host woła takty co 2 s zamiast co `interval`. Wtyczka sygnalizuje to wynikiem `{ "more": true }`. *(Moja decyzja.)*
- Zamykanie aplikacji anuluje takt. Zamykanie nie czeka na serwer dłużej niż 2 s.

### Przebieg synchronizacji (wtyczka)

1. `mail.folders` → utworzenie lub zmiana nazw lokalnych folderów i aktualizacja mapy w `state.json`.
2. Dla każdego folderu: `mail.sync` od zapisanego stanu.
   - Zmiana UIDVALIDITY: przebudowa folderu. Lokalne pliki z nieznanymi już UID-ami zostają z dopiskiem `[stary]` (nic nie kasujemy po cichu). Moja decyzja, do sprawdzenia.
3. Nowe UID-y z zakresu `since` → `mail.fetch` do pliku.
4. Zmienione flagi → zmiana nazwy pliku (`● `).
5. UID-y usunięte na serwerze → usunięcie pliku.
6. **Na serwer trafiają tylko jawne akcje z podglądu maila** (decyzja użytkownika). Usuń, Archiwizuj, Przenieś, Oznacz jako nieprzeczytany, Wyślij i otwarcie Enterem (`\Seen`) wykonują się od razu przez `mail.*`. Zwykłe F8/F6 na plikach zmienia tylko kopię lokalną. Plik, którego brakuje przy znanym UID, następna synchronizacja pobiera ponownie. Synchronizacja **nigdy** nie wnioskuje operacji na serwerze z braku pliku.
7. Zapis `state.json` po każdym folderze, żeby przerwanie niczego nie gubiło.

Pierwsza synchronizacja dużej skrzynki może trwać długo. Działa w tle, a karta pokazuje postęp, np. „pobieram 120 / 3400”.

### Podgląd maila (`.eml`)

- Wtyczka deklaruje podgląd dla `*.eml` (`contributes.previews`, `extensions: [".eml"]`) z priorytetem wyższym niż tekst.
- Widok czytania:
  - nagłówek: od, do, DW, temat, data;
  - treść jako tekst z klikalnymi linkami;
  - lista załączników z akcjami „Otwórz” i „Zapisz do drugiego panelu”;
  - u góry pasek akcji: Odpowiedz, Odpowiedz wszystkim, Przekaż, Archiwizuj, Usuń, Oznacz jako nieprzeczytany.
  Skróty dobiera budowniczy z `DefaultKeymap`. Działają tylko wtedy, gdy podgląd maila ma fokus.
- Mail oznacza się jako przeczytany (`\Seen` od razu i zmiana nazwy pliku), gdy otworzysz go Enterem, czyli w przypiętym podglądzie. Podgląd, który podąża za kursorem (Ctrl+Q), nic nie oznacza, więc przewijanie listy nie oznaczy wszystkiego po drodze. Host przekazuje wtyczce w `preview.render`, czy podgląd jest przypięty. *(Moja decyzja.)*
- **Potrzebne rozszerzenie podglądów (ogólne, nie pocztowe):** dziś `PreviewContent` nie ma przycisków ani formularzy. Dodajemy dwa ogólne rodzaje, użyteczne też dla innych wtyczek:
  - `actions` przy dowolnym podglądzie: lista `{ label, target }`, rysowana przez host jako pasek. Cele jak w `CardAction`, plus nowe `c4k:plugin/<akcja>?…`, które wraca do wtyczki jako `preview.action`.
  - `FormPreview`: pola jednoliniowe (Do, DW, UDW, Temat), jedno pole wielowierszowe (treść), lista plików (załączniki) i przyciski. Host rysuje to ze skórki, wysyła zmiany do wtyczki (`preview.formChanged`, debounce) i zgłasza kliknięcia przycisków (`preview.action`).
  Dokładny kształt obu typów ustala budowniczy w stylu istniejących rekordów `PreviewContent` i protokołu `preview.render`.

### Pisanie i odpowiadanie

- „Odpowiedz” / „Odpowiedz wszystkim” / „Przekaż”:
  1. Wtyczka woła `mail.compose`, co tworzy szkic w lokalnym `Szkice/`. Szkic ma wypełnione Do/DW, temat `Re:`/`Fwd:`, nagłówki `In-Reply-To`/`References` i cytat pod spodem z `> `.
  2. Podgląd przełącza się na ten szkic w trybie `FormPreview`.
- „Nowa wiadomość”: komenda wtyczki i przycisk w widoku konta.
- Szkic zapisuje się sam (`preview.formChanged` → `mail.compose` nadpisuje plik). Na serwer, do folderu Drafts, trafia przy takcie (APPEND, a stara wersja jest usuwana).
- Załączniki:
  - przycisk „Dołącz zaznaczone” bierze zaznaczenie z drugiego, widocznego panelu (kontekst wtyczki, jak dziś);
  - przycisk „Usuń załącznik” na liście.
- **Wyślij** → `mail.send`. Kopia trafia do Wysłanych, oryginał dostaje `\Answered`, szkic znika lokalnie i z serwera. Podgląd wraca do maila, na który odpowiadaliśmy.
- Błąd wysyłki: szkic zostaje, a podgląd pokazuje komunikat od serwera.

### Karta konta i dashboard `Poczta/`

- Karta (`preview.card`, z `IPreviewCardPlugin` z poprzedniej specyfikacji):
  - nieprzeczytane w Odebranych duży, a w pozostałych folderach mały (pomijamy Kosz i Spam);
  - „sprawdzono 12:41” albo „błąd: …” / „wymaga logowania”;
  - status Ok / Warning / Error;
  - akcje „Otwórz” (Odebrane) i „Sprawdź teraz”.
- Karta **czyta tylko pliki i `state.json`**, bez łączenia się z serwerem. Dzięki temu jest szybka i działa offline. Gdy dashboard jest otwarty, odświeża się przez `refresh` kafelka z poprzedniej specyfikacji, np. co 30 s.
- „Sprawdź teraz” = `c4k:plugin/check?account=…`, czyli jeden takt dla tego konta poza kolejką.

### Komenda „Dodaj konto pocztowe” (wtyczki)

- Uruchamiasz ją w folderze, który ma być folderem `Poczta`. Jest na dysku lub w udziale.
- Pyta o adres, serwer IMAP i SMTP. Serwery podpowiada z domeny: `imap.<domena>` / `mail.<domena>`.
- Hasło podaje się w oknie logowania aplikacji.
- Sprawdza logowanie przez `mail.folders`.
- Tworzy podfolder z `c4k.dir` i dopisuje kafelek-kartę do `c4k.dir` dashboardu `Poczta/`, tworząc go, jeśli go nie ma.
- Dodaje konto do listy w `storage` i uruchamia pierwszą synchronizację.
- Pytania zadaje przez istniejące `ui.prompt`.

### Język i miejsce wtyczki

- `plugins-catalog/mail-powershell/`, z id `official.mail`. Moja decyzja: PowerShell jest na każdym Windowsie, a katalog ma już wtyczki w PowerShell. Ciężką pracę (IMAP, MIME, HTML) robi host, więc język wtyczki nie przeszkadza.
- Uprawnienia: `mail`, `fs.read`, `fs.write`, `background`.

## Testy (wskazówki)

- Jednostkowe testy hosta:
  - mapowanie folderów i nazw plików;
  - zamiana HTML na tekst: brak zdalnych zasobów, linki zachowane;
  - `compose` dla odpowiedzi: nagłówki, cytat;
  - bramkowanie `mail.*` i audyt bez treści.
- Integracyjne na prawdziwym serwerze, gdy ustawione jest `C4K_TEST_IMAP`, jak `RemoteServerTests`. Np. docker `greenmail/standalone` (IMAP + SMTP). Testy obejmują pełny cykl: sync, fetch, flagi, move, delete, send i APPEND do Wysłanych.
- `ExternalPluginTests` dla wtyczki z katalogu na prawdziwym PowerShell, z serwerem z dockera, gdy jest dostępny.
- Test `PluginSourceCheck`: wtyczka jest czysta.

## Zostawione budowniczemu

- Dokładny kształt rekordów `actions`/`FormPreview` i ich protokołu.
- Wybór biblioteki HTML→tekst (np. HtmlAgilityPack lub własny prosty parser) w ramach anty-celów.
- Skróty klawiszowe w podglądzie maila.
- Kolejność i granice etapów wdrożenia. Sugeruję:
  1. SDK `mail.*` + `MailService` + testy na greenmail;
  2. praca w tle + `ui.notify`;
  3. `actions` + `FormPreview`;
  4. wtyczka (sync, podgląd, pisanie);
  5. karta i „Dodaj konto”.
