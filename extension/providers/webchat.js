// Shared delivery logic for web-chat providers (Gemini now; ChatGPT/Claude later), run as a content script.
// Each provider only supplies a selector set (see providers/<id>/selectors.js).
(() => {
  if (globalThis.CaptionRelayWebChat) return;

  const TAG = '[CaptionRelay]';
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
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
      await sleep(30);
      if (ok()) return 'execCommand';
    } catch (e) {
      console.warn(TAG, 'execCommand failed', e);
    }

    // 2) synthetic paste with a DataTransfer.
    try {
      selectAll(box);
      const dt = new DataTransfer();
      dt.setData('text/plain', full);
      box.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
      await sleep(50);
      if (ok()) return 'paste';
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
      await sleep(50);
      if (ok()) return 'innerText';
    } catch (e) {
      console.warn(TAG, 'direct set failed', e);
    }
    return null;
  }

  // Only buttons that look like "send": the container also has microphone / upload / tools buttons.
  const SEND_LIKE = /send|submit|ارسال|فرستادن/i;
  function looksLikeSend(btn) {
    // (btn.type is not used: every <button> defaults to type "submit".)
    const text = [btn.className, btn.getAttribute('aria-label'), btn.getAttribute('data-test-id'), btn.getAttribute('mattooltip')]
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
    const end = Date.now() + waitMs;
    while (Date.now() < end) {
      const found = find(sel.sendButton);
      if (found && isEnabled(found.el)) {
        found.el.click();
        return 'button ' + found.selector;
      }
      await sleep(50);
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
    const end = Date.now() + waitMs;
    while (Date.now() < end) {
      if (count(sel.userTurn) > turnsBefore) return 'new user turn';
      if (boxText(box) === '') return 'input emptied';
      if (isBusy(sel)) return 'generation started';
      await sleep(50);
    }
    return null;
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
    const found = findBox(sel);
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
          [`class="${b.className}"`, `aria-label="${b.getAttribute('aria-label') ?? ''}"`,
            b.getAttribute('data-test-id') ? `data-test-id="${b.getAttribute('data-test-id')}"` : '',
            b.disabled ? 'disabled' : ''].filter(Boolean).join(' '))
      : [];
    out.visibility = document.visibilityState;
    out.hasFocus = document.hasFocus();
    return out;
  }

  globalThis.CaptionRelayWebChat = { deliver, test, diagnose, isBusy, find, norm };
})();
