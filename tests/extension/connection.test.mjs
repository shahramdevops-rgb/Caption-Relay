// Unit tests for extension/lib/connection.js. Run: node --test tests/extension/*.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RelayConnection, PROTOCOL } from '../../extension/lib/connection.js';

class FakeSocket {
  static all = [];
  constructor(url) {
    this.url = url;
    this.readyState = 0;
    this.sent = [];
    this.closed = false;
    FakeSocket.all.push(this);
  }
  send(data) { this.sent.push(JSON.parse(data)); }
  close() { this.closed = true; this.readyState = 3; }
  // test helpers
  open() { this.readyState = 1; this.onopen?.(); }
  receive(obj) { this.onmessage?.({ data: JSON.stringify(obj) }); }
  drop() { this.readyState = 3; this.onclose?.(); }
}

function rig({ token = 'secret', port = 47321, onSend, onAuthError } = {}) {
  FakeSocket.all = [];
  const timers = [];
  const states = [];
  let settings = { port, token, provider: 'gemini' };
  const conn = new RelayConnection({
    WebSocketImpl: FakeSocket,
    getSettings: async () => settings,
    onSend: onSend ?? (async () => ({ status: 'sent', provider: 'gemini' })),
    onState: (s) => states.push(s),
    onAuthError,
    version: '0.1.0',
    setTimeoutFn: (f, ms) => { const t = { f, ms, cancelled: false }; timers.push(t); return t; },
    clearTimeoutFn: (t) => { t.cancelled = true; },
  });
  return {
    conn, timers, states,
    setSettings: (s) => { settings = { ...settings, ...s }; },
    socket: () => FakeSocket.all.at(-1),
    sockets: () => FakeSocket.all,
    fire: async () => { const t = timers.filter((x) => !x.cancelled).shift(); t.cancelled = true; t.f(); await tick(); },
  };
}

const tick = () => new Promise((r) => setTimeout(r, 0));

test('opens ws://127.0.0.1:{port}/relay and sends hello with token, protocol and provider', async () => {
  const r = rig({ port: 50000 });
  r.conn.start();
  await tick();
  assert.equal(r.socket().url, 'ws://127.0.0.1:50000/relay');
  r.socket().open();
  assert.deepEqual(r.socket().sent[0], {
    type: 'hello', token: 'secret', version: '0.1.0', protocol: PROTOCOL, client: 'CaptionRelay extension 0.1.0', provider: 'gemini',
  });
});

test('welcome turns the state on', async () => {
  const r = rig();
  r.conn.start();
  await tick();
  r.socket().open();
  r.socket().receive({ type: 'welcome', app: 'CaptionRelay', version: '0.1.0', protocol: 1 });
  assert.equal(r.conn.state, 'on');
  assert.deepEqual(r.states, ['connecting', 'on']);
  assert.equal(r.conn.status().lastError, null);
});

test('ping is answered with pong', async () => {
  const r = rig();
  r.conn.start();
  await tick();
  r.socket().open();
  r.socket().receive({ type: 'ping' });
  assert.deepEqual(r.socket().sent.at(-1), { type: 'pong' });
});

test('send is delivered by the provider and acked with the same id', async () => {
  const seen = [];
  const r = rig({ onSend: async (text, id) => { seen.push([text, id]); return { status: 'queued', provider: 'gemini' }; } });
  r.conn.start();
  await tick();
  r.socket().open();
  r.socket().receive({ type: 'send', id: 'abc', text: 'What is a delegate?' });
  await tick();
  assert.deepEqual(seen, [['What is a delegate?', 'abc']]);
  assert.deepEqual(r.socket().sent.at(-1), { type: 'ack', id: 'abc', status: 'queued', provider: 'gemini' });
});

test('a provider exception becomes an error ack', async () => {
  const r = rig({ onSend: async () => { throw new Error('input not found'); } });
  r.conn.start();
  await tick();
  r.socket().open();
  r.socket().receive({ type: 'send', id: 'x1', text: 'hi' });
  await tick();
  const ack = r.socket().sent.at(-1);
  assert.equal(ack.status, 'error');
  assert.equal(ack.detail, 'input not found');
});

test('reconnects with backoff 1 s, 2 s, 5 s, 5 s; welcome resets it', async () => {
  const r = rig();
  r.conn.start();
  await tick();
  r.socket().drop(); // app not running
  assert.equal(r.timers.at(-1).ms, 1000);
  await r.fire();
  r.socket().drop();
  assert.equal(r.timers.at(-1).ms, 2000);
  await r.fire();
  r.socket().drop();
  assert.equal(r.timers.at(-1).ms, 5000);
  await r.fire();
  r.socket().drop();
  assert.equal(r.timers.at(-1).ms, 5000);
  await r.fire();
  r.socket().open();
  r.socket().receive({ type: 'welcome' });
  r.socket().drop(); // app restarted
  assert.equal(r.timers.at(-1).ms, 1000);
});

test('wrong token: error message is shown as lastError', async () => {
  const r = rig();
  r.conn.start();
  await tick();
  r.socket().open();
  r.socket().receive({ type: 'error', reason: 'bad token' });
  r.socket().drop();
  assert.match(r.conn.status().lastError, /Wrong token/);
  assert.equal(r.conn.state, 'off');
});

test('no token: does not connect, waits for settings', async () => {
  const r = rig({ token: '' });
  r.conn.start();
  await tick();
  assert.equal(r.sockets().length, 0);
  assert.match(r.conn.status().lastError, /Token not set/);
  assert.equal(r.timers.length, 0);
});

test('ensure() connects when off, and does nothing while connected or while a retry is scheduled', async () => {
  const r = rig();
  r.conn.ensure();
  await tick();
  assert.equal(r.sockets().length, 1);
  r.conn.ensure();
  await tick();
  assert.equal(r.sockets().length, 1);
  r.socket().drop();
  r.conn.ensure(); // retry already scheduled
  await tick();
  assert.equal(r.sockets().length, 1);
});

test('restart() drops the socket and connects with the new settings at once', async () => {
  const r = rig();
  r.conn.start();
  await tick();
  r.socket().open();
  r.socket().receive({ type: 'welcome' });
  const old = r.socket();
  r.setSettings({ port: 50001, token: 'new' });
  r.conn.restart();
  await tick();
  assert.equal(old.closed, true);
  assert.equal(r.socket().url, 'ws://127.0.0.1:50001/relay');
  old.drop(); // late close event of the old socket must not schedule a reconnect
  assert.equal(r.timers.filter((t) => !t.cancelled).length, 0);
  r.socket().open();
  assert.equal(r.socket().sent[0].token, 'new');
});

test('sendLog sends a log message only while connected', async () => {
  const r = rig();
  assert.equal(r.conn.sendLog('info', 'early'), false);
  r.conn.start();
  await tick();
  r.socket().open();
  assert.equal(r.conn.sendLog('warn', 'buttons=[x]'), true);
  assert.deepEqual(r.socket().sent.at(-1), { type: 'log', level: 'warn', text: 'buttons=[x]' });
});

test('sendResult reports the final result of a queued send', async () => {
  const r = rig();
  r.conn.start();
  await tick();
  r.socket().open();
  r.conn.sendResult('q1', 'sent', 'execCommand', 'gemini');
  r.conn.sendResult('q2', 'busy-or-anything', 'still answering after 3 min', 'gemini');
  assert.deepEqual(r.socket().sent.at(-2), { type: 'result', id: 'q1', status: 'sent', detail: 'execCommand', provider: 'gemini' });
  assert.equal(r.socket().sent.at(-1).status, 'error');
});

test('unknown messages and invalid JSON are ignored', async () => {
  const r = rig();
  r.conn.start();
  await tick();
  r.socket().open();
  r.socket().receive({ type: 'something-new' });
  r.socket().onmessage({ data: 'not json' });
  assert.equal(r.socket().sent.length, 1); // only the hello
});

test('a refused token calls onAuthError (so the extension can fetch a fresh one); other errors do not', async () => {
  let calls = 0;
  const r = rig({ onAuthError: () => calls++ });
  r.conn.start();
  await tick();
  r.socket().open();
  r.socket().receive({ type: 'error', reason: 'protocol' });
  assert.equal(calls, 0);
  r.socket().receive({ type: 'error', reason: 'bad token' });
  assert.equal(calls, 1);
  assert.match(r.conn.status().lastError, /Wrong token/);
});
