"""Official plugin: JSON files checked, formatted and outlined; a dashboard card says whether the file is valid.

Everything is read through the app (fs.read) with c4k.read_text.
"""
import json

import c4k

MAX_READ = 4 * 1024 * 1024
MAX_SHOWN = 200 * 1024
MAX_OUTLINE = 120


def _strip_comments(text):
    """// and /* */ comments of .jsonc outside strings become spaces, so error positions keep their line and column."""
    out = []
    i, n = 0, len(text)
    in_string = False
    while i < n:
        ch = text[i]
        if in_string:
            out.append(ch)
            if ch == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if ch == '"':
                in_string = False
        elif ch == '"':
            in_string = True
            out.append(ch)
        elif text.startswith("//", i):
            end = text.find("\n", i)
            end = n if end < 0 else end
            out.append(" " * (end - i))
            i = end
            continue
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            out.append("".join(c if c == "\n" else " " for c in text[i:end]))
            i = end
            continue
        else:
            out.append(ch)
        i += 1
    return "".join(out)


class _TooLarge(Exception):
    """A file over MAX_READ is read only in part, so it cannot be checked (and must not be called invalid)."""

    lineno = colno = 0
    msg = f"plik większy niż {MAX_READ // 1024 // 1024} MB — nie sprawdzono"


def _load(entry):
    if (entry.get("size") or 0) > MAX_READ:
        return "", None, _TooLarge()
    text = c4k.read_text(entry["path"], max_bytes=MAX_READ).lstrip("﻿")  # a UTF-8 BOM from Windows editors
    source = _strip_comments(text) if entry["name"].lower().endswith(".jsonc") else text
    try:
        return text, json.loads(source), None
    except json.JSONDecodeError as error:
        return text, None, error


def _kind(value):
    if isinstance(value, dict):
        return f"obiekt ({len(value)})"
    if isinstance(value, list):
        return f"tablica ({len(value)})"
    if isinstance(value, bool):
        return "logiczna"
    if isinstance(value, (int, float)):
        return "liczba"
    if value is None:
        return "null"
    return "tekst"


def _outline(value, path, lines, depth=0):
    """`path` — kind, for the root's members down to depth 4; arrays are described by their first element."""
    if len(lines) >= MAX_OUTLINE or depth > 4:
        return
    if isinstance(value, dict):
        for key, child in value.items():
            child_path = f"{path}.{key}" if path else key
            lines.append(f"- `{child_path}` — {_kind(child)}")
            _outline(child, child_path, lines, depth + 1)
    elif isinstance(value, list) and value and isinstance(value[0], (dict, list)):
        _outline(value[0], f"{path}[0]", lines, depth + 1)


def _summary(value):
    if isinstance(value, dict):
        return f"obiekt, {len(value)} kluczy"
    if isinstance(value, list):
        return f"tablica, {len(value)} elementów"
    return _kind(value)


def _size(count):
    return f"{count} B" if count < 1024 else f"{count / 1024:.1f} KB" if count < 1024 * 1024 else f"{count / 1024 / 1024:.1f} MB"


@c4k.handler("preview.render")
def render(params):
    entry = params["entry"]
    text, value, error = _load(entry)
    lines = [f"### {entry['name']}", ""]
    if isinstance(error, _TooLarge):
        return {"kind": "markdown", "content": "\n".join(lines + [f"⚠ {_size(entry['size'])}: {error.msg}."])}
    if error is not None:
        lines += [f"❌ **Niepoprawny JSON**: {error.msg} — linia {error.lineno}, kolumna {error.colno}", ""]
        around = text.splitlines()[max(error.lineno - 3, 0):error.lineno + 2]
        lines += ["```", *around, "```"]
        return {"kind": "markdown", "content": "\n".join(lines)}

    lines += [f"✅ Poprawny JSON · {_summary(value)} · {_size(len(text.encode('utf-8')))}", ""]
    outline = []
    _outline(value, "", outline)
    if outline:
        lines += ["**Zarys**", "", *outline, ""]
    formatted = json.dumps(value, indent=2, ensure_ascii=False)
    if len(formatted) > MAX_SHOWN:
        formatted = formatted[:MAX_SHOWN] + "\n…"
        lines.append(f"*Pokazano pierwsze {MAX_SHOWN // 1024} KB.*")
    lines += ["```json", formatted, "```"]
    return {"kind": "markdown", "content": "\n".join(lines)}


@c4k.handler("preview.card")
def card(params):
    entry = params["entry"]
    text, value, error = _load(entry)
    items = [{"kind": "value", "label": "Rozmiar", "value": _size(entry.get("size") or len(text.encode("utf-8")))}]
    if isinstance(error, _TooLarge):
        items.append({"kind": "text", "text": error.msg})
        status = "warning"
    elif error is not None:
        items.append({"kind": "value", "label": "Błąd", "value": f"linia {error.lineno}, kolumna {error.colno}", "status": "error"})
        status = "error"
    else:
        items.append({"kind": "value", "label": "Zawartość", "value": _summary(value)})
        if isinstance(value, dict) and value:
            keys = list(value)[:5]
            items.append({"kind": "text", "text": " · ".join(keys) + (" · …" if len(value) > 5 else "")})
        status = "ok"
    return {"icon": "{ }", "status": status, "items": items, "actions": [{"label": "Otwórz", "target": entry["path"]}]}


c4k.serve()
