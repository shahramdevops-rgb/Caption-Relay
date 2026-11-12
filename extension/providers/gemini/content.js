// Content script on gemini.google.com: receives deliver/test requests from the service worker.
(() => {
  // Injected twice (manifest + scripting fallback): keep the first copy only while its extension context is alive.
  // After an extension reload the old copy is orphaned ("Extension context invalidated") and must be replaced.
  if (globalThis.__captionRelayGeminiAlive?.()) return;
  globalThis.__captionRelayGeminiAlive = () => {
    try {
      return !!chrome.runtime?.getManifest();
    } catch {
      return false;
    }
  };

  const sel = globalThis.CaptionRelaySelectors.gemini;
  const chat = globalThis.CaptionRelayWebChat;
  const toWorker = (message) => chrome.runtime.sendMessage(message).catch(() => {});

  const onDiag = (text) => toWorker({ type: 'captionrelay-diag', level: 'info', text });
  // Final result of queued sends (submitted after the answer ended, or given up).
  const onResult = (ids, ack) => toWorker({ type: 'captionrelay-result', ids, status: ack.status, detail: ack.detail });

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type === 'captionrelay-deliver') {
      chat
        .deliverOrQueue(sel, message.text, message.id, { onDiag, onResult })
        .then(sendResponse, (e) => sendResponse({ status: 'error', detail: String(e?.message ?? e) }));
      return true; // async response
    }
    if (message?.type === 'captionrelay-test') {
      chat.test(sel, message.text).then(sendResponse, (e) => sendResponse({ ok: false, detail: String(e?.message ?? e) }));
      return true;
    }
    if (message?.type === 'captionrelay-ping') {
      sendResponse({ ok: true });
      return false;
    }
    return false;
  });

  console.log('[CaptionRelay] Gemini content script ready');
})();
