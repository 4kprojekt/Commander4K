"""PE (EXE/DLL) reader for the pe-info plugin.

Everything is read through RangeReader (fs.read via c4k.read_bytes): 64 KB pages, a per-render budget, and a
loop guard on every table walk. A damaged file never raises out of parse(): each step that fails becomes a
warning and the later steps still run. Only a file without a usable DOS/PE header raises PeError.
"""
import math
import re
import struct
from collections import Counter
from dataclasses import dataclass, field

import c4k

PAGE = 65536
MAX_THUNKS = 4096          # loop guard for import thunk / name walks
MAX_DESCRIPTORS = 1024     # import descriptors
MAX_IMPORT_NAMES = 20000   # function names kept across all import and delay-import tables of one image
MAX_EXPORTS = 2000         # exports kept (the rest are only counted)
MAX_TABLE = 65536          # export table entries read
ENTROPY_BYTES = 256 * 1024

# Drive paths and UNC shares (\\server\share\...) are served as seekable FileStreams; \\?\ and \\.\ device paths and
# scheme:// paths (sftp, ftp, zip/7z/tar, reg) are not.
_LOCAL = re.compile(r"^(?:[A-Za-z]:\\|\\\\[^\\?.][^\\]*\\[^\\]+)")


class ReadLimit(Exception):
    """A read past the end of the file or over the per-render budget."""


class ReadFailed(ReadLimit):
    """The host could not read the file (not a property of the file itself)."""


class PeError(Exception):
    """The file is not a usable PE image."""


class RangeReader:
    """Page-cached random access over c4k.read_bytes with a budget.

    The budget counts the bytes fetched; for paths that are not local drives or UNC shares (SFTP, FTP, archives, device
    paths) the host may stream through the file up to the offset, so a fetch costs offset + length there.
    """

    def __init__(self, path, size, budget=8 * 1024 * 1024, fetch=None):
        self.path = path
        self.size = size
        self.budget = budget
        self.used = 0
        self.local = bool(_LOCAL.match(path or ""))
        self._fetch = fetch or c4k.read_bytes
        self._pages = {}

    def _page(self, index):
        page = self._pages.get(index)
        if page is not None:
            return page
        start = index * PAGE
        count = min(PAGE, self.size - start)
        if count <= 0:
            raise ReadLimit("odczyt poza końcem pliku")
        cost = count if self.local else start + count
        if self.used + cost > self.budget:
            raise ReadLimit("przekroczono limit odczytu")
        try:
            data = self._fetch(self.path, start, count)
        except c4k.HostError as error:
            raise ReadFailed(f"odczyt nie powiódł się: {error}") from None
        self.used += cost
        if len(data) < count:
            raise ReadLimit("nieoczekiwany koniec pliku")
        self._pages[index] = data
        return data

    def read(self, offset, length):
        if offset < 0 or length < 0 or offset + length > self.size:
            raise ReadLimit("odczyt poza końcem pliku")
        if length == 0:
            return b""
        first, last = offset // PAGE, (offset + length - 1) // PAGE
        if first == last:
            page = self._page(first)
            start = offset - first * PAGE
            return page[start:start + length]
        chunks = []
        for index in range(first, last + 1):
            page = self._page(index)
            start = max(offset, index * PAGE) - index * PAGE
            end = min(offset + length, (index + 1) * PAGE) - index * PAGE
            chunks.append(page[start:end])
        return b"".join(chunks)

    def read_clamped(self, offset, length):
        """Like read(), but a request running past the end of the file returns what exists (empty past the end)."""
        if offset < 0 or offset >= self.size:
            return b""
        return self.read(offset, min(length, self.size - offset))

    def cstring(self, offset, limit=260):
        data = self.read_clamped(offset, limit)
        if not data:
            raise ReadLimit("odczyt poza końcem pliku")
        end = data.find(b"\0")
        return (data if end < 0 else data[:end]).decode("latin-1")


@dataclass
class Section:
    name: str
    rva: int
    vsize: int
    raw_offset: int
    raw_size: int
    flags: int
    entropy: float = None


@dataclass
class Import:
    dll: str
    functions: list = field(default_factory=list)
    delayed: bool = False
    same_as: str = None    # the DLL whose name table this descriptor shares (its names are not repeated)
    cut: bool = False      # not walked: MAX_IMPORT_NAMES was reached before this table


@dataclass
class Exports:
    dll_name: str
    items: list = field(default_factory=list)   # (ordinal, name | None, forwarder | None)
    total: int = 0


@dataclass
class PeImage:
    machine: str
    is_dll: bool
    pe32_plus: bool
    subsystem: int
    timestamp: int
    linker: str
    os_version: str
    image_base: int
    entry_rva: int
    characteristics: int
    dll_characteristics: int
    headers_size: int = 0
    sections: list = field(default_factory=list)
    data_dirs: list = field(default_factory=list)
    imports: list = field(default_factory=list)
    exports: Exports = None
    tls_callbacks: int = 0
    overlay: tuple = None
    warnings: list = field(default_factory=list)
    file_size: int = 0
    import_names: int = 0                                  # names kept so far, against MAX_IMPORT_NAMES
    import_tables: dict = field(default_factory=dict)      # name-table RVA → DLL of the first descriptor using it


_MACHINES = {0x14C: "x86", 0x8664: "x64", 0xAA64: "ARM64", 0xA641: "ARM64EC", 0xA64E: "ARM64X", 0x1C0: "ARM", 0x1C4: "ARM", 0x200: "IA-64"}

DIR_EXPORT, DIR_IMPORT, DIR_SECURITY, DIR_TLS, DIR_DELAY, DIR_CLR = 0, 1, 4, 9, 13, 14

_RECOVERABLE = (ReadLimit, PeError, struct.error, ValueError, IndexError, OverflowError, UnicodeError)


def rva_to_offset(image, rva):
    """File offset of an RVA, or None when it points outside every section's raw data."""
    if rva < 0:
        return None
    for section in image.sections:
        extent = max(section.vsize, section.raw_size)
        if section.rva <= rva < section.rva + extent:
            delta = rva - section.rva
            return section.raw_offset + delta if delta < section.raw_size else None
    first = min((s.rva for s in image.sections), default=0)
    if rva < (first or image.headers_size) and rva < image.file_size:
        return rva   # headers
    return None


def _need(image, rva, label):
    offset = rva_to_offset(image, rva)
    if offset is None:
        raise PeError(f"{label}: RVA 0x{rva:X} poza obrazem — plik uszkodzony")
    return offset


def _section_name(raw):
    text = raw.rstrip(b"\0").decode("latin-1")
    return "".join(ch if ch.isprintable() else "?" for ch in text)


def parse(reader, details=True, entropy=True, names=True):
    """The image's headers, sections and imports; `details` adds exports, TLS and the overlay, `entropy` the section
    entropy (callers that read resources first pass False and call add_entropy afterwards, so it cannot use up the budget),
    `names` the imported function names (the dependency walk needs only the DLLs)."""
    size = reader.size
    try:
        dos = reader.read(0, min(64, size))
    except ReadFailed:
        raise
    except ReadLimit as error:
        raise PeError(f"nie można odczytać nagłówka: {error}") from None
    if len(dos) < 64 or dos[:2] != b"MZ":
        raise PeError("brak sygnatury MZ")
    (e_lfanew,) = struct.unpack_from("<I", dos, 0x3C)
    if e_lfanew < 64 or e_lfanew + 24 > size:
        raise PeError(f"wskaźnik na nagłówek PE (0x{e_lfanew:X}) leży poza plikiem")
    try:
        head = reader.read(e_lfanew, 24)
        if head[:4] != b"PE\0\0":
            raise PeError("brak sygnatury PE")
        machine, nsections, timestamp, _symptr, _nsyms, optsize, characteristics = struct.unpack_from("<HHIIIHH", head, 4)
        if optsize < 70:
            raise PeError("nagłówek opcjonalny jest za krótki")
        opt = reader.read_clamped(e_lfanew + 24, optsize)
        if len(opt) < 70:
            raise PeError("nagłówek opcjonalny jest ucięty")
    except ReadFailed:
        raise
    except ReadLimit as error:
        raise PeError(f"nagłówek PE jest ucięty: {error}") from None
    except struct.error:
        raise PeError("nagłówek PE jest uszkodzony") from None

    (magic,) = struct.unpack_from("<H", opt, 0)
    if magic not in (0x10B, 0x20B):
        raise PeError(f"nieznany typ nagłówka opcjonalnego (0x{magic:X})")
    plus = magic == 0x20B
    dirs_at = 112 if plus else 96
    if len(opt) < dirs_at - 4 + 4:
        raise PeError("nagłówek opcjonalny jest ucięty")
    warnings = []
    linker = f"{opt[2]}.{opt[3]}"
    (entry_rva,) = struct.unpack_from("<I", opt, 16)
    image_base = struct.unpack_from("<Q", opt, 24)[0] if plus else struct.unpack_from("<I", opt, 28)[0]
    os_major, os_minor = struct.unpack_from("<HH", opt, 40)
    (headers_size,) = struct.unpack_from("<I", opt, 60)
    subsystem, dll_chars = struct.unpack_from("<HH", opt, 68)
    count_at = dirs_at - 4
    (declared,) = struct.unpack_from("<I", opt, count_at) if len(opt) >= count_at + 4 else (0,)
    fits = max(0, (len(opt) - dirs_at) // 8)
    count = min(declared, 16, fits)
    if declared > 16 or declared > fits:
        warnings.append("katalogi danych: liczba wpisów w nagłówku jest niespójna")
    dirs = [struct.unpack_from("<II", opt, dirs_at + 8 * i) for i in range(count)]
    dirs += [(0, 0)] * (16 - len(dirs))

    image = PeImage(
        machine=_MACHINES.get(machine, f"0x{machine:04X}"),
        is_dll=bool(characteristics & 0x2000),
        pe32_plus=plus,
        subsystem=subsystem,
        timestamp=timestamp,
        linker=linker,
        os_version=f"{os_major}.{os_minor}",
        image_base=image_base,
        entry_rva=entry_rva,
        characteristics=characteristics,
        dll_characteristics=dll_chars,
        headers_size=headers_size,
        data_dirs=dirs,
        warnings=warnings,
        file_size=size,
    )

    def step(label, function):
        try:
            function()
        except _RECOVERABLE as error:
            text = str(error)
            image.warnings.append(text if text.startswith(label) else f"{label}: {text}")

    step("tabela sekcji", lambda: _sections(reader, image, e_lfanew + 24 + optsize, nsections))
    step("tabela importów", lambda: _imports(reader, image, delayed=False, names=names))
    step("tabela importów opóźnionych", lambda: _imports(reader, image, delayed=True, names=names))
    if details:
        step("tabela eksportów", lambda: _exports(reader, image))
        step("katalog TLS", lambda: _tls(reader, image))
        step("overlay", lambda: _overlay(reader, image))
    if entropy:
        add_entropy(reader, image)
    return image


def add_entropy(reader, image):
    """Section entropy (first ENTROPY_BYTES of each section); a failure becomes a warning."""
    try:
        _entropy(reader, image)
    except _RECOVERABLE as error:
        image.warnings.append(f"entropia sekcji: {error}")


def _sections(reader, image, table_at, count):
    if count > 96:
        image.warnings.append(f"tabela sekcji: nagłówek deklaruje {count} sekcji, odczytano 96")
        count = 96
    raw = reader.read_clamped(table_at, 40 * count)
    if len(raw) < 40 * count:
        image.warnings.append("tabela sekcji: ucięta — plik uszkodzony")
    for i in range(len(raw) // 40):
        chunk = raw[40 * i:40 * i + 40]
        vsize, rva, raw_size, raw_ptr = struct.unpack_from("<IIII", chunk, 8)
        (flags,) = struct.unpack_from("<I", chunk, 36)
        image.sections.append(Section(_section_name(chunk[:8]), rva, vsize, raw_ptr, raw_size, flags))


def _import_cap(image, label):
    """True (and one warning per image) once MAX_IMPORT_NAMES names have been kept."""
    if image.import_names < MAX_IMPORT_NAMES:
        return False
    if image.import_names == MAX_IMPORT_NAMES:
        image.import_names += 1   # past the cap: warned
        image.warnings.append(f"{label}: ponad {MAX_IMPORT_NAMES} nazw funkcji we wszystkich tabelach — lista importów przerwana")
    return True


def _thunk_names(reader, image, rva, functions, label):
    """Walk an import name table (at most MAX_THUNKS entries, and while the image keeps fewer than MAX_IMPORT_NAMES
    names), appending names / #ordinals to `functions`."""
    offset = _need(image, rva, label)
    width = 8 if image.pe32_plus else 4
    ordinal_flag = 1 << (width * 8 - 1)
    steps = 0
    bad_names = 0
    while True:
        data = reader.read_clamped(offset + steps * width, 256 * width)
        if len(data) < width:
            image.warnings.append(f"{label}: lista funkcji ucięta")
            break
        done = False
        for i in range(len(data) // width):
            (value,) = struct.unpack_from("<Q" if width == 8 else "<I", data, i * width)
            if value == 0:
                done = True
                break
            steps += 1
            if _import_cap(image, label):
                return
            if steps > MAX_THUNKS:
                image.warnings.append(f"{label}: więcej niż {MAX_THUNKS} pozycji — przerwano (możliwa pętla)")
                return
            image.import_names += 1
            if value & ordinal_flag:
                functions.append(f"#{value & 0xFFFF}")
                continue
            name_at = rva_to_offset(image, value & 0x7FFFFFFF)
            if name_at is None:
                bad_names += 1
                functions.append("?")
                continue
            functions.append(_clean(reader.cstring(name_at + 2, 256)))
        if done:
            break
    if bad_names:
        image.warnings.append(f"{label}: {bad_names} nazw funkcji wskazuje poza obraz")


def _clean(text):
    return "".join(ch if ch.isprintable() else "?" for ch in text)


def _imports(reader, image, delayed, names=True):
    label = "tabela importów opóźnionych" if delayed else "tabela importów"
    rva, size = image.data_dirs[DIR_DELAY if delayed else DIR_IMPORT]
    if rva == 0 or size == 0:
        return
    offset = _need(image, rva, label)
    width = 32 if delayed else 20
    index = 0
    while True:
        if index >= MAX_DESCRIPTORS:
            image.warnings.append(f"{label}: ponad {MAX_DESCRIPTORS} bibliotek — przerwano")
            return
        raw = reader.read_clamped(offset + index * width, width)
        if len(raw) < width:
            image.warnings.append(f"{label}: ucięta — plik uszkodzony")
            return
        index += 1
        if not any(raw):
            return
        if delayed:
            attrs, name_rva, _module, _iat, int_rva = struct.unpack_from("<IIIII", raw, 0)
            if not attrs & 1:   # old format: virtual addresses
                name_rva = (name_rva - image.image_base) & 0xFFFFFFFF if name_rva else 0
                int_rva = (int_rva - image.image_base) & 0xFFFFFFFF if int_rva else 0
            thunk_rva = int_rva
        else:
            oft, _stamp, _chain, name_rva, ft = struct.unpack_from("<IIIII", raw, 0)
            thunk_rva = oft or ft
        entry = Import("?", [], delayed)
        image.imports.append(entry)
        name_at = rva_to_offset(image, name_rva)
        if name_at is None:
            image.warnings.append(f"{label}: nazwa biblioteki #{index} poza obrazem — plik uszkodzony")
            continue
        entry.dll = _clean(reader.cstring(name_at, 260))
        if thunk_rva == 0 or not names:
            continue
        if thunk_rva in image.import_tables:
            entry.same_as = image.import_tables[thunk_rva]
            continue
        image.import_tables[thunk_rva] = entry.dll
        if image.import_names >= MAX_IMPORT_NAMES:
            entry.cut = True
            _import_cap(image, label)
            continue
        try:
            _thunk_names(reader, image, thunk_rva, entry.functions, f"{label} ({entry.dll})")
            entry.cut = image.import_names > MAX_IMPORT_NAMES   # the cap was reached inside this table
        except ReadLimit:
            raise
        except _RECOVERABLE as error:
            image.warnings.append(str(error))


def _exports(reader, image):
    rva, size = image.data_dirs[DIR_EXPORT]
    if rva == 0 or size == 0:
        return
    label = "tabela eksportów"
    offset = _need(image, rva, label)
    raw = reader.read(offset, 40)
    _chars, _stamp, _major, _minor, name_rva, base, n_funcs, n_names, funcs_rva, names_rva, ords_rva = struct.unpack_from("<IIHHIIIIIII", raw, 0)
    name_at = rva_to_offset(image, name_rva) if name_rva else None
    dll_name = _clean(reader.cstring(name_at, 260)) if name_rva and name_at is not None else ""
    exports = Exports(dll_name, [], n_funcs)
    image.exports = exports
    if n_funcs > MAX_TABLE or n_names > MAX_TABLE:
        image.warnings.append(f"{label}: nagłówek deklaruje {n_funcs} funkcji i {n_names} nazw — odczytano pierwsze {MAX_TABLE}")
    n_funcs, n_names = min(n_funcs, MAX_TABLE), min(n_names, MAX_TABLE)
    funcs = reader.read_clamped(_need(image, funcs_rva, label), 4 * n_funcs) if n_funcs else b""
    names = reader.read_clamped(_need(image, names_rva, label), 4 * n_names) if n_names else b""
    ords = reader.read_clamped(_need(image, ords_rva, label), 2 * n_names) if n_names else b""
    n_funcs, n_names = len(funcs) // 4, min(len(names) // 4, len(ords) // 2)

    def target(index):
        (func_rva,) = struct.unpack_from("<I", funcs, 4 * index)
        if rva <= func_rva < rva + size:
            at = rva_to_offset(image, func_rva)
            return _clean(reader.cstring(at, 260)) if at is not None else "?"
        return None

    named = set()
    for i in range(n_names):
        if len(exports.items) >= MAX_EXPORTS:
            break
        (name_ptr,) = struct.unpack_from("<I", names, 4 * i)
        (ordinal_index,) = struct.unpack_from("<H", ords, 2 * i)
        if ordinal_index >= n_funcs:
            continue
        named.add(ordinal_index)
        at = rva_to_offset(image, name_ptr)
        name = _clean(reader.cstring(at, 512)) if at is not None else "?"
        exports.items.append((base + ordinal_index, name, target(ordinal_index)))
    for i in range(n_funcs):
        if len(exports.items) >= MAX_EXPORTS:
            break
        if i in named or struct.unpack_from("<I", funcs, 4 * i)[0] == 0:
            continue
        exports.items.append((base + i, None, target(i)))


def _tls(reader, image):
    rva, size = image.data_dirs[DIR_TLS]
    if rva == 0 or size == 0:
        return
    offset = _need(image, rva, "katalog TLS")
    width = 8 if image.pe32_plus else 4
    raw = reader.read(offset, 4 * width)
    (callbacks_va,) = struct.unpack_from("<Q" if width == 8 else "<I", raw, 3 * width)
    if callbacks_va == 0:
        return
    at = rva_to_offset(image, callbacks_va - image.image_base)
    if at is None:
        raise PeError(f"katalog TLS: tablica wywołań zwrotnych poza obrazem — plik uszkodzony")
    data = reader.read_clamped(at, 64 * width)
    for i in range(len(data) // width):
        if struct.unpack_from("<Q" if width == 8 else "<I", data, i * width)[0] == 0:
            break
        image.tls_callbacks += 1


def _sniff(data):
    if data[:4] == b"PK\x03\x04":
        return "archiwum ZIP"
    if data[:6] == b"7z\xbc\xaf\x27\x1c":
        return "archiwum 7-Zip"
    if b"Nullsoft" in data:
        return "instalator Nullsoft (NSIS)"
    if b"Inno Setup" in data:
        return "instalator Inno Setup"
    return None


def _overlay(reader, image):
    """Data after the last section, minus the Authenticode certificate table (data directory 4 is a FILE OFFSET)."""
    end = max((s.raw_offset + s.raw_size for s in image.sections), default=0)
    if not end or end >= image.file_size:
        return
    cert_at, cert_size = image.data_dirs[DIR_SECURITY]
    start, stop = end, image.file_size
    if cert_at and cert_size and cert_at < stop and cert_at + cert_size > start:
        # the certificate table normally ends the file; keep the pieces of the tail it does not cover
        before = max(0, min(cert_at, stop) - start)
        after = max(0, stop - max(cert_at + cert_size, start))
        if before:
            stop = start + before
        elif after:
            start = cert_at + cert_size
        else:
            return
    if start >= stop:
        return
    try:
        kind = _sniff(reader.read_clamped(start, 16))
    except ReadLimit:
        kind = None
    image.overlay = (start, stop - start, kind)


def _entropy(reader, image):
    for section in image.sections:
        if section.raw_size == 0 or section.raw_offset >= image.file_size:
            continue
        data = reader.read_clamped(section.raw_offset, min(section.raw_size, ENTROPY_BYTES))
        if not data:
            continue
        total = len(data)
        section.entropy = -sum(c / total * math.log2(c / total) for c in Counter(data).values())
