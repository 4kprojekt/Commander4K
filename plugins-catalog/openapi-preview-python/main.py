"""Official plugin: OpenAPI 3 and Swagger 2 files (JSON or YAML) as endpoints grouped by tag; a dashboard card counts them.

Files are read through the app (fs.read) with c4k.read_text; YAML is parsed by yaml_lite (standard library only).
"""
import json

import c4k
import yaml_lite

MAX_READ = 8 * 1024 * 1024
METHODS = ["get", "post", "put", "patch", "delete", "head", "options", "trace"]


def _load(entry):
    text = c4k.read_text(entry["path"], max_bytes=MAX_READ).lstrip("﻿")  # a UTF-8 BOM from Windows editors
    if entry["name"].lower().endswith(".json"):
        return json.loads(text)
    return yaml_lite.loads(text)


def _endpoints(spec):
    """(tag, METHOD, path, summary) for every operation, in file order."""
    result = []
    for path, item in (spec.get("paths") or {}).items():
        if not isinstance(item, dict):
            continue
        for method in METHODS:
            operation = item.get(method)
            if isinstance(operation, dict):
                tags = operation.get("tags") or ["(bez tagu)"]
                summary = operation.get("summary") or operation.get("operationId") or ""
                result.append((str(tags[0]), method.upper(), str(path), str(summary)))
    return result


def _servers(spec):
    if spec.get("swagger"):
        host = spec.get("host")
        if not host:
            return []
        schemes = spec.get("schemes") or ["https"]
        return [f"{scheme}://{host}{spec.get('basePath') or ''}" for scheme in schemes]
    return [str(server.get("url")) for server in spec.get("servers") or [] if isinstance(server, dict) and server.get("url")]


def _schemas(spec):
    found = (spec.get("components") or {}).get("schemas") if spec.get("openapi") else spec.get("definitions")
    return list(found or {})


def _cell(text):
    return str(text).replace("|", "\\|").replace("\n", " ")


@c4k.handler("preview.render")
def render(params):
    entry = params["entry"]
    try:
        spec = _load(entry)
    except (ValueError, yaml_lite.YamlError) as error:
        return {"kind": "markdown", "content": f"### {entry['name']}\n\n❌ Nie udało się odczytać pliku (JSON/YAML): {error}"}
    if not isinstance(spec, dict) or not (spec.get("openapi") or spec.get("swagger")):
        return {"kind": "markdown", "content": f"### {entry['name']}\n\nTo nie jest specyfikacja OpenAPI (brak klucza `openapi` ani `swagger`)."}

    info = spec.get("info") or {}
    lines = [f"# {info.get('title') or entry['name']}", "",
             f"Wersja **{info.get('version', '?')}** · {'OpenAPI ' + str(spec['openapi']) if spec.get('openapi') else 'Swagger ' + str(spec['swagger'])}", ""]
    if info.get("description"):
        lines += [str(info["description"]).strip().split("\n\n")[0], ""]
    servers = _servers(spec)
    if servers:
        lines += ["**Serwery:** " + ", ".join(f"`{s}`" for s in servers), ""]

    groups = {}
    for tag, method, path, summary in _endpoints(spec):
        groups.setdefault(tag, []).append((method, path, summary))
    for tag, operations in groups.items():
        lines += [f"## {tag}", "", "| Metoda | Ścieżka | Opis |", "|---|---|---|"]
        lines += [f"| {method} | `{_cell(path)}` | {_cell(summary)} |" for method, path, summary in operations]
        lines.append("")

    schemas = _schemas(spec)
    if schemas:
        lines += ["## Schematy", "", ", ".join(f"`{name}`" for name in schemas)]
    return {"kind": "markdown", "content": "\n".join(lines)}


@c4k.handler("preview.card")
def card(params):
    entry = params["entry"]
    try:
        spec = _load(entry)
    except (ValueError, yaml_lite.YamlError) as error:
        return {"icon": "🔌", "status": "error", "subtitle": str(error), "items": [], "actions": [{"label": "Otwórz", "target": entry["path"]}]}

    info = spec.get("info") or {} if isinstance(spec, dict) else {}
    endpoints = _endpoints(spec) if isinstance(spec, dict) else []
    counts = {}
    for _, method, _, _ in endpoints:
        counts[method] = counts.get(method, 0) + 1
    chart = {"kind": "bar", "series": [{"name": method, "points": [{"x": i, "y": n, "label": method}]} for i, (method, n) in enumerate(counts.items())]}
    return {
        "title": info.get("title") or entry["name"],
        "subtitle": f"v{info.get('version', '?')}",
        "icon": "🔌",
        "status": "ok" if endpoints else "warning",
        "items": [
            {"kind": "value", "label": "Endpointy", "value": str(len(endpoints))},
            {"kind": "value", "label": "Schematy", "value": str(len(_schemas(spec)) if isinstance(spec, dict) else 0)},
            {"kind": "chart", "chart": chart},
            {"kind": "text", "text": " · ".join(f"{method} {n}" for method, n in counts.items())},
        ],
        "actions": [{"label": "Otwórz", "target": entry["path"]}],
    }


c4k.serve()
