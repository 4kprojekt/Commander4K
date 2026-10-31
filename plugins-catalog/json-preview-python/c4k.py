"""Commander4K plugin protocol v1 — Python client (copy this file next to your plugin's main.py).

Open source (MIT): https://github.com/4kprojekt/Commander4K/tree/main/plugins-sdk — reference: docs/plugins.md.

The app starts the plugin with stdin/stdout connected; messages are JSON-RPC 2.0, one per line.
Register handlers with @handler("command.execute") etc., call the app with the helpers below
(every call is permission-checked against plugin.json and written to the app's audit log), then serve().

RULE: do ALL file, folder, process, network and system work through these helpers — never with Python's
own open(), os, shutil, pathlib I/O, subprocess, socket, urllib or requests. The app checks permissions and audits
these calls, works on every location the user can open (shares, SFTP, archives, registry) and Commander4K's
installer warns about plugins that bypass them. Never print() to stdout: it is the protocol channel (use log()).
"""
import base64
import json
import sys

sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8", newline="\n")

_handlers = {}
_queue = []
_next_id = 0


class HostError(Exception):
    """The app refused or failed a call; code -32001 means a permission missing from plugin.json."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def handler(method):
    def register(function):
        _handlers[method] = function
        return function
    return register


def _send(message):
    sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _read():
    line = sys.stdin.readline()
    if not line:
        sys.exit(0)
    return json.loads(line)


def call(method, /, **params):
    """Request to the app; blocks until it answers. Requests arriving meanwhile are queued.

    `method` is positional-only, so a parameter of the same name (net.request's method="POST") is passed through.
    """
    global _next_id
    _next_id += 1
    request_id = f"p{_next_id}"
    _send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
    while True:
        message = _read()
        if message.get("id") == request_id and "method" not in message:
            if "error" in message:
                raise HostError(message["error"].get("code"), message["error"].get("message"))
            return message.get("result")
        _queue.append(message)


# ---- app API (see docs/plugins.md; the comment names the permission plugin.json must declare) ----
# Entries (list_dir, stat, context()["selection"]) are dicts: name, path, kind ("file", "directory", "drive",
# "archive"…), size (-1 = unknown), modified (ISO 8601 or None), hidden.
def context():                       return call("context.get")                              # {activePath, otherPath, selection}
def log(message):                    call("log.write", message=message)                      # audit log (use instead of print)
def notify(message, error=False):    call("ui.notify", message=message, error=error)
def show_text(title, text):          call("ui.showText", title=title, text=text)
def confirm(title, message):         return call("ui.confirm", title=title, message=message)  # True/False
def prompt(title, message, initial=""): return call("ui.prompt", title=title, message=message, initial=initial)  # str or None
def navigate(path, focus=None):      call("ui.navigate", path=path, focus=focus)
def open_url(url):                   call("ui.openUrl", url=url)                              # internal browser, http(s) only
def search(query, engine=None):      call("ui.search", query=query, engine=engine)            # engine: None, "assistant", "google", "chatgpt"…
def copy_text(text):                 call("ui.copyText", text=text)                           # clipboard (write only)
def storage_get(key, default=None):                                                          # the plugin's own settings/data (no permission)
    value = call("storage.get", key=key)
    return default if value is None else value
def storage_set(key, value):         call("storage.set", key=key, value=value)               # any JSON value; None removes the key
def list_dir(path):                  return call("fs.list", path=path)                       # fs.read  -> [entry]
def stat(path):                      return call("fs.stat", path=path)                       # fs.read  -> entry or None
def read_text(path, max_bytes=1048576): return call("fs.readText", path=path, maxBytes=max_bytes)  # fs.read -> str (UTF-8)
def read_bytes(path, offset=0, length=65536):                                                # fs.read  -> bytes (max 16 MB)
    return base64.b64decode(call("fs.readBytes", path=path, offset=offset, length=length)["data"])
def write_text(path, content):       call("fs.writeText", path=path, content=content)        # fs.write
def write_bytes(path, data):         call("fs.writeBytes", path=path, data=base64.b64encode(data).decode("ascii"))  # fs.write
def make_dir(path):                  call("fs.mkdir", path=path)                             # fs.write
def remove(path, recursive=False):   call("fs.delete", path=path, recursive=recursive)       # fs.write
def move(source, target, overwrite=False):                                                   # fs.write (also rename)
    call("fs.move", **{"from": source, "to": target, "overwrite": overwrite})
def list_processes():                return call("process.list")                             # process.read -> [{pid, parentPid, name, path, memoryBytes, started}]
def kill_process(pid, tree=False):   call("process.kill", pid=pid, tree=tree)                # process.kill
def run(file, args=(), cwd=None, elevated=False):                                            # shell.run -> {exitCode, output, error}
    return call("shell.run", file=file, args=list(args), cwd=cwd, elevated=elevated)
def system_info():                   return call("system.info")                              # system.info -> {os, machine, user, processors, memoryBytes, drives}
def http_get(url):                   return call("net.get", url=url)                         # net -> body text (error on non-2xx)
def http_request(url, method="GET", headers=None, body=None, content_type=None):             # net -> {status, headers, body}
    return call("net.request", url=url, method=method, headers=headers or {}, body=body, contentType=content_type)


def _dispatch(message):
    method = message.get("method")
    if method is None or method.startswith("$/"):
        return
    request_id = message.get("id")
    if method == "shutdown":
        _send({"jsonrpc": "2.0", "id": request_id, "result": None})
        sys.exit(0)
    function = _handlers.get(method)
    try:
        if function is None:
            if method == "initialize":
                result = {}
            else:
                raise HostError(-32601, f"Unknown method {method}")
        else:
            result = function(message.get("params") or {})
        if request_id is not None:
            _send({"jsonrpc": "2.0", "id": request_id, "result": result})
    except HostError as error:
        if request_id is not None:
            _send({"jsonrpc": "2.0", "id": request_id, "error": {"code": error.code or -32000, "message": str(error)}})
    except Exception as error:  # noqa: BLE001 - report any plugin failure to the app instead of dying
        if request_id is not None:
            _send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32000, "message": f"{type(error).__name__}: {error}"}})


def serve():
    """Main loop: handle requests from the app until it sends shutdown or closes stdin."""
    while True:
        _dispatch(_queue.pop(0) if _queue else _read())
