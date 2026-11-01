// Log viewer page (web preview). It reads the file from c4k.fileUrl in ranges (the app serves it with the plugin's
// fs.read permission), splits it into entries (time, level, source, message, continuation lines such as stack
// traces), groups them by request id or source, and draws only the rows on screen. No network: the page cannot
// reach anything but its own origin.
'use strict';

const bridge = window.c4k;
const TAIL_BYTES = 8 * 1024 * 1024;   // first look: the end of the file
const FULL_BYTES = 64 * 1024 * 1024;  // "Wczytaj więcej": at most this much
const LIVE_MS = 2000;
const ROW = 24;                        // every row and group header has the same height (virtual list)
const LEVELS = ['error', 'warning', 'info', 'debug'];
const LEVEL_NAMES = { error: 'Błędy', warning: 'Ostrzeżenia', info: 'Info', debug: 'Debug' };
const LEVEL_SHORT = { error: 'BŁĄD', warning: 'OSTRZ.', info: 'INFO', debug: 'DEBUG', '': '—' };

const $ = id => document.getElementById(id);
const ui = {
  live: $('live'), filter: $('filter'), chips: $('chips'), group: $('group'), exportButton: $('export'),
  table: $('table'), viewport: $('viewport'), spacer: $('spacerRows'), rows: $('rows'), empty: $('empty'),
  detail: $('detailBody'), status: $('status'),
};

const state = {
  entries: [], offset: 0, start: 0, size: 0, pending: '', decoder: null, hasTimestamps: null,
  levels: new Set(LEVELS), group: true, live: false, filter: '', similar: null,
  collapsed: new Set(), expanded: new Set(), items: [], cursor: 0, loading: false, generation: 0, timer: null,
};

// ---------- parsing ----------
const TS_ISO = /^\[?(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}:\d{2})(?:[.,](\d{1,7}))?(?:Z|[+-]\d{2}:?\d{2})?\]?\s*/;
const TS_TIME = /^\[?(\d{2}:\d{2}:\d{2})(?:[.,](\d{1,7}))?\]?\s+/;
const TS_SYSLOG = /^([A-Z][a-z]{2} +\d{1,2}) (\d{2}:\d{2}:\d{2})\s+/;
const LEVEL = /^[\[(<]?(FATAL|CRITICAL|CRIT|FTL|EMERG|ALERT|ERROR|ERR|EROR|FAIL|SEVERE|WARN|WARNING|WRN|INFO|INFORMATION|INF|NOTICE|DEBUG|DBG|TRACE|TRC|VERBOSE|VRB)[\])>]?:?\s+/i;
const LEVEL_ANYWHERE = /\b(FATAL|CRITICAL|ERROR|EXCEPTION|SEVERE|WARN(?:ING)?|INFO|DEBUG|TRACE)\b/i;
const CONTINUATION = /^(\s|at |Caused by|---|\.\.\. \d+ more|Traceback|File "|\}|\])/;
const SOURCE = /^(?:\[([^\]\s]{1,60})\]|([A-Za-z_][\w.$-]{0,60}(?:\[\d+\])?)(?::|\s+-|\s+(?=[A-ZĄĆĘŁŃÓŚŹŻ])))\s*/;
const CORRELATION = /(?:request|req|żądanie|trace|correlation|corr|operation|op)[ _-]?(?:id)?[=: ]+#?([0-9a-f][0-9a-f-]{5,})/i;
const GUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i;

function levelOf(word) {
  const w = word.toUpperCase();
  if (/^(FATAL|CRITICAL|CRIT|FTL|EMERG|ALERT|ERROR|ERR|EROR|FAIL|SEVERE|EXCEPTION)$/.test(w)) return 'error';
  if (/^(WARN|WARNING|WRN)$/.test(w)) return 'warning';
  if (/^(INFO|INFORMATION|INF|NOTICE)$/.test(w)) return 'info';
  return 'debug';
}

function startsEntry(line) {
  return TS_ISO.test(line) || TS_SYSLOG.test(line) || TS_TIME.test(line) || LEVEL.test(line);
}

/** One line that starts an entry → { time, stamp, level, source, message, corr }. */
function parseHead(line) {
  let rest = line;
  let time = '';
  let stamp = '';
  let m = TS_ISO.exec(rest);
  if (m) {
    time = m[2] + (m[3] ? '.' + m[3].padEnd(3, '0').slice(0, 3) : '');
    stamp = `${m[1]} ${m[2]}${m[3] ? '.' + m[3] : ''}`;
    rest = rest.slice(m[0].length);
  } else if ((m = TS_SYSLOG.exec(rest))) {
    time = m[2]; stamp = `${m[1]} ${m[2]}`; rest = rest.slice(m[0].length);
  } else if ((m = TS_TIME.exec(rest))) {
    time = m[1] + (m[2] ? '.' + m[2].padEnd(3, '0').slice(0, 3) : ''); stamp = time; rest = rest.slice(m[0].length);
  }

  let level = '';
  if ((m = LEVEL.exec(rest))) {
    level = levelOf(m[1]); rest = rest.slice(m[0].length);
  }

  let source = '';
  if ((m = SOURCE.exec(rest)) && (m[1] || m[2])) {
    const candidate = m[1] || m[2];
    // A source looks like a name (Sync.Io, sshd[42], [Backup]), not like the first word of a sentence.
    if (m[1] || /[.\[_$]/.test(candidate) || /:$/.test(m[0].trim())) {
      source = candidate; rest = rest.slice(m[0].length);
    }
  }

  if (!level && (m = LEVEL_ANYWHERE.exec(rest.slice(0, 120)))) level = levelOf(m[1] === 'EXCEPTION' ? 'ERROR' : m[1]);
  const message = rest.replace(/^[\s:|\-–]+/, '');
  const c = CORRELATION.exec(line) || GUID.exec(line);
  return { time, stamp, level, source, message, corr: c ? (c[1] || c[0]).toLowerCase() : '' };
}

function template(text) {
  return text.replace(GUID, '*').replace(/"[^"]{0,200}"|'[^']{0,200}'/g, '"*"').replace(/\b0x[0-9a-f]+\b|\b[0-9a-f]{8,}\b/gi, '*')
    .replace(/\d+([.,]\d+)?/g, '#').replace(/\s+/g, ' ').trim();
}

/** Adds decoded text; a line still being written stays in state.pending until its newline arrives. */
function addText(text, final) {
  const all = state.pending + text;
  const lines = all.split(/\r?\n/);
  state.pending = final ? '' : lines.pop();
  if (state.hasTimestamps === null) {
    const sample = lines.filter(l => l.trim()).slice(0, 200);
    state.hasTimestamps = sample.length > 0 && sample.filter(startsEntry).length >= sample.length * 0.3;
  }

  let current = state.entries[state.entries.length - 1] || null;
  for (const line of lines) {
    state.lineNumber++;
    if (!line.trim()) continue;
    const continues = current && (CONTINUATION.test(line) || (state.hasTimestamps && !startsEntry(line)));
    if (continues) {
      if (current.extra.length < 400) current.extra.push(line);
      if (!current.level && /exception|error/i.test(line)) current.level = 'error';
      continue;
    }

    const head = parseHead(line);
    current = { ...head, line: state.lineNumber, raw: line, extra: [], tpl: null };
    state.entries.push(current);
  }
}

// ---------- reading ----------
async function fileSize(url) {
  const response = await fetch(url, { method: 'HEAD', cache: 'no-store' });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return Number(response.headers.get('Content-Length') || 0);
}

async function readRange(url, from) {
  const response = await fetch(url, { headers: { Range: `bytes=${from}-` }, cache: 'no-store' });
  if (response.status === 416) return new Uint8Array(0);
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  // 200 = the whole file (the range was not honoured): keep only what is new.
  return response.status === 200 && from > 0 ? bytes.subarray(from) : bytes;
}

async function load(budget = TAIL_BYTES) {
  const generation = ++state.generation;
  stopLive();
  Object.assign(state, { entries: [], pending: '', hasTimestamps: null, lineNumber: 0, cursor: 0, similar: null, loading: true });
  state.collapsed.clear(); state.expanded.clear();
  render();
  try {
    const url = bridge.fileUrl;
    const size = await fileSize(url);
    const start = Math.max(0, size - budget);
    const bytes = await readRange(url, start);
    if (generation !== state.generation) return;
    state.decoder = new TextDecoder('utf-8');
    let text = state.decoder.decode(bytes, { stream: true });
    if (start > 0) text = text.slice(text.indexOf('\n') + 1); // the first line is cut in the middle
    Object.assign(state, { size, start, offset: start + bytes.length });
    addText(text, false);
    state.loading = false;
    rebuild(true);
    if (state.live) startLive();
  } catch (error) {
    if (generation !== state.generation) return;
    state.loading = false;
    ui.empty.hidden = false;
    ui.empty.textContent = 'Nie można odczytać pliku: ' + error.message;
    setStatus();
  }
}

async function poll() {
  const generation = state.generation;
  try {
    const url = bridge.fileUrl;
    const size = await fileSize(url);
    if (generation !== state.generation) return;
    if (size < state.offset) { await load(); return; } // rotated or truncated: start again
    if (size === state.offset) return;
    const bytes = await readRange(url, state.offset);
    if (generation !== state.generation || bytes.length === 0) return;
    const atEnd = ui.viewport.scrollTop + ui.viewport.clientHeight >= ui.viewport.scrollHeight - ROW * 2;
    state.offset += bytes.length; state.size = size;
    addText(state.decoder.decode(bytes, { stream: true }), false);
    rebuild(false);
    if (atEnd) { ui.viewport.scrollTop = ui.viewport.scrollHeight; }
  } catch { /* the next tick tries again */ }
}

function startLive() { stopLive(); state.timer = setInterval(poll, LIVE_MS); }
function stopLive() { if (state.timer) clearInterval(state.timer); state.timer = null; }

// ---------- model → rows ----------
function visible(entry) {
  const level = entry.level || 'debug';
  if (!state.levels.has(level)) return false;
  if (state.similar !== null && (entry.tpl ??= template(entry.message)) !== state.similar) return false;
  if (!state.filter) return true;
  const needle = state.filter;
  return entry.message.toLowerCase().includes(needle) || entry.source.toLowerCase().includes(needle)
    || entry.time.includes(needle) || entry.extra.some(l => l.toLowerCase().includes(needle));
}

function groupKey(entry) {
  if (entry.corr) return 'r:' + entry.corr;
  return 's:' + (entry.source ? entry.source.split(/[.\[]/)[0] : '');
}

function rebuild(resetCursor) {
  const shown = state.entries.filter(visible);
  const items = [];
  if (state.group) {
    const groups = new Map();
    for (const entry of shown) {
      const key = groupKey(entry);
      let group = groups.get(key);
      if (!group) groups.set(key, group = { key, entries: [], errors: 0, warnings: 0 });
      group.entries.push(entry);
      if (entry.level === 'error') group.errors++;
      if (entry.level === 'warning') group.warnings++;
    }

    // Titles come from each group's first entry in the file, so filtering does not rename groups.
    const firstOf = new Map();
    for (const entry of state.entries) if (!firstOf.has(groupKey(entry))) firstOf.set(groupKey(entry), entry);
    for (const group of groups.values()) {
      const first = firstOf.get(group.key) ?? group.entries[0];
      group.title = group.key.startsWith('r:')
        ? `${shorten(first.message, 70)} · żądanie ${group.key.slice(2, 10)}`
        : `${group.key.slice(2) || 'Bez źródła'} · ${group.entries.length.toLocaleString('pl-PL')} ${plural(group.entries.length, 'wpis', 'wpisy', 'wpisów')}`;
      const quiet = group.errors === 0 && group.warnings === 0 && group.entries.length > 20;
      const collapsed = state.collapsed.has(group.key) || (quiet && !state.expanded.has(group.key));
      items.push({ type: 'group', group, collapsed });
      if (!collapsed) for (const entry of group.entries) items.push({ type: 'entry', entry });
    }
  } else {
    for (const entry of shown) items.push({ type: 'entry', entry });
  }

  state.items = items;
  state.shown = shown.length;
  if (resetCursor) {
    const firstError = items.findIndex(i => i.type === 'entry' && i.entry.level === 'error');
    state.cursor = Math.max(0, firstError >= 0 ? firstError : items.length - 1);
  } else {
    state.cursor = Math.min(state.cursor, Math.max(0, items.length - 1));
  }

  renderChips();
  render();
  if (resetCursor) scrollToCursor(true);
  showDetails();
}

// ---------- drawing ----------
function render() {
  ui.spacer.style.height = state.items.length * ROW + 'px';
  ui.empty.hidden = state.loading ? false : state.items.length > 0;
  if (state.loading) ui.empty.textContent = 'Wczytywanie…';
  else if (state.items.length === 0) ui.empty.textContent = state.entries.length ? 'Nic nie pasuje do filtra.' : 'Plik jest pusty.';
  const top = ui.viewport.scrollTop;
  const first = Math.max(0, Math.floor(top / ROW) - 5);
  const last = Math.min(state.items.length, Math.ceil((top + ui.viewport.clientHeight) / ROW) + 5);
  ui.rows.style.transform = `translateY(${first * ROW}px)`;
  const fragment = document.createDocumentFragment();
  for (let i = first; i < last; i++) fragment.appendChild(drawItem(state.items[i], i));
  ui.rows.replaceChildren(fragment);
  setStatus();
}

function drawItem(item, index) {
  const div = document.createElement('div');
  div.dataset.index = index;
  div.setAttribute('role', 'row');
  if (item.type === 'group') {
    const g = item.group;
    div.className = 'group' + (index === state.cursor ? ' cur' : '');
    div.style.height = ROW + 'px';
    div.append(span('chev', item.collapsed ? '▸' : '▾'), span('title', g.title));
    const count = span('count' + (g.errors ? ' error' : ''), g.entries.length.toLocaleString('pl-PL'));
    count.title = g.errors ? `${g.errors} błędów, ${g.warnings} ostrzeżeń` : `${g.warnings} ostrzeżeń`;
    div.append(count);
    return div;
  }

  const e = item.entry;
  div.className = 'row' + (index === state.cursor ? ' cur' : '');
  div.style.height = ROW + 'px';
  const level = span('c-level', '');
  const label = span('lvl ' + (e.level || 'debug'), (e.level === 'error' ? '● ' : e.level === 'warning' ? '▲ ' : '') + LEVEL_SHORT[e.level]);
  level.append(label);
  const message = span('c-msg', '');
  highlight(message, e.message + (e.extra.length ? `  (+${e.extra.length})` : ''));
  div.append(span('c-time', e.time || '—'), level, span('c-source', e.source), message);
  div.title = e.message;
  return div;
}

function span(className, text) {
  const s = document.createElement('span');
  s.className = className;
  s.textContent = text;
  return s;
}

/** Text with the filter's matches marked; built from text nodes, never from HTML. */
function highlight(target, text) {
  if (!state.filter) { target.textContent = text; return; }
  const lower = text.toLowerCase();
  let at = 0;
  for (let i = lower.indexOf(state.filter); i >= 0; i = lower.indexOf(state.filter, at)) {
    target.append(text.slice(at, i));
    const mark = document.createElement('mark');
    mark.textContent = text.slice(i, i + state.filter.length);
    target.append(mark);
    at = i + state.filter.length;
  }
  target.append(text.slice(at));
}

function renderChips() {
  const counts = { error: 0, warning: 0, info: 0, debug: 0 };
  for (const e of state.entries) counts[e.level || 'debug']++;
  state.counts = counts;
  const colors = { error: 'var(--error)', warning: 'var(--warning)', info: 'var(--info)', debug: 'var(--debug)' };
  const chips = LEVELS.map(level => {
    const chip = document.createElement('button');
    chip.type = 'button';
    chip.className = 'chip' + (state.levels.has(level) ? ' on' : '');
    chip.setAttribute('aria-pressed', state.levels.has(level));
    const dot = span('dot', ''); dot.style.background = colors[level];
    chip.append(dot, LEVEL_NAMES[level] + ' ', Object.assign(document.createElement('i'), { textContent: counts[level].toLocaleString('pl-PL') }));
    chip.addEventListener('click', () => {
      if (state.levels.has(level)) state.levels.delete(level); else state.levels.add(level);
      saveSettings();
      rebuild(false);
    });
    return chip;
  });
  if (state.similar !== null) {
    const similar = document.createElement('button');
    similar.type = 'button';
    similar.className = 'chip on';
    similar.title = state.similar;
    similar.append('Podobne: ' + shorten(state.similar, 30) + ' ✕');
    similar.addEventListener('click', () => { state.similar = null; rebuild(false); });
    chips.push(similar);
  }
  ui.chips.replaceChildren(...chips);
}

function setStatus() {
  const c = state.counts || { error: 0, warning: 0 };
  const parts = [];
  if (state.loading) parts.push('Wczytywanie…');
  else {
    parts.push(`${state.entries.length.toLocaleString('pl-PL')} ${plural(state.entries.length, 'wpis', 'wpisy', 'wpisów')} · widoczne ${(state.shown ?? 0).toLocaleString('pl-PL')}`);
    if (c.error) parts.push(`<span class="e">● ${c.error.toLocaleString('pl-PL')} ${plural(c.error, 'błąd', 'błędy', 'błędów')}</span>`);
    if (c.warning) parts.push(`<span class="w">▲ ${c.warning.toLocaleString('pl-PL')} ${plural(c.warning, 'ostrzeżenie', 'ostrzeżenia', 'ostrzeżeń')}</span>`);
    if (state.filter || state.similar !== null || state.levels.size < LEVELS.length) parts.push('<span class="f">Filtr aktywny</span>');
    if (state.start > 0) parts.push(`pokazano ostatnie ${formatSize(state.size - state.start)} z ${formatSize(state.size)}`);
    if (state.live) parts.push('na żywo');
  }
  // Only fixed markup and numbers go into innerHTML; file text never does.
  ui.status.innerHTML = parts.map(p => `<span>${p}</span>`).join('');
  if (!state.loading && state.start > 0 && state.size - state.start < Math.min(state.size, FULL_BYTES)) {
    const more = document.createElement('button');
    more.className = 'btn'; more.type = 'button'; more.textContent = 'Wczytaj więcej';
    more.addEventListener('click', () => load(FULL_BYTES));
    ui.status.append(more);
  }
}

function showDetails() {
  const item = state.items[state.cursor];
  ui.detail.replaceChildren();
  if (!item) { ui.detail.append(Object.assign(document.createElement('p'), { className: 'muted', textContent: 'Brak wpisów.' })); return; }
  if (item.type === 'group') {
    const g = item.group;
    ui.detail.append(Object.assign(document.createElement('p'), { className: 'msg', textContent: g.title }));
    ui.detail.append(kv([
      ['Wpisy', g.entries.length.toLocaleString('pl-PL')],
      ['Błędy', String(g.errors)],
      ['Ostrzeżenia', String(g.warnings)],
      ['Od', g.entries[0].stamp || '—'],
      ['Do', g.entries[g.entries.length - 1].stamp || '—'],
    ]));
    return;
  }

  const e = item.entry;
  ui.detail.append(Object.assign(document.createElement('p'), { className: 'msg', textContent: e.message }));
  ui.detail.append(kv([
    ['Czas', e.stamp || '—'],
    ['Poziom', LEVEL_SHORT[e.level]],
    ['Źródło', e.source || '—'],
    ['Żądanie', e.corr || '—'],
    ['Wiersz', state.start > 0 ? `${e.line.toLocaleString('pl-PL')} (od początku wczytanego fragmentu)` : e.line.toLocaleString('pl-PL')],
  ]));
  if (e.extra.length) {
    ui.detail.append(Object.assign(document.createElement('pre'), { textContent: e.extra.join('\n') }));
  }

  const actions = document.createElement('div');
  actions.className = 'actions';
  actions.append(
    button('Kopiuj wpis', () => { bridge.copyText([e.raw, ...e.extra].join('\n')); bridge.notify('Skopiowano wpis.'); }),
    button('Szukaj podobnych', () => { state.similar = e.tpl ??= template(e.message); rebuild(true); }),
  );
  ui.detail.append(actions);
}

function kv(pairs) {
  const dl = document.createElement('dl');
  dl.className = 'kv';
  for (const [k, v] of pairs) dl.append(Object.assign(document.createElement('dt'), { textContent: k }), Object.assign(document.createElement('dd'), { textContent: v }));
  return dl;
}

function button(text, onClick) {
  const b = document.createElement('button');
  b.type = 'button'; b.className = 'btn'; b.textContent = text;
  b.addEventListener('click', onClick);
  return b;
}

// ---------- interaction ----------
function moveCursor(to) {
  state.cursor = Math.max(0, Math.min(state.items.length - 1, to));
  scrollToCursor(false);
  render();
  showDetails();
}

function scrollToCursor(center) {
  const top = state.cursor * ROW;
  const view = ui.viewport;
  if (center) view.scrollTop = Math.max(0, top - view.clientHeight / 2);
  else if (top < view.scrollTop) view.scrollTop = top;
  else if (top + ROW > view.scrollTop + view.clientHeight) view.scrollTop = top + ROW - view.clientHeight;
}

function toggleGroup(item, open) {
  const key = item.group.key;
  const wantOpen = open ?? item.collapsed;
  if (wantOpen) { state.collapsed.delete(key); state.expanded.add(key); } else { state.collapsed.add(key); state.expanded.delete(key); }
  rebuild(false);
}

ui.viewport.addEventListener('scroll', render, { passive: true });
window.addEventListener('resize', render);
ui.rows.addEventListener('click', event => {
  const row = event.target.closest('[data-index]');
  if (!row) return;
  const index = Number(row.dataset.index);
  const item = state.items[index];
  if (item?.type === 'group' && (event.target.classList.contains('chev') || index === state.cursor)) { state.cursor = index; toggleGroup(item); return; }
  moveCursor(index);
  ui.table.focus();
});

ui.table.addEventListener('keydown', event => {
  const page = Math.max(1, Math.floor(ui.viewport.clientHeight / ROW) - 1);
  const item = state.items[state.cursor];
  switch (event.key) {
    case 'ArrowDown': moveCursor(state.cursor + 1); break;
    case 'ArrowUp': moveCursor(state.cursor - 1); break;
    case 'PageDown': moveCursor(state.cursor + page); break;
    case 'PageUp': moveCursor(state.cursor - page); break;
    case 'Home': moveCursor(0); break;
    case 'End': moveCursor(state.items.length - 1); break;
    case 'ArrowRight': if (item?.type === 'group' && item.collapsed) toggleGroup(item, true); else return; break;
    case 'ArrowLeft': if (item?.type === 'group' && !item.collapsed) toggleGroup(item, false); else return; break;
    case 'Enter':
      if (item?.type === 'group') toggleGroup(item);
      else ui.detail.querySelector('button')?.focus();
      break;
    default: return;
  }
  event.preventDefault();
});

let filterDelay = null;
ui.filter.addEventListener('input', () => {
  clearTimeout(filterDelay);
  filterDelay = setTimeout(() => { state.filter = ui.filter.value.trim().toLowerCase(); rebuild(false); }, 150);
});
ui.filter.addEventListener('keydown', event => {
  if (event.key === 'Escape' && ui.filter.value) { ui.filter.value = ''; state.filter = ''; rebuild(false); event.preventDefault(); }
  if (event.key === 'Enter' || event.key === 'ArrowDown') { ui.table.focus(); event.preventDefault(); }
});
document.addEventListener('keydown', event => {
  if (event.ctrlKey && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'f') { ui.filter.focus(); ui.filter.select(); event.preventDefault(); }
});

ui.live.addEventListener('change', () => { state.live = ui.live.checked; saveSettings(); if (state.live) { startLive(); poll(); } else stopLive(); setStatus(); });
ui.group.addEventListener('change', () => { state.group = ui.group.checked; saveSettings(); rebuild(true); });
ui.exportButton.addEventListener('click', () => {
  const lines = [];
  let length = 0;
  for (const e of state.entries.filter(visible)) {
    for (const line of [e.raw, ...e.extra]) { lines.push(line); length += line.length + 1; }
    if (length > 2_000_000) break;
  }
  bridge.copyText(lines.join('\n'));
  bridge.notify(`Skopiowano ${lines.length.toLocaleString('pl-PL')} linii widocznych wpisów.`);
});

function saveSettings() {
  bridge.request('saveSettings', { live: state.live, group: state.group, levels: [...state.levels] }).catch(() => {});
}

// ---------- helpers ----------
function shorten(text, max) { return text.length > max ? text.slice(0, max - 1) + '…' : text; }
function plural(n, one, few, many) {
  if (n === 1) return one;
  const d = n % 10, t = n % 100;
  return d >= 2 && d <= 4 && (t < 12 || t > 14) ? few : many;
}
function formatSize(bytes) {
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes, unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toLocaleString('pl-PL', { maximumFractionDigits: unit ? 1 : 0 })} ${units[unit]}`;
}

// ---------- skin ----------
function hex(color) { const v = parseInt(color.slice(1, 7), 16); return [v >> 16 & 255, v >> 8 & 255, v & 255]; }
function mix(a, b, t) { const x = hex(a), y = hex(b); return '#' + x.map((c, i) => Math.round(c + (y[i] - c) * t).toString(16).padStart(2, '0')).join(''); }
function luminance(color) { const [r, g, b] = hex(color); return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255; }

function applySkin(skin) {
  if (!skin) return;
  const dark = luminance(skin.background) < 0.5;
  const root = document.documentElement.style;
  const set = (name, value) => root.setProperty(name, value);
  set('--bg', skin.background); set('--fg', skin.foreground); set('--accent', skin.accent); set('--muted', skin.muted);
  set('--border', skin.border); set('--sel', skin.selection); set('--selfg', luminance(skin.selection) < 0.55 ? '#ffffff' : '#111111');
  set('--header', mix(skin.background, skin.foreground, 0.04)); set('--hover', mix(skin.background, skin.foreground, 0.08));
  set('--highlight', mix(skin.background, skin.accent, dark ? 0.2 : 0.12)); set('--highlightfg', mix(skin.accent, skin.foreground, dark ? 0.25 : 0.35));
  set('--code', dark ? mix(skin.background, '#000000', 0.3) : mix(skin.background, skin.foreground, 0.03));
  set('--error', dark ? '#FF6B6B' : '#C42B2B'); set('--warning', dark ? '#F2B544' : '#9A5B00');
  set('--info', dark ? '#6CC4FF' : '#0B6FB8'); set('--debug', skin.muted);
  set('--font', skin.fontFamily); set('--size', skin.fontSize + 'px');
}

// ---------- start ----------
function start() {
  if (!bridge) { document.body.textContent = 'Ta strona działa tylko w podglądzie Commander4K.'; return; }
  const settings = bridge.state || {};
  state.live = settings.live === true;
  state.group = settings.group !== false;
  if (Array.isArray(settings.levels)) state.levels = new Set(settings.levels.filter(l => LEVELS.includes(l)));
  ui.live.checked = state.live;
  ui.group.checked = state.group;
  applySkin(bridge.skin);
  bridge.on('skin', applySkin);
  bridge.on('entry', () => { ui.filter.value = ''; state.filter = ''; load(); });
  load();
  ui.table.focus();
}

start();
