"""Official plugin: EXE/DLL (PE) files described from their headers; nothing is executed.

Everything is read through the app (fs.read) with c4k.read_bytes. One function per Markdown section; ORDER lists the
sections of each preview id. Nothing raises out of preview.render.
"""
import datetime

import c4k
import pe

MAX_SHOWN_FUNCTIONS = 400
MAX_SHOWN_EXPORTS = 2000

SUBSYSTEMS = {1: "natywny (sterownik)", 2: "GUI Windows", 3: "konsola", 7: "POSIX", 9: "Windows CE",
              10: "EFI", 11: "EFI", 12: "EFI", 13: "EFI", 14: "Xbox", 16: "aplikacja rozruchowa"}
WINDOWS = {"5.0": "Windows 2000", "5.1": "Windows XP", "5.2": "Windows XP x64", "6.0": "Windows Vista", "6.1": "Windows 7",
           "6.2": "Windows 8", "6.3": "Windows 8.1", "10.0": "Windows 10/11"}
PACKERS = ("upx", "aspack", "themida", "mpress", "petite", "fsg", ".vmp", "enigma")


def _size(count):
    return f"{count} B" if count < 1024 else f"{count / 1024:.1f} KB" if count < 1024 * 1024 else f"{count / 1024 / 1024:.1f} MB"


def _mark(flag):
    return "✅" if flag else "❌"


def _cell(text):
    return str(text).replace("|", "\\|")


def _timestamp(image):
    stamp = image.timestamp
    if stamp == 0 or stamp < 631152000 or stamp > datetime.datetime.now(datetime.timezone.utc).timestamp():
        return "deterministyczny (hash)"
    return datetime.datetime.fromtimestamp(stamp, datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")


def section_header(image, entry):
    kind = "biblioteka DLL" if image.is_dll else "program"
    bits = "PE32+" if image.pe32_plus else "PE32"
    system = WINDOWS.get(image.os_version)
    rows = [
        ("architektura", f"{image.machine} ({bits})"),
        ("typ", kind),
        ("podsystem", SUBSYSTEMS.get(image.subsystem, f"inny ({image.subsystem})")),
        ("czas kompilacji", _timestamp(image)),
        ("linker", image.linker),
        ("minimalny Windows", f"{image.os_version} ({system})" if system else image.os_version),
        ("punkt wejścia", f"0x{image.entry_rva:X}" if image.entry_rva else "brak"),
        ("rozmiar pliku", _size(image.file_size)),
    ]
    return ["## Nagłówek", "", "| Cecha | Wartość |", "|---|---|"] + [f"| {name} | {_cell(value)} |" for name, value in rows] + [""]


def section_security(image, entry):
    d = image.dll_characteristics
    return ["## Zabezpieczenia", "",
            f"- ASLR: {_mark(d & 0x40)}",
            f"- Wysoka entropia VA: {_mark(d & 0x20)}",
            f"- DEP/NX: {_mark(d & 0x100)}",
            f"- CFG: {_mark(d & 0x4000)}",
            f"- Integralność wymuszona: {_mark(d & 0x80)}",
            f"- AppContainer: {_mark(d & 0x1000)}",
            f"- Large address aware: {_mark(image.characteristics & 0x20)}", ""]


def section_sections(image, entry):
    if not image.sections:
        return []
    lines = ["## Sekcje", "", "| Nazwa | RVA | Rozmiar | Entropia | Flagi |", "|---|---|---|---|---|"]
    for s in image.sections:
        flags = ("R" if s.flags & 0x40000000 else "-") + ("W" if s.flags & 0x80000000 else "-") + ("X" if s.flags & 0x20000000 else "-")
        entropy = f"{s.entropy:.2f}" if s.entropy is not None else "—"
        lines.append(f"| {_cell(s.name)} | 0x{s.rva:X} | {_size(s.raw_size)} | {entropy} | {flags} |")
    return lines + [""]


def section_imports(image, entry):
    if not image.imports:
        return []
    lines = ["## Importy", ""]
    for item in image.imports:
        suffix = ", opóźniony" if item.delayed else ""
        lines.append(f"- **{_cell(item.dll)}** — {len(item.functions)} funkcji{suffix}")
        shown = item.functions[:MAX_SHOWN_FUNCTIONS]
        if shown:
            text = ", ".join("`" + name.replace("`", "'") + "`" for name in shown)
            if len(item.functions) > len(shown):
                text += f" … i jeszcze {len(item.functions) - len(shown)}"
            lines.append(f"  {text}")
    return lines + [""]


def section_exports(image, entry):
    exports = image.exports
    if exports is None:
        return []
    name = f" ({_cell(exports.dll_name)})" if exports.dll_name else ""
    lines = ["## Eksporty", "", f"{exports.total} wpisów{name}", "", "| Ordinal | Nazwa | Przekierowanie |", "|---|---|---|"]
    for ordinal, func, forwarder in exports.items[:MAX_SHOWN_EXPORTS]:
        lines.append(f"| {ordinal} | {_cell(func) if func else '—'} | {'→ ' + _cell(forwarder) if forwarder else ''} |")
    if exports.total > len(exports.items):
        lines += ["", f"… i jeszcze {exports.total - len(exports.items)}"]
    return lines + [""]


def section_notes(image, entry):
    notes = [f"⚠ {w}" for w in image.warnings]
    for s in image.sections:
        if s.flags & 0x20000000 and s.flags & 0x80000000:
            notes.append(f"Sekcja {_cell(s.name)} jest jednocześnie zapisywalna i wykonywalna (RWX).")
    packed = [s.name for s in image.sections if (s.entropy or 0) > 7.2 or s.name.lower().startswith(PACKERS)]
    if packed:
        notes.append(f"Sekcje {', '.join(_cell(n) for n in packed)}: wysoka entropia lub nazwa packera — prawdopodobnie spakowany.")
    if image.tls_callbacks:
        notes.append(f"Plik ma {image.tls_callbacks} wywołań zwrotnych TLS (kod uruchamiany przed punktem wejścia).")
    if image.overlay:
        notes.append(f"Za ostatnią sekcją są dodatkowe dane ({_size(image.overlay[1])}); mogą zawierać podpis lub dołączone archiwum.")
    return ["## Uwagi", ""] + [f"- {n}" for n in notes] + [""] if notes else []


ORDER = {
    "exe": [section_header, section_security, section_sections, section_imports, section_exports, section_notes],
    "dll": [section_header, section_exports, section_security, section_sections, section_imports, section_notes],
}


def _analyze(entry):
    size = entry.get("size")
    if not size:
        found = c4k.stat(entry["path"])
        size = (found or {}).get("size") or 0
    return pe.parse(pe.RangeReader(entry["path"], size))


@c4k.handler("preview.render")
def render(params):
    entry = params["entry"]
    kind = params.get("preview") or "exe"
    lines = [f"# {entry['name']}", ""]
    try:
        image = _analyze(entry)
    except Exception as error:  # nothing may raise out of preview.render
        return {"kind": "markdown", "content": "\n".join(lines + [f"⚠ To nie jest prawidłowym plikiem PE ({error})."])}
    for build in ORDER.get(kind, ORDER["exe"]):
        try:
            lines += build(image, entry)
        except Exception as error:
            lines += [f"⚠ Sekcja {build.__name__.removeprefix('section_')}: nie udało się wyświetlić ({error}).", ""]
    return {"kind": "markdown", "content": "\n".join(lines)}


@c4k.handler("preview.card")
def card(params):
    entry = params["entry"]
    actions = [{"label": "Otwórz", "target": entry["path"]}]
    try:
        image = _analyze(entry)
    except Exception as error:
        return {"icon": "⚙", "status": "error", "items": [{"kind": "text", "text": f"To nie jest prawidłowy plik PE ({error})"}], "actions": actions}
    items = [{"kind": "value", "label": "Architektura", "value": image.machine},
             {"kind": "value", "label": "Typ", "value": "DLL" if image.is_dll else SUBSYSTEMS.get(image.subsystem, "program")},
             {"kind": "value", "label": "Importy", "value": str(len(image.imports))}]
    if image.exports is not None:
        items.append({"kind": "value", "label": "Eksporty", "value": str(image.exports.total)})
    return {"icon": "⚙", "status": "warning" if image.warnings else "ok", "items": items, "actions": actions}


c4k.serve()
