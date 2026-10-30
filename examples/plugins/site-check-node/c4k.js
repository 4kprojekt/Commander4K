// Commander4K plugin protocol v1 — Node.js client (copy this file next to your plugin's main.js).
//
// The app starts the plugin with stdin/stdout connected; messages are JSON-RPC 2.0, one per line.
// Register handlers with on('command.execute', async params => ...), call the app with the helpers
// below (every call is permission-checked against plugin.json and audited), then nothing else: the
// process keeps running while stdin is open.
'use strict';
const readline = require('node:readline');

const handlers = new Map();
const pending = new Map();
let nextId = 0;

class HostError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code; // -32001: permission missing from plugin.json
  }
}

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

function on(method, fn) {
  handlers.set(method, fn);
}

function call(method, params = {}) {
  const id = `p${++nextId}`;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    send({ jsonrpc: '2.0', id, method, params });
  });
}

async function dispatch(message) {
  if (message.method === undefined) {
    const waiter = pending.get(message.id);
    if (waiter) {
      pending.delete(message.id);
      if (message.error) waiter.reject(new HostError(message.error.code, message.error.message));
      else waiter.resolve(message.result);
    }
    return;
  }
  if (message.method.startsWith('$/')) return;
  if (message.method === 'shutdown') {
    send({ jsonrpc: '2.0', id: message.id, result: null });
    process.exit(0);
  }
  const fn = handlers.get(message.method) ?? (message.method === 'initialize' ? async () => ({}) : undefined);
  try {
    if (!fn) throw new HostError(-32601, `Unknown method ${message.method}`);
    const result = await fn(message.params ?? {});
    if (message.id !== undefined) send({ jsonrpc: '2.0', id: message.id, result: result ?? null });
  } catch (error) {
    if (message.id !== undefined) {
      send({ jsonrpc: '2.0', id: message.id, error: { code: error.code ?? -32000, message: String(error.message ?? error) } });
    }
  }
}

readline.createInterface({ input: process.stdin }).on('line', line => {
  if (line.trim()) dispatch(JSON.parse(line));
});
process.stdin.on('end', () => process.exit(0));

// ---- app API (see docs/plugins.md) ----
module.exports = {
  on,
  call,
  HostError,
  context: () => call('context.get'),
  log: message => call('log.write', { message }),
  notify: (message, error = false) => call('ui.notify', { message, error }),
  showText: (title, text) => call('ui.showText', { title, text }),
  confirm: (title, message) => call('ui.confirm', { title, message }),
  prompt: (title, message, initial = '') => call('ui.prompt', { title, message, initial }),
  navigate: (path, focus = null) => call('ui.navigate', { path, focus }),
  openUrl: url => call('ui.openUrl', { url }),                                   // internal browser, http(s) only
  search: (query, engine = null) => call('ui.search', { query, engine }),        // engine: null, 'assistant', 'google', 'chatgpt'…
  listDir: path => call('fs.list', { path }),                                  // fs.read
  readText: (path, maxBytes = 1048576) => call('fs.readText', { path, maxBytes }), // fs.read
  writeText: (path, content) => call('fs.writeText', { path, content }),       // fs.write
  killProcess: (pid, tree = false) => call('process.kill', { pid, tree }),      // process.kill
  run: (file, args = [], cwd = null, elevated = false) => call('shell.run', { file, args, cwd, elevated }), // shell.run
  httpGet: url => call('net.get', { url }),                                      // net
};
