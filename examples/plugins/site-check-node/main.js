// Example plugin: checks web sites through the app's net.get API (needs the "net" permission).
// Everything goes through the SDK: the site list is the plugin's own data (storage.get/set, no permission needed).
'use strict';
const c4k = require('./c4k');

const DEFAULT_SITES = ['https://github.com', 'https://www.nuget.org', 'https://learn.microsoft.com'];

async function loadSites() {
  return c4k.storageGet('sites', DEFAULT_SITES);
}

async function check(url) {
  const started = Date.now();
  try {
    const body = await c4k.httpGet(url);
    const title = /<title[^>]*>([^<]*)<\/title>/i.exec(body)?.[1]?.trim() ?? '';
    return { ok: true, ms: Date.now() - started, size: body.length, title };
  } catch (error) {
    return { ok: false, ms: Date.now() - started, error: error.message };
  }
}

c4k.on('command.execute', async () => {
  const url = await c4k.prompt('Sprawdź adres URL', 'Adres (https://…):', 'https://github.com');
  if (!url) return null;
  const result = await check(url);
  return result.ok
    ? { text: { title: url, content: `Tytuł: ${result.title}\nRozmiar: ${result.size} znaków\nCzas: ${result.ms} ms` } }
    : { message: `${url}: ${result.error}` };
});

c4k.on('panel.load', async () => {
  const sites = await loadSites();
  const results = await Promise.all(sites.map(async url => ({ url, ...(await check(url)) })));
  return {
    columns: [
      { id: 'url', header: 'Adres', width: 320 },
      { id: 'state', header: 'Stan', width: 80 },
      { id: 'ms', header: 'Czas', align: 'right', width: 80 },
      { id: 'title', header: 'Tytuł / błąd', width: 360 },
    ],
    rows: results.map(r => ({
      id: r.url,
      cells: [r.url, r.ok ? 'OK' : 'BŁĄD', `${r.ms} ms`, r.ok ? r.title : r.error],
      severity: r.ok ? (r.ms > 2000 ? 'warning' : 'normal') : 'error',
      sortKeys: [null, null, r.ms, null],
    })),
    status: `${results.filter(r => r.ok).length}/${results.length} dostępnych · ${new Date().toLocaleTimeString()}`,
  };
});

c4k.on('panel.details', async ({ rowId }) => ({
  sections: [{ title: rowId, items: [['Szczegóły', 'Kliknij „Odśwież”, aby sprawdzić ponownie.']] }],
}));

// Panel actions: "add" asks for an address, "remove" drops the selected rows (the row id is the address).
c4k.on('panel.action', async ({ action, rowIds }) => {
  const sites = await loadSites();
  if (action === 'add') {
    const url = await c4k.prompt('Dodaj stronę', 'Adres (https://…):', 'https://');
    if (!url || !/^https?:\/\/\S+$/.test(url)) return null;
    sites.push(url);
  } else if (action === 'remove') {
    for (const id of rowIds ?? []) {
      const index = sites.indexOf(id);
      if (index >= 0) sites.splice(index, 1);
    }
  } else {
    return null;
  }
  await c4k.storageSet('sites', sites);
  return null;
});
