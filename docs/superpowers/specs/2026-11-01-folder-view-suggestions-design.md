# Podpowiedzi widoku folderu (folder view suggestions)

Data: 2026-11-01. Status: projekt do akceptacji.

## Po co

Użytkownik stoi kursorem na folderze pełnym zdjęć i widzi tylko listę nazw. Galeria już istnieje (`FolderViewIds.Gallery`),
ale da się ją włączyć tylko przez ręczny `c4k.dir` albo `cm_FolderView` — trzeba wiedzieć, że jest. Aplikacja ma sama
zauważyć, że zawartość folderu pasuje do jakiegoś podglądu, i dyskretnie zaproponować przełączenie. Po przełączeniu ma
jasno pokazać, jak wrócić do zwykłej listy plików (w stylu driver.js: przygaszone tło, podświetlony element, dymek).

Galeria jest pierwszym przypadkiem, ale **reguła ma być uniwersalna i należeć do wtyczki podglądu**: każdy podgląd
(wbudowany lub zewnętrzny) sam ocenia, czy folder do niego pasuje.

## Cele

1. Kursor zatrzymany na folderze → w tle ocena przez wszystkie podglądy, które to umieją → najlepsza propozycja jako małe,
   nieblokujące powiadomienie w tym panelu: „Ten folder zawiera 42 zdjęcia. Przełączyć w widok galerii?”.
2. Akceptacja **zapisuje `c4k.dir`** w folderze (decyzja użytkownika) i od razu otwiera widok (jak Enter na folderze
   z `c4k.dir`).
3. Po otwarciu: jednorazowy przewodnik w stylu driver.js wskazujący, gdzie wrócić do listy plików.
4. Wtyczki zewnętrzne: **deklaratywna reguła w `plugin.json` + opcjonalne dopytanie procesu wtyczki dopiero po
   dopasowaniu reguły** (decyzja użytkownika). Proces wtyczki nigdy nie startuje tylko dlatego, że kursor przejechał
   po folderze, który nie pasuje do jej reguły.

## Poza zakresem

- Podpowiedzi dla plików (tylko foldery, dyski/udziały nie).
- Automatyczne przełączanie bez pytania.
- Podpowiedzi tam, gdzie `c4k.dir` nie działa: SFTP/FTP, archiwa, rejestr, `net://`, `local://` — tylko `file` i `smb`
  (ta sama reguła co `ConfigureFolderViewAsync`).
- Przewodnik po całej aplikacji — tylko ten jeden dymek „tu wrócisz”.

## Anty-cele (porażka, nawet jeśli działa)

- Powiadomienie zabiera klawisze albo fokus — strzałki/Enter/litery muszą działać jak dotąd.
- Zalewanie dymkami przy przewijaniu listy.
- Jakiekolwiek I/O na wątku UI albo zauważalne przycięcie nawigacji; ocena wisząca na wolnym udziale SMB.
- Uruchamianie procesów wtyczek zewnętrznych przy każdym folderze.
- Pisanie do folderu użytkownika czegokolwiek poza `c4k.dir` po wyraźnej akceptacji (odmowy nie trafiają do folderu).

## Jak to działa

### 1. Kontrakt w SDK (`Commander4K.PluginSdk`)

Nowy opcjonalny interfejs obok `IPreviewPlugin`:

```csharp
/// A preview that can tell whether a whole folder suits it.
public interface IFolderSuggestingPreview : IPreviewPlugin
{
    Task<FolderSuggestion?> SuggestAsync(FolderSnapshot folder, IPluginHost host, CancellationToken cancellationToken);
}

/// One bounded listing shared by every candidate (no plugin lists the folder itself for this).
public sealed record FolderSnapshot(VfsEntry Folder, IReadOnlyList<VfsEntry> Entries, bool Truncated);

/// Score 0..1; Message is the hint text (already localized by the plugin); View is what goes into c4k.dir "enter".
public sealed record FolderSuggestion(double Score, string Message, FolderSuggestionView View);
```

`FolderSuggestionView` = albo wbudowany widok (`"gallery"`, `"notes"`…), albo `{ plugin, preview, options? }` — dokładnie
to, co `FolderConfig` umie zapisać jako akcję `enter`. `null` z `SuggestAsync` = „nie pasuję”.

### 2. Reguła w manifeście (wtyczki zewnętrzne)

`contributes.previews[]` dostaje opcjonalne `suggest`:

```json
"suggest": {
  "files": ["*.jpg", "*.png", "*.webp"],   // wzorce jak w "extensions" (ExternalPreviewPlugin.Matches)
  "minCount": 5,                            // co najmniej tyle pasujących plików
  "minShare": 0.6,                          // udział pasujących wśród plików (bez folderów)
  "contains": [".obsidian"],                // alternatywnie/dodatkowo: wpis o tej nazwie musi istnieć
  "message": { "pl": "…{count}…", "en": "…{count}…" },
  "ask": false                              // true = po dopasowaniu zapytaj proces: preview.suggest
}
```

- Host sam sprawdza regułę na `FolderSnapshot` (czysta funkcja w Core, testowalna).
- Przy `Truncated` reguła liczy się na częściowej liście (`minCount` i `minShare` na tym, co przeczytano) —
  udokumentować w `docs/plugins.md`. *(decyzja projektanta)*
- `ask: false` → propozycja ze stałym wynikiem **0,7**, tekst z `message` (`{count}` = liczba pasujących).
  `ask: true` → wynik podaje wtyczka. *(decyzja projektanta)*
- `ask: true` → dopiero po dopasowaniu host wysyła `preview.suggest` `{ preview, folder, entries, truncated }`;
  odpowiedź `{ score, message, options? }` albo `null`. Uruchomienie jeszcze niedziałającego procesu jest dozwolone
  i ma osobny, dłuższy limit (~5 s), poza budżetem 1,5 s: jeśli kursor nadal stoi na folderze, karta może pojawić się
  później albo zostać podmieniona na lepszą propozycję. Błąd/timeout = brak propozycji. *(decyzja projektanta)* Wywołanie przez `ScopedPluginHost`/`ExternalPlugin.HandleRequestAsync`, audytowane.
- Wtyczka **niezatwierdzona lub wyłączona nie bierze udziału** (ani reguła, ani proces).
- Dokumentacja: `docs/plugins.md` (tabela protokołu + krótki przewodnik), `docs/plugin.schema.json`, `PluginManifest`
  (`PreviewContribution` + nowy rekord), helpery w trzech klientach SDK i kopie w przykładach (test identyczności).

### 3. Wbudowane podglądy

- **Galeria** (nowy wbudowany podgląd-sugestor albo `FolderPreviewPlugin`): ≥ 5 obrazów i ≥ 60% plików to obrazy
  (rozszerzenia jak w `FolderViews.GalleryAsync`). Komunikat: `Loc.P(n, "…{0} zdjęcie…|…zdjęcia…|…zdjęć…")`.
- **Notatki**: tylko z migawki, bez chodzenia po rodzicach (`FindVaultRootAsync` nie jest używane — inaczej każdy
  podfolder vaulta by podpowiadał i byłoby dodatkowe I/O): folder bezpośrednio zawiera `.obsidian`, albo ≥ 5 plików
  `.md` stanowiących ≥ 60% plików (ostrzejszy próg niż obecne `LooksLikeNotes`, żeby małe foldery z README nie
  podpowiadały). *(decyzja projektanta)*
- Wynik wbudowanych: **0,7** przy dopasowaniu (jak reguły manifestu).
- Progi to wartości startowe; mogą trafić do stałych w kodzie (nie ustawienia).

### 4. Wyzwalanie (App)

- Źródło: zmiana `CurrentEntry` w aktywnej zakładce `FilePanelView` (te same zdarzenia, które śledzi `PreviewPanelView`,
  bez pollingu).
- Warunki: wpis jest folderem (`VfsEntryKind.Directory`, nie `..`), schemat `file`/`smb`, panel w trybie listy plików
  (nie pokazuje trybu), folder nie ma `c4k.dir`, folder nie jest na liście odmów, ustawienie włączone.
- Postój kursora: ~700 ms debounce; każdy ruch kursora/zmiana folderu anuluje trwającą ocenę i chowa powiadomienie.
- Listing: przez `VfsRouter` (już owinięty `ResilientAsyncExecutor`), limit ~500 wpisów (`Truncated`) i ~1,5 s łącznie
  na listing + wszystkie oceny. Sprawdzenie `c4k.dir` z tej samej migawki (bez osobnego odczytu).
- Oceny równolegle, każda z własnym limitem; wyjątki łapane w naszym kodzie (zasady „debugger-friendly” z CLAUDE.md).
- Wygrywa najwyższy `Score` (≥ 0,5); remis → wtyczka zewnętrzna przed wbudowaną (użytkownik zainstalował ją
  świadomie), dalej wyższy `Priority` podglądu. Jedna podpowiedź naraz. *(decyzja projektanta)*
- Cache wyniku per ścieżka + mtime folderu (krótko, w pamięci), żeby powrót kursora na ten sam folder nie liczył od nowa.

### 5. Powiadomienie (App)

- Mała karta w rogu panelu (nad listą, nie zasłania kursora — np. prawy dolny róg panelu), kolory wyłącznie ze
  `SkinBrushes`, wygląd zgodny z `IsModern` (zaokrąglona karta) / retro (ramka i tekst).
- Treść: ikona + `Message` + przyciski **„Przełącz”**, **„Nie teraz”**, **„Nie pytaj o ten folder”**.
- Nie bierze fokusu; klawisze zostają w panelu. Skrót akceptacji z klawiatury: nowa komenda
  `cm_AcceptFolderSuggestion` (domyślnie Ctrl+Enter — wolne w `DefaultKeymap`), opisana
  w `RegisterCommands`; w karcie podpowiedź klawisza.
- Znika: ruch kursora, zmiana folderu, „Nie teraz”, wejście w tryb panelu.
- „Nie teraz” wycisza ten folder do końca sesji (zbiór w pamięci, obok trwałej listy odmów). *(decyzja projektanta)*
- „Nie pytaj o ten folder” → ścieżka w `data/folder-suggestions.json` (lub w SQLite obok frecency — wybór
  implementującego). Bez okna potwierdzenia.

### 6. Akceptacja

1. Zapis `c4k.dir` przez `FolderConfigWriter` z akcją z `FolderSuggestionView`, w tym samym kształcie, który dziś pisze
   `ConfigureFolderViewAsync` (`where = "both"`: preview i enter). Zapis poza wątkiem UI. Błąd zapisu (brak uprawnień,
   udział tylko do odczytu) → `Notify(..., isError: true)` i otwarcie widoku jednorazowo, bez zapisu.
2. Wejście w folder jak Enter na folderze z `c4k.dir` (`MainWindow.EnterFolderAsync` → `RunFolderActionAsync` →
   przypięty `PreviewPanelView`).
3. Przewodnik (sekcja 7).
4. Wtyczka z katalogu, której nie ma zainstalowanej, nie zgłasza się (sugerują tylko zainstalowane i zatwierdzone).

### 7. Przewodnik „tu wrócisz” (driver.js-like)

- Nakładka nad oknem: przygaszenie (półprzezroczysty kolor tła ze skórki) z wycięciem wokół elementu powrotu
  przypiętego widoku — przycisk „📄 Pliki (Esc)” w `PreviewPanelView` (nie `_backButton`, który cofa historię widoków),
  obramowanie `focusRing`, dymek ze strzałką:
  „Tu (albo Esc) wrócisz do zwykłej listy plików. Shift+Enter wchodzi do folderu bez widoku.” + przycisk „OK”.
- Zamyka go: OK, Esc, klik gdziekolwiek, dowolny klawisz (klawisz nie jest dalej przekazywany tylko dla Esc/Enter, żeby
  pierwsze Esc nie wyszło od razu z widoku).
- Pokazywany przy akceptacji podpowiedzi, dopóki użytkownik nie zamknie go 3 razy albo raz nie wróci tym elementem/Esc
  (licznik w ustawieniach lub `data/`, nie w folderze).
- Komponent ogólny (`App/Tour/SpotlightOverlay` czy podobnie: cel = kontrolka, tekst, przyciski), żeby dało się go
  użyć ponownie, ale na razie jedno użycie.

### 8. Ustawienia i lokalizacja

- `AppSettings.FolderSuggestions` (bool, domyślnie `true`) + wpis w `SettingsCatalog.Create` (tytuł, opis po polsku,
  słowa kluczowe: podpowiedzi, galeria, widok folderu, sugestie).
- Opcja wyczyszczenia listy odmów (komenda albo przycisk w ustawieniach — wybór implementującego).
- Wszystkie teksty przez `Loc.T` / `Loc.P`, wpisy w `en.json`. Komunikaty z manifestu: `message` wybierany wg języka UI,
  fallback `en`, potem pierwszy.

## Testy

- Core: dopasowanie reguły manifestu (wzorce, `minCount`, `minShare`, `contains`, `Truncated`), wybór zwycięzcy, lista
  odmów, kształt zapisanego `c4k.dir`.
- Wbudowane: galeria i notatki na fałszywych listingach (próg tuż pod/nad).
- Zewnętrzne: `ExternalPluginTests` — przykład z `suggest` + `ask: true` odpowiada na `preview.suggest`; wtyczka bez
  zgody nie jest pytana; timeout = brak propozycji.
- `ManifestReader` czyta `suggest`; schemat i `docs/plugins.md` zaktualizowane; kopie SDK identyczne; przykłady czyste
  w `PluginSourceCheck`.
- UI: harness headless — kursor na folderze ze zdjęciami → karta widoczna; akceptacja → `c4k.dir` zapisany, galeria
  przypięta, nakładka widoczna; Esc zamyka nakładkę, drugie Esc wraca do plików. Zrzut ekranu do weryfikacji wyglądu.

## Zostawione implementującemu

Dokładne miejsce i wygląd karty w panelu, nazwy klas, czy galeria-sugestor to osobny wbudowany podgląd czy część
`FolderPreviewPlugin`, gdzie trzymać listę odmów i licznik przewodnika, dokładny skrót klawiszowy (jeśli Ctrl+Enter
zajęty), limit czasu `preview.suggest`.
