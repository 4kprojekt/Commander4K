# `*.c4k` files replace `c4k.dir` — design

Date: 2026-11-08. Status: draft for approval.

## Intent and why

Today a folder is configured by one hidden convention file, `c4k.dir`: a JSON object with a `preview` action, an `enter` action and, for dashboards, a hand-ordered list of tiles. It is one file per folder, so one folder cannot show several things unless the user writes a tile list by hand, and the user has to know the file exists.

New model: **a folder is configured by the `*.c4k` files in it.** Each file describes exactly one thing (a plugin view, a built-in view, a connection, a link to another folder, a URL, a command). What the folder shows follows from how many files there are:

- no `.c4k` file: the ordinary file list;
- one file: the folder's view is that item;
- two or more: an automatic **dashboard**, one card per file, sorted by file name.

The user orders the dashboard by renaming files (`01-server.c4k`, `02-logs.c4k`), not by editing a list. `c4k.dir` is removed entirely, with no compatibility reader and no migration (old files are ignored).

## Goals

- One concept: a `.c4k` file is one item. Same format for a single view, a dashboard card, a connection and a folder link.
- A dashboard needs no authoring: drop several `.c4k` files in a folder.
- Order is the file name; moving a card is a rename.
- A folder can itself be a `.c4k` item (a link whose target is another folder), so dashboards can point at folders.
- Everything that used `c4k.dir` moves to `.c4k`: app pages (plugin manager, settings, skins), the mail account folder, folder suggestions, MCP dashboard tools, the trust store.
- Entering a connection item (SFTP/SSH, FTP, SMB) works as before. The remote content stays on the server, the `.c4k` stays where it was configured.

## Non-goals and anti-goals

- No reader for `c4k.dir`, no migration tool, no schema bridge. Existing files stop working.
- No manual tile order field, no section headings, no ↑/↓ buttons, no tile-less "heading" items. Order is the file name.
- No dashboard of one item, and no way to force a dashboard from a single file.
- No new AI features (project rule). The MCP tools and prompts are updated, nothing more.
- Anti-goal: opening a folder must not get noticeably slower or hang on slow shares.

## File format

`<name>.c4k`, JSON with comments and trailing commas allowed (as `c4k.dir` today), at most 256 KB. The file base name is the default `title` and the sort key. `docs/c4k.schema.json` replaces `docs/c4k.dir.schema.json`.

One item = the merge of today's `FolderAction` and `FolderTile`: `view` (`gallery`, `notes`, `markdown`, `files`, `settings`, `skins`, `plugins`, `api`), or `plugin` (+ `preview`/`panel`, `options`), or `open` (folder, archive, `sftp://`, `ftp://`, SMB, with `user`/`keyFile`), `url`, `run`, `api`; plus the card fields (`card`, `refresh`, `size`, `title`, `description`, `icon`). Dropped as top-level keys: `preview`/`enter` split, `tiles`, `heading`, `dashboard` as a view id.

Preview and Enter used to be separate actions; now one item serves both, decided by its kind:
- view or plugin: shown in the quick view (cursor on the folder) and pinned on Enter;
- `open`, `url`, `run`: the quick view shows the card (if any) or the title; the action happens only on Enter or when the tile is clicked (`run` still needs trust).

An item's context folder is the folder that contains the file, unless the item gives its own `folder` (for a mail account whose messages live elsewhere). This is a call of mine, to settle the mail case; the builder may name the key differently.

## Resolution

A single reader replaces `FolderConfig.ReadAsync` (called at `AppHost.cs:322`):

1. List the folder (one listing, 2 s limit as today, only on file/smb/sftp/ftp), keep entries ending `.c4k` (case-insensitive), at most 50, natural sort of the names (so `2-` before `10-`), each parsed independently. A broken file becomes an item with `Error`, shown as an error card; it never hides the others.
2. 0 items: nothing. 1 item: its view. 2+ items: `DashboardPreview` whose tiles are the items.
3. `CardResolver`, `CardRefreshLoop`, `ChartControl`, `TileGridLayout` and `ViewHistory` stay; `DashboardView` loses editing of headings and order.

The reader is shared with `FolderSuggestionService` (its one listing already exists; "folder has no c4k.dir" becomes "folder has no `.c4k`"). When the panel already holds the listing of the folder, the builder should reuse it instead of listing twice.

## Writing

The old `FolderConfigWriter` edited one JSON document in place (append tile, move tile, set options). Replaced by a per-file writer:

- create item: new file named from the title/plugin, made unique by suffix (`mail.c4k`, `mail 2.c4k`), never overwriting;
- set plugin options: edit the one file that names the plugin (setup flow, `preview.setup`);
- resize: write `size` into that file (the only remaining dashboard editing; ⤢ stays);
- remove: out of scope for the card hover (no ×). Deleting a card = deleting the file in the panel.
- files with comments are not rewritten silently: keep today's rule (ask or refuse).

Writes go through the VFS so they work on disks and shares and are refused (with the existing fallback of opening the view once) on read-only providers.

## Trust

`FolderTrustStore` keys on file path + SHA-256 of the content, and was per `c4k.dir`. It becomes per `.c4k` file: each file with `run`, a side-effecting `api` or an `ssh` card is locked until the user allows that file ("Zezwól"). `TrustSummary()` is computed per item. A dashboard therefore shows some cards locked and others live.

## Connections: Esc and errors

An item with `open` pointing at a remote or another folder navigates the panel there. The tab remembers the **origin**: the folder that holds the `.c4k`.

- Esc in that panel, and switching the Pliki · Auto · Widok switch to Pliki, return to the origin's file list. This replaces going up folder by folder on the server.
- A failed connection (unknown host, refused login, timeout) does **not** return by itself. The panel stays on the failed path with the error message (as in the current "Nieznany host." screen), and Esc or Pliki leads back to the origin.
- The origin is dropped when the user navigates by their own choice (path bar, drive menu, another tab content). Exact rule is the builder's, my call: keep it when the current path is the target or below it.
- The remote server's own files are never read for `.c4k` unless the panel enters a folder there; there is no special remote handling beyond that.

## App pages

`MainWindow.EnsureAppFolderConfigsAsync` writes `plugins.c4k` (in `data/plugins`), `settings.c4k` (in `config/`), `skins.c4k` (in `config/skins/`) with `"view": …`, when missing, never overwriting. The quick view still never opens these pages for a folder under the cursor. Consequence to accept: a second `.c4k` file dropped into one of these folders turns it into a dashboard and hides the page. Not guarded against.

## Everything else that changes

- Mail: the account folder holds a `.c4k` item with `plugin: official.mail` and the options; `add-account` writes it. A dashboard of accounts is a folder with several `.c4k` items, one per account (each with `folder` to the account's own folder).
- MCP (`McpDashboardTools`, `dashboard_guide`, `get_dashboard`, `check_dashboard`, `save_dashboard`, prompt `build_dashboard`, `McpPluginKnowledge`): a dashboard is a set of files. `save_dashboard` takes a list of named items and writes one `.c4k` per item (Ask + Writes as today; never trusts files). `check_dashboard` keeps the ASCII grid. The guide text is rewritten.
- Commands: `cm_FolderView`, `cm_AddApiCard`, `cm_AcceptFolderSuggestion` write items instead of editing c4k.dir. Names stay unless the builder finds a clash.
- `FileIcons` gives `.c4k` its icon (instead of the `c4k.dir` name check); `PanelSorting` unchanged.
- Settings: `folderConfigs` keeps its meaning (read `.c4k` files at all). Descriptions in `SettingsCatalog` are updated.
- Localization: every changed user-visible string goes through `Loc.T`, with `en.json` entries (`LocalizationTests`).
- Docs and tests: `docs/plugins.md`, the schema, `CLAUDE.md` (the c4k.dir sections), `docs/DesignSystem` mentions if any, README demo `c4k.dir` listings, and the demo folders in `C:\Users\Public\Commander4K-demo`. Tests that use `c4k.dir` are rewritten; add tests for the reader (counts 0/1/N, ordering, broken file, size cap, listing timeout), the writer (unique names) and per-file trust.
- Catalog plugins that mention c4k.dir in docs or options text are updated; run `pwsh ./plugins-catalog/Update-Catalog.ps1` afterwards.

## Calls made after the builder check (author's decisions, to be confirmed on reading)

1. **Single non-view item.** Behaves as `enter` does today: entering the folder (Auto/Widok mode) performs the item. `open` navigates, `url` opens the browser panel, `run` runs only when the file is trusted (otherwise the locked card with "Zezwól"). Pliki mode never reads `.c4k`, so the folder is always reachable as files. Esc/Pliki return to the file list.
2. **Writers and suggestions.** `.c4k` files are ordinary visible files (they must be renameable); gallery, notes and "mostly Markdown" detection ignore them. Suggestions are offered only for folders with no `.c4k`. A writer that would turn a folder with exactly one item into a dashboard (second file) asks first ("Folder stanie się dashboardem"); MCP writers refuse and say so.
3. **Card form.** A tile uses `card` as today (`"auto"` for plugins/panels that provide a card). With no card form it is a shortcut tile: icon, title, description, click = the item's action. Built-in views (gallery, notes, files, markdown, settings, skins, plugins) are shortcut tiles on a dashboard.
4. **Context folder.** `folder` is generally allowed (relative to the file's folder, or absolute/VFS) and is checked by the MCP path policy like any path argument. Items sharing one folder are independent; there is no shared pinned state.
5. **Mail.** `add-account` writes the account's own `.c4k` in the account folder (opening it shows the account) and, when run from a dashboard folder, a card `.c4k` there with `folder` pointing to the account folder. An account folder does not need to be a dashboard member to work.
6. **Trust hash.** Trust is keyed by file path plus a hash of the security-relevant fields only (`run`, `api` method/url/body, `ssh` host, `user`, `keyFile`), not of the whole text, so ⤢ resize and edits to `title`/`size` never revoke trust.
7. **Esc origin.** Origin is per tab. It survives a failed connection and a retry (the tab stays on the failed path). It is dropped on user navigation outside the target (path bar, drive menu, another content). It is independent of `ViewHistory`; Esc with an origin goes to the origin's file list, without one it behaves as today.
8. **Files with comments.** UI writers (resize, setup options) ask before rewriting a file that has comments, as today; MCP `save_dashboard` refuses to overwrite such a file and reports it.
9. **Cap and timeout.** Above 50 items the first 50 (by sort order) are used and the dashboard shows a notice card "Pominięto N plików .c4k". When the 2 s listing limit trips the folder shows its ordinary file list, no retry (navigation is never held up).

## Approaches considered

- **A. New file format on input, old `FolderConfig`/`FolderAction` model inside.** Smallest change: the reader would build a synthetic `FolderConfig` from the files. Rejected by the user: it keeps the preview/enter split alive internally.
- **B. Rebuild the model around items (chosen by the user).** `FolderConfig` is replaced by a list of `C4kItem`; the resolver, dashboard, writers, trust and MCP are written around files. More work (about 38 files plus tests), cleaner result.

## Left to the builder

- Concrete names (`C4kItem`, `C4kFolder`, writer class), the exact set of JSON keys beyond the ones named, and the schema file.
- The `size` semantics of `FolderTile` carried to the item unchanged.
- Rule for dropping the origin, how to reuse the panel's listing, and how a card of an `open` item looks when there is no preview.
- Whether the first pass keeps the dashboard's hover ⤢ only, or removes hover editing completely if it complicates file writes.
- Order of work: reader and model first, then the dashboard and trust, then writers and MCP, then docs; the app must build and the tests pass after each step.
