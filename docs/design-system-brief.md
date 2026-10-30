# Brief dla projektanta: design system Commander4K

Zaprojektuj design system dla **Commander4K**. To dwupanelowy menedżer plików dla Windows 11, w tradycji Norton Commandera, Midnight Commandera i Total Commandera. Jest open source: https://github.com/4kprojekt/Commander4K

Zanim zaczniesz, przeczytaj, w jakiej technologii powstaje interfejs. To mocno wyznacza, co da się zbudować, a czego nie.

## 1. Technologia interfejsu

- **Avalonia UI 12** (wieloplatformowy framework .NET), uruchamiany tylko na Windows 11 (x64 i ARM64), na **.NET 10**.
- **Interfejs jest pisany w C#, bez XAML.** Kontrolki powstają w kodzie (biblioteka Avalonia.Markup.Declarative). Nie ma plików stylów ani szablonów graficznych. Wszystko, co zaprojektujesz, musi dać się zapisać jako wartości: kolory, rozmiary, odstępy, promienie, kroje pisma, grubości linii.
- **Rendering przez Skia** (GPU). Rysowanie jest szybkie i ostre na ekranach 4K i przy skalowaniu Windows 100–250%.
- **Standardowe kontrolki** pochodzą z motywu **Avalonia Fluent** (jasny albo ciemny): przyciski, pola tekstowe, listy rozwijane, przełączniki, menu, menu kontekstowe, paski przewijania, okna dialogowe. Zmienisz ich kolory, rozmiary, promienie i odstępy. Nie zmienisz ich struktury, chyba że opiszesz nową kontrolkę.
- **Lista plików nie składa się z kontrolek.** Rysuje ją jedna kontrolka „owner-draw” (`FileViewportCanvas`), która maluje tylko widoczne wiersze, bo katalog może mieć 500 000 pozycji. W wierszu da się narysować:
  - prostokąty tła, także zaokrąglone, i linie;
  - tekst jednym krojem pisma, w dwóch grubościach (normalna i pogrubiona), z wielokropkiem przy obcięciu;
  - proste ikony wektorowe (ścieżki, kształty);
  - kolor tekstu zależny od typu pozycji.

  Nie da się w wierszu umieścić kontrolek (przycisków, pól). Złożone efekty (cienie, gradienty, przezroczystość) są możliwe, ale muszą być tanie, bo ekran przerysowuje się przy każdym ruchu kursora.
- **Okno przeglądarki** (Microsoft Edge WebView2) jest natywnym oknem osadzonym w panelu i nie da się go stylować. Projektujesz tylko jego pasek narzędzi.
- **Pasek tytułu okna** jest systemowy (Windows 11).
- **Ikony.** Dziś przyciski i palety używają emoji (Segoe UI Emoji), a lista plików prostych kształtów folderu i pliku. Możesz zaproponować zestaw ikon:
  - najlepiej jako ścieżki SVG (dają się zamienić na geometrię Avalonii);
  - albo w oparciu o **Segoe Fluent Icons**, czcionkę wbudowaną w Windows 11.

  Bitmapy odpadają.
- **Animacje.** Interfejs ma działać natychmiast: zero blokad, pierwsze pliki widać po 1 ms. Proponuj tylko krótkie przejścia (do ~120 ms) i tylko tam, gdzie pomagają: podświetlenia, rozwijanie. Nigdy na liście plików.

## 2. Obecny model skórek — Twoje tokeny muszą się na niego mapować

Aplikacja ma **skórki** jako pliki JSON. Wbudowane są: Nowoczesna ciemna, Nowoczesna jasna, Norton Commander, Midnight Commander i Total Commander (klasyczna). Użytkownicy tworzą własne, także z pomocą AI. Schemat JSON (`config/skins/skin.schema.json`) jest generowany z kodu. Skórka zawiera trzy grupy tokenów.

**`palette`**: kolory w formacie `#RRGGBB` albo `#AARRGGBB`.

| Token | Gdzie jest używany |
|---|---|
| `windowBackground` | tło okna, odstępy między obszarami |
| `panelBackground` | tło paneli plików i okien narzędziowych |
| `panelBorder` | ramki paneli, linie oddzielające |
| `headerBackground` / `headerForeground` | nagłówki kolumn, paski ścieżki, nagłówki sekcji |
| `fileForeground` | zwykłe pliki |
| `directoryForeground` | foldery, dyski, klucze rejestru |
| `hiddenForeground` | pliki ukryte i systemowe |
| `executableForeground` | .exe, .msi, .ps1, .bat… |
| `archiveForeground` | .zip, .7z, .tar.gz… |
| `networkForeground` | hosty, udziały sieciowe, akcje typu „+ Dodaj połączenie” |
| `selectedForeground` | pliki zaznaczone (Insert / Spacja / prawy przycisk) |
| `cursorBackground` / `cursorForeground` | wiersz kursora w aktywnym panelu |
| `inactiveCursorBackground` | wiersz kursora w nieaktywnym panelu (rysowany z obwódką) |
| `statusBackground` / `statusForeground` | pasek stanu, pasek zadań w tle, palety |
| `commandLineBackground` / `commandLineForeground` | linia poleceń pod panelami |
| `functionKeyNumberBackground` / `…Foreground` | numery F1–F10 na dolnym pasku |
| `functionKeyLabelBackground` / `…Foreground` | opisy klawiszy F na dolnym pasku |
| `accent` | akcenty: aktywne elementy, ikony palet, nagłówki ustawień |
| `progress` | paski postępu kopiowania |
| `error` | błędy, ostrzeżenia |

**`typography`**:
- `fontFamily` to lista zapasowa, np. `"Cascadia Mono, Consolas"` albo `"Segoe UI Variable Text, Segoe UI"`;
- `fontSize` w pikselach, domyślnie 13;
- `rowHeightFactor` to wysokość wiersza listy jako wielokrotność rozmiaru pisma, domyślnie 1.6.

**`chrome`**:
- `borderStyle`: `None`, `Single` albo `Double` (podwójna linia jak w Nortonie);
- `cornerRadius`;
- `showIcons`;
- `showColumnHeaders`;
- `uppercaseNames` (wygląd DOS);
- `themeVariant`: `Dark` albo `Light`, czyli wariant Fluent dla standardowych kontrolek.

Aplikacja **sprawdza kontrast WCAG** każdej skórki i ostrzega, gdy tekst jest za mało czytelny. Cel to co najmniej 4.5:1 dla tekstu na liście plików i 3:1 dla elementów pomocniczych.

Jeśli Twój design system potrzebuje więcej tokenów (np. odstępy, osobny kolor hover, kolor fokusu, powierzchnie drugiego poziomu, cienie), **wypisz je osobno jako „nowe tokeny”**: nazwa, typ, wartość domyślna, gdzie są używane. Dodamy je do modelu skórek. Każdy nowy token musi mieć sensowną wartość dla wszystkich skórek, także retro.

## 3. Obszary interfejsu do zaprojektowania

1. **Okno główne**:
   - pasek menu (Plik, Polecenia, Panele, Wtyczki, Konfiguracja, Pomoc);
   - dwa panele plików, każdy z kartami (`Ctrl+T`), polem ścieżki, nagłówkiem kolumn, listą i paskiem stanu („9 plików (54 KB), 10 katalogów”);
   - pasek zadań w tle (kopiowanie z paskiem postępu i przyciskiem anulowania);
   - linia poleceń;
   - dolny pasek klawiszy F1–F10.
2. **Lista plików.** Kolumny to Nazwa, Rozmiar i Data plus opcjonalne kolumny wtyczek (Git: „zmieniony”, „nowy”, „ignorowany”; wymiary zdjęcia, data wykonania, aparat; SHA-256). Do zaprojektowania:
   - stany wiersza: zwykły, kursor w panelu aktywnym i nieaktywnym, zaznaczony, kursor na zaznaczonym, ukryty, folder, plik wykonywalny, archiwum, wiersz „..” (`<W GÓRĘ>`);
   - stany listy: wczytywanie (szkielet i pasek „Wczytywanie… [Esc – Anuluj]”), błąd, pusty folder;
   - ikony typu pozycji.
3. **Tryby panelu.** Zamiast listy plików panel może pokazywać jeden z trybów, a tryb otwiera się w panelu obok listy:
   - **Panele wtyczek** (informacje o systemie, menedżer zadań, analiza logów, katalog poleceń PowerShell, menedżer wtyczek): pole filtra, przyciski akcji, tabela-drzewo z poziomami ważności wierszy (zwykły, wyciszony, wyróżniony, ostrzeżenie, błąd), panel szczegółów, pasek stanu.
   - **Szybki podgląd** (`Ctrl+Q`): Markdown renderowany natywnie (nagłówki, listy, tabele, cytaty, bloki kodu), edycja obok podglądu na żywo, tekst, obraz, rozmiary podfolderów z paskami.
   - **Przeglądarka:** pasek z przyciskami wstecz/dalej/odśwież, polem adresu, wyborem wyszukiwarki i przyciskiem „✨ Zapytaj AI”.
4. **Palety:** paski przycisków dokowane po lewej, po prawej, na dole albo wąsko między panelami (ok. 80 px: ikona nad podpisem).
   - Rodzaje: szybkie akcje, ulubione foldery, zapisane widoki, ostatnie lokalizacje, palety wtyczek.
   - Każda ma tytuł, przyciski akcji („+”), przycisk zamknięcia ✕, uchwyt do zmiany rozmiaru i menu kontekstowe pozycji.
5. **Okna dialogowe:**
   - potwierdzenia;
   - wpisywanie tekstu;
   - kopiowanie i przenoszenie (cel, nadpisywanie, kolejka);
   - logowanie (SMB, SFTP);
   - ostrzeżenie o kluczu SSH;
   - lista wyboru z filtrem (paleta komend `Ctrl+Shift+P`, wybór trybu panelu F2);
   - aktualizacja;
   - „O programie”.
6. **Okno ustawień** (F1): wyszukiwarka u góry, sekcje (Język, Wygląd, Panele, Palety, Kolumny, Podgląd, Sieć, Przeglądarka, Aktualizacje, Skróty klawiszowe, Pliki konfiguracyjne). Każdy wiersz ma tytuł, opis i kontrolkę po prawej (przełącznik, lista albo pole tekstowe).
7. **Wybór skórki** z podglądem na żywo i generator skórek z AI.
8. **Okna narzędziowe:** podgląd F3 (tekst lub zrzut szesnastkowy) i wyszukiwanie Alt+F7 (wyniki strumieniowo).

## 4. Zasady, których design system musi przestrzegać

- **Klawiatura przede wszystkim.**
  - Wszystko działa z klawiatury: Tab przełącza panele, a klawisze F, `Ctrl` i `Alt` uruchamiają komendy.
  - Fokus i aktywny panel muszą być widoczne na pierwszy rzut oka. Aktywny panel ma wyróżniony pasek ścieżki i pełny kursor, nieaktywny tylko obwódkę kursora.
- **Gęstość.** To narzędzie do pracy na tysiącach plików. Wiersz listy ma ok. 20–24 px przy piśmie 13 px. Zaproponuj warianty gęstości (kompaktowy, domyślny, wygodny) jako wartości `fontSize` i `rowHeightFactor`.
- **Czytelność kolorów typów plików.** Foldery, pliki, pliki wykonywalne, archiwa, ukryte i zaznaczone muszą się różnić także dla osób z zaburzeniami widzenia barw. Nie polegaj na samym kolorze: pomaga ikona i pogrubienie folderów.
- **Dwa języki interfejsu.** Polski jest językiem źródłowym, angielski wbudowanym, a kolejne języki dodają użytkownicy. Polskie teksty bywają o 20–40% dłuższe od angielskich. Przyciski, kolumny i palety muszą to znieść: obcinają tekst z wielokropkiem, nie łamią układu.
- **Skórki retro zostają.** Norton i Midnight Commander (niebieskie tło, pismo o stałej szerokości, podwójne ramki, wielkie litery) muszą dać się wyrazić tymi samymi tokenami co nowoczesny design. Design system nie może zakładać, że wszystko jest zaokrąglone i jasne.
- **Jasny i ciemny wariant** dla każdej nowoczesnej skórki.
- **Bez przeźroczystości okien** (Mica, Acrylic): nie są dziś obsługiwane. Jeśli je proponujesz, oznacz je jako opcjonalne.

## 5. Czego oczekuję

1. **Krótki opis kierunku wizualnego**, 3–5 zdań: charakter, inspiracje, czym ma się różnić od Eksploratora Windows i od Total Commandera.
2. **Tokeny** w tabeli: nazwa, wartość dla wariantu ciemnego i jasnego, zastosowanie. Najpierw tokeny z sekcji 2, potem osobno „nowe tokeny”. Do tego skala odstępów, promieni, grubości linii i typografii (rozmiary, grubości, wysokości wierszy).
3. **Gotowe skórki jako JSON** zgodne ze schematem z sekcji 2 (`id` małymi literami z myślnikami, `displayName`, `palette`, `typography`, `chrome`), co najmniej ciemna i jasna. Mogę je od razu wczytać do aplikacji (`config/skins/<id>.json`) i ocenić na żywo.
4. **Specyfikację komponentów** z sekcji 3: wymiary, stany (zwykły, hover, wciśnięty, fokus, wyłączony, aktywny/nieaktywny panel), użyte tokeny.
5. **Zestaw ikon:** co najmniej typy plików, akcje palet, przyciski paneli i menu, jako ścieżki SVG (siatka 16 i 20 px, linia albo wypełnienie, spójnie) albo jako mapowanie na glify Segoe Fluent Icons.
6. **Makiety:** okno główne (ciemne i jasne), panel wtyczki, szybki podgląd Markdown, okno ustawień, jedna paleta w każdym położeniu.

Jeśli jakiś pomysł wymaga zmian w kodzie (np. nowy token albo nowy rodzaj kontrolki), opisz go wprost. Oceniam koszt i decyduję, czy wchodzi do pierwszej wersji.
