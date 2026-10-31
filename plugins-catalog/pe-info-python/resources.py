"""PE resources for the pe-info plugin: version info (RT_VERSION) and the application manifest (RT_MANIFEST).

Both are read through RangeReader. The resource tree is walked at most three levels deep (type, name, language) and
at most MAX_ENTRIES entries per directory. Manifest XML goes through ET.fromstring only, after a DTD guard.
"""
import struct
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

import pe

RT_VERSION, RT_MANIFEST = 16, 24
DIR_RESOURCE = 2
MAX_ENTRIES = 512
MAX_RESOURCE = 1024 * 1024

VERSION_KEYS = ("CompanyName", "FileDescription", "FileVersion", "ProductName", "ProductVersion", "OriginalFilename",
                "InternalName", "LegalCopyright")

SUPPORTED_OS = {
    "8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a": "Windows 10/11",
    "1f676c76-80e1-4239-95bb-83d0f6d0da78": "Windows 8.1",
    "4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38": "Windows 8",
    "35138b9a-5d96-4fbd-8e2d-a2440225f93a": "Windows 7",
    "e2011457-1546-43c5-a5fe-008deee3d3f0": "Windows Vista",
}


@dataclass
class Manifest:
    xml: str = ""
    execution_level: str = None
    ui_access: bool = False
    auto_elevate: bool = False
    dpi: str = None
    long_path_aware: bool = False
    supported_os: list = field(default_factory=list)
    dependencies: list = field(default_factory=list)
    skipped_reason: str = None
    error: str = None          # the XML could not be parsed


def _directory(reader, base, rel):
    raw = reader.read(base + rel, 16)
    named, ids = struct.unpack_from("<HH", raw, 12)
    count = min(named + ids, MAX_ENTRIES)
    data = reader.read_clamped(base + rel + 16, 8 * count)
    entries = []
    for i in range(len(data) // 8):
        key, target = struct.unpack_from("<II", data, 8 * i)
        entries.append((None if key & 0x80000000 else key, target & 0x7FFFFFFF, bool(target & 0x80000000)))
    return entries


def find_resource(reader, image, type_id):
    """Bytes of the first resource of this type (first name, first language), or None when there is none."""
    rva, size = image.data_dirs[DIR_RESOURCE]
    if rva == 0 or size == 0:
        return None
    base = pe.rva_to_offset(image, rva)
    if base is None:
        raise pe.PeError("katalog zasobów poza obrazem — plik uszkodzony")
    rel = 0
    for level in range(3):
        entries = _directory(reader, base, rel)
        if level == 0:
            entries = [e for e in entries if e[0] == type_id]
        if not entries:
            return None
        _key, target, is_dir = entries[0]
        rel = target
        if not is_dir:
            break
    else:
        return None
    entry = reader.read(base + rel, 16)
    data_rva, data_size = struct.unpack_from("<II", entry, 0)
    if data_size == 0:
        return None
    if data_size > MAX_RESOURCE:
        raise pe.ReadLimit(f"zasób ma {data_size} B — pominięto")
    at = pe.rva_to_offset(image, data_rva)
    if at is None:
        raise pe.PeError("dane zasobu poza obrazem — plik uszkodzony")
    return reader.read(at, data_size)


def _align(value):
    return (value + 3) & ~3


def _block(data, pos, end):
    """One VS_VERSIONINFO-style block at `pos`: (key, value bytes, value_length, is_text, children start, block end)."""
    if pos + 6 > end:
        return None
    length, value_length, kind = struct.unpack_from("<HHH", data, pos)
    block_end = min(pos + length, end)
    if length < 6 or block_end <= pos:
        return None
    key_end = pos + 6
    while key_end + 1 < block_end and data[key_end:key_end + 2] != b"\0\0":
        key_end += 2
    key = data[pos + 6:key_end].decode("utf-16-le", "replace")
    value_at = _align(key_end + 2)
    size = value_length * 2 if kind == 1 else value_length
    value = data[value_at:min(value_at + size, block_end)]
    return key, value, kind, _align(value_at + size), block_end


def _children(data, start, end, limit=64):
    pos = start
    for _ in range(limit):
        block = _block(data, pos, end)
        if block is None:
            return
        yield block
        pos = _align(block[4])
        if pos >= end:
            return


def version_info(reader, image):
    """StringFileInfo values of the first language, plus "_fixed" (x.y.z.w from VS_FIXEDFILEINFO); {} without RT_VERSION."""
    data = find_resource(reader, image, RT_VERSION)
    if not data:
        return {}
    root = _block(data, 0, len(data))
    if root is None or root[0] != "VS_VERSION_INFO":
        raise pe.PeError("zasób wersji jest uszkodzony")
    result = {}
    fixed = root[1]
    if len(fixed) >= 16:
        signature, _struct_version, ms, ls = struct.unpack_from("<IIII", fixed, 0)
        if signature == 0xFEEF04BD:
            result["_fixed"] = f"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}"
    for key, _value, _kind, children_at, end in _children(data, root[3], root[4]):
        if key != "StringFileInfo":
            continue
        for _table_key, _v, _k, strings_at, table_end in _children(data, children_at, end, 4):
            for name, value, kind, _c, _e in _children(data, strings_at, table_end, 256):
                if name in VERSION_KEYS and kind == 1:
                    result[name] = value.decode("utf-16-le", "replace").rstrip("\0").strip()
            break   # first language only
        break
    return result


def _local(tag):
    return tag.rsplit("}", 1)[-1]


def _truthy(text):
    return (text or "").strip().lower() == "true"


def parse_manifest(text):
    """Manifest fields from XML text. DTDs/entities are never parsed. Resource padding (NULs, whitespace) is stripped."""
    text = text.rstrip("\0 \r\n\t")
    manifest = Manifest(xml=text)
    if "<!doctype" in text.lower() or "<!entity" in text.lower():
        manifest.xml = ""
        manifest.skipped_reason = "pominięto: DTD w XML"
        return manifest
    try:
        root = ET.fromstring(text)
    except ET.ParseError as error:
        manifest.error = f"XML jest uszkodzony ({error})"
        return manifest
    dpi_aware = None
    for node in root.iter():
        tag = _local(node.tag)
        value = (node.text or "").strip()
        if tag == "requestedExecutionLevel":
            manifest.execution_level = node.get("level")
            manifest.ui_access = _truthy(node.get("uiAccess"))
        elif tag == "autoElevate":
            manifest.auto_elevate = _truthy(value)
        elif tag == "dpiAwareness" and value:
            manifest.dpi = value
        elif tag == "dpiAware" and value:
            dpi_aware = value
        elif tag == "longPathAware":
            manifest.long_path_aware = _truthy(value)
        elif tag == "supportedOS":
            guid = (node.get("Id") or "").strip("{} ").lower()
            if guid:
                manifest.supported_os.append(SUPPORTED_OS.get(guid, guid))
        elif tag == "dependentAssembly":
            for child in node:
                if _local(child.tag) == "assemblyIdentity" and child.get("name"):
                    manifest.dependencies.append(child.get("name"))
    if manifest.dpi is None:
        manifest.dpi = dpi_aware
    return manifest


def manifest(reader, image):
    data = find_resource(reader, image, RT_MANIFEST)
    if not data:
        return None
    text = data.decode("utf-16", "replace") if data[:2] in (b"\xff\xfe", b"\xfe\xff") else data.decode("utf-8-sig", "replace")
    return parse_manifest(text)
