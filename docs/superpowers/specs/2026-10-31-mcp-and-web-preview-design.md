# MCP dla AI zamiast AI w przeglądarce, podgląd `kind: "web"`

Data: 2026-10-31. Status: projekt do zatwierdzenia.

> **⚠ Nieaktualne (2026-11-05).** Projekt został wdrożony i jest tylko zapisem historii. Zakłada, że czat w aplikacji (`AiChatWindow`, dostawcy w `Core/Ai`) zostaje, ale on też już nie istnieje. Usunięto również prompty kopiowane do schowka (generator skórek, „Przetłumacz interfejs z AI”), `AiAppContext` (teraz `McpAppContext`) i `AiPluginInstaller` (teraz `PluginInstaller`). Commander4K nie ma własnego AI, a modele działają z nim wyłącznie jako klienci MCP. Aktualny stan opisują `CLAUDE.md` oraz `docs/plugins.md`.

## Po co

Dziś AI spoza aplikacji pracuje z Commander4K przez panel przeglądarki: aplikacja wpisuje prompt (stan aplikacji, przewodnik po wtyczkach) w pole czatu Gemini/ChatGPT (`PageScripts.TypeIntoChat`), a potem zbiera wtyczkę z bloków kodu na stronie (`PageScripts.ReadCodeBlocks`, `PluginAuthoring.FromBlocks`). To jest kruche (zależy od DOM-u cudzych stron) i jednokierunkowe — model nie widzi skutków, nie poprawi wtyczki sam.

Zamiast tego Commander4K staje się **serwerem MCP**. Użytkownik podłącza do niego Claude Code (i każdego innego klienta MCP), a model może poznać stan aplikacji, sterować nią, używać wtyczek i pisać nowe — w pętli: napisz → zlintuj → zainstaluj → wywołaj → przeczytaj błędy → popraw.

Przykład, który ma zadziałać od początku do końca: w Claude Code „napisz plugin do podglądu plików PDF z możliwością szukania i drukowania; ma działać asynchronicznie, żeby nie blokować GUI”. Dziś to niemożliwe niezależnie od AI, bo `preview.render` umie tylko Markdown/tekst/kafelki — dlatego częścią projektu jest nowy rodzaj podglądu **`kind: "web"`** (strona HTML/JS wtyczki w WebView2).

## Cele

1. Usunąć AI przez przeglądarkę (menu „🤖 AI”, wpisywanie w czat stron, instalacja wtyczki z odpowiedzi na stronie).
2. Serwer MCP w aplikacji: kontekst, sterowanie, pliki, używanie wtyczek, tworzenie wtyczek.
3. Każde narzędzie MCP ma konfigurowalny tryb: zezwalaj / pytaj / zablokuj; część operacji zawsze pyta.
4. Podgląd `kind: "web"` dla wtyczek zewnętrznych.
5. Przykładowa wtyczka `pdf-viewer` (PDF.js, szukanie, drukowanie) jako dowód, że całość działa.

## Poza zakresem

- Czat w aplikacji (`AiChatWindow`, `AiChatTools`, dostawcy w `Core/Ai`, `AiProvidersWindow`) zostaje bez zmian. Panel przeglądarki zostaje jako przeglądarka (wyszukiwarki, w tym Gemini/ChatGPT jako zwykłe strony, `ui.openUrl`/`ui.search`).
- MCP nie wysyła promptów do klienta (brak „sampling”); to klient prowadzi rozmowę.
- `kind: "web"` tylko dla wtyczek zewnętrznych w panelu podglądu — nie dla paneli wtyczek, palet ani wbudowanych podglądów.
- Brak zdalnego dostępu: serwer słucha tylko na pętli zwrotnej.

## Czego nie wolno (anty-cele)

- MCP nie może być furtką obok modelu uprawnień: instalacja wtyczki zawsze przez `AiPluginInstaller` (okno uprawnień, wyłączona do zatwierdzenia, `PluginApprovalStore`), nigdy auto-włączenie.
- Strona `kind: "web"` nie dostaje własnego dostępu do dysku ani sieci — tylko przez host wtyczki (`ScopedPluginHost`, audyt).
- Żadnego I/O na wątku UI; żadnych zacięć GUI, gdy AI czeka na potwierdzenie albo wywołuje długie operacje.
- Bez nowych ciężkich zależności rozdmuchujących single-file exe (np. pełne ASP.NET Core) — patrz „Transport”.

## Część 1: usunięcie AI z przeglądarki

Usunąć:
- `BrowserPanelView`: `AiMenuButton` i wszystko, czego używa tylko ono — `AskAssistantAsync` (wariant „ze stanem aplikacji”), `ContextNote`, `CurrentAssistant`, `WritePluginAsync`, `InstallPluginFromPageAsync`, `AskWithPromptAsync`, `TypeIntoChatAsync`, `ReadCodeBlocksAsync`, właściwości `Ai`/`InstallPlugin`, jeśli zostaną bez użytku.
- `PageScripts.TypeIntoChat`, `PageScripts.ReadCodeBlocks` (cały plik, jeśli nic nie zostaje).
- `SearchEngine.ChatUrl`, a `IsAssistant`/`AppSettings.AssistantEngine`/wpis w `SettingsCatalog` — jeśli po usunięciu nic ich nie używa (budowniczy sprawdza; wyszukiwarki same zostają).
- `PluginAuthoring.FromBlocks` i parser bloków ze strony (testy w `PluginAuthoringTests` odpowiednio); `ParseFiles` (Markdown z czatu w aplikacji) zostaje.
- Zbędne klucze w `en.json` (test `LocalizationTests` wyłapie przestarzałe).

Zastąpić ścieżki, które dziś spadają do przeglądarki, gdy nie ma dostawcy w aplikacji (`MainWindow.AskAiAboutFileAsync`, `CreatePreviewPluginAsync`, `WritePluginWithAiAsync`):
- Jeśli `AiInApp` i dostawca jest zalogowany → jak dziś, czat w aplikacji.
- W przeciwnym razie → okno „Użyj Claude Code”: gotowy prompt (prośba + próbka pliku, ale **bez** wklejonego przewodnika — model pobierze go narzędziem MCP) skopiowany do schowka i krótka instrukcja: włącz serwer MCP (przycisk, jeśli wyłączony) i komenda `claude mcp add …` do skopiowania. Ten sam prompt jest dostępny jako MCP „prompt” (patrz niżej), więc w Claude Code wystarczy `/mcp__commander4k__…`.

Aktualizacja `CLAUDE.md` (sekcja o menu „🤖 AI” i `PageScripts`) i `docs/plugins.md`, jeśli o tym wspomina.

## Część 2: serwer MCP

### Transport

- MCP Streamable HTTP (specyfikacja 2025-06-18), jeden endpoint `POST /mcp` na `127.0.0.1`, port z ustawień (domyślnie stały, np. 47800; 0 = losowy zapisany przy pierwszym starcie — decyzja budowniczego). Odpowiedzi jako `application/json` (bez SSE) wystarczą, bo serwer nie wysyła powiadomień z własnej inicjatywy; `GET /mcp` → 405. Obsługa `Mcp-Session-Id` (sesja tworzona przy `initialize`).
- Uwierzytelnianie: losowy token (32 bajty, base64url) w `Authorization: Bearer …`, trzymany przez `SecretProtector` (DPAPI) w konfiguracji; przycisk „Nowy token” unieważnia stare. Sprawdzanie nagłówka `Origin` (odrzucać obce originy — ochrona przed DNS rebinding ze stron w przeglądarce).
- Wybór implementacji: **moja decyzja** — własna, mała implementacja na `HttpListener` (prefiks `http://localhost:<port>/`, nie wymaga uprawnień administratora; albo `TcpListener` jak `AiLogin`) zamiast pakietu `ModelContextProtocol.AspNetCore`, który ciągnie ASP.NET Core do single-file exe. Warstwa protokołu (JSON-RPC, `initialize`, `tools/list`, `tools/call`, `prompts/list`, `prompts/get`, `ping`, sesje, token) w `Commander4K.Core/Mcp/`, testowalna bez UI. Jeśli budowniczy stwierdzi, że oficjalny pakiet `ModelContextProtocol` (rdzeń, bez AspNetCore) da się użyć z własnym transportem HTTP bez dużego narzutu, może go użyć — kryterium: brak frameworku ASP.NET Core w publikacji.
- Wywołania narzędzi idą w tle (`Task.Run`/async), UI tylko przez `Dispatcher`. Narzędzie czekające na potwierdzenie trzyma otwarte żądanie HTTP; anulowanie (`notifications/cancelled` lub zerwane połączenie) zamyka okno potwierdzenia jako „Nie”.
- Serwer domyślnie **wyłączony** (`AppSettings.Mcp.Enabled = false`), start/stop bez restartu aplikacji.

### Podłączanie

- Komenda `cm_McpConnect` („Połącz Claude Code / klienta MCP”): włącza serwer, jeśli trzeba, i pokazuje okno z komendą `claude mcp add --transport http commander4k http://localhost:<port>/mcp --header "Authorization: Bearer <token>"` (przycisk Kopiuj), także wariantem JSON do `.mcp.json`, oraz stanem: działa / port zajęty / liczba sesji.
- Pasek stanu pokazuje znaczek „AI podłączone”, gdy jest aktywna sesja MCP (klik → okno ustawień MCP z listą ostatnich wywołań).
- Każde wywołanie narzędzia → `AuditLog` (nowy plik `data/logs/mcp-YYYYMMDD.log` albo ten sam format co pluginy z prefiksem `mcp`; decyzja budowniczego), z narzędziem, argumentami (skrót), decyzją i wynikiem (ok/błąd).

### Narzędzia

Nazwy w `snake_case`, opisy po angielsku (to prompt). Kolumna „domyślnie” to tryb fabryczny; **zawsze pyta** = tryb nie da się ustawić na „zezwalaj” ani „zezwól do końca sesji”.

Kontekst i wiedza:
| narzędzie | co robi | domyślnie |
|---|---|---|
| `get_context` | `AiAppContext.Describe()`: oba panele (ścieżka, tryb, kursor, zaznaczenie, próbka listy), karty, aktywny panel, wtyczki i ich stan, runtime'y | zezwalaj |
| `list_commands` | komendy z `CommandRegistry` (id, opis, skrót), w tym `plugin:` i `panel:` | zezwalaj |
| `get_plugin_guide` | `docs/plugins.md` + schemat + przykład dla wybranego runtime'u (parametr `runtime?`, `example?` — nazwa przykładu, np. podgląd) | zezwalaj |
| `list_plugins` | zainstalowane wtyczki: id, wersja, folder, włączona/zatwierdzona, uprawnienia, wkłady (komendy, panele, podglądy, palety) | zezwalaj |

Sterowanie:
| narzędzie | co robi | domyślnie |
|---|---|---|
| `navigate` | `{ panel: "left"\|"right"\|"active"\|"other", path }` | zezwalaj |
| `select` | ustawia kursor i/lub zaznaczenie w panelu (`names[]`, `cursor?`) | zezwalaj |
| `set_panel_mode` | pliki / podgląd / panel wtyczki (`panel:<id>`) | zezwalaj |
| `run_command` | wykonuje komendę `cm_*` przez `CommandRegistry` (jak z palety) | pytaj |

Pliki (przez `VfsRouter`, więc dyski, SMB, SFTP, FTP, archiwa, rejestr):
| narzędzie | co robi | domyślnie |
|---|---|---|
| `list_directory` | jak dziś w `AiChatTools` (limit wpisów) | zezwalaj |
| `stat` | jeden wpis | zezwalaj |
| `read_file` | tekst (limit znaków) albo base64 z limitem bajtów | pytaj |
| `write_file` | tekst/base64, nadpisanie | pytaj |
| `make_directory` | | pytaj |
| `move` / `delete` | | **zawsze pyta** |

Używanie wtyczek:
| narzędzie | co robi | domyślnie |
|---|---|---|
| `run_plugin_command` | `plugin:<id>:<cmd>` z kontekstem; zwraca wiadomość/tekst | pytaj |
| `load_plugin_panel` | `panel.load` jako dane (kolumny, wiersze, status), z `argument`/`filter` | pytaj |
| `render_preview` | wybiera podgląd jak `PreviewPanelView` dla ścieżki (albo `pluginId`) i zwraca treść: Markdown/tekst/kafelki jako JSON; dla `web` — informację o stronie (bez renderowania) | pytaj |

Tworzenie wtyczek:
| narzędzie | co robi | domyślnie |
|---|---|---|
| `check_plugin` | `{ files: [{ path, content }] }` albo `{ folder }`: `PluginAuthoring.Prepare` (ścieżki, manifest) + `PluginSourceCheck`; zwraca listę problemów, nic nie instaluje | zezwalaj |
| `install_plugin` | `{ files[] }` albo `{ folder }`: przez `AiPluginInstaller` (okno uprawnień = potwierdzenie, odwołanie wcześniejszej zgody, wyłączona). Aktualizacja istniejącej wtyczki = to samo narzędzie (nowa wersja, ponowna zgoda). Pod MCP instalator **nie** otwiera menedżera wtyczek ani przycisku „poproś AI o poprawkę” — problemy z lintu/manifestu wracają jako wynik narzędzia (tekst `FixRequest`) | **zawsze pyta** |
| `enable_plugin` | pokazuje to samo okno zatwierdzenia uprawnień co menedżer wtyczek (lista uprawnień z manifestu); „Tak” = `PluginApprovalStore.Approve` (w kodzie zatwierdzenie i włączenie to jedna flaga). Tak pętla napisz → zainstaluj → włącz → użyj kończy się bez wchodzenia do menedżera, a człowiek nadal zatwierdza każdą nową wersję kodu | **zawsze pyta** |
| `reload_plugin` | restart procesu włączonej wtyczki (np. po zawieszeniu). Nie wczytuje nowego kodu poza procesem instalacji: kod zmienia się tylko przez `install_plugin` | zezwalaj |
| `get_plugin_log` | ostatnie wpisy audytu wtyczki, stderr jej procesu, ostatnie błędy JSON-RPC (budowniczy dokłada bufor stderr w `ExternalPlugin`, jeśli go nie ma) | zezwalaj |

Pliki wtyczki: model pisze je do własnego folderu roboczego (Claude Code swoimi narzędziami, np. w repo, albo `write_file`) i instaluje `{ folder }`, albo wysyła `{ files[] }` (tekst) bezpośrednio. Docelowy folder instalacji jak dziś (`plugins/` w folderze danych). Zasoby zewnętrzne (np. PDF.js) Claude Code pobiera własnymi narzędziami do folderu roboczego — MCP nie ma narzędzia sieciowego.

Zmiany w `PluginAuthoring` dla instalacji z folderu: pliki binarne (`GeneratedFile` z bajtami albo osobny typ), większe limity (rząd wielkości: kilkaset plików, kilka MB na plik, kilkadziesiąt MB łącznie — budowniczy ustala liczby), lint (`PluginSourceCheck`) tylko dla plików tekstowych kodu procesu. Limity katalogu oficjalnego bez zmian.

**Dozwolone foldery (decyzja użytkownika):** `AppSettings.Mcp.AllowedRoots` — lista ścieżek VFS (dowolny schemat: `C:\…`, `\\srv\share`, `sftp://…`, `reg://…`). Każda operacja MCP na ścieżce spoza listy jest **blokowana** (błąd narzędzia z podpowiedzią, że użytkownik może dodać folder w ustawieniach MCP), niezależnie od trybu narzędzia. Dotyczy narzędzi plikowych, `navigate`, `render_preview`, `install_plugin { folder }`, `check_plugin { folder }` i ścieżek w argumentach `run_plugin_command`/`load_plugin_panel` (kontekst wtyczki: aktywna ścieżka i zaznaczenie spoza listy → odmowa). `get_context` pokazuje ścieżki paneli zawsze, ale próbkę listy i zaznaczenie tylko wewnątrz dozwolonych folderów — **moja decyzja**. Porównanie po znormalizowanej ścieżce (bez `..`, wielkość liter jak dostawca: na dyskach i SMB bez rozróżniania), granica na separatorze (`C:\Repo` nie obejmuje `C:\Repos2`). Domyślnie lista pusta; okno `cm_McpConnect` i ustawienia mają „Dodaj folder z aktywnego panelu”. Lista w oknie ustawień MCP i jako wyszukiwalne ustawienie. Ścieżki chronione niżej są zablokowane nawet wewnątrz dozwolonego folderu.

**Ochrona kodu wtyczek:** `write_file`/`make_directory`/`move`/`delete` odmawiają ścieżek w zainstalowanych folderach wtyczek (oba `plugins/`) i w `config/` aplikacji (zgody, tokeny, klucze) — błąd narzędzia z podpowiedzią „use install_plugin”. Bez tego zapis „Tak do końca sesji” + restart wtyczki uruchomiłby nowy kod bez przeglądu.

### Prompty MCP

`prompts/list` udostępnia gotowe zadania z argumentami, które Claude Code pokazuje jako komendy `/mcp__commander4k__<nazwa>`:
- `write_plugin` (`wish`),
- `preview_plugin_for_file` (`path`, `wish?`) — to samo co dziś „utwórz wtyczkę podglądu”: prośba + próbka pliku (`AiAppContext.DescribeFileAsync`) + wskazówka, żeby pobrać przewodnik narzędziem i przetestować `render_preview`,
- `explain_file_format` (`path`).

### Konfiguracja aplikacji, tłumaczenia, skórki

Dopisane 2026-10-31 na prośbę użytkownika. Klient MCP sam jest modelem, więc MCP nie woła API (`ClaudeSkinGenerator`, dostawcy z `Core/Ai`). Daje modelowi to samo, co dziś trafia do promptu kopiowanego do schowka (`TranslationKit.PromptFor`, `SkinPrompt.ForChat`, schemat skórki), sprawdza wynik i zapisuje go. `config/` jest chroniony przed `write_file`, więc do każdego z tych plików prowadzi osobne narzędzie. Zapisuje ono tylko do pliku o nazwie wyliczonej z walidowanego id albo kodu, a nigdy pod ścieżkę podaną przez model.

Konfiguracja (przez `AppHost.SettingsCatalog`, czyli te same wpisy co w oknie `Ctrl+,`, razem z `column:` i `mcp:`):
| narzędzie | co robi | domyślnie |
|---|---|---|
| `list_settings` | `{ query? }` → `SettingsCatalog.Search`: klucz, kategoria, tytuł, opis, rodzaj (toggle/text/choice), bieżąca wartość, dozwolone wartości, „wymaga restartu” | zezwalaj |
| `set_setting` | `{ key, value }`: `SettingDescriptor.Set` → `MainWindow.OnSettingChanged(key)` (skórka, gęstość, kolumny… działają od razu) → `SaveSettingsAsync`. Wartość jest walidowana według rodzaju i listy wyborów. Wynik podaje starą i nową wartość oraz to, czy zmiana wymaga restartu | pytaj |
| `list_key_bindings` | skróty z `AppHost.KeyBindings` z opisem komendy (`CommandRegistry`) | zezwalaj |
| `set_key_binding` | `{ keys, command }` (`command: null` usuwa skrót): kopia słownika → `AppHost.SaveKeyBindingsAsync` (odrzuca złą sekwencję). Jeśli skrót był już przypisany, wynik podaje poprzednią komendę | pytaj |

Ustawień, które poszerzają uprawnienia albo podmieniają źródło kodu, MCP nie zmienia, w żadnym trybie: `mcp.*` i `mcp:*` (włączenie serwera, port, dozwolone foldery, tryby narzędzi), `pluginCatalogUrl`, `aiCatalogUrl` i `updateRepository`. Nie zmienia też `tools.json` (polecenia zewnętrzne, czyli uruchamianie programów), połączeń (sekrety) ani kluczy dostawców AI. `set_setting` odpowiada wtedy błędem „change this in the Commander4K settings window”. `list_settings` pokazuje te wpisy z adnotacją „read-only over MCP”, żeby model wiedział dlaczego. To **moja decyzja**: inaczej „Tak do końca sesji” dla `set_setting` pozwoliłoby modelowi samemu dodać sobie folder albo przełączyć `delete` na „zezwalaj”.

Tłumaczenia (`config/i18n/<kod>.json`, `TranslationKit`):
| narzędzie | co robi | domyślnie |
|---|---|---|
| `get_translation_kit` | `{ language, name?, onlyMissing?, offset?, limit? }`: zasady z `TranslationKit.PromptFor` (bez wklejonego pliku) + liczba form mnogich + strona wpisów z en.json (klucz PL → wartość EN). Przy `onlyMissing` tylko klucze, których brakuje w istniejącym pliku języka. en.json ma ok. 80 KB, więc stronicowanie (domyślnie 300 wpisów) pozwala modelowi tłumaczyć partiami i uzupełniać istniejące języki | zezwalaj |
| `check_translation` | `{ language, entries }`: `TranslationKit.Check` na pliku po scaleniu, bez zapisu | zezwalaj |
| `save_translation` | `{ language, entries }`: scala z istniejącym plikiem (nowe wartości nadpisują stare) i zapisuje atomowo. Odmawia, jeśli scalone wpisy mają błędy placeholderów/form mnogich albo nieznane klucze; brakujące klucze tylko zgłasza (aplikacja pokaże wtedy tekst angielski). Kod `pl` odrzucony (język źródłowy). Kod walidowany: `^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$`. Wynik: liczba wpisów, ile brakuje, i podpowiedź `set_setting language` (wymaga restartu) | pytaj |

Skórki (`config/skins/<id>.json`):
| narzędzie | co robi | domyślnie |
|---|---|---|
| `get_skin_guide` | `SkinPrompt.DesignBrief` + `SkinSchema.Create()` + JSON bieżącej skórki jako punkt wyjścia | zezwalaj |
| `list_skins` | id, nazwa, wbudowana/użytkownika, bieżąca, ostrzeżenia `SkinValidator` | zezwalaj |
| `get_skin` | `{ id }` → JSON skórki (`SkinJson.Serialize`) | zezwalaj |
| `check_skin` | `{ json }`: `SkinJson.Deserialize` + `SkinValidator.Validate` (kontrast WCAG), bez zmian na ekranie | zezwalaj |
| `save_skin` | `{ json, apply? }`: własne okno potwierdzenia z **podglądem na żywo** (`SkinManager.Preview`) i ostrzeżeniami walidatora. „Tak” zapisuje plik jak `AiSkinDialog.SaveAsync` (id wbudowanej skórki dostaje `-ai`, id walidowane `^[a-z0-9][a-z0-9-]{0,63}$`), robi `ReloadSkinsAsync` i ustawia skórkę, gdy `apply` (domyślnie tak; inaczej przywraca poprzednią). „Nie” przywraca poprzednią skórkę | pytaj, sam potwierdza |

Zmiana skórki bez zapisu to `set_setting skin`. Wszystkie te narzędzia nie mają argumentów ścieżek, więc dozwolone foldery ich nie dotyczą. Zapisy idą tylko do `settings.json`, `keymap.json`, `i18n/<kod>.json` i `skins/<id>.json` w `config/` aplikacji.

Prompty MCP dla tych obszarów: `translate_ui` (`language`, `name?`) — „pobierz `get_translation_kit` partiami, sprawdzaj `check_translation`, zapisuj `save_translation`”; `design_skin` (`description`) — „`get_skin_guide`, zaprojektuj, `check_skin`, popraw ostrzeżenia, `save_skin`”.

### Potwierdzenia i konfiguracja

- `AppSettings.Mcp`: `Enabled`, `Port`, `AllowedRoots`, `ToolModes: { "<tool>": "allow"|"ask"|"block" }` (brak wpisu = domyślny). Token osobno (DPAPI).
- `SettingsCatalog`: przełącznik i port jako zwykłe ustawienia; każde narzędzie jako wpis z wyborem trybu (np. `mcp:<tool>`, kategoria „MCP”, jak `column:`/`palette:` w `AppHost.ExtraSettings`), więc są wyszukiwalne (`Ctrl+,`, `Ctrl+Shift+P`). Narzędzia „zawsze pyta” mają tylko „pytaj”/„zablokuj”.
- Okno potwierdzenia (niemodalne względem pracy w panelach? — **moja decyzja: modalne dla okna głównego, ale nie blokujące wątku**, jak inne dialogi `DialogWindow`): nazwa klienta MCP (z `initialize.clientInfo`), narzędzie po ludzku (`Loc.T`), argumenty (ścieżki, komenda, rozmiar treści; podgląd treści zapisu, przycięty), przyciski Tak / Nie / Tak do końca sesji (to ostatnie ukryte dla „zawsze pyta”). Kilka żądań naraz → kolejka okien, nie stos.
- „Do końca sesji” = do zamknięcia aplikacji, dla danego narzędzia i klienta (`clientInfo.name`), a nie `Mcp-Session-Id` (Claude Code łączy się ponownie często). W menu znaczka „AI podłączone” jest „Cofnij zgody z tej sesji”. Nic nie zapisuje się na dysk — **moja decyzja**.
- Odmowa i „zablokuj” → wynik narzędzia z `isError: true` i powodem po angielsku („The user declined…”, „This tool is disabled in Commander4K settings”). Narzędzia zablokowane nadal są na liście `tools/list` (z dopiskiem w opisie), żeby model wiedział, dlaczego nie działa — **moja decyzja**; alternatywa to ukrywanie.

### Lokalizacja i pozostałe reguły

UI przez `Loc.T` + `en.json`; opisy narzędzi, wyniki dla modelu i wpisy audytu po angielsku (nie tłumaczone, jak prompty). Nowe komendy w `CommandIds`, opisy w `MainWindow.RegisterCommands`.

## Część 3: podgląd `kind: "web"`

### Protokół (`docs/plugins.md`, `docs/plugin.schema.json`, SDK)

`preview.render` może zwrócić:
```json
{ "kind": "web", "page": "viewer/index.html", "state": { "any": "json" } }
```
- `page` — ścieżka względna w folderze wtyczki (walidowana jak w `PluginAuthoring`: bez `..`, bez ścieżek absolutnych).
- `state` — dowolny JSON przekazany stronie (np. ustawienia, numer strony).
- Manifest nie wymaga nowego wkładu; podgląd jak każdy inny w `contributes.previews`. Strona i jej zasoby (np. PDF.js) leżą w folderze wtyczki.

### Wyświetlanie

- Nowy `WebPreview(string PluginId, string PageUrl, JsonElement? State)` w `PluginSdk` (`PreviewContent`), budowany w `ExternalPreviewPlugin` (`ExternalPluginAdapters`).
- `PreviewPanelView` pokazuje go w `WebView2Host` (wspólne środowisko `WebView2Host.GetEnvironmentAsync`, osobny kontroler na panel podglądu; profil jak przeglądarka albo osobny — decyzja budowniczego, ale bez dostępu do ciasteczek przeglądarki, jeśli to proste).
- Folder wtyczki mapowany na wirtualny host `https://<plugin-id>.plugin.c4k/` (`SetVirtualHostNameToFolderMapping`, dostęp `Deny` dla innych originów). Nawigacja poza ten host zablokowana (`NavigationStarting` → anuluj; linki http(s) → `ui.openUrl` w panelu przeglądarki), nowe okna zablokowane, pobieranie zablokowane.
- Sieć: `WebResourceRequested` odrzuca wszystko poza wirtualnym hostem wtyczki i plikiem bieżącego wpisu (niżej). Wtyczka z uprawnieniem `net` nadal nie dostaje sieci ze strony — sieć tylko przez SDK po stronie procesu wtyczki (**moja decyzja**, prostsza i bezpieczniejsza).
- Gdy kursor zmienia plik, a nowy wynik `preview.render` ma ten sam `page` tej samej wtyczki → strona dostaje zdarzenie `entry` (bez przeładowania). Inny `page`/wtyczka → nawigacja.
- Bez WebView2 albo headless → `MessagePreview` z komunikatem (jak Mermaid).

### Most strona ↔ aplikacja (`window.c4k`)

Aplikacja wstrzykuje skrypt (`AddScriptToExecuteOnDocumentCreatedAsync`) z obiektem:
- `c4k.entry` — `{ name, path, kind, size, modified }` bieżącego wpisu, `c4k.state` — `state` z `preview.render`, `c4k.skin` — kolory skórki (tokeny `SkinBrushes`) i gęstość, żeby strona pasowała do motywu.
- `c4k.on("entry", fn)`, `c4k.on("skin", fn)`.
- `c4k.fileUrl` — adres bieżącego pliku do `fetch()` (np. `https://<plugin-id>.plugin.c4k/__entry/<token>`), obsługiwany w `WebResourceRequested` strumieniowo przez `ScopedPluginHost.OpenReadAsync` tej wtyczki (gating `fs.read`, audyt, zakresy `Range` dla dużych plików — PDF.js ich używa). Token ważny tylko dla bieżącego wpisu. Jeśli strumień dostawcy da się przewijać (dysk, zwykle SMB), `Range` obsługiwany wprost; jeśli nie (SFTP, FTP, archiwa), plik jest raz kopiowany do pliku tymczasowego (jak w `ArchiveVfsPlugin`) i `Range` idzie z niego; plik tymczasowy usuwany przy zmianie wpisu — **moja decyzja**.
- `c4k.request(method, params)` → Promise: przekazanie do procesu wtyczki jako nowe wywołanie JSON-RPC `preview.message` `{ preview, entry, method, params }` → wynik. Pozwala, żeby ciężką pracę robił proces wtyczki (SDK, uprawnienia), a strona tylko rysowała.
- `c4k.notify(text)`, `c4k.copyText(text)` → `IPluginHost` (z audytem).
- `c4k.save(bytes|text)` tylko gdy `editable: true` w wyniku i dostawca ma `VfsCapabilities.Write` (jak dziś edycja podglądu; przez `fs.write`) — **opcjonalne, może poczekać**.

Klawisze: wewnątrz strony zostają zwykłe klawisze i Ctrl+F/Ctrl+P/Ctrl+C itd.; F-klawisze (oprócz F5/F12 jak w przeglądarce? — w podglądzie F5 też do aplikacji), Esc, Tab, Ctrl+Q i kombinacje Alt/Ctrl+Shift przekazywane do keymapy przez `AcceleratorKeyPressed` (jak `BrowserPanelView`). Drukowanie: `window.print()` otwiera okno druku WebView2.

### SDK

Każdy z trzech klientów (`plugins-sdk/{python,node,powershell}`) dostaje pomocnik zwracający wynik `web` oraz rejestrację handlera `preview.message`; kopie w przykładach i katalogu (test identyczności), tabela w `docs/plugins.md`. `PluginSourceCheck` nie skanuje plików `.html`/`.js` strony pod kątem `fetch` itp. (strona działa w piaskownicy), ale nadal skanuje kod procesu wtyczki — budowniczy rozróżnia po tym, czy plik jest `main` z manifestu (lub przez niego importowany) czy zasobem strony; prostsza reguła do wyboru: zasoby w podfolderze wskazanym przez `page` są pomijane.

## Część 4: przykład `pdf-viewer`

- Wtyczka zewnętrzna (Node albo Python — decyzja budowniczego; Node naturalniej z PDF.js), podgląd `.pdf`, `kind: "web"`, strona z PDF.js (wersja przypięta, sprawdzona sha512 jak Mermaid, w folderze wtyczki).
- Strona: przewijane strony, powiększenie, pole szukania z podświetleniem i następny/poprzedni (Ctrl+F w stronie), przycisk Drukuj (`window.print()` z widokiem do druku stron), kolory ze skórki. Plik przez `c4k.fileUrl` z `Range`, więc duże PDF-y nie ładują się w całości.
- Uprawnienia: `fs.read`. Asynchroniczność: proces wtyczki prawie nic nie robi; renderowanie w WebView2 (osobny proces), więc GUI aplikacji nie jest blokowane.
- Miejsce: `examples/plugins/pdf-viewer-node`. Przez rozmiar PDF.js (worker ~1 MB+, cmaps, fonty) **nie** trafia do szablonów osadzonych w aplikacji ani do katalogu oficjalnego (limity katalogu zostają) — **moja decyzja**; instalacja z folderu przez `install_plugin { folder }`.
- Test akceptacyjny całości: w Claude Code z podłączonym MCP prośba z „Po co” daje działającą wtyczkę bez ręcznego kopiowania plików (Claude pisze, `check_plugin`, `install_plugin`, użytkownik zatwierdza, `render_preview`/`navigate` + podgląd w oknie).

## Testy

- Core: protokół MCP (initialize, sesje, token, Origin, tools/list, tools/call, błędy JSON-RPC, anulowanie), dozwolone foldery (granice ścieżek, `..`, schematy, ścieżki chronione), tryby narzędzi i „zawsze pyta” (nie da się ustawić allow), mapowanie odmowy na `isError`.
- Konfiguracja/tłumaczenia/skórki: `set_setting` odrzuca `mcp.*`/`mcp:*`/adresy katalogów i aktualizacji, waliduje wartości wyborów i liczby; stronicowanie i `onlyMissing` w `get_translation_kit`; `save_translation` scala, odrzuca `pl`, zły kod i błędy placeholderów; `check_skin` zgłasza niski kontrast; nazwy plików tylko z walidowanych id.
- Integracyjny: prawdziwy `HttpClient` przeciw serwerowi na porcie 0 z atrapą narzędzi.
- `ExternalPluginAdapters`: parsowanie `kind: "web"` i walidacja `page`; `preview.message` w trzech SDK (`ExternalPluginTests` z prawdziwymi runtime'ami).
- `LocalizationTests`, `PluginCatalogTests`, test kopii SDK — jak zawsze.
- WebView2 nie działa headless: podgląd web i okno potwierdzeń weryfikować w prawdziwym oknie.

## Kolejność

1. Usunięcie AI z przeglądarki (+ okno „Użyj Claude Code” jako zaślepka bez MCP lub od razu z MCP).
2. Serwer MCP: protokół i transport w Core → narzędzia kontekstu/sterowania/plików → potwierdzenia i ustawienia → wtyczki → prompty → konfiguracja, tłumaczenia, skórki.
3. `kind: "web"`: protokół i SDK → wyświetlanie i most → `preview.message`.
4. `pdf-viewer` i test akceptacyjny przez Claude Code.

## Zostawione budowniczemu

Port domyślny, `HttpListener` vs `TcpListener`, układ plików w `Core/Mcp` i `App/Mcp`, dokładne limity rozmiarów (odczyt, base64), format logu MCP, profil WebView2 dla podglądu, runtime przykładu PDF, czy przykład idzie do szablonów, szczegóły wyglądu okien.
