import { listProviders, DEFAULT_PROVIDER } from './providers/registry.js';

const DEFAULTS = { port: 47321, token: '', provider: DEFAULT_PROVIDER };
const $ = (id) => document.getElementById(id);

async function load() {
  const providerSelect = $('provider');
  for (const p of listProviders()) {
    providerSelect.add(new Option(p.name, p.id));
  }
  for (const later of ['ChatGPT', 'Claude', 'OpenAI API']) {
    const o = new Option(`${later} (later)`, '');
    o.disabled = true;
    providerSelect.add(o);
  }

  const s = await chrome.storage.local.get(DEFAULTS);
  $('port').value = s.port;
  $('token').value = s.token;
  providerSelect.value = s.provider;
}

async function save(event) {
  event.preventDefault();
  const port = Number($('port').value);
  const token = $('token').value.trim();
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    $('saved').textContent = 'Port must be 1024–65535';
    return;
  }
  await chrome.storage.local.set({ port, token, provider: $('provider').value || DEFAULT_PROVIDER });
  $('saved').textContent = 'Saved';
  setTimeout(() => ($('saved').textContent = ''), 2000);
}

async function refreshStatus() {
  let s;
  try {
    s = await chrome.runtime.sendMessage({ type: 'status' });
  } catch {
    s = null;
  }
  const box = $('status');
  box.classList.remove('on', 'connecting');
  if (!s) {
    $('statusText').textContent = 'Extension background is starting…';
    return;
  }
  if (s.state === 'on') {
    box.classList.add('on');
    $('statusText').textContent = 'Connected to the CaptionRelay app';
  } else if (s.state === 'connecting') {
    box.classList.add('connecting');
    $('statusText').textContent = 'Connecting…';
  } else {
    $('statusText').textContent = s.lastError
      ? `Not connected: ${s.lastError}`
      : 'Not connected (is the CaptionRelay app running?)';
  }
}

async function runTest() {
  $('testResult').textContent = 'Testing…';
  let r;
  try {
    r = await chrome.runtime.sendMessage({ type: 'test-insert' });
  } catch (e) {
    r = { ok: false, detail: String(e) };
  }
  const lines = [(r?.ok ? '✓ ' : '✗ ') + (r?.detail ?? 'no answer')];
  if (r?.report) {
    for (const [name, value] of Object.entries(r.report)) {
      if (Array.isArray(value)) {
        lines.push(`${name}:`);
        for (const item of value) lines.push(`  ${item}`);
      } else {
        lines.push(`${name.padEnd(15)} ${value === null ? '— not found' : value}`);
      }
    }
  }
  $('testResult').textContent = lines.join('\n');
}

$('form').addEventListener('submit', save);
$('test').addEventListener('click', runTest);
$('showToken').addEventListener('change', (e) => ($('token').type = e.target.checked ? 'text' : 'password'));
$('reconnect').addEventListener('click', () => chrome.runtime.sendMessage({ type: 'reconnect' }));

load();
refreshStatus();
setInterval(refreshStatus, 1000);
