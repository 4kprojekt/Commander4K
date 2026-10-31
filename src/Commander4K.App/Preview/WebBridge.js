// window.c4k: the bridge between a web preview page and Commander4K (see "Web previews" in docs/plugins.md).
// Injected into the top frame before any page script runs; the app appends "(<initial data as JSON>);" to this file.
// Messages to the app: { type: "request", id, token, method, params } | { type: "notify", text, error } | { type: "copyText", text } | { type: "key", key: "Escape" }.
// Messages from the app: { type: "reply", id, token, result | error } | { type: "entry", token, entry, fileUrl, state } | { type: "skin", skin }.
(function (init) {
  'use strict';
  if (window !== window.top || !window.chrome || !window.chrome.webview || window.c4k) {
    return;
  }

  const webview = window.chrome.webview;
  const listeners = { entry: [], skin: [] };
  const pending = new Map();
  let token = init.token;
  let nextId = 1;

  function fire(event, payload) {
    for (const fn of listeners[event].slice()) {
      try {
        fn(payload);
      } catch (error) {
        console.error('c4k "' + event + '" listener failed', error);
      }
    }
  }

  const c4k = {
    entry: init.entry,
    state: init.state,
    skin: init.skin,
    fileUrl: init.fileUrl,

    /** Subscribes to "entry" ({ entry, fileUrl, state }) or "skin" (the new c4k.skin); returns a function that unsubscribes. */
    on(event, fn) {
      if (!(event in listeners)) {
        throw new Error('Unknown c4k event: ' + event);
      }

      if (typeof fn !== 'function') {
        throw new TypeError('c4k.on needs a function');
      }

      listeners[event].push(fn);
      return () => {
        const index = listeners[event].indexOf(fn);
        if (index >= 0) {
          listeners[event].splice(index, 1);
        }
      };
    },

    /** Sends a request to the plugin's preview.message handler; resolves with its result, rejects with its error. */
    request(method, params) {
      if (typeof method !== 'string' || method.length === 0) {
        return Promise.reject(new TypeError('c4k.request needs a method name'));
      }

      return new Promise((resolve, reject) => {
        const id = nextId++;
        pending.set(id, { resolve, reject, token });
        webview.postMessage({ type: 'request', id, token, method, params: params === undefined ? null : params });
      });
    },

    /** Shows a short message in the app's status area. */
    notify(text, error) {
      webview.postMessage({ type: 'notify', text: String(text), error: error === true });
    },

    /** Puts text on the clipboard. */
    copyText(text) {
      webview.postMessage({ type: 'copyText', text: String(text) });
    },
  };

  webview.addEventListener('message', (event) => {
    const message = event.data;
    if (!message || typeof message !== 'object') {
      return;
    }

    if (message.type === 'reply') {
      const call = pending.get(message.id);
      if (!call) {
        return;
      }

      pending.delete(message.id);
      if (call.token !== token || message.token !== token) {
        return; // A reply about a file that is no longer shown.
      }

      if ('error' in message) {
        call.reject(new Error(String(message.error)));
      } else {
        call.resolve(message.result === undefined ? null : message.result);
      }
    } else if (message.type === 'entry') {
      token = message.token;
      for (const [id, call] of pending) {
        if (call.token !== token) {
          pending.delete(id); // Requests about the previous file are dropped, never settled.
        }
      }

      c4k.entry = message.entry;
      c4k.fileUrl = message.fileUrl;
      c4k.state = message.state === undefined ? null : message.state;
      fire('entry', { entry: c4k.entry, fileUrl: c4k.fileUrl, state: c4k.state });
    } else if (message.type === 'skin') {
      c4k.skin = message.skin;
      fire('skin', c4k.skin);
    }
  });

  // Esc the page did not use closes the preview. WebView2 usually reports Esc to the app itself (then the page never
  // sees it); this covers the case where it does not. Checked after dispatch, so the page's own handlers (on window
  // too) can call preventDefault() first; the app ignores this when it already got the same Esc.
  window.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !event.repeat) {
      setTimeout(() => {
        if (!event.defaultPrevented) {
          webview.postMessage({ type: 'key', key: 'Escape' });
        }
      }, 0);
    }
  });

  Object.defineProperty(window, 'c4k', { value: c4k, writable: false, configurable: false, enumerable: true });
})
