// Service worker: keeps the WebSocket to the CaptionRelay app open and hands "send" messages to the active provider.
import { RelayConnection } from './lib/connection.js';
import { getProvider, DEFAULT_PROVIDER } from './providers/registry.js';
import { log, warn } from './lib/log.js';

const DEFAULTS = { port: 47321, token: '', provider: DEFAULT_PROVIDER };

async function getSettings() {
  return chrome.storage.local.get(DEFAULTS);
}

async function deliver(text, id) {
  const settings = await getSettings();
  const provider = getProvider(settings.provider);
  log(`send ${String(id).slice(0, 8)}: ${text.length} chars -> ${provider.id}`);
  const ack = await provider.deliver(text, { id });
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
  onState: updateBadge,
  logger: { log, warn },
  version: chrome.runtime.getManifest().version,
});

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
  }
  return false;
});

updateBadge('off');
connection.start();
