"""Package metadata found inside archives: NuGet .nuspec, wheel METADATA, JAR MANIFEST.MF, VSIX extension.vsixmanifest.

Each reader takes the file's text and returns (rows, dependencies): rows are (label, value) pairs, dependencies are
display strings. XML is parsed only with ET.fromstring, and never when it carries a DTD (UnsafeXml).
"""
import xml.etree.ElementTree as ET


class UnsafeXml(Exception):
    """The XML declares a DTD or entities; it is not parsed (entity expansion)."""


def _xml(text):
    text = text.lstrip(chr(0xFEFF))
    upper = text.upper()
    if "<!DOCTYPE" in upper or "<!ENTITY" in upper:
        raise UnsafeXml("pominięto: DTD w XML")
    return ET.fromstring(text)


def _local(tag):
    return tag.rsplit("}", 1)[-1] if isinstance(tag, str) else ""


def _child(element, name):
    if element is None:
        return None
    for child in element:
        if _local(child.tag) == name:
            return child
    return None


def _children(element, name):
    return [child for child in (element if element is not None else []) if _local(child.tag) == name]


def _text(element, name):
    found = _child(element, name)
    return (found.text or "").strip() if found is not None else ""


def nuspec(xml):
    root = _xml(xml)
    metadata = _child(root, "metadata")
    rows = [(label, _text(metadata, key)) for label, key in
            (("id", "id"), ("wersja", "version"), ("autorzy", "authors"), ("opis", "description"))]
    rows = [(label, value) for label, value in rows if value]
    dependencies = []
    container = _child(metadata, "dependencies")
    groups = _children(container, "group")
    for group in groups:
        framework = group.get("targetFramework") or "dowolny framework"
        items = _children(group, "dependency")
        if not items:
            dependencies.append(f"{framework}: brak zależności")
        for item in items:
            dependencies.append(f"{framework}: {item.get('id', '?')} {item.get('version', '')}".rstrip())
    for item in _children(container, "dependency"):  # the old flat form, no groups
        dependencies.append(f"{item.get('id', '?')} {item.get('version', '')}".rstrip())
    return rows, dependencies


def _headers(text, stop_at_blank=True):
    """'Key: value' lines with continuation lines (leading whitespace); repeated keys keep every value."""
    headers = []
    for line in text.lstrip(chr(0xFEFF)).replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        if not line.strip():
            if stop_at_blank and headers:
                break
            continue
        if line[0] in " \t" and headers:
            key, value = headers[-1]
            headers[-1] = (key, value + line[1:])
            continue
        key, separator, value = line.partition(":")
        if separator:
            headers.append((key.strip(), value.strip()))
    return headers


def wheel_metadata(text):
    headers = _headers(text)
    rows = [(label, value) for key, label in (("Name", "nazwa"), ("Version", "wersja"), ("Summary", "opis"),
                                              ("Requires-Python", "wymagany Python"))
            for name, value in headers if name.lower() == key.lower() and value]
    dependencies = [value for name, value in headers if name.lower() == "requires-dist" and value]
    return rows, dependencies


def jar_manifest(text):
    headers = _headers(text)
    rows = [(label, value) for key, label in (("Main-Class", "klasa startowa"), ("Implementation-Title", "nazwa"),
                                              ("Implementation-Version", "wersja"), ("Implementation-Vendor", "dostawca"),
                                              ("Automatic-Module-Name", "moduł"))
            for name, value in headers if name.lower() == key.lower() and value]
    dependencies = [part for name, value in headers if name.lower() == "class-path" for part in value.split() if part]
    return rows, dependencies


def vsix(xml):
    root = _xml(xml)
    metadata = _child(root, "Metadata")
    identity = _child(metadata, "Identity")
    rows = []
    if identity is not None:
        rows += [(label, identity.get(key)) for key, label in (("Id", "id"), ("Version", "wersja"), ("Publisher", "wydawca"))
                 if identity.get(key)]
    for key, label in (("DisplayName", "nazwa"), ("Description", "opis")):
        if _text(metadata, key):
            rows.append((label, _text(metadata, key)))
    dependencies = []
    for item in _children(_child(root, "Dependencies"), "Dependency"):
        name = item.get("Id") or item.get("DisplayName") or "?"
        dependencies.append(f"{name} {item.get('Version', '')}".rstrip())
    for item in _children(_child(metadata, "Properties"), "Property"):  # VS Code: comma-separated extension ids
        if item.get("Id") in ("Microsoft.VisualStudio.Code.ExtensionDependencies", "Microsoft.VisualStudio.Code.ExtensionPack"):
            dependencies += [part.strip() for part in (item.get("Value") or "").split(",") if part.strip()]
        if item.get("Id") == "Microsoft.VisualStudio.Code.Engine" and item.get("Value"):
            rows.append(("VS Code", item.get("Value")))
    return rows, dependencies
