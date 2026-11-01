"""Read-only RouterOS REST preview for MikroTik service entries."""
import base64
import ipaddress
import json
import re

import c4k

MAX_ROWS = 50


def _host_from_entry(entry):
    path = entry.get("path", "")
    if not isinstance(path, str) or not path.startswith("service://"):
        return ""
    authority = path[len("service://"):]
    if authority.startswith("["):
        end = authority.find("]")
        if end < 0 or authority[end + 1:end + 2] != ":":
            return ""
        host, service_port = authority[1:end], authority[end + 2:]
    else:
        host, separator, service_port = authority.rpartition(":")
        if not separator:
            return ""
    if not service_port.isdigit() or not 1 <= int(service_port) <= 65535:
        return ""
    return host if _valid_host(host) else ""


def _valid_host(host):
    if not host or len(host) > 253 or any(ord(char) < 33 or char in "/\\@?#" for char in host):
        return False
    try:
        ipaddress.ip_address(host)
        return True
    except ValueError:
        return all(
            label
            and len(label) <= 63
            and re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?", label)
            for label in host.rstrip(".").split(".")
        )


def _form(host, values=None, message=None, is_error=False):
    values = values or {}
    return {
        "kind": "form",
        "title": "MikroTik RouterOS · REST",
        "fields": [
            {"id": "host", "label": "Host", "value": values.get("host", host)},
            {"id": "scheme", "label": "Protokół (http/https)", "value": values.get("scheme", "http")},
            {"id": "port", "label": "Port REST", "value": values.get("port", "80")},
            {"id": "user", "label": "Użytkownik", "value": values.get("user", "admin")},
            {"id": "password", "label": "Hasło", "value": values.get("password", ""), "password": True},
        ],
        "buttons": [{"label": "Połącz · tylko odczyt", "target": "c4k:plugin/connect"}],
        "message": message or (
            "Wymagany jest RouterOS 7 z włączonym REST. Podaj konto z uprawnieniami tylko do odczytu. "
            "Hasło nie jest zapisywane. HTTPS wymaga certyfikatu zaufanego przez Windows."
        ),
        "messageError": is_error,
    }


def _base_url(values):
    host = values.get("host", "").strip()
    scheme = values.get("scheme", "").strip().lower()
    if not _valid_host(host):
        raise ValueError("Nieprawidłowa nazwa hosta lub adres IP.")
    if scheme not in ("http", "https"):
        raise ValueError("Protokół musi mieć wartość http albo https.")
    try:
        port = int(values.get("port", ""))
    except (TypeError, ValueError):
        raise ValueError("Port musi być liczbą z zakresu 1–65535.") from None
    if not 1 <= port <= 65535:
        raise ValueError("Port musi być liczbą z zakresu 1–65535.")
    try:
        is_ipv6 = ipaddress.ip_address(host).version == 6
    except ValueError:
        is_ipv6 = False
    authority_host = f"[{host}]" if is_ipv6 else host
    default_port = 80 if scheme == "http" else 443
    authority = authority_host if port == default_port else f"{authority_host}:{port}"
    return f"{scheme}://{authority}/rest"


def _request(base_url, path, authorization):
    response = c4k.http_request(
        base_url + path,
        headers={"Accept": "application/json", "Authorization": authorization},
    )
    status = response.get("status", 0)
    if status != 200:
        if status == 401:
            raise PermissionError("Nieprawidłowy użytkownik lub hasło.")
        if status == 403:
            raise PermissionError("Konto nie ma uprawnień do odczytu danych RouterOS.")
        raise RuntimeError(f"RouterOS REST zwrócił HTTP {status}.")
    try:
        data = json.loads(response.get("body", ""))
    except (TypeError, json.JSONDecodeError):
        raise RuntimeError(f"RouterOS zwrócił niepoprawne dane JSON dla {path}.") from None
    if isinstance(data, dict):
        return [data]
    if not isinstance(data, list):
        raise RuntimeError(f"RouterOS zwrócił nieobsługiwany format dla {path}.")
    return data


def _cell(value):
    text = str(value if value is not None else "").replace("\r", " ").replace("\n", " ")
    return text.replace("|", r"\|")


def _table(title, rows, columns):
    lines = [f"### {title}", ""]
    if not rows:
        return lines + ["Brak wpisów.", ""]
    lines += ["| " + " | ".join(columns.values()) + " |", "| " + " | ".join("---" for _ in columns) + " |"]
    for row in rows[:MAX_ROWS]:
        lines.append("| " + " | ".join(_cell(row.get(key, "")) for key in columns) + " |")
    if len(rows) > MAX_ROWS:
        lines += ["", f"*Pokazano {MAX_ROWS} z {len(rows)} wpisów.*"]
    lines.append("")
    return lines


def _overview(base_url, user, password):
    token = base64.b64encode(f"{user}:{password}".encode("utf-8")).decode("ascii")
    authorization = "Basic " + token
    identity = _request(base_url, "/system/identity", authorization)
    resource = _request(base_url, "/system/resource", authorization)
    identity_name = identity[0].get("name", "RouterOS") if identity else "RouterOS"
    system = resource[0] if resource else {}
    lines = [f"# MikroTik RouterOS · {_cell(identity_name)}", ""]
    lines.append(f"- **Wersja:** {_cell(system.get('version', '—'))}")
    lines.append(f"- **Model:** {_cell(system.get('board-name', system.get('architecture-name', '—')))}")
    lines.append(f"- **Czas pracy:** {_cell(system.get('uptime', '—'))}")
    lines.append(f"- **CPU:** {_cell(system.get('cpu', '—'))} · {_cell(system.get('cpu-load', '—'))}%")
    lines.append(
        f"- **Pamięć:** {_cell(system.get('free-memory', '—'))} wolne / "
        f"{_cell(system.get('total-memory', '—'))} łącznie"
    )
    lines.append("")
    sections = [
        ("/interface", "Interfejsy", {"name": "Nazwa", "type": "Typ", "running": "Aktywny", "disabled": "Wyłączony", "mac-address": "MAC"}),
        ("/ip/address", "Adresy IP", {"address": "Adres", "network": "Sieć", "interface": "Interfejs", "disabled": "Wyłączony"}),
        ("/ip/route", "Trasy", {"dst-address": "Cel", "gateway": "Brama", "active": "Aktywna", "distance": "Dystans"}),
        ("/ip/service", "Usługi RouterOS", {"name": "Nazwa", "port": "Port", "disabled": "Wyłączona", "available": "Dostępna"}),
    ]
    for path, title, columns in sections:
        try:
            lines.extend(_table(title, _request(base_url, path, authorization), columns))
        except PermissionError as error:
            lines.extend([f"### {title}", "", f"Brak dostępu: {_cell(error)}", ""])
        except RuntimeError as error:
            lines.extend([f"### {title}", "", f"Nie udało się odczytać: {_cell(error)}", ""])
    return "\n".join(lines)


@c4k.handler("preview.render")
def render(params):
    entry = params.get("entry", {})
    host = _host_from_entry(entry)
    if not host:
        return {"kind": "markdown", "content": "Nie udało się odczytać hosta z wpisu usługi MikroTik."}
    return _form(host)


@c4k.handler("preview.action")
def action(params):
    if params.get("action") != "connect":
        return None
    entry = params.get("entry", {})
    host = _host_from_entry(entry)
    values = params.get("values") or {}
    if not host:
        return {"kind": "markdown", "content": "Nie udało się odczytać hosta z wpisu usługi MikroTik."}
    user = values.get("user", "").strip()
    password = values.get("password", "")
    try:
        if not user:
            raise ValueError("Podaj nazwę użytkownika.")
        base_url = _base_url(values)
        return {"kind": "markdown", "content": _overview(base_url, user, password)}
    except (ValueError, PermissionError, RuntimeError) as error:
        return _form(host, values, str(error), True)


@c4k.handler("preview.formChanged")
def form_changed(_params):
    return None


if __name__ == "__main__":
    c4k.serve()
