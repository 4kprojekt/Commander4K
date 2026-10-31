"""Example plugin: a dedicated quick view (Ctrl+Q) for project manifests and API descriptions.

Shows what a project is made of — dependencies of npm, .NET, Python, Go, Rust and PHP projects — and the modules
of an HTTP API described by OpenAPI/Swagger (endpoints grouped by tag). Everything goes through the SDK: the file is
read with c4k.read_text (permission fs.read), parsing uses only Python's standard library on that text.
"""
import json
import re
import tomllib
import xml.etree.ElementTree as ElementTree

import c4k

METHODS = ("get", "post", "put", "patch", "delete", "head", "options")


def table(headers, rows):
    def cell(value):
        return str(value).replace("|", "\\|").replace("\n", " ")
    lines = ["| " + " | ".join(headers) + " |", "|" + "---|" * len(headers)]
    lines += ["| " + " | ".join(cell(v) for v in row) + " |" for row in rows]
    return "\n".join(lines)


def section(title, rows, headers=("Moduł", "Wersja")):
    return f"## {title} ({len(rows)})\n\n" + (table(headers, rows) if rows else "_brak_") + "\n"


def npm(text, name):
    data = json.loads(text)
    parts = [f"# 📦 {data.get('name', name)} {data.get('version', '')}".rstrip(), data.get("description", "")]
    for key, title in (("dependencies", "Zależności"), ("devDependencies", "Zależności deweloperskie"), ("peerDependencies", "Wymagane przez użytkownika")):
        if key in data:
            parts.append(section(title, sorted(data[key].items())))
    if "scripts" in data:
        parts.append(section("Skrypty npm", sorted(data["scripts"].items()), ("Skrypt", "Polecenie")))
    return parts


def dotnet(text, name):
    root = ElementTree.fromstring(text)
    def all_of(tag):
        return [e for e in root.iter() if e.tag.split("}")[-1] == tag]
    frameworks = " · ".join(e.text or "" for e in all_of("TargetFramework") + all_of("TargetFrameworks"))
    packages = [(e.get("Include") or e.get("Update"), e.get("Version") or "(centralnie)") for e in all_of("PackageReference") + all_of("PackageVersion")]
    projects = [(e.get("Include"), "") for e in all_of("ProjectReference")]
    parts = [f"# 🟪 {name}", f"Platforma: **{frameworks}**" if frameworks else ""]
    parts.append(section("Pakiety NuGet", sorted(p for p in packages if p[0])))
    if projects:
        parts.append(section("Projekty", projects, ("Projekt", "")))
    return parts


def python_project(text, name):
    if name.lower().endswith(".txt"):
        rows = []
        for line in text.splitlines():
            line = line.split("#", 1)[0].strip()
            if line and not line.startswith("-"):
                match = re.match(r"([A-Za-z0-9_.\-\[\]]+)\s*(.*)", line)
                rows.append((match.group(1), match.group(2) or "dowolna") if match else (line, ""))
        return [f"# 🐍 {name}", section("Pakiety", rows)]
    data = tomllib.loads(text)
    project = data.get("project", {})
    parts = [f"# 🐍 {project.get('name', name)} {project.get('version', '')}".rstrip(), project.get("description", "")]
    parts.append(section("Zależności", [split_requirement(r) for r in project.get("dependencies", [])]))
    for group, requirements in project.get("optional-dependencies", {}).items():
        parts.append(section(f"Opcjonalne: {group}", [split_requirement(r) for r in requirements]))
    poetry = data.get("tool", {}).get("poetry", {}).get("dependencies", {})
    if poetry:
        parts.append(section("Poetry", [(k, v if isinstance(v, str) else json.dumps(v)) for k, v in poetry.items()]))
    return parts


def split_requirement(requirement):
    match = re.match(r"([A-Za-z0-9_.\-\[\]]+)\s*(.*)", requirement)
    return (match.group(1), match.group(2) or "dowolna") if match else (requirement, "")


def go_mod(text, name):
    module = re.search(r"^module\s+(\S+)", text, re.M)
    requirements = re.findall(r"^\s*([\w.\-/]+\.[\w.\-/]+)\s+(v[\w.\-+]+)", text, re.M)
    return [f"# 🐹 {module.group(1) if module else name}", section("Moduły Go", requirements)]


def cargo(text, name):
    data = tomllib.loads(text)
    package = data.get("package", {})
    def rows(table_):
        return [(k, v if isinstance(v, str) else v.get("version", json.dumps(v))) for k, v in table_.items()]
    parts = [f"# 🦀 {package.get('name', name)} {package.get('version', '')}".rstrip()]
    parts.append(section("Zależności", rows(data.get("dependencies", {}))))
    if data.get("dev-dependencies"):
        parts.append(section("Zależności deweloperskie", rows(data["dev-dependencies"])))
    return parts


def composer(text, name):
    data = json.loads(text)
    parts = [f"# 🐘 {data.get('name', name)}", data.get("description", "")]
    parts.append(section("Wymagania", sorted(data.get("require", {}).items())))
    if data.get("require-dev"):
        parts.append(section("Wymagania deweloperskie", sorted(data["require-dev"].items())))
    return parts


def openapi(text, name):
    spec = json.loads(text)
    info = spec.get("info", {})
    version = spec.get("openapi") or spec.get("swagger") or "?"
    modules = {}
    for path, operations in spec.get("paths", {}).items():
        for method, operation in operations.items():
            if method in METHODS and isinstance(operation, dict):
                tag = (operation.get("tags") or [path.strip("/").split("/")[0] or "/"])[0]
                summary = operation.get("summary") or operation.get("operationId") or ""
                modules.setdefault(tag, []).append((method.upper(), path, summary))
    servers = ", ".join(s.get("url", "") for s in spec.get("servers", [])) or spec.get("host", "")
    parts = [f"# 🔌 {info.get('title', name)} {info.get('version', '')}".rstrip(),
             f"OpenAPI/Swagger {version}" + (f" · serwer: {servers}" if servers else ""),
             info.get("description", ""),
             f"**{len(modules)} modułów, {sum(len(v) for v in modules.values())} operacji**"]
    for tag in sorted(modules):
        parts.append(section(f"Moduł `{tag}`", sorted(modules[tag], key=lambda r: (r[1], r[0])), ("Metoda", "Ścieżka", "Opis")))
    schemas = spec.get("components", {}).get("schemas") or spec.get("definitions") or {}
    if schemas:
        parts.append(section("Modele danych", [(k, len(v.get("properties", {}))) for k, v in sorted(schemas.items())], ("Model", "Pola")))
    return parts


def render(name, text):
    lower = name.lower()
    if lower == "package.json":
        return npm(text, name)
    if lower == "composer.json":
        return composer(text, name)
    if lower.endswith((".csproj", ".fsproj", ".vbproj", ".props")):
        return dotnet(text, name)
    if lower == "pyproject.toml" or (lower.startswith("requirements") and lower.endswith(".txt")):
        return python_project(text, name)
    if lower == "go.mod":
        return go_mod(text, name)
    if lower == "cargo.toml":
        return cargo(text, name)
    return openapi(text, name)


@c4k.handler("preview.render")
def preview(params):
    entry = params["entry"]
    text = c4k.read_text(entry["path"], 4 * 1024 * 1024)
    try:
        parts = render(entry["name"], text)
    except (ValueError, KeyError, TypeError, AttributeError, ElementTree.ParseError, tomllib.TOMLDecodeError) as error:
        return {"kind": "text", "content": f"Nie udało się odczytać struktury ({type(error).__name__}: {error}).\n\n{text}"}
    return {"kind": "markdown", "content": "\n\n".join(p for p in parts if p)}


c4k.serve()
