// Shared service-worker side of every web-chat provider (Gemini, ChatGPT, ...): picks the target tab and asks its
// content script to deliver. A provider only supplies its id, name, site and content-script files.
//
// Background tabs (Phase 10): the target tab does not need focus or visibility — inserting with execCommand and
// clicking work in a hidden tab. Chrome may still discard (Memory Saver) or freeze a background tab, so the target
// tab is marked not auto-discardable, and a tab that is discarded or does not answer within a moment is reloaded
// (the conversation is stored by the site) and the text is delivered once it has loaded ("queued" + result).
import { log, warn } from '../lib/log.js';

const PING_TIMEOUT_MS = 1000; // a live content script answers a ping in a few ms; a frozen tab never does
const DELIVER_TIMEOUT_MS = 10000; // delivery itself may take ~3 s (button wait + verification)

class NoResponse extends Error {}

function withTimeout(promise, ms) {
  return Promise.race([promise, new Promise((_, reject) => setTimeout(() => reject(new NoResponse('no response')), ms))]);
}

function waitForLoad(tabId, timeoutMs) {
  return new Promise((resolve) => {
    const timer = setTimeout(done, timeoutMs, false);
    function listener(id, info) {
      if (id === tabId && info.status === 'complete') done(true);
    }
    function done(ok) {
      clearTimeout(timer);
      chrome.tabs.onUpdated.removeListener(listener);
      resolve(ok);
    }
    chrome.tabs.onUpdated.addListener(listener);
  });
}

/** Keep Chrome's Memory Saver from unloading the tab we deliver to. */
async function protect(tab) {
  if (tab.autoDiscardable === false) return;
  try {
    await chrome.tabs.update(tab.id, { autoDiscardable: false });
    log(`tab ${tab.id} marked not auto-discardable`);
  } catch (e) {
    warn(`could not protect tab ${tab.id}: ${e}`);
  }
}

/**
 * After a paste: makes the tab the active one in its window and brings the window forward (restored if minimized),
 * so the input that holds the pasted text has the keyboard focus. Returns the window's bounds (screen DIPs) for the
 * app, which brings it to the front itself if Windows did not let Chrome do it; true if the bounds are unknown.
 */
async function focusTab(tab) {
  try {
    await chrome.tabs.update(tab.id, { active: true });
    const before = await chrome.windows.get(tab.windowId);
    await chrome.windows.update(tab.windowId, before.state === 'minimized' ? { state: 'normal', focused: true } : { focused: true });
    const win = await chrome.windows.get(tab.windowId);
    return { left: win.left, top: win.top, width: win.width, height: win.height };
  } catch (e) {
    warn(`could not focus tab ${tab.id}: ${e?.message ?? e}`);
    return true;
  }
}

/** One line about the target tab for the app log. */
async function describe(tab) {
  let windowState = '?';
  try {
    windowState = (await chrome.windows.get(tab.windowId)).state;
  } catch {
    // ignore
  }
  return `target tab #${tab.id}: active=${tab.active} window=${windowState} discarded=${tab.discarded} frozen=${tab.frozen ?? '?'} status=${tab.status}`;
}

/**
 * @param {{id: string, name: string, origin: string, newChatUrl: string, contentFiles: string[]}} cfg
 *   origin e.g. 'https://chatgpt.com' (tabs are matched by `${origin}/*`).
 */
export function createWebProvider({ id, name, origin, newChatUrl, contentFiles }) {
  const urlPattern = `${origin}/*`;

  // While a tab holds queued text (the chat still answering there), new sends go to that same tab so they are
  // merged into the same message — even if the user meanwhile looked at another tab of the same chat.
  let queuedTabId = null;

  /** The tab with queued text, else the most recently active tab of this chat, or null. */
  async function findTab() {
    if (queuedTabId !== null) {
      try {
        const tab = await chrome.tabs.get(queuedTabId);
        if (tab?.url?.startsWith(`${origin}/`)) return tab;
      } catch {
        // closed
      }
      queuedTabId = null;
    }
    const tabs = await chrome.tabs.query({ url: urlPattern });
    if (tabs.length === 0) return null;
    tabs.sort((a, b) => (b.lastAccessed ?? 0) - (a.lastAccessed ?? 0));
    return tabs[0];
  }

  /** Sends to the tab's content script; injects it first if the tab was open before the extension (re)loaded. */
  async function sendToTab(tabId, message, timeoutMs = DELIVER_TIMEOUT_MS) {
    try {
      return await withTimeout(chrome.tabs.sendMessage(tabId, message), timeoutMs);
    } catch (e) {
      if (e instanceof NoResponse) throw e; // frozen / hung page: caller reloads
      log(`injecting the content script into tab ${tabId}`);
      await chrome.scripting.executeScript({ target: { tabId }, files: contentFiles });
      return await withTimeout(chrome.tabs.sendMessage(tabId, message), timeoutMs);
    }
  }

  /** True if the tab's content script answers a ping quickly (injecting it if needed). */
  async function isAlive(tabId) {
    try {
      const r = await sendToTab(tabId, { type: 'captionrelay-ping' }, PING_TIMEOUT_MS);
      return !!r?.ok;
    } catch (e) {
      if (e instanceof NoResponse) return false;
      throw e;
    }
  }

  /**
   * sent / queued (the chat is answering; the page submits it later and reports a result) / error.
   * submit false: only paste into the input (ack "sent" with submitted: false). merge: merge with what the input
   * holds instead of appending (paste only, and a held tap key).
   */
  async function deliverToTab(tabId, text, msgId, submit = true, merge = false) {
    const ack = await sendToTab(tabId, { type: 'captionrelay-deliver', text, id: msgId, submit, merge }).catch((e) => {
      if (e instanceof NoResponse) return { status: 'error', detail: `the ${name} tab stopped answering` };
      throw e;
    });
    if (!ack) return { status: 'error', detail: `no answer from the ${name} tab` };
    if (ack.status === 'queued') queuedTabId = tabId;
    return ack;
  }

  /**
   * For a tab that is (re)loading: waits for it, then delivers, and reports the final result through ctx.onResult
   * (the app already got "queued").
   */
  function deliverAfterLoad(tabId, text, ctx, what) {
    (async () => {
      const loaded = await waitForLoad(tabId, 20000);
      if (!loaded) {
        warn(`the ${what} ${name} tab did not finish loading`);
        ctx.onResult?.('error', `the ${what} ${name} tab did not load`);
        return;
      }
      for (let i = 0; i < 20; i++) {
        const ack = await deliverToTab(tabId, text, ctx.id, ctx.submit !== false, ctx.merge === true).catch((e) => ({ status: 'error', detail: String(e?.message ?? e) }));
        if (ack.status === 'sent') {
          log(`delivered to the ${what} ${name} tab`);
          ctx.onResult?.('sent', ack.detail, ack.submitted);
          return;
        }
        if (ack.status === 'queued') return; // the page reports the result itself
        if (ack.detail === 'not logged in') {
          warn(`${name}: not logged in`);
          ctx.onResult?.('error', 'not logged in');
          return;
        }
        await new Promise((r) => setTimeout(r, 500)); // the editor appears a little after "complete"
      }
      warn(`could not deliver to the ${what} ${name} tab`);
      ctx.onResult?.('error', `could not deliver to the ${what} ${name} tab`);
    })();
  }

  async function reloadAndDeliver(tab, text, ctx, reason) {
    warn(`tab ${tab.id} ${reason}; reloading it and delivering after load`);
    queuedTabId = null;
    await chrome.tabs.reload(tab.id);
    deliverAfterLoad(tab.id, text, ctx, 'reloaded');
    return { status: 'queued', detail: `reloading the ${name} tab (${reason})` };
  }

  return {
    id,
    name,
    kind: 'web',
    urlPatterns: [urlPattern],
    newChatUrl,

    /**
     * @param {string} text
     * @param {{id: string, submit?: boolean, merge?: boolean, focus?: boolean, onResult: (status: string, detail?: string, submitted?: boolean) => void}} ctx
     *   submit false = only paste into the input. onResult reports the final result when the ack was "queued" and
     *   delivery happens here (new or reloaded tab).
     */
    async deliver(text, ctx = {}) {
      const tab = await findTab();
      if (tab) {
        log(await describe(tab));
        await protect(tab);
        if (tab.discarded) return reloadAndDeliver(tab, text, ctx, 'was discarded by Chrome');
        const t0 = Date.now();
        if (!(await isAlive(tab.id))) return reloadAndDeliver(tab, text, ctx, `did not answer a ping within ${PING_TIMEOUT_MS} ms (frozen?)`);
        const pingMs = Date.now() - t0;
        if (pingMs > 200) log(`tab ${tab.id} answered the ping after ${pingMs} ms`);
        const ack = await deliverToTab(tab.id, text, ctx.id, ctx.submit !== false, ctx.merge === true);
        if (ctx.focus && ack?.status === 'sent' && ack.submitted === false) ack.focus = await focusTab(tab);
        return ack;
      }

      // No tab: open one in the background and deliver once it has loaded. That takes longer than the app waits
      // for an ack, so answer "queued" now and report the result when done.
      const created = await chrome.tabs.create({ url: newChatUrl, active: false });
      log(`no ${name} tab; opened tab ${created.id} in the background`);
      protect(created);
      deliverAfterLoad(created.id, text, ctx, 'new');
      return { status: 'queued', detail: `opened a new ${name} tab` };
    },

    /** A tab reported the result of its queue: it no longer holds queued text. */
    queueDone(tabId) {
      if (queuedTabId === tabId) queuedTabId = null;
    },

    /** Options "Test": insert without submitting, report selector matches. */
    async test(text) {
      const tab = await findTab();
      if (!tab) return { ok: false, detail: `no ${name} tab is open` };
      return sendToTab(tab.id, { type: 'captionrelay-test', text });
    },
  };
}
