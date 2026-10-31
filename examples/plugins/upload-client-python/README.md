# upload-client-python

Example of passing files between panels (`accepts` / `provides`, see "Receiving and providing files" in
[`docs/plugins.md`](../../../docs/plugins.md)).

- Permission: only `net`. During `panel.receive` the app grants read access to the offered files itself.
- Panel "Adresy wysyłki" keeps HTTP endpoints in the plugin's storage; "Dodaj endpoint" asks for a URL, "Usuń" removes rows.
- Mark files in the other panel and press F5: pick an endpoint and each file is POSTed as `multipart/form-data`
  (field `file`) with `report_progress`. The result message reads "Wgrano N/M plików" with the HTTP code per file.
- F5 on an endpoint row offers "Zapisz odpowiedź GET jako plik": the response body becomes a `.json` file named after the
  host and path (`127.0.0.1_8080_api_data.json`) in the other panel. A failed request (HTTP error, no connection) is
  returned as that file's `error`, so the app reports it and writes nothing.
- "Dodaj endpoint" refuses anything that is not an http(s) address with an error notification.
- Folders sent to the panel are reported as not supported; "Wgrano N/M" and the progress bar count files only.
- `c4k.py` is a copy of `plugins-sdk/python/c4k.py`.
