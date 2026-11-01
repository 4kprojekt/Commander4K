"""Official plugin: EXE/DLL (PE) files described from their headers; nothing is executed.

Everything is read through the app (fs.read) with c4k.read_bytes. One function per Markdown section; ORDER lists the
sections of each preview id. Nothing raises out of preview.render.
"""
import datetime
import unicodedata

import c4k
import clr
import deps
import pe
import resources
import signature

MAX_SHOWN_FUNCTIONS = 400
MAX_SHOWN_IMPORTS = 300    # DLLs listed under "Importy"
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
CARD_LEVELS = {"asInvoker": "uprawnienia użytkownika", "highestAvailable": "najwyższe dostępne",
               "requireAdministrator": "wymaga administratora"}
CARD_BUDGET = 2 * 1024 * 1024   # a dashboard card reads less: no entropy, no tree, direct imports only
NO_MANIFEST = "brak manifestu — uprawnienia użytkownika, wirtualizacja UAC dla starszych programów"
NO_LEVEL = "manifest bez requestedExecutionLevel — uprawnienia użytkownika"
MAX_MODULES = 60
MAX_RESOLVED_REFERENCES = 200   # each one costs a c4k.stat; the rest are listed without a location
ONCE_LABELS = {"version": "zasoby wersji", "clr": ".NET", "references": "odwołania .NET"}
ASSEMBLY_KINDS = {"local": "✅", "package": "✅", "framework": "ℹ️", "gac": "ℹ️", "missing": "❌"}
NO_SIGNATURE = "brak osadzonego podpisu — pliki Windows bywają podpisane katalogiem (.cat), którego ta wtyczka nie sprawdza"
NOT_VERIFIED = "podpis nie został zweryfikowany kryptograficznie — to dane odczytane z pliku, nie potwierdzenie autentyczności"
DEPENDENCY_KINDS = {"apiset": ("ℹ️", "zestaw API systemu"), "known": ("✅", "znana systemowa (KnownDLLs)"),
                    "local": ("✅", "obok pliku"), "missing": ("❌", "nie znaleziono")}


def _size(count):
    text = f"{count} B" if count < 1024 else f"{count / 1024:.1f} KB" if count < 1024 * 1024 else f"{count / 1048576:.1f} MB"
    return text.replace(".", ",")


def _plural(count, one, few, many):
    """Polish plural form: 1 → one, 2–4 (but not 12–14) → few, otherwise many."""
    if count == 1:
        return one
    return few if count % 10 in (2, 3, 4) and count % 100 not in (12, 13, 14) else many


def _mark(flag):
    return "✅" if flag else "❌"


def _cell(text):
    """Text for one Markdown line or table cell: `|` escaped, CR/LF and other control characters become spaces."""
    return "".join(" " if unicodedata.category(ch) in ("Cc", "Zl", "Zp") else ch for ch in str(text)).replace("|", "\\|")


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
    for item in image.imports[:MAX_SHOWN_IMPORTS]:
        suffix = ", opóźniony" if item.delayed else ""
        if item.same_as is not None:
            lines.append(f"- **{_cell(item.dll)}** — ta sama tabela co {_cell(item.same_as)}{suffix}")
            continue
        if item.cut and not item.functions:
            lines.append(f"- **{_cell(item.dll)}** — funkcji nie odczytano (limit importów){suffix}")
            continue
        count = f"{len(item.functions)}+" if item.cut else str(len(item.functions))
        lines.append(f"- **{_cell(item.dll)}** — {count} funkcji{suffix}")
        shown = item.functions[:MAX_SHOWN_FUNCTIONS]
        if shown:
            text = ", ".join("`" + name.replace("`", "'") + "`" for name in shown)
            if len(item.functions) > len(shown):
                text += f" … i jeszcze {len(item.functions) - len(shown)}"
            lines.append(f"  {text}")
    more = len(image.imports) - MAX_SHOWN_IMPORTS
    if more > 0:
        lines.append(f"- … i jeszcze {more} {_plural(more, 'biblioteka', 'biblioteki', 'bibliotek')}")
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
        word = _plural(nodes.more_api_sets, "zestaw", "zestawy", "zestawów")
        lines.append(f"- … i jeszcze {nodes.more_api_sets} {word} API systemu")
    if nodes.truncated:
        lines.append(f"- … pominięto dalsze moduły (limit {MAX_MODULES})")
    return lines + [""]


def section_dotnet(image, entry, ctx):
    info = ctx.clr()
    if info is None:
        return []
    rows = [("środowisko", info.runtime or "—")]
    flags = list(info.flags)
    if "tylko IL" in flags and "wymaga 32 bitów" not in flags and image.machine == "x86":
        flags.append("dowolna architektura (AnyCPU)")   # the PE32 header of an IL-only image says nothing about the CPU
    if flags:
        rows.append(("flagi", ", ".join(flags)))
    if info.assembly:
        name, version, culture = info.assembly
        rows.append(("zestaw", f"{name} {version}" + (f" ({culture})" if culture else "")))
    if info.target_framework:
        rows.append(("platforma docelowa", info.target_framework))
    shared = clr.frameworks(ctx.runtimeconfig())
    if shared:
        rows.append(("framework (runtimeconfig.json)", ", ".join(f"{n} {v}".strip() for n, v in shared)))
    lines = ["## .NET", "", "| Cecha | Wartość |", "|---|---|"] + [f"| {name} | {_cell(value)} |" for name, value in rows] + [""]
    if info.warnings:
        lines += [f"⚠ {_cell(w)}" for w in info.warnings] + [""]
    references = ctx.assembly_references()
    if references:
        lines += [f"Odwołania do zestawów ({len(references) + ctx.more_references}):", ""]
        for name, version, token, kind, detail in references:
            key = f", token {token}" if token else ""
            lines.append(f"- {ASSEMBLY_KINDS.get(kind, 'ℹ️')} {_cell(name)} {version}{key} — {_cell(detail)}")
        if ctx.more_references:
            word = _plural(ctx.more_references, "referencja", "referencje", "referencji")
            lines.append(f"- … i jeszcze {ctx.more_references} {word} (nie sprawdzono)")
        lines.append("")
    return lines


def section_signature(image, entry, ctx):
    sig = ctx.signature()
    lines = ["## Podpis", ""]
    if not sig.present:
        return lines + ["- brak osadzonego podpisu Authenticode", ""]
    if sig.error and not (sig.subject or sig.organization or sig.issuer or sig.serial):
        return lines + [f"⚠ nie udało się odczytać podpisu: {_cell(sig.error)}", ""]
    rows = (("Podpisujący", sig.subject), ("Organizacja", sig.organization), ("Wystawca", sig.issuer),
            ("Numer seryjny", sig.serial), ("Ważny od", sig.not_before), ("Ważny do", sig.not_after),
            ("Czas podpisania", sig.signing_time), ("Znacznik czasu", "tak" if sig.timestamped else "nie"))
    lines += ["| Pole | Wartość |", "|---|---|"] + [f"| {label} | {_cell(value)} |" for label, value in rows if value]
    lines.append("")
    if sig.error:
        lines.append(f"⚠ nie udało się odczytać podpisu: {_cell(sig.error)}")
    return lines + [f"ℹ️ Uwaga: {NOT_VERIFIED}.", ""]


def section_version(image, entry, ctx):
    info = ctx.version()
    labels = (("FileDescription", "Opis"), ("CompanyName", "Firma"), ("ProductName", "Produkt"), ("FileVersion", "Wersja pliku"),
              ("ProductVersion", "Wersja produktu"), ("OriginalFilename", "Oryginalna nazwa"), ("InternalName", "Nazwa wewnętrzna"),
              ("LegalCopyright", "Prawa autorskie"), ("_fixed", "Wersja numeryczna"))
    rows = [f"| {label} | {_cell(info[key])} |" for key, label in labels if info.get(key)]
    return ["## Wersja", "", "| Pole | Wartość |", "|---|---|"] + rows + [""] if rows else []


def notes(image, manifest, signature, missing=()):
    """Facts about the file, never a verdict. `signature` may be None (not read): then it adds nothing."""
    result = []
    if signature is not None and not signature.present:
        result.append(f"Podpis: {NO_SIGNATURE}.")
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
    lines = [f"⚠ {w}" for w in image.warnings] + notes(image, manifest, ctx.signature(), ctx.missing())
    return ["## Uwagi", ""] + [f"- {n}" for n in lines] + [""] if lines else []


ORDER = {
    "exe": [section_permissions, section_signature, section_dependencies, section_version, section_header, section_dotnet,
            section_security, section_sections, section_imports, section_exports, section_notes],
    "dll": [section_header, section_dotnet, section_exports, section_permissions, section_signature, section_dependencies,
            section_version, section_security, section_sections, section_imports, section_notes],
}


class Context:
    """The reader and the lazily read resources of one render; a failure is kept, never raised."""

    def __init__(self, reader, image, path=None, max_depth=3):
        self.reader = reader
        self.max_depth = max_depth
        self.image = image
        self.path = path
        self.manifest_error = None
        self.dependencies_error = None
        self._dependencies = None
        self._manifest = None
        self._version = None
        self._clr = None
        self._signature = None
        self._references = None
        self._runtimeconfig = None
        self.more_references = 0
        self._done = set()

    def _once(self, name, read, failed=None):
        """read() the first time `name` is asked for; an exception never escapes: it becomes failed(text) when given,
        the manifest error, or a warning."""
        if name in self._done:
            return None
        self._done.add(name)
        try:
            return read()
        except Exception as error:  # render reads these before any section's own try: nothing may escape
            text = str(error) or type(error).__name__
            if failed is not None:
                return failed(text)
            if name == "manifest":
                self.manifest_error = text
            else:
                self.image.warnings.append(f"{ONCE_LABELS.get(name, name)}: {text}")
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

    def clr(self):
        """The .NET metadata (clr.clr_info), read once; None for a native image."""
        value = self._once("clr", lambda: clr.clr_info(self.reader, self.image))
        if value is not None:
            self._clr = value
        return self._clr

    def signature(self):
        """The embedded Authenticode signer (signature.signer), read once; a failure is a Signature with an error."""
        value = self._once("signature", lambda: signature.signer(self.reader, self.image),
                           failed=lambda text: signature.Signature(present=True, error=text))
        if value is not None:
            self._signature = value
        return self._signature or signature.Signature()

    def _sibling(self, suffix):
        """(base, path) of `<base><suffix>` next to the file; base is the file name without its extension."""
        name = deps.base_name(self.path)
        base = name.rsplit(".", 1)[0] if "." in name else name
        return base, deps._join(deps.folder_of(self.path), base + suffix)

    def runtimeconfig(self):
        """<base>.runtimeconfig.json next to the file, or None (the normal case for libraries)."""
        if "runtimeconfig" not in self._done:
            self._done.add("runtimeconfig")
            if self.path is not None and self.clr() is not None:
                self._runtimeconfig = clr.read_json(self._sibling(".runtimeconfig.json")[1])
        return self._runtimeconfig

    def assembly_references(self):
        """(name, version, token, kind, detail) of the first MAX_RESOLVED_REFERENCES AssemblyRefs, resolved once (one
        c4k.stat each); more_references counts the rest."""
        value = self._once("references", self._resolve_references)
        if value is not None:
            self._references = value
        return self._references or []

    def _resolve_references(self):
        info = self.clr()
        if info is None or not info.references or self.path is None:
            return []
        base, deps_path = self._sibling(".deps.json")
        manifest = clr.read_json(deps_path)
        folder, cache, result = deps.folder_of(self.path), {}, []
        self.more_references = max(0, len(info.references) - MAX_RESOLVED_REFERENCES)
        for name, version, token in info.references[:MAX_RESOLVED_REFERENCES]:
            try:
                kind, detail = clr.resolve_assembly(name, folder, base, self.runtimeconfig(), manifest,
                                                    info.target_framework, cache)
            except Exception as error:  # nothing may raise out of preview.render
                kind, detail = "missing", f"⚠ {error}"
            result.append((name, version, token, kind, detail))
        return result

    def dependencies(self):
        """The dependency tree (deps.tree), built once; a failure is kept in dependencies_error."""
        if self._dependencies is None:
            self._dependencies = deps.Tree()
            if self.path is not None:
                # the walk reads from what is left of this render's budget, and the main reader pays for it
                budget = deps.Budget(self.reader.budget - self.reader.used)
                try:
                    self._dependencies = deps.tree(self.path, self.image, max_depth=self.max_depth,
                                                    max_modules=MAX_MODULES, budget=budget)
                except Exception as error:  # nothing may raise out of preview.render
                    self.dependencies_error = str(error) or type(error).__name__
                finally:
                    self.reader.used += budget.spent
        return self._dependencies

    def missing(self):
        """Names of dependencies that were not found and are not delay-loaded (the card counts them)."""
        return [n.resolution.name for n in self.dependencies() if n.resolution.kind == "missing" and not n.delayed]


def _analyze(entry, entropy=True, budget=8 * 1024 * 1024, details=True):
    size = entry.get("size")
    if not size:
        found = c4k.stat(entry["path"])
        size = (found or {}).get("size") or 0
    reader = pe.RangeReader(entry["path"], size, budget=budget)
    return reader, pe.parse(reader, details=details, entropy=entropy)


@c4k.handler("preview.render")
def render(params):
    lines = []
    try:
        entry = params["entry"]
        kind = params.get("preview") or "exe"
        lines = [f"# {_cell(entry.get('name') or entry.get('path') or 'plik')}", ""]
        reader, image = _analyze(entry, entropy=False)
    except pe.PeError as error:
        return {"kind": "markdown", "content": "\n".join(lines + [f"⚠ To nie jest prawidłowym plikiem PE ({error})."])}
    except Exception as error:  # nothing may raise out of preview.render
        return {"kind": "markdown", "content": "\n".join(lines + [f"⚠ Nie udało się odczytać pliku: {error}"])}
    ctx = Context(reader, image, entry["path"])
    ctx.manifest()   # resources first: the section entropy reads must not use up the budget before them
    ctx.version()
    ctx.clr()          # then .NET metadata and the signer, before the dependency walk and the entropy
    ctx.signature()
    ctx.assembly_references()
    ctx.dependencies()   # then the dependency walk, which shares this render's budget
    pe.add_entropy(reader, image)
    for build in ORDER.get(kind, ORDER["exe"]):
        try:
            lines += build(image, entry, ctx)
        except Exception as error:
            lines += [f"⚠ Sekcja {build.__name__.removeprefix('section_')}: nie udało się wyświetlić ({error}).", ""]
    return {"kind": "markdown", "content": "\n".join(lines)}


SYSTEM_FOLDERS = ("system32", "syswow64", "sysarm32", "winsxs")
NO_EMBEDDED_SYSTEM = "brak osadzonego (pliki systemu Windows są zwykle podpisane w katalogu — nie sprawdzono)"


def _in_windows(path, root=None):
    """True for a file in %SystemRoot%\\System32, SysWOW64, SysArm32 or WinSxS (any depth) or directly in %SystemRoot%
    (regedit.exe); not for user-writable folders such as Temp, Tasks or tracing."""
    root = (root or deps._system_root()).replace("/", "\\").rstrip("\\").lower()
    path = (path or "").replace("/", "\\").lower()
    if not path.startswith(root + "\\"):
        return False
    rest = path[len(root) + 1:].split("\\")
    return len(rest) == 1 or rest[0] in SYSTEM_FOLDERS


def _card_signature(sig, path):
    """(value, status) of the card's "Podpis" row; status is None for a readable signature. System files of Windows
    (see _in_windows) without an embedded signature are not flagged: Windows usually signs them in catalogs (.cat),
    which this plugin does not check."""
    if not sig.present:
        return (NO_EMBEDDED_SYSTEM, None) if _in_windows(path) else ("brak", "warning")
    who = sig.organization or sig.subject
    return (who, None) if who else ("nie odczytano", "warning")


def _card_level(ctx):
    """(value, status) of the card's "Uprawnienia" row: a warning for requireAdministrator or uiAccess=true."""
    manifest = ctx.manifest()
    usable = manifest is not None and not manifest.skipped_reason
    level = manifest.execution_level if usable else None
    ui_access = bool(usable and manifest.ui_access)
    value = CARD_LEVELS.get(level, "uprawnienia użytkownika") + (", uiAccess" if ui_access else "")
    return value, ("warning" if level == "requireAdministrator" or ui_access else None)


@c4k.handler("preview.card")
def card(params):
    """The dashboard form: architecture, version, signer, execution level and missing DLLs, read with CARD_BUDGET."""
    entry = params.get("entry") or {}
    path = entry.get("path") or ""
    icon = "⚙"
    actions = [{"label": "Pokaż szczegóły", "target": path}] if path else []
    try:
        reader, image = _analyze(entry, entropy=False, budget=CARD_BUDGET, details=False)
    except pe.PeError:
        return {"title": entry.get("name"), "icon": icon, "status": "error",
                "items": [{"kind": "text", "text": "To nie jest prawidłowy plik PE"}], "actions": actions}
    except Exception as error:  # an unreadable file is a card, never a JSON-RPC error
        return {"title": entry.get("name"), "icon": icon, "status": "error",
                "items": [{"kind": "text", "text": f"Nie udało się odczytać pliku: {error}"}], "actions": actions}
    ctx = Context(reader, image, path, max_depth=1)
    try:
        ctx.manifest()
        info = ctx.version()
        sig = ctx.signature()
        missing = ctx.missing()   # direct imports only (max_depth=1): one c4k.stat each, nothing walked
        signer, signer_status = _card_signature(sig, path)
        level, level_status = _card_level(ctx)
        items = [{"kind": "value", "label": "Architektura", "value": f"{image.machine} ({'PE32+' if image.pe32_plus else 'PE32'})"},
                 {"kind": "value", "label": "Wersja", "value": info.get("_fixed") or "—"},
                 {"kind": "value", "label": "Podpis", "value": signer, **({"status": signer_status} if signer_status else {})},
                 {"kind": "value", "label": "Uprawnienia", "value": level, **({"status": level_status} if level_status else {})}]
        if missing:
            items.append({"kind": "value", "label": "Brakujące DLL", "value": str(len(missing)), "status": "error"})
            items.append({"kind": "text", "text": ", ".join(missing[:5]) + (" …" if len(missing) > 5 else "")})
    except Exception as error:  # nothing may raise out of preview.card
        return {"title": entry.get("name"), "icon": icon, "status": "error",
                "items": [{"kind": "text", "text": f"Nie udało się odczytać pliku: {error}"}], "actions": actions}
    return {"title": info.get("FileDescription") or entry.get("name"), "subtitle": info.get("CompanyName"), "icon": icon,
            "status": "warning" if signer_status or level_status or missing else "ok", "items": items, "actions": actions}


c4k.serve()
