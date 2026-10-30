# Commander4K plugins

A plugin adds **commands** (menu bar, command palette, key bindings), **panels** (a tree/table shown inside a file panel, with a details pane and actions), **previews** (the quick view, `Ctrl+Q`, for chosen file types) and **palettes** (button bars docked below, beside or between the panels; the user picks the position in the settings). You can write one in **Python, Node.js, PowerShell 7, .NET or any executable**. Each plugin runs as a separate process and talks to Commander4K over a small JSON-RPC protocol.

The protocol is deliberately small and explicit so that both people and AI assistants can write plugins from this document alone. The quickest start is the plugin manager (*Wtyczki ▸ Menedżer wtyczek…*, `Ctrl+N`), which copies a working example in your language into the plugins folder.

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
| `fs.read` | `fs.list`, `fs.readText`. They work on any location the app can open: disks, SMB shares, SFTP/FTP, the registry. |
| `fs.write` | `fs.writeText` |
| `process.kill` | `process.kill` |
| `shell.run` | `shell.run` |
| `net` | `net.get` |
| `process.read`, `system.info` | Declares that the plugin reads processes or system configuration itself. Shown to the user, not enforced. |

The UI and context calls (`ui.*`, `context.get`, `log.write`) need no permission.

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
| `panel.load` | `{ panel, argument, filter, context }` | `{ columns: [{ id, header, align?: "left"\|"right"\|"center", width? }], rows: [{ id, parentId?, cells: [string], severity?: "normal"\|"muted"\|"highlight"\|"warning"\|"error", sortKeys?: [number\|null], expanded? }], status? }` |
| `panel.details` | `{ panel, rowId }` | `{ sections: [{ title, items: [[key, value]] }] }` or `null` |
| `panel.action` | `{ panel, action, rowIds, context }` | `{ text?: string }`. Text is shown in a viewer, and the panel reloads afterwards. |
| `preview.render` | `{ preview, entry: { name, path, kind, size, … }, context }` | `{ kind: "markdown"\|"text", content, editable? }`. Quick view (`Ctrl+Q`) of the entry under the cursor. Markdown is rendered with formatting. The highest `priority` wins among previews that accept the extension (`"/"` in `extensions` means folders). |
| `palette.items` | `{ palette, context }` | `{ items: [{ id, title, icon?, tooltip? }] }`. Buttons of a docked palette. Asked again after every navigation and after every `palette.action`, so items may depend on `context.activePath`. |
| `palette.invoke` | `{ palette, item, context }` | Same as `command.execute`. The user clicked an item. |
| `palette.action` | `{ palette, action, item, context }` | Same as `command.execute`. `item` is `null` for header buttons and the clicked item for `forItem` actions (context menu). |
| `shutdown` | — | `null`, then exit |

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
| `fs.list` | `{ path }` | `[entry]` | `fs.read` |
| `fs.readText` | `{ path, maxBytes? }` (default 1 MB, max 16 MB) | string | `fs.read` |
| `fs.writeText` | `{ path, content }` | `null` | `fs.write` |
| `process.kill` | `{ pid, tree? }` | `null` | `process.kill` |
| `shell.run` | `{ file, args: [string], cwd?, elevated? }` | `{ exitCode, output, error }` | `shell.run` |
| `net.get` | `{ url }` (http/https, max 5 MB) | string | `net` |

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

## Examples

These are in [`examples/plugins`](../examples/plugins), and the app embeds them as templates:

- **`folder-stats-python`** (`fs.read`): a command, a panel of file extensions with details and an action, and a quick view rendering `.csv` files as a table. It also has a command that tries to write a file, to show how the app refuses.
- **`site-check-node`** (`net`): a command, and a panel that refreshes itself every 60 s.
- **`git-powershell`** (`shell.run`): `git status`, and a commit history panel. `git` runs through the app, so each command line is audited.

## Writing a plugin with an AI assistant

Give the assistant this file, one example plugin and a description of what you want. Ask it to:

1. declare the smallest possible `permissions`;
2. send all file, process, network and program work through the app API listed above, never through the language's own file or process functions;
3. keep the plugin in one folder: `plugin.json`, the entry file and the copied SDK client.

Then copy the folder into `plugins/`. Open the plugin manager and check the permissions it asks for before you enable it.
