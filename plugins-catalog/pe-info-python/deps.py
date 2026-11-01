"""Dependency resolution for the pe-info plugin: where Windows would load each imported DLL from (simplified).

Order: API set (api-ms-*, ext-ms-*) -> KnownDLLs (registry) -> the file's own folder -> System32 (SysWOW64 for x86)
-> not found. Each folder check is one c4k.stat, cached per (folder, name) within a render (with the size, so a DLL
that is walked is not stat'ed again). DLLs found next to the file are parsed in turn (headers and imports only, each
with its own RangeReader), up to max_depth levels and max_modules modules (API sets, resolved by name alone, do not
count; at most MAX_API_SETS of them are listed). All those readers draw on one shared Budget: once it is spent the walk
stops and the node says so. System DLLs are not walked. A file inside System32/SysWOW64 has no app-local
neighbours: its imports resolve as system DLLs.

c4k (and pe, which imports it) are imported inside the functions that call the host, so system_folder and resolve's
pure parts can be imported in a plain Python process.
"""
import os
from dataclasses import dataclass

KNOWN_DLLS_KEY = r"reg://HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs"
API_SET_PREFIXES = ("api-ms-", "ext-ms-")
MAX_API_SETS = 200
DEFAULT_BUDGET = 8 * 1024 * 1024
OVER_BUDGET = "przekroczono limit odczytu"

_known = None


@dataclass
class Resolution:
    name: str
    kind: str            # "apiset" | "known" | "local" | "system" | "missing"
    path: str = None
    size: int = None     # from the stat that found it


@dataclass
class Node:
    resolution: Resolution
    depth: int
    children_count: int = 0
    error: str = None
    delayed: bool = False


class Tree(list):
    """Nodes in display order (each followed by its children); `truncated` when max_modules was reached,
    `more_api_sets` = API sets left out after MAX_API_SETS."""
    truncated = False
    more_api_sets = 0


class Budget:
    """Bytes the whole walk may still read (shared by every reader it creates)."""

    def __init__(self, total):
        self.remaining = max(0, total)
        self.spent = 0

    def take(self, count):
        self.remaining = max(0, self.remaining - count)
        self.spent += count


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
    """(path, size) of the file `name` in `folder`, or None; one c4k.stat, cached."""
    key = (folder.lower(), name.lower())
    if cache is not None and key in cache:
        return cache[key]
    path = _join(folder, name)
    try:
        import c4k
        found = c4k.stat(path)
    except Exception:  # an unreadable location counts as "not there"
        found = None
    result = (path, found.get("size") or 0) if found and found.get("kind") != "directory" else None
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
        return Resolution(name, "local", local[0], local[1])
    found = _exists(system, name, cache)
    if found:
        return Resolution(name, "system", found[0], found[1])
    return Resolution(name, "missing")


def _parse_local(resolution, budget):
    """Headers and imports of an app-local DLL: its own reader, limited to what is left of the shared budget."""
    import pe
    reader = pe.RangeReader(resolution.path, resolution.size or 0, budget=budget.remaining)
    try:
        image = pe.parse(reader, details=False, entropy=False)
    finally:
        budget.take(reader.used)
    over = any(OVER_BUDGET in w for w in image.warnings)
    return image, (OVER_BUDGET if over else None)


def tree(path, image, max_depth=3, max_modules=60, known=None, cache=None, budget=None):
    """Dependencies of `image` (the file at `path`) as a flat pre-order list; modules already seen are not repeated
    (a module seen first as delay-loaded and later as a normal import becomes a normal one)."""
    known = known_dlls() if known is None else known
    cache = {} if cache is None else cache
    budget = Budget(DEFAULT_BUDGET) if budget is None else budget
    nodes = Tree()
    seen = {base_name(path).lower()}
    by_name = {}
    counted = [0, 0]   # [modules that cost host calls, API sets listed]

    def walk(imports, folder, machine, depth, delayed_parent):
        added = 0
        for item in imports:
            name = (item.dll or "").strip()
            lower = name.lower()
            delayed = delayed_parent or item.delayed
            if not name or name == "?":
                continue
            if lower in seen:
                existing = by_name.get(lower)
                if existing is not None and existing.delayed and not delayed:
                    existing.delayed = False
                continue
            if lower.startswith(API_SET_PREFIXES):
                seen.add(lower)
                if counted[1] >= MAX_API_SETS:
                    nodes.more_api_sets += 1
                    continue
                counted[1] += 1
            else:
                if counted[0] >= max_modules:
                    nodes.truncated = True
                    continue
                seen.add(lower)
                counted[0] += 1
            node = Node(resolve(name, folder, machine, known, cache), depth, delayed=delayed)
            by_name[lower] = node
            nodes.append(node)
            added += 1
            if node.resolution.kind == "local" and depth + 1 < max_depth:
                if budget.remaining <= 0:
                    node.error = OVER_BUDGET
                    continue
                try:
                    child, node.error = _parse_local(node.resolution, budget)
                except Exception as error:  # a broken neighbour is shown, never raised
                    text = str(error) or type(error).__name__
                    node.error = OVER_BUDGET if OVER_BUDGET in text else text
                    continue
                node.children_count = walk(child.imports, folder_of(node.resolution.path), child.machine, depth + 1, node.delayed)
        return added

    walk(image.imports, folder_of(path), image.machine, 0, False)
    return nodes
