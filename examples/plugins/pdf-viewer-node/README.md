# pdf-viewer-node — PDF quick view (web preview example)

Shows `.pdf` files in Commander4K's quick view (`Ctrl+Q`) with [PDF.js](https://mozilla.github.io/pdf.js/): pages, zoom, search and printing. It is the example for previews of `kind: "web"` (see "Web previews" in [`docs/plugins.md`](../../../docs/plugins.md)).

- `main.js` (Node.js ≥ 18, through `c4k.js`) answers `preview.render` with `webPreview("index.html", { zoom })` and stores the zoom the page sends with `c4k.request("saveZoom", …)` in the plugin's storage.
- `web/` is the page (`webRoot`): `index.html`, `viewer.js`, `viewer.css` and PDF.js in `web/pdfjs` (version and checksum in [`web/pdfjs/SOURCE.md`](web/pdfjs/SOURCE.md)). The page reads the PDF from `c4k.fileUrl` in 64 KB ranges; the app serves it with this plugin's `fs.read` permission. The page has no network access.

This example is not embedded in the app as a template (PDF.js is ~6 MB) and is not in the plugin catalog.

## Install

1. Copy the `pdf-viewer-node` folder into the plugins folder: `plugins/` in the data folder (the plugin manager's "Folder wtyczek", F7, opens it) or `plugins/` next to `Commander4K.exe`. Node.js 18 or newer must be installed.
2. Restart Commander4K (plugins are found at start-up).
3. In the plugin manager, select "Podgląd PDF (Node.js, PDF.js)" and approve its permission `fs.read` (Ctrl+E). Until then it stays disabled.

## Use

- Put the cursor on a `.pdf` file and press `Ctrl+Q`. Moving the cursor to another PDF loads it in the same page.
- Toolbar: ▲/▼ or the page number (Enter) to move between pages; −/+ and the zoom list (page width, whole page, percentages). The zoom is remembered.
- Search: `Ctrl+F` focuses the box; Enter finds the next match, Shift+Enter the previous one; `n / m` shows the match count and all matches are highlighted.
- "Drukuj" renders every page at 150 dpi, then opens the print dialog.
- Colours follow the app's skin. Esc closes the preview.
