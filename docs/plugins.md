# Commander4K plugins

A plugin adds **commands** (menu bar, command palette, key bindings), **panels** (a tree/table shown inside a file panel, with a details pane and actions), **previews** (the quick view, `Ctrl+Q`, for chosen file types). You can write one in **Python, Node.js, PowerShell 7, .NET or any executable**. Each plugin runs as a separate process and talks to Commander4K over a small JSON-RPC protocol.

The protocol is deliberately small and explicit so that both people and MCP clients such as Claude Code can write plugins from this document alone. The quickest start is the plugin manager (*Wtyczki ▸ Menedżer wtyczek…*, `Ctrl+N`), which copies a working example in your language into the plugins folder.

The SDK is open source (MIT, [`plugins-sdk/`](../plugins-sdk)). Each client is one dependency-free file.

## The golden rule: everything through the SDK

A plugin talks to the system **only** through the SDK client (`c4k`). That covers:
- files and folders, on any location: disks, shares, SFTP/FTP, archives, the registry;
- processes and programs;
- HTTP;
- system information;
- the clipboard;
- its own settings and data.

So it does **not** use its language's own I/O:

| Language | Do not use | Use instead (SDK) |
|---|---|---|
| Python | `open()`, `os.*` file/process functions, `shutil`, `pathlib` reads/writes, `subprocess`, `socket`, `urllib`, `requests`, `winreg`, `ctypes`, `print()` | `read_text`, `read_bytes`, `write_text`, `write_bytes`, `list_dir`, `stat`, `make_dir`, `remove`, `move`, `run`, `list_processes`, `kill_process`, `http_get`, `http_request`, `system_info`, `storage_get/set`, `log` |
| Node.js | `fs`, `child_process`, `net`, `http(s)`, `fetch`, `console.log` | `readText`, `readBytes`, `writeText`, `writeBytes`, `listDir`, `stat`, `makeDir`, `remove`, `move`, `run`, `listProcesses`, `killProcess`, `httpGet`, `httpRequest`, `systemInfo`, `storageGet/Set`, `log` |
| PowerShell | `Get-Content`, `Set-Content`, `Get-ChildItem`, `Remove-Item`, `Copy/Move/New-Item`, `Start-Process`, `Get-Process`, `Invoke-WebRequest`, `Invoke-RestMethod`, `Get-CimInstance`, `[System.IO.*]`, `& program.exe`, `Write-Host` | `Get-C4KText`, `Read-C4KBytes`, `Set-C4KText`, `Write-C4KBytes`, `Get-C4KChildItem`, `Get-C4KItem`, `New-C4KDirectory`, `Remove-C4KItem`, `Move-C4KItem`, `Invoke-C4KProcess`, `Get-C4KProcess`, `Stop-C4KProcess`, `Invoke-C4KHttpGet`, `Invoke-C4KHttpRequest`, `Get-C4KSystemInfo`, `Get/Set-C4KStorage`, `Write-C4KLog` |

Why it matters:
- the app checks every call against the plugin's permissions and writes it to the audit log;
- paths work on every location the user can open, not only local disks;
- **stdout is the protocol channel**: one stray `print` breaks the plugin.

Parsing text you have already read (JSON, XML, TOML, regular expressions) is fine with your language's standard library.

Commander4K checks plugin code for these bypasses:
- `check_plugin` and `install_plugin` (MCP) refuse such code and say what to change;
- the plugin manager lists every place (details, "Zgodność z SDK") and warns before enabling the plugin.

## Security model

- A plugin can only use what its manifest declares in `permissions`. Every request it makes to the app is checked against that list.
- Every call, whether allowed, denied or failed, is written to the audit log at `data/logs/plugins-YYYYMMDD.log`. The plugin manager shows the same log (`F3`).
- A new plugin is **disabled** until the user approves its permissions. If an update asks for more permissions, the user has to approve it again.
- **Elevated program runs** (`shell.run` with `elevated: true`) also ask the user every time.
- **Out of process:** the plugin is a separate process, so it never shares memory with Commander4K.
- **What the permissions do not stop:** they control what the plugin can do *through the app*. They are not an OS sandbox. A plugin process runs as your user and could still read your files with its own runtime's file functions. Install plugins only from authors you trust. The audit log and permissions show what a well-behaved plugin does, and they stop it from using the app to act on its behalf.

## Layout

```
plugins/
  my-plugin/
    plugin.json      manifest (required)
    main.py          entry point named in plugin.json
    c4k.py           protocol client copied from plugins-sdk/<language>/
```

A plugin keeps its settings and data with `storage_get` / `storage_set` (no permission needed: it only sees its own `data/plugin-data/<id>.json`), not in files next to its code.

Commander4K looks for plugins in a `plugins` folder next to `Commander4K.exe` and in the data folder: `%AppData%\Commander4K\plugins` for MSI installs, or next to the exe for portable copies.

## Manifest: `plugin.json`

The schema is in [`plugin.schema.json`](plugin.schema.json).

```json
{
  "$schema": "https://raw.githubusercontent.com/4kprojekt/Commander4K/main/docs/plugin.schema.json",
  "id": "acme.folder-tools",
  "name": "Folder tools",
  "version": "1.0.0",
  "author": "ACME",
  "description": "What it does, in one sentence.",
  "runtime": { "type": "python", "entry": "main.py", "version": ">=3.10" },
  "permissions": ["fs.read"],
  "contributes": {
    "commands": [
      { "id": "summary", "title": "Folder summary", "category": "ACME/Folders", "description": "…", "shortcut": "Ctrl+Alt+S" }
    ],
    "panels": [
      {
        "id": "sizes", "title": "Sizes", "description": "…",
        "requiresArgument": false, "refreshSeconds": 30,
        "actions": [ { "id": "open", "title": "Open", "shortcut": "Enter", "confirm": false, "appliesToRows": true } ]
      }
    ],
    "previews": [
      { "id": "csv", "title": "CSV table", "extensions": [".csv"], "priority": 60 }
    ]
  }
}
```

### Fields

| Field | Meaning |
|---|---|
| `id` | Lower case: letters, digits, `.`, `-`, `_`. It must be unique, and the `builtin.` prefix is reserved. |
| `runtime.type` | One of `python`, `node`, `powershell` (PowerShell 7 `pwsh`, or Windows PowerShell 5.1 as a fallback), `dotnet` (a `.dll` run by `dotnet`, or an `.exe`), or `exe`. |
| `runtime.entry` | The file to start. It must be inside the plugin folder. |
| `runtime.version` | A required range. Supported forms: `>=3.10`, `>18 <25`, `^7` (same major version), or a bare `3.10` (meaning `>=`). If the runtime is missing or too old, the plugin manager shows the reason and an install command (`winget …`). |
| `category` | The menu path under *Wtyczki*. A `/` creates a submenu. |
| `shortcut` | Panel actions only, e.g. `Enter`, `F3`, `Ctrl+R`, `Delete`. |

A `contributes.panels` entry may set `accepts`, `provides` (files to and from the other panel, see [Receiving and providing files](#receiving-and-providing-files)) and `card` (a dashboard widget, `panel.card`).

Commands get the keymap id `plugin:<pluginId>:<commandId>`, and panels get `panel:<pluginId>/<panelId>`. Users can bind them in the settings (`Ctrl+,`).

### Permissions

| Permission | Allows these app API calls |
|---|---|
| `fs.read` | `fs.list`, `fs.stat`, `fs.size`, `fs.readText`, `fs.readBytes`. They work on any location the app can open: disks, SMB shares, SFTP/FTP, archives, the registry. |
| `fs.write` | `fs.writeText`, `fs.writeBytes`, `fs.mkdir`, `fs.delete`, `fs.move` |
| `process.read` | `process.list` |
| `process.kill` | `process.kill` |
| `shell.run` | `shell.run` |
| `net` | `net.get`, `net.request` |
| `system.info` | `system.info` |
| `mail` | `mail.*` (see [Mail](#mail)). Calls that read or write message files also need `fs.read` / `fs.write` for those paths. |
| `background` | Lets the app call the plugin on a timer (see [Background work](#background-work)). Calls nothing itself. |

These calls need no permission:
- the UI and context calls: `ui.*`, `context.get`, `log.write`;
- the plugin's own storage: `storage.*`.

Ask for the fewest permissions that do the job.

## Protocol

**Transport:**
- The plugin reads **stdin** and writes **stdout**, encoded as UTF-8.
- There is **one JSON-RPC 2.0 message per line**. Escape newlines inside strings, as any JSON serializer does.
- Anything the plugin writes to **stderr** goes into the audit log.
- `C4K_PROTOCOL=1` is set in the environment.

**Startup:**
- The app starts the plugin process the first time one of its commands or panels is used.
- It restarts the process if it crashed, and sends `shutdown` when the app exits.
- **Both directions:** the app sends requests to the plugin, and while handling one, the plugin may send requests to the app (`fs.list`, `ui.confirm`…) and wait for the answers. Ids are any JSON value: use strings such as `"p1"` for your own requests.
- **Cancellation:** the app may send the notification `$/cancelRequest {"id": …}` when the user presses Esc. Plugins may ignore it.

### Requests from the app to the plugin

| Method | Params | Result |
|---|---|---|
| `initialize` | `{ protocol, app: {name, version}, locale, permissions }` | `{}` |
| `command.execute` | `{ command, context }` | `{ message?: string, text?: { title, content } }`. `message` appears in the status bar and `text` in a viewer window. |
| `panel.load` | `{ panel, argument, filter, context, options }` | `{ columns: [{ id, header, align?: "left"\|"right"\|"center", width? }], rows: [{ id, parentId?, cells: [string], severity?: "normal"\|"muted"\|"highlight"\|"warning"\|"error", sortKeys?: [number\|null], expanded? }], status? }` |
| `panel.details` | `{ panel, rowId }` | `{ sections: [{ title, items: [[key, value]] }] }` or `null` |
| `panel.action` | `{ panel, action, rowIds, context }` | `{ text?: string }`. Text is shown in a viewer, and the panel reloads afterwards. |
| `panel.receivers` | `{ panel, files: [{ path, name, kind: "file"\|"folder", size }], context }` | `[{ id, title, description?, extensions?, multiple?, folders?, returnsMessage? }]`. Panels with `accepts` only; answer within 5 s. See [Receiving and providing files](#receiving-and-providing-files). |
| `panel.receive` | `{ panel, receiver, jobId, files, context }` | `{ text?, results?: [{ path, ok, error? }] }`. One call for the whole selection; no time limit. |
| `panel.providers` | `{ panel, rowIds, context }` | `[{ id, title, description? }]`. Panels with `provides` only; answer within 5 s. |
| `panel.provide` | `{ panel, provider, jobId, rowIds, context }` | `{ files: [{ name, text? \| base64? \| url? \| error? }], text? }`. No time limit; the whole reply must stay under 16 MB. |
| `panel.card` | `{ panel, entry: { … } \| null, context, options, previous }` | Only for panels with `"card": true` in `contributes.panels`. The panel's dashboard widget, answered exactly like `preview.card` (same items, actions and `previous`). `entry` is the tile's `open` (or its `folder`), when it has one. A tile names it `"card": "<plugin id>/<panel id>"`; panels without `"card": true` get a plain card with their description and an Open button (`c4k:panel/<id>`). |
| `preview.render` | `{ preview, entry: { name, path, kind, size, … }, context, options, pinned }` | `{ kind: "markdown"\|"text", content, editable? }` or `{ kind: "tiles", title, description?, tileWidth?, tiles: [{ title, subtitle?, icon?, image?, target? }] }`. Or `{ kind: "web", page, state? }` (an HTML page from the preview's `webRoot`, see "Web previews"). A tile's `image` is a path the app reads with your `fs.read` permission; `target` is a path, an http(s) URL or another URI such as `steam://rungameid/570` (the user confirms the scheme once). Quick view (`Ctrl+Q`) of the entry under the cursor; read the file with `read_text`/`read_bytes`. Markdown is rendered with formatting (headings, tables, code). The highest `priority` wins among previews that accept the file. `extensions` items: `".csv"` (extension), `"package.json"` (file name), `"openapi*.json"` (pattern with `*`/`?`), `"/"` (folders). A preview may also return `{ kind: "chart", title, description?, chart }` (see `preview.card` for `chart`) or a form: `{ kind: "form", title, fields: [{ id, label, value, multiline? }], files: [{ name, path, size }], buttons: [{ label, target, shortcut? }], message?, messageError? }`. Any result may carry `path` (an absolute path other than the entry's): you renamed or moved the entry (a message marked read) or the view is now about another file (the draft of a reply), and the app follows that path for later renders, actions and form edits. Any result may carry `actions: [{ label, target, shortcut? }]`, shown as a bar of buttons above the content; a `shortcut` (`"R"`, `"Ctrl+Enter"`) works while the preview's panel is active (plain keys not while the user types in a form field). A `target` is what a tile target is, or `c4k:plugin/<action>?k=v` (build it with `action_target`), which calls your `preview.action`. `pinned` is `true` when the user opened the entry with Enter (the view stays on it, so it is the place for buttons) and `false` in the quick view that follows the cursor. With `"enter": true` in `contributes.previews`, Enter on a matching file opens this pinned view over the panel when your preview is the highest-priority match; Shift+Enter still opens the file with Windows, and Esc returns to the files. |
| `preview.action` | `{ preview, entry, options, action, args, values? }` | `null` (the app renders the entry again) or a new result in the `preview.render` shape. A `c4k:plugin/…` button or a dashboard card action (the card is read again afterwards); `args` holds the target's query, `values` the form fields when the button belongs to a form. |
| `preview.formChanged` | `{ preview, entry, options, values }` | `null`. The form's field values, sent 800 ms after the user stops typing and always before a button's `preview.action`, so you can keep a draft. |
| `preview.suggest` | `{ preview, folder: { … }, entries: [{ … }], truncated, context }` | Only for previews whose `suggest` rule has `"ask": true`, and only after the rule matched the folder under the cursor. `{ score (0–1), message, options? }` or `null`. Answers below 0.5 are ignored, and the best answer across all previews becomes a hint ("switch to …?"); accepting writes a `.c4k` file with `{ "plugin": <id>, "preview": <preview>, "options": <options> }`. `entries` is the folder listing (at most 500, `truncated` when cut). 5 s limit, process start included; an error or timeout means no suggestion. See "Folder suggestions". |
| `preview.message` | `{ preview, entry, method, params, context }` | Any JSON value. A web preview page called `c4k.request(method, params)`; answer it here (30 s limit). See "Web previews". |
| `preview.card` | `{ preview, entry: { … } \| null, context, options, previous }` | Only for previews with `"card": true` in `contributes.previews`. The small form shown on a `.c4k` dashboard: `{ title?, subtitle?, icon?, status?: "ok"\|"warning"\|"error", items: [...], actions: [{ label, target }] }`. Items: `{ kind: "value", label, value, status? }`, `{ kind: "progress", label, fraction (0–1), text? }`, `{ kind: "text", text }`, `{ kind: "thumbnails", images: [path] }` (at most 4, read with your `fs.read`), `{ kind: "chart", chart: { kind: "line"\|"bar", unit?, min?, max?, series: [{ name, points: [{ x, y, label? }] }] } }`. `previous` is the card you returned last time (thumbnails as a count), so a card refreshed on a timer can add a point to its chart; keep at most 120 points. An action `target` is a path, a URL, `c4k:refresh` (read the card again) or `c4k:plugin/<action>?k=v` (your `preview.action` with the card's entry; the card is read again afterwards). Previews without `"card": true` get a plain card with their title and an Open button. |
| `preview.setup` | `{ preview, folder, context }` | Only for folder previews with `"setup": true`. Sent when the user gives a folder this preview (*Widok folderu…*), after the plugin is installed and enabled and before the `.c4k` file is written. Ask what the folder needs in one `ui.dialog` window (or `ui.confirm` for a yes/no; the call may take up to 15 minutes) and answer `{ options: { … } }`; the app writes them as the item's `options`. Answer `null` when the user cancels: nothing is written. Throw to show an error. |
| `background.tick` | — | `{ more?: boolean }` or `null`. Only with the `background` permission and `contributes.background`; 60 s limit. See [Background work](#background-work). |
| `shutdown` | — | `null`, then exit |

Sensitive form fields: in a `preview.render` form field, set `"password": true` to mask the value in the UI. The value is still sent through the form protocol; plugins should not log or persist it unless the user explicitly asks.

`options` is the `"options"` object of the `.c4k` item when the panel or preview was opened through it (see [.c4k files](#c4k-files)), otherwise `null`.

`context` = `{ activePath, otherPath, selection: [{ name, path, kind, size, modified, hidden }] }`:
- `activePath` is the location of the file list the user is looking at;
- `selection` holds the marked entries, or the entry under the cursor.

Rows form a tree through `parentId`. The app handles sorting (column header click), filtering (typing), expand and collapse, marking (`Insert`) and the details pane. A plugin only describes data.

Long loads are fine: the panel shows a spinner and stays responsive. Requests time out after 60 s, except `panel.receivers`/`panel.providers` (5 s, a dialog is waiting) and `panel.receive`/`panel.provide` (no limit; the user cancels them). While a request waits for the user (`ui.confirm`, `ui.prompt`, `ui.dialog`, or an interactive mail call showing the password dialog), its clock stands still and starts again from the full time afterwards.

### Requests from the plugin to the app

| Method | Params | Result | Permission |
|---|---|---|---|
| `context.get` | — | context, as above | — |
| `log.write` | `{ message }` | `null` | — |
| `ui.notify` | `{ message, error?, target? }` | `null` | — (`target`: a path or link opened when the toast is clicked) |
| `ui.showText` | `{ title, text }` | `null` | — |
| `ui.confirm` | `{ title, message }` | `true`/`false` | — |
| `ui.prompt` | `{ title, message, initial? }` | string or `null` | — |
| `ui.dialog` | `{ title, fields: [{ id, type?, label?, value?, placeholder?, help?, required?, options?, visibleWhen?, min?, max?, button? }], description?, submit?, message?, errors? }` | `{ id: value }` or `null` | — (one window with many fields; see [Dialog windows](#dialog-windows-uidialog); audited without the answers) |
| `ui.navigate` | `{ path, focus? }` | `null` | — |
| `ui.openUrl` | `{ url }` | `null` | — (http/https only; opens the app's built-in browser panel, audited) |
| `ui.openFolder` | `{ path }` | `null` | — (the user is asked where to open it: Explorer or the active panel; nothing is created; audited) |
| `ui.search` | `{ query, engine? }` | `null` | — (`engine`: omitted = the user's search engine, or `google`, `bing`, `duckduckgo`; any other value uses the user's search engine; use it e.g. to let the user look up an error) |
| `ui.copyText` | `{ text }` | `null` | — (clipboard, write only) |
| `ui.editFile` | `{ path }` | `null` | — (opens the file in the user's editor from tools.json; audited) |
| `ui.openPanel` | `{ panel, argument? }` | `null` | — (`panel`: a panel id as in the keymap without the `panel:` prefix, e.g. `<plugin id>/<panel id>`) |
| `storage.get` | `{ key }` | any JSON value or `null` | — (the plugin's own data) |
| `storage.set` | `{ key, value }` (`null` removes; max 1 MB per plugin) | `null` | — |
| `storage.keys` | — | `[string]` | — |
| `fs.size` | `{ path }` (a folder, counted with everything below) | `{ bytes, files, folders, inaccessible }` (`inaccessible`: folders that could not be read) | `fs.read` |
| `fs.list` | `{ path }` | `[entry]` | `fs.read` |
| `fs.stat` | `{ path }` | entry or `null` (missing) | `fs.read` |
| `fs.readText` | `{ path, maxBytes? }` (default 1 MB, max 16 MB; a longer file is cut at a character boundary) | string (UTF-8) | `fs.read` |
| `fs.readBytes` | `{ path, offset?, length? }` (default 64 KB, max 16 MB) | `{ data (base64), bytesRead, eof }` | `fs.read` |
| `fs.writeText` | `{ path, content }` | `null` | `fs.write` |
| `fs.writeBytes` | `{ path, data (base64) }` | `null` | `fs.write` |
| `fs.mkdir` | `{ path, hidden? }` (creates parents; `hidden` sets the Hidden attribute on disks and shares) | `null` | `fs.write` |
| `fs.delete` | `{ path, recursive? }` | `null` | `fs.write` |
| `fs.move` | `{ from, to, overwrite? }` (also rename) | `null` | `fs.write` |
| `process.list` | — | `[{ pid, parentPid, name, path, memoryBytes, started }]` | `process.read` |
| `process.kill` | `{ pid, tree? }` | `null` | `process.kill` |
| `shell.run` | `{ file, args: [string], cwd?, elevated? }` | `{ exitCode, output, error }` | `shell.run` |
| `system.info` | — | `{ os, osVersion, architecture, machine, user, processors, memoryBytes, drives: [{ name, type, format, label, totalBytes, freeBytes }] }` | `system.info` |
| `net.get` | `{ url }` (http/https, max 5 MB) | string (error on non-2xx) | `net` |
| `net.request` | `{ url, method?, headers?, body?, contentType?, bodyFile?, multipart?: { field, path, fileName? } }` (http/https, response max 5 MB) | `{ status, headers, body }` (any status) | `net`; `bodyFile`/`multipart` also need `fs.read` or a [read grant](#receiving-and-providing-files) for that path |
| `job.progress` | `{ jobId, done, total, bytes?, text? }` — a **notification** (no `id`, no answer) | — | — |
| `mail.folders` | `{ account }` | `[{ serverName, localName, separator, special, uidValidity, count, unread }]` | `mail` |
| `mail.sync` | `{ account, folder, uidValidity, lastUid, modSeq, since?, knownUids?, limit? }` (`limit` = at most that many new messages, the lowest uids first) | `{ uidValidity, uidValidityChanged, modSeq, new: [{ uid, date, from, subject, flags, size, fileName, attachments }], flagChanges: [{ uid, flags }], removed: [uid], hasMore }` (`hasMore`: more new messages remain beyond `limit`; `attachments`: the message carries files, from its body structure) | `mail` |
| `mail.fetch` | `{ account, folder, uid, path }` (downloads the message to `path`) | `{ bytes }` | `mail` + `fs.write` |
| `mail.setFlags` | `{ account, folder, uids, add?, remove? }` | `null` | `mail` |
| `mail.move` | `{ account, folder, uids, target }` | `null` | `mail` |
| `mail.delete` | `{ account, folder, uids }` (to Trash; inside Trash the messages are expunged; an account without a Trash folder fails instead of expunging) | `null` | `mail` |
| `mail.append` | `{ account, folder, path, flags? }` (uploads a message file) | `{ uid }` (`null` when the server does not report it) | `mail` + `fs.read` |
| `mail.parse` | `{ path }` | `{ from, to, cc, subject, date, messageId, references, bodyText, attachments: [{ index, fileName, contentType, size }] }` | `mail` + `fs.read` |
| `mail.saveAttachment` | `{ path, index, target }` | `null` | `mail` + `fs.read` + `fs.write` |
| `mail.reply` | `{ path, mode: "reply"\|"replyAll"\|"forward", self }` | draft | `mail` + `fs.read` |
| `mail.compose` | `{ path, draft }` (writes the draft as a message file) | `null` | `mail` + `fs.write` (+ `fs.read` for attachments) |
| `mail.send` | `{ account, path }` (sends the draft file, then copies it to Sent) | `{ messageId }` | `mail` + `fs.read` |
| `mail.discover` | `{ email }`: finds the servers in order domain autoconfig, SRV, Thunderbird ISPDB, MX (Google/Microsoft 365 = OAuth2), Autodiscover, then probing `mail.`/`imap.`/`smtp.`; a certificate made out to another name makes that name a candidate. Each server must accept TLS (993/465) or a required STARTTLS with a valid certificate. Never logs in, ≤ 20 s | `{ imap, smtp: { host, port, security: ssl\|starttls, username } \| null, authMethods, source, confidence: high\|medium\|low, requiresUserConfirmation, confirmationReason, attempts }`. Show the server names before you log in when `requiresUserConfirmation` is set | `mail` |
| `background.wake` | `{}` (asks for a `background.tick` now) | — | `background` |

`entry` = `{ name, path, kind, size, modified, hidden }`:
- `kind`: `file`, `directory`, `drive`, `archive`, `share`, `host`, `registrykey`…;
- `size`: `-1` when unknown;
- `modified`: ISO 8601 or `null`.

**Errors** use standard JSON-RPC codes:
- `-32000`: the call failed (file not found, time-out, server error…). The message says why.
- `-32001`: permission denied. The message names the missing permission.
- `-32010`: mail login needed, with `data: { authRequired: true, realm }`.
- `-32011`: the mail server's certificate is not trusted yet, with `data: { host, fingerprint }`.
- `-32012`: `mail.send` got no confirmation (timeout, lost connection), so the message may or may not have been sent, with `data: { outcomeUnknown: true }`. Do not simply send it again.
- `-32601`: unknown method.
- `-32602`: invalid parameters.
- `-32800`: the request was cancelled.

The SDK clients turn these into exceptions.

**Audit:** every call is written to the audit log under its method name. Reads and writes are logged by what they do: `fs.readText`/`fs.readBytes` as `fs.read`, `fs.writeText`/`fs.writeBytes` as `fs.write`. `ui.notify`, `context.get` and `job.progress` are not logged.

**Threads:** the app answers every request off its UI thread, and your handlers may take as long as the limits below allow: the window never waits for a plugin.

### Receiving and providing files

A panel can take files from a file panel (`"accepts": true` in its `contributes.panels` entry) and hand rows back
as files (`"provides": true`).

**Receiving** — the user marks files in one panel, your panel is open in the other, and presses F5:
1. The app calls `panel.receivers` with the selection and shows the receivers that fit. It checks `extensions`
   (`.ext`, file names, `*`/`?` patterns, as in previews), `multiple` (more than one item) and `folders` itself, so
   you may return every receiver and let the app filter. A missing or empty `extensions` (`[]`) means any file.
   Folders are judged only by `folders`: `extensions` never applies to them. Receiver ids must be unique; a
   repeated id keeps its first entry (the same goes for provider ids).
2. The user picks one and the app calls `panel.receive` **once for the whole selection** as a background job.
3. **While that call runs, your plugin may read the offered files** — and everything under offered folders — with
   `fs.readBytes`/`fs.readText`/`fs.list`/`fs.stat`, even without `fs.read`. The grant ends with the call (also when
   it is cancelled). It covers every request your process makes meanwhile. The grant is by path: links (junctions,
   symbolic links) inside an offered folder are followed, so whatever they point to is readable too. Every granted
   path is written to the audit log.
4. To upload without loading the file into memory, pass the path to `net.request` as `bodyFile` (raw body, sent
   with `contentType`, default `application/octet-stream`) or as `multipart: { field, path, fileName? }`. The app
   streams it through its own file system layer, so it works for shares, SFTP and archives too. Such uploads have
   no time limit (other `net.*` calls stop after 10 minutes); they end when the job is cancelled, which also ends
   the read grant.
5. Return `{ text?, results? }`: `text` is shown to the user (set `returnsMessage` on the receiver), `results` lists
   per-file failures (`{ path, ok: false, error }`). Your panel reloads afterwards.

**Providing** — your panel is active and the user presses F5:
1. The app calls `panel.providers` with the selected row ids; the user picks one (no choice when there is one) and
   the target folder (the other panel, editable; created when missing) and overwrite policy.
2. The app calls `panel.provide` and writes each returned file itself: `text` (UTF-8) or `base64` inline, up to
   8 MB per file; larger files as an `http`/`https` `url` the app downloads (other schemes are refused). **The whole
   reply is one protocol message and must stay under 16 MB** (base64 adds a third): a longer reply closes the
   connection, fails the job with "the reply exceeds 16 MB" and restarts your plugin on its next call — return big
   or many files as `url`. `name` may contain subfolders (`docs/a.txt`), never `..` or an absolute path. A file that
   breaks these rules (too large, no content, another URL scheme, no name) is reported to the user; the other files
   are still written.
3. When one row cannot become a file (its download failed, it has no data), return `{ name, error: "why" }` for it:
   the app shows the reason and writes nothing for that entry. Every written, skipped or failed file is audited
   under your plugin's id.

Only copying works this way: F6 (move) into or out of a plugin panel is refused with a message, and so is F5 onto a
panel without `accepts` (or onto the browser) — nothing is copied into the folder hidden under it.

**Progress:** `panel.receive` and `panel.provide` carry a `jobId`. Send `job.progress { jobId, done, total, bytes?,
text? }` as a notification (`report_progress` in the SDKs) and the job's progress bar follows it; without it the bar
is indeterminate. Unknown ids are ignored. Cancelling the job sends `$/cancelRequest`.

**Keys:** in a panel with `provides`, F5 (or whatever key the user bound to copying) provides files and **Ctrl+R**
refreshes; panels without `provides` keep F5 = refresh. A panel action declared with `"shortcut": "F5"` still wins
over both, so do not bind F5 to an action in a panel that provides files.

### Mail

The `mail` permission gives a plugin IMAP/SMTP access **without** the credentials. The app keeps the passwords (encrypted, per plugin, user and server) and the trusted certificates; the plugin names an account and the app does the rest.

- `account` = `{ imap: "host:993", smtp: "host:465" | null, user, interactive? }`. `interactive` defaults to `true`.
- TLS is mandatory: ports 993 and 465 use implicit TLS, any other port requires STARTTLS. Plain-text login is never attempted. A certificate that fails validation is trusted only after the user confirms its SHA-256 fingerprint.
- An interactive call may show the password or certificate dialog. With `interactive: false` (background work) the app never shows a dialog: a missing or refused password fails with `-32010`, an untrusted certificate with `-32011`. After `-32010` stop background syncs for that account until an interactive call succeeds.
- Limits: at most 2 IMAP connections per account (closed after 5 minutes idle) and 30 seconds per operation (fetch and send add 1 second per 100 KB). Every call can be cancelled.
- Flags are the strings `seen`, `flagged`, `answered` and `draft`.
- Messages live as `.eml` files at paths the plugin chooses; the app writes and reads them through the file permissions. A forward lists the original's attachments as `<file>.eml#<n>` in `draft.attachments`, and `mail.compose` copies them from there.
- A `draft` is `{ to, cc, bcc, subject, body, inReplyTo, references, attachments, from }`. Addresses are `{ name, address }` (a plain string is accepted as the address).
- File names in attachments come from the sender and are untrusted: `mail.saveAttachment` writes only to the `target` the plugin gives, so build that name yourself and sanitize it.
- Every call is written to the audit log with the account and counts, never with message content or passwords.

### Background work

A plugin with the `background` permission and `contributes.background: { "interval": "5m" }` is called by the app on a timer (`background.tick`), also while its panel is closed. The interval is at least one minute, one tick runs per plugin at a time, a tick that takes longer than 60 seconds is abandoned, and a tick that answers `{ "more": true }` is followed by the next one after 2 seconds. A tick should do bounded work (for example at most 50 downloads or 15 seconds) and report `more` instead of running long. The user can switch background work off in the settings (`PluginBackground`, "Wtyczki mogą działać w tle"); while it is off, or until the user has approved the `background` permission, no tick is sent.

- `contributes.background.interval` is `<n>s`, `<n>m` or `<n>h`; shorter than a minute is raised to one minute. A manifest that declares `contributes.background` without the `background` permission, or with an interval that does not parse, is rejected. A plugin may contribute only `background`.
- Answer `background.tick` with a handler (`@c4k.handler("background.tick")`, `c4k.on('background.tick', …)`, `Register-C4KHandler 'background.tick'`). The result is `{ "more": true }` when there is more work (the next tick follows after 2 seconds) or `null` / `{}` when done. The first tick runs right after the app has started the plugin loop, then once per interval after the previous tick ended.
- `background.wake` (plugin to app, needs `background`; `background_wake()`, `backgroundWake()`, `Request-C4KBackgroundTick`) asks for a tick as soon as the current one ends. Use it after the plugin learns that something changed, for example after a user action.
- A tick that gets no answer within 60 seconds is abandoned and the next one runs on schedule; a failed tick is written to the audit log. Ticks stop when the app closes (it waits at most 2 seconds).

## SDK clients

Copy one file into your plugin folder:

| Language | File | Main loop |
|---|---|---|
| Python ≥ 3.9 | [`plugins-sdk/python/c4k.py`](../plugins-sdk/python/c4k.py) | `@c4k.handler("panel.load")` … `c4k.serve()` |
| Node.js ≥ 18 | [`plugins-sdk/node/c4k.js`](../plugins-sdk/node/c4k.js) | `c4k.on('panel.load', async p => …)` |
| PowerShell 7 | [`plugins-sdk/powershell/c4k.ps1`](../plugins-sdk/powershell/c4k.ps1) | `Register-C4KHandler 'panel.load' { param($p) … }` … `Start-C4KPlugin` |

For .NET, write the ~100-line loop yourself with `System.Text.Json`, following [`JsonRpcConnection`](../src/Commander4K.Core/Plugins/JsonRpcConnection.cs). Every client is a single dependency-free file.

### SDK reference

Every helper maps to one protocol method above:
- Python helpers block until the app answers.
- Node helpers return Promises.
- PowerShell functions return hashtables.
- A refused call raises (`HostError` in Python and Node, a terminating error in PowerShell). Code `-32001` means a permission is missing from `plugin.json`. `HostError` carries `code` and `data`; in PowerShell they are the exception's `Data['Code']` and `Data['ErrorData']`.

| Protocol method | Python (`import c4k`) | Node (`const c4k = require('./c4k')`) | PowerShell (`. "$PSScriptRoot/c4k.ps1"`) |
|---|---|---|---|
| handlers | `@c4k.handler("command.execute")` + `c4k.serve()` | `c4k.on('command.execute', async p => …)` | `Register-C4KHandler 'command.execute' { param($p) … }` + `Start-C4KPlugin` |
| `context.get` | `context()` | `context()` | `Get-C4KContext` |
| `log.write` | `log(message)` | `log(message)` | `Write-C4KLog $Message` |
| `ui.notify` | `notify(message, error=False, target=None)` | `notify(message, error, target)` | `Show-C4KNotification $Message [-IsError] [-Target $Target]` |
| `ui.showText` | `show_text(title, text)` | `showText(title, text)` | `Show-C4KText $Title $Text` |
| `ui.confirm` | `confirm(title, message)` | `confirm(title, message)` | `Confirm-C4K $Title $Message` |
| `ui.prompt` | `prompt(title, message, initial="")` | `prompt(title, message, initial)` | `Read-C4KInput $Title $Message $Initial` |
| `ui.dialog` | `dialog(title, fields, description=None, submit=None, message=None, errors=None)` | `dialog(title, fields, { description, submit, message, errors })` | `Show-C4KDialog $Title $Fields [-Description] [-Submit] [-Message] [-Errors]` |
| `ui.navigate` | `navigate(path, focus=None)` | `navigate(path, focus)` | `Open-C4KPath $Path [$Focus]` |
| `ui.openUrl` | `open_url(url)` | `openUrl(url)` | `Open-C4KUrl $Url` |
| `ui.openFolder` | `open_folder(path)` | `openFolder(path)` | `Open-C4KFolder $Path` |
| `ui.search` | `search(query, engine=None)` | `search(query, engine)` | `Search-C4KWeb $Query $Engine` |
| `ui.copyText` | `copy_text(text)` | `copyText(text)` | `Copy-C4KText $Text` |
| `ui.editFile` | `edit_file(path)` | `editFile(path)` | `Edit-C4KFile $Path` |
| `ui.openPanel` | `open_panel(panel, argument=None)` | `openPanel(panel, argument)` | `Open-C4KPanel $Panel [$Argument]` |
| — (`c4k:plugin/…` target for `preview.action`) | `action_target(action, **args)` | `actionTarget(action, args)` | `Get-C4KActionTarget $Action @{ k = v }` |
| — (result of `preview.render`) | `web_preview(page, state=None)` | `webPreview(page, state)` | `New-C4KWebPreview $Page [$State]` |
| — (result of `preview.suggest`) | `folder_suggestion(score, message, options=None)` | `folderSuggestion(score, message, options)` | `New-C4KFolderSuggestion $Score $Message [$Options]` |
| `storage.get` / `.set` | `storage_get(key, default)` / `storage_set(key, value)` | `storageGet(key, fallback)` / `storageSet(key, value)` | `Get-C4KStorage $Key $Default` / `Set-C4KStorage $Key $Value` |
| `storage.keys` | `storage_keys()` | `storageKeys()` | `Get-C4KStorageKeys` |
| `fs.list` | `list_dir(path)` | `listDir(path)` | `Get-C4KChildItem $Path` |
| `fs.stat` | `stat(path)` | `stat(path)` | `Get-C4KItem $Path` |
| `fs.size` | `dir_size(path)` | `dirSize(path)` | `Get-C4KFolderSize $Path` |
| `fs.readText` | `read_text(path, max_bytes=1048576)` | `readText(path, maxBytes)` | `Get-C4KText $Path [$MaxBytes]` |
| `fs.readBytes` | `read_bytes(path, offset=0, length=65536)` → `bytes` | `readBytes(path, offset, length)` → `Buffer` | `Read-C4KBytes $Path [$Offset] [$Length]` → `byte[]` |
| `fs.writeText` | `write_text(path, content)` | `writeText(path, content)` | `Set-C4KText $Path $Content` |
| `fs.writeBytes` | `write_bytes(path, data)` | `writeBytes(path, data)` | `Write-C4KBytes $Path $Data` |
| `fs.mkdir` | `make_dir(path, hidden=False)` | `makeDir(path, hidden = false)` | `New-C4KDirectory $Path [-Hidden]` |
| `fs.delete` | `remove(path, recursive=False)` | `remove(path, recursive)` | `Remove-C4KItem $Path [-Recurse]` |
| `fs.move` | `move(source, target, overwrite=False)` | `move(from, to, overwrite)` | `Move-C4KItem $Path $Destination [-Force]` |
| `process.list` | `list_processes()` | `listProcesses()` | `Get-C4KProcess` |
| `process.kill` | `kill_process(pid, tree=False)` | `killProcess(pid, tree)` | `Stop-C4KProcess $Id [-Tree]` |
| `shell.run` | `run(file, args=(), cwd=None, elevated=False)` | `run(file, args, cwd, elevated)` | `Invoke-C4KProcess $File $Arguments [$WorkingDirectory] [-Elevated]` |
| `system.info` | `system_info()` | `systemInfo()` | `Get-C4KSystemInfo` |
| `net.get` | `http_get(url)` | `httpGet(url)` | `Invoke-C4KHttpGet $Url` |
| `net.request` | `http_request(url, method="GET", headers=None, body=None, content_type=None, body_file=None, multipart=None)` | `httpRequest(url, { method, headers, body, contentType, bodyFile, multipart })` | `Invoke-C4KHttpRequest $Url [$Method] [$Headers] [$Body] [$ContentType] [-BodyFile] [-MultipartField -MultipartPath [-MultipartFileName]]` |
| `job.progress` | `report_progress(job_id, done, total, bytes=0, text=None)` | `reportProgress(jobId, done, total, { bytes, text })` | `Send-C4KProgress -JobId -Done -Total [-Bytes] [-Text]` |
| `mail.folders` | `mail_folders(account)` | `mailFolders(account)` | `Get-C4KMailFolders $Account` |
| `mail.sync` | `mail_sync(account, folder, uid_validity, last_uid, mod_seq, since, known_uids, limit)` | `mailSync(account, folder, { uidValidity, lastUid, modSeq, since, knownUids, limit })` | `Sync-C4KMail $Account $Folder [$UidValidity] [$LastUid] [$ModSeq] [$Since] [$KnownUids] [$Limit]` |
| `mail.fetch` | `mail_fetch(account, folder, uid, path)` → bytes written | `mailFetch(account, folder, uid, path)` | `Save-C4KMail $Account $Folder $Uid $Path` |
| `mail.setFlags` | `mail_set_flags(account, folder, uids, add, remove)` | `mailSetFlags(account, folder, uids, add, remove)` | `Set-C4KMailFlags $Account $Folder $Uids [$Add] [$Remove]` |
| `mail.move` | `mail_move(account, folder, uids, target)` | `mailMove(account, folder, uids, target)` | `Move-C4KMail $Account $Folder $Uids $Target` |
| `mail.delete` | `mail_delete(account, folder, uids)` | `mailDelete(account, folder, uids)` | `Remove-C4KMail $Account $Folder $Uids` |
| `mail.append` | `mail_append(account, folder, path, flags)` → uid or `None` | `mailAppend(account, folder, path, flags)` | `Add-C4KMail $Account $Folder $Path [$Flags]` |
| `mail.parse` | `mail_parse(path)` | `mailParse(path)` | `Read-C4KMail $Path` |
| `mail.saveAttachment` | `mail_save_attachment(path, index, target)` | `mailSaveAttachment(path, index, target)` | `Save-C4KMailAttachment $Path $Index $Target` |
| `mail.reply` | `mail_reply(path, mode="reply", self_address="")` | `mailReply(path, mode, self)` | `New-C4KMailReply $Path [$Mode] [$Self]` |
| `mail.compose` | `mail_compose(path, draft)` | `mailCompose(path, draft)` | `Write-C4KMailDraft $Path $Draft` |
| `mail.send` | `mail_send(account, path)` → Message-ID | `mailSend(account, path)` | `Send-C4KMail $Account $Path` |
| `mail.discover` | `mail_discover(email)` | `mailDiscover(email)` | `Find-C4KMailConfig $Email` |
| `background.wake` | `background_wake()` | `backgroundWake()` | `Request-C4KBackgroundTick` |
| any other | `call("method", **params)` | `call('method', params)` | `Invoke-C4K 'method' @{ … }` |

Minimal plugin (Python): a quick view for `.ini` files:

```python
import c4k

@c4k.handler("preview.render")
def render(params):
    text = c4k.read_text(params["entry"]["path"])
    rows = [line.split("=", 1) for line in text.splitlines() if "=" in line]
    table = "| Key | Value |\n|---|---|\n" + "\n".join(f"| {k.strip()} | {v.strip()} |" for k, v in rows)
    return {"kind": "markdown", "content": f"# {params['entry']['name']}\n\n{table}"}

c4k.serve()
```

Its `plugin.json` declares `"permissions": ["fs.read"]` and `"contributes": { "previews": [ { "id": "ini", "title": "INI", "extensions": [".ini"], "priority": 60 } ] }`.

## Building the interface

A plugin never draws controls. It returns data, and the app draws it with its own design system and the user's skin. So a plugin looks like the rest of the app, follows skin changes, and can be written in any language. There are four building blocks; pick the smallest one that does the job.

| Block | Where | Returned by | Use it for |
|---|---|---|---|
| Panel table | a file panel | `panel.load` | lists and trees with columns, a details pane and actions on marked rows |
| Markdown view | the preview / a folder view | `preview.render` → `{ kind: "markdown" }` | reports, summaries, read-only pages; add `actions` for buttons |
| Preview form | the preview / a folder view | `preview.render` → `{ kind: "form" }` | editing that stays on screen (a draft, a note): text fields, attached files, buttons |
| List with preview | a folder view | `preview.render` → `{ kind: "list" }` | the files of a folder as rows (messages, tickets) with the preview of the selected one under the list or in the other panel |
| Message reader | the preview | `preview.render` → `{ kind: "mail" }` | a message: sender, attachments, text, source and a quick reply |
| Dialog window | a modal window | your call to `ui.dialog` | settings, setup and logins: one window with many fields, sections and modes |

`ui.prompt` (one line) and `ui.confirm` (yes/no) stay for single questions. Do not chain several `ui.prompt` calls: use one `ui.dialog`.

### Markdown views and buttons

Markdown is rendered with formatting: headings, lists, tables, code, quotes and `mermaid` diagrams. There is no HTML engine; for a full HTML page use a [web preview](#web-previews).

Any `preview.render` result may carry `actions`, a bar of buttons above the content. A button's `target` built with `action_target` (`Get-C4KActionTarget` in PowerShell) comes back to your `preview.action` handler, which returns `null` (render again) or a new result:

```powershell
Register-C4KHandler 'preview.render' {
    param($p)
    $md = "# $($p.entry.name)`n`n| Klucz | Wartość |`n|---|---|`n| Rozmiar | $($p.entry.size) B |"
    @{ kind = 'markdown'; content = $md; actions = @(@{ label = 'Odśwież'; target = (Get-C4KActionTarget 'refresh'); shortcut = 'R' }) }
}
Register-C4KHandler 'preview.action' { param($p) if ($p.action -eq 'refresh') { $null } }
```

`pinned` is `true` when the user opened the entry with Enter; that is the place for buttons that change something.

An action may add `icon` (one of `mail inbox sent drafts trash archive junk folder reply replyAll forward delete flag unread move attachment new refresh send edit download`) and `style`: `primary` (the main action, accent colour) or `icon` (an icon button; the label becomes its tooltip). Without them the button looks as before.

### Lists with a preview

`{ kind: "list", folder, items: [{ path, title, subtitle?, snippet?, meta?, group?, tags? }], title?, tabs?: [{ label, target, count?, selected?, icon? }], filters?: [{ tag, label }], search?, status?, statusRight?, selected?, empty?, more?: { label, target }, searchTarget?, query? }` shows the files under `folder` as two-line rows (design "Tryb panelu — klient poczty"). Every `path` must be a file in `folder` or one of its subfolders (the mail plugin keeps messages in `year\month` folders); other rows are dropped.

- Selecting a row (click, ↑↓, PgUp/PgDn, Home/End) puts the panel's cursor on that file. The preview under the list follows the cursor like Ctrl+Q, so it shows whatever preview the file has: yours, another plugin's or a built-in one. When the opposite panel shows the quick view of this panel, the list has no preview of its own and the selected file appears there.
- Enter or a double click opens the file like Enter in the file list.
- Search (Ctrl+F) matches `title`, `subtitle` and `snippet`; a filter shows the rows whose `tags` contain its `tag` (empty `tag` = all). The tags `unread` (bold, a dot), `flagged` and `attachment` (icons) also change the row. Rows with the same `group` follow one heading.
- Large folders: send one page and `more` (a button under the last row; answer with the longer list). With `searchTarget` the search field asks you: 350 ms after typing stops, that action runs with `values: { query }` and you answer with the matches and `query` set, so search reaches rows you did not send. The field, the filter and the scroll stay while you answer.
- A tab's `target` runs like an action (`Get-C4KActionTarget 'folder' @{ name = ... }`); answer with the other list. `actions` appear beside the search field.

### Message reader

`{ kind: "mail", subject, from, fromAddress?, to?, date?, attachments?: [{ name, size?, target? }], body, source?, folder?, quickReply?: { target, placeholder?, from? }, status?, note? }` shows a message: the subject, the sender with an avatar, attachment tiles (a click runs the tile's `target`), the text with a "Treść · Tekst · Źródło" switch and `actions` in the bar above. `quickReply` adds a text box; "Wyślij" (Ctrl+Enter) runs its `target` with `values: { text }`.

### Preview forms

`{ kind: "form", title, fields: [{ id, label, value, multiline?, password? }], files, buttons, message?, messageError? }` keeps text fields inside the preview. The app sends `preview.formChanged` with all values 800 ms after the user stops typing (save a draft there) and passes the same `values` to `preview.action` when a button is pressed. Return the form again with `message` / `messageError: true` to report a problem. The mail plugin's message drafts are this kind of form.

### Dialog windows: `ui.dialog`

`ui.dialog` opens a modal window built like the app's own dialogs (the SFTP connection window, for example): a header, section headings, labels above fields, help text under them, an error line per field and Cancel / OK in the footer. The call returns when the user closes the window: an object with the answers by field id, or `null` for Cancel.

| Request key | Meaning |
|---|---|
| `title` | Window title (required). |
| `description` | Text under the header. |
| `fields` | The fields, in order (required, at most 60). |
| `submit` | Label of the OK button (default "OK"), e.g. "Zapisz", "Połącz". |
| `message` | An error shown above the fields (a refused login…). |
| `errors` | `{ fieldId: "message" }`: errors shown under those fields. |

| Field key | Meaning |
|---|---|
| `id` | Key of the answer; unique and not empty (a heading may leave it empty). |
| `type` | `text` (default), `multiline`, `password`, `number`, `select` (drop-down list), `segmented` (a switch of 2–4 modes), `checkbox`, `file`, `folder` (a text box with a "Wybierz…" picker), `heading` (a section title, no value). |
| `label`, `placeholder`, `help` | Label above the field (beside a checkbox), grey text inside an empty box, muted text under the field. |
| `value` | Initial value: text, a number, `true`/`false`, or an option's `value`. |
| `options` | For `select` and `segmented`: `[{ value, label }]` or plain strings. |
| `visibleWhen` | `{ otherFieldId: value or [values] }`: the field is shown only while every named field has one of those values. This is how modes work (password vs key). |
| `required` | The window does not close while the field is empty (a checkbox: not ticked). Checked only while the field is visible. |
| `min`, `max` | Range for `number`. |
| `button` | Label of a button beside a `text`/`password`/`number` box (e.g. `Auto`). It closes the window without checks; the answers then hold every field as typed plus `"`$button": "<field id>"`. Do the work and call `ui.dialog` again with the new `value`s (the mail plugin's "Auto" fills in the servers this way). |

Answers: `number` fields answer a number (or `null` when empty), checkboxes `true`/`false`, all others text. Hidden fields and headings are left out, so a field of the mode the user did not pick never reaches you. Text is trimmed except for `password` and `multiline`.

The app checks only `required` and number ranges. Anything else (the format of an address, a login that must work) is your check: when it fails, call `ui.dialog` again with the answers as `value`s and `errors` or `message`. The user sees the same window again with the problem marked. Password fields are masked; never log or store their values (the app keeps real passwords for you where it can, e.g. mail logins). The audit log records the window title and field ids, never the answers.

```powershell
function Read-Settings($Previous, $Errors, [string] $Message) {
    $v = { param($Id, $Default) if ($Previous -and $null -ne $Previous[$Id]) { $Previous[$Id] } else { $Default } }
    $fields = @(
        @{ id = 'server'; type = 'heading'; label = 'Serwer' }
        @{ id = 'host'; label = 'Adres'; value = (& $v 'host' ''); placeholder = 'nas.lan'; required = $true }
        @{ id = 'port'; type = 'number'; label = 'Port'; value = (& $v 'port' 22); min = 1; max = 65535 }
        @{ id = 'auth'; type = 'segmented'; label = 'Logowanie'; value = (& $v 'auth' 'password')
           options = @(@{ value = 'password'; label = 'Hasło' }, @{ value = 'key'; label = 'Klucz' }) }
        @{ id = 'keyFile'; type = 'file'; label = 'Plik klucza'; value = (& $v 'keyFile' ''); visibleWhen = @{ auth = 'key' }; required = $true }
        @{ id = 'save'; type = 'checkbox'; label = 'Zapamiętaj'; value = (& $v 'save' $true) }
    )
    Show-C4KDialog 'Połączenie' $fields -Submit 'Połącz' -Message $Message -Errors $Errors
}

$answers = $null; $errors = $null
while ($true) {
    $answers = Read-Settings $answers $errors
    if ($null -eq $answers) { return $null }                 # Cancel
    $errors = @{}
    if ($answers['host'] -match '\s') { $errors['host'] = 'Adres nie może zawierać spacji.' }
    if ($errors.Count -eq 0) { break }                       # otherwise the same window again, with the error
}
```

The same in Python is `c4k.dialog(title, fields, description=None, submit=None, message=None, errors=None)` and in Node `c4k.dialog(title, fields, { description, submit, message, errors })`. A dialog is the natural answer to `preview.setup` (a folder plugin asks for its options once) and to a command such as "Dodaj konto". The time the user spends in the window does not count against the time limit of the request you are handling (the same holds for `ui.prompt`, `ui.confirm` and an interactive mail login). Open dialogs only while handling something the user started, never from `background.tick`.

### A PowerShell plugin step by step

1. Make a folder `plugins/my-plugin/` with `plugin.json`, `main.ps1` and a copy of [`plugins-sdk/powershell/c4k.ps1`](../plugins-sdk/powershell/c4k.ps1). The plugin manager (`Ctrl+N` → `powershell`) creates a working one.
2. In `plugin.json` set `"runtime": { "type": "powershell", "entry": "main.ps1", "version": ">=7.2" }`, the smallest `permissions` and what you contribute (`commands`, `panels`, `previews`).
3. `main.ps1` dot-sources the client, registers one handler per request and starts the loop:

   ```powershell
   . "$PSScriptRoot/c4k.ps1"
   Register-C4KHandler 'command.execute' {
       param($p)
       $answers = Show-C4KDialog 'Powitanie' @(@{ id = 'name'; label = 'Imię'; required = $true })
       if ($answers) { Show-C4KNotification "Cześć, $($answers['name'])!" }
       $null
   }
   Start-C4KPlugin
   ```

4. Do every file, process and network operation with the `*-C4K*` functions from the [SDK reference](#sdk-reference), never with PowerShell's own cmdlets. Write diagnostics with `Write-C4KLog`.
5. Enable the plugin in the plugin manager and read its log there (`F3`) when something fails. Claude Code over MCP does the same with `check_plugin`, `install_plugin`, `render_preview` and `get_plugin_log`.

The examples [`git-powershell`](../examples/plugins/git-powershell) (command + panel), [`plugins-catalog/steam-library-powershell`](../plugins-catalog/steam-library-powershell) (folder view with tiles) and [`plugins-catalog/mail-powershell`](../plugins-catalog/mail-powershell) (Markdown views, forms, a setup dialog, cards, background work) show complete plugins.

### PowerShell: returning values

The client runs a handler as `$result = & $handler $params`, and PowerShell sends **everything the handler outputs** into `$result`, not only the last line. The usual mistakes:

- **Stray output becomes part of the answer.** `$list.Add($x)` on an `ArrayList`, `$hash.Remove($k)` or a cmdlet that returns something turn the answer into an array, and the app rejects it. Discard such values: `[void] $list.Add($x)`, `$null = …`, or use `[System.Collections.Generic.List[object]]`, whose `Add` returns nothing.
- **Arrays unroll.** A function that returns a one-item array returns the item; an empty array becomes `$null`. Return lists with a leading comma, `, $rows.ToArray()`, or wrap them where you use them: `rows = @(Get-Rows)`.
- **Nesting depth.** Answers are serialized with `ConvertTo-Json -Depth 30`; anything deeper becomes a type name string.
- **Parameters are hashtables.** `$p.entry.path` works, but values arrive as JSON types: cast where it matters (`[string] $p.entry.path`, `[bool] $p.pinned`).
- **Errors.** `$ErrorActionPreference` is `Stop`, so an uncaught error ends the request and its message is shown to the user. Catch the ones you can explain and throw a short sentence instead.
- **Never write to stdout.** `Write-Output`, `Write-Host`, a bare expression at script level or `echo` break the protocol; `Write-C4KLog` goes to the plugin log.

## Web previews

A preview can show an HTML/JS page in WebView2 instead of text: viewers, charts, editors. Declare the folder with the page in the preview contribution and return `kind: "web"` from `preview.render`:

```json
"previews": [ { "id": "viewer", "title": "Viewer", "extensions": [".pdf"], "webRoot": "web" } ]
```

```json
{ "kind": "web", "page": "index.html", "state": { "zoom": "page-width" } }
```

- `webRoot` is required for `kind: "web"`: a relative folder inside the plugin (no `..`, no drive letters). The plugin is refused if the folder does not exist; a `web` result from a preview without `webRoot` shows an error message.
- `page` is a path inside `webRoot` (`/` separators). A page that is missing, rooted or leaves `webRoot` shows an error message. `state` is any JSON value, handed to the page as `c4k.state`.
- The page is served from `https://<label>.plugin.c4k/`, one origin per plugin id: an id made only of `a-z`, `0-9` and single inner hyphens (at most 63 characters) is the label itself; any other id becomes a readable part (lower case, other characters as `-`, shortened), `--` and the first 16 hex digits of the SHA-256 of the exact id (`examples.pdf-viewer` → `examples-pdf-viewer--<hash>`). Read `location.host` rather than building it. Only files under `webRoot`, with a Content Security Policy that allows that origin plus `data:` and `blob:` (inline scripts and `eval` are allowed, `object`, `form` submissions and other origins are not). It cannot read the rest of the plugin folder or the disk, and it has its own WebView2 profile, without the browser panel's cookies or sign-ins. Service workers cannot be registered (their script requests get 403).
- Nothing leaves the machine: requests to any other host get 403, and the profile's network goes to a proxy that does not exist, so WebSockets and WebRTC fail too. A top-level navigation to another address is cancelled and the preview stays. Only an http(s) link the user clicks (a plain link, `target="_blank"`, or `window.open` called from the click) opens in the app's browser panel (at most once a second); navigations a script starts on its own (`location.href = …`, `window.open` from a timer) open nothing. Frames may show only the page's own files, `about:` and `blob:`. Downloads are cancelled, permission prompts (camera, location, notifications…) are denied, and other URI schemes (`mailto:` …) do not launch.
- The page reads the file being previewed from `c4k.fileUrl`, `https://<host>/__entry/<token>/<name>`. The token is new for every file shown; an old token gets 404. `GET` and `HEAD` are supported, with a single `Range: bytes=a-b`, `a-` or `-n`: 206 with `Content-Range`, 416 when the range starts beyond the end, 200 with the whole file without a header or with one the app does not understand (several ranges, other units). Responses about the file (200, 206, 416) carry `Accept-Ranges: bytes`; `Content-Type` follows the file extension (`application/octet-stream` when unknown). The app reads the file with the plugin's `fs.read` permission, audited: without the permission every request gets 403. A file that is not seekable (inside an archive, on some remote servers) is first copied to a temporary file. Opened on its own (in a frame or as the page), the file is sandboxed content without scripts.
- `c4k.request(method, params)` reaches the plugin as the request `preview.message` with `{ preview, entry, method, params, context }`; the handler's return value resolves the page's promise, an error rejects it. Register it like any handler (`@c4k.handler("preview.message")`). The SDK helper `web_preview(page, state)` / `webPreview(page, state)` / `New-C4KWebPreview` builds the `preview.render` result.
- Files in `webRoot` are page code, not plugin code: `PluginSourceCheck` does not lint them. They have no SDK; the page talks to the app only through the `window.c4k` bridge.

The `window.c4k` bridge exists in the top frame before the page's own scripts run:
- `c4k.entry` = `{ name, path, kind, size, modified }` (like `entry` in the protocol, without `hidden`), `c4k.fileUrl`, `c4k.state` (the `state` from `preview.render`, or `null`) and `c4k.skin` = `{ background, foreground, accent, muted, selection, border, fontFamily, fontSize }` (colours as `#rrggbb`, the size in pixels).
- `c4k.on(event, fn)` returns a function that unsubscribes. `"entry"`: the cursor moved to another file that the same page of the same plugin shows; the page is not reloaded, `fn({ entry, fileUrl, state })` is called and the `c4k` fields are already updated (another page or plugin navigates instead). `"skin"`: `fn(skin)` after the user changed the skin.
- `c4k.request(method, params)` returns a promise: the plugin's `preview.message` handler gets `{ preview, entry, method, params, context }`, its result resolves the promise and its error rejects it (with an `Error` carrying the message). A request still running when the cursor moves to another file never settles. At most 16 requests per file may be unanswered at once; more are rejected with `too many requests`.
- `c4k.notify(text, error = false)` shows a message in the status area; `c4k.copyText(text)` puts text on the clipboard. Together they are limited to 5 calls a second per file; extra calls are dropped silently.
- Keys: F1–F12 (with any modifiers), Esc, Tab, Ctrl+Q, Alt combinations without Ctrl and Ctrl+Shift combinations without Alt are offered to the application first: when the app has a command for the key it runs it (Esc closes the preview, Ctrl+Q toggles it) and the page never sees the key; otherwise the page gets it. Ctrl+Alt+<character key> always goes to the page, because Ctrl+Alt is AltGr on many keyboards (Polish ą, ł…). Every other key stays in the page. If the page receives Esc and does not call `preventDefault()`, the preview closes. The page has the default context menu (copy) and Ctrl+wheel zoom.

## Examples

These are in [`examples/plugins`](../examples/plugins), and the app embeds them as templates:

- **`folder-stats-python`** (`fs.read`): a command, a panel of file extensions with details and an action, and a quick view rendering `.csv` files as a table. It also has a command that tries to write a file, to show how the app refuses.
- **`site-check-node`** (`net`): a command, and a panel that refreshes itself every 60 s.
- **`git-powershell`** (`shell.run`): `git status`, and a commit history panel. `git` runs through the app, so each command line is audited.
- **`project-modules-python`** (`fs.read`): a dedicated quick view for chosen files. It shows the dependencies of `package.json`, `*.csproj`, `pyproject.toml`, `requirements*.txt`, `go.mod`, `Cargo.toml` and `composer.json`, and the modules of an HTTP API from OpenAPI/Swagger files (endpoints grouped by tag, plus data models). It shows file names and patterns in `extensions`.
- **`upload-client-python`** (`net`): a panel that takes and gives files (`accepts`/`provides`, see [Receiving and providing files](#receiving-and-providing-files)). F5 from a file panel POSTs the marked files to a saved HTTP endpoint (multipart, with progress and a result message), and F5 on an endpoint saves its GET response as a file. It needs no `fs.read`: the app grants reading the offered files during the call.
- `site-check-node` keeps its site list in the plugin's storage (`storageGet`/`storageSet`), not in a file.

[`pdf-viewer-node`](../examples/plugins/pdf-viewer-node) (`fs.read`) is the web preview example. It is not embedded as a template (PDF.js is ~6 MB) and is not in the catalog; copy the folder into `plugins/`. `main.js` only returns `webPreview("index.html", { zoom })` and stores the zoom that the page sends with `c4k.request("saveZoom", { zoom })`. The page (`web/`) runs PDF.js 5 (`PDFViewer`, `PDFFindController`) under the page CSP:
- ES modules and the worker load from the page's own origin (`GlobalWorkerOptions.workerSrc`, `cMapUrl`, `standardFontDataUrl`, `wasmUrl` as absolute URLs built from `import.meta.url`, since PDF.js does not resolve relative ones).
- `getDocument({ url: c4k.fileUrl, rangeChunkSize: 65536, disableAutoFetch: true })` reads the file in ranges; on `c4k.on("entry")` the page destroys the old loading task and opens the new file without a reload.
- Colours come from `c4k.skin` as CSS variables and follow `c4k.on("skin")`. `Ctrl+F` stays in the page and focuses the search box.
- Printing renders every page at 150 dpi into a container that only `@media print` shows, calls `window.print()` and empties it on `afterprint`.

When the quick view cannot recognise a file (only hex is left), it offers official previews from the catalog and a prompt for Claude Code (over MCP) that explains the format or writes a preview plugin for that type, from a sample of the file.

## Panels: everything is a folder

Each side of the window is a row of tabs, and **a tab is a folder plus what it shows**. What it shows is a plugin:

| Content | Title on the tab | Sends to the other panel (F5 here) | Receives from the other panel (F5 there) |
|---|---|---|---|
| File list (the default) | The folder's name | Marked files and folders (copy, move with F6) | Files and folders, written into its folder |
| Preview or a folder's own view (a `.c4k` `view` or `plugin` preview: gallery, notes, mail, Steam…, or the dashboard of several `.c4k` files) | The folder's name; the path bar shows the view's title (the item's `title`) | Nothing (Esc shows the files, then F5) | Files, written into the folder under the view |
| Plugin panel (`builtin.sysinfo`, `builtin.taskmanager`, `builtin.powershell`, plugin panels) | The folder's name when bound to it, otherwise the panel's title | With `provides`: the selected rows as files (`panel.provide`) | With `accepts`: the files, handed to the plugin (`panel.receive`) |
| Browser, app pages (settings, skins) | The page's title | Nothing | Nothing |

F6 (move) works only between folders: into or out of a plugin panel it is refused. The app decides all this from what the content declares (`accepts`, `provides`), never from the plugin's own checks, and a refused transfer says why in the status bar.

**Bound to a folder, or opened by itself.** A content is either the folder's own (its `.c4k` file names it, or it is an app page such as the plugin manager on `data/plugins`) or opened ad hoc (F2, the command palette, a shortcut such as `Ctrl+Alt+I`). Any panel can be bound to a folder; a folder with this `system.c4k` opens the system information on Enter:

```json
{ "title": "Ten komputer", "panel": "builtin.sysinfo" }
```

**Files ↔ view.** The path bar of a file list has *Pliki · Auto · Widok*, kept per tab. *Pliki* never reads `.c4k` files; *Auto* shows the folder's view when it has `.c4k` files; *Widok* always shows a view ("Brak zdefiniowanego podglądu" when there is none). On a folder's own view the same switch, or Esc, shows the folder's files in the default file list plugin, and *Auto*/*Widok* brings the view back. A content opened by itself has no switch: Esc closes it. `Shift+Enter` always enters a folder as plain files.

**Every panel is a widget.** A dashboard tile can show any panel as a card: `{ "title": "System", "card": "builtin.sysinfo" }`, `"builtin.taskmanager"`, or `"<plugin id>/<panel id>"` for plugin panels with `"card": true` (they answer `panel.card`). `"card": true` on a folder whose `.c4k` file opens a panel shows that panel's widget. The card's Open button (`c4k:panel/<id>`) opens the panel opposite the dashboard.

**For AI clients (MCP).** `get_context` describes each panel's content in one line: what it is, whether it is bound to the folder, and what it provides or receives. `set_panel_mode` switches a panel between `files`, `preview` and `panel:<id>`; `load_plugin_panel` loads a panel's rows; `dashboard_guide` lists the panels that draw widgets. Write a `.c4k` file into a folder with `write_file` (or `save_dashboard` for dashboards) to bind a view to it.

## `.c4k` files

A folder can say how it wants to be shown. Every `*.c4k` file in it (JSON with comments and trailing commas, schema [`c4k.schema.json`](c4k.schema.json)) describes **one item**: a view, a plugin, a panel, a connection, a link, a command or an API request. What the folder shows follows from how many there are:

- no `.c4k` file: the ordinary file list;
- one file: that item is the folder's view. A view or plugin item is shown by the quick view (Ctrl+Q on the folder) and pinned on Enter; an `open`, `url` or `run` item happens on Enter;
- two or more: a **dashboard** with one tile per file, ordered by file name in natural order (`2-…` before `10-…`, case ignored). Rename a file to move its tile; delete it to remove the tile.

At most 50 files are read (the first 50 by name; the dashboard shows "Pominięto N plików .c4k" for the rest), each up to 256 KB. A broken file becomes an error tile and never hides the others. The listing has a 2 s limit and runs only on disks, shares, SFTP and FTP; when it trips, the folder shows its files. `.c4k` files are ordinary visible files: the panel marks them with their own icon and `<WIDOK>`, and Enter on one runs that item. Shift+Enter always opens the plain file list, *Pliki* on the path bar never reads `.c4k`, and the setting "Pliki .c4k w folderach" turns the feature off. `cm_FolderView` (*Widok folderu (plik .c4k)*) writes a new file from a short choice; the file is named after the title or plugin and made unique (`mail.c4k`, `mail 2.c4k`), and existing files are never overwritten. A writer that would add a second file to a folder with one item asks first, because the folder becomes a dashboard.

The file name without `.c4k` is the default `title`. One target per item:

| Target | Example | What happens |
|---|---|---|
| `view` | `"gallery"`, `"notes"`, `"markdown"`, `"files"`, `"api"` | Built-in views: image thumbnails (made in the background and kept in the folder's hidden `.thumbnail.c4k`, named `<image>.<size>-<mtime>.webp`, stale ones deleted), Markdown notes with their links, a Markdown file (`file`, README.md by default), the plain list, the fields of an HTTP API (`api`, GET and HEAD only). `"settings"`, `"skins"` and `"plugins"` are the app's own pages (settings, skins, plugin manager): the app writes `settings.c4k`, `skins.c4k` and `plugins.c4k` into `config/`, `config/skins/` and the plugins folder when those have no `.c4k` file, and *Pliki* switches between the page and the files. A second `.c4k` file in one of these folders turns it into a dashboard. |
| `plugin` (+ `preview`) | `{ "plugin": "official.steam-library" }` | The plugin's preview (`contributes.previews`, `extensions: ["/"]`) renders the folder, with `options`. On Enter it fills the panel; Esc returns to the files. |
| `panel` (+ `plugin`) | `{ "panel": "builtin.sysinfo" }`, `{ "plugin": "examples.git", "panel": "log" }` | Opens a panel plugin with the folder as `argument` and `options`: a built-in panel by its id, a plugin's panel by `plugin` + `panel` (or `"<plugin>/<panel>"`). |
| `open` (+ `user`, `keyFile`) | `{ "open": "sftp://me@nas.lan/srv", "keyFile": "~/.ssh/id_ed25519" }` | Goes there instead; relative paths are resolved against the file's folder. The tab remembers that folder as its origin: Esc (or *Pliki*) goes back to its file list, also after a failed connection, which stays on screen with its error. Navigating elsewhere by hand drops the origin. |
| `url` | `{ "url": "https://grafana.lan" }` | The page in the panel browser. |
| `run` | `{ "run": { "command": "build.ps1", "args": ["-Release"], "cwd": "src" } }` | Runs a command, on disks and shares only. The user sees it first and trusts this file (see below). |

Other keys: `title`, `description`, `icon`, `image` (a picture for the tile), `options` (passed to the plugin), `folder` (the item's context folder, relative to the file or absolute; the file's folder by default: `preview.setup` is asked about it, and a card without `open` shows it, e.g. a mail account kept in another folder), and the card fields below.

```json
{
  "$schema": "https://raw.githubusercontent.com/4kprojekt/Commander4K/main/docs/c4k.schema.json",
  "title": "Gry",
  "plugin": "official.steam-library",
  "options": { "sort": "size" }
}
```

**Trust.** An item that runs a command, sends a request that may change something (`api` with a method other than GET/HEAD), signs in to an SSH server (`ssh` card) or reads an SFTP/FTP target as a card stays locked until the user allows **that file** ("Zezwól"). Trust is stored in `config/trusted-folders.json` per file path, with a hash of only those fields (`run`, the request's method, URL, body and headers, the server, `user` and `keyFile`, also inside an `ssh` card's `options`), so resizing a tile or changing its title keeps it, while another command, server or login, or the same file under another name, asks again. On a dashboard each card is locked or live on its own.

A plugin that is not installed, is disabled, or has `"setup": true` but no `options` in its item shows a setup card instead. Its button installs the plugin from the official catalog (one consent), enables it, sends `preview.setup` so the plugin can ask for its options in a dialog, writes the answer as `options` into the item that names the plugin, and shows the view. Writers ask before rewriting a file that has comments (they would be lost).

The official `official.git-repo` plugin works this way: `git-repo.c4k` = `{ "plugin": "official.git-repo" }` in a repository shows its branch, changes, recent commits, branches, tags and remotes, read with `git` through `shell.run` (options `commits`, `files`, `branches`).

To make a plugin for folders, contribute a preview with `"extensions": ["/"]` and a negative `priority`, so it only shows folders whose `.c4k` file names it, and return `kind: "tiles"` or `markdown` from `preview.render`. Read `options` for your settings.

### Folder suggestions

When the cursor rests on a folder without any `.c4k` file (on a disk or a share), the app reads its listing once and asks every preview that can judge folders. The built-in gallery (5+ images, 60 % of the files) and notes (`.obsidian`, or 5+ Markdown files, 60 % of the files) do this, and so can yours; `.c4k` files never count as content. The best answer appears as a small hint in the panel. Accepting it (button or `Ctrl+Enter`) writes a `.c4k` file naming your preview (`{ "plugin", "preview", "options" }`) and opens the view.

Add a `suggest` rule to the preview contribution:

```json
{ "id": "tables", "title": "Tabele CSV w folderze", "extensions": ["/"], "priority": -1,
  "suggest": { "files": [".csv"], "minCount": 3, "minShare": 0.5, "ask": true } }
```

- The app checks the rule itself, without starting your plugin: `files` patterns (like `extensions`), at least `minCount` matching files making at least `minShare` of all files (folders do not count), and every name in `contains`. For a folder with more than 500 entries, the rule is checked on the first 500.
- Without `ask`, a match is a suggestion with score 0.7 and the text from `message` (`{ "pl": "…", "en": "… {count} …" }`, the preview title by default).
- With `"ask": true`, a match sends `preview.suggest`, and your answer decides: `folder_suggestion(score, message, options)`, or `None`/`null` for "not for me".
- Scores below 0.5 are ignored. On a tie, a plugin wins over the built-in views, then the higher `priority` wins. The example `folder-stats-python` has such a preview.

### Dashboard cards

On a dashboard, an item with `"card"` is a live card drawn by a preview instead of a plain shortcut tile (icon, title, description; a click performs the item). Built-in views, `url` and `run` items are shortcut tiles. A dashboard folder might hold:

```jsonc
// 01 zdjecia.c4k — four thumbnails, count, size, Open
{ "title": "Zdjęcia", "open": "photos", "card": "gallery" }
// 02 dokumenty.c4k — whatever Ctrl+Q would show for it
{ "title": "Dokumenty", "open": "~/Documents", "card": true }
// 03 serwery.c4k — a folder that is itself a dashboard: a dashboard card
{ "title": "Serwery", "open": "servers", "card": true }
// 04 vps.c4k
{ "title": "VPS", "open": "sftp://kamil@vps.example.com/", "card": "ssh", "refresh": "10s", "options": { "keyFile": "~/.ssh/id_ed25519" } }
// 05 grafana.c4k
{ "title": "Grafana", "card": "api", "refresh": "1m", "size": "wide",
  "api": { "url": "https://grafana.example.com/api/health", "headers": { "Authorization": "{secret:grafana}" },
           "fields": [ { "path": "database", "label": "Baza", "as": "status", "ok": ["ok"] }, { "path": "version" } ] } }
// 06 konfiguracja.c4k
{ "title": "Konfiguracja", "open": "config.json", "card": "official.json-preview" }
// 07 tokio.c4k
{ "title": "Tokio", "card": "clock", "options": { "timeZone": "Asia/Tokyo" } }
// 08 dyski.c4k
{ "title": "Dyski", "card": "disks", "size": "tall" }
// 09 notatka.c4k
{ "title": "Na piątek", "card": "note", "size": "full", "options": { "text": "Kopia NAS i przegląd certyfikatów." } }
```

- `card`: `true` = the preview Ctrl+Q would pick for the target (`open`, else `folder`), including that folder's own `.c4k` files; or a name: `files`, `gallery`, `notes`, `markdown`, `dashboard`, `api`, `ssh`, the widgets `clock`, `note`, `disks`, a panel id (`builtin.sysinfo`, `builtin.taskmanager`, `plugin/panel`; see [Panels](#panels-everything-is-a-folder)), or a plugin id (`plugin` or `plugin/preview`). A named card ignores the target's `.c4k` files. A card that points at a folder with a dashboard shows that dashboard's title and tiles, and a click opens it in place: ← (Alt+←, Backspace) goes back, the breadcrumbs jump, and Esc shows the files of the folder on screen.
- `refresh`: `10s`, `5m`, `1h` (at least 2 s), only while the dashboard is visible. Without it the card is read once and has "Odśwież".
- `size`: the tile's footprint on the dashboard grid, columns × rows: `small` 1×1 (default), `wide` 2×1, `tall` 1×2, `large` 2×2, `huge` 3×2, `full` (the whole row, 1 high). The grid has as many columns as fit the panel (at least 240 px each) and places tiles in order, filling a hole beside a taller tile with the next small ones; spans wider than the grid shrink to it. A taller card shows more text lines, a taller chart and bigger thumbnails. The ⤢ button over a tile (shown on hover) cycles the sizes and writes `size` into that tile's file. `options`: passed to the card's preview.
- A card whose answer has exactly one value (an `api` card with one field, for example) is drawn as a KPI: the number large, coloured by its status, the label under it.
- Widget cards need no folder: `clock` (time and date; `options.timeZone` is an IANA or Windows id; read every 15 s), `note` (`options.text`, or the start of the text file in `open`) and `disks` (used and free space of this computer's disks, or of the disk holding `open`; read every minute). `refresh` overrides those intervals.
- `api`: `url`, `method` (GET by default), `headers`, `body`, `contentType`, `fields` (`path` such as `a.b` or `items[0].name`, `label`, `as`: `value`, `chart` or `status`, `unit`, `ok`). `cm_AddApiCard` asks the API once, lists the fields of its answer and writes a new `.c4k` file with the card. `{ "view": "api", "api": { … } }` shows the same fields as a folder's view (GET and HEAD only).
- Secrets: write `{secret:name}` in a header or the body (never in the url, which is logged); the value lives encrypted in `config/secrets.json`, bound to the origin (scheme, host, port) it was first saved for, and a card that misses it offers to enter it. Never put tokens in a `.c4k` file.
- Consent: `api` cards with methods other than GET/HEAD, every `ssh` card and cards that read an SFTP/FTP target wait until the user trusts their file (the same trust as `run`), and only on disks and shares.
- `ssh` runs one fixed, read-only command (uname, `/proc`, `df`) with the saved login or keys; non-Linux servers show their status only. `options.keyFile` (and `options.user`) pick the key, else the item's own `keyFile`/`user`; like every path in the file, a relative `keyFile` is resolved against the folder and `~` is the user's profile.

The official catalog has preview plugins with cards: `official.json-preview`, `official.openapi-preview`, `official.html-preview`, `official.pe-info` (EXE/DLL: architecture, version, signer, execution level, missing DLLs) and `official.archive-info` (file count, unpacked size, compression, package id and version). For your own, see `preview.card` (previews) and `panel.card` (panels) in the protocol table.

### Example: mail ("Poczta")

The official mail plugin (`official.mail`) keeps an account in a folder and shows it as a card on a dashboard. There are two ways in. The command *Dodaj konto pocztowe* (menu *Wtyczki ▸ Poczta*) creates a new account folder under the active one. Choosing *Poczta* in *Widok folderu…* turns the current folder into an account through `preview.setup`. Both open one `ui.dialog` window: address, sender name, (folder name), servers "Z adresu" or "Ręcznie" (`visibleWhen` shows IMAP/SMTP only for the manual mode), how often to check and how far back to fetch. The plugin then logs in once (the app asks for the password and keeps it; it never reaches a file); a refused login opens the same window again with the reason, so nothing is written until it works. By hand, the account folder `Poczta/firma` holds `firma.c4k`:

```jsonc
{
  "plugin": "official.mail",
  "options": { "imap": "imap.example.com:993", "smtp": "smtp.example.com:465", "user": "ja@example.com", "refresh": "5m", "since": "3m" }
}
```

When the active folder is already a dashboard (two or more `.c4k` files; with exactly one the command asks first), the command also writes a card for the new account there, so `Poczta/` shows every account with its unread count (the plugin needs the `background` permission and the setting *PluginBackground* for the sync between visits):

```jsonc
// Poczta/firma.c4k
{ "title": "firma", "plugin": "official.mail", "card": true, "folder": "firma", "open": "firma", "refresh": "30s" }
```

The card reads only the message files and `.mail/state.json`; *Sprawdź teraz* syncs that account at once (at most 10 s, the rest continues in the background). Enter on the card opens the account folder.

## Official plugin catalog

[`plugins-catalog/catalog.json`](../plugins-catalog/catalog.json) lists the plugins that can be installed from the plugin manager (group "Katalog oficjalnych wtyczek", Ctrl+I) and from `.c4k` files. The app downloads it at start-up from the repository, so new plugins appear without an update. Each entry gives the plugin folder (relative to `baseUrl`) and the SHA-256 of every file. The hash is computed over the UTF-8 text with CR removed. A downloaded file that does not match is refused. An installed plugin starts disabled until the user approves its permissions, like any other.

To add a plugin, put its folder in `plugins-catalog/` (official) or `examples/plugins/`, add an entry (`id`, `name`, `runtime`, `description` per language, `tags`, an `example` `.c4k` item, `path`), then run `pwsh ./plugins-catalog/Update-Catalog.ps1` to fill in the files and hashes. `PluginCatalogTests` fails if the catalog is out of date.

## Writing a plugin by hand

Commander4K has no AI of its own. To have a model write a plugin, connect Claude Code (or another MCP client) and let it use `get_plugin_guide`, `check_plugin` and `install_plugin`: see the next section. By hand:

1. declare the smallest possible `permissions`;
2. send all file, process, network and program work through the app API listed above, never through the language's own file or process functions;
3. keep the plugin in one folder: `plugin.json`, the entry file and the copied SDK client.

Then copy the folder into `plugins/`. Open the plugin manager and check the permissions it asks for before you enable it.

## Writing plugins with Claude Code over MCP

Commander4K is also an MCP server (Model Context Protocol, Streamable HTTP, protocol 2025-06-18). Claude Code connected to it can look at your panels, write a plugin into a folder you chose, check it, install it and try it, while you confirm each risky step in the app.

**Connect.** Open *Połącz Claude Code* (command `cm_McpConnect`, or click the MCP badge in the status bar), switch the server on and add at least one allowed folder. The window shows the command to run once in a terminal:

```
claude mcp add --transport http commander4k http://127.0.0.1:47800/mcp --header "Authorization: Bearer <token>"
```

It also shows the same server as a `.mcp.json` entry for a project. Use `127.0.0.1` exactly: the listener is bound to that address and HTTP.sys rejects requests whose `Host` header says `localhost`. Every request needs the bearer token. "Nowy token" replaces it, and the old token stops working at once. The server listens on loopback only and refuses browser origins other than localhost. The badge shows "AI podłączone (n)" while clients are connected, and says so when the port is in use.

**What the client knows at once.** The `initialize` answer carries server instructions built for that session: the built-in panels and every enabled plugin with the call that uses it (`run_plugin_command` for commands, `load_plugin_panel` for panels, `render_preview` and the `.c4k` line for previews, the dashboard card names), so the client uses installed plugins without being told. `list_plugins` gives the current state later.

**Tools.** Each tool has a mode, set per tool in the settings (`Ctrl+,`, search "mcp"): allow, ask or block. The defaults are:

| Tool | Default | What it does |
|---|---|---|
| `get_context`, `list_commands`, `list_plugins`, `get_plugin_guide` | allow | The app's state, commands, installed plugins, this guide with the schema and an example. |
| `list_directory`, `stat` | allow | Folder listings and entry details, inside the allowed folders. |
| `navigate`, `select`, `set_panel_mode` | allow | Drive the panels. |
| `check_plugin`, `get_plugin_log` | allow | Validate a plugin without installing it (manifest, limits, SDK bypasses); read an installed external plugin's recent log lines (not built-ins such as the browser, and not other MCP clients' calls). |
| `read_file`, `write_file`, `make_directory` | ask | Read and write files inside the allowed folders. |
| `move`, `delete` | always ask | No "yes for this session" for these. |
| `run_command`, `run_plugin_command`, `load_plugin_panel`, `render_preview`, `reload_plugin` | ask | Run app or plugin commands, open plugin panels, render a preview, restart a plugin's process. |
| `run_panel_action` | always ask | Run one action of a panel plugin on rows it listed (end a process, a plugin panel's own actions). Ids come from `load_plugin_panel`; the plugin manager's actions are refused. |
| `run_preview_action` | always ask | Press a button of a plugin's preview (`c4k:plugin/<action>` targets, as `render_preview` lists them) and return what it shows afterwards. |
| `render_card` | ask | Read one dashboard card from its item JSON as text (values, progress, chart series, actions). Cards that wait for trust are refused. |
| `disable_plugin` | ask | Disable an installed plugin; nothing is deleted, and `enable_plugin` asks again. |
| `install_plugin`, `enable_plugin` | always ask | Show the plugin's permissions in their own dialog. |
| `list_settings`, `list_key_bindings` | allow | The settings (as in `Ctrl+,`: key, value, allowed values) and the keyboard shortcuts. |
| `set_setting`, `set_key_binding` | ask | Change a setting (applied at once and saved) or bind keys to a command (saved to `keymap.json`). |
| `get_translation_kit`, `check_translation` | allow | The translation rules and the texts to translate, page by page (`onlyMissing` to complete a language); check translated entries without saving. |
| `save_translation` | ask | Merge translated entries into `config/i18n/<code>.json`. Refused for wrong placeholders or plural forms and unknown keys; `pl` is the source language. |
| `get_skin_guide`, `list_skins`, `get_skin`, `check_skin` | allow | The skin brief, schema and current skin; the skins; one skin's JSON; schema and contrast check. |
| `dashboard_guide` | allow | How to build a folder dashboard: one `.c4k` file per tile, ordered by file name, item fields and sizes, the grid, every card type with its options (and the cards of the installed plugins), KPI tiles, design rules and recipes. |
| `get_dashboard`, `check_dashboard` | allow | A folder's `.c4k` files (name and JSON of each); a draft (a list of named items) checked without saving: errors, warnings (missing plugins, cards that wait for trust, design hints) and the grid drawn at 2, 3 and 4 columns. |
| `save_dashboard` | ask | Writes one `.c4k` file per item into an allowed folder after the same checks (refused with errors, with a `folder` key outside the allowed folders, or when it would rewrite a file with comments); the dialog shows the files. The file is never trusted by this: `run`, SSH and POST cards stay locked until "Zezwól". |
| `save_skin` | always ask | Shows the skin live in the window while you decide; "Zapisz skórkę" saves it to `config/skins/<id>.json` and switches to it, anything else puts the old skin back. |

A blocked tool is still listed, with "(Disabled by the user in Commander4K.)" at the end of its description, and calling it returns an error. Prompts: `write_plugin`, `preview_plugin_for_file`, `explain_file_format`, `translate_ui`, `design_skin`, `build_dashboard` (guide → read the folder → plan the tiles and KPIs → check until clean → save and show it). The two prompts about a file include its first bytes only when the file is allowed, not protected, and `read_file` is not blocked; each of them is audited as `prompt:<name>`.

**Allowed folders.** File tools only work inside the folders listed in the connect window (or the setting "Foldery dostępne dla MCP"). Any VFS path can be one: a disk folder, `\\server\share`, `sftp://…`. With an empty list no file can be touched. A path outside them is refused with a message that tells the model to ask you. The same applies to folders the app tools take from the panels: `get_context` shows a panel's contents only when its folder is allowed (and not protected), `select` needs the chosen panel's folder, and `run_command`, `run_plugin_command`, `load_plugin_panel`, `run_panel_action`, `render_card` and `render_preview`/`run_preview_action` with a plugin's preview (the plugin is sent the panels' context) need both panels' folders and the marked entries, inside the allowed folders and outside the protected ones. `load_plugin_panel` passes `argument` only to panels that take a file, after the same check; other panels never see it. "Dodaj folder z aktywnego panelu" accepts real folders only: disk paths, `\\server\share`, `sftp://` and `ftp://` (not This PC, `net://`, the registry or archives; type those into the setting if you really mean it).

**Protected folders.** No tool can read or change the `plugins/` folders (next to the exe and in the data folder), `config/` (approvals, encrypted credentials and the MCP token, known hosts, trusted folders), `data/` (audit logs, plugin storage, the browser profile with its sign-ins) or the folder of `Commander4K.exe` itself (a DLL placed there would load on the next start), even when they sit inside an allowed folder. Only `list_directory` and `stat` work there, because they return names and sizes, not contents. `navigate`, the panel checks of the app tools and the `explain_file_format` prompt refuse them too. Writing tools (`write_file`, `make_directory`, `move`, `delete`), `render_preview` (a folder preview reads its children) and the recursive `folder` argument of `check_plugin` and `install_plugin` are also refused on a folder that contains a protected one. Paths with NTFS stream syntax (a `:` after the drive, as in `file.txt:stream` or `folder::$INDEX_ALLOCATION`) are refused by every tool, because they reach real files under a different name. The refusal reads "Commander4K's own plugin, config and data folders cannot be accessed through MCP." Plugins get into `plugins/` only through `install_plugin`.

**Confirmations.** A tool in "ask" mode opens a dialog in Commander4K: "Tak", "Nie" or "Tak do końca sesji" (yes for this tool and client until the server stops or you press "Cofnij zgody z tej sesji"). Esc, Enter and closing the dialog mean "Nie"; in the install and enable dialogs of `install_plugin` and `enable_plugin`, Enter and Esc mean "Anuluj". The `write_file` dialog shows the start of the text to be written (up to 2000 characters), or "binary, N bytes" for base64; that preview is not written to the audit log. Only one dialog is shown at a time, and parallel calls wait their turn. A call that the client cancels, or that is still waiting when the server stops, closes its dialog as "Nie". A declined call returns "The user declined this action in Commander4K." The client name in the dialog and the log is what the client reported about itself; do not rely on it.

**Configuration, translations and skins.** The client is the model and the app has no AI of its own: these tools hand out the instructions (translation kit, skin brief and schema), check the answer and save it. They take no paths: files are named only from a checked language code (`de`, `pt-BR`) or skin id (`solarized-dark`), and `config/` stays closed to the file tools. Some settings are read-only over MCP, whatever the mode and even after "Tak do końca sesji": everything under MCP (server, port, allowed folders, tool modes) and the plugin catalog and update addresses. `list_settings` marks them, and `set_setting` answers that they can only be changed in the settings window. External tools (`tools.json`) and saved connections are not reachable either.

**Audit.** Every call is written to `data/logs/plugins-YYYYMMDD.log` as `mcp:<client>` with the tool, a summary and the outcome. The connect window lists the last 50 calls.

**The plugin flow.**

1. `get_plugin_guide` (optionally with `runtime` or `example`) returns this guide, the manifest schema and an example plugin.
2. Write the files into a folder inside the allowed folders with `write_file`.
3. `check_plugin` with `folder` (or `files`) reports manifest errors and SDK bypasses. Fix them until it is clean.
4. `install_plugin` shows its permissions to the user and installs it into `plugins/<id>`, disabled. Code that bypasses the SDK is not installed.
5. `enable_plugin` shows the permissions again; on "yes" the plugin starts.
6. Try it with `run_plugin_command`, `load_plugin_panel` (and `run_panel_action` for a panel's actions), `render_preview` (and `run_preview_action` for its buttons) or `render_card`. `render_preview` returns every kind of result as text: lists, messages, forms, charts, the actions, and for a web preview the page name with its `state`. Read problems with `get_plugin_log`. After editing the code, call `install_plugin` again (it replaces the installed code), then `reload_plugin`; `reload_plugin` also restarts a plugin that hangs. Grown permissions need `enable_plugin` again.
