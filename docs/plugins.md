# Commander4K plugins

A plugin adds **commands** (menu bar, command palette, key bindings), **panels** (a tree/table shown inside a file panel, with a details pane and actions), **previews** (the quick view, `Ctrl+Q`, for chosen file types) and **palettes** (button bars docked below, beside or between the panels; the user picks the position in the settings). You can write one in **Python, Node.js, PowerShell 7, .NET or any executable**. Each plugin runs as a separate process and talks to Commander4K over a small JSON-RPC protocol.

The protocol is deliberately small and explicit so that both people and AI assistants can write plugins from this document alone. The quickest start is the plugin manager (*Wtyczki ▸ Menedżer wtyczek…*, `Ctrl+N`), which copies a working example in your language into the plugins folder.

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
- the AI installer refuses such code and offers to send the assistant a fix request;
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
    ],
    "palettes": [
      {
        "id": "projects", "title": "Projects", "dock": "left",
        "actions": [ { "id": "add", "title": "Add", "icon": "+" }, { "id": "remove", "title": "Remove", "icon": "✕", "forItem": true } ]
      }
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

Commands get the keymap id `plugin:<pluginId>:<commandId>`, and panels get `panel:<pluginId>/<panelId>`. Users can bind them in the settings (`Ctrl+,`).

### Permissions

| Permission | Allows these app API calls |
|---|---|
| `fs.read` | `fs.list`, `fs.stat`, `fs.readText`, `fs.readBytes`. They work on any location the app can open: disks, SMB shares, SFTP/FTP, archives, the registry. |
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
- The app starts the plugin process the first time one of its commands or panels is used. A plugin with a visible palette starts with the app, because the palette asks for its items.
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
| `preview.render` | `{ preview, entry: { name, path, kind, size, … }, context, options }` | `{ kind: "markdown"\|"text", content, editable? }` or `{ kind: "tiles", title, description?, tileWidth?, tiles: [{ title, subtitle?, icon?, image?, target? }] }`. A tile's `image` is a path the app reads with your `fs.read` permission; `target` is a path, an http(s) URL or another URI such as `steam://rungameid/570` (the user confirms the scheme once). Quick view (`Ctrl+Q`) of the entry under the cursor; read the file with `read_text`/`read_bytes`. Markdown is rendered with formatting (headings, tables, code). The highest `priority` wins among previews that accept the file. `extensions` items: `".csv"` (extension), `"package.json"` (file name), `"openapi*.json"` (pattern with `*`/`?`), `"/"` (folders). A preview may also return `{ kind: "chart", title, description?, chart }` (see `preview.card` for `chart`). |
| `preview.card` | `{ preview, entry: { … } \| null, context, options, previous }` | Only for previews with `"card": true` in `contributes.previews`. The small form shown on a `c4k.dir` dashboard: `{ title?, subtitle?, icon?, status?: "ok"\|"warning"\|"error", items: [...], actions: [{ label, target }] }`. Items: `{ kind: "value", label, value, status? }`, `{ kind: "progress", label, fraction (0–1), text? }`, `{ kind: "text", text }`, `{ kind: "thumbnails", images: [path] }` (at most 4, read with your `fs.read`), `{ kind: "chart", chart: { kind: "line"\|"bar", unit?, min?, max?, series: [{ name, points: [{ x, y, label? }] }] } }`. `previous` is the card you returned last time (thumbnails as a count), so a card refreshed on a timer can add a point to its chart; keep at most 120 points. An action `target` is a path, a URL, or `c4k:refresh` (read the card again). Previews without `"card": true` get a plain card with their title and an Open button. |
| `palette.items` | `{ palette, context }` | `{ items: [{ id, title, icon?, tooltip? }] }`. Buttons of a docked palette. Asked again after every navigation and after every `palette.action`, so items may depend on `context.activePath`. |
| `palette.invoke` | `{ palette, item, context }` | Same as `command.execute`. The user clicked an item. |
| `palette.action` | `{ palette, action, item, context }` | Same as `command.execute`. `item` is `null` for header buttons and the clicked item for `forItem` actions (context menu). |
| `shutdown` | — | `null`, then exit |

`options` is the `"options"` object of the folder's `c4k.dir` when the panel or preview was opened through it (see [Folder files](#folder-files-c4kdir)), otherwise `null`.

`context` = `{ activePath, otherPath, selection: [{ name, path, kind, size, modified, hidden }] }`:
- `activePath` is the location of the file list the user is looking at;
- `selection` holds the marked entries, or the entry under the cursor.

Rows form a tree through `parentId`. The app handles sorting (column header click), filtering (typing), expand and collapse, marking (`Insert`) and the details pane. A plugin only describes data.

Long loads are fine: the panel shows a spinner and stays responsive. Requests time out after 60 s.

### Requests from the plugin to the app

| Method | Params | Result | Permission |
|---|---|---|---|
| `context.get` | — | context, as above | — |
| `log.write` | `{ message }` | `null` | — |
| `ui.notify` | `{ message, error?, target? }` | `null` | — (`target`: a path or link opened when the toast is clicked) |
| `ui.showText` | `{ title, text }` | `null` | — |
| `ui.confirm` | `{ title, message }` | `true`/`false` | — |
| `ui.prompt` | `{ title, message, initial? }` | string or `null` | — |
| `ui.navigate` | `{ path, focus? }` | `null` | — |
| `ui.openUrl` | `{ url }` | `null` | — (http/https only; opens the app's built-in browser panel, audited) |
| `ui.search` | `{ query, engine? }` | `null` | — (`engine`: omitted = the user's search engine, `"assistant"` = the user's AI assistant, or `google`, `bing`, `duckduckgo`, `chatgpt`, `claude`, `perplexity`, `gemini`; use it e.g. to let the user look up an error) |
| `ui.copyText` | `{ text }` | `null` | — (clipboard, write only) |
| `storage.get` | `{ key }` | any JSON value or `null` | — (the plugin's own data) |
| `storage.set` | `{ key, value }` (`null` removes; max 1 MB per plugin) | `null` | — |
| `storage.keys` | — | `[string]` | — |
| `fs.list` | `{ path }` | `[entry]` | `fs.read` |
| `fs.stat` | `{ path }` | entry or `null` (missing) | `fs.read` |
| `fs.readText` | `{ path, maxBytes? }` (default 1 MB, max 16 MB) | string (UTF-8) | `fs.read` |
| `fs.readBytes` | `{ path, offset?, length? }` (default 64 KB, max 16 MB) | `{ data (base64), bytesRead, eof }` | `fs.read` |
| `fs.writeText` | `{ path, content }` | `null` | `fs.write` |
| `fs.writeBytes` | `{ path, data (base64) }` | `null` | `fs.write` |
| `fs.mkdir` | `{ path }` (creates parents) | `null` | `fs.write` |
| `fs.delete` | `{ path, recursive? }` | `null` | `fs.write` |
| `fs.move` | `{ from, to, overwrite? }` (also rename) | `null` | `fs.write` |
| `process.list` | — | `[{ pid, parentPid, name, path, memoryBytes, started }]` | `process.read` |
| `process.kill` | `{ pid, tree? }` | `null` | `process.kill` |
| `shell.run` | `{ file, args: [string], cwd?, elevated? }` | `{ exitCode, output, error }` | `shell.run` |
| `system.info` | — | `{ os, osVersion, architecture, machine, user, processors, memoryBytes, drives: [{ name, type, format, label, totalBytes, freeBytes }] }` | `system.info` |
| `net.get` | `{ url }` (http/https, max 5 MB) | string (error on non-2xx) | `net` |
| `net.request` | `{ url, method?, headers?, body?, contentType? }` (http/https, max 5 MB) | `{ status, headers, body }` (any status) | `net` |
| `mail.folders` | `{ account }` | `[{ serverName, localName, separator, special, uidValidity, count, unread }]` | `mail` |
| `mail.sync` | `{ account, folder, uidValidity, lastUid, modSeq, since?, knownUids? }` | `{ uidValidity, uidValidityChanged, modSeq, new: [{ uid, date, from, subject, flags, size, fileName }], flagChanges: [{ uid, flags }], removed: [uid] }` | `mail` |
| `mail.fetch` | `{ account, folder, uid, path }` (downloads the message to `path`) | `{ bytes }` | `mail` + `fs.write` |
| `mail.setFlags` | `{ account, folder, uids, add?, remove? }` | `null` | `mail` |
| `mail.move` | `{ account, folder, uids, target }` | `null` | `mail` |
| `mail.delete` | `{ account, folder, uids }` (to Trash; inside Trash the messages are expunged) | `null` | `mail` |
| `mail.append` | `{ account, folder, path, flags? }` (uploads a message file) | `{ uid }` (`null` when the server does not report it) | `mail` + `fs.read` |
| `mail.parse` | `{ path }` | `{ from, to, cc, subject, date, messageId, references, bodyText, attachments: [{ index, fileName, contentType, size }] }` | `mail` + `fs.read` |
| `mail.saveAttachment` | `{ path, index, target }` | `null` | `mail` + `fs.read` + `fs.write` |
| `mail.reply` | `{ path, mode: "reply"\|"replyAll"\|"forward", self }` | draft | `mail` + `fs.read` |
| `mail.compose` | `{ path, draft }` (writes the draft as a message file) | `null` | `mail` + `fs.write` (+ `fs.read` for attachments) |
| `mail.send` | `{ account, path }` (sends the draft file, then copies it to Sent) | `{ messageId }` | `mail` + `fs.read` |
| `background.wake` | `{}` (asks for a `background.tick` now) | — | `background` |

`entry` = `{ name, path, kind, size, modified, hidden }`:
- `kind`: `file`, `directory`, `drive`, `archive`, `share`, `host`, `registrykey`…;
- `size`: `-1` when unknown;
- `modified`: ISO 8601 or `null`.

**Errors** use standard JSON-RPC codes:
- `-32001`: permission denied. The message names the missing permission.
- `-32010`: mail login needed, with `data: { authRequired: true, realm }`.
- `-32011`: the mail server's certificate is not trusted yet, with `data: { host, fingerprint }`.
- `-32601`: unknown method.
- `-32602`: invalid parameters.

The SDK clients turn these into exceptions.

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
| `ui.navigate` | `navigate(path, focus=None)` | `navigate(path, focus)` | — (`Invoke-C4K 'ui.navigate' @{ path = … }`) |
| `ui.openUrl` | `open_url(url)` | `openUrl(url)` | `Open-C4KUrl $Url` |
| `ui.search` | `search(query, engine=None)` | `search(query, engine)` | `Search-C4KWeb $Query $Engine` |
| `ui.copyText` | `copy_text(text)` | `copyText(text)` | `Copy-C4KText $Text` |
| `storage.get` / `.set` | `storage_get(key, default)` / `storage_set(key, value)` | `storageGet(key, fallback)` / `storageSet(key, value)` | `Get-C4KStorage $Key $Default` / `Set-C4KStorage $Key $Value` |
| `fs.list` | `list_dir(path)` | `listDir(path)` | `Get-C4KChildItem $Path` |
| `fs.stat` | `stat(path)` | `stat(path)` | `Get-C4KItem $Path` |
| `fs.readText` | `read_text(path, max_bytes=1048576)` | `readText(path, maxBytes)` | `Get-C4KText $Path [$MaxBytes]` |
| `fs.readBytes` | `read_bytes(path, offset=0, length=65536)` → `bytes` | `readBytes(path, offset, length)` → `Buffer` | `Read-C4KBytes $Path [$Offset] [$Length]` → `byte[]` |
| `fs.writeText` | `write_text(path, content)` | `writeText(path, content)` | `Set-C4KText $Path $Content` |
| `fs.writeBytes` | `write_bytes(path, data)` | `writeBytes(path, data)` | `Write-C4KBytes $Path $Data` |
| `fs.mkdir` | `make_dir(path)` | `makeDir(path)` | `New-C4KDirectory $Path` |
| `fs.delete` | `remove(path, recursive=False)` | `remove(path, recursive)` | `Remove-C4KItem $Path [-Recurse]` |
| `fs.move` | `move(source, target, overwrite=False)` | `move(from, to, overwrite)` | `Move-C4KItem $Path $Destination [-Force]` |
| `process.list` | `list_processes()` | `listProcesses()` | `Get-C4KProcess` |
| `process.kill` | `kill_process(pid, tree=False)` | `killProcess(pid, tree)` | `Stop-C4KProcess $Id [-Tree]` |
| `shell.run` | `run(file, args=(), cwd=None, elevated=False)` | `run(file, args, cwd, elevated)` | `Invoke-C4KProcess $File $Arguments [$WorkingDirectory] [-Elevated]` |
| `system.info` | `system_info()` | `systemInfo()` | `Get-C4KSystemInfo` |
| `net.get` | `http_get(url)` | `httpGet(url)` | `Invoke-C4KHttpGet $Url` |
| `net.request` | `http_request(url, method="GET", headers=None, body=None, content_type=None)` | `httpRequest(url, { method, headers, body, contentType })` | `Invoke-C4KHttpRequest $Url [$Method] [$Headers] [$Body] [$ContentType]` |
| `mail.folders` | `mail_folders(account)` | `mailFolders(account)` | `Get-C4KMailFolders $Account` |
| `mail.sync` | `mail_sync(account, folder, uid_validity, last_uid, mod_seq, since, known_uids)` | `mailSync(account, folder, { uidValidity, lastUid, modSeq, since, knownUids })` | `Sync-C4KMail $Account $Folder [$UidValidity] [$LastUid] [$ModSeq] [$Since] [$KnownUids]` |
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

## Examples

These are in [`examples/plugins`](../examples/plugins), and the app embeds them as templates:

- **`folder-stats-python`** (`fs.read`): a command, a panel of file extensions with details and an action, and a quick view rendering `.csv` files as a table. It also has a command that tries to write a file, to show how the app refuses.
- **`site-check-node`** (`net`): a command, and a panel that refreshes itself every 60 s.
- **`git-powershell`** (`shell.run`): `git status`, and a commit history panel. `git` runs through the app, so each command line is audited.
- **`project-modules-python`** (`fs.read`): a dedicated quick view for chosen files. It shows the dependencies of `package.json`, `*.csproj`, `pyproject.toml`, `requirements*.txt`, `go.mod`, `Cargo.toml` and `composer.json`, and the modules of an HTTP API from OpenAPI/Swagger files (endpoints grouped by tag, plus data models). It shows file names and patterns in `extensions`.
- `site-check-node` keeps its site list in the plugin's storage (`storageGet`/`storageSet`), not in a file.

When the quick view cannot recognise a file (only hex is left), it offers to ask an AI what the format is. It can then have an AI write a preview plugin for that type, from a sample of the file.

## Folder files (`c4k.dir`)

A folder can say how it wants to be shown. A `c4k.dir` file in it (JSON with comments, schema [`c4k.dir.schema.json`](c4k.dir.schema.json)) has two optional actions: `preview` (the quick view of the folder) and `enter` (what Enter on it does). Shift+Enter always opens the plain file list, and the setting "Pliki c4k.dir w folderach" turns the feature off. `cm_FolderView` writes the file from a short choice.

Each action is a built-in view name, a plugin id, or an object with one target:

| Target | Example | What happens |
|---|---|---|
| `view` | `"gallery"`, `"dashboard"`, `"notes"`, `"markdown"`, `"files"` | Built-in views: image thumbnails, tiles from `tiles`, Markdown notes with their links, a Markdown file (`file`, README.md by default), the plain list. |
| `plugin` (+ `preview`) | `{ "plugin": "official.steam-library" }` | The plugin's preview (`contributes.previews`, `extensions: ["/"]`) renders the folder, with `options`. On Enter it fills the panel; Esc returns to the files. |
| `panel` (+ `plugin`) | `{ "plugin": "examples.git", "panel": "log" }` | Opens a panel plugin with the folder as `argument` and `options`. |
| `open` (+ `user`, `keyFile`) | `{ "open": "sftp://me@nas.lan/srv", "keyFile": "~/.ssh/id_ed25519" }` | Goes there instead; relative paths are resolved against the folder. |
| `url` | `{ "url": "https://grafana.lan" }` | The page in the panel browser. |
| `run` | `{ "run": { "command": "build.ps1", "args": ["-Release"], "cwd": "src" } }` | Runs a command. The user sees it first and trusts this exact file content; a changed or copied `c4k.dir` asks again. |

Dashboard tiles: `{ "title", "subtitle"?, "icon"?, "image"?, "open" | "url" | "run" }`.

```json
{
  "$schema": "https://raw.githubusercontent.com/4kprojekt/Commander4K/main/docs/c4k.dir.schema.json",
  "title": "Gry",
  "preview": { "plugin": "official.steam-library", "options": { "sort": "size" } },
  "enter": { "plugin": "official.steam-library" }
}
```

A plugin that is not installed shows a tile that downloads it from the official catalog.

The official `official.git-repo` plugin works this way: `{ "preview": { "plugin": "official.git-repo" } }` in a repository shows its branch, changes, recent commits, branches, tags and remotes, read with `git` through `shell.run` (options `commits`, `files`, `branches`).

To make a plugin for folders, contribute a preview with `"extensions": ["/"]` and a negative `priority`, so it only shows folders whose `c4k.dir` names it, and return `kind: "tiles"` or `markdown` from `preview.render`. Read `options` for your settings.

### Dashboard cards

A dashboard tile with `"card"` is a live card drawn by a preview instead of a plain shortcut. Plain tiles keep working as before, and both can sit in one dashboard.

```jsonc
{
  "enter": {
    "view": "dashboard",
    "tiles": [
      { "title": "Zdjęcia", "open": "photos", "card": "gallery" },        // four thumbnails, count, size, Open
      { "title": "Dokumenty", "open": "~/Documents", "card": true },      // whatever Ctrl+Q would show for it
      { "title": "Serwery", "open": "servers", "card": true },            // a folder with its own dashboard: a dashboard card
      { "title": "VPS", "open": "sftp://kamil@vps.example.com/", "card": "ssh", "refresh": "10s", "options": { "keyFile": "~/.ssh/id_ed25519" } },
      { "title": "Grafana", "card": "api", "refresh": "1m", "size": "wide",
        "api": { "url": "https://grafana.example.com/api/health", "headers": { "Authorization": "{secret:grafana}" },
                 "fields": [ { "path": "database", "label": "Baza", "as": "status", "ok": ["ok"] }, { "path": "version" } ] } },
      { "title": "Konfiguracja", "open": "config.json", "card": "official.json-preview" }
    ]
  },
  "preview": "dashboard"
}
```

- `card`: `true` = the preview Ctrl+Q would pick for `open`, including that folder's own `c4k.dir`; or a name: `files`, `gallery`, `notes`, `markdown`, `dashboard`, `api`, `ssh`, or a plugin id (`plugin` or `plugin/preview`). A named card ignores the target's `c4k.dir`. A card that points at a folder with a dashboard shows that dashboard's title and tiles, and a click opens it in place: ← (Alt+←, Backspace) goes back, the breadcrumbs jump, and Esc shows the files of the folder on screen.
- `refresh`: `10s`, `5m`, `1h` (at least 2 s), only while the dashboard is visible. Without it the card is read once and has "Odśwież".
- `size`: `small` (default) or `wide`. `options`: passed to the card's preview.
- `api`: `url`, `method` (GET by default), `headers`, `body`, `contentType`, `fields` (`path` such as `a.b` or `items[0].name`, `label`, `as`: `value`, `chart` or `status`, `unit`, `ok`). `cm_AddApiCard` asks the API once, lists the fields of its answer and writes the tile. `"preview": { "view": "api", "api": { … } }` shows the same fields as the folder's preview (GET and HEAD only).
- Secrets: write `{secret:name}` in a header or the body (never in the url, which is logged); the value lives encrypted in `config/secrets.json`, bound to the origin (scheme, host, port) it was first saved for, and a card that misses it offers to enter it. Never put tokens in `c4k.dir`.
- Consent: `api` cards with methods other than GET/HEAD and every `ssh` card wait until the user trusts this `c4k.dir` (the same trust as `run`; a changed file asks again), and only on disks and shares.
- `ssh` runs one fixed, read-only command (uname, `/proc`, `df`) with the saved login or keys; non-Linux servers show their status only.

The official catalog has preview plugins with cards: `official.json-preview`, `official.openapi-preview` and `official.html-preview`. For your own, see `preview.card` in the protocol table.

## Official plugin catalog

[`plugins-catalog/catalog.json`](../plugins-catalog/catalog.json) lists the plugins that can be installed from the plugin manager (group "Katalog oficjalnych wtyczek", Ctrl+I) and from `c4k.dir`. The app downloads it at start-up from the repository, so new plugins appear without an update. Each entry gives the plugin folder (relative to `baseUrl`) and the SHA-256 of every file. The hash is computed over the UTF-8 text with CR removed. A downloaded file that does not match is refused. An installed plugin starts disabled until the user approves its permissions, like any other.

To add a plugin, put its folder in `plugins-catalog/` (official) or `examples/plugins/`, add an entry (`id`, `name`, `runtime`, `description` per language, `tags`, an `example` c4k.dir, `path`), then run `pwsh ./plugins-catalog/Update-Catalog.ps1` to fill in the files and hashes. `PluginCatalogTests` fails if the catalog is out of date.

## Writing a plugin with an AI assistant

Commander4K helps with this (*Konfiguracja ▸ Napisz wtyczkę z AI*):

- **In-app AI chat** ("🧩 Napisz wtyczkę"): the model gets this guide, an example in a runtime you have installed and the app's current state. It can also call `get_context`, `list_directory`, `read_text_file` (only with your consent) and `get_plugin_guide`. An answer containing a plugin gets a "🧩 Zainstaluj wtyczkę" button.
- **Claude Code over MCP:** Claude Code connected to Commander4K can write the plugin itself with the MCP tools `get_plugin_guide`, `check_plugin` and `install_plugin`. Without a signed-in provider, "Napisz wtyczkę z AI" copies the request for Claude Code.

Either way the installer checks the paths and the manifest, shows the permissions before writing anything, and puts the plugin into `plugins/<id>` **disabled**. You enable it in the plugin manager after reviewing it. It also adds the SDK client when the answer leaves it out.

The answer must give every file as a line `File: <relative path>` followed by one fenced code block with the complete file.

To do it by hand, give the assistant this file, one example plugin and a description of what you want. Ask it to:

1. declare the smallest possible `permissions`;
2. send all file, process, network and program work through the app API listed above, never through the language's own file or process functions;
3. keep the plugin in one folder: `plugin.json`, the entry file and the copied SDK client.

Then copy the folder into `plugins/`. Open the plugin manager and check the permissions it asks for before you enable it.
