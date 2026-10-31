"""Official plugin: HTML pages as readable Markdown (title, contents, text, links, images); a dashboard card counts them.

The page is read through the app (fs.read) with c4k.read_bytes and never rendered: no scripts run.
"""
import pathlib
import re
from html.parser import HTMLParser

import c4k

MAX_READ = 4 * 1024 * 1024
MAX_TEXT = 6000
MAX_LINKS = 200
MAX_IMAGES = 100
SKIPPED = {"script", "style", "nav", "footer", "noscript", "template", "svg", "head"}
BLOCKS = {"p", "li", "td", "th", "blockquote", "pre", "dd", "dt", "figcaption"}
_CHARSET = re.compile(rb"""charset\s*=\s*["']?([A-Za-z0-9_\-]+)""", re.IGNORECASE)


class _Page(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.title = ""
        self.description = ""
        self.headings = []
        self.blocks = []
        self.links = []
        self.images = []
        self._skip = 0
        self._in_title = False
        self._heading = None
        self._block = None
        self._link = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "title":
            self._in_title = True
        elif tag == "meta" and (attrs.get("name") or "").lower() == "description":
            self.description = (attrs.get("content") or "").strip()
        elif tag in SKIPPED:
            self._skip += 1
        if tag == "a" and attrs.get("href"):
            self._link = [attrs["href"], []]
        elif tag == "img" and attrs.get("src"):
            self.images.append((attrs["src"], (attrs.get("alt") or "").strip()))
        elif self._skip == 0 and tag in ("h1", "h2", "h3"):
            self._heading = [int(tag[1]), []]
        elif self._skip == 0 and tag in BLOCKS:
            self._block = []

    def handle_endtag(self, tag):
        if tag == "title":
            self._in_title = False
        elif tag in SKIPPED and self._skip > 0:
            self._skip -= 1
        if tag == "a" and self._link is not None:
            self.links.append((self._link[0], _squeeze("".join(self._link[1]))))
            self._link = None
        elif tag in ("h1", "h2", "h3") and self._heading is not None:
            text = _squeeze("".join(self._heading[1]))
            if text:
                self.headings.append((self._heading[0], text))
            self._heading = None
        elif tag in BLOCKS and self._block is not None:
            text = _squeeze("".join(self._block))
            if text:
                self.blocks.append(text)
            self._block = None

    def handle_data(self, data):
        if self._in_title:
            self.title += data
        if self._link is not None:
            self._link[1].append(data)
        if self._skip:
            return
        if self._heading is not None:
            self._heading[1].append(data)
        elif self._block is not None:
            self._block.append(data)


def _squeeze(text):
    return " ".join(text.split())


def _decode(data):
    """BOM, then the page's own charset (meta), then UTF-8, then Windows-1250."""
    if data.startswith(b"\xef\xbb\xbf"):
        return data[3:].decode("utf-8", errors="replace")
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        return data.decode("utf-16", errors="replace")
    declared = _CHARSET.search(data[:4096])
    if declared:
        try:
            return data.decode(declared.group(1).decode("ascii"), errors="replace")
        except LookupError:
            pass
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("cp1250", errors="replace")


def _read(entry):
    size = entry.get("size") or MAX_READ
    data = c4k.read_bytes(entry["path"], 0, min(max(size, 1), MAX_READ))
    page = _Page()
    page.feed(_decode(data))
    page.close()
    return page, len(data)


def _size(count):
    return f"{count} B" if count < 1024 else f"{count / 1024:.1f} KB" if count < 1024 * 1024 else f"{count / 1024 / 1024:.1f} MB"


def _browser_target(path):
    """file:// address of a page on a disk or share, for the app's browser panel."""
    return pathlib.PureWindowsPath(path).as_uri() if re.match(r"^([A-Za-z]:\\|\\\\)", path) else path


def _cell(text):
    return text.replace("[", "\\[").replace("]", "\\]")


@c4k.handler("preview.render")
def render(params):
    entry = params["entry"]
    page, size = _read(entry)
    title = _squeeze(page.title) or entry["name"]
    lines = [f"# {title}", ""]
    if page.description:
        lines += [f"> {page.description}", ""]
    lines += [f"*{_size(size)} · {len(page.links)} linków · {len(page.images)} obrazów*", ""]
    if page.headings:
        lines += ["## Spis treści", ""]
        top = min(level for level, _ in page.headings)
        lines += [f"{'  ' * (level - top)}- {text}" for level, text in page.headings]
        lines.append("")
    if page.blocks:
        lines += ["## Tekst", ""]
        shown = 0
        for block in page.blocks:
            if shown + len(block) > MAX_TEXT:
                lines.append("…")
                break
            lines += [block, ""]
            shown += len(block)
    if page.links:
        lines += ["## Linki", ""]
        lines += [f"- [{_cell(text) or href}]({href.replace(' ', '%20')})" for href, text in page.links[:MAX_LINKS]]
        lines.append("")
    if page.images:
        lines += ["## Obrazy", ""]
        lines += [f"- `{src}`" + (f" — {alt}" if alt else "") for src, alt in page.images[:MAX_IMAGES]]
    return {"kind": "markdown", "content": "\n".join(lines)}


@c4k.handler("preview.card")
def card(params):
    entry = params["entry"]
    page, size = _read(entry)
    return {
        "title": _squeeze(page.title) or entry["name"],
        "subtitle": page.description[:120] or None,
        "icon": "🌐",
        "status": "ok",
        "items": [
            {"kind": "value", "label": "Rozmiar", "value": _size(size)},
            {"kind": "value", "label": "Linki", "value": str(len(page.links))},
            {"kind": "value", "label": "Obrazy", "value": str(len(page.images))},
        ],
        "actions": [
            {"label": "Otwórz w przeglądarce", "target": _browser_target(entry["path"])},
            {"label": "Pokaż plik", "target": entry["path"]},
        ],
    }


c4k.serve()
