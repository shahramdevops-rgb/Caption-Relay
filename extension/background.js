// Service worker: keeps the WebSocket to the CaptionRelay app open and hands "send" messages to the active provider.
import { RelayConnection } from './lib/connection.js';
import { getProvider, DEFAULT_PROVIDER } from './providers/registry.js';
import { log, warn, setForwarder } from './lib/log.js';

const DEFAULTS = { port: 47321, token: '', provider: DEFAULT_PROVIDER };

async function getSettings() {
  return chrome.storage.local.get(DEFAULTS);
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

function updateBadge(state) {
  const on = state === 'on';
  chrome.action.setBadgeText({ text: on ? 'ON' : 'OFF' });
  chrome.action.setBadgeBackgroundColor({ color: on ? '#2e9d4f' : '#c62828' });
  chrome.action.setTitle({ title: on ? 'CaptionRelay: connected' : 'CaptionRelay: not connected' });
}

const connection = new RelayConnection({
  WebSocketImpl: WebSocket,
  getSettings,
  onSend: deliver,
  onState: (state) => {
    updateBadge(state);
    if (state === 'on') reportEnvironment();
  },
  logger: { log, warn },
  version: chrome.runtime.getManifest().version,
});

// Extension log lines also go to the app's log (only while connected).
setForwarder((level, text) => connection.sendLog(level, text));

/** On connect: browser version and the Gemini tabs we could deliver to (no titles or URLs beyond the host). */
async function reportEnvironment() {
  try {
    const chromeVersion = /Chrome\/([\d.]+)/.exec(navigator.userAgent)?.[1] ?? '?';
    const tabs = await chrome.tabs.query({ url: 'https://gemini.google.com/*' });
    const now = Date.now();
    const list = tabs
      .sort((a, b) => (b.lastAccessed ?? 0) - (a.lastAccessed ?? 0))
      .map((t) => `#${t.id}${t.active ? ' active' : ''}${t.discarded ? ' discarded' : ''}${t.frozen ? ' frozen' : ''} ${Math.round((now - (t.lastAccessed ?? now)) / 1000)}s ago`);
    log(`environment: Chrome ${chromeVersion}, Gemini tabs: ${list.length ? list.join(', ') : 'none'}`);
  } catch (e) {
    warn('environment report failed', String(e));
  }
}

// Reconnect check every 30 s, in case the worker was stopped and restarted without a socket.
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

chrome.action.onClicked.addListener(() => chrome.runtime.openOptionsPage());

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === 'status') {
    sendResponse(connection.status());
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

updateBadge('off');
connection.start();
