// Gemini web-chat provider (service-worker side): picks the target tab and asks its content script to deliver.
import { log, warn } from '../../lib/log.js';

const URL_PATTERN = 'https://gemini.google.com/*';
const NEW_CHAT_URL = 'https://gemini.google.com/app';
const CONTENT_FILES = ['providers/gemini/selectors.js', 'providers/webchat.js', 'providers/gemini/content.js'];

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

/** Sends to the tab's content script; injects it first if the tab was open before the extension (re)loaded. */
async function sendToTab(tabId, message) {
  try {
    return await chrome.tabs.sendMessage(tabId, message);
  } catch {
    log(`injecting the content script into tab ${tabId}`);
    await chrome.scripting.executeScript({ target: { tabId }, files: CONTENT_FILES });
    return await chrome.tabs.sendMessage(tabId, message);
  }
}

/** sent / queued (Gemini is answering; the page submits it later and reports a result) / error. */
async function deliverToTab(tabId, text, id) {
  const ack = await sendToTab(tabId, { type: 'captionrelay-deliver', text, id });
  if (!ack) return { status: 'error', detail: 'no answer from the Gemini tab' };
  if (ack.status === 'queued') queuedTabId = tabId;
  return ack;
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
   *   onResult reports the final result when the ack was "queued" and delivery happens here (new tab).
   */
  async deliver(text, ctx = {}) {
    const tab = await findTab();
    if (tab) return deliverToTab(tab.id, text, ctx.id);

    // No Gemini tab: open one in the background and deliver once it has loaded. That takes longer than the
    // app waits for an ack, so answer "queued" now and report the result when done.
    const created = await chrome.tabs.create({ url: NEW_CHAT_URL, active: false });
    log(`no Gemini tab; opened tab ${created.id} in the background`);
    (async () => {
      const loaded = await waitForLoad(created.id, 20000);
      if (!loaded) {
        warn('the new Gemini tab did not finish loading');
        ctx.onResult?.('error', 'the new Gemini tab did not load');
        return;
      }
      for (let i = 0; i < 20; i++) {
        const ack = await deliverToTab(created.id, text, ctx.id).catch((e) => ({ status: 'error', detail: String(e) }));
        if (ack.status === 'sent') {
          log('delivered to the new Gemini tab');
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
      warn('could not deliver to the new Gemini tab');
      ctx.onResult?.('error', 'could not deliver to the new Gemini tab');
    })();
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
