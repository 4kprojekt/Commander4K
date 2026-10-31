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
| `preview.render` | `{ preview, entry: { name, path, kind, size, … }, context, options }` | `{ kind: "markdown"\|"text", content, editable? }` or `{ kind: "tiles", title, description?, tileWidth?, tiles: [{ title, subtitle?, icon?, image?, target? }] }`. A tile's `image` is a path the app reads with your `fs.read` permission; `target` is a path, an http(s) URL or another URI such as `steam://rungameid/570` (the user confirms the scheme once). Quick view (`Ctrl+Q`) of the entry under the cursor; read the file with `read_text`/`read_bytes`. Markdown is rendered with formatting (headings, tables, code). The highest `priority` wins among previews that accept the file. `extensions` items: `".csv"` (extension), `"package.json"` (file name), `"openapi*.json"` (pattern with `*`/`?`), `"/"` (folders). |
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
| `net.request` | `{ url, method?, headers?, body?, contentType? }` (http/https, max 5 MB) | `{ status, headers, body }` (any status) | `net` |

`entry` = `{ name, path, kind, size, modified, hidden }`:
- `kind`: `file`, `directory`, `drive`, `archive`, `share`, `host`, `registrykey`…;
- `size`: `-1` when unknown;
- `modified`: ISO 8601 or `null`.

**Errors** use standard JSON-RPC codes:
- `-32001`: permission denied. The message names the missing permission.
- `-32601`: unknown method.
- `-32602`: invalid parameters.

The SDK clients turn these into exceptions.

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
| `check_plugin`, `get_plugin_log` | allow | Validate a plugin without installing it (manifest, limits, SDK bypasses); read a plugin's recent log lines. |
| `read_file`, `write_file`, `make_directory` | ask | Read and write files inside the allowed folders. |
| `move`, `delete` | always ask | No "yes for this session" for these. |
| `run_command`, `run_plugin_command`, `load_plugin_panel`, `render_preview`, `reload_plugin` | ask | Run app or plugin commands, open plugin panels, render a preview, restart a plugin's process. |
| `install_plugin`, `enable_plugin` | always ask | Show the plugin's permissions in their own dialog. |

A blocked tool is still listed, with "(Disabled by the user in Commander4K.)" at the end of its description, and calling it returns an error. Prompts: `write_plugin`, `preview_plugin_for_file`, `explain_file_format`.

**Allowed folders.** File tools only work inside the folders listed in the connect window (or the setting "Foldery dostępne dla MCP"). Any VFS path can be one: a disk folder, `\server\share`, `sftp://…`. With an empty list no file can be touched. A path outside them is refused with a message that tells the model to ask you. The same applies to folders the app tools take from the panels: `get_context` shows a panel's contents only when its folder is allowed, and `run_command`, `run_plugin_command` and `load_plugin_panel` need both panels' folders and the marked entries inside the allowed folders.

**Protected folders.** Writing tools (`write_file`, `make_directory`, `move`, `delete`) never change the `plugins/` folders (next to the exe and in the data folder), `config/` (approvals, credentials, keymap) or `data/` (audit logs, plugin storage, the browser profile), even when they sit inside an allowed folder. Plugins get into `plugins/` only through `install_plugin`.

**Confirmations.** A tool in "ask" mode opens a dialog in Commander4K: "Tak", "Nie" or "Tak do końca sesji" (yes for this tool and client until the server stops or you press "Cofnij zgody z tej sesji"). Esc, Enter and closing the dialog mean "Nie". Only one dialog is shown at a time, and parallel calls wait their turn. A call that the client cancels, or that is still waiting when the server stops, closes its dialog as "Nie". A declined call returns "The user declined this action in Commander4K." The client name in the dialog and the log is what the client reported about itself; do not rely on it.

**Audit.** Every call is written to `data/logs/plugins-YYYYMMDD.log` as `mcp:<client>` with the tool, a summary and the outcome. The connect window lists the last 50 calls.

**The plugin flow.**

1. `get_plugin_guide` (optionally with `runtime` or `example`) returns this guide, the manifest schema and an example plugin.
2. Write the files into a folder inside the allowed folders with `write_file`.
3. `check_plugin` with `folder` (or `files`) reports manifest errors and SDK bypasses. Fix them until it is clean.
4. `install_plugin` shows its permissions to the user and installs it into `plugins/<id>`, disabled. Code that bypasses the SDK is not installed.
5. `enable_plugin` shows the permissions again; on "yes" the plugin starts.
6. Try it with `run_plugin_command`, `load_plugin_panel` or `render_preview`. Read problems with `get_plugin_log`. After editing the code, install again and call `reload_plugin`. Grown permissions need `enable_plugin` again.
