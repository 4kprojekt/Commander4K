"""Example plugin: extension statistics of the folder shown in the active panel (read-only)."""
import os
from collections import defaultdict

import c4k


def human(size):
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size:.0f} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024


def groups(context):
    files = [e for e in c4k.list_dir(context["activePath"]) if e["kind"] == "file"]
    by_ext = defaultdict(list)
    for entry in files:
        by_ext[os.path.splitext(entry["name"])[1].lower() or "(bez)"].append(entry)
    return sorted(by_ext.items(), key=lambda item: -sum(max(f["size"], 0) for f in item[1]))


@c4k.handler("command.execute")
def execute(params):
    context = params["context"]
    if params["command"] == "try-write":
        try:
            c4k.write_text(os.path.join(context["activePath"], "c4k-test.txt"), "test")
            return {"message": "Zapis się udał (nie powinien!)"}
        except c4k.HostError as error:
            return {"message": f"Zablokowano zgodnie z manifestem: {error}"}

    lines = [f"Folder: {context['activePath']}", ""]
    for ext, files in groups(context):
        total = sum(max(f["size"], 0) for f in files)
        lines.append(f"{ext:<12} {len(files):>6} plików  {human(total):>10}")
    return {"text": {"title": "Podsumowanie folderu", "content": "\n".join(lines)}}


@c4k.handler("panel.load")
def load(params):
    data = groups(params["context"])
    rows = [
        {
            "id": ext,
            "cells": [ext, str(len(files)), human(sum(max(f["size"], 0) for f in files))],
            "sortKeys": [None, len(files), sum(max(f["size"], 0) for f in files)],
            "severity": "highlight" if index == 0 else "normal",
        }
        for index, (ext, files) in enumerate(data)
    ]
    return {
        "columns": [
            {"id": "ext", "header": "Rozszerzenie", "width": 200},
            {"id": "count", "header": "Pliki", "align": "right", "width": 90},
            {"id": "size", "header": "Rozmiar", "align": "right", "width": 120},
        ],
        "rows": rows,
        "status": f"{len(rows)} rozszerzeń w {params['context']['activePath']}",
    }


@c4k.handler("panel.details")
def details(params):
    context = c4k.context()
    for ext, files in groups(context):
        if ext == params["rowId"]:
            biggest = sorted(files, key=lambda f: -f["size"])[:10]
            return {"sections": [{"title": f"Największe pliki {ext}", "items": [[f["name"], human(f["size"])] for f in biggest]}]}
    return None


@c4k.handler("panel.action")
def action(params):
    context = params["context"]
    names = [f["name"] for ext, files in groups(context) if ext in params["rowIds"] for f in files]
    return {"text": "\n".join(sorted(names))}


@c4k.handler("preview.render")
def preview(params):
    """Quick view (Ctrl+Q) of .csv files as a Markdown table; the app renders the Markdown."""
    import csv
    import io

    text = c4k.read_text(params["entry"]["path"], max_bytes=512 * 1024)
    sample = text[:4096]
    try:
        dialect = csv.Sniffer().sniff(sample, delimiters=",;\t|")
    except csv.Error:
        dialect = csv.excel
    rows = list(csv.reader(io.StringIO(text), dialect))[:200]
    if not rows:
        return {"kind": "text", "content": "(pusty plik)"}

    def cell(value):
        return value.replace("|", "\\|").replace("\n", " ")

    width = max(len(r) for r in rows)
    header = rows[0] + [""] * (width - len(rows[0]))
    lines = [f"### {params['entry']['name']}", "",
             "| " + " | ".join(cell(h) for h in header) + " |",
             "|" + "---|" * width]
    for row in rows[1:]:
        row = row + [""] * (width - len(row))
        lines.append("| " + " | ".join(cell(v) for v in row) + " |")
    lines.append("")
    lines.append(f"*{len(rows) - 1} wierszy, {width} kolumn (separator `{dialect.delimiter}`)*")
    return {"kind": "markdown", "content": "\n".join(lines), "editable": False}


c4k.serve()
