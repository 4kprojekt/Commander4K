"""Dependency resolution for the pe-info plugin: where Windows would load each imported DLL from (simplified).

Order: API set (api-ms-*, ext-ms-*) -> KnownDLLs (registry) -> the file's own folder -> System32 (SysWOW64 for x86)
-> not found. Each folder check is one c4k.stat, cached per (folder, name) within a render. DLLs found next to the file
are parsed in turn (headers and imports only, each with its own RangeReader and budget), up to max_depth levels and
max_modules modules (API sets, resolved by name alone, do not count); system DLLs are not walked. A file inside
System32/SysWOW64 has no app-local neighbours: its imports resolve as system DLLs.

c4k (and pe, which imports it) are imported inside the functions that call the host, so system_folder and resolve's
pure parts can be imported in a plain Python process.
"""
import os
from dataclasses import dataclass

KNOWN_DLLS_KEY = r"reg://HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs"
API_SET_PREFIXES = ("api-ms-", "ext-ms-")

_known = None


@dataclass
class Resolution:
    name: str
    kind: str            # "apiset" | "known" | "local" | "system" | "missing"
    path: str = None


@dataclass
class Node:
    resolution: Resolution
    depth: int
    children_count: int = 0
    error: str = None
    delayed: bool = False


class Tree(list):
    """Nodes in display order (each followed by its children); `truncated` when MAX_MODULES was reached."""
    truncated = False


def system_folder(machine, system_root):
    """The folder a DLL of this machine type is loaded from: SysWOW64 for x86 images, System32 otherwise."""
    return _join(system_root.rstrip("\\/"), "SysWOW64" if machine == "x86" else "System32")


def _system_root():
    return os.environ.get("SystemRoot", r"C:\Windows")


def _join(folder, name):
    separator = "\\" if "\\" in folder and "://" not in folder else "/"
    return folder if not name else (folder.rstrip("\\/") + separator + name)


def folder_of(path):
    cut = max(path.rfind("\\"), path.rfind("/"))
    return path[:cut] if cut > 0 else path


def base_name(path):
    return path[max(path.rfind("\\"), path.rfind("/")) + 1:]


def known_dlls():
    """Lower-cased file names of the KnownDLLs (read once per process); an empty set when the registry cannot be read."""
    global _known
    if _known is None:
        try:
            import c4k
            names = set()
            for entry in c4k.list_dir(KNOWN_DLLS_KEY) or []:
                name = str(entry.get("name") or "").strip().lower()
                if name:
                    names.add(name if name.endswith(".dll") else name + ".dll")
            _known = names
        except Exception:  # any failure (no permission, no reg:// provider) means "no KnownDLLs"
            _known = set()
    return _known


def _exists(folder, name, cache):
    key = (folder.lower(), name.lower())
    if cache is not None and key in cache:
        return cache[key]
    path = _join(folder, name)
    try:
        import c4k
        found = c4k.stat(path)
    except Exception:  # an unreadable location counts as "not there"
        found = None
    result = path if found and found.get("kind") != "directory" else None
    if cache is not None:
        cache[key] = result
    return result


def resolve(name, folder, machine, known, cache=None):
    lower = name.lower()
    if lower.startswith(API_SET_PREFIXES):
        return Resolution(name, "apiset")
    root = _system_root()
    system = system_folder(machine, root)
    if lower in known:
        return Resolution(name, "known", _join(system, name))
    # a file inside System32/SysWOW64 has system neighbours, not app-local ones (and they are never walked)
    in_system = folder.rstrip("\\/").lower() in {system_folder(m, root).lower() for m in ("x86", "x64")}
    local = None if in_system else _exists(folder, name, cache)
    if local:
        return Resolution(name, "local", local)
    found = _exists(system, name, cache)
    if found:
        return Resolution(name, "system", found)
    return Resolution(name, "missing")


def _parse_local(path):
    """Headers and imports of an app-local DLL, with its own reader and budget."""
    import c4k
    import pe
    found = c4k.stat(path) or {}
    reader = pe.RangeReader(path, found.get("size") or 0)
    return pe.parse(reader, details=False, entropy=False)


def tree(path, image, max_depth=3, max_modules=60, known=None, cache=None):
    """Dependencies of `image` (the file at `path`) as a flat pre-order list; modules already seen are not repeated."""
    known = known_dlls() if known is None else known
    cache = {} if cache is None else cache
    nodes = Tree()
    seen = {base_name(path).lower()}
    counted = [0]   # modules that cost host calls; API sets are resolved by name and do not count

    def walk(imports, folder, machine, depth, delayed_parent):
        added = 0
        for item in imports:
            name = (item.dll or "").strip()
            if not name or name == "?" or name.lower() in seen:
                continue
            api_set = name.lower().startswith(API_SET_PREFIXES)
            if not api_set and counted[0] >= max_modules:
                nodes.truncated = True
                continue
            seen.add(name.lower())
            counted[0] += 0 if api_set else 1
            node = Node(resolve(name, folder, machine, known, cache), depth, delayed=delayed_parent or item.delayed)
            nodes.append(node)
            added += 1
            if node.resolution.kind == "local" and depth + 1 < max_depth:
                try:
                    child = _parse_local(node.resolution.path)
                except Exception as error:  # a broken neighbour is shown, never raised
                    node.error = str(error) or type(error).__name__
                    continue
                node.children_count = walk(child.imports, folder_of(node.resolution.path), child.machine, depth + 1, node.delayed)
        return added

    walk(image.imports, folder_of(path), image.machine, 0, False)
    return nodes
