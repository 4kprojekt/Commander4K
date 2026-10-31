"""Example plugin: passing files between panels (panel.receive / panel.provide).

The panel lists saved HTTP endpoints (kept in the plugin's storage). Files marked in the other panel and copied here
(F5) are POSTed to the chosen endpoint as multipart forms. The app reads the files itself, only during that call, so
this plugin needs just the `net` permission. In the other direction, F5 on an endpoint saves the response of a GET
request as a file in the other panel. Everything goes through the SDK.
"""
import c4k


def endpoints():
    return c4k.storage_get("endpoints", []) or []


def host_name(url):
    authority = url.split("://", 1)[-1].split("/", 1)[0].split("?", 1)[0]
    return authority.replace(":", "_") or "odpowiedz"


@c4k.handler("panel.load")
def load(params):
    urls = endpoints()
    return {
        "columns": [{"id": "url", "header": "Adres", "width": 520}],
        "rows": [{"id": url, "cells": [url]} for url in urls],
        "status": f"{len(urls)} adresów",
    }


@c4k.handler("panel.action")
def action(params):
    urls = endpoints()
    if params["action"] == "add":
        url = c4k.prompt("Dodaj endpoint", "Adres http(s), na który wysyłać pliki:", "http://")
        if url and url.startswith(("http://", "https://")) and url not in urls:
            c4k.storage_set("endpoints", urls + [url])
    elif params["action"] == "remove":
        gone = set(params["rowIds"])
        c4k.storage_set("endpoints", [u for u in urls if u not in gone])
    return {}


@c4k.handler("panel.receivers")
def receivers(params):
    # One receiver per endpoint; the app filters by count and kind itself.
    return [
        {"id": url, "title": f"Wyślij na {url}", "description": "POST multipart, pole „file”", "multiple": True, "returnsMessage": True}
        for url in endpoints()
    ]


@c4k.handler("panel.receive")
def receive(params):
    url = params["receiver"]
    files = params["files"]
    sent = 0
    lines = []
    results = []
    done_bytes = 0
    for index, item in enumerate(files):
        if item["kind"] != "file":
            results.append({"path": item["path"], "ok": False, "error": "Foldery nie są obsługiwane"})
            continue
        try:
            response = c4k.http_request(url, "POST", multipart={"field": "file", "path": item["path"]})
            ok = 200 <= response["status"] < 300
            sent += 1 if ok else 0
            lines.append(f"{item['name']}: HTTP {response['status']}")
            results.append({"path": item["path"], "ok": ok, "error": None if ok else f"HTTP {response['status']}"})
        except c4k.HostError as error:
            lines.append(f"{item['name']}: {error}")
            results.append({"path": item["path"], "ok": False, "error": str(error)})
        done_bytes += max(item["size"], 0)
        c4k.report_progress(params["jobId"], index + 1, len(files), bytes=done_bytes, text=item["name"])
    return {"text": f"Wgrano {sent}/{len(files)} plików\n" + "\n".join(lines), "results": results}


@c4k.handler("panel.providers")
def providers(params):
    return [{"id": "save-response", "title": "Zapisz odpowiedź GET jako plik", "description": "Pobiera adres i zapisuje treść odpowiedzi."}]


@c4k.handler("panel.provide")
def provide(params):
    files = []
    rows = params["rowIds"]
    for index, url in enumerate(rows):
        try:
            response = c4k.http_request(url, "GET")
            files.append({"name": f"{host_name(url)}.json", "text": response["body"]})
        except c4k.HostError as error:
            files.append({"name": f"{host_name(url)}.json", "text": f"{error}"})
        c4k.report_progress(params["jobId"], index + 1, len(rows))
    return {"files": files}


c4k.serve()
