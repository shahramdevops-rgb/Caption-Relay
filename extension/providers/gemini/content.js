// Content script on gemini.google.com: receives deliver/test requests from the service worker.
(() => {
  if (globalThis.__captionRelayGeminiContent) return; // injected twice (manifest + scripting fallback)
  globalThis.__captionRelayGeminiContent = true;

  const sel = globalThis.CaptionRelaySelectors.gemini;
  const chat = globalThis.CaptionRelayWebChat;

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type === 'captionrelay-deliver') {
      const onDiag = (text) => chrome.runtime.sendMessage({ type: 'captionrelay-diag', level: 'info', text }).catch(() => {});
      chat.deliver(sel, message.text, { onDiag }).then(sendResponse, (e) => sendResponse({ status: 'error', detail: String(e?.message ?? e) }));
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
