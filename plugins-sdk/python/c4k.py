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
    """The app refused or failed a call. code -32001: a permission missing from plugin.json; -32010: mail login
    needed (data = {"authRequired": True, "realm": ...}); -32011: untrusted mail certificate (data = {"host", "fingerprint"})."""

    def __init__(self, code, message, data=None):
        super().__init__(message)
        self.code = code
        self.data = data


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
                raise HostError(message["error"].get("code"), message["error"].get("message"), message["error"].get("data"))
            return message.get("result")
        _queue.append(message)


# ---- app API (see docs/plugins.md; the comment names the permission plugin.json must declare) ----
# Entries (list_dir, stat, context()["selection"]) are dicts: name, path, kind ("file", "directory", "drive",
# "archive"…), size (-1 = unknown), modified (ISO 8601 or None), hidden.
def context():                       return call("context.get")                              # {activePath, otherPath, selection}
def log(message):                    call("log.write", message=message)                      # audit log (use instead of print)
def notify(message, error=False, target=None): call("ui.notify", message=message, error=error, target=target)  # target: path/link opened when the toast is clicked
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


# ---- mail (permission "mail"; fetch/compose/attachments also need fs.write, parse/send/append fs.read) ----
# account = {"imap": "host:993", "smtp": "host:465" or None, "user": "name", "interactive": True}. The app owns the
# password and certificates: interactive=False never shows a dialog and raises HostError -32010/-32011 instead.
# Flags are "seen", "flagged", "answered", "draft". Message files are .eml paths (any location the app can open).
def mail_folders(account):           return call("mail.folders", account=account)             # -> [{serverName, localName, separator, special, uidValidity, count, unread}]
def mail_sync(account, folder, uid_validity=0, last_uid=0, mod_seq=0, since=None, known_uids=()):  # -> {uidValidity, uidValidityChanged, modSeq, new, flagChanges, removed}
    return call("mail.sync", account=account, folder=folder, uidValidity=uid_validity, lastUid=last_uid, modSeq=mod_seq, since=since, knownUids=list(known_uids))
def mail_fetch(account, folder, uid, path): return call("mail.fetch", account=account, folder=folder, uid=uid, path=path)["bytes"]  # fs.write -> bytes written
def mail_set_flags(account, folder, uids, add=(), remove=()): call("mail.setFlags", account=account, folder=folder, uids=list(uids), add=list(add), remove=list(remove))
def mail_move(account, folder, uids, target): call("mail.move", account=account, folder=folder, uids=list(uids), target=target)
def mail_delete(account, folder, uids): call("mail.delete", account=account, folder=folder, uids=list(uids))  # Trash, or expunge inside Trash
def mail_append(account, folder, path, flags=()): return call("mail.append", account=account, folder=folder, path=path, flags=list(flags))["uid"]  # fs.read -> new uid or None
def mail_parse(path):                return call("mail.parse", path=path)                      # fs.read -> {from, to, cc, subject, date, messageId, references, bodyText, attachments}
def mail_save_attachment(path, index, target): call("mail.saveAttachment", path=path, index=index, target=target)  # fs.read + fs.write
def mail_reply(path, mode="reply", self_address=""): return call("mail.reply", path=path, mode=mode, self=self_address)  # mode: reply | replyAll | forward -> draft (fs.read)
def mail_compose(path, draft):       call("mail.compose", path=path, draft=draft)              # fs.write (+ fs.read for draft["attachments"]); draft = {to, cc, bcc, subject, body, inReplyTo, references, attachments, from}
def mail_send(account, path):        return call("mail.send", account=account, path=path)["messageId"]  # fs.read -> Message-ID
def background_wake():              call("background.wake")                                    # background: ask the app for a background.tick now (answer ticks with @handler("background.tick") -> {"more": bool})


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
            failure = {"code": error.code or -32000, "message": str(error)}
            if error.data is not None:
                failure["data"] = error.data
            _send({"jsonrpc": "2.0", "id": request_id, "error": failure})
    except Exception as error:  # noqa: BLE001 - report any plugin failure to the app instead of dying
        if request_id is not None:
            _send({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32000, "message": f"{type(error).__name__}: {error}"}})


def serve():
    """Main loop: handle requests from the app until it sends shutdown or closes stdin."""
    while True:
        _dispatch(_queue.pop(0) if _queue else _read())
