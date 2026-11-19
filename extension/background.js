// Service worker: keeps the WebSocket to the CaptionRelay app open and hands "send" messages to the active provider.
import { RelayConnection } from './lib/connection.js';
import { getProvider, DEFAULT_PROVIDER } from './providers/registry.js';
import { log, warn, setForwarder } from './lib/log.js';

// autoSubmit false (default): the text is only pasted into the chat's input; the user presses Enter themselves.
// focusAfterPaste true (default): after a paste the chat's tab, window and input get the focus, so Enter sends it.
const DEFAULTS = { port: 47321, token: '', provider: DEFAULT_PROVIDER, autoSubmit: false, focusAfterPaste: true };
const NATIVE_HOST = 'com.captionrelay.host';
const NATIVE_RETRY_MS = 20000;
let lastNativeFetch = 0;

/**
 * Asks the CaptionRelay app (native messaging host, registered by the app's exe) for its port and token and
 * stores them, so nobody has to copy the token by hand. Returns true when stored settings changed. Throttled:
 * Chrome starts a process for every call.
 */
async function fetchFromApp(reason) {
  const now = Date.now();
  if (now - lastNativeFetch < NATIVE_RETRY_MS) return false;
  lastNativeFetch = now;
  let reply;
  try {
    reply = await chrome.runtime.sendNativeMessage(NATIVE_HOST, { type: 'getConfig' });
  } catch (e) {
    warn(`could not ask the app for the token (${reason}): ${e?.message ?? e}`);
    return false;
  }
  if (reply?.type !== 'config' || !reply.token) {
    warn(`app did not give a token (${reason}): ${reply?.reason ?? 'no reply'}`);
    return false;
  }
  const current = await chrome.storage.local.get(DEFAULTS);
  if (current.token === reply.token && current.port === reply.port) return false;
  log(`token and port received from the app (${reason})`);
  await chrome.storage.local.set({ token: reply.token, port: reply.port }); // onChanged reconnects
  return true;
}

async function getSettings() {
  const settings = await chrome.storage.local.get(DEFAULTS);
  if (!settings.token && (await fetchFromApp('no token'))) {
    return chrome.storage.local.get(DEFAULTS);
  }
  return settings;
}

/**
 * Delivers one send from the app. Paste only (default): merged with what the chat's input holds, not sent.
 * "Press Enter for me": pasted and sent. A held tap key (opts.submit) sends this one in either mode, merged first.
 */
async function deliver(text, id, opts = {}) {
  const settings = await getSettings();
  const provider = getProvider(settings.provider);
  const forced = opts.submit === true;
  const submit = forced || settings.autoSubmit === true;
  const merge = forced || !submit;
  log(`send ${String(id).slice(0, 8)}: ${text.length} chars -> ${provider.id}${forced ? ' (paste + send: key held)' : submit ? '' : ' (paste only)'}`);
  const onResult = (status, detail, submitted) => connection.sendResult(id, status, detail, provider.id, submitted);
  const focus = !submit && settings.focusAfterPaste !== false;
  const ack = await provider.deliver(text, { id, onResult, submit, merge, focus });
  log(`ack ${String(id).slice(0, 8)}: ${ack.status}${ack.detail ? ' (' + ack.detail + ')' : ''}`);
  return { provider: provider.id, ...ack };
}

/** Badge: green ON while the app is running and connected, grey OFF otherwise. */
function updateBadge(state) {
  const on = state === 'on';
  chrome.action.setBadgeText({ text: on ? 'ON' : 'OFF' });
  chrome.action.setBadgeBackgroundColor({ color: on ? '#2e9d4f' : '#6e7781' });
  chrome.action.setTitle({ title: on ? 'CaptionRelay: on (app running)' : 'CaptionRelay: off' });
}

/**
 * Popup switch ON: start the CaptionRelay app through the native messaging host, then connect. The app starts Live
 * Captions itself. Resolves with { ok, status | detail }.
 */
async function turnOn() {
  let reply;
  try {
    reply = await chrome.runtime.sendNativeMessage(NATIVE_HOST, { type: 'launch' });
  } catch (e) {
    warn('could not start the app:', String(e?.message ?? e));
    return { ok: false, detail: 'Could not start the app. Open CaptionRelay.exe once, then try again.' };
  }
  if (reply?.type !== 'launched') {
    return { ok: false, detail: reply?.reason ?? 'The app did not start.' };
  }
  log(`turn on: app ${reply.status}`);
  setTimeout(() => connection.restart(), 800); // the app needs a moment to open its port; backoff retries after that
  return { ok: true, status: reply.status };
}

/** Popup switch OFF: ask the app to exit (it closes Live Captions too). Resolves with { ok, detail? }. */
function turnOff() {
  if (connection.state !== 'on') return { ok: false, detail: 'The app is not running.' };
  log('turn off: asking the app to exit');
  return { ok: connection.sendQuit() };
}

const connection = new RelayConnection({
  WebSocketImpl: WebSocket,
  getSettings,
  onSend: deliver,
  onState: (state) => {
    updateBadge(state);
    if (state === 'on') {
      reportEnvironment();
      // Remember the app's hotkeys for the popup (shown even while disconnected).
      if (connection.app?.sendKey) {
        const { sendKey, skipKey, tapKey, tapWords } = connection.app;
        chrome.storage.local.set({ appKeys: { sendKey, skipKey, tapKey, tapWords } });
      }
    }
  },
  onAuthError: () => fetchFromApp('token refused'),
  logger: { log, warn },
  version: chrome.runtime.getManifest().version,
});

// Extension log lines also go to the app's log (only while connected).
setForwarder((level, text) => connection.sendLog(level, text));

/** On connect: browser version and the active provider's tabs we could deliver to (no titles or URLs). */
async function reportEnvironment() {
  try {
    const chromeVersion = /Chrome\/([\d.]+)/.exec(navigator.userAgent)?.[1] ?? '?';
    const provider = getProvider((await chrome.storage.local.get(DEFAULTS)).provider);
    const tabs = provider.urlPatterns ? await chrome.tabs.query({ url: provider.urlPatterns }) : [];
    const now = Date.now();
    const list = tabs
      .sort((a, b) => (b.lastAccessed ?? 0) - (a.lastAccessed ?? 0))
      .map((t) => `#${t.id}${t.active ? ' active' : ''}${t.discarded ? ' discarded' : ''}${t.frozen ? ' frozen' : ''} ${Math.round((now - (t.lastAccessed ?? now)) / 1000)}s ago`);
    log(`environment: Chrome ${chromeVersion}, ${provider.name} tabs: ${list.length ? list.join(', ') : 'none'}`);
  } catch (e) {
    warn('environment report failed', String(e));
  }
}

// Reconnect check every 30 s (the shortest alarm Chrome allows), so an app started from the desktop is found even
// when the service worker was asleep.
chrome.alarms.create('captionrelay-keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === 'captionrelay-keepalive') connection.ensure();
});

chrome.runtime.onStartup.addListener(() => connection.ensure());
chrome.runtime.onInstalled.addListener(() => connection.ensure());

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && (changes.port || changes.token || changes.provider)) {
    log('settings changed; reconnecting');
    connection.restart();
  }
});

// The toolbar icon opens popup.html (manifest "default_popup"); the full settings stay on the options page.

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === 'status') {
    connection.ensure(); // opening the popup also looks for an app that was just started
    sendResponse(connection.status());
  } else if (message?.type === 'turn-on') {
    turnOn().then(sendResponse);
    return true; // async response
  } else if (message?.type === 'turn-off') {
    sendResponse(turnOff());
  } else if (message?.type === 'set-tap-preview') {
    sendResponse({ ok: connection.sendSetTapPreview(message.value === true) });
  } else if (message?.type === 'reconnect') {
    connection.restart();
    sendResponse({ ok: true });
  } else if (message?.type === 'captionrelay-result') {
    // From a content script: queued sends were submitted (or given up) after the answer ended.
    getSettings().then((s) => {
      getProvider(s.provider).queueDone?.(_sender.tab?.id);
      for (const id of message.ids ?? []) connection.sendResult(id, message.status, message.detail, s.provider);
      log(`result for ${message.ids?.length ?? 0} queued send(s): ${message.status}${message.detail ? ' (' + message.detail + ')' : ''}`);
    });
    return false;
  } else if (message?.type === 'captionrelay-diag') {
    // From a content script: page diagnostics (no chat text).
    const line = `[tab ${_sender.tab?.id ?? '?'}] ${message.text}`;
    if (message.level === 'warn') warn(line);
    else log(line);
    return false;
  } else if (message?.type === 'test-insert') {
    getSettings()
      .then((s) => getProvider(s.provider).test('CaptionRelay test'))
      .then(sendResponse, (e) => sendResponse({ ok: false, detail: String(e?.message ?? e) }));
    return true; // async response
  }
  return false;
});

chrome.storage.local.remove('enabled'); // left over from the first popup version (switch = connect/disconnect)
updateBadge('off');
connection.start();
