"""Official plugin: what is inside an archive (ZIP, 7z, RAR, TAR, NuGet, wheel, JAR, VSIX), read through the app.

The app's archive file system lists the archive (<scheme>://<archive>, entries <scheme>://<archive>::/<inner/path>), so
nothing is unpacked to disk. The walk is breadth-first and capped; package metadata is read with c4k.read_text.
Nothing raises out of preview.render: a failed host call becomes a "⚠" line.
"""
import posixpath
from collections import deque

import c4k
import packages

# Copy of ArchiveVfsPlugin.Formats: extension → scheme, longest first so .tar.gz wins over .gz.
FORMATS = [
    (".tar.gz", "tar"), (".tar.bz2", "tar"), (".tar.xz", "tar"), (".tgz", "tar"), (".tbz2", "tar"), (".txz", "tar"), (".tar", "tar"),
    (".zip", "zip"), (".jar", "zip"), (".nupkg", "zip"), (".vsix", "zip"), (".whl", "zip"),
    (".7z", "7z"), (".rar", "rar"), (".gz", "gz"),
]
MAX_ENTRIES = 20000
MAX_DIRS = 2000
MAX_TREE_LINES = 200
MAX_LARGEST = 15
MAX_TYPES = 10
README_LINES = 40
README_BYTES = 16384
EXECUTABLE = {".exe", ".dll", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".lnk", ".scr", ".hta"}
DECOYS = {".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".csv", ".jpg", ".jpeg",
          ".png", ".gif", ".bmp", ".mp3", ".mp4", ".avi", ".mov", ".zip", ".rar", ".htm", ".html"}
BOMB_RATIO = 100
BOMB_SIZE = 1 << 30


def _size(count):
    text = f"{count} B" if count < 1024 else f"{count / 1024:.1f} KB" if count < 1024 * 1024 else f"{count / 1048576:.1f} MB"
    return text.replace(".", ",")


def _plural(count, one, few, many):
    """Polish plural form: 1 → one, 2–4 (but not 12–14) → few, otherwise many."""
    if count == 1:
        return one
    return few if count % 10 in (2, 3, 4) and count % 100 not in (12, 13, 14) else many


def _count(number):
    return f"{number:,}".replace(",", chr(0xA0))  # Polish thousands separator (no-break space)


def _files(count):
    return f"{count} {_plural(count, 'plik', 'pliki', 'plików')}"


def _md(text):
    """A file name as Markdown text: inline markup characters escaped, nothing that starts a block."""
    text = str(text)
    for char in "\\`*_[]|<":
        text = text.replace(char, "\\" + char)
    return "\\" + text if text[:1] in "#+-" else text


def archive_root(path):
    lower = path.lower()
    scheme = next((scheme for extension, scheme in FORMATS if lower.endswith(extension)), "zip")
    return f"{scheme}://{path}"


def inner(root, relative):
    return f"{root}::/{relative}"


class Listing:
    def __init__(self):
        self.files = []      # entries with an added "relative" key (path inside the archive, "/"-separated)
        self.dirs = 0
        self.folders = []    # relative paths of the folders seen
        self.truncated = False
        self.error = None    # the app's message when the archive itself could not be listed
        self.failed = []     # (relative folder, message) for sub-folders that could not be listed


def _relative(root, entry):
    path = entry.get("path") or ""
    prefix = root + "::/"
    return path[len(prefix):].strip("/") if path.startswith(prefix) else entry.get("name") or path


def walk(root, max_entries=MAX_ENTRIES, max_dirs=MAX_DIRS):
    listing = Listing()
    queue = deque([(root, "")])
    seen = {root}
    listed = 0
    count = 0
    while queue:
        if listed >= max_dirs:
            listing.truncated = True
            break
        path, relative = queue.popleft()
        try:
            entries = c4k.list_dir(path) or []
        except Exception as error:
            if listed == 0:
                listing.error = str(error) or type(error).__name__
                return listing
            listing.failed.append((relative, str(error)))
            listed += 1
            continue
        listed += 1
        for entry in entries:
            if count >= max_entries:
                listing.truncated = True
                return listing
            count += 1
            entry = dict(entry)
            entry["relative"] = _relative(root, entry)
            if entry.get("kind") == "directory":
                listing.dirs += 1
                listing.folders.append(entry["relative"])
                child = entry.get("path")
                if child and child not in seen:
                    seen.add(child)
                    queue.append((child, entry["relative"]))
            else:
                listing.files.append(entry)
    return listing


def _extension(name):
    return posixpath.splitext(name)[1].lower()


def _total(listing):
    return sum(entry.get("size") or 0 for entry in listing.files)


def _ratio(total, archive_size):
    return total / archive_size if archive_size else 0


def section_summary(listing, entry):
    total = _total(listing)
    size = entry.get("size") or 0
    lines = ["## Podsumowanie", "",
             f"- Pliki: {len(listing.files)}",
             f"- Foldery: {listing.dirs}",
             f"- Rozmiar po rozpakowaniu: {_size(total)}",
             f"- Rozmiar archiwum: {_size(size)}"]
    if size and total:
        lines.append(f"- Współczynnik rozpakowania: {_ratio(total, size):.1f}×".replace(".", ","))
    if listing.truncated:
        lines += ["", f"⚠ lista niepełna: przejrzano najwyżej {_count(MAX_ENTRIES)} wpisów i {_count(MAX_DIRS)} folderów, "
                      "sumy dotyczą tylko przejrzanej części."]
    for relative, message in listing.failed[:5]:
        lines += ["", f"⚠ Nie udało się odczytać folderu {_md(relative)}: {message}"]
    return lines + [""]


def section_tree(listing, entry):
    children = {}
    for relative in listing.folders:
        children.setdefault(posixpath.dirname(relative), []).append((0, posixpath.basename(relative), relative, None))
    for item in listing.files:
        relative = item["relative"]
        children.setdefault(posixpath.dirname(relative), []).append((1, posixpath.basename(relative), relative, item))
    totals = {}
    for item in listing.files:
        top = item["relative"].split("/", 1)
        if len(top) == 2:
            count, size = totals.get(top[0], (0, 0))
            totals[top[0]] = (count + 1, size + (item.get("size") or 0))

    def describe(name, relative, item, depth):
        indent = "  " * depth
        if item is not None:
            return f"{indent}- {_md(name)} — {_size(item.get('size') or 0)}"
        if depth == 0 and relative in totals:
            count, size = totals[relative]
            return f"{indent}- 📁 {_md(name)}/ — {_files(count)}, {_size(size)}"
        return f"{indent}- 📁 {_md(name)}/"

    lines = []
    for kind, name, relative, item in sorted(children.get("", []), key=lambda c: (c[0], c[1].lower())):
        lines.append(describe(name, relative, item, 0))
        if kind == 0:
            for _kind, sub, sub_relative, sub_item in sorted(children.get(relative, []), key=lambda c: (c[0], c[1].lower())):
                lines.append(describe(sub, sub_relative, sub_item, 1))
    if not lines:
        return ["## Zawartość", "", "Archiwum jest puste.", ""]
    shown = lines[:MAX_TREE_LINES]
    if len(lines) > len(shown):
        shown.append(f"- … i jeszcze {len(lines) - len(shown)} {_plural(len(lines) - len(shown), 'pozycja', 'pozycje', 'pozycji')}")
    return ["## Zawartość", ""] + shown + [""]


def section_largest(listing, entry):
    largest = sorted(listing.files, key=lambda item: item.get("size") or 0, reverse=True)[:MAX_LARGEST]
    if not largest:
        return []
    lines = ["## Największe pliki", "", "| Plik | Rozmiar |", "| --- | ---: |"]
    lines += [f"| {_md(item['relative'])} | {_size(item.get('size') or 0)} |" for item in largest]
    return lines + [""]


def section_types(listing, entry):
    types = {}
    for item in listing.files:
        extension = _extension(item["relative"]) or "(bez rozszerzenia)"
        count, size = types.get(extension, (0, 0))
        types[extension] = (count + 1, size + (item.get("size") or 0))
    if not types:
        return []
    top = sorted(types.items(), key=lambda pair: (-pair[1][0], -pair[1][1], pair[0]))[:MAX_TYPES]
    lines = ["## Typy plików", "", "| Rozszerzenie | Pliki | Rozmiar |", "| --- | ---: | ---: |"]
    lines += [f"| {_md(extension)} | {count} | {_size(size)} |" for extension, (count, size) in top]
    if len(types) > len(top):
        lines += ["", f"… i jeszcze {len(types) - len(top)} {_plural(len(types) - len(top), 'typ', 'typy', 'typów')}"]
    return lines + [""]


def section_readme(listing, entry):
    candidates = [item for item in listing.files if "/" not in item["relative"] and item["relative"].lower().startswith("readme")]
    if not candidates:
        return []
    candidates.sort(key=lambda item: (_extension(item["relative"]) not in (".md", ".markdown"), item["relative"].lower()))
    readme = candidates[0]
    try:
        text = c4k.read_text(readme["path"], max_bytes=README_BYTES)
    except Exception as error:
        return ["## README", "", f"⚠ Nie udało się odczytać {_md(readme['relative'])}: {error}", ""]
    rows = text.lstrip(chr(0xFEFF)).replace("\r\n", "\n").split("\n")
    cut = len(rows) > README_LINES
    rows = rows[:README_LINES]
    lines = ["## README", "", f"*{_md(readme['relative'])}*", ""]
    if _extension(readme["relative"]) in (".md", ".markdown"):
        lines += rows
        if sum(1 for row in rows if row.lstrip().startswith(("```", "~~~"))) % 2:
            lines.append("```")  # the cut left a code block open
    else:
        lines += ["```text"] + [row.replace("```", "ˋˋˋ") for row in rows] + ["```"]
    if cut:
        lines += ["", f"… (pierwsze {README_LINES} wierszy)"]
    return lines + [""]


def _find(listing, test):
    return next((item for item in listing.files if test(item["relative"].lower())), None)


def _package(root, listing, entry):
    """(title, file entry or inner path, reader) for the archive's package kind, else None."""
    name = (entry.get("name") or entry.get("path") or "").lower()
    if name.endswith(".nupkg"):
        found = _find(listing, lambda r: "/" not in r and r.endswith(".nuspec"))
        return "Pakiet NuGet", found, packages.nuspec
    if name.endswith(".whl"):
        found = _find(listing, lambda r: r.count("/") == 1 and r.split("/")[0].endswith(".dist-info") and r.endswith("/metadata"))
        return "Python (wheel)", found, packages.wheel_metadata
    if name.endswith(".jar"):
        found = _find(listing, lambda r: r == "meta-inf/manifest.mf")
        return "Java (JAR)", found or {"path": inner(root, "META-INF/MANIFEST.MF"), "relative": "META-INF/MANIFEST.MF"}, packages.jar_manifest
    if name.endswith(".vsix"):
        found = _find(listing, lambda r: r == "extension.vsixmanifest")
        return "Rozszerzenie VS Code/VS", found or {"path": inner(root, "extension.vsixmanifest"), "relative": "extension.vsixmanifest"}, packages.vsix
    return None


def section_package(root, listing, entry):
    package = _package(root, listing, entry)
    if package is None:
        return []
    title, found, read = package
    lines = [f"## {title}", ""]
    if found is None:
        return lines + ["⚠ Nie znaleziono pliku z opisem pakietu.", ""]
    try:
        text = c4k.read_text(found["path"])
    except Exception as error:
        return lines + [f"⚠ Nie udało się odczytać {_md(found['relative'])}: {error}", ""]
    try:
        rows, dependencies = read(text)
    except packages.UnsafeXml as error:
        return lines + [f"⚠ {_md(found['relative'])}: {error} — plik nie został odczytany.", ""]
    except Exception as error:
        return lines + [f"⚠ Nie udało się odczytać {_md(found['relative'])}: {error}", ""]
    lines.append(f"*{_md(found['relative'])}*")
    lines.append("")
    if rows:
        lines += ["| | |", "| --- | --- |"] + [f"| {label} | {_md(value)} |" for label, value in rows]
    lines += ["", "### Zależności", ""]
    lines += [f"- {_md(dependency)}" for dependency in dependencies] if dependencies else ["brak"]
    return lines + [""]


def notes(listing, entry):
    found = []
    executables = [item["relative"] for item in listing.files if _extension(item["relative"]) in EXECUTABLE]
    if executables:
        sample = ", ".join(_md(name) for name in executables[:10])
        more = f" … i jeszcze {len(executables) - 10}" if len(executables) > 10 else ""
        found.append(f"zawiera pliki wykonywalne lub skrypty ({len(executables)}): {sample}{more}")
    for item in listing.files:
        name = posixpath.basename(item["relative"])
        stem, last = posixpath.splitext(name.lower())
        if last in EXECUTABLE and posixpath.splitext(stem)[1] in DECOYS:
            found.append(f"podwójne rozszerzenie: {_md(item['relative'])} — wygląda na dokument, a jest plikiem wykonywalnym")
    total = _total(listing)
    if total > BOMB_SIZE and _ratio(total, entry.get("size") or 0) > BOMB_RATIO:
        found.append(f"możliwa bomba zip: {_size(total)} po rozpakowaniu z {_size(entry.get('size') or 0)}")
    return found


def section_notes(listing, entry):
    found = notes(listing, entry)
    return ["## Uwagi", ""] + ([f"- {note}" for note in found] or ["Brak uwag."]) + [""]


@c4k.handler("preview.render")
def render(params):
    entry = params["entry"]
    lines = [f"# {_md(entry.get('name') or entry.get('path') or 'archiwum')}", ""]
    try:
        root = archive_root(entry["path"])
        listing = walk(root)
    except Exception as error:  # nothing may raise out of preview.render
        return {"kind": "markdown", "content": "\n".join(lines + [f"⚠ Nie udało się odczytać archiwum: {error}"])}
    if listing.error is not None:
        lines += [f"⚠ Nie udało się otworzyć archiwum: {listing.error}", "",
                  f"Rozmiar pliku: {_size(entry.get('size') or 0)}"]
        return {"kind": "markdown", "content": "\n".join(lines)}
    builds = [section_summary, section_tree, section_largest, section_types, section_readme,
              lambda listing, entry: section_package(root, listing, entry), section_notes]
    for build in builds:
        try:
            lines += build(listing, entry)
        except Exception as error:
            lines += [f"⚠ Nie udało się wyświetlić części podglądu ({error}).", ""]
    return {"kind": "markdown", "content": "\n".join(lines)}


@c4k.handler("preview.card")
def card(params):
    # Minimal card; the full one comes with the dashboard work.
    entry = params.get("entry") or {}
    actions = [{"label": "Otwórz", "target": entry.get("path", "")}] if entry.get("path") else []
    try:
        listing = walk(archive_root(entry["path"]))
    except Exception as error:
        return {"icon": "🗜", "status": "error", "items": [{"kind": "text", "text": str(error)}], "actions": actions}
    if listing.error is not None:
        return {"icon": "🗜", "status": "error", "items": [{"kind": "text", "text": listing.error}], "actions": actions}
    items = [{"kind": "value", "label": "Pliki", "value": str(len(listing.files))},
             {"kind": "value", "label": "Rozmiar", "value": _size(_total(listing))}]
    return {"icon": "🗜", "status": "warning" if listing.truncated else "ok", "items": items, "actions": actions}


c4k.serve()
