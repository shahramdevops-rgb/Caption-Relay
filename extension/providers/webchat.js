// Shared delivery logic for web-chat providers (Gemini, ChatGPT; Claude later), run as a content script.
// Each provider only supplies a selector set (see providers/<id>/selectors.js).
// Always (re)defined: after an extension reload an old copy may still be in the page; it must be replaced.
(() => {

  const TAG = '[CaptionRelay]';
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  /**
   * Resolves with cond()'s first truthy value, or null after ms. Re-checks on every DOM change (MutationObserver)
   * instead of short timers: in a hidden tab Chrome delays timers to ~1 s (or more), DOM observers are not delayed.
   * (The deadline timer itself may fire late in a hidden tab; that only matters on failure.)
   */
  function waitFor(cond, ms) {
    const first = cond();
    if (first) return Promise.resolve(first);
    return new Promise((resolve) => {
      let done = false;
      const finish = (value) => {
        if (done) return;
        done = true;
        observer.disconnect();
        clearTimeout(timer);
        resolve(value);
      };
      const observer = new MutationObserver(() => {
        const v = cond();
        if (v) finish(v);
      });
      observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
      const timer = setTimeout(() => finish(cond() || null), ms);
    });
  }
  const norm = (s) => (s || '').replace(/​/g, '').replace(/\s+/g, ' ').trim();

  function visible(el) {
    if (!el || !el.isConnected) return false;
    const r = el.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none';
  }

  /** First visible element for a selector list; returns { el, selector } or null. */
  function find(list, root = document) {
    for (const selector of list || []) {
      for (const el of root.querySelectorAll(selector)) {
        if (visible(el)) return { el, selector };
      }
    }
    return null;
  }

  function count(list) {
    for (const selector of list || []) {
      const n = document.querySelectorAll(selector).length;
      if (n > 0) return n;
    }
    return 0;
  }

  function boxText(box) {
    return norm(box.innerText);
  }

  function isEnabled(button) {
    return button && !button.disabled && button.getAttribute('aria-disabled') !== 'true';
  }

  function isBusy(sel) {
    return !!find(sel.stopButton);
  }

  function selectAll(box) {
    box.focus();
    const range = document.createRange();
    range.selectNodeContents(box);
    const s = window.getSelection();
    s.removeAllRanges();
    s.addRange(range);
  }

  /** Puts `full` into the box (replacing what is there), trying three methods; returns the method used or null. */
  async function insert(box, full) {
    const want = norm(full);
    const ok = () => boxText(box).includes(want);

    // 1) focus + select + execCommand('insertText').
    try {
      selectAll(box);
      document.execCommand('insertText', false, full);
      if (await waitFor(ok, 150)) return 'execCommand';
    } catch (e) {
      console.warn(TAG, 'execCommand failed', e);
    }

    // 2) synthetic paste with a DataTransfer.
    try {
      selectAll(box);
      const dt = new DataTransfer();
      dt.setData('text/plain', full);
      box.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
      if (await waitFor(ok, 150)) return 'paste';
    } catch (e) {
      console.warn(TAG, 'paste failed', e);
    }

    // 3) set the content directly and dispatch input.
    try {
      box.textContent = '';
      for (const line of full.split('\n')) {
        const p = document.createElement('p');
        p.textContent = line || ' ';
        box.appendChild(p);
      }
      box.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: full }));
      if (await waitFor(ok, 150)) return 'innerText';
    } catch (e) {
      console.warn(TAG, 'direct set failed', e);
    }
    return null;
  }

  // Only buttons that look like "send": the container also has microphone / upload / tools buttons.
  const SEND_LIKE = /send|submit|ارسال|فرستادن/i;
  function looksLikeSend(btn) {
    // (btn.type is not used: every <button> defaults to type "submit".)
    const text = [btn.className, btn.getAttribute('aria-label'), btn.getAttribute('data-test-id'), btn.getAttribute('data-testid'), btn.getAttribute('mattooltip')]
      .filter(Boolean)
      .join(' ');
    return SEND_LIKE.test(text);
  }

  /** Send-like button inside the input's container that is closest to the box (fallback when no selector matches). */
  function closestButton(sel, box) {
    let container = null;
    for (const s of sel.inputContainer || []) {
      container = box.closest(s);
      if (container) break;
    }
    container = container || box.parentElement?.parentElement?.parentElement;
    if (!container) return null;
    const b = box.getBoundingClientRect();
    let best = null;
    let bestD = Infinity;
    for (const btn of container.querySelectorAll('button')) {
      if (!visible(btn) || !isEnabled(btn) || !looksLikeSend(btn)) continue;
      const r = btn.getBoundingClientRect();
      const d = Math.hypot(r.left - b.right, r.top - b.top);
      if (d < bestD) {
        best = btn;
        bestD = d;
      }
    }
    return best;
  }

  async function submit(sel, box, waitMs) {
    const found = await waitFor(() => {
      const f = find(sel.sendButton);
      return f && isEnabled(f.el) ? f : null;
    }, waitMs);
    if (found) {
      found.el.click();
      return 'button ' + found.selector;
    }
    const near = closestButton(sel, box);
    if (near) {
      near.click();
      return 'closest button';
    }
    box.focus();
    for (const type of ['keydown', 'keypress', 'keyup']) {
      box.dispatchEvent(new KeyboardEvent(type, { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
    }
    return 'Enter key';
  }

  /** True once the box was emptied, a new user turn appeared, or generation started. */
  async function appeared(sel, box, turnsBefore, waitMs) {
    return waitFor(() => {
      if (count(sel.userTurn) > turnsBefore) return 'new user turn';
      if (boxText(box) === '') return 'input emptied';
      if (isBusy(sel)) return 'generation started';
      return null;
    }, waitMs);
  }

  function findBox(sel) {
    const found = find(sel.input);
    if (found) return found;
    return null;
  }

  function missingBoxError(sel) {
    return find(sel.signedOut) ? 'not logged in' : 'input not found';
  }

  /**
   * Inserts `text` (after the user's draft, if any) and submits it.
   * Returns an ack: { status: 'sent' | 'error', detail? }.
   */
  /** One line of page state for the app log: busy, visibility, focus, turns and the buttons near the box. */
  function snapshot(sel) {
    const d = diagnose(sel);
    return `busy=${d.busy} visibility=${d.visibility} focus=${d.hasFocus} userTurns=${d.userTurns} stopButton=${d.stopButton} buttons=[${d.buttons.join(' | ')}]`;
  }

  async function deliver(sel, text, opts = {}) {
    const t0 = performance.now();
    const diag = opts.onDiag ?? (() => {});
    // Right after a (re)load the page may still be hydrating (ChatGPT first shows a plain placeholder): wait a moment.
    let found = findBox(sel);
    if (!found && !find(sel.signedOut)) found = await waitFor(() => findBox(sel), opts.inputWaitMs ?? 3000);
    if (!found) {
      diag(`deliver: no input. ${snapshot(sel)}`);
      return { status: 'error', detail: missingBoxError(sel) };
    }
    const box = found.el;
    diag(`deliver start: ${snapshot(sel)}`);

    if (isBusy(sel)) return { status: 'busy' };

    const draft = boxText(box);
    const full = draft ? `${box.innerText.trim()}\n\n${text}` : text;
    const method = await insert(box, full);
    if (!method) return { status: 'error', detail: 'could not insert text into the input' };

    const turnsBefore = count(sel.userTurn);
    const how = await submit(sel, box, opts.buttonWaitMs ?? 1500);
    const proof = await appeared(sel, box, turnsBefore, opts.verifyMs ?? 1200);
    const ms = Math.round(performance.now() - t0);
    console.log(TAG, `deliver: insert=${method}, submit=${how}, verified=${proof}, draft=${draft.length > 0}, ${ms} ms`);
    if (!proof) {
      diag(`deliver failed: ${snapshot(sel)}`);
      return { status: 'error', detail: how === 'Enter key' ? 'send button not found' : 'message did not appear after submit' };
    }
    setTimeout(() => diag(`1 s after send: ${snapshot(sel)}`), 1000);
    return { status: 'sent', detail: `${method}, ${how}, ${proof}, ${ms} ms` };
  }

  // ---- Busy handling: queue while the chat is answering, merge, submit when the answer ends ----

  const queue = { items: [], startedAt: 0, observer: null, poll: null, flushing: false, opts: null, sel: null };

  /**
   * Delivers now, or — while the chat is still answering (or earlier texts are waiting) — queues the text and
   * acks "queued". Queued texts are merged (blank line between them) into ONE message, submitted as soon as the
   * answer ends; opts.onResult(ids, ack) then reports the final result of every queued id.
   */
  async function deliverOrQueue(sel, text, id, opts = {}) {
    const diag = opts.onDiag ?? (() => {});
    if (queue.items.length > 0 || queue.flushing || isBusy(sel)) {
      return enqueue(sel, text, id, opts, diag);
    }
    const ack = await deliver(sel, text, opts);
    if (ack.status === 'busy') return enqueue(sel, text, id, opts, diag); // started answering just now
    return ack;
  }

  function enqueue(sel, text, id, opts, diag) {
    queue.items.push({ id, text });
    queue.startedAt ||= Date.now();
    queue.sel = sel;
    queue.opts = opts;
    diag(`queued ${String(id).slice(0, 8)}: ${queue.items.length} waiting for the answer to finish`);
    watch();
    return { status: 'queued', detail: `waiting for the answer to finish (${queue.items.length} queued)` };
  }

  function watch() {
    if (queue.observer) return;
    queue.observer = new MutationObserver(() => check());
    queue.observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class', 'aria-label', 'disabled'] });
    queue.poll = setInterval(check, queue.opts?.pollMs ?? 250); // throttled to 1/s in a hidden tab; the observer is not
    check();
  }

  function unwatch() {
    queue.observer?.disconnect();
    queue.observer = null;
    clearInterval(queue.poll);
    queue.poll = null;
  }

  function check() {
    if (queue.flushing || queue.items.length === 0) return;
    const opts = queue.opts ?? {};
    if (isBusy(queue.sel)) {
      const maxWait = opts.maxWaitMs ?? 180000;
      if (Date.now() - queue.startedAt > maxWait) {
        const items = queue.items.splice(0);
        queue.startedAt = 0;
        unwatch();
        const waited = maxWait >= 60000 ? `${Math.round(maxWait / 60000)} min` : `${Math.round(maxWait / 1000)} s`;
        opts.onDiag?.(`gave up on ${items.length} queued after ${waited}`);
        opts.onResult?.(items.map((i) => i.id), { status: 'error', detail: `still answering after ${waited}` });
      }
      return;
    }
    flush();
  }

  async function flush() {
    queue.flushing = true;
    unwatch();
    const opts = queue.opts ?? {};
    await sleep(opts.settleMs ?? 300); // let the page switch the stop button back
    if (isBusy(queue.sel)) {
      queue.flushing = false;
      watch();
      return;
    }
    const items = queue.items.splice(0);
    const waited = Date.now() - queue.startedAt;
    queue.startedAt = 0;
    const merged = items.map((i) => i.text).join('\n\n');
    const ack = await deliver(queue.sel, merged, opts);
    queue.flushing = false;
    if (ack.status === 'busy') {
      queue.items.unshift(...items);
      queue.startedAt = Date.now() - waited;
      watch();
      return;
    }
    opts.onDiag?.(`flushed ${items.length} queued after ${Math.round(waited / 1000)} s: ${ack.status}`);
    opts.onResult?.(items.map((i) => i.id), ack);
    if (queue.items.length > 0) watch(); // more arrived while submitting
  }

  /** Options "Test": inserts text without submitting and reports which selectors matched. */
  async function test(sel, text) {
    const report = diagnose(sel);
    const found = findBox(sel);
    if (!found) return { ok: false, detail: missingBoxError(sel), report };
    const box = found.el;
    const draft = boxText(box);
    const full = draft ? `${box.innerText.trim()}\n\n${text}` : text;
    const method = await insert(box, full);
    await sleep(300); // the send button appears once the box has text
    const after = find(sel.sendButton);
    report.sendButtonAfterInsert = after ? after.selector : null;
    report.sendButtonEnabled = after ? isEnabled(after.el) : false;
    report.closestSendLikeButton = closestButton(sel, box) ? 'found' : null;
    return { ok: !!method, detail: method ? `inserted with ${method} (not submitted)` : 'could not insert text', report };
  }

  /** Up to 4 plain class names (Tailwind variants like hover:… or w-[…] are left out to keep the log short). */
  function shortClass(el) {
    return String(el.className?.baseVal ?? el.className ?? '').split(/\s+/).filter((c) => c && !/[:\[\]()!]/.test(c)).slice(0, 4).join(' ');
  }

  /** Which selector of each list matches (no page text is reported). */
  function diagnose(sel) {
    const out = {};
    for (const [name, list] of Object.entries(sel)) {
      const f = find(list);
      out[name] = f ? f.selector : null;
    }
    out.userTurns = count(sel.userTurn);
    out.busy = isBusy(sel);
    // Attributes of the visible buttons around the prompt box (UI labels only, no chat text), to fix selectors.
    const box = find(sel.input)?.el;
    let container = null;
    for (const s of sel.inputContainer || []) {
      container = box?.closest(s);
      if (container) break;
    }
    out.buttons = container
      ? [...container.querySelectorAll('button')].filter(visible).map((b) =>
          [`class="${shortClass(b)}"`, b.type === 'submit' ? 'type="submit"' : '', `aria-label="${b.getAttribute('aria-label') ?? ''}"`,
            b.getAttribute('data-test-id') ? `data-test-id="${b.getAttribute('data-test-id')}"` : '',
            b.getAttribute('data-testid') ? `data-testid="${b.getAttribute('data-testid')}"` : '',
            b.disabled ? 'disabled' : ''].filter(Boolean).join(' '))
      : [];
    out.visibility = document.visibilityState;
    out.hasFocus = document.hasFocus();
    return out;
  }

  /** Content-script side of a provider: answers deliver / test / ping requests from the service worker. */
  function listen(sel, name) {
    const toWorker = (message) => chrome.runtime.sendMessage(message).catch(() => {});
    const onDiag = (text) => toWorker({ type: 'captionrelay-diag', level: 'info', text });
    // Final result of queued sends (submitted after the answer ended, or given up).
    const onResult = (ids, ack) => toWorker({ type: 'captionrelay-result', ids, status: ack.status, detail: ack.detail });

    chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
      if (message?.type === 'captionrelay-deliver') {
        deliverOrQueue(sel, message.text, message.id, { onDiag, onResult })
          .then(sendResponse, (e) => sendResponse({ status: 'error', detail: String(e?.message ?? e) }));
        return true; // async response
      }
      if (message?.type === 'captionrelay-test') {
        test(sel, message.text).then(sendResponse, (e) => sendResponse({ ok: false, detail: String(e?.message ?? e) }));
        return true;
      }
      if (message?.type === 'captionrelay-ping') {
        sendResponse({ ok: true });
        return false;
      }
      return false;
    });
    console.log(TAG, `${name} content script ready`);
  }

  globalThis.CaptionRelayWebChat = { deliver, deliverOrQueue, queueLength: () => queue.items.length, test, diagnose, isBusy, find, norm, listen };
})();
