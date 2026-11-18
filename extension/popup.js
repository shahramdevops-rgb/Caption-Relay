// Toolbar popup: the switch starts / closes the CaptionRelay app (and with it Live Captions), plus status, which
// chat receives the text, whether it is only pasted or also submitted, and the app's hotkeys. The switch shows ON exactly while the app is running and connected,
// so starting or closing the app any other way (desktop, taskbar, tray menu) is reflected here too.
import { listProviders, getProvider, DEFAULT_PROVIDER } from './providers/registry.js';

const DEFAULT_KEYS = { sendKey: 'Num -', skipKey: 'F9', tapKey: 'Num +', tapWords: 15 };
const START_TIMEOUT_MS = 20000;
const STOP_TIMEOUT_MS = 8000;
const $ = (id) => document.getElementById(id);

// A switch flip in progress: { want: 'on' | 'off', until: timestamp }.
let pending = null;
let message = null; // error from the last flip, shown until the next one
let tapPreviewPendingUntil = 0; // after a click, keep showing the new value until the app confirms it

/** "Ctrl+Shift" → <kbd>Ctrl</kbd> + <kbd>Shift</kbd>; "Num +" → one <kbd>; "None" → "not set". */
function renderCombo(el, combo) {
  el.replaceChildren();
  if (!combo || combo === 'None') {
    const s = document.createElement('span');
    s.className = 'none';
    s.textContent = 'not set';
    el.append(s);
    return;
  }
  const parts = /^\w+(\+\w+)+$/.test(combo) ? combo.split('+') : [combo]; // "Num +" is one key
  parts.forEach((part, i) => {
    if (i > 0) el.append(' + ');
    const k = document.createElement('kbd');
    k.textContent = part;
    el.append(k);
  });
}

/** What the send key does: paste only (default) or paste and submit. */
function showSendWhat(providerId, autoSubmit) {
  const name = getProvider(providerId).name;
  const el = $('sendWhat');
  el.replaceChildren();
  const b = document.createElement('b');
  b.textContent = autoSubmit ? 'Send' : 'Paste';
  el.append(b, autoSubmit
    ? ` everything said since the last skip to ${name}`
    : ` everything said since the last skip into the ${name} input (not sent)`);
}

function showKeys(keys, guessed) {
  renderCombo($('sendKey'), keys.sendKey);
  renderCombo($('skipKey'), keys.skipKey);
  // Older app versions do not report a tap key: hide the row then.
  const tapKey = keys.tapKey === undefined ? DEFAULT_KEYS.tapKey : keys.tapKey;
  $('tapRow').hidden = tapKey === null;
  renderCombo($('tapKey'), tapKey);
  const n = keys.tapWords ?? DEFAULT_KEYS.tapWords;
  const b = document.createElement('b');
  b.textContent = 'Tap';
  const hold = document.createElement('b');
  hold.textContent = 'Hold';
  $('tapWhat').replaceChildren(b, `: the last ${n} words; tap again for ${n} more. `, hold, ': paste and send');
  $('keysNote').textContent = guessed
    ? 'Default keys shown; the app reports yours once it runs. Press and release in any window.'
    : 'Press and release in any window; your focus stays where it is. Change them in the app\'s config.json.';
}

async function load() {
  const select = $('provider');
  for (const p of listProviders()) select.add(new Option(p.name, p.id));

  const s = await chrome.storage.local.get({ provider: DEFAULT_PROVIDER, appKeys: null, autoSubmit: false });
  select.value = s.provider;
  $('autoSubmit').checked = s.autoSubmit === true;
  showSendWhat(s.provider, s.autoSubmit === true);
  showKeys(s.appKeys ?? DEFAULT_KEYS, !s.appKeys);
}

async function refresh() {
  let s;
  try {
    s = await chrome.runtime.sendMessage({ type: 'status' });
  } catch {
    s = null;
  }
  const on = s?.state === 'on';

  if (pending && ((pending.want === 'on') === on || Date.now() > pending.until)) {
    if ((pending.want === 'on') !== on) {
      message = pending.want === 'on'
        ? 'The app did not come up. Open CaptionRelay.exe yourself, or check its log.'
        : 'The app is still running. Close it from its taskbar window or tray icon.';
    }
    pending = null;
  }

  const box = $('status');
  const text = $('statusText');
  box.classList.remove('on', 'connecting', 'disabled');
  $('enabled').checked = pending ? pending.want === 'on' : on;
  $('enabled').disabled = pending !== null;
  $('keys').classList.toggle('disabled', !on);

  if (!s) {
    text.textContent = 'Starting…';
  } else if (pending?.want === 'on') {
    box.classList.add('connecting');
    text.textContent = 'Starting CaptionRelay and Live Captions…';
  } else if (pending?.want === 'off') {
    box.classList.add('connecting');
    text.textContent = 'Closing CaptionRelay and Live Captions…';
  } else if (on) {
    box.classList.add('on');
    text.textContent = 'On: CaptionRelay is running';
  } else if (message) {
    text.textContent = message;
  } else if (s.lastError) {
    text.textContent = `Off: ${s.lastError}`;
  } else {
    box.classList.add('disabled');
    text.textContent = 'Off: CaptionRelay is not running. Turn on to start it.';
  }

  if (s?.app?.sendKey) showKeys(s.app, false);

  // Tap preview switch: the app's setting, so only while the app is running.
  const tp = $('tapPreview');
  tp.disabled = !on || typeof s?.app?.tapPreview !== 'boolean';
  if (!tp.disabled && Date.now() > tapPreviewPendingUntil) tp.checked = s.app.tapPreview;
  $('tapPreviewRow').classList.toggle('disabled', tp.disabled);
}

$('enabled').addEventListener('change', async (e) => {
  const want = e.target.checked ? 'on' : 'off';
  message = null;
  pending = { want, until: Date.now() + (want === 'on' ? START_TIMEOUT_MS : STOP_TIMEOUT_MS) };
  refresh();
  let r;
  try {
    r = await chrome.runtime.sendMessage({ type: want === 'on' ? 'turn-on' : 'turn-off' });
  } catch (err) {
    r = { ok: false, detail: String(err?.message ?? err) };
  }
  if (!r?.ok) {
    pending = null;
    message = r?.detail ?? 'Something went wrong.';
  }
  refresh();
});

$('tapPreview').addEventListener('change', async (e) => {
  tapPreviewPendingUntil = Date.now() + 1500;
  try {
    await chrome.runtime.sendMessage({ type: 'set-tap-preview', value: e.target.checked });
  } catch {
    // the next refresh shows the real state
  }
});

$('provider').addEventListener('change', async (e) => {
  await chrome.storage.local.set({ provider: e.target.value });
  showSendWhat(e.target.value, $('autoSubmit').checked);
});

$('autoSubmit').addEventListener('change', async (e) => {
  await chrome.storage.local.set({ autoSubmit: e.target.checked });
  showSendWhat($('provider').value, e.target.checked);
});

$('settings').addEventListener('click', () => {
  chrome.runtime.openOptionsPage();
  window.close();
});

await load();
await refresh();
setInterval(refresh, 500);
