// Gemini web-chat provider (service-worker side): picks the target tab and asks its content script to deliver.
//
// Background tabs (Phase 10): the target tab does not need focus or visibility — inserting with execCommand and
// clicking work in a hidden tab. Chrome may still discard (Memory Saver) or freeze a background tab, so the target
// tab is marked not auto-discardable, and a tab that is discarded or does not answer within a moment is reloaded
// (the conversation is stored by Google) and the text is delivered once it has loaded ("queued" + result).
import { log, warn } from '../../lib/log.js';

const URL_PATTERN = 'https://gemini.google.com/*';
const NEW_CHAT_URL = 'https://gemini.google.com/app';
const CONTENT_FILES = ['providers/gemini/selectors.js', 'providers/webchat.js', 'providers/gemini/content.js'];
const PING_TIMEOUT_MS = 1000; // a live content script answers a ping in a few ms; a frozen tab never does
const DELIVER_TIMEOUT_MS = 10000; // delivery itself may take ~3 s (button wait + verification)

// While a tab holds queued text (Gemini still answering there), new sends go to that same tab so they are merged
// into the same message — even if the user meanwhile looked at another Gemini tab.
let queuedTabId = null;

/** The tab with queued text, else the most recently active Gemini tab, or null. */
async function findTab() {
  if (queuedTabId !== null) {
    try {
      const tab = await chrome.tabs.get(queuedTabId);
      if (tab?.url?.startsWith('https://gemini.google.com/')) return tab;
    } catch {
      // closed
    }
    queuedTabId = null;
  }
  const tabs = await chrome.tabs.query({ url: URL_PATTERN });
  if (tabs.length === 0) return null;
  tabs.sort((a, b) => (b.lastAccessed ?? 0) - (a.lastAccessed ?? 0));
  return tabs[0];
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

class NoResponse extends Error {}

function withTimeout(promise, ms) {
  return Promise.race([promise, new Promise((_, reject) => setTimeout(() => reject(new NoResponse('no response')), ms))]);
}

/** Sends to the tab's content script; injects it first if the tab was open before the extension (re)loaded. */
async function sendToTab(tabId, message, timeoutMs = DELIVER_TIMEOUT_MS) {
  try {
    return await withTimeout(chrome.tabs.sendMessage(tabId, message), timeoutMs);
  } catch (e) {
    if (e instanceof NoResponse) throw e; // frozen / hung page: caller reloads
    log(`injecting the content script into tab ${tabId}`);
    await chrome.scripting.executeScript({ target: { tabId }, files: CONTENT_FILES });
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

/** sent / queued (Gemini is answering; the page submits it later and reports a result) / error. */
async function deliverToTab(tabId, text, id) {
  const ack = await sendToTab(tabId, { type: 'captionrelay-deliver', text, id }).catch((e) => {
    if (e instanceof NoResponse) return { status: 'error', detail: 'the Gemini tab stopped answering' };
    throw e;
  });
  if (!ack) return { status: 'error', detail: 'no answer from the Gemini tab' };
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
      warn(`the ${what} Gemini tab did not finish loading`);
      ctx.onResult?.('error', `the ${what} Gemini tab did not load`);
      return;
    }
    for (let i = 0; i < 20; i++) {
      const ack = await deliverToTab(tabId, text, ctx.id).catch((e) => ({ status: 'error', detail: String(e?.message ?? e) }));
      if (ack.status === 'sent') {
        log(`delivered to the ${what} Gemini tab`);
        ctx.onResult?.('sent', ack.detail);
        return;
      }
      if (ack.status === 'queued') return; // the page reports the result itself
      if (ack.detail === 'not logged in') {
        warn('Gemini: not logged in');
        ctx.onResult?.('error', 'not logged in');
        return;
      }
      await new Promise((r) => setTimeout(r, 500)); // the editor appears a little after "complete"
    }
    warn(`could not deliver to the ${what} Gemini tab`);
    ctx.onResult?.('error', `could not deliver to the ${what} Gemini tab`);
  })();
}

async function reloadAndDeliver(tab, text, ctx, reason) {
  warn(`tab ${tab.id} ${reason}; reloading it and delivering after load`);
  queuedTabId = null;
  await chrome.tabs.reload(tab.id);
  deliverAfterLoad(tab.id, text, ctx, 'reloaded');
  return { status: 'queued', detail: `reloading the Gemini tab (${reason})` };
}

export const gemini = {
  id: 'gemini',
  name: 'Gemini',
  kind: 'web',
  urlPatterns: [URL_PATTERN],
  newChatUrl: NEW_CHAT_URL,

  /**
   * @param {string} text
   * @param {{id: string, onResult: (status: string, detail?: string) => void}} ctx
   *   onResult reports the final result when the ack was "queued" and delivery happens here (new or reloaded tab).
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
      return deliverToTab(tab.id, text, ctx.id);
    }

    // No Gemini tab: open one in the background and deliver once it has loaded. That takes longer than the
    // app waits for an ack, so answer "queued" now and report the result when done.
    const created = await chrome.tabs.create({ url: NEW_CHAT_URL, active: false });
    log(`no Gemini tab; opened tab ${created.id} in the background`);
    protect(created);
    deliverAfterLoad(created.id, text, ctx, 'new');
    return { status: 'queued', detail: 'opened a new Gemini tab' };
  },

  /** A tab reported the result of its queue: it no longer holds queued text. */
  queueDone(tabId) {
    if (queuedTabId === tabId) queuedTabId = null;
  },

  /** Options "Test": insert without submitting, report selector matches. */
  async test(text) {
    const tab = await findTab();
    if (!tab) return { ok: false, detail: 'no Gemini tab is open' };
    return sendToTab(tab.id, { type: 'captionrelay-test', text });
  },
};
