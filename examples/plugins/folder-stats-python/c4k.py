"""Commander4K plugin protocol v1 — Python client (copy this file next to your plugin's main.py).

The app starts the plugin with stdin/stdout connected; messages are JSON-RPC 2.0, one per line.
Register handlers with @handler("command.execute") etc., call the app with the helpers below
(every call is permission-checked against plugin.json and written to the app's audit log), then run().
"""
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


def call(method, **params):
    """Request to the app; blocks until it answers. Requests arriving meanwhile are queued."""
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


# ---- app API (see docs/plugins.md) ----
def context():                       return call("context.get")
def log(message):                    call("log.write", message=message)
def notify(message, error=False):    call("ui.notify", message=message, error=error)
def show_text(title, text):          call("ui.showText", title=title, text=text)
def confirm(title, message):         return call("ui.confirm", title=title, message=message)
def prompt(title, message, initial=""): return call("ui.prompt", title=title, message=message, initial=initial)
def navigate(path, focus=None):      call("ui.navigate", path=path, focus=focus)
def open_url(url):                   call("ui.openUrl", url=url)                              # internal browser, http(s) only
def search(query, engine=None):      call("ui.search", query=query, engine=engine)            # engine: None, "assistant", "google", "chatgpt"…
def list_dir(path):                  return call("fs.list", path=path)                       # fs.read
def read_text(path, max_bytes=1048576): return call("fs.readText", path=path, maxBytes=max_bytes)  # fs.read
def write_text(path, content):       call("fs.writeText", path=path, content=content)        # fs.write
def kill_process(pid, tree=False):   call("process.kill", pid=pid, tree=tree)                # process.kill
def run(file, args=(), cwd=None, elevated=False):                                            # shell.run
    return call("shell.run", file=file, args=list(args), cwd=cwd, elevated=elevated)
def http_get(url):                   return call("net.get", url=url)                         # net


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
