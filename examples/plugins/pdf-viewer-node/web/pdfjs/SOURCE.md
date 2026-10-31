# PDF.js (vendored)

- Package: `pdfjs-dist` **5.7.284** (newest 5.x on 2026-10-31; 6.x exists and was not used)
- Tarball: https://registry.npmjs.org/pdfjs-dist/-/pdfjs-dist-5.7.284.tgz
- Integrity (npm `dist.integrity`, checked before extracting — base64 SHA-512 of the tarball matched):
  `sha512-h4EdYQczmGhbOlqc3PPZwxevn7ApdWPbovAuWXOB/DjIyigSnwfy2oze7c6mRcSr9XgLp3eN3EeL4DyySTPMFw==`
- Licence: Apache-2.0 (`LICENSE`); the WebAssembly decoders keep their own licence files in `wasm/`.
- Downloaded with curl, extracted with `tar -xzf`; no `npm install`, no package scripts.

Files copied unchanged from the package:

| Here | From the package |
|---|---|
| `build/pdf.min.mjs`, `build/pdf.worker.min.mjs` | `build/` |
| `web/pdf_viewer.mjs`, `web/pdf_viewer.css` | `web/` |
| `web/images/` | `web/images/` (icons referenced by `pdf_viewer.css` and annotation icons) |
| `cmaps/`, `standard_fonts/`, `wasm/`, `iccs/` | same folders (CJK character maps, the 14 standard fonts, JPEG 2000/JBIG2/colour decoders, ICC profiles) |
| `LICENSE` | `LICENSE` |

To update: download the new tarball, verify it against `npm view pdfjs-dist@<version> dist.integrity`, copy the same files and update this file.
