# upload-client-python

Example of passing files between panels (`accepts` / `provides`, see "Receiving and providing files" in
[`docs/plugins.md`](../../../docs/plugins.md)).

- Permission: only `net`. During `panel.receive` the app grants read access to the offered files itself.
- Panel "Adresy wysyłki" keeps HTTP endpoints in the plugin's storage; "Dodaj endpoint" asks for a URL, "Usuń" removes rows.
- Mark files in the other panel and press F5: pick an endpoint and each file is POSTed as `multipart/form-data`
  (field `file`) with `report_progress`. The result message reads "Wgrano N/M plików" with the HTTP code per file.
- F5 on an endpoint row offers "Zapisz odpowiedź GET jako plik": the response body becomes `<host>.json` in the other panel.
- `c4k.py` is a copy of `plugins-sdk/python/c4k.py`.
