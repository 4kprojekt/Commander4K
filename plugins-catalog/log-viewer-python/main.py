# Official plugin: log files as a table of entries (web preview).
# The page in web/ reads the file itself from c4k.fileUrl (the app serves it with this plugin's fs.read permission,
# audited); this process only names the page and keeps the user's choices in the plugin's storage.
import c4k

DEFAULTS = {"live": False, "levels": ["error", "warning", "info", "debug"], "group": True}
LEVELS = {"error", "warning", "info", "debug"}


def settings():
    stored = c4k.storage_get("settings", {}) or {}
    result = dict(DEFAULTS)
    if isinstance(stored.get("live"), bool):
        result["live"] = stored["live"]
    if isinstance(stored.get("group"), bool):
        result["group"] = stored["group"]
    if isinstance(stored.get("levels"), list):
        result["levels"] = [level for level in stored["levels"] if level in LEVELS]
    return result


@c4k.handler("preview.render")
def render(params):
    return c4k.web_preview("index.html", settings())


@c4k.handler("preview.message")
def message(params):
    method = params.get("method")
    if method == "saveSettings":
        values = params.get("params") or {}
        current = settings()
        if isinstance(values.get("live"), bool):
            current["live"] = values["live"]
        if isinstance(values.get("group"), bool):
            current["group"] = values["group"]
        if isinstance(values.get("levels"), list):
            current["levels"] = [level for level in values["levels"] if level in LEVELS]
        c4k.storage_set("settings", current)
        return None
    raise ValueError("Unknown request: " + str(method))


c4k.serve()
