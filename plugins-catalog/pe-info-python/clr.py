""".NET metadata for the pe-info plugin (ECMA-335): the CLR header, the metadata streams, the Assembly and AssemblyRef
tables and the target framework.

Everything goes through the render's RangeReader, so it shares the read budget. Only the pages that hold the stream
headers, the two tables, the strings that are named and the scanned part of #Blob are read: the row sizes of every
table before AssemblyRef are computed from the ECMA-335 column lists, so the rows in between are skipped, not read.
A damaged header becomes a warning; clr_info returns None only for a file without CLR data.
"""
import hashlib
import json
import struct
from dataclasses import dataclass, field

import pe

METADATA_SIGNATURE = 0x424A5342
MAX_STREAMS = 16
MAX_REFERENCES = 1024
BLOB_SCAN = 1024 * 1024         # at most this much of #Blob is searched for the target framework
FRAMEWORK_PREFIXES = (b".NETCoreApp,Version=", b".NETFramework,Version=", b".NETStandard,Version=")
SCAN_CHUNK = 65536
SCAN_OVERLAP = max(len(p) for p in FRAMEWORK_PREFIXES) + 32   # a hit starting in a chunk is read with its version
MAX_FRAMEWORK_TEXT = 256

# coded index kinds (ECMA-335 II.24.2.6): the tables each one can point to, in tag order (None = unused tag)
CODED = {
    "TypeDefOrRef": (0x02, 0x01, 0x1B),
    "HasConstant": (0x04, 0x08, 0x17),
    "HasCustomAttribute": (0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20,
                           0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B),
    "HasFieldMarshal": (0x04, 0x08),
    "HasDeclSecurity": (0x02, 0x06, 0x20),
    "MemberRefParent": (0x02, 0x01, 0x1A, 0x06, 0x1B),
    "HasSemantics": (0x14, 0x17),
    "MethodDefOrRef": (0x06, 0x0A),
    "MemberForwarded": (0x04, 0x06),
    "CustomAttributeType": (None, None, 0x06, 0x0A, None),
    "ResolutionScope": (0x00, 0x1A, 0x23, 0x01),
}

# columns of tables 0x00..0x23: 2/4 = fixed width, "s"/"g"/"b" = heap index, an int in a tuple = simple table
# index, a str = coded index
COLUMNS = {
    0x00: (2, "s", "g", "g", "g"),                                  # Module
    0x01: ("ResolutionScope", "s", "s"),                            # TypeRef
    0x02: (4, "s", "s", "TypeDefOrRef", (0x04,), (0x06,)),          # TypeDef
    0x03: ((0x04,),),                                               # FieldPtr
    0x04: (2, "s", "b"),                                            # Field
    0x05: ((0x06,),),                                               # MethodPtr
    0x06: (4, 2, 2, "s", "b", (0x08,)),                             # MethodDef
    0x07: ((0x08,),),                                               # ParamPtr
    0x08: (2, 2, "s"),                                              # Param
    0x09: ((0x02,), "TypeDefOrRef"),                                # InterfaceImpl
    0x0A: ("MemberRefParent", "s", "b"),                            # MemberRef
    0x0B: (2, "HasConstant", "b"),                                  # Constant (type + padding)
    0x0C: ("HasCustomAttribute", "CustomAttributeType", "b"),       # CustomAttribute
    0x0D: ("HasFieldMarshal", "b"),                                 # FieldMarshal
    0x0E: (2, "HasDeclSecurity", "b"),                              # DeclSecurity
    0x0F: (2, 4, (0x02,)),                                          # ClassLayout
    0x10: (4, (0x04,)),                                             # FieldLayout
    0x11: ("b",),                                                   # StandAloneSig
    0x12: ((0x02,), (0x14,)),                                       # EventMap
    0x13: ((0x14,),),                                               # EventPtr
    0x14: (2, "s", "TypeDefOrRef"),                                 # Event
    0x15: ((0x02,), (0x17,)),                                       # PropertyMap
    0x16: ((0x17,),),                                               # PropertyPtr
    0x17: (2, "s", "b"),                                            # Property
    0x18: (2, (0x06,), "HasSemantics"),                             # MethodSemantics
    0x19: ((0x02,), "MethodDefOrRef", "MethodDefOrRef"),            # MethodImpl
    0x1A: ("s",),                                                   # ModuleRef
    0x1B: ("b",),                                                   # TypeSpec
    0x1C: (2, "MemberForwarded", "s", (0x1A,)),                     # ImplMap
    0x1D: (4, (0x04,)),                                             # FieldRVA
    0x1E: (4, 4),                                                   # EncLog
    0x1F: (4,),                                                     # EncMap
    0x20: (4, 2, 2, 2, 2, 4, "b", "s", "s"),                        # Assembly
    0x21: (4,),                                                     # AssemblyProcessor
    0x22: (4, 4, 4),                                                # AssemblyOS
    0x23: (2, 2, 2, 2, 4, "b", "s", "s", "b"),                      # AssemblyRef
}
ASSEMBLY, ASSEMBLY_REF = 0x20, 0x23

FLAGS = ((0x1, "tylko IL"), (0x2, "wymaga 32 bitów"), (0x20000, "preferuje 32 bity"), (0x8, "podpisany silną nazwą"),
         (0x10, "natywny punkt wejścia"))


@dataclass
class Clr:
    runtime: str = ""
    flags: list = field(default_factory=list)
    assembly: tuple = None            # (name, version, culture)
    references: list = field(default_factory=list)   # (name, version, token_hex | None)
    target_framework: str = None
    warnings: list = field(default_factory=list)


class MetadataError(Exception):
    """The metadata is damaged: the message says where."""


def clr_info(reader, image):
    """The .NET metadata of the image, or None when it has no CLR data directory. Never raises for a damaged file:
    what cannot be read becomes a warning in Clr.warnings."""
    rva, size = image.data_dirs[pe.DIR_CLR] if len(image.data_dirs) > pe.DIR_CLR else (0, 0)
    if rva == 0 or size == 0:
        return None
    info = Clr()
    try:
        _read(reader, image, rva, info)
    except pe.ReadLimit as error:
        info.warnings.append(f".NET: {error}")
    except (MetadataError, pe.PeError, struct.error, ValueError, IndexError, OverflowError, UnicodeError) as error:
        info.warnings.append(f".NET: {error or type(error).__name__} — metadane uszkodzone")
    return info


def _offset(image, rva, label):
    offset = pe.rva_to_offset(image, rva)
    if offset is None:
        raise MetadataError(f"{label}: RVA 0x{rva:X} poza obrazem")
    return offset


def _read(reader, image, rva, info):
    header = reader.read(_offset(image, rva, "nagłówek CLR"), 24)
    _cb, _major, _minor, meta_rva, meta_size, flags = struct.unpack_from("<IHHIII", header, 0)
    info.flags = [text for bit, text in FLAGS if flags & bit]
    if meta_rva == 0 or meta_size < 16:
        raise MetadataError("brak metadanych")
    root_at = _offset(image, meta_rva, "metadane")
    root = reader.read_clamped(root_at, min(meta_size, 4096))
    if len(root) < 16:
        raise MetadataError("nagłówek metadanych jest ucięty")
    signature, _maj, _min, _reserved, length = struct.unpack_from("<IHHII", root, 0)
    if signature != METADATA_SIGNATURE:
        raise MetadataError("brak sygnatury BSJB")
    if length > 255 or 16 + length + 4 > len(root):
        raise MetadataError("nagłówek metadanych jest niespójny")
    info.runtime = root[16:16 + length].split(b"\0", 1)[0].decode("utf-8", "replace")
    at = 16 + ((length + 3) & ~3)
    _flags, count = struct.unpack_from("<HH", root, at)
    at += 4
    streams = {}
    for _ in range(min(count, MAX_STREAMS)):
        offset, stream_size = struct.unpack_from("<II", root, at)
        end = root.find(b"\0", at + 8, at + 8 + 32)
        if end < 0:
            raise MetadataError("nagłówek strumienia jest uszkodzony")
        name = root[at + 8:end].decode("ascii", "replace")
        at = at + 8 + ((end - (at + 8) + 1 + 3) & ~3)
        if offset + stream_size > meta_size:
            raise MetadataError(f"strumień {name} wychodzi poza metadane")
        streams[name] = (root_at + offset, stream_size)
    tables = streams.get("#~") or streams.get("#-")
    if tables is None:
        raise MetadataError("brak strumienia tabel")
    strings = streams.get("#Strings", (0, 0))
    blob = streams.get("#Blob", (0, 0))
    _tables(reader, info, tables, strings, blob, wide="#JTD" in streams)
    info.target_framework = _target_framework(reader, blob)


def _tables(reader, info, tables, strings, blob, wide):
    start, size = tables
    head = reader.read(start, 24)
    heap_sizes = head[6]
    valid, _sorted = struct.unpack_from("<QQ", head, 8)
    present = [i for i in range(64) if valid >> i & 1]
    counts = reader.read(start + 24, 4 * len(present))
    rows = [0] * 64
    for i, table in enumerate(present):
        (rows[table],) = struct.unpack_from("<I", counts, 4 * i)
    at = start + 24 + 4 * len(present)
    if heap_sizes & 0x40:   # #- with extra data: four bytes after the row counts
        at += 4
    unknown = [t for t in present if t < ASSEMBLY_REF and t not in COLUMNS]
    if unknown:
        info.warnings.append(f".NET: nieznana tabela metadanych 0x{unknown[0]:02X} — pominięto odwołania")
        return

    heap = {"s": 4 if wide or heap_sizes & 1 else 2, "g": 4 if wide or heap_sizes & 2 else 2,
            "b": 4 if wide or heap_sizes & 4 else 2}

    def width(column):
        if isinstance(column, int):
            return column
        if isinstance(column, tuple):
            return 4 if wide or rows[column[0]] >= 1 << 16 else 2
        if column in heap:
            return heap[column]
        targets = CODED[column]
        bits = max(1, (len(targets) - 1).bit_length())
        largest = max(rows[t] for t in targets if t is not None)
        return 4 if wide or largest >= 1 << (16 - bits) else 2

    offsets = {}
    for table in range(ASSEMBLY_REF + 1):
        offsets[table] = at
        at += rows[table] * sum(width(c) for c in COLUMNS[table])
    if at > start + size:
        raise MetadataError("tabele wychodzą poza strumień")

    def row(table, index):
        widths = [width(c) for c in COLUMNS[table]]
        raw = reader.read(offsets[table] + index * sum(widths), sum(widths))
        values, pos = [], 0
        for w in widths:
            values.append(int.from_bytes(raw[pos:pos + w], "little"))
            pos += w
        return values

    if rows[ASSEMBLY]:
        _hash, major, minor, build, revision, _flags, _key, name, culture = row(ASSEMBLY, 0)
        info.assembly = (_string(reader, strings, name), f"{major}.{minor}.{build}.{revision}", _string(reader, strings, culture))
    for index in range(min(rows[ASSEMBLY_REF], MAX_REFERENCES)):
        major, minor, build, revision, flags, key, name, _culture, _hash = row(ASSEMBLY_REF, index)
        info.references.append((_string(reader, strings, name), f"{major}.{minor}.{build}.{revision}",
                                _token(_blob(reader, blob, key), flags & 1)))
    if rows[ASSEMBLY_REF] > MAX_REFERENCES:
        info.warnings.append(f".NET: odczytano {MAX_REFERENCES} z {rows[ASSEMBLY_REF]} odwołań")


def _string(reader, heap, index):
    start, size = heap
    if index == 0:
        return ""
    if index >= size:
        raise MetadataError("indeks #Strings poza strumieniem")
    data = reader.read_clamped(start + index, min(1024, size - index))
    end = data.find(b"\0")
    return "".join(ch if ch.isprintable() else "?" for ch in (data if end < 0 else data[:end]).decode("utf-8", "replace"))


def _blob(reader, heap, index):
    start, size = heap
    if index == 0 or index >= size:
        return b""
    head = reader.read_clamped(start + index, 4)
    if not head:
        return b""
    if head[0] & 0x80 == 0:
        length, skip = head[0], 1
    elif head[0] & 0xC0 == 0x80 and len(head) >= 2:
        length, skip = (head[0] & 0x3F) << 8 | head[1], 2
    elif len(head) >= 4:
        length, skip = (head[0] & 0x1F) << 24 | head[1] << 16 | head[2] << 8 | head[3], 4
    else:
        raise MetadataError("blob jest uszkodzony")
    if index + skip + length > size:
        raise MetadataError("blob wychodzi poza strumień")
    return reader.read(start + index + skip, min(length, 4096))


def _token(data, full_key):
    """The public key token as hex: the 8-byte token itself, or the last 8 bytes of SHA-1 of a full key, reversed."""
    if not data:
        return None
    if full_key or len(data) > 8:
        return hashlib.sha1(data).digest()[-8:][::-1].hex()
    return data.hex()


def _target_framework(reader, blob):
    start, size = blob
    if not size:
        return None
    limit = min(size, BLOB_SCAN)
    position = 0
    try:
        while position < limit:
            data = reader.read_clamped(start + position, min(SCAN_CHUNK + SCAN_OVERLAP, limit - position))
            hits = [found for found in (data.find(prefix) for prefix in FRAMEWORK_PREFIXES) if found >= 0]
            if hits:
                found = min(hits)   # the earliest hit, whichever prefix it has
                text = reader.read_clamped(start + position + found, min(MAX_FRAMEWORK_TEXT, size - position - found))
                end = 0
                while end < len(text) and 0x20 <= text[end] < 0x7F:
                    end += 1
                return text[:end].decode("ascii")
            position += SCAN_CHUNK
    except pe.ReadLimit:
        return None
    return None


def read_json(path):
    """A JSON file next to the image (runtimeconfig.json / deps.json), or None: a missing file is the normal case."""
    try:
        import c4k
        found = c4k.stat(path)
        if not found or found.get("kind") == "directory":
            return None
        value = json.loads(c4k.read_text(path, max_bytes=1048576))
        return value if isinstance(value, dict) else None
    except Exception:  # unreadable or not JSON: treated as absent
        return None


def frameworks(runtimeconfig):
    """(name, version) of the shared frameworks a runtimeconfig.json asks for."""
    options = (runtimeconfig or {}).get("runtimeOptions") or {}
    found = []
    for item in [options.get("framework")] + list(options.get("frameworks") or []):
        if isinstance(item, dict) and item.get("name"):
            found.append((str(item["name"]), str(item.get("version") or "")))
    return found


def _package(ref_name, deps):
    """'<package>/<version>' of a deps.json library that carries `ref_name`.dll, or None."""
    wanted = ref_name.lower() + ".dll"
    for target in ((deps or {}).get("targets") or {}).values():
        if not isinstance(target, dict):
            continue
        for library, entry in target.items():
            runtime = (entry or {}).get("runtime") if isinstance(entry, dict) else None
            if isinstance(runtime, dict) and any(str(k).replace("\\", "/").rsplit("/", 1)[-1].lower() == wanted for k in runtime):
                return library
    for library in ((deps or {}).get("libraries") or {}):
        if str(library).split("/", 1)[0].lower() == ref_name.lower():
            return library
    return None


def _is_framework_name(name):
    lower = name.lower()
    return lower in ("mscorlib", "netstandard") or lower == "system" or lower.startswith(("system.", "microsoft.", "windows"))


def resolve_assembly(ref_name, folder, base, runtimeconfig, deps, target_framework=None, cache=None):
    """`base` is the file name without its extension (runtimeconfig/deps were read as <base>.runtimeconfig.json and
    <base>.deps.json by the caller). (kind, detail) of where `ref_name` is loaded from: "local" (next to the file), "package" (deps.json),
    "framework" (a shared framework from runtimeconfig.json, or the runtime that loads a .NET Core library),
    "gac" (.NET Framework) or "missing"."""
    import deps as native
    if native._exists(folder, ref_name + ".dll", cache):
        return "local", "obok pliku"
    package = _package(ref_name, deps)
    if package:
        return "package", f"pakiet {package.replace('/', ' ')}"
    shared = frameworks(runtimeconfig)
    if shared:
        return "framework", "framework " + ", ".join(f"{n} {v}".strip() for n, v in shared)
    target = target_framework or ""
    if _is_framework_name(ref_name):
        if target.startswith((".NETCoreApp", ".NETStandard")):
            return "framework", "framework .NET (dostarcza go program, który ładuje bibliotekę)"
        return "gac", "GAC / .NET Framework"
    return "missing", "nie znaleziono"
