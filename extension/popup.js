// Toolbar popup: on/off switch, connection status, which chat receives the text, and the app's hotkeys.
import { listProviders, getProvider, DEFAULT_PROVIDER } from './providers/registry.js';

const DEFAULT_KEYS = { sendKey: 'Ctrl+Shift', skipKey: 'F9' };
const $ = (id) => document.getElementById(id);

/** "Ctrl+Shift" → <kbd>Ctrl</kbd> + <kbd>Shift</kbd>; "None" → "off". */
function renderCombo(el, combo) {
  el.replaceChildren();
  if (!combo || combo === 'None') {
    const s = document.createElement('span');
    s.className = 'none';
    s.textContent = 'not set';
    el.append(s);
    return;
  }
  combo.split('+').forEach((part, i) => {
    if (i > 0) el.append(' + ');
    const k = document.createElement('kbd');
    k.textContent = part;
    el.append(k);
  });
}

async function load() {
  const select = $('provider');
  for (const p of listProviders()) select.add(new Option(p.name, p.id));

  const s = await chrome.storage.local.get({ provider: DEFAULT_PROVIDER, enabled: true, appKeys: null });
  select.value = s.provider;
  $('providerName').textContent = getProvider(s.provider).name;
  $('enabled').checked = s.enabled !== false;
  showKeys(s.appKeys ?? DEFAULT_KEYS, !s.appKeys);
}

function showKeys(keys, guessed) {
  renderCombo($('sendKey'), keys.sendKey);
  renderCombo($('skipKey'), keys.skipKey);
  $('keysNote').textContent = guessed
    ? 'Default keys shown; the app reports yours once it connects. Press and release in any window.'
    : 'Press and release in any window; your focus stays where it is. Change them in the app\'s config.json.';
}

async function refresh() {
  let s;
  try {
    s = await chrome.runtime.sendMessage({ type: 'status' });
  } catch {
    s = null;
  }
  const box = $('status');
  const text = $('statusText');
  box.classList.remove('on', 'connecting', 'disabled');
  $('keys').classList.toggle('disabled', s?.enabled === false);

  if (!s) {
    text.textContent = 'Starting…';
  } else if (s.enabled === false) {
    box.classList.add('disabled');
    text.textContent = 'Turned off. Shortcuts send nothing; the app keeps the text.';
  } else if (s.state === 'on') {
    box.classList.add('on');
    text.textContent = 'On: connected to the CaptionRelay app';
  } else if (s.state === 'connecting') {
    box.classList.add('connecting');
    text.textContent = 'Connecting…';
  } else {
    text.textContent = s.lastError ? `Not connected: ${s.lastError}` : 'Not connected: is the CaptionRelay app running?';
  }

  if (s?.app?.sendKey) showKeys(s.app, false);
}

$('enabled').addEventListener('change', async (e) => {
  await chrome.storage.local.set({ enabled: e.target.checked });
  setTimeout(refresh, 150);
});

$('provider').addEventListener('change', async (e) => {
  await chrome.storage.local.set({ provider: e.target.value });
  $('providerName').textContent = getProvider(e.target.value).name;
});

$('settings').addEventListener('click', () => {
  chrome.runtime.openOptionsPage();
  window.close();
});

await load();
await refresh();
setInterval(refresh, 1000);
