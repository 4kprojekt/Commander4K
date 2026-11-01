"""Official plugin: EXE/DLL (PE) files described from their headers; nothing is executed.

Everything is read through the app (fs.read) with c4k.read_bytes. One function per Markdown section; ORDER lists the
sections of each preview id. Nothing raises out of preview.render.
"""
import datetime

import c4k
import deps
import pe
import resources

MAX_SHOWN_FUNCTIONS = 400
MAX_SHOWN_EXPORTS = 2000

SUBSYSTEMS = {1: "natywny (sterownik)", 2: "GUI Windows", 3: "konsola", 7: "POSIX", 9: "Windows CE",
              10: "EFI", 11: "EFI", 12: "EFI", 13: "EFI", 14: "Xbox", 16: "aplikacja rozruchowa"}
WINDOWS = {"5.0": "Windows 2000", "5.1": "Windows XP", "5.2": "Windows XP x64", "6.0": "Windows Vista", "6.1": "Windows 7",
           "6.2": "Windows 8", "6.3": "Windows 8.1", "10.0": "Windows 10/11"}
PACKERS = {"upx0", "upx1", "upx2", ".aspack", ".adata", ".themida", ".vmp0", ".vmp1", ".mpress1", ".mpress2", "fsg!"}
SENSITIVE = {"VirtualAllocEx", "WriteProcessMemory", "CreateRemoteThread", "SetWindowsHookExA", "SetWindowsHookExW",
             "NtUnmapViewOfSection", "URLDownloadToFileA", "URLDownloadToFileW", "AdjustTokenPrivileges", "IsDebuggerPresent"}
LEVELS = {"asInvoker": "uprawnienia użytkownika",
          "highestAvailable": "najwyższe dostępne (administrator pyta o UAC)",
          "requireAdministrator": "wymaga administratora (UAC)"}
NO_MANIFEST = "brak manifestu — uprawnienia użytkownika, wirtualizacja UAC dla starszych programów"
NO_LEVEL = "manifest bez requestedExecutionLevel — uprawnienia użytkownika"
MAX_MODULES = 60
DEPENDENCY_KINDS = {"apiset": ("ℹ️", "zestaw API systemu"), "known": ("✅", "znana systemowa (KnownDLLs)"),
                    "local": ("✅", "obok pliku"), "missing": ("❌", "nie znaleziono")}


def _size(count):
    text = f"{count} B" if count < 1024 else f"{count / 1024:.1f} KB" if count < 1024 * 1024 else f"{count / 1048576:.1f} MB"
    return text.replace(".", ",")


def _mark(flag):
    return "✅" if flag else "❌"


def _cell(text):
    return str(text).replace("|", "\\|")


def _timestamp(image):
    stamp = image.timestamp
    if stamp == 0 or stamp < 631152000 or stamp > datetime.datetime.now(datetime.timezone.utc).timestamp():
        return "deterministyczny (hash)"
    return datetime.datetime.fromtimestamp(stamp, datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")


def section_header(image, entry, ctx):
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


def section_security(image, entry, ctx):
    d = image.dll_characteristics
    return ["## Zabezpieczenia", "",
            f"- ASLR: {_mark(d & 0x40)}",
            f"- Wysoka entropia VA: {_mark(d & 0x20)}",
            f"- DEP/NX: {_mark(d & 0x100)}",
            f"- CFG: {_mark(d & 0x4000)}",
            f"- Integralność wymuszona: {_mark(d & 0x80)}",
            f"- AppContainer: {_mark(d & 0x1000)}",
            f"- Large address aware: {_mark(image.characteristics & 0x20)}", ""]


def section_sections(image, entry, ctx):
    if not image.sections:
        return []
    lines = ["## Sekcje", "", "| Nazwa | RVA | Rozmiar | Entropia | Flagi |", "|---|---|---|---|---|"]
    for s in image.sections:
        flags = ("R" if s.flags & 0x40000000 else "-") + ("W" if s.flags & 0x80000000 else "-") + ("X" if s.flags & 0x20000000 else "-")
        entropy = f"{s.entropy:.2f}" if s.entropy is not None else "—"
        lines.append(f"| {_cell(s.name)} | 0x{s.rva:X} | {_size(s.raw_size)} | {entropy} | {flags} |")
    return lines + [""]


def section_imports(image, entry, ctx):
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


def section_exports(image, entry, ctx):
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


def section_permissions(image, entry, ctx):
    manifest = ctx.manifest()
    lines = ["## Uprawnienia", ""]
    error = ctx.manifest_error or (manifest.error if manifest is not None else None)
    if error:
        return lines + [f"- Poziom wykonania: ⚠ nie odczytano manifestu: {_cell(error)}", ""]
    if manifest is None:
        return lines + [f"- Poziom wykonania: {NO_MANIFEST}", ""]
    if manifest.skipped_reason:
        return lines + [f"⚠ Manifest: {manifest.skipped_reason}", ""]
    level = manifest.execution_level
    if level:
        lines.append(f"- Poziom wykonania: **{_cell(level)}** — {LEVELS.get(level, 'nieznany poziom')}")
    else:
        lines.append(f"- Poziom wykonania: {NO_LEVEL}")
    lines.append(f"- uiAccess: {'tak' if manifest.ui_access else 'nie'}")
    if manifest.auto_elevate:
        lines.append("- autoElevate: tak (podnosi uprawnienia bez pytania, jeśli to program systemowy)")
    if manifest.dpi:
        lines.append(f"- Skalowanie DPI: {_cell(manifest.dpi)}")
    lines.append(f"- Długie ścieżki: {'tak' if manifest.long_path_aware else 'nie zadeklarowano'}")
    if manifest.supported_os:
        lines.append(f"- Obsługiwane systemy: {', '.join(manifest.supported_os)}")
    if manifest.dependencies:
        lines.append(f"- Zależne zestawy: {', '.join(_cell(n) for n in manifest.dependencies)}")
    return lines + [""]


def _dependency_line(node):
    resolution = node.resolution
    if resolution.kind == "system":
        icon, text = "✅", f"systemowa ({deps.base_name(deps.folder_of(resolution.path))})"
    else:
        icon, text = DEPENDENCY_KINDS[resolution.kind]
    if node.delayed:
        if resolution.kind == "missing":
            icon = "ℹ️"
        text += " (opóźnione)"
    if node.error:
        text += f" — ⚠ nie odczytano importów: {_cell(node.error)}"
    return f"{'  ' * node.depth}- {icon} {_cell(resolution.name)} — {text}"


def section_dependencies(image, entry, ctx):
    nodes = ctx.dependencies()
    if ctx.dependencies_error:
        return ["## Zależności", "", f"⚠ Nie udało się ustalić zależności: {_cell(ctx.dependencies_error)}", ""]
    if not nodes:
        return []
    lines = ["## Zależności", ""] + [_dependency_line(node) for node in nodes]
    if nodes.more_api_sets:
        lines.append(f"- … i jeszcze {nodes.more_api_sets} zestawów API systemu")
    if nodes.truncated:
        lines.append(f"- … pominięto dalsze moduły (limit {MAX_MODULES})")
    return lines + [""]


def section_version(image, entry, ctx):
    info = ctx.version()
    labels = (("FileDescription", "Opis"), ("CompanyName", "Firma"), ("ProductName", "Produkt"), ("FileVersion", "Wersja pliku"),
              ("ProductVersion", "Wersja produktu"), ("OriginalFilename", "Oryginalna nazwa"), ("InternalName", "Nazwa wewnętrzna"),
              ("LegalCopyright", "Prawa autorskie"), ("_fixed", "Wersja numeryczna"))
    rows = [f"| {label} | {_cell(info[key])} |" for key, label in labels if info.get(key)]
    return ["## Wersja", "", "| Pole | Wartość |", "|---|---|"] + rows + [""] if rows else []


def notes(image, manifest, signature, missing=()):
    """Facts about the file, never a verdict. `signature` is None until the signature reader exists: then it adds nothing."""
    result = []
    d = image.dll_characteristics
    off = [name for name, flag in (("ASLR", 0x40), ("DEP/NX", 0x100), ("CFG", 0x4000)) if not d & flag]
    if off:
        result.append(f"Wyłączone zabezpieczenia: {', '.join(off)}.")
    for s in image.sections:
        if s.flags & 0x20000000 and s.flags & 0x80000000:
            result.append(f"Sekcja {_cell(s.name)} jest jednocześnie zapisywalna i wykonywalna (RWX).")
    packed = [s.name for s in image.sections
              if s.name.lower() in PACKERS or (s.flags & 0x20000000 and (s.entropy or 0) > 7.2)]
    if packed:
        result.append(f"Sekcje {', '.join(_cell(n) for n in packed)}: wysoka entropia kodu lub nazwa packera — prawdopodobnie spakowany.")
    if image.tls_callbacks:
        result.append(f"Plik ma {image.tls_callbacks} wywołań zwrotnych TLS (kod uruchamiany przed punktem wejścia).")
    if image.overlay:
        kind = f"; wygląda na: {image.overlay[2]}" if image.overlay[2] else ""
        result.append(f"Dane doklejone za ostatnią sekcją: {_size(image.overlay[1])}{kind}.")
    if manifest is not None:
        if manifest.execution_level == "requireAdministrator":
            result.append("Manifest wymaga uprawnień administratora (requireAdministrator).")
        if manifest.ui_access:
            result.append("Manifest ustawia uiAccess=true (dostęp do interfejsu innych programów).")
    if missing:
        result.append(f"Nie znaleziono bibliotek: {', '.join(_cell(n) for n in missing)}.")
    names = {f for item in image.imports for f in item.functions}
    used = sorted(SENSITIVE & names)
    if used:
        result.append(f"Funkcje wrażliwe (informacyjnie): {', '.join(used)}.")
    return result


def section_notes(image, entry, ctx):
    manifest = ctx.manifest()
    lines = [f"⚠ {w}" for w in image.warnings] + notes(image, manifest, None, ctx.missing())
    return ["## Uwagi", ""] + [f"- {n}" for n in lines] + [""] if lines else []


ORDER = {
    "exe": [section_permissions, section_dependencies, section_version, section_header, section_security, section_sections,
            section_imports, section_exports, section_notes],
    "dll": [section_header, section_exports, section_permissions, section_dependencies, section_version, section_security,
            section_sections, section_imports, section_notes],
}


class Context:
    """The reader and the lazily read resources of one render; a failure is kept, never raised."""

    def __init__(self, reader, image, path=None):
        self.reader = reader
        self.image = image
        self.path = path
        self.manifest_error = None
        self.dependencies_error = None
        self._dependencies = None
        self._manifest = None
        self._version = None
        self._done = set()

    def _once(self, name, read):
        if name in self._done:
            return None
        self._done.add(name)
        try:
            return read()
        except Exception as error:  # render reads resources before any section's own try: nothing may escape
            text = str(error) or type(error).__name__
            if name == "manifest":
                self.manifest_error = text
            else:
                self.image.warnings.append(f"zasoby wersji: {text}")
        return None

    def manifest(self):
        value = self._once("manifest", lambda: resources.manifest(self.reader, self.image))
        if value is not None:
            self._manifest = value
        return self._manifest

    def version(self):
        value = self._once("version", lambda: resources.version_info(self.reader, self.image))
        if value is not None:
            self._version = value
        return self._version or {}

    def dependencies(self):
        """The dependency tree (deps.tree), built once; a failure is kept in dependencies_error."""
        if self._dependencies is None:
            self._dependencies = deps.Tree()
            if self.path is not None:
                # the walk reads from what is left of this render's budget, and the main reader pays for it
                budget = deps.Budget(self.reader.budget - self.reader.used)
                try:
                    self._dependencies = deps.tree(self.path, self.image, max_modules=MAX_MODULES, budget=budget)
                except Exception as error:  # nothing may raise out of preview.render
                    self.dependencies_error = str(error) or type(error).__name__
                finally:
                    self.reader.used += budget.spent
        return self._dependencies

    def missing(self):
        """Names of dependencies that were not found and are not delay-loaded (the card counts them)."""
        return [n.resolution.name for n in self.dependencies() if n.resolution.kind == "missing" and not n.delayed]


def _analyze(entry, entropy=True):
    size = entry.get("size")
    if not size:
        found = c4k.stat(entry["path"])
        size = (found or {}).get("size") or 0
    reader = pe.RangeReader(entry["path"], size)
    return reader, pe.parse(reader, entropy=entropy)


@c4k.handler("preview.render")
def render(params):
    entry = params["entry"]
    kind = params.get("preview") or "exe"
    lines = [f"# {entry['name']}", ""]
    try:
        reader, image = _analyze(entry, entropy=False)
    except pe.PeError as error:
        return {"kind": "markdown", "content": "\n".join(lines + [f"⚠ To nie jest prawidłowym plikiem PE ({error})."])}
    except Exception as error:  # nothing may raise out of preview.render
        return {"kind": "markdown", "content": "\n".join(lines + [f"⚠ Nie udało się odczytać pliku: {error}"])}
    ctx = Context(reader, image, entry["path"])
    ctx.manifest()   # resources first: the section entropy reads must not use up the budget before them
    ctx.version()
    ctx.dependencies()   # then the dependency walk, which shares this render's budget
    pe.add_entropy(reader, image)
    for build in ORDER.get(kind, ORDER["exe"]):
        try:
            lines += build(image, entry, ctx)
        except Exception as error:
            lines += [f"⚠ Sekcja {build.__name__.removeprefix('section_')}: nie udało się wyświetlić ({error}).", ""]
    return {"kind": "markdown", "content": "\n".join(lines)}


@c4k.handler("preview.card")
def card(params):
    entry = params["entry"]
    actions = [{"label": "Otwórz", "target": entry["path"]}]
    try:
        _reader, image = _analyze(entry)
    except Exception as error:
        return {"icon": "⚙", "status": "error", "items": [{"kind": "text", "text": f"To nie jest prawidłowy plik PE ({error})"}], "actions": actions}
    items = [{"kind": "value", "label": "Architektura", "value": image.machine},
             {"kind": "value", "label": "Typ", "value": "DLL" if image.is_dll else SUBSYSTEMS.get(image.subsystem, "program")},
             {"kind": "value", "label": "Importy", "value": str(len(image.imports))}]
    if image.exports is not None:
        items.append({"kind": "value", "label": "Eksporty", "value": str(image.exports.total)})
    return {"icon": "⚙", "status": "warning" if image.warnings else "ok", "items": items, "actions": actions}


c4k.serve()
