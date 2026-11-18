// WebSocket connection to the CaptionRelay Windows app (see docs/protocol.md).
// Plain JS, no chrome.* calls here, so it can be tested in Node with a fake WebSocket.

export const PROTOCOL = 1;
export const DEFAULT_BACKOFF_MS = [1000, 2000, 5000];

export class RelayConnection {
  /**
   * @param {object} o
   * @param {typeof WebSocket} o.WebSocketImpl
   * @param {() => Promise<{port:number, token:string, provider:string}>} o.getSettings
   * @param {(text:string, id:string) => Promise<{status:string, detail?:string, provider?:string}>} o.onSend
   * @param {(state:string) => void} [o.onState]  'off' | 'connecting' | 'on'
   * @param {() => void} [o.onAuthError]  the app refused the token (the caller may fetch a fresh one)
   * @param {{log:Function, warn:Function}} [o.logger]
   * @param {string} [o.version]
   * @param {number[]} [o.backoffMs]
   * @param {Function} [o.setTimeoutFn]
   * @param {Function} [o.clearTimeoutFn]
   */
  constructor(o) {
    this.WebSocketImpl = o.WebSocketImpl;
    this.getSettings = o.getSettings;
    this.onSend = o.onSend;
    this.onState = o.onState ?? (() => {});
    this.onAuthError = o.onAuthError ?? (() => {});
    this.logger = o.logger ?? { log() {}, warn() {} };
    this.version = o.version ?? '0.0.0';
    this.backoffMs = o.backoffMs ?? DEFAULT_BACKOFF_MS;
    this.setTimeoutFn = o.setTimeoutFn ?? ((f, ms) => setTimeout(f, ms));
    this.clearTimeoutFn = o.clearTimeoutFn ?? ((t) => clearTimeout(t));

    this.ws = null;
    this.state = 'off';
    this.attempt = 0;
    this.timer = null;
    this.lastError = null;
    this.connectedAt = null;
    this.app = null; // from the last welcome: { name, version, sendKey, skipKey, tapKey, tapWords }
    this.stopped = false;
    this.generation = 0; // ignores events of sockets replaced by restart()
  }

  /** Connects unless already connected/connecting or a reconnect is scheduled. */
  ensure() {
    this.stopped = false;
    if (this.state !== 'off' || this.timer !== null) return;
    this.connect();
  }

  start() {
    this.ensure();
  }

  /** Settings changed or the user asked to reconnect: drop the socket and connect again now. */
  restart() {
    this.stopped = false;
    this.attempt = 0;
    this.cancelTimer();
    this.closeSocket();
    this.setState('off');
    this.connect();
  }

  stop() {
    this.stopped = true;
    this.cancelTimer();
    this.closeSocket();
    this.setState('off');
  }

  status() {
    return { state: this.state, lastError: this.lastError, connectedAt: this.connectedAt, attempt: this.attempt, app: this.app };
  }

  async connect() {
    const generation = ++this.generation;
    this.setState('connecting');
    let settings;
    try {
      settings = await this.getSettings();
    } catch (e) {
      this.lastError = 'Could not read settings: ' + e;
      this.setState('off');
      this.scheduleReconnect();
      return;
    }
    if (generation !== this.generation) return;

    if (!settings.token) {
      this.lastError = 'Token not set (start the CaptionRelay app, or paste the token in the extension options)';
      this.setState('off');
      return; // wait for a settings change
    }

    const url = `ws://127.0.0.1:${settings.port}/relay`;
    let ws;
    try {
      ws = new this.WebSocketImpl(url);
    } catch (e) {
      this.lastError = 'WebSocket failed: ' + e;
      this.setState('off');
      this.scheduleReconnect();
      return;
    }
    this.ws = ws;

    ws.onopen = () => {
      if (generation !== this.generation) return;
      this.sendJson({
        type: 'hello',
        token: settings.token,
        version: this.version,
        protocol: PROTOCOL,
        client: `CaptionRelay extension ${this.version}`,
        provider: settings.provider,
      });
    };

    ws.onmessage = (event) => {
      if (generation !== this.generation) return;
      this.handleMessage(event.data);
    };

    ws.onclose = () => {
      if (generation !== this.generation) return;
      const wasOn = this.state === 'on';
      this.ws = null;
      this.connectedAt = null;
      this.setState('off');
      if (wasOn) this.logger.log('disconnected from the app');
      this.scheduleReconnect();
    };

    ws.onerror = () => {
      // onclose follows; nothing else to do (the app may simply not be running).
    };
  }

  async handleMessage(data) {
    let m;
    try {
      m = JSON.parse(data);
    } catch {
      this.logger.warn('invalid JSON from the app');
      return;
    }

    switch (m.type) {
      case 'welcome':
        this.attempt = 0;
        this.lastError = null;
        this.connectedAt = Date.now();
        this.app = {
          name: m.app,
          version: m.version,
          sendKey: m.sendKey ?? null,
          skipKey: m.skipKey ?? null,
          tapKey: m.tapKey ?? null,
          tapWords: m.tapWords ?? null,
          tapPreview: typeof m.tapPreview === 'boolean' ? m.tapPreview : null,
        };
        this.setState('on');
        this.logger.log(`connected to ${m.app} ${m.version} (protocol ${m.protocol})`);
        break;
      case 'error':
        this.lastError = m.reason === 'bad token' ? 'Wrong token (copy it again from the CaptionRelay tray menu)' : `App error: ${m.reason}`;
        this.logger.warn('app refused the connection:', m.reason);
        if (m.reason === 'bad token') this.onAuthError();
        break;
      case 'ping':
        this.sendJson({ type: 'pong' });
        break;
      case 'pong':
        break;
      case 'send':
        await this.handleSend(m);
        break;
      case 'settings':
        // The app's settings changed (e.g. the tap preview turned on in its taskbar window).
        if (this.app && typeof m.tapPreview === 'boolean') this.app.tapPreview = m.tapPreview;
        break;
      default:
        break; // forward compatible
    }
  }

  async handleSend(m) {
    let ack;
    try {
      // submit: true = the tap key was held: paste AND send this one, whatever the paste-only setting says.
      ack = await this.onSend(m.text ?? '', m.id, { submit: m.submit === true });
    } catch (e) {
      ack = { status: 'error', detail: String(e?.message ?? e) };
    }
    this.sendJson({ type: 'ack', id: m.id, status: ack.status, detail: ack.detail, provider: ack.provider, submitted: ack.submitted, alreadyInInput: ack.alreadyInInput });
  }

  /** Final result of a send that was acked "queued" (submitted false = only pasted into the input). */
  sendResult(id, status, detail, provider, submitted) {
    return this.sendJson({ type: 'result', id, status: status === 'sent' ? 'sent' : 'error', detail, provider, submitted });
  }

  /** Popup switch: turn the app's tap preview on or off. Returns false when not connected. */
  sendSetTapPreview(on) {
    const ok = this.sendJson({ type: 'setTapPreview', value: !!on });
    if (ok && this.app) this.app.tapPreview = !!on;
    return ok;
  }

  /** Asks the app to exit (popup switch off). Returns false when not connected. */
  sendQuit() {
    return this.sendJson({ type: 'quit' });
  }

  /** Diagnostics into the app's log (only when connected; never chat text). */
  sendLog(level, text) {
    return this.sendJson({ type: 'log', level, text: String(text).slice(0, 4000) });
  }

  sendJson(obj) {
    if (this.ws && this.ws.readyState === 1) {
      this.ws.send(JSON.stringify(obj));
      return true;
    }
    return false;
  }

  scheduleReconnect() {
    if (this.stopped || this.timer !== null) return;
    const delay = this.backoffMs[Math.min(this.attempt, this.backoffMs.length - 1)];
    this.attempt++;
    this.timer = this.setTimeoutFn(() => {
      this.timer = null;
      this.connect();
    }, delay);
  }

  cancelTimer() {
    if (this.timer !== null) {
      this.clearTimeoutFn(this.timer);
      this.timer = null;
    }
  }

  closeSocket() {
    this.generation++;
    if (this.ws) {
      try {
        this.ws.close();
      } catch {
        // ignore
      }
      this.ws = null;
    }
    this.connectedAt = null;
  }

  setState(state) {
    if (this.state === state) return;
    this.state = state;
    this.onState(state);
  }
}
