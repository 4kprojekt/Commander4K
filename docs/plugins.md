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
| `panel.receivers` | `{ panel, files: [{ path, name, kind: "file"\|"folder", size }], context }` | `[{ id, title, description?, extensions?, multiple?, folders?, returnsMessage? }]`. Panels with `accepts` only; answer within 5 s. See [Receiving and providing files](#receiving-and-providing-files). |
| `panel.receive` | `{ panel, receiver, jobId, files, context }` | `{ text?, results?: [{ path, ok, error? }] }`. One call for the whole selection; no time limit. |
| `panel.providers` | `{ panel, rowIds, context }` | `[{ id, title, description? }]`. Panels with `provides` only; answer within 5 s. |
| `panel.provide` | `{ panel, provider, jobId, rowIds, context }` | `{ files: [{ name, text? \| base64? \| url? \| error? }], text? }`. No time limit; the whole reply must stay under 16 MB. |
| `preview.render` | `{ preview, entry: { name, path, kind, size, … }, context, options }` | `{ kind: "markdown"\|"text", content, editable? }` or `{ kind: "tiles", title, description?, tileWidth?, tiles: [{ title, subtitle?, icon?, image?, target? }] }` or `{ kind: "web", page, state? }` (an HTML page from the preview's `webRoot`, see "Web previews"). A tile's `image` is a path the app reads with your `fs.read` permission; `target` is a path, an http(s) URL or another URI such as `steam://rungameid/570` (the user confirms the scheme once). Quick view (`Ctrl+Q`) of the entry under the cursor; read the file with `read_text`/`read_bytes`. Markdown is rendered with formatting (headings, tables, code). The highest `priority` wins among previews that accept the file. `extensions` items: `".csv"` (extension), `"package.json"` (file name), `"openapi*.json"` (pattern with `*`/`?`), `"/"` (folders). |
| `preview.message` | `{ preview, entry, method, params, context }` | Any JSON value. A web preview page called `c4k.request(method, params)`; answer it here (30 s limit). See "Web previews". |
| `palette.items` | `{ palette, context }` | `{ items: [{ id, title, icon?, tooltip? }] }`. Buttons of a docked palette. Asked again after every navigation and after every `palette.action`, so items may depend on `context.activePath`. |
| `palette.invoke` | `{ palette, item, context }` | Same as `command.execute`. The user clicked an item. |
| `palette.action` | `{ palette, action, item, context }` | Same as `command.execute`. `item` is `null` for header buttons and the clicked item for `forItem` actions (context menu). |
| `shutdown` | — | `null`, then exit |

`options` is the `"options"` object of the folder's `c4k.dir` when the panel or preview was opened through it (see [Folder files](#folder-files-c4kdir)), otherwise `null`.

`context` = `{ activePath, otherPath, selection: [{ name, path, kind, size, modified, hidden }] }`:
- `activePath` is the location of the file list the user is looking at;
- `selection` holds the marked entries, or the entry under the cursor.

Rows form a tree through `parentId`. The app handles sorting (column header click), filtering (typing), expand and collapse, marking (`Insert`) and the details pane. A plugin only describes data.

Long loads are fine: the panel shows a spinner and stays responsive. Requests time out after 60 s, except `panel.receivers`/`panel.providers` (5 s, a dialog is waiting) and `panel.receive`/`panel.provide` (no limit; the user cancels them).

### Requests from the plugin to the app

| Method | Params | Result | Permission |
|---|---|---|---|
| `context.get` | — | context, as above | — |
| `log.write` | `{ message }` | `null` | — |
| `ui.notify` | `{ message, error? }` | `null` | — |
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
| `net.request` | `{ url, method?, headers?, body?, contentType?, bodyFile?, multipart?: { field, path, fileName? } }` (http/https, response max 5 MB) | `{ status, headers, body }` (any status) | `net`; `bodyFile`/`multipart` also need `fs.read` or a [read grant](#receiving-and-providing-files) for that path |
| `job.progress` | `{ jobId, done, total, bytes?, text? }` — a **notification** (no `id`, no answer) | — | — |

`entry` = `{ name, path, kind, size, modified, hidden }`:
- `kind`: `file`, `directory`, `drive`, `archive`, `share`, `host`, `registrykey`…;
- `size`: `-1` when unknown;
- `modified`: ISO 8601 or `null`.

**Errors** use standard JSON-RPC codes:
- `-32001`: permission denied. The message names the missing permission.
- `-32601`: unknown method.
- `-32602`: invalid parameters.

The SDK clients turn these into exceptions.

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
- A refused call raises (`HostError` in Python and Node, a terminating error in PowerShell). Code `-32001` means a permission is missing from `plugin.json`.

| Protocol method | Python (`import c4k`) | Node (`const c4k = require('./c4k')`) | PowerShell (`. "$PSScriptRoot/c4k.ps1"`) |
|---|---|---|---|
| handlers | `@c4k.handler("command.execute")` + `c4k.serve()` | `c4k.on('command.execute', async p => …)` | `Register-C4KHandler 'command.execute' { param($p) … }` + `Start-C4KPlugin` |
| `context.get` | `context()` | `context()` | `Get-C4KContext` |
| `log.write` | `log(message)` | `log(message)` | `Write-C4KLog $Message` |
| `ui.notify` | `notify(message, error=False)` | `notify(message, error)` | `Show-C4KNotification $Message [-IsError]` |
| `ui.showText` | `show_text(title, text)` | `showText(title, text)` | `Show-C4KText $Title $Text` |
| `ui.confirm` | `confirm(title, message)` | `confirm(title, message)` | `Confirm-C4K $Title $Message` |
| `ui.prompt` | `prompt(title, message, initial="")` | `prompt(title, message, initial)` | `Read-C4KInput $Title $Message $Initial` |
| `ui.navigate` | `navigate(path, focus=None)` | `navigate(path, focus)` | — (`Invoke-C4K 'ui.navigate' @{ path = … }`) |
| `ui.openUrl` | `open_url(url)` | `openUrl(url)` | `Open-C4KUrl $Url` |
| `ui.search` | `search(query, engine=None)` | `search(query, engine)` | `Search-C4KWeb $Query $Engine` |
| `ui.copyText` | `copy_text(text)` | `copyText(text)` | `Copy-C4KText $Text` |
| — (result of `preview.render`) | `web_preview(page, state=None)` | `webPreview(page, state)` | `New-C4KWebPreview $Page [$State]` |
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
| `net.request` | `http_request(url, method="GET", headers=None, body=None, content_type=None, body_file=None, multipart=None)` | `httpRequest(url, { method, headers, body, contentType, bodyFile, multipart })` | `Invoke-C4KHttpRequest $Url [$Method] [$Headers] [$Body] [$ContentType] [-BodyFile] [-MultipartField -MultipartPath [-MultipartFileName]]` |
| `job.progress` | `report_progress(job_id, done, total, bytes=0, text=None)` | `reportProgress(jobId, done, total, { bytes, text })` | `Send-C4KProgress -JobId -Done -Total [-Bytes] [-Text]` |
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

To make a plugin for folders, contribute a preview with `"extensions": ["/"]` and a negative `priority`, so it only shows folders whose `c4k.dir` names it, and return `kind: "tiles"` or `markdown` from `preview.render`. Read `options` for your settings.

## Official plugin catalog

[`plugins-catalog/catalog.json`](../plugins-catalog/catalog.json) lists the plugins that can be installed from the plugin manager (group "Katalog oficjalnych wtyczek", Ctrl+I) and from `c4k.dir`. The app downloads it at start-up from the repository, so new plugins appear without an update. Each entry gives the plugin folder (relative to `baseUrl`) and the SHA-256 of every file. The hash is computed over the UTF-8 text with CR removed. A downloaded file that does not match is refused. An installed plugin starts disabled until the user approves its permissions, like any other.

To add a plugin, put its folder in `plugins-catalog/` (official) or `examples/plugins/`, add an entry (`id`, `name`, `runtime`, `description` per language, `tags`, an `example` c4k.dir, `path`), then run `pwsh ./plugins-catalog/Update-Catalog.ps1` to fill in the files and hashes. `PluginCatalogTests` fails if the catalog is out of date.

## Writing a plugin with an AI assistant

Commander4K helps with this (*Konfiguracja ▸ Napisz wtyczkę z AI*):

- **In-app AI chat** ("🧩 Napisz wtyczkę"): the model gets this guide, an example in a runtime you have installed and the app's current state. It can also call `get_context`, `list_directory`, `read_text_file` (only with your consent) and `get_plugin_guide`. An answer containing a plugin gets a "🧩 Zainstaluj wtyczkę" button.
- **Claude Code over MCP:** Claude Code connected to Commander4K can write the plugin itself with the MCP tools `get_plugin_guide`, `check_plugin` and `install_plugin`. Without a signed-in provider, "Napisz wtyczkę z AI" copies the request for Claude Code. See [Writing plugins with Claude Code over MCP](#writing-plugins-with-claude-code-over-mcp).

Either way the installer checks the paths and the manifest, shows the permissions before writing anything, and puts the plugin into `plugins/<id>` **disabled**. You enable it in the plugin manager after reviewing it. It also adds the SDK client when the answer leaves it out.

The answer must give every file as a line `File: <relative path>` followed by one fenced code block with the complete file.

To do it by hand, give the assistant this file, one example plugin and a description of what you want. Ask it to:

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
| `install_plugin`, `enable_plugin` | always ask | Show the plugin's permissions in their own dialog. |
| `list_settings`, `list_key_bindings` | allow | The settings (as in `Ctrl+,`: key, value, allowed values) and the keyboard shortcuts. |
| `set_setting`, `set_key_binding` | ask | Change a setting (applied at once and saved) or bind keys to a command (saved to `keymap.json`). |
| `get_translation_kit`, `check_translation` | allow | The translation rules and the texts to translate, page by page (`onlyMissing` to complete a language); check translated entries without saving. |
| `save_translation` | ask | Merge translated entries into `config/i18n/<code>.json`. Refused for wrong placeholders or plural forms and unknown keys; `pl` is the source language. |
| `get_skin_guide`, `list_skins`, `get_skin`, `check_skin` | allow | The skin brief, schema and current skin; the skins; one skin's JSON; schema and contrast check. |
| `save_skin` | always ask | Shows the skin live in the window while you decide; "Zapisz skórkę" saves it to `config/skins/<id>.json` and switches to it, anything else puts the old skin back. |

A blocked tool is still listed, with "(Disabled by the user in Commander4K.)" at the end of its description, and calling it returns an error. Prompts: `write_plugin`, `preview_plugin_for_file`, `explain_file_format`, `translate_ui`, `design_skin`. The two prompts about a file include its first bytes only when the file is allowed, not protected, and `read_file` is not blocked; each of them is audited as `prompt:<name>`.

**Allowed folders.** File tools only work inside the folders listed in the connect window (or the setting "Foldery dostępne dla MCP"). Any VFS path can be one: a disk folder, `\\server\share`, `sftp://…`. With an empty list no file can be touched. A path outside them is refused with a message that tells the model to ask you. The same applies to folders the app tools take from the panels: `get_context` shows a panel's contents only when its folder is allowed (and not protected), `select` needs the chosen panel's folder, and `run_command`, `run_plugin_command` and `load_plugin_panel` need both panels' folders and the marked entries, inside the allowed folders and outside the protected ones. `load_plugin_panel` passes `argument` only to panels that take a file, after the same check; other panels never see it. "Dodaj folder z aktywnego panelu" accepts real folders only: disk paths, `\\server\share`, `sftp://` and `ftp://` (not This PC, `net://`, the registry or archives; type those into the setting if you really mean it).

**Protected folders.** No tool can read or change the `plugins/` folders (next to the exe and in the data folder), `config/` (approvals, encrypted credentials and the MCP token, known hosts, trusted folders), `data/` (audit logs, plugin storage, the browser profile with its sign-ins) or the folder of `Commander4K.exe` itself (a DLL placed there would load on the next start), even when they sit inside an allowed folder. Only `list_directory` and `stat` work there, because they return names and sizes, not contents. `navigate`, the panel checks of the app tools and the `explain_file_format` prompt refuse them too. Writing tools (`write_file`, `make_directory`, `move`, `delete`), `render_preview` (a folder preview reads its children) and the recursive `folder` argument of `check_plugin` and `install_plugin` are also refused on a folder that contains a protected one. Paths with NTFS stream syntax (a `:` after the drive, as in `file.txt:stream` or `folder::$INDEX_ALLOCATION`) are refused by every tool, because they reach real files under a different name. The refusal reads "Commander4K's own plugin, config and data folders cannot be accessed through MCP." Plugins get into `plugins/` only through `install_plugin`.

**Confirmations.** A tool in "ask" mode opens a dialog in Commander4K: "Tak", "Nie" or "Tak do końca sesji" (yes for this tool and client until the server stops or you press "Cofnij zgody z tej sesji"). Esc, Enter and closing the dialog mean "Nie"; in the install and enable dialogs of `install_plugin` and `enable_plugin`, Enter and Esc mean "Anuluj". The `write_file` dialog shows the start of the text to be written (up to 2000 characters), or "binary, N bytes" for base64; that preview is not written to the audit log. Only one dialog is shown at a time, and parallel calls wait their turn. A call that the client cancels, or that is still waiting when the server stops, closes its dialog as "Nie". A declined call returns "The user declined this action in Commander4K." The client name in the dialog and the log is what the client reported about itself; do not rely on it.

**Configuration, translations and skins.** The client is the model, so these tools call no AI API: they hand out what the clipboard prompts ("Przetłumacz interfejs z AI", "Generuj skórkę z AI") contain, check the answer and save it. They take no paths: files are named only from a checked language code (`de`, `pt-BR`) or skin id (`solarized-dark`), and `config/` stays closed to the file tools. Some settings are read-only over MCP, whatever the mode and even after "Tak do końca sesji": everything under MCP (server, port, allowed folders, tool modes) and the plugin catalog, AI catalog and update addresses. `list_settings` marks them, and `set_setting` answers that they can only be changed in the settings window. External tools (`tools.json`), saved connections and AI keys are not reachable either.

**Audit.** Every call is written to `data/logs/plugins-YYYYMMDD.log` as `mcp:<client>` with the tool, a summary and the outcome. The connect window lists the last 50 calls.

**The plugin flow.**

1. `get_plugin_guide` (optionally with `runtime` or `example`) returns this guide, the manifest schema and an example plugin.
2. Write the files into a folder inside the allowed folders with `write_file`.
3. `check_plugin` with `folder` (or `files`) reports manifest errors and SDK bypasses. Fix them until it is clean.
4. `install_plugin` shows its permissions to the user and installs it into `plugins/<id>`, disabled. Code that bypasses the SDK is not installed.
5. `enable_plugin` shows the permissions again; on "yes" the plugin starts.
6. Try it with `run_plugin_command`, `load_plugin_panel` or `render_preview`. Read problems with `get_plugin_log`. After editing the code, call `install_plugin` again (it replaces the installed code), then `reload_plugin`; `reload_plugin` also restarts a plugin that hangs. Grown permissions need `enable_plugin` again.
