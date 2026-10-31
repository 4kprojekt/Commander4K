// Example plugin: a web preview. The page in web/ (PDF.js) shows the PDF; this process only
// tells the app which page to load and remembers the zoom the user picked (storage, no permission).
// The PDF itself is served to the page by the app at c4k.fileUrl, read with this plugin's fs.read permission.
'use strict';
const c4k = require('./c4k');

const DEFAULT_ZOOM = 'page-width';
const NAMED_ZOOMS = new Set(['auto', 'page-width', 'page-fit', 'page-actual']);

// A zoom is a named PDF.js scale or a number between 10% and 1000%.
function validZoom(value) {
  if (typeof value === 'string' && NAMED_ZOOMS.has(value)) return value;
  if (typeof value === 'number' && Number.isFinite(value) && value >= 0.1 && value <= 10) return value;
  return null;
}

c4k.on('preview.render', async () => {
  const zoom = validZoom(await c4k.storageGet('zoom')) ?? DEFAULT_ZOOM;
  return c4k.webPreview('index.html', { zoom });
});

// Requests from the page (c4k.request in web/viewer.js).
c4k.on('preview.message', async ({ method, params }) => {
  if (method === 'saveZoom') {
    const zoom = validZoom(params?.zoom);
    if (zoom === null) throw new Error('Invalid zoom');
    await c4k.storageSet('zoom', zoom);
    return null;
  }
  throw new Error(`Unknown request: ${method}`);
});
