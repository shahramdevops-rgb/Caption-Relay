// Service worker: keeps the WebSocket to the CaptionRelay app open and hands "send" messages to the active provider.
import { RelayConnection } from './lib/connection.js';
import { getProvider, DEFAULT_PROVIDER } from './providers/registry.js';
import { log, warn, setForwarder } from './lib/log.js';

const DEFAULTS = { port: 47321, token: '', provider: DEFAULT_PROVIDER };
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

async function deliver(text, id) {
  const settings = await getSettings();
  const provider = getProvider(settings.provider);
  log(`send ${String(id).slice(0, 8)}: ${text.length} chars -> ${provider.id}`);
  const onResult = (status, detail) => connection.sendResult(id, status, detail, provider.id);
  const ack = await provider.deliver(text, { id, onResult });
  log(`ack ${String(id).slice(0, 8)}: ${ack.status}${ack.detail ? ' (' + ack.detail + ')' : ''}`);
  return { provider: provider.id, ...ack };
}

// On/off switch in the popup. Off = disconnected from the app (the app keeps the text: "not connected").
let enabled = true;

async function loadEnabled() {
  enabled = (await chrome.storage.local.get({ enabled: true })).enabled !== false;
  return enabled;
}

function updateBadge(state) {
  if (!enabled) {
    chrome.action.setBadgeText({ text: 'OFF' });
    chrome.action.setBadgeBackgroundColor({ color: '#6e7781' });
    chrome.action.setTitle({ title: 'CaptionRelay: turned off' });
    return;
  }
  const on = state === 'on';
  chrome.action.setBadgeText({ text: on ? 'ON' : 'OFF' });
  chrome.action.setBadgeBackgroundColor({ color: on ? '#2e9d4f' : '#c62828' });
  chrome.action.setTitle({ title: on ? 'CaptionRelay: connected' : 'CaptionRelay: not connected' });
}

/** Connects unless the user turned CaptionRelay off in the popup. */
async function ensureIfEnabled() {
  if (await loadEnabled()) connection.ensure();
  else updateBadge('off');
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
      if (connection.app?.sendKey) chrome.storage.local.set({ appKeys: { sendKey: connection.app.sendKey, skipKey: connection.app.skipKey } });
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

// Reconnect check every 30 s, in case the worker was stopped and restarted without a socket.
chrome.alarms.create('captionrelay-keepalive', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === 'captionrelay-keepalive') ensureIfEnabled();
});

chrome.runtime.onStartup.addListener(() => ensureIfEnabled());
chrome.runtime.onInstalled.addListener(() => ensureIfEnabled());

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local') return;
  if (changes.enabled) {
    enabled = changes.enabled.newValue !== false;
    log(`turned ${enabled ? 'on' : 'off'} in the popup`);
    if (enabled) {
      connection.restart();
    } else {
      connection.stop();
      updateBadge('off');
    }
    return;
  }
  if (enabled && (changes.port || changes.token || changes.provider)) {
    log('settings changed; reconnecting');
    connection.restart();
  }
});

// The toolbar icon opens popup.html (manifest "default_popup"); the full settings stay on the options page.

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === 'status') {
    loadEnabled().then((on) => sendResponse({ ...connection.status(), enabled: on }));
    return true; // async response
  } else if (message?.type === 'reconnect') {
    if (enabled) connection.restart();
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

updateBadge('off');
ensureIfEnabled();
