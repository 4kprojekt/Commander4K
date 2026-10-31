// Commander4K plugin protocol v1 — Node.js client (copy this file next to your plugin's main.js).
//
// Open source (MIT): https://github.com/4kprojekt/Commander4K/tree/main/plugins-sdk — reference: docs/plugins.md.
//
// The app starts the plugin with stdin/stdout connected; messages are JSON-RPC 2.0, one per line.
// Register handlers with on('command.execute', async params => ...), call the app with the helpers
// below (every call is permission-checked against plugin.json and audited), then nothing else: the
// process keeps running while stdin is open.
//
// RULE: do ALL file, folder, process, network and system work through these helpers — never with Node's own
// fs, child_process, net, http(s) or fetch. The app checks permissions and audits these calls, works on every
// location the user can open (shares, SFTP, archives, registry) and Commander4K's installer warns about plugins
// that bypass them. Never console.log(): stdout is the protocol channel (use log()).
'use strict';
const readline = require('node:readline');

const handlers = new Map();
const pending = new Map();
let nextId = 0;

class HostError extends Error {
  constructor(code, message, data = null) {
    super(message);
    this.code = code; // -32001: permission missing from plugin.json; -32010: mail login needed; -32011: untrusted mail certificate
    this.data = data; // -32010: { authRequired: true, realm }; -32011: { host, fingerprint }
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
      if (message.error) waiter.reject(new HostError(message.error.code, message.error.message, message.error.data ?? null));
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
      send({ jsonrpc: '2.0', id: message.id, error: { code: error.code ?? -32000, message: String(error.message ?? error), ...(error.data ? { data: error.data } : {}) } });
    }
  }
}

readline.createInterface({ input: process.stdin }).on('line', line => {
  if (line.trim()) dispatch(JSON.parse(line));
});
process.stdin.on('end', () => process.exit(0));

// ---- app API (see docs/plugins.md; the comment names the permission plugin.json must declare) ----
// Every helper returns a Promise. Entries (listDir, stat, context().selection) are objects: name, path,
// kind ('file', 'directory', 'drive', 'archive'…), size (-1 = unknown), modified (ISO 8601 or null), hidden.
module.exports = {
  on,
  call,
  HostError,
  context: () => call('context.get'),                                            // {activePath, otherPath, selection}
  log: message => call('log.write', { message }),                                // audit log (use instead of console.log)
  notify: (message, error = false, target = null) => call('ui.notify', { message, error, target }), // target: path/link opened when the toast is clicked
  showText: (title, text) => call('ui.showText', { title, text }),
  confirm: (title, message) => call('ui.confirm', { title, message }),           // true/false
  prompt: (title, message, initial = '') => call('ui.prompt', { title, message, initial }), // string or null
  navigate: (path, focus = null) => call('ui.navigate', { path, focus }),
  openUrl: url => call('ui.openUrl', { url }),                                   // internal browser, http(s) only
  search: (query, engine = null) => call('ui.search', { query, engine }),        // engine: null, 'assistant', 'google', 'chatgpt'…
  copyText: text => call('ui.copyText', { text }),                               // clipboard (write only)
  storageGet: async (key, fallback = null) => (await call('storage.get', { key })) ?? fallback, // the plugin's own settings/data (no permission)
  storageSet: (key, value) => call('storage.set', { key, value }),              // any JSON value; null removes the key
  listDir: path => call('fs.list', { path }),                                  // fs.read -> [entry]
  stat: path => call('fs.stat', { path }),                                     // fs.read -> entry or null
  readText: (path, maxBytes = 1048576) => call('fs.readText', { path, maxBytes }), // fs.read -> string (UTF-8)
  readBytes: async (path, offset = 0, length = 65536) =>                         // fs.read -> Buffer (max 16 MB)
    Buffer.from((await call('fs.readBytes', { path, offset, length })).data, 'base64'),
  writeText: (path, content) => call('fs.writeText', { path, content }),       // fs.write
  writeBytes: (path, data) => call('fs.writeBytes', { path, data: Buffer.from(data).toString('base64') }), // fs.write
  makeDir: path => call('fs.mkdir', { path }),                                 // fs.write
  remove: (path, recursive = false) => call('fs.delete', { path, recursive }), // fs.write
  move: (from, to, overwrite = false) => call('fs.move', { from, to, overwrite }), // fs.write (also rename)
  listProcesses: () => call('process.list'),                                     // process.read -> [{pid, parentPid, name, path, memoryBytes, started}]
  killProcess: (pid, tree = false) => call('process.kill', { pid, tree }),      // process.kill
  run: (file, args = [], cwd = null, elevated = false) => call('shell.run', { file, args, cwd, elevated }), // shell.run -> {exitCode, output, error}
  systemInfo: () => call('system.info'),                                         // system.info -> {os, machine, user, processors, memoryBytes, drives}
  httpGet: url => call('net.get', { url }),                                      // net -> body text (error on non-2xx)
  httpRequest: (url, { method = 'GET', headers = {}, body = null, contentType = null } = {}) => // net -> {status, headers, body}
    call('net.request', { url, method, headers, body, contentType }),

  // ---- mail (permission 'mail'; fetch/compose/attachments also need fs.write, parse/send/append fs.read) ----
  // account = { imap: 'host:993', smtp: 'host:465' | null, user: 'name', interactive: true }. The app owns the password
  // and certificates: interactive: false never shows a dialog and rejects with HostError -32010/-32011 instead.
  // Flags are 'seen', 'flagged', 'answered', 'draft'. Message files are .eml paths (any location the app can open).
  mailFolders: account => call('mail.folders', { account }),                     // -> [{serverName, localName, separator, special, uidValidity, count, unread}]
  mailSync: (account, folder, { uidValidity = 0, lastUid = 0, modSeq = 0, since = null, knownUids = [] } = {}) => // -> {uidValidity, uidValidityChanged, modSeq, new, flagChanges, removed}
    call('mail.sync', { account, folder, uidValidity, lastUid, modSeq, since, knownUids }),
  mailFetch: async (account, folder, uid, path) => (await call('mail.fetch', { account, folder, uid, path })).bytes, // fs.write -> bytes written
  mailSetFlags: (account, folder, uids, add = [], remove = []) => call('mail.setFlags', { account, folder, uids, add, remove }),
  mailMove: (account, folder, uids, target) => call('mail.move', { account, folder, uids, target }),
  mailDelete: (account, folder, uids) => call('mail.delete', { account, folder, uids }), // Trash, or expunge inside Trash
  mailAppend: async (account, folder, path, flags = []) => (await call('mail.append', { account, folder, path, flags })).uid, // fs.read -> new uid or null
  mailParse: path => call('mail.parse', { path }),                               // fs.read -> {from, to, cc, subject, date, messageId, references, bodyText, attachments}
  mailSaveAttachment: (path, index, target) => call('mail.saveAttachment', { path, index, target }), // fs.read + fs.write
  mailReply: (path, mode = 'reply', self = '') => call('mail.reply', { path, mode, self }), // mode: reply | replyAll | forward -> draft (fs.read)
  mailCompose: (path, draft) => call('mail.compose', { path, draft }),           // fs.write (+ fs.read for draft.attachments); draft = {to, cc, bcc, subject, body, inReplyTo, references, attachments, from}
  mailSend: async (account, path) => (await call('mail.send', { account, path })).messageId, // fs.read -> Message-ID
};
